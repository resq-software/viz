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

using System.Numerics;
using FluentAssertions;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>The answer depends on the declared inputs, and on nothing else.</summary>
/// <remarks>
/// Three ways that promise can break, and one case for each. Something out of band changes the
/// answer — the sea state is the candidate here, since a wave model that quietly reached the
/// navigation solution would be very hard to see. Something malformed changes it into garbage —
/// a non-finite disturbance must be absorbed and a non-finite state must fail at the boundary
/// rather than poisoning every later frame. Or nothing changes at all and the answer moves
/// anyway, which is what a hidden clock, a cached value or a state-dependent iteration count
/// look like from outside.
/// </remarks>
public sealed partial class SurfaceDynamicsTests
{
    // ─── Waves are decoration, and stay decoration ──────────────────────────

    /// <summary>
    /// A rough sea moves the hull in heave, roll and pitch and leaves its track over the ground
    /// bit-for-bit identical to the same run in a flat calm.
    /// </summary>
    /// <remarks>
    /// Wave motion is visual only in this pass, which means the navigation solution is computed
    /// as though the surface were flat. The structural guarantee is that
    /// <see cref="ISurfaceDynamics.Step"/> has no wave parameter at all, so there is no channel
    /// through which a sea state could reach it; this case pins the behaviour that guarantee is
    /// there to produce, and it fails the moment anyone adds one.
    /// <para>
    /// The wind that raises the sea is deliberately <em>not</em> passed to the dynamics. Wind is
    /// a genuine disturbance — it makes leeway, and leeway does move the vessel — so feeding it
    /// in would make this case about the wrong thing entirely.
    /// </para>
    /// </remarks>
    [Fact]
    public void Waves_Move_The_Hull_But_Not_The_Navigation_Solution()
    {
        const int Steps = 3600;

        var profile = SurfaceProfile.SurfaceVessel;
        var model = new SurfaceDynamics(profile);
        var sea = WaveModel.Default;

        var windEus = new Vector3(9f, 0f, 0f);
        var conditions = new SurfaceConditions(
            new Vector3(0.4f, 0f, -0.2f), Vector3.Zero, double.PositiveInfinity);
        var setpoint = new SurfaceSetpoint(3.0, 0.05);

        sea.SignificantHeightFor(windEus).Should().BeGreaterThan(
            sea.SignificantHeightFor(Vector3.Zero), "this wind must actually raise a sea");

        var flatCalm = SurfaceMotionState.DeadInTheWater(0.0, 0.0, 0.0);
        var rough = flatCalm;
        double maxHeaveM = 0.0;
        double maxRollRad = 0.0;
        double maxPitchRad = 0.0;

        for (int i = 0; i < Steps; i++)
        {
            flatCalm = model.Step(flatCalm, setpoint, Dt, conditions);
            rough = model.Step(rough, setpoint, Dt, conditions);

            var motion = sea.Sample(
                new Vector3((float)rough.EastM, 0f, (float)rough.SouthM),
                i * Dt,
                rough.HeadingRad,
                windEus,
                profile);

            double.IsFinite(motion.HeaveM).Should().BeTrue();
            Math.Abs(motion.RollRad).Should().BeLessThanOrEqualTo(0.35);
            Math.Abs(motion.PitchRad).Should().BeLessThanOrEqualTo(0.35);

            maxHeaveM = Math.Max(maxHeaveM, Math.Abs(motion.HeaveM));
            maxRollRad = Math.Max(maxRollRad, Math.Abs(motion.RollRad));
            maxPitchRad = Math.Max(maxPitchRad, Math.Abs(motion.PitchRad));
        }

        maxHeaveM.Should().BeGreaterThan(0.1, "an assertion about a sea that never moved proves nothing");
        maxRollRad.Should().BeGreaterThan(0.005);
        maxPitchRad.Should().BeGreaterThan(0.005);

        Bits(rough).Should().Equal(Bits(flatCalm),
            "the sea state reached the display and nothing else");

        // Nor does the heave hide inside the published position: the vertical component is the
        // mean water surface it was handed, exactly.
        rough.ToPositionEus(3.0).Y.Should().Be(3.0f);
    }

    // ─── Nothing finite in produces anything non-finite out ─────────────────

    /// <summary>A timestep that cannot produce a meaningful integration is refused, not absorbed.</summary>
    /// <param name="deltaSeconds">Timestep offered to the model.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-Dt)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Step_Rejects_A_Timestep_That_Is_Not_Positive_And_Finite(double deltaSeconds)
    {
        var model = new SurfaceDynamics(SurfaceProfile.SurfaceVessel);

        Action act = () => model.Step(
            SurfaceMotionState.DeadInTheWater(0.0, 0.0, 0.0),
            SurfaceSetpoint.Drift,
            deltaSeconds,
            SurfaceConditions.Calm);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("deltaSeconds");
    }

    /// <summary>A non-finite state or setpoint fails at the boundary rather than poisoning the pose.</summary>
    [Fact]
    public void Step_And_Resolve_Reject_A_Non_Finite_State_Or_Setpoint()
    {
        var model = new SurfaceDynamics(SurfaceProfile.SurfaceVessel);
        var badState = new SurfaceMotionState(double.NaN, 0.0, 0.0, 0.0, 0.0, 0.0);

        Action steppedBadState = () => model.Step(
            badState, SurfaceSetpoint.Drift, Dt, SurfaceConditions.Calm);
        steppedBadState.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("state");

        Action resolvedBadState = () => model.Resolve(badState, SurfaceConditions.Calm);
        resolvedBadState.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("state");

        Action steppedBadSetpoint = () => model.Step(
            SurfaceMotionState.DeadInTheWater(0.0, 0.0, 0.0),
            new SurfaceSetpoint(double.PositiveInfinity),
            Dt,
            SurfaceConditions.Calm);
        steppedBadSetpoint.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("setpoint");
    }

    /// <summary>
    /// A non-finite current, wind or ceiling becalms the vessel rather than faulting the whole
    /// asset pass.
    /// </summary>
    /// <remarks>
    /// The asymmetry with the case above is deliberate and worth stating: a bad <em>state</em>
    /// can only come from corruption on our side and must fail loudly, while a bad
    /// <em>environment sample</em> is a momentary glitch in a procedural field and should cost
    /// one frame of disturbance, not the whole vessel.
    /// </remarks>
    [Fact]
    public void Non_Finite_Conditions_Becalm_The_Vessel_Rather_Than_Faulting_It()
    {
        const int Steps = 1200;

        var model = new SurfaceDynamics(SurfaceProfile.SurfaceVessel);
        var start = SurfaceMotionState.DeadInTheWater(5.0, -2.0, 0.9);
        var setpoint = new SurfaceSetpoint(2.0);

        var poisoned = new SurfaceConditions(
            new Vector3(float.NaN, 0f, 1f),
            new Vector3(0f, 0f, float.PositiveInfinity),
            double.PositiveInfinity);

        Bits(Run(model, start, setpoint, Steps, poisoned))
            .Should().Equal(Bits(Run(model, start, setpoint, Steps, SurfaceConditions.Calm)),
                "an unusable disturbance is replaced by no disturbance at all");

        // A non-finite ceiling is the one figure that cannot be replaced by "none": a ceiling of
        // infinity would let a corrupt sample raise a limit rather than lower one, so it becomes
        // a ceiling of zero and the vessel simply makes no way.
        Run(model, start, setpoint, Steps,
            new SurfaceConditions(Vector3.Zero, Vector3.Zero, double.NaN))
            .SurgeMps.Should().Be(0.0);
    }

    /// <summary>
    /// Every combination of extreme-but-finite setpoint and degenerate conditions stays finite
    /// across a long run, with the heading always normalised.
    /// </summary>
    [Fact]
    public void Extreme_Finite_Inputs_Never_Produce_A_Non_Finite_State()
    {
        SurfaceSetpoint[] setpoints =
        [
            SurfaceSetpoint.Drift,
            new SurfaceSetpoint(1e9, 1e9),
            new SurfaceSetpoint(-1e9, -1e9),
            new SurfaceSetpoint(double.MaxValue, double.MaxValue),
            new SurfaceSetpoint(double.Epsilon, -double.Epsilon),
        ];

        SurfaceConditions[] conditions =
        [
            SurfaceConditions.Calm,
            new SurfaceConditions(new Vector3(float.MaxValue, 0f, float.MinValue), Vector3.Zero, 0.0),
            new SurfaceConditions(new Vector3(float.NaN, 0f, 0f), new Vector3(0f, 0f, float.NaN), -5.0),
            new SurfaceConditions(new Vector3(3f, 0f, -4f), new Vector3(40f, 0f, 30f), double.PositiveInfinity),
        ];

        var model = new SurfaceDynamics(SurfaceProfile.SurfaceVessel);

        for (int s = 0; s < setpoints.Length; s++)
        {
            for (int c = 0; c < conditions.Length; c++)
            {
                var state = Run(
                    model, SurfaceMotionState.DeadInTheWater(0.0, 0.0, 1.0), setpoints[s], 720, conditions[c]);

                // Indices rather than the values themselves: FluentAssertions treats a reason as
                // a format string, and a record's ToString is full of braces.
                string because = $"setpoint {s}, conditions {c}";

                double.IsFinite(state.EastM).Should().BeTrue(because);
                double.IsFinite(state.SouthM).Should().BeTrue(because);
                double.IsFinite(state.HeadingRad).Should().BeTrue(because);
                double.IsFinite(state.SurgeMps).Should().BeTrue(because);
                double.IsFinite(state.SwayMps).Should().BeTrue(because);
                double.IsFinite(state.YawRateRadPerSec).Should().BeTrue(because);
                state.HeadingRad.Should().BeInRange(0.0, Math.Tau, because);
            }
        }
    }

    /// <summary>A becalmed hull left to itself in slack water holds its pose to the last bit.</summary>
    /// <remarks>
    /// Compared as bits rather than with a tolerance: a micrometre a step is invisible to any
    /// epsilon and ruinous over an hour, and this is the one condition where the model owes an
    /// exact zero rather than a small residual.
    /// </remarks>
    [Fact]
    public void A_Becalmed_Hull_Holding_Drift_Does_Not_Move()
    {
        var model = new SurfaceDynamics(SurfaceProfile.SurfaceVessel);
        var start = SurfaceMotionState.DeadInTheWater(1234.5, -678.25, 2.5);

        Bits(Run(model, start, SurfaceSetpoint.Drift, 3600)).Should().Equal(Bits(start));
    }

    // ─── Determinism ────────────────────────────────────────────────────────

    /// <summary>
    /// The same inputs stepped twice, through independently constructed models, produce
    /// bit-identical state.
    /// </summary>
    /// <remarks>
    /// Compared as raw bits rather than as doubles, because <c>-0.0 == 0.0</c> and NaN equals
    /// nothing: a model that had started to depend on evaluation order could disagree about the
    /// sign of a zero and never fail a numeric equality assertion. Two instances rather than one
    /// stepped twice, so a model that quietly cached anything between steps fails too.
    /// </remarks>
    [Fact]
    public void Stepping_The_Same_Inputs_Twice_Produces_Bit_Identical_State()
    {
        var model = new SurfaceDynamics(SurfaceProfile.SurfaceVessel);
        var replay = new SurfaceDynamics(SurfaceProfile.SurfaceVessel);

        Bits(RunSchedule(model)).Should().Equal(Bits(RunSchedule(replay)));
    }

    /// <summary>The factory builds the displacement model, and the model refuses a profile it cannot integrate.</summary>
    /// <remarks>
    /// Both time constants and the turning circle are divisors, and a non-positive one is the
    /// only way this model can produce a non-finite pose. Checking them once at construction is
    /// what lets every per-step path assume they are sound.
    /// </remarks>
    [Fact]
    public void The_Factory_Builds_The_Displacement_Model_And_Refuses_An_Unusable_Profile()
    {
        SurfaceDynamics.For(SurfaceProfile.SurfaceVessel).Should().BeOfType<SurfaceDynamics>();
        SurfaceDynamics.For(SurfaceProfile.Sailboat).ModelKey
            .Should().Be(SurfaceProfile.SailingHullModelKey,
                "a sailing hull under bare poles obeys the same equations, but it is not the same profile");

        Action zeroSurgeTimeConstant = () => _ = new SurfaceDynamics(
            SurfaceProfile.SurfaceVessel with { SurgeTimeConstantSec = 0.0 });
        zeroSurgeTimeConstant.Should().Throw<ArgumentException>();

        Action zeroTurningCircle = () => _ = new SurfaceDynamics(
            SurfaceProfile.SurfaceVessel with { MinTurnRadiusM = 0.0 });
        zeroTurningCircle.Should().Throw<ArgumentException>();
    }
}
