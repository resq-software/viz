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

using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

// The position half of SafeActionPolicyTests: how uncertainty grows, which commands a position
// nobody can vouch for blocks, and that every refusal says why in a token. Split from the
// behaviour half so each file answers one question; the suite's summary lives on the primary
// declaration in SafeActionPolicyTests.cs.
public partial class SafeActionPolicyTests
{
    /// <summary>An hour of silence adds nothing to where a stopped rover is.</summary>
    [Fact]
    public void Silence_Adds_No_Uncertainty_To_A_Stopped_Rover()
    {
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.AckermannRover),
            State(Ground(growthMps: 0.0)),
            environment: new Plateau().Sample(default, 1.0),
            elapsedSinceContactSeconds: 3_600.0);

        assessment.PositionUncertaintyGrowthMps.Should().Be(0.0);

        assessment.ProjectedPositionUncertaintyM.Should().Be(
            0.0,
            "a rover that lost its link stopped and stayed put, so an hour of silence adds no "
            + "metres to where it is");

        assessment.IsPositionFixUsable.Should().BeTrue(
            "the vehicle's own fix is unaffected by the bearer that carries it");
    }

    /// <summary>A hull's uncertainty keeps growing for as long as it is out of contact.</summary>
    [Fact]
    public void Silence_Grows_A_Drifting_Vessels_Uncertainty()
    {
        var descriptor = Describe(VehicleClass.SurfaceVessel);
        var state = State(Surface());

        var atOneMinute = SafeActionPolicy.Evaluate(descriptor, state, environment: null, 60.0);
        var atTwoMinutes = SafeActionPolicy.Evaluate(descriptor, state, environment: null, 120.0);

        atOneMinute.ProjectedPositionUncertaintyM.Should().BeApproximately(VesselDriftMps * 60.0, 1e-9);

        atTwoMinutes.ProjectedPositionUncertaintyM.Should().BeGreaterThan(
            atOneMinute.ProjectedPositionUncertaintyM,
            "a hull has no way of stopping, so the longer it is out of contact the further from "
            + "its last report it must be looked for");
    }

    /// <summary>An optimistic hull does not get an optimistic search radius.</summary>
    [Fact]
    public void Uncertainty_Growth_Is_Never_Below_The_Drift_The_Water_Imposes()
    {
        // The hull reports an optimistic rate; the basin's set is faster. An advisory radius may
        // not be smaller than what the current alone would carry the vessel.
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.SurfaceVessel),
            State(Surface(growthMps: 0.05)),
            environment: new Basin().Sample(default, 1.0),
            elapsedSinceContactSeconds: 100.0);

        assessment.PositionUncertaintyGrowthMps.Should().BeApproximately(0.5, 1e-6);
    }

    /// <summary>The drift term needs both a platform that drifts and water to drift on.</summary>
    [Fact]
    public void A_Rover_Acquires_No_Drift_From_Water_It_Is_Not_In()
    {
        var assessment = SafeActionPolicy.Evaluate(
            Describe(VehicleClass.AckermannRover),
            State(Ground(growthMps: 0.0)),
            environment: new Basin().Sample(default, 1.0),
            elapsedSinceContactSeconds: 100.0);

        assessment.PositionUncertaintyGrowthMps.Should().Be(
            0.0, "a platform that declares no passive drift does not acquire one by being near water");
    }

    /// <summary>The stale-position gate covers the catalog's set exactly — no wider, no narrower.</summary>
    /// <param name="vehicleClass">Class to run the whole command vocabulary against.</param>
    [Theory]
    [MemberData(nameof(SpawnableClasses))]
    public void A_Stale_Position_Blocks_Exactly_The_Commands_That_Need_One(VehicleClass vehicleClass)
    {
        var descriptor = Describe(vehicleClass);
        var state = State(DomainStateFor(vehicleClass, LinkLossBehavior.StopAndHold));

        var current = SafeActionPolicy.Evaluate(descriptor, state, environment: null, 0.0);
        var overdue = SafeActionPolicy.Evaluate(descriptor, state, environment: null, 10.0);

        overdue.EffectiveFreshness.Should().Be(DataFreshness.Stale);

        int blocked = 0;
        int permitted = 0;

        foreach (var kind in AllCommandKinds)
        {
            if (!SafeActionPolicy.Authorize(descriptor, current, kind).IsAllowed)
            {
                continue;
            }

            var decision = SafeActionPolicy.Authorize(descriptor, overdue, kind);
            CommandCatalog.TryGet(
                    AssetCommandTranslator.ToCatalogKind(kind), out var definition)
                .Should().BeTrue("a command the policy permits must have a catalog row");
            definition.Should().NotBeNull();
            bool needsPosition = definition?.RequiresFreshPosition ?? false;

            decision.IsAllowed.Should().Be(
                !needsPosition,
                "'{0}' {1} a current position, so an overdue report must {2} it",
                kind,
                needsPosition ? "needs" : "does not need",
                needsPosition ? "block" : "leave alone");

            if (needsPosition)
            {
                decision.ReasonCode.Should().Be(SafeActionReasons.PositionStale);
                blocked++;
            }
            else
            {
                permitted++;
            }
        }

        blocked.Should().BeGreaterThan(0, "the blocking half of the rule must actually be exercised");
        permitted.Should().BeGreaterThan(0, "so must the half that lets a stop through");
    }

    /// <summary>No refusal is ever prose, unexplained, or a token nothing can map.</summary>
    /// <param name="vehicleClass">Class to run every command and situation against.</param>
    [Theory]
    [MemberData(nameof(SpawnableClasses))]
    public void Every_Refusal_Carries_A_Known_Reason_Code(VehicleClass vehicleClass)
    {
        var descriptor = Describe(vehicleClass);
        var domainState = DomainStateFor(vehicleClass, DefaultBehaviourFor(vehicleClass));

        SafeActionAssessment[] situations =
        [
            SafeActionPolicy.Evaluate(descriptor, State(domainState), null, 0.0),
            SafeActionPolicy.Evaluate(descriptor, State(domainState), null, 10.0),
            SafeActionPolicy.Evaluate(
                descriptor, State(domainState, positionSigmaM: 400.0), null, 0.0),
            SafeActionPolicy.Evaluate(
                descriptor,
                State(domainState, operationalState: OperationalState.Emergency),
                null,
                0.0),
        ];

        int refusals = 0;

        foreach (var situation in situations)
        {
            foreach (var kind in AllCommandKinds)
            {
                var decision = SafeActionPolicy.Authorize(descriptor, situation, kind);

                if (decision.IsAllowed)
                {
                    decision.ReasonCode.Should().Be(SafeActionReasons.Nominal);
                    continue;
                }

                refusals++;

                decision.ReasonCode.Should().NotBe(
                    SafeActionReasons.Nominal, "a refusal must say why it refused");

                KnownReasons.Should().Contain(
                    decision.ReasonCode,
                    "'{0}' is not a token anything downstream could map", decision.ReasonCode);
            }
        }

        refusals.Should().BeGreaterThan(0, "the assertion must actually have been reached");
    }

    /// <summary>Nothing this layer knows about a stuck rover takes its recovery away.</summary>
    [Fact]
    public void An_Immobilised_Rover_Is_Never_Refused_Its_Way_Out()
    {
        // A rover that cannot reverse out of a bog has shipped here before. Nothing this layer
        // knows about a stuck vehicle may take its way out away from it.
        var descriptor = Describe(VehicleClass.AckermannRover);
        var bogged = Ground() with { IsImmobilised = true, ImmobilisationReason = "slope-exceeded" };

        var assessment = SafeActionPolicy.Evaluate(
            descriptor, State(bogged, lowEnergy: true), environment: null, 0.0);

        SafeActionPolicy.Authorize(descriptor, assessment, AssetCommandKind.Reverse)
            .IsAllowed.Should().BeTrue();

        SafeActionPolicy.Authorize(descriptor, assessment, AssetCommandKind.DriveTo)
            .IsAllowed.Should().BeTrue();

        SafeActionPolicy.Authorize(descriptor, assessment, AssetCommandKind.Stop)
            .IsAllowed.Should().BeTrue();
    }
}
