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
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>The sea is a field, and a field does not jump because one hull felt a gust.</summary>
/// <remarks>
/// <see cref="WaveModel"/> is handed the wind sampled <em>at the vessel</em>, and under
/// <see cref="WeatherMode.Turbulent"/> that wind is hash noise quantised to one-second buckets:
/// it steps, hard, about once a second. Everything here exists because an earlier revision fed
/// that sample into the wave <em>geometry</em> — the peak wavelength and the wave bearing — and
/// then measured phase from the scene origin. A hull a kilometre out has a phase of a kilometre
/// over a few tens of metres, so a gust moved it by many multiples of <c>2*pi</c> and its heave,
/// roll and pitch teleported once a second.
/// <para>
/// The property that makes that impossible is that the field's geometry is fixed and the wind
/// enters only as one scalar sea state multiplying the whole field. So heave is <c>Hs</c> times a
/// function of position and time alone, and the tangents of roll and pitch likewise. Dividing the
/// motion back out by <c>Hs</c> therefore recovers a quantity that <em>cannot</em> depend on the
/// wind at all, and bounding its per-step change is a far sharper statement than bounding the
/// motion itself: it holds whatever the gusts do.
/// </para>
/// </remarks>
public sealed class WaveModelTests
{
    /// <summary>Frame interval the asset world steps at, in seconds.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>A minute of stepping — long enough for many gust buckets to come and go.</summary>
    private const int Steps = 3600;

    /// <summary>Vessel east coordinate, far enough out for a phase error to be measured in cycles.</summary>
    private const double FarEastM = 1200.0;

    /// <summary>Vessel south coordinate, likewise.</summary>
    private const double FarSouthM = -900.0;

    /// <summary>The hull every case rides on.</summary>
    private static readonly SurfaceProfile Hull = SurfaceProfile.SurfaceVessel;

    // ─── Continuity in time under a wind that is not continuous ─────────────

    /// <summary>
    /// A vessel a kilometre and a half from the origin, under turbulent weather for a simulated
    /// minute, never sees its heave, roll or pitch jump: every per-step change is bounded, and
    /// the sea-state-normalised field is continuous to within a step of wave phase.
    /// </summary>
    /// <remarks>
    /// The three normalised bounds are the load-bearing ones. <c>heave / Hs</c> and
    /// <c>tan(roll) / Hs</c> are pure functions of position and simulation time, so in one step
    /// they can only move by one step of phase — about a hundredth — no matter how violently the
    /// sampled wind moved. Against the previous model those same quantities swung by their full
    /// range roughly once per simulated second.
    /// <para>
    /// The absolute bounds are stated in terms of the sea state's own change, because that is the
    /// one channel the wind is allowed: a gust may make the sea taller, and a taller sea heaves a
    /// hull further. What it may not do is move the hull to a different part of the wave.
    /// </para>
    /// </remarks>
    [Fact]
    public void Attitude_Is_Continuous_In_Time_Under_Turbulent_Weather()
    {
        var model = WaveModel.Default;
        var weather = new WeatherSystem(new WeatherConfig(
            Mode: WeatherMode.Turbulent,
            WindDirection: 210.0,
            WindSpeed: 12.0,
            TurbulenceSeed: 20240));

        double eastM = FarEastM;
        double southM = FarSouthM;
        const double HeadingRad = 1.2;
        const double SpeedMps = 2.5;

        WaveMotion previous = default;
        bool hasPrevious = false;

        double worstHeaveExcess = 0.0;
        double worstRollExcess = 0.0;
        double worstPitchExcess = 0.0;
        double worstNormalisedHeave = 0.0;
        double worstNormalisedRoll = 0.0;
        double worstNormalisedPitch = 0.0;
        double worstSeaStateStep = 0.0;
        double biggestHeave = 0.0;
        double biggestRoll = 0.0;

        for (int i = 0; i < Steps; i++)
        {
            var wind = weather.GetWind(eastM, 0.0, southM);
            var motion = model.Sample(
                new Vector3((float)eastM, 0f, (float)southM), i * Dt, HeadingRad, wind, Hull);

            motion.SignificantHeightM.Should().BeGreaterThan(0.0);

            if (hasPrevious)
            {
                double seaStateStep = Math.Abs(motion.SignificantHeightM - previous.SignificantHeightM);
                worstSeaStateStep = Math.Max(worstSeaStateStep, seaStateStep);

                worstHeaveExcess = Math.Max(
                    worstHeaveExcess,
                    Math.Abs(motion.HeaveM - previous.HeaveM) - (0.5 * seaStateStep));
                worstRollExcess = Math.Max(
                    worstRollExcess,
                    Math.Abs(motion.RollRad - previous.RollRad) - (0.2 * seaStateStep));
                worstPitchExcess = Math.Max(
                    worstPitchExcess,
                    Math.Abs(motion.PitchRad - previous.PitchRad) - (0.2 * seaStateStep));

                worstNormalisedHeave = Math.Max(worstNormalisedHeave, Shape(motion, previous, m => m.HeaveM));
                worstNormalisedRoll = Math.Max(
                    worstNormalisedRoll, Shape(motion, previous, m => Math.Tan(m.RollRad)));
                worstNormalisedPitch = Math.Max(
                    worstNormalisedPitch, Shape(motion, previous, m => Math.Tan(m.PitchRad)));
            }

            biggestHeave = Math.Max(biggestHeave, Math.Abs(motion.HeaveM));
            biggestRoll = Math.Max(biggestRoll, Math.Abs(motion.RollRad));

            previous = motion;
            hasPrevious = true;

            eastM += SpeedMps * Math.Sin(HeadingRad) * Dt;
            southM -= SpeedMps * Math.Cos(HeadingRad) * Dt;
            weather.Step(Dt);
        }

        // A case run in a flat calm, or in a sea that never moved, would prove nothing at all.
        worstSeaStateStep.Should().BeGreaterThan(
            0.25, "the gusts must actually be moving the sea state for this to be a test");
        biggestHeave.Should().BeGreaterThan(0.2, "the vessel must actually be riding a sea");
        biggestRoll.Should().BeGreaterThan(0.01);

        worstNormalisedHeave.Should().BeLessThan(
            0.05,
            "heave over the sea state is a function of position and time alone, so one step of it "
                + "can only be one step of wave phase");
        worstNormalisedRoll.Should().BeLessThan(0.01);
        worstNormalisedPitch.Should().BeLessThan(0.01);

        worstHeaveExcess.Should().BeLessThan(
            0.05, "beyond the sea state getting taller there is nothing left to move the hull");
        worstRollExcess.Should().BeLessThan(0.02);
        worstPitchExcess.Should().BeLessThan(0.02);
    }

    /// <summary>The change in one motion quantity once the sea state is divided back out.</summary>
    /// <param name="motion">Motion at the end of the step.</param>
    /// <param name="previous">Motion at the start of the step.</param>
    /// <param name="select">Quantity to normalise — heave, or the tangent of an attitude angle.</param>
    /// <returns>The absolute change in that quantity per metre of significant height.</returns>
    private static double Shape(WaveMotion motion, WaveMotion previous, Func<WaveMotion, double> select) =>
        Math.Abs((select(motion) / motion.SignificantHeightM)
            - (select(previous) / previous.SignificantHeightM));

    // ─── A small change in the wind stays small, however far out the hull is ─

    /// <summary>
    /// Freshening the wind by one percent changes the attitude by a sliver, and by the same
    /// sliver at the origin, a mile out and eight miles out.
    /// </summary>
    /// <remarks>
    /// This is the defect stated directly. The previous model put the wind inside the phase, and
    /// phase is distance over wavelength, so the further from the origin a hull was the more a
    /// one-percent wind change moved it — past a kilometre it was a full swing between crest and
    /// trough. Now the wind changes only the height of the sea, so the motion changes by at most
    /// half the sea state's own change, a bound with no distance term in it at all.
    /// </remarks>
    /// <param name="distanceM">How far from the scene origin the vessel sits.</param>
    [Theory]
    [InlineData(0.0)]
    [InlineData(120.0)]
    [InlineData(1500.0)]
    [InlineData(12000.0)]
    public void A_Small_Wind_Change_Does_Not_Amplify_With_Distance_From_The_Origin(double distanceM)
    {
        var model = WaveModel.Default;
        var position = new Vector3((float)distanceM, 0f, (float)(-0.73 * distanceM));

        var breeze = new Vector3(6.4f, 0f, 6.4f);
        var freshened = breeze * 1.01f;

        var before = model.Sample(position, 37.0, 1.1, breeze, Hull);
        var after = model.Sample(position, 37.0, 1.1, freshened, Hull);

        double seaStateChange = Math.Abs(after.SignificantHeightM - before.SignificantHeightM);
        seaStateChange.Should().BeGreaterThan(0.0, "a percent more wind does raise a slightly bigger sea");
        seaStateChange.Should().BeLessThan(0.1);

        // Half the sea state's change is the whole budget: the field's shape did not move,
        // because position, time and heading did not move.
        Math.Abs(after.HeaveM - before.HeaveM).Should().BeLessThanOrEqualTo(0.5 * seaStateChange);
        Math.Abs(after.RollRad - before.RollRad).Should().BeLessThan(0.005);
        Math.Abs(after.PitchRad - before.PitchRad).Should().BeLessThan(0.005);
    }

    /// <summary>Two winds of equal speed from opposite quarters raise the same sea.</summary>
    /// <remarks>
    /// The swell bearing belongs to the sea state and is fixed for the life of the model, so it
    /// cannot be steered by the wind one hull happens to be sitting in. Under the previous model
    /// these two samples were on opposite sides of the wave field and disagreed completely.
    /// </remarks>
    [Fact]
    public void The_Wave_Field_Is_Not_Steered_By_The_Wind_At_One_Hull()
    {
        var model = WaveModel.Default;
        var position = new Vector3(1700f, 0f, -1900f);

        var northerly = new Vector3(0f, 0f, 9f);
        var southerly = new Vector3(0f, 0f, -9f);

        var one = model.Sample(position, 5.0, 0.4, northerly, Hull);
        var other = model.Sample(position, 5.0, 0.4, southerly, Hull);

        other.Should().Be(one, "the two winds are the same sea state, and the sea state is the whole input");
        Math.Abs(one.HeaveM).Should().BeGreaterThan(
            0.2, "a comparison of two flat surfaces would prove nothing");
    }

    // ─── Purity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The sample is a function of its arguments: repeating it, reordering it, or asking a
    /// different instance returns the identical bits.
    /// </summary>
    /// <remarks>
    /// A cached sea state, a per-instance filter or anything else that remembered the previous
    /// call would show up here as an answer that depended on call order. It would also break
    /// determinism outright, because the asset world does not promise a stepping order.
    /// </remarks>
    [Fact]
    public void The_Field_Is_A_Pure_Function_Of_Its_Arguments()
    {
        var shared = WaveModel.Default;
        var fresh = new WaveModel();

        var position = new Vector3(880f, 0f, 1440f);
        var wind = new Vector3(-7f, 0.4f, 3f);

        var reference = shared.Sample(position, 12.5, 2.7, wind, Hull);

        // Walk the model through a long, unrelated trajectory, then come back and ask again.
        for (int i = 0; i < 500; i++)
        {
            shared.Sample(new Vector3(i * 13f, 0f, i * -7f), i * Dt, i * 0.01, wind * 1.3f, Hull);
        }

        shared.Sample(position, 12.5, 2.7, wind, Hull).Should().Be(reference);
        fresh.Sample(position, 12.5, 2.7, wind, Hull).Should().Be(reference);
        new WaveModel().Sample(position, 12.5, 2.7, wind, Hull).Should().Be(reference);
    }

    /// <summary>Two vessels in the same place at the same time on the same heading ride the same wave.</summary>
    /// <remarks>
    /// Stated separately from purity because it is the property an operator would notice: two
    /// boats rafted together must not be drawn on different waves. They reach the rendezvous by
    /// different tracks and through different weather, which is exactly the state a stateful
    /// model would be carrying.
    /// </remarks>
    [Fact]
    public void Two_Vessels_At_The_Same_Place_And_Time_See_The_Same_Attitude()
    {
        var model = WaveModel.Default;
        var rendezvous = new Vector3(-640f, 0f, 2100f);
        const double ArrivalSeconds = 98.0;
        const double HeadingRad = 5.1;

        var wind = new Vector3(5f, 0f, -4f);

        // Vessel A closes from the east, vessel B from the north, in different gusts.
        for (int i = 0; i < 200; i++)
        {
            model.Sample(rendezvous + new Vector3(2000f - (i * 10f), 0f, 0f), i * 1.5, 4.7, wind, Hull);
        }

        var vesselA = model.Sample(rendezvous, ArrivalSeconds, HeadingRad, wind, Hull);

        for (int i = 0; i < 313; i++)
        {
            model.Sample(rendezvous + new Vector3(0f, 0f, (i * -9f) - 1500f), i * 0.7, 1.05, wind * 0.4f, Hull);
        }

        var vesselB = model.Sample(rendezvous, ArrivalSeconds, HeadingRad, wind, Hull);

        vesselB.Should().Be(vesselA);
        Math.Abs(vesselA.HeaveM).Should().BeGreaterThan(0.2, "two flat hulls would agree trivially");
    }

    // ─── Still decoration, still bounded ────────────────────────────────────

    /// <summary>
    /// A rough sea leaves the navigation solution bit-for-bit identical to a flat calm, and the
    /// published position stays on the mean water surface.
    /// </summary>
    /// <remarks>
    /// The structural guarantee is that <see cref="ISurfaceDynamics.Step"/> has no wave parameter
    /// to pass one through; this pins the behaviour that guarantee exists to produce. The wind is
    /// deliberately withheld from the conditions: wind makes leeway, leeway does move a vessel,
    /// and feeding it in would make the case about the wrong thing.
    /// </remarks>
    [Fact]
    public void Waves_Still_Do_Not_Reach_The_Navigation_Solution()
    {
        var dynamics = new SurfaceDynamics(Hull);
        var sea = WaveModel.Default;

        var gale = new Vector3(17f, 0f, -6f);
        sea.SignificantHeightFor(gale).Should().BeGreaterThan(
            sea.SignificantHeightFor(Vector3.Zero), "this wind must actually raise a sea");

        var conditions = new SurfaceConditions(
            new Vector3(0.4f, 0f, -0.2f), Vector3.Zero, double.PositiveInfinity);
        var setpoint = new SurfaceSetpoint(3.0, 0.05);

        var flatCalm = SurfaceMotionState.DeadInTheWater(FarEastM, FarSouthM, 0.0);
        var rough = flatCalm;
        double biggestHeave = 0.0;
        double biggestRoll = 0.0;
        double biggestPitch = 0.0;

        for (int i = 0; i < Steps; i++)
        {
            flatCalm = dynamics.Step(flatCalm, setpoint, Dt, conditions);
            rough = dynamics.Step(rough, setpoint, Dt, conditions);

            var motion = sea.Sample(
                new Vector3((float)rough.EastM, 0f, (float)rough.SouthM),
                i * Dt,
                rough.HeadingRad,
                gale,
                Hull);

            double.IsFinite(motion.HeaveM).Should().BeTrue();
            Math.Abs(motion.HeaveM).Should().BeLessThanOrEqualTo(0.5 * motion.SignificantHeightM);
            Math.Abs(motion.RollRad).Should().BeLessThanOrEqualTo(0.35);
            Math.Abs(motion.PitchRad).Should().BeLessThanOrEqualTo(0.35);

            biggestHeave = Math.Max(biggestHeave, Math.Abs(motion.HeaveM));
            biggestRoll = Math.Max(biggestRoll, Math.Abs(motion.RollRad));
            biggestPitch = Math.Max(biggestPitch, Math.Abs(motion.PitchRad));
        }

        biggestHeave.Should().BeGreaterThan(0.1, "an assertion about a sea that never moved proves nothing");
        biggestRoll.Should().BeGreaterThan(0.005);
        biggestPitch.Should().BeGreaterThan(0.005);

        Bits(rough).Should().Equal(Bits(flatCalm), "the sea state reached the display and nothing else");
        rough.ToPositionEus(3.0).Y.Should().Be(3.0f, "a vessel floats on the mean surface, not on a crest");
    }

    /// <summary>Nothing on the dynamics path has anywhere to put a wave.</summary>
    /// <remarks>
    /// The case above pins what the model does; this pins that there is no channel through which
    /// it could do anything else. A parameter, return or property of type
    /// <see cref="WaveMotion"/> anywhere on the navigation path is the change that would let a
    /// decoration start steering, and catching it here is cheaper than noticing a hull grounded
    /// on a trough.
    /// </remarks>
    [Fact]
    public void Nothing_On_The_Dynamics_Path_Can_Carry_A_Wave()
    {
        Type[] navigationPath =
        [
            typeof(ISurfaceDynamics),
            typeof(SurfaceDynamics),
            typeof(SurfaceMotionState),
            typeof(SurfaceSetpoint),
            typeof(SurfaceConditions),
            typeof(SurfaceVelocities),
        ];

        foreach (var type in navigationPath)
        {
            foreach (var method in type.GetMethods())
            {
                // GetElementType unwraps an `in` or `ref` parameter; it is null for everything
                // else, which is why the null-coalesce is the plain case rather than the guard.
                method.GetParameters()
                    .Select(p => p.ParameterType.GetElementType() ?? p.ParameterType)
                    .Append(method.ReturnType)
                    .Should().NotContain(
                        typeof(WaveMotion), "{0}.{1} is on the navigation path", type.Name, method.Name);
            }

            type.GetProperties().Select(property => property.PropertyType)
                .Should().NotContain(typeof(WaveMotion), "{0} is on the navigation path", type.Name);
        }
    }

    /// <summary>Every integrated quantity of a motion state, as raw bits.</summary>
    /// <param name="state">State to reduce.</param>
    /// <returns>The bit patterns, so two runs are compared exactly rather than approximately.</returns>
    private static long[] Bits(SurfaceMotionState state) =>
    [
        BitConverter.DoubleToInt64Bits(state.EastM),
        BitConverter.DoubleToInt64Bits(state.SouthM),
        BitConverter.DoubleToInt64Bits(state.HeadingRad),
        BitConverter.DoubleToInt64Bits(state.SurgeMps),
        BitConverter.DoubleToInt64Bits(state.SwayMps),
        BitConverter.DoubleToInt64Bits(state.YawRateRadPerSec),
    ];

    // ─── The one channel the wind does have ─────────────────────────────────

    /// <summary>The sea state rises smoothly and monotonically with wind speed, and stops at the ceiling.</summary>
    /// <remarks>
    /// The wind reaches the rendered motion through this function and nowhere else, so a corner
    /// in it — a hard clamp at the ceiling, say — would be a corner in the motion. The Lipschitz
    /// bound is what makes the continuity case above hold for any wind rather than for one seed.
    /// </remarks>
    [Fact]
    public void The_Sea_State_Rises_Smoothly_And_Monotonically_With_Wind_Speed()
    {
        var model = WaveModel.Default;
        double previous = model.SignificantHeightFor(Vector3.Zero);

        previous.Should().BeGreaterThan(0.0, "a mirror-flat surface reads as a rendering fault");
        previous.Should().BeLessThan(0.25, "a flat calm is a residual swell, not a sea");

        for (int i = 1; i <= 4000; i++)
        {
            double speed = i * 0.01;
            double height = model.SignificantHeightFor(new Vector3((float)speed, 0f, 0f));

            height.Should().BeGreaterThanOrEqualTo(previous, "more wind never means less sea");
            height.Should().BeLessThanOrEqualTo(model.MaxSignificantHeightM);
            (height - previous).Should().BeLessThan(
                0.0025, "the sea state must not be able to step, whatever the wind does");

            previous = height;
        }

        previous.Should().BeGreaterThan(2.0, "forty metres a second is a storm, and must read as one");
        model.SignificantHeightFor(new Vector3(500f, 0f, 500f))
            .Should().BeLessThanOrEqualTo(model.MaxSignificantHeightM);
    }

    // ─── Boundaries ─────────────────────────────────────────────────────────

    /// <summary>A hull is required; a bad number is not fatal.</summary>
    /// <remarks>
    /// A null profile is a wiring mistake and should fail loudly. A non-finite position, time,
    /// heading or wind is a numerical one somewhere upstream, and faulting the whole asset pass
    /// over a decoration would be the wrong trade — the hull is drawn level instead.
    /// </remarks>
    [Fact]
    public void Bad_Inputs_Are_Refused_Or_Absorbed_As_Appropriate()
    {
        var model = WaveModel.Default;
        var wind = new Vector3(8f, 0f, 2f);

        // The null! exists only to reach the guard; the parameter is non-nullable.
        Action noHull = () => model.Sample(Vector3.Zero, 1.0, 0.0, wind, null!);
        noHull.Should().Throw<ArgumentNullException>();

        model.Sample(Vector3.Zero, double.NaN, 0.0, wind, Hull).Should().Be(WaveMotion.Calm);
        model.Sample(Vector3.Zero, 1.0, double.PositiveInfinity, wind, Hull).Should().Be(WaveMotion.Calm);
        model.Sample(new Vector3(float.NaN, 0f, 0f), 1.0, 0.0, wind, Hull).Should().Be(WaveMotion.Calm);
        model.Sample(Vector3.Zero, 1.0, 0.0, new Vector3(0f, 0f, float.NaN), Hull).Should().Be(WaveMotion.Calm);
    }

    /// <summary>A model cannot be built around a sea state that is not a number.</summary>
    /// <param name="maxSignificantHeightM">Ceiling offered to the constructor.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    public void A_Nonsensical_Ceiling_Is_Refused(double maxSignificantHeightM)
    {
        Action act = () => new WaveModel(maxSignificantHeightM);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("maxSignificantHeightM");
    }

    /// <summary>A model cannot be built around a swell that runs nowhere.</summary>
    /// <param name="swellBearingRad">Bearing offered to the constructor.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void A_Nonsensical_Swell_Bearing_Is_Refused(double swellBearingRad)
    {
        Action act = () => new WaveModel(2.5, swellBearingRad);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("swellBearingRad");
    }

    /// <summary>A ceiling of zero is a request for a mirror, and is honoured exactly.</summary>
    [Fact]
    public void A_Zero_Ceiling_Flattens_The_Sea()
    {
        var mirror = new WaveModel(0.0);

        mirror.SignificantHeightFor(new Vector3(20f, 0f, 0f)).Should().Be(0.0);
        mirror.Sample(new Vector3(500f, 0f, 500f), 9.0, 1.0, new Vector3(20f, 0f, 0f), Hull)
            .Should().Be(WaveMotion.Calm);
    }
}
