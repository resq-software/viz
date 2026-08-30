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

/// <summary>The cross-domain rejections the contract calls out by name.</summary>
/// <remarks>
/// Asking a rover to take off, or an asset without the dock capability to dock, must be refused
/// with a machine-readable reason and no side effect. Kept in their own file because these are
/// the cases a reviewer will want to read end to end.
/// </remarks>
public sealed partial class AssetCommandValidationTests
{
    // ── Domain gating the contract calls out by name ───────────────────────────

    [Theory]
    [InlineData(CommandKinds.Takeoff, VehicleClass.AckermannRover, CommandRejectionReasons.CapabilityNotDeclared)]
    [InlineData(CommandKinds.Takeoff, VehicleClass.DifferentialRover, CommandRejectionReasons.CapabilityNotDeclared)]
    [InlineData(CommandKinds.Takeoff, VehicleClass.TrackedRover, CommandRejectionReasons.CapabilityNotDeclared)]
    [InlineData(CommandKinds.Takeoff, VehicleClass.SurfaceVessel, CommandRejectionReasons.CapabilityNotDeclared)]
    [InlineData(CommandKinds.Land, VehicleClass.AckermannRover, CommandRejectionReasons.DomainNotApplicable)]
    [InlineData(CommandKinds.Land, VehicleClass.DifferentialRover, CommandRejectionReasons.DomainNotApplicable)]
    [InlineData(CommandKinds.Land, VehicleClass.TrackedRover, CommandRejectionReasons.DomainNotApplicable)]
    [InlineData(CommandKinds.Land, VehicleClass.SurfaceVessel, CommandRejectionReasons.CapabilityNotDeclared)]
    [InlineData(CommandKinds.SetAltitude, VehicleClass.AckermannRover, CommandRejectionReasons.CapabilityNotDeclared)]
    [InlineData(CommandKinds.SetAltitude, VehicleClass.SurfaceVessel, CommandRejectionReasons.CapabilityNotDeclared)]
    [InlineData(CommandKinds.Loiter, VehicleClass.AckermannRover, CommandRejectionReasons.CapabilityNotDeclared)]
    [InlineData(CommandKinds.Loiter, VehicleClass.SurfaceVessel, CommandRejectionReasons.CapabilityNotDeclared)]
    public void Air_Commands_Are_Rejected_For_Ground_And_Surface_Profiles(
        string kind, VehicleClass vehicleClass, string expectedReason)
    {
        var descriptor = AssetProfiles.Create(AssetId, vehicleClass);
        descriptor.Domain.Should().NotBe(AssetDomain.Air);

        var result = CommandCatalog.Validate(EnvelopeFor(Definition(kind)), descriptor, StateFor(), Now);

        AssertRejectedCleanly(result, $"'{kind}' on a {vehicleClass}");
        result.ReasonCode.Should().Be(expectedReason);
    }

    [Theory]
    [InlineData(CommandKinds.Takeoff)]
    [InlineData(CommandKinds.Land)]
    [InlineData(CommandKinds.SetAltitude)]
    [InlineData(CommandKinds.Loiter)]
    public void Air_Commands_Stay_Rejected_When_A_Ground_Or_Surface_Descriptor_Over_Declares(string kind)
    {
        var envelope = EnvelopeFor(Definition(kind));

        foreach (var domain in new[] { AssetDomain.Ground, AssetDomain.Surface })
        {
            // A descriptor claiming Takeoff and Land it cannot honour. The domain list is the
            // gate that still fires, which is why it is not folded into the capability mask.
            var result = CommandCatalog.Validate(
                envelope, DescriptorFor(domain, AllCapabilities), StateFor(), Now);

            AssertRejectedCleanly(result, $"'{kind}' on an over-declaring {domain} asset");
            result.ReasonCode.Should().Be(CommandRejectionReasons.DomainNotApplicable);
        }
    }

    [Fact]
    public void Dock_Is_Rejected_Without_The_Dock_Capability()
    {
        var definition = Definition(CommandKinds.Dock);
        var envelope = EnvelopeFor(definition);

        var undockable = DescriptorFor(AssetDomain.Surface, AllCapabilities & ~AssetCapability.Dock);
        var result = CommandCatalog.Validate(envelope, undockable, StateFor(), Now);

        AssertRejectedCleanly(result, "dock without the Dock capability");
        result.ReasonCode.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);

        // A multirotor declares no Dock either, and the capability gate runs before the domain
        // gate, so this is a capability refusal rather than a domain one.
        CommandCatalog.Validate(
                envelope, AssetProfiles.Create(AssetId, VehicleClass.Multirotor), StateFor(), Now)
            .ReasonCode.Should().Be(CommandRejectionReasons.CapabilityNotDeclared);
    }

    [Fact]
    public void Dock_Is_Accepted_For_A_Vessel_That_Declares_It()
    {
        var vessel = AssetProfiles.Create(AssetId, VehicleClass.SurfaceVessel);
        vessel.Capabilities.Should().HaveFlag(AssetCapability.Dock);

        var envelope = EnvelopeFor(Definition(CommandKinds.Dock)) with
        {
            Target = new AssetCommandTarget("dock-north", StandoffM: 8.0),
        };

        var result = CommandCatalog.Validate(envelope, vessel, StateFor(), Now);

        result.IsAccepted.Should().BeTrue("refused with {0}: {1}", result.ReasonCode, result.Message);
        result.Intent?.Target.Should().BeOfType<AssetCommandTarget>(
            "an asset-referenced dock target is resolved when the command executes, not when it is issued");
    }

    [Fact]
    public void StationKeep_Is_Rejected_For_A_Hull_That_Cannot_Hold_Station()
    {
        var vessel = AssetProfiles.Create(AssetId, VehicleClass.SurfaceVessel);
        vessel.Motion.CanStationKeep.Should().BeFalse();
        vessel.Motion.MinSpeedMps.Should().BeGreaterThan(0);

        var result = CommandCatalog.Validate(
            EnvelopeFor(Definition(CommandKinds.StationKeep)), vessel, StateFor(), Now);

        AssertRejectedCleanly(result, "stationKeep on a displacement hull");
        result.ReasonCode.Should().Be(
            CommandRejectionReasons.CapabilityNotDeclared,
            "a single-screw hull loses steerage below its minimum speed, so holding a spot is a "
            + "capability it must not claim");
    }

    [Fact]
    public void Zero_Speed_Is_Refused_For_A_Hull_And_Allowed_For_A_Multirotor()
    {
        var envelope = EnvelopeFor(Definition(CommandKinds.SetSpeed)) with
        {
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CommandParameters.Speed] = "0",
            },
        };

        var vesselResult = CommandCatalog.Validate(
            envelope, AssetProfiles.Create(AssetId, VehicleClass.SurfaceVessel), StateFor(), Now);

        AssertRejectedCleanly(vesselResult, "setSpeed 0 on a displacement hull");
        vesselResult.ReasonCode.Should().Be(CommandRejectionReasons.ParameterOutOfRange);
        vesselResult.Field.Should().Be($"parameters.{CommandParameters.Speed}");

        CommandCatalog.Validate(
                envelope, AssetProfiles.Create(AssetId, VehicleClass.Multirotor), StateFor(), Now)
            .IsAccepted.Should().BeTrue("a multirotor can hold a hover at zero ground speed");
    }

    [Fact]
    public void Stop_Commands_Are_Never_Blocked_By_State_Capability_Or_Freshness()
    {
        var unhappyStates = new[]
        {
            OperationalState.Faulted, OperationalState.Offline,
            OperationalState.Emergency, OperationalState.Unknown,
        };

        foreach (var kind in new[] { CommandKinds.Stop, CommandKinds.EmergencyStop })
        {
            var envelope = EnvelopeFor(Definition(kind));

            foreach (var operationalState in unhappyStates)
            {
                CommandCatalog.Validate(
                        envelope,
                        DescriptorFor(AssetDomain.Ground, AssetCapability.None),
                        StateFor(operationalState, DataFreshness.Lost),
                        Now)
                    .IsAccepted.Should().BeTrue(
                        "refusing '{0}' because the asset is {1} is exactly backwards",
                        kind, operationalState);
            }
        }
    }
}
