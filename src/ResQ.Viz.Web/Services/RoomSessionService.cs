/**
 * Copyright 2024 ResQ Technologies Ltd.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Wire shape persisted inside the <c>viz_session</c> cookie. Encrypted with
/// <see cref="IDataProtector"/> (AES-256-CBC + HMAC-SHA256, automatic key
/// rotation) so the client cannot read or forge it. Bound to an IP-prefix
/// bucket so a stolen cookie replayed from a different network is rejected.
/// </summary>
public sealed record RoomSession(string RoomId, string IpBucket, long ExpiresUnix);

/// <summary>
/// Issues, validates, and refreshes <see cref="RoomSession"/> cookies, and
/// owns the mapping between authenticated requests and <see cref="SimulationRoom"/>
/// instances. The cookie is the only place the room id lives — never in URLs
/// or response bodies — so it cannot leak via Referer headers, browser history,
/// or screen captures.
/// </summary>
public sealed class RoomSessionService
{
    /// <summary>Cookie name carrying the protected <see cref="RoomSession"/> blob.</summary>
    public const string CookieName = "viz_session";

    /// <summary>Data-protection purpose string. Changing this rotates all live cookies.</summary>
    public const string ProtectorPurpose = "ResQ.Viz.Web.RoomSession.v1";

    /// <summary>Lifetime of an issued session before re-issue is required.</summary>
    public static readonly TimeSpan SessionTtl = TimeSpan.FromHours(24);

    private readonly IDataProtector _protector;
    private readonly SimulationManager _manager;
    private readonly ILogger<RoomSessionService> _logger;

    /// <summary>Initialises the service.</summary>
    public RoomSessionService(
        IDataProtectionProvider provider,
        SimulationManager manager,
        ILogger<RoomSessionService> logger)
    {
        _protector = provider.CreateProtector(ProtectorPurpose);
        _manager = manager;
        _logger = logger;
    }

    /// <summary>
    /// Computes the IP-prefix bucket used to bind a session: <c>/24</c> for IPv4,
    /// <c>/64</c> for IPv6. Soft binding — survives carrier-NAT IP rotations
    /// inside the same prefix while rejecting cookie replay from a different
    /// network. <c>null</c> input (in-process tests) maps to a stable sentinel.
    /// </summary>
    public static string IpBucket(IPAddress? ip)
    {
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24";
        if (bytes.Length == 16)
        {
            var sb = new StringBuilder(20);
            for (var i = 0; i < 8; i += 2)
            {
                if (i > 0) sb.Append(':');
                sb.Append($"{bytes[i]:x2}{bytes[i + 1]:x2}");
            }
            sb.Append("::/64");
            return sb.ToString();
        }
        return "unknown";
    }

    /// <summary>Cryptographically random 256-bit room id, hex-encoded lowercase.</summary>
    public static string NewRoomId()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Issue a brand-new session bound to the supplied IP. Creates the underlying
    /// <see cref="SimulationRoom"/>. Returns a result with null fields when the
    /// room cap is reached.
    /// </summary>
    public IssueResult Issue(IPAddress? ip)
    {
        var bucket = IpBucket(ip);
        var roomId = NewRoomId();
        var room = _manager.CreateOrGet(roomId, bucket);
        if (room is null) return new IssueResult(null, null, "capacity");

        var session = new RoomSession(
            RoomId: roomId,
            IpBucket: bucket,
            ExpiresUnix: DateTimeOffset.UtcNow.Add(SessionTtl).ToUnixTimeSeconds());
        var json = JsonSerializer.Serialize(session);
        var protectedValue = _protector.Protect(json);
        return new IssueResult(protectedValue, room, null);
    }

    /// <summary>
    /// Validate an existing cookie value against the live request IP. The
    /// cookie is rejected (and the caller treated as unauthenticated) if any
    /// of these are true: cookie missing, signature/encryption invalid, expired,
    /// IP-bucket mismatch, or the underlying room has been reaped.
    /// </summary>
    public bool TryValidate(
        string? cookieValue,
        IPAddress? currentIp,
        out RoomSession? session,
        out SimulationRoom? room)
    {
        session = null;
        room = null;
        if (string.IsNullOrEmpty(cookieValue)) return false;

        string json;
        try
        {
            json = _protector.Unprotect(cookieValue);
        }
        catch (CryptographicException)
        {
            // Tampered, expired key, or rotated key ring — any of these is
            // indistinguishable from a forgery. Reject silently.
            return false;
        }

        RoomSession? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RoomSession>(json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (parsed is null) return false;

        if (parsed.ExpiresUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false;

        var currentBucket = IpBucket(currentIp);
        if (!string.Equals(parsed.IpBucket, currentBucket, StringComparison.Ordinal))
        {
            // Hard fail on IP-bucket change. A user changing networks (home →
            // coffee shop) gets a fresh sim. The alternative — allowing IP
            // changes — turns the cookie into a bearer token anyone on any
            // network can replay.
            _logger.LogWarning(
                "Session IP-bucket mismatch (cookie={CookieBucket} current={CurrentBucket}); rejecting.",
                parsed.IpBucket, currentBucket);
            return false;
        }

        if (!_manager.TryGet(parsed.RoomId, out var liveRoom) || liveRoom is null)
        {
            // Room reaped while the cookie was still valid (e.g. tab idle for hours).
            return false;
        }

        liveRoom.Touch();
        session = parsed;
        room = liveRoom;
        return true;
    }

    /// <summary>
    /// Idempotent session bootstrap. Returns the existing valid session if the
    /// caller already has one; otherwise issues a new session + new room.
    /// </summary>
    public IssueResult IssueOrRefresh(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress;
        var cookie = httpContext.Request.Cookies[CookieName];
        if (TryValidate(cookie, ip, out _, out var existingRoom) && existingRoom is not null)
            return new IssueResult(cookie, existingRoom, null);
        return Issue(ip);
    }

    /// <summary>Result of <see cref="Issue"/> / <see cref="IssueOrRefresh"/>.</summary>
    public sealed record IssueResult(string? CookieValue, SimulationRoom? Room, string? FailureReason);
}
