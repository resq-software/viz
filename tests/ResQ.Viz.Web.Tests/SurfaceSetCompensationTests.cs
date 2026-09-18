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
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The transit law against a set: that it takes the drift out of the speed command, and that it
/// refuses a leg it cannot make good instead of holding station on it.
/// </summary>
/// <remarks>
/// The defect these pin is a frame confusion with a very specific signature. The coast cap is a
/// closure rate <em>over the ground</em> — the fastest approach from which a first-order surge
/// can still stop inside the arrival tolerance — but <see cref="SurfaceSetpoint.SurgeMps"/> is
/// water-relative, and the drift is added to it downstream by the integrator. Commanding the
/// closure rate as surge therefore under-delivers by the set's along-track component, and the
/// vessel settles where the two balance:
/// <code>
///     (d - tolerance) / tau_u = setAlongTrack     =&gt;     d = tolerance + tau_u * setAlongTrack
/// </code>
/// At that range the distance stops falling, so the cap stops falling, so the command never
/// changes again. The vessel holds station short of its target with way on, mode
/// <c>transit</c> and execution <c>Executing</c>, and raises nothing — indistinguishable to an
/// operator from a long passage.
/// <para>
/// These drive <see cref="SurfaceNavigator.Sample"/> directly rather than through an asset. The
/// equilibrium is a property of the control law alone, and asserting it against the law means a
/// failure names the law rather than whatever integrated it.
/// </para>
/// </remarks>
public sealed class SurfaceSetCompensationTests
{
    /// <summary>The shipped surface vessel: 6 m/s ahead, tau_u 6 s, 13 m long.</summary>
    private static SurfaceProfile Profile => SurfaceProfile.SurfaceVessel;

    /// <summary>Heading and target both due north, so the set lies square along the track.</summary>
    private const double North = 0.0;

    /// <summary>A vessel at the origin heading north, making its cruise speed through the water.</summary>
    private static SurfaceMotionState UnderWay(double northM) => new(
        EastM: 0.0,
        SouthM: -northM,
        HeadingRad: North,
        SurgeMps: Profile.MaxSpeedMps,
        SwayMps: 0.0,
        YawRateRadPerSec: 0.0);

    /// <summary>Guidance input carrying a scene-frame drift and nothing else of consequence.</summary>
    /// <remarks>
    /// The velocities block is only read by the course-hold law, so a transit case can leave it
    /// consistent with the drift and ignore it. The ceiling is the profile's own maximum: these
    /// cases are about the set, not about water that imposes a limit.
    /// </remarks>
    private static SurfaceGuidanceInput Input(Vector3 driftEus) => new(
        DeltaSeconds: 1.0 / 60.0,
        SpeedCeilingMps: Profile.MaxSpeedMps,
        Velocities: new SurfaceVelocities(
            GroundVelocityEus: driftEus,
            WaterRelativeVelocityEus: Vector3.Zero,
            DriftVelocityEus: driftEus,
            HeadingRad: North,
            CourseOverGroundRad: North,
            SpeedOverGroundMps: 0.0,
            SpeedThroughWaterMps: 0.0),
        PassiveDriftEus: driftEus,
        WindEus: Vector3.Zero);

    /// <summary>A northward set of <paramref name="speedMps"/>, in the scene frame (−Z is north).</summary>
    private static Vector3 SettingNorth(double speedMps) => new(0f, 0f, (float)-speedMps);

    // ─── The compensation ───────────────────────────────────────────────────

    /// <summary>A foul set is added to the speed command, not left for the hull to lose.</summary>
    /// <remarks>
    /// The arithmetic is exact and worth stating rather than approximating: at 30 m to run with
    /// a 13 m arrival tolerance and tau_u of 6 s, the ground closure the cap permits is
    /// (30 − 13) / 6 = 2.833 m/s. Against a 1 m/s foul set the hull must make 3.833 m/s through
    /// the water to close at that rate. Commanding 2.833 — which is what shipped — closes at
    /// 1.833 and the range never reaches tolerance.
    /// </remarks>
    [Fact]
    public void A_Foul_Set_Is_Added_To_The_Commanded_Surge()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.TransitTo(new Vector3(0f, 0f, -30f));

        // Setting south — against a vessel bound north — is +Z in the scene frame.
        var outcome = navigator.Sample(UnderWay(northM: 0.0), Input(SettingNorth(-1.0)));

        double coast = (30.0 - navigator.ArrivalToleranceM) / Profile.SurgeTimeConstantSec;

        outcome.Setpoint.SurgeMps.Should().BeApproximately(
            coast + 1.0,
            0.01,
            "the cap is a closure rate over the ground and the surge is water-relative, so the "
            + "set has to be put back in or the hull makes good exactly that much less");
    }

    /// <summary>A fair set is taken back out of it.</summary>
    /// <remarks>
    /// The same correction with the opposite sign, and the half that stops the fix from being a
    /// blanket speed increase: a set carrying the vessel toward its target does part of the work,
    /// and commanding the full cap on top of it would overshoot the arrival tolerance at a
    /// closure the hull cannot stop from.
    /// </remarks>
    [Fact]
    public void A_Fair_Set_Is_Taken_Out_Of_The_Commanded_Surge()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.TransitTo(new Vector3(0f, 0f, -30f));

        var outcome = navigator.Sample(UnderWay(northM: 0.0), Input(SettingNorth(1.0)));

        double coast = (30.0 - navigator.ArrivalToleranceM) / Profile.SurgeTimeConstantSec;

        outcome.Setpoint.SurgeMps.Should().BeApproximately(coast - 1.0, 0.01);
    }

    /// <summary>Slack water is unchanged: the correction is zero, not merely small.</summary>
    [Fact]
    public void Slack_Water_Commands_Exactly_The_Coast_Cap()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.TransitTo(new Vector3(0f, 0f, -30f));

        var outcome = navigator.Sample(UnderWay(northM: 0.0), Input(Vector3.Zero));

        outcome.Setpoint.SurgeMps.Should().BeApproximately(
            (30.0 - navigator.ArrivalToleranceM) / Profile.SurgeTimeConstantSec, 1e-9);
    }

    /// <summary>A beam set does not change the speed command at all.</summary>
    /// <remarks>
    /// Only the along-track component is unaccounted for. The cross-track component is already
    /// absorbed by the heading law, which closes on the live bearing to the target every step, so
    /// taking it out of the speed as well would double-count it and slow every crabbing passage.
    /// </remarks>
    [Fact]
    public void A_Beam_Set_Leaves_The_Speed_Command_Alone()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.TransitTo(new Vector3(0f, 0f, -30f));

        var outcome = navigator.Sample(UnderWay(northM: 0.0), Input(new Vector3(2f, 0f, 0f)));

        outcome.Setpoint.SurgeMps.Should().BeApproximately(
            (30.0 - navigator.ArrivalToleranceM) / Profile.SurgeTimeConstantSec, 1e-9);
    }

    // ─── The equilibrium that used to be permanent ──────────────────────────

    /// <summary>
    /// At the range the uncompensated law stalled at, the command is now enough to keep closing.
    /// </summary>
    /// <remarks>
    /// This is the defect stated as a number. The old equilibrium sits at
    /// <c>tolerance + tau_u * set</c> — 13 + 6 × 0.5 = 16 m for this hull under a half-knot-ish
    /// foul set, which is the range the audit measured a vessel frozen at. The test asserts the
    /// hull is asked for more than the set, because any margin at all means the range keeps
    /// falling and the arrival guard is reachable; asserting a specific surge here would pin the
    /// gain rather than the property that matters.
    /// </remarks>
    [Fact]
    public void At_The_Old_Stall_Range_The_Vessel_Is_Still_Told_To_Close()
    {
        var navigator = new SurfaceNavigator(Profile);
        double set = 0.5;
        double stallRange = navigator.ArrivalToleranceM + (Profile.SurgeTimeConstantSec * set);

        navigator.TransitTo(new Vector3(0f, 0f, (float)-stallRange));

        var outcome = navigator.Sample(UnderWay(northM: 0.0), Input(SettingNorth(-set)));

        outcome.Setpoint.SurgeMps.Should().BeGreaterThan(
            set + 0.01,
            "at the old equilibrium the commanded surge equalled the set exactly, so closure was "
            + "zero and the range never moved again");
        outcome.Mode.Should().Be(SurfaceGuidanceMode.Transiting);
    }

    // ─── The set that cannot be stemmed ─────────────────────────────────────

    /// <summary>A set stronger than the hull ends the passage instead of holding station on it.</summary>
    /// <remarks>
    /// Compensation alone would move the silent failure rather than remove it: once the foul set
    /// exceeds the speed the hull can make, no command closes the range, and the uncompensated
    /// behaviour — hover forever, report Executing — returns unchanged. The refusal is what makes
    /// the fix complete. It is deliberately judged on the <em>best achievable</em> closure at full
    /// throttle rather than on the closure being made, so a vessel merely accelerating onto its
    /// leg is never mistaken for one that is stuck.
    /// </remarks>
    [Fact]
    public void A_Set_The_Hull_Cannot_Stem_Blocks_The_Passage()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.TransitTo(new Vector3(0f, 0f, -400f));

        var outcome = navigator.Sample(
            UnderWay(northM: 0.0), Input(SettingNorth(-(Profile.MaxSpeedMps + 1.0))));

        outcome.HasBecomeBlocked.Should().BeTrue();
        outcome.BlockingReason.Should().Be(WaterBlockReason.SetExceedsPropulsion);
        outcome.Mode.Should().Be(SurfaceGuidanceMode.Blocked);
        outcome.Setpoint.SurgeMps.Should().Be(0.0, "a passage that cannot be made is not attempted");
    }

    /// <summary>A set the hull can only just stem is a slow passage, not a refusal.</summary>
    /// <remarks>
    /// The boundary matters as much as the refusal: a threshold set too high refuses legs that
    /// are merely slow, which is the same silent-failure trade in the opposite direction — except
    /// that the operator loses a passage the vessel could have made.
    /// </remarks>
    [Fact]
    public void A_Set_The_Hull_Can_Just_Stem_Is_Not_Blocked()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.TransitTo(new Vector3(0f, 0f, -400f));

        var outcome = navigator.Sample(
            UnderWay(northM: 0.0), Input(SettingNorth(-(Profile.MaxSpeedMps - 0.5))));

        outcome.HasBecomeBlocked.Should().BeFalse();
        outcome.Mode.Should().Be(SurfaceGuidanceMode.Transiting);
        outcome.Setpoint.SurgeMps.Should().BeApproximately(
            Profile.MaxSpeedMps, 0.01, "full throttle, because the cap far exceeds it at 400 m");
    }

    /// <summary>A fair set never blocks, however strong, and far out it still costs full throttle.</summary>
    /// <remarks>
    /// The refusal is tested on the best achievable closure, which a fair set can only increase,
    /// so no fair set can trip it. The second assertion is the one worth stating: at 400 m the
    /// coast cap is 64 m/s, far above anything the hull can make, so even an 11 m/s fair set
    /// leaves the command at full ahead. A set only displaces throttle once the cap has come down
    /// near it — which is the next case.
    /// </remarks>
    [Fact]
    public void A_Fair_Set_Stronger_Than_The_Hull_Does_Not_Block()
    {
        var navigator = new SurfaceNavigator(Profile);
        navigator.TransitTo(new Vector3(0f, 0f, -400f));

        var outcome = navigator.Sample(
            UnderWay(northM: 0.0), Input(SettingNorth(Profile.MaxSpeedMps + 5.0)));

        outcome.HasBecomeBlocked.Should().BeFalse();
        outcome.Setpoint.SurgeMps.Should().BeApproximately(Profile.MaxSpeedMps, 0.01);
    }

    /// <summary>Close in, a fair set that exceeds the coast cap stops the propeller outright.</summary>
    /// <remarks>
    /// The arrival case, and the reason the correction has to be signed rather than a speed
    /// bonus. The range is picked so the cap permits exactly 1 m/s of closure; a fair set of
    /// 2 m/s is already carrying the hull in twice that fast. Adding throttle on top would arrive
    /// at a closure the hull cannot stop from and overrun the tolerance, so the command goes to
    /// zero and the set does the rest.
    /// </remarks>
    [Fact]
    public void A_Fair_Set_Beyond_The_Coast_Cap_Cuts_The_Throttle()
    {
        var navigator = new SurfaceNavigator(Profile);
        double oneMetrePerSecondOut = navigator.ArrivalToleranceM + Profile.SurgeTimeConstantSec;

        navigator.TransitTo(new Vector3(0f, 0f, (float)-oneMetrePerSecondOut));

        var outcome = navigator.Sample(UnderWay(northM: 0.0), Input(SettingNorth(2.0)));

        outcome.HasBecomeBlocked.Should().BeFalse();
        outcome.Setpoint.SurgeMps.Should().Be(
            0.0, "the set alone already closes faster than the hull could stop from");
    }
}
