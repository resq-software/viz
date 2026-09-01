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

/// <summary>
/// The safe-action layer: that a declared safety behaviour is one the system actually carries
/// out, and that carrying it out never strands the asset.
/// </summary>
/// <remarks>
/// <see cref="LinkLossBehavior"/> was declared in the model and published on the wire from the
/// day the multi-domain asset landed, and until <see cref="SafeActionPolicy"/> nothing executed
/// it. An operator could read "on link loss: return to base" off a drone's panel and plan around
/// a promise no code kept. These tests exist to keep that from being true again, so they check
/// execution rather than declaration: what command comes out, for each domain, from the value
/// the domain state actually carries.
/// <para>
/// Three properties get more attention than the rest, because each is a defect this codebase has
/// already shipped. <b>Advertised is accepted</b> — every command the policy resolves to must be
/// one the asset's own capabilities, domain and target rules would take, checked against the
/// same catalog the validator uses. <b>Nothing is unrecoverable</b> — no state this layer keeps
/// may refuse the command that undoes it, and a latched emergency stop is left alone precisely
/// because the only command that reaches one is the command that releases it. <b>Two quantities
/// that differ are both published</b> — reported against effective freshness, the asset's own fix
/// against the position an operator holds, projected growth against accrued drift.
/// </para>
/// </remarks>
public partial class SafeActionPolicyTests
{
    /// <summary>What each domain declares it does on link loss, and the command that does it.</summary>
    public static TheoryData<VehicleClass, LinkLossBehavior, AssetCommandKind> LinkLossCases => new()
    {
        { VehicleClass.Multirotor, LinkLossBehavior.ReturnToBase, AssetCommandKind.ReturnToBase },
        { VehicleClass.AckermannRover, LinkLossBehavior.StopAndHold, AssetCommandKind.Stop },
        { VehicleClass.SurfaceVessel, LinkLossBehavior.DriftAndAlert, AssetCommandKind.Stop },
    };

    /// <summary>What each domain does about a spent reserve, which is not always what it does about silence.</summary>
    public static TheoryData<VehicleClass, AssetCommandKind> ReserveCases => new()
    {
        { VehicleClass.Multirotor, AssetCommandKind.ReturnToBase },
        { VehicleClass.AckermannRover, AssetCommandKind.Stop },
        { VehicleClass.SurfaceVessel, AssetCommandKind.Stop },
    };

    /// <summary>Every class this build can spawn, so no domain can be quietly skipped.</summary>
    public static TheoryData<VehicleClass> SpawnableClasses => new()
    {
        VehicleClass.Multirotor,
        VehicleClass.AckermannRover,
        VehicleClass.SurfaceVessel,
    };

    /// <summary>Each domain's advertised link-loss behaviour comes back out as a real command.</summary>
    /// <remarks>
    /// The test the whole layer exists for. Before it, the three behaviours were published on
    /// the wire and executed nowhere, so an operator panel read a promise nothing kept.
    /// </remarks>
    /// <param name="vehicleClass">Class to build a descriptor and domain state for.</param>
    /// <param name="declared">Behaviour that class advertises.</param>
    /// <param name="expected">Command that carries it out.</param>
    [Theory]
    [MemberData(nameof(LinkLossCases))]
    public void Link_Loss_Executes_The_Behaviour_Each_Domain_Declares(
        VehicleClass vehicleClass, LinkLossBehavior declared, AssetCommandKind expected)
    {
        var descriptor = Describe(vehicleClass);
        var state = State(DomainStateFor(vehicleClass, declared));

        var assessment = SafeActionPolicy.Evaluate(descriptor, state, environment: null, 10.0);

        assessment.Trigger.Should().Be(SafeActionTrigger.LinkLoss);
        assessment.ReasonCode.Should().Be(SafeActionReasons.LinkLost);

        assessment.DeclaredBehaviour.Should().Be(
            declared, "the behaviour acted on must be the one the domain state published");

        assessment.ResolvedCommand.Should().Be(
            expected, "a declared behaviour that resolves to nothing is a promise nobody keeps");

        assessment.IsDegraded.Should().BeFalse(
            "each domain's own declared behaviour is issuable as it stands");
    }

    /// <summary>The declared value is read, not a per-domain assumption about it.</summary>
    [Fact]
    public void Link_Loss_Follows_A_Declared_Behaviour_That_Is_Not_The_Domain_Default()
    {
        // The point of reading the published value rather than switching on the domain: a rover
        // that has been given somewhere to go back to must go back to it, not stop where a
        // hardcoded per-domain assumption thinks a rover belongs.
        var descriptor = Describe(VehicleClass.AckermannRover);
        var state = State(Ground(LinkLossBehavior.ReturnToBase));

        var assessment = SafeActionPolicy.Evaluate(descriptor, state, environment: null, 10.0);

        assessment.ResolvedCommand.Should().Be(AssetCommandKind.ReturnToBase);
        assessment.IsDegraded.Should().BeFalse();
    }

    /// <summary>A spent energy reserve starts the recovery each profile calls for.</summary>
    /// <param name="vehicleClass">Class to build a descriptor and domain state for.</param>
    /// <param name="expected">Command that recovery resolves to.</param>
    [Theory]
    [MemberData(nameof(ReserveCases))]
    public void A_Spent_Reserve_Triggers_The_Profiles_Own_Behaviour(
        VehicleClass vehicleClass, AssetCommandKind expected)
    {
        var state = State(
            DomainStateFor(vehicleClass, DefaultBehaviourFor(vehicleClass)), lowEnergy: true);

        var assessment = SafeActionPolicy.Evaluate(
            Describe(vehicleClass), state, environment: null, elapsedSinceContactSeconds: 0.0);

        assessment.Trigger.Should().Be(SafeActionTrigger.LowEnergy);
        assessment.ReasonCode.Should().Be(SafeActionReasons.EnergyReserve);
        assessment.ResolvedCommand.Should().Be(expected);
    }

    /// <summary>Drifting answers a lost operator, never a flat battery.</summary>
    [Fact]
    public void A_Spent_Reserve_Never_Casts_A_Vessel_Adrift()
    {
        // Link loss and a spent reserve are different situations: the operator is still there for
        // the second one, so the hull stops working the mission instead of being cast adrift.
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.SurfaceVessel),
            State(Surface(LinkLossBehavior.DriftAndAlert), lowEnergy: true),
            environment: null,
            elapsedSinceContactSeconds: 0.0);

        assessment.DeclaredBehaviour.Should().Be(LinkLossBehavior.StopAndHold);
    }

    /// <summary>An asset with no reserve of its own cannot spend one.</summary>
    [Fact]
    public void An_Externally_Powered_Asset_Is_Never_Low_On_Energy()
    {
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.AckermannRover),
            State(Ground(), lowEnergy: true, externallyPowered: true),
            environment: null,
            elapsedSinceContactSeconds: 0.0);

        assessment.Trigger.Should().Be(
            SafeActionTrigger.None, "an asset fed from outside has no reserve to spend");
    }

    /// <summary>Silence outranks a spent reserve, because the operator is gone either way.</summary>
    [Fact]
    public void Link_Loss_Wins_When_Both_Conditions_Stand()
    {
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.AckermannRover),
            State(Ground(LinkLossBehavior.DriftAndAlert), lowEnergy: true),
            environment: null,
            elapsedSinceContactSeconds: 10.0);

        assessment.Trigger.Should().Be(SafeActionTrigger.LinkLoss);

        assessment.DeclaredBehaviour.Should().Be(
            LinkLossBehavior.DriftAndAlert,
            "a silent asset has lost the operator who would have decided about its reserve, so "
            + "the instruction it was given in advance is the one that applies");
    }

    /// <summary>A navigation fix too poor to fly home on degrades the return into a landing.</summary>
    [Fact]
    public void An_Airframe_With_A_Poor_Fix_Lands_Instead_Of_Returning()
    {
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.Multirotor),
            State(Air(), positionSigmaM: 120.0),
            environment: null,
            elapsedSinceContactSeconds: 10.0);

        assessment.DeclaredBehaviour.Should().Be(LinkLossBehavior.ReturnToBase);
        assessment.ResolvedCommand.Should().Be(AssetCommandKind.Land);
        assessment.ResolutionReason.Should().Be(SafeActionReasons.PositionUncertain);
        assessment.IsDegraded.Should().BeTrue();
    }

    /// <summary>Docking needs a berth to name, and a fallback has none.</summary>
    [Fact]
    public void A_Behaviour_Needing_A_Destination_Degrades_Rather_Than_Being_Issued()
    {
        // Docking needs a berth to name and a fallback has none, so the honest outcome is a
        // recorded degrade rather than a command issued for the executor to refuse.
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.SurfaceVessel),
            State(Surface(LinkLossBehavior.Dock)),
            environment: null,
            elapsedSinceContactSeconds: 10.0);

        assessment.ResolvedCommand.Should().Be(AssetCommandKind.Stop);
        assessment.ResolutionReason.Should().Be(SafeActionReasons.CommandTargetRequired);
    }

    /// <summary>An asset that declares nothing still gets something safe done to it.</summary>
    [Fact]
    public void An_Undeclared_Behaviour_Degrades_To_Stopping()
    {
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.AckermannRover),
            State(domainState: null),
            environment: null,
            elapsedSinceContactSeconds: 10.0);

        assessment.DeclaredBehaviour.Should().Be(LinkLossBehavior.Unknown);
        assessment.ResolvedCommand.Should().Be(AssetCommandKind.Stop);
        assessment.ResolutionReason.Should().Be(SafeActionReasons.BehaviourUnknown);
    }

    /// <summary>A fallback may not release the emergency stop an operator set.</summary>
    [Fact]
    public void Nothing_Is_Issued_While_A_Latched_Emergency_Stop_Stands()
    {
        // The trap this avoids: the only command a latched asset accepts is stop, and stop is
        // what releases the latch. A fallback issued here would undo an operator's emergency stop
        // with nobody watching.
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.AckermannRover),
            State(Ground(), operationalState: OperationalState.Emergency),
            environment: null,
            elapsedSinceContactSeconds: 10.0);

        assessment.IsEmergencyStopped.Should().BeTrue();
        assessment.ResolvedCommand.Should().Be(AssetCommandKind.Unspecified);
        assessment.ResolutionReason.Should().Be(SafeActionReasons.EmergencyStopEngaged);
    }

    /// <summary>Advertised is accepted, applied to this layer's own output.</summary>
    /// <param name="vehicleClass">Class to run every declared behaviour against.</param>
    [Theory]
    [MemberData(nameof(SpawnableClasses))]
    public void Every_Resolved_Command_Is_One_The_Asset_Would_Accept(VehicleClass vehicleClass)
    {
        var descriptor = Describe(vehicleClass);

        foreach (var behaviour in Enum.GetValues<LinkLossBehavior>())
        {
            var assessment = SafeActionPolicy.Evaluate(
                descriptor,
                State(DomainStateFor(vehicleClass, behaviour)),
                environment: null,
                elapsedSinceContactSeconds: 10.0);

            if (assessment.ResolvedCommand == AssetCommandKind.Unspecified)
            {
                continue;
            }

            string? token = AssetCommandTranslator.ToCatalogKind(assessment.ResolvedCommand);
            CommandCatalog.TryGet(token, out var definition).Should().BeTrue(
                "the policy may only resolve to a command this build registers");

            // TryGet's NotNullWhen contract is not visible through the assertion above, so the
            // suppression stands in for a null check the previous line has already made.
            definition!.IsSatisfiedBy(descriptor.Capabilities).Should().BeTrue(
                "resolving to a command the asset cannot take is the advertised-but-refused "
                + "defect in a new place");

            definition.AppliesTo(descriptor.Domain).Should().BeTrue();

            definition.RequiresTarget.Should().BeFalse(
                "a fallback has no destination to name, so it may never resolve to a command "
                + "that needs one");
        }
    }

    /// <summary>The domain extension a class publishes, carrying a chosen behaviour.</summary>
    /// <param name="vehicleClass">Class to build a domain state for.</param>
    /// <param name="behaviour">Link-loss behaviour to advertise.</param>
    /// <returns>The typed domain half.</returns>
    private static IAssetDomainState DomainStateFor(
        VehicleClass vehicleClass, LinkLossBehavior behaviour) =>
        AssetProfiles.DomainFor(vehicleClass) switch
        {
            AssetDomain.Air => Air(behaviour),
            AssetDomain.Ground => Ground(behaviour),
            _ => Surface(behaviour),
        };

    /// <summary>The behaviour each domain's own safety policy derives by default.</summary>
    /// <param name="vehicleClass">Class to ask about.</param>
    /// <returns>The behaviour that domain's executor publishes.</returns>
    private static LinkLossBehavior DefaultBehaviourFor(VehicleClass vehicleClass) =>
        AssetProfiles.DomainFor(vehicleClass) switch
        {
            AssetDomain.Air => LinkLossBehavior.ReturnToBase,
            AssetDomain.Ground => LinkLossBehavior.StopAndHold,
            _ => LinkLossBehavior.DriftAndAlert,
        };
}
