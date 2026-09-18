/**
 * Copyright 2026 ResQ Systems, Inc.
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

using System.Numerics;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The corridor geometry every domain shares: which peer is in the way, and how much room is
/// left before the two footprints meet.
/// </summary>
/// <remarks>
/// A corridor and not a radius, which is the whole design. A disc around the vehicle reports
/// every peer that is merely close — the one just safely passed, the one abeam on a parallel
/// track — and braking for those is how a fleet deadlocks in an open field. These cases pin the
/// distinction: directly ahead blocks, directly behind does not, and abeam depends on whether
/// the two hulls have to pass through the same width.
/// <para>
/// Scene frame throughout: <c>+X</c> is east and <c>-Z</c> is north, which is what every probe
/// in the asset code already lays off with.
/// </para>
/// </remarks>
public sealed class PeerSeparationTests
{
    private const string Self = "rover-1";
    private const string Other = "rover-2";
    private const double North = 0.0;
    private const double East = Math.PI / 2.0;

    /// <summary>Both vehicles two metres across, so a gap is the range less four.</summary>
    private const double Footprint = 2.0;

    private static Vector3 NorthOf(double metres) => new(0f, 0f, (float)-metres);

    private static PeerPose Peer(
        Vector3 positionEus, string id = Other, AssetDomain domain = AssetDomain.Ground) =>
        new(id, domain, positionEus, Footprint);

    private static PeerContact Look(
        IReadOnlyList<PeerPose> peers, double headingRad = North, double reachM = 50.0) =>
        PeerSeparation.NearestAhead(
            peers, Self, AssetDomain.Ground, Vector3.Zero, Footprint, headingRad, reachM);

    // ─── What counts as in the way ──────────────────────────────────────────

    /// <summary>A peer dead ahead is found, and the gap has both footprints taken out.</summary>
    /// <remarks>
    /// Surface to surface, not centre to centre: at 20 m between centres with a 2 m radius each,
    /// the clear ground is 16 m. A consumer comparing that against a standoff is then comparing
    /// two quantities of the same kind, which is the only reason the subtraction lives here
    /// rather than in each caller.
    /// </remarks>
    [Fact]
    public void A_Peer_Dead_Ahead_Is_Found_With_Both_Footprints_Removed()
    {
        var contact = Look([Peer(NorthOf(20.0))]);

        contact.Exists.Should().BeTrue();
        contact.AssetId.Should().Be(Other);
        contact.GapM.Should().BeApproximately(20.0 - (2.0 * Footprint), 1e-9);
    }

    /// <summary>A peer astern is not in the way.</summary>
    [Fact]
    public void A_Peer_Astern_Is_Ignored()
    {
        Look([Peer(NorthOf(-20.0))]).Exists.Should().BeFalse();
    }

    /// <summary>A peer beyond the reach is not in the way yet.</summary>
    /// <remarks>
    /// The reach is the caller's stopping distance, so this is the statement that a vehicle is
    /// not slowed for something it could still stop short of without slowing at all.
    /// </remarks>
    [Fact]
    public void A_Peer_Beyond_The_Reach_Is_Ignored()
    {
        Look([Peer(NorthOf(60.0))], reachM: 50.0).Exists.Should().BeFalse();
    }

    /// <summary>A peer abeam, clear of the corridor, is passed rather than braked for.</summary>
    /// <remarks>
    /// Five metres off a track needing four is clear by a metre. This is the case a radius test
    /// gets wrong, and it is not an edge case — it is every vehicle working alongside another.
    /// </remarks>
    [Fact]
    public void A_Peer_Clear_Of_The_Corridor_Is_Passed()
    {
        var abeam = new Vector3(5.0f, 0f, -20f);

        Look([Peer(abeam)]).Exists.Should().BeFalse();
    }

    /// <summary>A peer offset but still inside the swept width does block.</summary>
    /// <remarks>
    /// The complement of the case above, and the reason the width is the sum of the two radii
    /// rather than one of them: both hulls have to fit through the same gap.
    /// </remarks>
    [Fact]
    public void A_Peer_Inside_The_Swept_Width_Blocks()
    {
        var offset = new Vector3(3.0f, 0f, -20f);

        Look([Peer(offset)]).Exists.Should().BeTrue();
    }

    /// <summary>The corridor follows the direction of travel, not a fixed axis.</summary>
    [Fact]
    public void The_Corridor_Turns_With_The_Heading()
    {
        var toTheEast = new Vector3(20f, 0f, 0f);

        Look([Peer(toTheEast)], headingRad: North).Exists.Should().BeFalse();
        Look([Peer(toTheEast)], headingRad: East).Exists.Should().BeTrue();
    }

    // ─── Who counts ─────────────────────────────────────────────────────────

    /// <summary>A vehicle never finds itself, even from a peer list that places it wrongly.</summary>
    /// <remarks>
    /// The caller's own pose is in the list by contract, and at its own position it is excluded
    /// by the astern test alone — which makes the identity check look like a guard that cannot
    /// fail. It can. A list built from stale poses, or one that duplicates an entry, puts the
    /// caller's own id at a position ahead of it, and a vehicle that brakes for itself stops
    /// forever and never explains why. The entry here is deliberately placed ahead so that the
    /// identity check is the only thing excluding it.
    /// </remarks>
    [Fact]
    public void A_Vehicle_Does_Not_Find_Itself()
    {
        Look([Peer(Vector3.Zero, id: Self)]).Exists.Should().BeFalse();
        Look([Peer(NorthOf(10.0), id: Self)]).Exists.Should().BeFalse();
    }

    /// <summary>Another domain's assets are not considered.</summary>
    /// <remarks>
    /// Cross-domain blocking is a documented scope deferral on <see cref="PeerPose"/> — a rover
    /// blocked by a landed drone needs a shared obstacle representation that does not exist. This
    /// pins the deferral rather than leaving it to be rediscovered as a bug.
    /// </remarks>
    [Fact]
    public void Another_Domain_Is_Not_Considered()
    {
        Look([Peer(NorthOf(10.0), domain: AssetDomain.Air)]).Exists.Should().BeFalse();
        Look([Peer(NorthOf(10.0), domain: AssetDomain.Surface)]).Exists.Should().BeFalse();
    }

    /// <summary>With several peers ahead, the nearest is the one returned.</summary>
    [Fact]
    public void The_Nearest_Peer_Ahead_Wins()
    {
        var contact = Look([
            Peer(NorthOf(40.0), id: "far"),
            Peer(NorthOf(12.0), id: "near"),
            Peer(NorthOf(25.0), id: "middle"),
        ]);

        contact.AssetId.Should().Be("near");
        contact.GapM.Should().BeApproximately(12.0 - (2.0 * Footprint), 1e-9);
    }

    // ─── States the simulation can actually reach ───────────────────────────

    /// <summary>Overlapping footprints report a negative gap rather than a clamped zero.</summary>
    /// <remarks>
    /// Reachable: an asset spawned on top of another, or two that closed faster than one step
    /// could arrest. A consumer has to be able to tell "touching" from "already inside", because
    /// the second calls for reversing out and the first does not.
    /// </remarks>
    [Fact]
    public void Overlapping_Footprints_Report_A_Negative_Gap()
    {
        Look([Peer(NorthOf(1.0))]).GapM.Should().BeApproximately(1.0 - (2.0 * Footprint), 1e-9);
    }

    /// <summary>An empty world, a zero reach and a nonsense reach all report nothing.</summary>
    [Fact]
    public void Degenerate_Inputs_Report_Nothing()
    {
        Look([]).Exists.Should().BeFalse();
        Look([Peer(NorthOf(5.0))], reachM: 0.0).Exists.Should().BeFalse();
        Look([Peer(NorthOf(5.0))], reachM: double.NaN).Exists.Should().BeFalse();
        Look([Peer(NorthOf(5.0))], reachM: double.PositiveInfinity).Exists.Should().BeFalse();
    }

    /// <summary>No peer at all is infinite room, not a large number.</summary>
    /// <remarks>
    /// The consumers branch on infinity to mean "unconstrained", so a sentinel like
    /// <c>double.MaxValue</c> would silently impose a ceiling on an empty road.
    /// </remarks>
    [Fact]
    public void No_Peer_Is_Infinite_Room()
    {
        PeerContact.None.GapM.Should().Be(double.PositiveInfinity);
        Look([]).GapM.Should().Be(double.PositiveInfinity);
    }
}
