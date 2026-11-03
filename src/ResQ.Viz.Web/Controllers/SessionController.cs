/**
 * Copyright 2026 ResQ Systems, Inc.
 * SPDX-License-Identifier: Apache-2.0
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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

/// <summary>
/// Session bootstrap endpoints. The client calls <c>POST /api/sim/session</c>
/// once on boot to obtain (or refresh) the <c>viz_session</c> cookie that
/// authenticates every subsequent <c>/api/sim/*</c> hit and the SignalR
/// handshake. The room id is never returned in URL form — only inside the
/// HttpOnly cookie and (for HUD display) the JSON body of <c>GET /info</c>.
/// </summary>
[ApiController]
[Route("api/sim/session")]
public sealed class SessionController : ControllerBase
{
    private readonly RoomSessionService _sessions;
    private readonly IServiceProvider _services;
    private readonly ILogger<SessionController> _logger;

    /// <summary>Initialises the controller.</summary>
    public SessionController(RoomSessionService sessions, IServiceProvider services, ILogger<SessionController> logger)
    {
        _sessions = sessions;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Bootstrap or refresh the caller's session. Idempotent: if the caller
    /// already has a valid <c>viz_session</c> cookie that matches their IP
    /// bucket, the existing room is returned and the cookie left in place.
    /// Otherwise a fresh 256-bit room id is allocated and a new cookie set.
    /// Rate-limited via the <c>destructive</c> bucket so an attacker can't
    /// spawn 1000 rooms from one IP.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("destructive")]
    public async Task<IActionResult> Create()
    {
        var result = _sessions.IssueOrRefresh(HttpContext);
        if (result.CookieValue is null || result.Room is null)
        {
            _logger.LogWarning("Session creation failed: {Reason}", result.FailureReason ?? "unknown");
            return StatusCode(503, new { error = "unavailable", reason = result.FailureReason ?? "unknown" });
        }

        Response.Cookies.Append(RoomSessionService.CookieName, result.CookieValue, BuildCookieOptions());

        // Wire the Furia Viz Bridge relay when configured. The relay connects to the bridge and
        // relays frames into the room through the shared IFrameBroadcaster; SimulationManager
        // detects IsBridgeMode and skips the local tick loop.  Resolve the relay lazily so a
        // deployment without BridgeRelay:BaseUrl still starts without the bridge on its service
        // graph.  Pass the room so the relay can mark the room as bridge-driven after connect.
        var bridgeRelay = _services.GetService<BridgeRelayService>();
        if (bridgeRelay is not null)
        {
            try
            {
                _ = await bridgeRelay.CreateSessionAsync(result.Room.Id, result.Room);
                _logger.LogInformation("Bridge relay session created for room {RoomId}.", result.Room.Id);
            }
            catch (Exception ex)
            {
                // Bridge unreachable — fall back to local simulation.  The room is fully usable;
                // an operator simply won't see the Furia Viz Bridge picture until the relay
                // can reconnect (or the host is restarted with a working bridge URL).
                _logger.LogWarning(ex, "Bridge relay failed for room {RoomId}; falling back to local simulation.", result.Room.Id);
            }
        }

        return Ok(new { roomId = result.Room.Id, expiresIn = (int)RoomSessionService.SessionTtl.TotalSeconds });
    }

    /// <summary>
    /// Returns metadata about the caller's current session (room id, age).
    /// Authenticated — used by the HUD to display the room id without
    /// exposing it via URL or query string.
    /// </summary>
    [HttpGet("info")]
    [RequireRoom]
    public IActionResult Info()
    {
        var room = HttpContext.Room();
        return Ok(new
        {
            roomId = room.Id,
            createdAt = room.CreatedAtUtc,
            connectionCount = room.ConnectionCount,
        });
    }

    /// <summary>
    /// Destroys the caller's session cookie. Does not reap the room — that's
    /// the manager's job once connection count hits zero and the idle window
    /// elapses. Requires a valid session: callers without one have nothing to
    /// clear and get the same 401 redirect every other authenticated endpoint
    /// returns.
    /// </summary>
    [HttpDelete]
    [RequireRoom]
    [EnableRateLimiting("destructive")]
    public IActionResult Delete()
    {
        var room = HttpContext.Room();

        // Tear down the bridge relay for this room so the SSE stream closes and the relay
        // removes the session from its dictionary.  If the room was using local simulation
        // (no bridge relay or relay not configured) this is a harmless no-op.
        var bridgeRelay = _services.GetService<BridgeRelayService>();
        if (bridgeRelay is not null)
        {
            _ = bridgeRelay.RemoveSessionAsync(room.Id);
        }

        Response.Cookies.Delete(RoomSessionService.CookieName);
        return Ok(new { cleared = true });
    }

    private CookieOptions BuildCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        MaxAge = RoomSessionService.SessionTtl,
        IsEssential = true,
    };
}
