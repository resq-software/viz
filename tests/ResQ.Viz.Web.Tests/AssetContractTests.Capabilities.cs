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

using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Capability gating, power, and the motion constraints a task allocator reads.</summary>
/// <remarks>
/// Behaviour is gated by declared capability rather than by a switch over vehicle class, so
/// these cases assert on the mask and never on the class.
/// </remarks>
public partial class AssetContractTests
{
    // ─── Capability gating ──────────────────────────────────────────────────

    /// <summary>
    /// The same command against the same domain is accepted or refused purely on the declared
    /// capability. The vehicle class is deliberately swapped the "wrong" way round in both cases.
    /// </summary>
    [Fact]
    public void Takeoff_Is_Gated_By_Declared_Capability_Not_By_Vehicle_Class()
    {
        var roverClassWithTakeoff = DescriptorFor(
            "air-1", AssetDomain.Air, VehicleClass.AckermannRover,
            AssetCapability.Arm | AssetCapability.Navigate3D | AssetCapability.Takeoff);
        var multirotorClassWithout = DescriptorFor(
            "air-2", AssetDomain.Air, VehicleClass.Multirotor,
            AssetProfiles.CapabilitiesFor(VehicleClass.Multirotor) & ~AssetCapability.Takeoff);

        var accepted = Validate(roverClassWithTakeoff, EnvelopeFor("air-1", CommandKinds.Takeoff));
        var refused = Validate(multirotorClassWithout, EnvelopeFor("air-2", CommandKinds.Takeoff));

        accepted.IsAccepted.Should().BeTrue("the descriptor declares Takeoff, whatever its class says");
        accepted.Intent!.RequiredCapabilities.Should().Be(AssetCapability.Takeoff);

        refused.IsAccepted.Should().BeFalse("the class says multirotor but the capability is withheld");
        refused.ReasonCode.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
    }

    /// <summary>Telling a rover to take off is refused with a machine-readable reason and no intent.</summary>
    [Fact]
    public void Takeoff_On_A_Rover_Is_Refused_With_A_Coded_Reason()
    {
        var rover = ProfileDescriptor("rover-1", VehicleClass.AckermannRover);

        var result = Validate(rover, EnvelopeFor("rover-1", CommandKinds.Takeoff));

        result.IsAccepted.Should().BeFalse();
        result.Intent.Should().BeNull("a rejection must not hand anything downstream that can act");
        result.ReasonCode.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
        result.ToProblem(traceId: "trace-1").Code.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
    }

    /// <summary>
    /// A descriptor that wrongly declares an air capability is still refused, on the domain gate.
    /// The two rejections carry different codes because they are different bugs.
    /// </summary>
    [Fact]
    public void A_Rover_Declaring_Takeoff_Is_Still_Refused_On_The_Domain_Gate()
    {
        var rover = DescriptorFor(
            "rover-2", AssetDomain.Ground, VehicleClass.TrackedRover,
            AssetProfiles.CapabilitiesFor(VehicleClass.TrackedRover) | AssetCapability.Takeoff);

        var result = Validate(rover, EnvelopeFor("rover-2", CommandKinds.Takeoff));

        result.IsAccepted.Should().BeFalse();
        result.ReasonCode.Should().Be(CommandRejectionReasons.DomainNotApplicable);
    }

    /// <summary>Docking is gated on the Dock capability, in both directions, on the same hull.</summary>
    [Fact]
    public void Dock_Is_Gated_On_The_Dock_Capability()
    {
        var vessel = ProfileDescriptor("vessel-1", VehicleClass.SurfaceVessel);
        var vesselWithoutDock = vessel with { Capabilities = vessel.Capabilities & ~AssetCapability.Dock };
        // A berth is a position: dock advertises a framed point (and, on an anchored deployment,
        // a geodetic one), and nothing else, because nothing else can be resolved to steer to.
        var target = new PointCommandTarget(SamplePose(), AcceptanceRadiusM: 3.0);

        var accepted = Validate(vessel, EnvelopeFor("vessel-1", CommandKinds.Dock, target));
        var refused = Validate(vesselWithoutDock, EnvelopeFor("vessel-1", CommandKinds.Dock, target));

        accepted.IsAccepted.Should().BeTrue();
        refused.IsAccepted.Should().BeFalse();
        refused.ReasonCode.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
    }

    /// <summary>
    /// "Wait here" is refused for a displacement hull, because the profile does not declare
    /// station keeping — which is the same fact its motion constraints record.
    /// </summary>
    [Fact]
    public void StationKeep_Is_Refused_For_A_Hull_That_Cannot_Hold_Station()
    {
        var vessel = ProfileDescriptor("vessel-2", VehicleClass.SurfaceVessel);

        var result = Validate(vessel, EnvelopeFor("vessel-2", CommandKinds.StationKeep));

        vessel.Motion.CanStationKeep.Should().BeFalse();
        vessel.Capabilities.HasFlag(AssetCapability.StationKeep).Should().BeFalse();
        result.IsAccepted.Should().BeFalse();
        result.ReasonCode.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
    }

    /// <summary>A refused command leaves the descriptor and the state exactly as it found them.</summary>
    [Fact]
    public void A_Refused_Command_Has_No_Side_Effects()
    {
        var descriptor = ProfileDescriptor("rover-3", VehicleClass.DifferentialRover);
        var state = StateFor("rover-3");
        var pristineDescriptor = ProfileDescriptor("rover-3", VehicleClass.DifferentialRover);
        var pristineState = StateFor("rover-3");

        var result = CommandCatalog.Validate(
            EnvelopeFor("rover-3", CommandKinds.Takeoff), descriptor, state, ValidationTime);

        result.IsAccepted.Should().BeFalse();

        // AssetDescriptor holds only value members, so record equality is already structural.
        // AssetState holds collections, whose record equality is reference equality — hence the
        // deep comparison there.
        descriptor.Should().Be(pristineDescriptor);
        state.Should().BeEquivalentTo(pristineState);
    }

    // ─── Power ──────────────────────────────────────────────────────────────

    /// <summary>A fuel-burning asset describes itself without any battery vocabulary.</summary>
    [Fact]
    public void PowerState_Describes_A_Fuel_Powered_Asset()
    {
        var power = new PowerState(
            Sources:
            [
                new PowerSource(
                    SourceId: "tank-1",
                    Kind: PowerSourceKind.Fuel,
                    PercentRemaining: 62.5,
                    RemainingEnergyWh: 41_000.0,
                    RemainingTime: TimeSpan.FromHours(6),
                    DrawWatts: 5_400.0),
            ],
            PercentRemaining: 62.5,
            RemainingEnergyWh: 41_000.0,
            RemainingTime: TimeSpan.FromHours(6));

        var restored = FromJson<PowerState>(ToJson(power));

        restored.Should().NotBeNull();
        restored!.Sources.Should().ContainSingle().Which.Kind.Should().Be(PowerSourceKind.Fuel);
        restored.Sources.Should().NotContain(source => source.Kind == PowerSourceKind.Battery);
        restored.RemainingTime.Should().Be(TimeSpan.FromHours(6));
        restored.IsExternallyPowered.Should().BeFalse();
        restored.IsCharging.Should().BeFalse();
    }

    /// <summary>
    /// A tethered asset has no meaningful percentage or endurance, and reports that as absent
    /// rather than as a misleading 0 or 100.
    /// </summary>
    [Fact]
    public void PowerState_Describes_An_Externally_Powered_Asset_Without_Faking_A_Percentage()
    {
        var power = new PowerState(
            Sources: [new PowerSource("tether-1", PowerSourceKind.Tethered, DrawWatts: 900.0)],
            IsExternallyPowered: true);

        var restored = FromJson<PowerState>(ToJson(power));

        restored.Should().NotBeNull();
        restored!.IsExternallyPowered.Should().BeTrue();
        restored.PercentRemaining.Should().BeNull("a tether has no state of charge to report");
        restored.RemainingEnergyWh.Should().BeNull();
        restored.RemainingTime.Should().BeNull("endurance on a tether is effectively unbounded");
        restored.Sources.Should().ContainSingle().Which.PercentRemaining.Should().BeNull();
    }

    /// <summary>The power records carry no battery-only field, so a non-battery asset never has to lie.</summary>
    [Fact]
    public void Power_Records_Carry_No_Battery_Specific_Fields()
    {
        PropertyNames<PowerState>().Should().NotContain(
            name => name.Contains("Battery", StringComparison.Ordinal));
        PropertyNames<PowerSource>().Should().NotContain(
            name => name.Contains("Battery", StringComparison.Ordinal));

        Enum.GetValues<PowerSourceKind>().Should().Contain(
            new[] { PowerSourceKind.Fuel, PowerSourceKind.Tethered, PowerSourceKind.External });
    }

    // ─── Motion constraints ─────────────────────────────────────────────────

    /// <summary>
    /// A displacement hull cannot stop and cannot stay put; a multirotor can do both. This pair is
    /// what a task allocator reads before assigning "wait here".
    /// </summary>
    [Fact]
    public void A_Vessel_Has_A_Floor_Speed_And_Passive_Drift_Where_A_Multirotor_Has_Neither()
    {
        var vessel = AssetProfiles.MotionFor(VehicleClass.SurfaceVessel);
        var multirotor = AssetProfiles.MotionFor(VehicleClass.Multirotor);

        vessel.MinSpeedMps.Should().BeGreaterThan(0, "below steerage way the rudder has no authority");
        vessel.PassiveDriftMps.Should().BeGreaterThan(0, "unpowered, a hull moves with the current");
        vessel.MinTurnRadiusM.Should().BeGreaterThan(0);
        vessel.CanStationKeep.Should().BeFalse();

        multirotor.MinSpeedMps.Should().Be(0);
        multirotor.PassiveDriftMps.Should().Be(0);
        multirotor.MinTurnRadiusM.Should().Be(0);
        multirotor.CanStationKeep.Should().BeTrue();
        multirotor.StationKeepCostW.Should().BeGreaterThan(0, "a hover is not free, even though it is possible");
    }
}
