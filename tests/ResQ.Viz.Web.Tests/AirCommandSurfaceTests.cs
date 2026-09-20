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
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Entities;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// What the air executor's command switch will answer to, asked of the switch itself.
/// </summary>
/// <remarks>
/// The air domain deliberately keeps no hand-written table of foreign commands — the argument is
/// recorded at <c>AirAsset.Apply</c>, and it is that the catalog is the one rule and a second
/// copy of a rule drifts. That choice is sound, and it has a cost: nothing but the switch itself
/// states what air accepts, so the switch growing a case the catalog never offered is invisible.
/// These assertions are what the choice trades for, and until they existed it was not paid for.
/// </remarks>
public sealed class AirCommandSurfaceTests
{
    private const string ProbeId = "probe-1";

    private const float CruiseAltitudeM = 60f;

    /// <summary>A multirotor accepts exactly the commands the catalog advertises to air.</summary>
    /// <remarks>
    /// The converse of <c>GroundWiringHardeningTests
    /// .Every_Command_Advertised_To_An_Asset_Is_One_That_Asset_Accepts</c>, and the direction
    /// that was missing. That assertion walks the catalog and checks each advertised command is
    /// accepted. It cannot see a command an executor accepts and the catalog never offers,
    /// because it never asks — it only ever enumerates rows that are advertised already.
    /// <para>
    /// The gap was not hypothetical. <see cref="AssetCommandKind.StationKeep"/> had a case in
    /// the air executor while <see cref="CommandCatalog"/> registers that row
    /// <c>domains: SurfaceOnly</c>, and every gate ahead of the switch let it through: the
    /// addressee matches, this domain passes no domain table by choice, and the capability check
    /// passes because a multirotor genuinely declares
    /// <see cref="AssetCapability.StationKeep"/>. The switch was the last gate and it said yes.
    /// </para>
    /// <para>
    /// Applied straight to the asset rather than through a room, deliberately. Through a room
    /// the validator refuses first and this passes without the executor ever being reached —
    /// vacuously, because what the executor does once something is past the validator is the
    /// whole question. Nor is that a contrived path: the v1 compatibility adapter builds a
    /// <see cref="SimulatedAssetCommand"/> directly, which is why ground and surface each keep a
    /// domain table, and why the domain that keeps none needs this.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_Air_Asset_Accepts_Exactly_The_Commands_The_Catalog_Advertises_To_Air()
    {
        var capabilities = AssetProfiles.CapabilitiesFor(VehicleClass.Multirotor);
        var foreign = new SortedSet<string>(StringComparer.Ordinal);
        var refused = new SortedSet<string>(StringComparer.Ordinal);
        int advertised = 0;

        foreach (var definition in CommandCatalog.All)
        {
            var kind = AssetCommandTranslator.ToAssetCommandKind(definition.Kind);

            if (kind == AssetCommandKind.Unspecified)
            {
                continue;
            }

            if (definition.AppliesTo(AssetDomain.Air) && definition.IsSatisfiedBy(capabilities))
            {
                advertised++;
            }
            else
            {
                foreign.Add(definition.Kind);
            }

            // A fresh asset per command. Land latches a landed flag, and while an emergency stop
            // is a hover here rather than a state the asset holds, relying on either staying that
            // way would make the result depend on catalog order — which is not what this asserts.
            var result = BuildAirAsset().Apply(ProbeFor(definition, kind));

            if (!result.IsAccepted && IsStructuralRefusal(result.Reason))
            {
                refused.Add(definition.Kind);
            }
        }

        advertised.Should().BeGreaterThan(0, "the comparison is vacuous if the catalog offers air nothing");

        foreign.Should().NotBeEmpty(
            "the comparison is vacuous unless commands from the other domains were probed too");

        refused.Should().BeEquivalentTo(
            foreign,
            "an executor's switch is a second statement of the catalog's domain list, and two "
            + "statements of one rule drift the moment either is edited alone. A command "
            + "accepted here but advertised nowhere is reachable only through the v1 adapter, "
            + "which skips the validator — so it is exactly the path nothing else watches. "
            + "Close a divergence by removing the case or by widening the catalog row, never by "
            + "excusing it here");
    }

    /// <summary>Whether a refusal is about what the asset IS rather than what state it is in.</summary>
    /// <remarks>
    /// The same discriminator <c>GroundWiringHardeningTests</c> uses, and for the same reason: an
    /// asset refuses <c>takeoff</c> when already airborne and <c>land</c> when already down, and
    /// those are correct answers about a moment rather than about the command belonging to
    /// another domain. Counting them would make this assert which state the probe happened to
    /// start in.
    /// </remarks>
    /// <param name="reason">Rejection token from the executor.</param>
    /// <returns><see langword="true"/> when the refusal is structural.</returns>
    private static bool IsStructuralRefusal(string? reason) =>
        reason is not null
        && (reason.StartsWith("capability.", StringComparison.Ordinal)
            || reason.EndsWith(".unsupported", StringComparison.Ordinal)
            || reason.EndsWith(".unavailable", StringComparison.Ordinal));

    /// <summary>The instance, named so a regression says what broke rather than that two sets differ.</summary>
    /// <remarks>
    /// Kept beside the general invariant for the reason
    /// <c>A_Rover_Is_Never_Offered_A_Steering_Control_It_Would_Refuse</c> is kept beside its own:
    /// a set-equality failure reports that two collections differ, and this reports which command
    /// it was and why it was reachable.
    /// </remarks>
    [Fact]
    public void A_Drone_Refuses_Station_Keeping_Because_The_Catalog_Says_It_Is_Not_Its_Command()
    {
        var capabilities = AssetProfiles.CapabilitiesFor(VehicleClass.Multirotor);

        // Both premises are asserted rather than assumed. If a multirotor stops declaring the
        // capability, or the catalog gains an air row, the refusal below starts coming from a
        // different gate and this test keeps passing while testing nothing.
        capabilities.Should().HaveFlag(
            AssetCapability.StationKeep,
            "the refusal under test is the switch's; a missing capability would make it the gate's");

        var definition = CommandCatalog.All
            .Single(d => string.Equals(d.Kind, CommandKinds.StationKeep, StringComparison.Ordinal));

        definition.AppliesTo(AssetDomain.Air).Should().BeFalse(
            "a catalog row for air would make the refusal below wrong");

        var result = BuildAirAsset().Apply(
            ProbeFor(definition, AssetCommandKind.StationKeep));

        result.IsAccepted.Should().BeFalse(
            "a multirotor clears every gate ahead of the switch for this command — its own "
            + "capability bit is set and this domain keeps no domain table — so the switch is "
            + "the only thing left that can refuse a command the catalog never offered it");

        result.Reason.Should().Be("command.unsupported");
    }

    /// <summary>A multirotor asset at altitude, freshly built.</summary>
    /// <returns>An air asset carrying the shipped multirotor descriptor.</returns>
    private static AirAsset BuildAirAsset()
    {
        var drone = new SimulatedDrone(
            ProbeId, new Vector3(0f, CruiseAltitudeM, 0f), FlightModelType.Kinematic);

        return new AirAsset(drone, AssetProfiles.Create(ProbeId, VehicleClass.Multirotor));
    }

    /// <summary>A probe built to the catalog row's own shape.</summary>
    /// <remarks>
    /// Every scalar parameter is supplied whether or not the kind reads it, because a probe
    /// tailored per kind would refuse <c>setSpeed</c> for a missing speed and that refusal is
    /// indistinguishable from the domain refusal under test — which would quietly turn the set
    /// comparison into a comparison of which fields the probe filled in.
    /// <para>
    /// The target is the exception, and it is read from <see cref="CommandDefinition"/> rather
    /// than always supplied, because for one row a target is not an ignored extra but a refusal:
    /// <c>land</c> takes none and answers <c>command.target.unsupported</c> to one, the flight
    /// model having no way to sequence "fly there, then descend". That token ends in
    /// <c>.unsupported</c>, so a blanket target made the one command in the catalog that
    /// documents its own absent target look like a domain divergence.
    /// </para>
    /// </remarks>
    /// <param name="definition">Catalog row the probe is built to.</param>
    /// <param name="kind">Executable kind to probe with.</param>
    /// <returns>A command addressed to the probe asset.</returns>
    private static SimulatedAssetCommand ProbeFor(CommandDefinition definition, AssetCommandKind kind) => new(
        kind,
        ProbeId,
        Target: definition.AllowedTargets == CommandTargetKinds.None
            ? null
            : new FramedPose(
                CoordinateFrame.LocalEus,
                null,
                new Vector3(25f, CruiseAltitudeM, 25f),
                Quaternion.Identity),
        SpeedMps: 4.0,
        HeadingRad: 0.0,
        AltitudeM: CruiseAltitudeM,
        CommandId: Guid.Empty,
        AltitudeReference: VerticalReference.AboveGround);
}
