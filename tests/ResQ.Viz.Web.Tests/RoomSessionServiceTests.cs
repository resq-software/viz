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
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Tests for <see cref="RoomSessionService"/> — the strict-mode invariants
/// every cookie must satisfy: signed payload, unexpired, IP-bucket match,
/// and live-room lookup. Each test exercises one rejection axis to make
/// regressions land on a single failure rather than a smeared one.
/// </summary>
public class RoomSessionServiceTests
{
    private static (RoomSessionService sessions, SimulationManager manager) Build()
    {
        var hubMock = new Mock<IHubContext<VizHub>>();
        var clientsMock = new Mock<IHubClients>();
        var proxyMock = new Mock<IClientProxy>();
        clientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(proxyMock.Object);
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        var manager = new SimulationManager(hubMock.Object, new VizFrameBuilder(), NullLoggerFactory.Instance);
        var sessions = new RoomSessionService(
            new EphemeralDataProtectionProvider(),
            manager,
            NullLogger<RoomSessionService>.Instance);
        return (sessions, manager);
    }

    // ─── IpBucket ───────────────────────────────────────────────────────────

    [Fact]
    public void IpBucket_IPv4_Returns_Slash24_Prefix()
    {
        RoomSessionService.IpBucket(IPAddress.Parse("203.0.113.42"))
            .Should().Be("203.0.113.0/24");
    }

    [Fact]
    public void IpBucket_IPv6_Returns_Slash64_Prefix()
    {
        var bucket = RoomSessionService.IpBucket(IPAddress.Parse("2001:db8:abcd:1234:5678::1"));
        bucket.Should().EndWith("::/64");
        bucket.Should().StartWith("2001:0db8:abcd:1234");
    }

    [Fact]
    public void IpBucket_Null_Maps_To_Sentinel()
    {
        RoomSessionService.IpBucket(null).Should().Be("unknown");
    }

    [Fact]
    public void IpBucket_IPv4_Mapped_IPv6_Folds_To_IPv4()
    {
        // ::ffff:203.0.113.42 — the IPv4-mapped form. Should bucket as IPv4 /24
        // so a client connecting via IPv6 dual-stack still gets the same
        // bucket as if it had connected over plain IPv4.
        RoomSessionService.IpBucket(IPAddress.Parse("::ffff:203.0.113.42"))
            .Should().Be("203.0.113.0/24");
    }

    // ─── NewRoomId ──────────────────────────────────────────────────────────

    [Fact]
    public void NewRoomId_Returns_64_Hex_Chars()
    {
        var id = RoomSessionService.NewRoomId();
        id.Should().HaveLength(64);
        id.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void NewRoomId_Is_Unique_Across_Calls()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => RoomSessionService.NewRoomId()).ToHashSet();
        ids.Should().HaveCount(100, "256-bit random ids should never collide");
    }

    // ─── Issue ──────────────────────────────────────────────────────────────

    [Fact]
    public void Issue_Returns_Cookie_And_Live_Room()
    {
        var (sessions, manager) = Build();

        var result = sessions.Issue(IPAddress.Parse("10.0.0.1"));

        result.CookieValue.Should().NotBeNullOrEmpty();
        result.Room.Should().NotBeNull();
        result.FailureReason.Should().BeNull();
        manager.RoomCount.Should().Be(1);
    }

    [Fact]
    public void Issue_BindsRoom_To_IpBucket()
    {
        var (sessions, _) = Build();

        var result = sessions.Issue(IPAddress.Parse("10.0.0.1"));

        result.Room!.IpBucket.Should().Be("10.0.0.0/24");
    }

    // ─── TryValidate ────────────────────────────────────────────────────────

    [Fact]
    public void TryValidate_Roundtrips_Issued_Cookie()
    {
        var (sessions, _) = Build();
        var ip = IPAddress.Parse("10.0.0.1");
        var issued = sessions.Issue(ip);

        var ok = sessions.TryValidate(issued.CookieValue, ip, out var session, out var room);

        ok.Should().BeTrue();
        room.Should().BeSameAs(issued.Room);
        session!.RoomId.Should().Be(issued.Room!.Id);
    }

    [Fact]
    public void TryValidate_Empty_Cookie_Fails()
    {
        var (sessions, _) = Build();

        sessions.TryValidate(null, IPAddress.Parse("10.0.0.1"), out _, out _).Should().BeFalse();
        sessions.TryValidate("", IPAddress.Parse("10.0.0.1"), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryValidate_Forged_Cookie_Fails()
    {
        var (sessions, _) = Build();

        var ok = sessions.TryValidate(
            "this-is-not-a-real-protected-cookie",
            IPAddress.Parse("10.0.0.1"),
            out _, out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryValidate_IpBucket_Mismatch_Fails()
    {
        var (sessions, _) = Build();
        var issued = sessions.Issue(IPAddress.Parse("10.0.0.10"));

        // Same /24 — should still pass.
        sessions.TryValidate(issued.CookieValue, IPAddress.Parse("10.0.0.99"), out _, out _)
            .Should().BeTrue("addresses inside the same /24 bucket must match");

        // Different /24 — strict mode rejects.
        sessions.TryValidate(issued.CookieValue, IPAddress.Parse("10.0.99.10"), out _, out _)
            .Should().BeFalse("addresses outside the bound /24 bucket must be rejected");

        // Completely different network.
        sessions.TryValidate(issued.CookieValue, IPAddress.Parse("203.0.113.5"), out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryValidate_Reaped_Room_Fails()
    {
        var (sessions, manager) = Build();
        var ip = IPAddress.Parse("10.0.0.1");
        var issued = sessions.Issue(ip);

        // Simulate the reaper dropping the room while the cookie is still
        // cryptographically valid (e.g. tab idle for hours).
        manager.Remove(issued.Room!.Id);

        var ok = sessions.TryValidate(issued.CookieValue, ip, out _, out var room);

        ok.Should().BeFalse();
        room.Should().BeNull();
    }

    [Fact]
    public void TryValidate_Tampered_Cookie_Fails()
    {
        var (sessions, _) = Build();
        var ip = IPAddress.Parse("10.0.0.1");
        var issued = sessions.Issue(ip);

        // Flip a character in the middle of the protected blob.
        var cookie = issued.CookieValue!;
        var tampered = cookie[..(cookie.Length / 2)]
            + (cookie[cookie.Length / 2] == 'A' ? 'B' : 'A')
            + cookie[(cookie.Length / 2 + 1)..];

        sessions.TryValidate(tampered, ip, out _, out _).Should().BeFalse();
    }
}
