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
/// The screening sequence every asset runs before it looks at what a command asks for.
/// </summary>
/// <remarks>
/// The order is a safety property and was argued at length in two files, implemented in three,
/// and asserted in none. These cases state it directly: each gate is shown to fire ahead of the
/// next by constructing a command that would be refused by both and checking which token comes
/// back. A reordering that no compiler notices now fails here.
/// </remarks>
public sealed class AssetCommandGateTests
{
    private const string Asset = "rover-1";
    private const AssetCapability None = 0;
    private const AssetCapability Drive = AssetCapability.Navigate2D;

    private static string? Screen(
        AssetCommandKind kind,
        string assetId = Asset,
        AssetCapability capabilities = Drive,
        bool stopped = false,
        Func<AssetCommandKind, string?>? domain = null) =>
        AssetCommandGate.Screen(
            new SimulatedAssetCommand(Kind: kind, AssetId: assetId),
            Asset,
            capabilities,
            stopped,
            domain ?? (static _ => null));

    // ─── The order, which is the point ──────────────────────────────────────

    /// <summary>A command for another asset is refused before anything else is consulted.</summary>
    /// <remarks>
    /// Somebody else's command is not this asset's to judge. Its capabilities and its emergency
    /// latch are irrelevant, and reporting one of those would send an operator to the wrong
    /// vehicle looking for the wrong problem.
    /// </remarks>
    [Fact]
    public void The_Addressee_Is_Checked_Before_Everything_Else()
    {
        // Wrong asset, AND a missing capability, AND latched, AND another domain's command.
        Screen(
            AssetCommandKind.Dock,
            assetId: "somebody-else",
            capabilities: None,
            stopped: true,
            domain: static _ => "command.domain.surface")
            .Should().Be("command.assetMismatch");
    }

    /// <summary>The domain is checked before the capability.</summary>
    /// <remarks>
    /// Defence in depth, and it only works in this order. The catalog would already refuse these,
    /// but the v1 adapter does not consult the catalog and a descriptor that wrongly declared
    /// another domain's capability would sail through the capability gate. Checking capability
    /// first would let exactly that descriptor through.
    /// </remarks>
    [Fact]
    public void The_Domain_Is_Checked_Before_The_Capability()
    {
        Screen(
            AssetCommandKind.Dock,
            capabilities: None,
            domain: static _ => "command.domain.surface")
            .Should().Be("command.domain.surface");
    }

    /// <summary>The capability is checked before the emergency latch.</summary>
    /// <remarks>
    /// A latched asset refusing a command it could never perform should say which of the two is
    /// the real problem, and the permanent one is more useful than the temporary one.
    /// </remarks>
    [Fact]
    public void The_Capability_Is_Checked_Before_The_Latch()
    {
        Screen(AssetCommandKind.Dock, capabilities: None, stopped: true)
            .Should().Be("capability.missing");
    }

    /// <summary>The latch is last, and it refuses.</summary>
    [Fact]
    public void A_Latched_Asset_Refuses_An_Ordinary_Command()
    {
        Screen(AssetCommandKind.GoTo, stopped: true).Should().Be("asset.emergencyStopped");
    }

    // ─── The release, which must get through ────────────────────────────────

    /// <summary>A latched asset still admits the three commands that can free it.</summary>
    /// <remarks>
    /// A stop that cannot be released is a bricked asset. The repeated emergency stop is included
    /// so re-issuing one is never refused: refusing to stop something because it is already
    /// stopping is exactly backwards.
    /// </remarks>
    [Theory]
    [InlineData(AssetCommandKind.Stop)]
    [InlineData(AssetCommandKind.ResumeAutonomy)]
    [InlineData(AssetCommandKind.EmergencyStop)]
    public void A_Latched_Asset_Still_Admits_A_Release(AssetCommandKind kind)
    {
        Screen(kind, stopped: true).Should().BeNull();
    }

    /// <summary>The release exemption does not bypass the gates above it.</summary>
    /// <remarks>
    /// Being a release is a reason to pass the latch, not a reason to reach an asset that is not
    /// the addressee.
    /// </remarks>
    [Fact]
    public void A_Release_For_Another_Asset_Is_Still_Refused()
    {
        Screen(AssetCommandKind.Stop, assetId: "somebody-else", stopped: true)
            .Should().Be("command.assetMismatch");
    }

    // ─── The ordinary case ──────────────────────────────────────────────────

    /// <summary>A command that passes every gate returns nothing at all.</summary>
    [Fact]
    public void A_Permitted_Command_Is_Not_Refused()
    {
        Screen(AssetCommandKind.GoTo).Should().BeNull();
    }

    /// <summary>A domain that declines to keep a table is honoured.</summary>
    /// <remarks>
    /// The air domain's documented position: the catalog's own rule rather than a restatement of
    /// it. Ground and surface hold the opposite view, also documented. The gate has to serve both
    /// without preferring either.
    /// </remarks>
    [Fact]
    public void A_Domain_With_No_Table_Refuses_Nothing_On_That_Ground()
    {
        // Given the capability, so this isolates the domain gate rather than tripping the one
        // below it — which is what the first version of this test did.
        Screen(
            AssetCommandKind.Dock,
            capabilities: AssetCapability.Dock,
            domain: static _ => null)
            .Should().BeNull();
    }

    // ─── Target resolution ──────────────────────────────────────────────────

    /// <summary>Only the scene frame is accepted.</summary>
    /// <remarks>
    /// Converting from another frame needs a shared origin, and guessing one is how a waypoint
    /// ends up mirrored about the chart.
    /// </remarks>
    [Fact]
    public void A_Target_In_Another_Frame_Is_Refused()
    {
        var pose = new FramedPose(
            CoordinateFrame.BodyFlu, null, Vector3.One, Quaternion.Identity);

        AssetCommandGate.ResolveTarget(pose, out _).Should().Be("command.target.frame");
    }

    /// <summary>A missing target is refused rather than defaulted to the origin.</summary>
    /// <remarks>
    /// The out parameter is zeroed on every failure, so a caller that ignores the token drives to
    /// the scene origin rather than somewhere random — but it is still a caller bug, and the
    /// token is the contract.
    /// </remarks>
    [Fact]
    public void A_Missing_Target_Is_Refused_And_Zeroed()
    {
        AssetCommandGate.ResolveTarget(null, out var target).Should().NotBeNull();
        target.Should().Be(Vector3.Zero);
    }

    /// <summary>A scene-frame target is resolved to its position.</summary>
    [Fact]
    public void A_Scene_Frame_Target_Resolves()
    {
        var pose = new FramedPose(
            CoordinateFrame.LocalEus, null, new Vector3(3f, 0f, -4f), Quaternion.Identity);

        AssetCommandGate.ResolveTarget(pose, out var target).Should().BeNull();
        target.Should().Be(new Vector3(3f, 0f, -4f));
    }

    // ─── Speed ──────────────────────────────────────────────────────────────

    /// <summary>Zero is refused rather than treated as a stop.</summary>
    /// <remarks>
    /// A speed setpoint is a standing value governing every leg after it, so accepting zero
    /// leaves a vehicle that answers every later waypoint by not moving, with nothing in its
    /// state explaining why. Stopping is a command of its own.
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void A_Non_Positive_Speed_Is_Refused(double speed)
    {
        AssetCommandGate.ValidateSpeed(
            new SimulatedAssetCommand(
                Kind: AssetCommandKind.SetSpeed, AssetId: Asset, SpeedMps: speed),
            out _)
            .Should().Be("command.speed.outOfRange");
    }

    /// <summary>An absent or non-finite speed is a different refusal from an out-of-range one.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_Missing_Or_Nonsense_Speed_Is_Refused_As_Missing(double? speed)
    {
        AssetCommandGate.ValidateSpeed(
            new SimulatedAssetCommand(
                Kind: AssetCommandKind.SetSpeed, AssetId: Asset, SpeedMps: speed),
            out _)
            .Should().Be("command.speed.missing");
    }

    /// <summary>A usable speed passes and is handed back.</summary>
    [Fact]
    public void A_Positive_Speed_Passes()
    {
        AssetCommandGate.ValidateSpeed(
            new SimulatedAssetCommand(
                Kind: AssetCommandKind.SetSpeed, AssetId: Asset, SpeedMps: 2.5),
            out var speed)
            .Should().BeNull();

        speed.Should().Be(2.5);
    }
}
