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
using System.Reflection;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// What a vessel publishes about itself, and whether two identical runs stay identical.
/// </summary>
/// <remarks>
/// The surface-domain counterpart of <see cref="GroundAssetStateTests"/>, asking the same two
/// questions of a different medium. The first is <b>honesty</b>: every field of
/// <see cref="SurfaceDomainState"/> has to describe the hull that was actually integrated, not a
/// plausible number computed beside it. That failure is silent — a speed through water published
/// where a speed over ground belonged still serialises, still animates, and still reads
/// perfectly on a display. The second is <b>replayability</b>: the same command log against the
/// same seed must produce the same states, or a recorded incident cannot be re-run and a
/// regression cannot be bisected.
/// <para>
/// The fields are <em>enumerated</em> rather than spot-checked, and the enumeration is closed
/// against reflection, so a field added to the wire model later cannot ship as a default with
/// nothing noticing. The air domain shipped with airspeed and ground speed inverted; a vessel
/// carries four such quantities that genuinely diverge — heading, course over ground, speed over
/// ground and speed through water — and three more in depth, draft and under-keel clearance, so
/// the same class of error has twice as many places to hide here.
/// </para>
/// <para>
/// The velocity assertions are anchored to the one quantity that is not a matter of convention:
/// the actual per-tick position delta. The uncertainty-growth assertions are made side by side
/// with a rover in the same world, because the per-domain divergence is the whole reason that
/// field is a rate rather than a constant — a stopped rover's stays at exactly zero while a
/// drifting hull's never settles.
/// </para>
/// <para>
/// Deterministic by construction: a fixed timestep, a fixed seed, literal timestamps, an
/// analytic basin for the arithmetic cases and a frozen wall clock for the whole-world ones. No
/// sleeps, no ambient clock, and nothing that varies with machine speed.
/// </para>
/// </remarks>
public sealed partial class SurfaceAssetStateTests
{
    // ─── The surface extension describes the vessel that was integrated ─────

    /// <summary>Every field of the surface extension is populated from the integrated hull.</summary>
    /// <remarks>
    /// The enumeration is closed: each assertion records the field it covers, and the recorded
    /// set is compared against <see cref="SurfaceDomainState"/>'s own reflected properties. A
    /// field added to the wire model without an assertion here fails this case rather than
    /// shipping as a silent default, which is exactly what a spot-check cannot do.
    /// <para>
    /// The vessel is measured mid-manoeuvre in a cross-set and a breeze, because that is the only
    /// state in which the fields that are easy to conflate actually differ: with slack water,
    /// still air and a steady course, heading and course over ground agree, the two speeds agree,
    /// and sway and yaw rate are both zero — so a swap or a stuck field would read as correct.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_Field_Of_The_Surface_Extension_Describes_The_Vessel_That_Was_Integrated()
    {
        var water = new OpenWater(BasinDepthM, SteadySetEus, SteadyBreezeEus);
        var rig = Rig(water, East);
        rig.Send(TransitTo(rig.AssetId, SternwardTarget));
        rig.Run(TurningSteps);

        var previous = rig.Step();
        var state = rig.Capture();
        var surface = SurfaceState(state);

        var track = (rig.Asset.PositionEus - previous) / (float)Dt;
        double safeMarginM = UnderKeelClearance.SafeMarginForDraft(rig.Profile.DraftM);
        double clearanceM = water.DepthM - rig.Profile.DraftM;

        // Rebuilt from the profile's own constants rather than restated, so the expectation and
        // the vessel read the same current coupling and the same leeway fraction.
        float leeway = (float)rig.Profile.LeewayFraction;
        var drift = new Vector3(
            (float)(water.CurrentEus.X * rig.Profile.PassiveCurrentCoupling) + (water.WindEus.X * leeway),
            0f,
            (float)(water.CurrentEus.Z * rig.Profile.PassiveCurrentCoupling) + (water.WindEus.Z * leeway));

        // The wave model is a pure function of position, time, heading, wind and hull, so the
        // published decoration can be recomputed exactly instead of merely bounded.
        var wave = WaveModel.Default.Sample(
            rig.Asset.PositionEus,
            rig.SimulationTimeSeconds,
            surface.HeadingRad,
            water.WindEus,
            rig.Profile);

        var asserted = new List<string>();

        void Check(string field, Action assertion)
        {
            asserted.Add(field);
            assertion();
        }

        Check(nameof(surface.Type), () =>
            surface.Type.Should().Be(SurfaceDomainState.Discriminator));

        // The bow direction, cross-checked against the attitude that is rendered from it. A
        // heading reported but no longer driving the pose would draw a hull broadside to its own
        // track, and only tying the two together catches that.
        Check(nameof(surface.HeadingRad), () =>
        {
            var bow = Vector3.Transform(Vector3.UnitX, state.Pose.Orientation);
            CoordinateFrames.BearingFromEusVector(bow).Should()
                .BeApproximately(surface.HeadingRad, 1e-3);
            surface.HeadingRad.Should().NotBe(East, "the vessel has been wearing round");
        });

        Check(nameof(surface.CourseOverGroundRad), () =>
        {
            surface.CourseOverGroundRad.Should().BeApproximately(
                CoordinateFrames.BearingFromEusVector(track, surface.HeadingRad), 1e-3);

            double crab = Math.Abs(CoordinateFrames.NormalizeAngle(
                surface.CourseOverGroundRad - surface.HeadingRad));
            Math.Min(crab, Math.Tau - crab).Should().BeGreaterThan(
                0.01, "a cross-set puts the track off the bow, which is why these are two fields");
        });

        Check(nameof(surface.SpeedOverGroundMps), () =>
            surface.SpeedOverGroundMps.Should().BeApproximately(
                CoordinateFrames.SpeedOverGround(track), VelocityToleranceMps));

        Check(nameof(surface.SpeedThroughWaterMps), () =>
        {
            surface.SpeedThroughWaterMps.Should().BeApproximately(
                Math.Sqrt((surface.SurgeMps * surface.SurgeMps) + (surface.SwayMps * surface.SwayMps)),
                1e-9);
            surface.SpeedThroughWaterMps.Should().NotBeApproximately(
                surface.SpeedOverGroundMps, 0.01,
                "a log reading and a ground track are different numbers in a tideway");
        });

        Check(nameof(surface.SurgeMps), () => surface.SurgeMps.Should().BePositive());

        Check(nameof(surface.SwayMps), () => surface.SwayMps.Should().NotBe(
            0.0, "a hull crabs through a turn and in a beam wind; zero here means it is not wired"));

        Check(nameof(surface.YawRateRadPerSec), () =>
        {
            surface.YawRateRadPerSec.Should().NotBe(0.0, "the vessel is still under helm");
            Math.Abs(surface.YawRateRadPerSec).Should()
                .BeLessThanOrEqualTo(rig.Profile.MaxYawRateRadPerSec);
            PublishedYawRateRadPerSec(state).Should()
                .BeApproximately(surface.YawRateRadPerSec, 1e-6);
        });

        // The mean surface, and the height the hull is published at. Wave heave is decoration and
        // must appear in neither — a pose that rode the swell would ground a hull on a decoration
        // the moment anything differenced it against the bed.
        Check(nameof(surface.WaterSurfaceElevationM), () =>
        {
            surface.WaterSurfaceElevationM.Should().BeApproximately(water.SeaLevelM, 1e-9);
            state.Pose.Position.Y.Should().Be((float)water.SeaLevelM);
        });

        Check(nameof(surface.WaterDepthM), () =>
            surface.WaterDepthM.Should().BeApproximately(water.DepthM, 1e-9));

        Check(nameof(surface.DraftM), () =>
            surface.DraftM.Should().BeApproximately(rig.Profile.DraftM, 1e-9));

        Check(nameof(surface.UnderKeelClearanceM), () =>
            surface.UnderKeelClearanceM.Should().BeApproximately(
                clearanceM, 1e-9,
                "depth, draft and clearance are three quantities and the third is the difference "
                + "of the first two"));

        // Read off the same band the derating curve is defined on, never a second comparison
        // against the depth: two copies of "is there enough water here" is how a flag comes to
        // contradict the number published beside it.
        Check(nameof(surface.HasUnsafeUnderKeelClearance), () =>
            surface.HasUnsafeUnderKeelClearance.Should().Be(
                UnderKeelClearance.Classify(clearanceM, safeMarginM)
                    is UnderKeelClearanceClass.Critical or UnderKeelClearanceClass.Aground));

        Check(nameof(surface.CurrentSpeedMps), () =>
            surface.CurrentSpeedMps.Should().BeApproximately(
                CoordinateFrames.SpeedOverGround(water.CurrentEus), DerivedToleranceMps));

        Check(nameof(surface.CurrentDirectionRad), () =>
            surface.CurrentDirectionRad.Should().BeApproximately(
                CoordinateFrames.BearingFromEusVector(water.CurrentEus, surface.HeadingRad),
                AngleToleranceRad));

        Check(nameof(surface.WindSpeedMps), () =>
            surface.WindSpeedMps.Should().BeApproximately(
                CoordinateFrames.SpeedOverGround(water.WindEus), DerivedToleranceMps));

        Check(nameof(surface.WindDirectionRad), () =>
            surface.WindDirectionRad.Should().BeApproximately(
                CoordinateFrames.BearingFromEusVector(water.WindEus, surface.HeadingRad),
                AngleToleranceRad));

        Check(nameof(surface.IsInsideWaterMask), () =>
            surface.IsInsideWaterMask.Should().BeTrue());

        Check(nameof(surface.LinkLossBehavior), () =>
        {
            surface.LinkLossBehavior.Should().Be(rig.Asset.Safety.LinkLoss);
            surface.LinkLossBehavior.Should().Be(
                LinkLossBehavior.DriftAndAlert,
                "a single-screw hull that loses its link has no way of holding a position");
        });

        // The environment is published as the set and the wind; what the hull makes of them is a
        // third number, and the two are deliberately not the same.
        Check(nameof(surface.PositionUncertaintyGrowthMps), () =>
        {
            surface.PositionUncertaintyGrowthMps.Should().BeApproximately(
                CoordinateFrames.SpeedOverGround(drift), DerivedToleranceMps);
            surface.PositionUncertaintyGrowthMps.Should().BePositive();
        });

        Check(nameof(surface.StationKeep), () => surface.StationKeep.Should().BeNull(
            "this hull cannot hold a station, and reporting a hold it is not keeping would be "
            + "worse than reporting none"));

        Check(nameof(surface.HeaveM), () =>
            surface.HeaveM.Should().BeApproximately(wave.HeaveM, 1e-9));

        Check(nameof(surface.RollRad), () =>
            surface.RollRad.Should().BeApproximately(wave.RollRad, 1e-9));

        Check(nameof(surface.PitchRad), () =>
            surface.PitchRad.Should().BeApproximately(wave.PitchRad, 1e-9));

        typeof(SurfaceDomainState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().BeEquivalentTo(
                asserted,
                "every field of the surface extension must be asserted here, so one added later "
                + "cannot ship as a silent default");
    }

    /// <summary>A hull that can hold a station publishes every field of the hold it is keeping.</summary>
    /// <remarks>
    /// Run against a hull built for this case rather than the shipped workboat, which has one
    /// screw, one rudder, no <see cref="AssetCapability.StationKeep"/> and honestly refuses
    /// <c>stationKeep</c>. That refusal is pinned elsewhere and must stay. What is exercised here
    /// is the second gate — the one that fires when a descriptor genuinely declares the
    /// capability — and the projection behind it, which is otherwise unreachable and would ship
    /// untested.
    /// </remarks>
    [Fact]
    public void A_Hull_That_Can_Hold_Station_Publishes_Every_Field_Of_Its_Hold()
    {
        var water = new OpenWater(BasinDepthM, SteadySetEus, SteadyBreezeEus);
        var thrusters = SurfaceProfile.SurfaceVessel with
        {
            CanStationKeep = true,
            StationKeepPowerW = 450.0,
        };

        var rig = Rig(water, North, thrusters, AssetCapability.StationKeep);
        var station = rig.Asset.PositionEus;

        rig.Send(Command(rig.AssetId, AssetCommandKind.StationKeep));
        rig.Run(TurningSteps);

        var surface = SurfaceState(rig.Capture());
        var hold = surface.StationKeep.Should().BeOfType<StationKeepState>().Subject;

        surface.LinkLossBehavior.Should().Be(
            LinkLossBehavior.HoldPosition,
            "policy follows the propulsion arrangement rather than the domain");

        var asserted = new List<string>();

        void Check(string field, Action assertion)
        {
            asserted.Add(field);
            assertion();
        }

        Check(nameof(hold.IsEngaged), () => hold.IsEngaged.Should().BeTrue());

        Check(nameof(hold.Target), () =>
        {
            var target = hold.Target.Should().BeOfType<FramedPose>().Subject;
            target.Frame.Should().Be(CoordinateFrame.LocalEus, "a bare position is not a station");
            target.Position.X.Should().BeApproximately(station.X, 1e-3f);
            target.Position.Z.Should().BeApproximately(station.Z, 1e-3f);
        });

        Check(nameof(hold.ToleranceRadiusM), () => hold.ToleranceRadiusM.Should().BeApproximately(
            Math.Max(StationKeepGoal.MinToleranceRadiusM, rig.Profile.LengthM), 1e-9,
            "a station is one overall length, derived from the hull rather than picked"));

        Check(nameof(hold.HeadingPolicy), () => hold.HeadingPolicy.Should().Be(
            StationKeepHeadingPolicy.MinimumPower, "no heading was commanded with the hold"));

        Check(nameof(hold.HeadingSetpointRad), () =>
        {
            hold.HeadingSetpointRad.Should().NotBeNull(
                "the heading the law is steering to is published under every policy");
            double.IsFinite(hold.HeadingSetpointRad ?? double.NaN).Should().BeTrue();
        });

        // Tolerated to a tenth of a metre rather than compared exactly: the error the law
        // reported was measured at the top of the last step, before that step's integration, so
        // the hull has moved one step's worth since. That gap is real and small, and pinning it
        // any tighter would be pinning the order of operations rather than the quantity.
        Check(nameof(hold.PositionErrorM), () =>
        {
            var here = rig.Asset.PositionEus;
            double error = Math.Sqrt(
                ((here.X - station.X) * (here.X - station.X))
                + ((here.Z - station.Z) * (here.Z - station.Z)));

            hold.PositionErrorM.Should().NotBeNull();
            (hold.PositionErrorM ?? double.NaN).Should().BeApproximately(error, 0.1);
        });

        // The flag and the reason are two renderings of one phase, so they can never disagree: a
        // degraded hold always names why, and a nominal one never invents a reason.
        Check(nameof(hold.IsDegraded), () =>
        {
            hold.IsDegraded.Should().Be(hold.DegradedReason is not null);
            hold.IsDegraded.Should().BeFalse(
                "the set and the breeze here are well inside the effort the hold may spend");
        });

        Check(nameof(hold.DegradedReason), () => hold.DegradedReason.Should().BeNull());

        typeof(StationKeepState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Should().BeEquivalentTo(
                asserted,
                "every field of the hold must be asserted here for the same reason the domain "
                + "extension's are");
    }
}
