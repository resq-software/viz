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
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>The limiters, the non-finite boundary, and determinism.</summary>
/// <remarks>
/// Each limiter gets its own case, driven so that it is the only ceiling anywhere near binding.
/// That is the whole point of splitting them: a single "the rover does not exceed its envelope"
/// test passes just as happily when four limits are broken and a fifth is doing all the work,
/// and tells you nothing about which one you just deleted.
/// </remarks>
public sealed partial class GroundDynamicsTests
{
    // ─── One limit at a time ────────────────────────────────────────────────

    /// <summary>The steering-rate limit binds: one step slews the rack by exactly rate times dt.</summary>
    [Fact]
    public void Steering_Rate_Limit_Binds()
    {
        var profile = GroundProfile.AckermannRover;
        var model = new AckermannDynamics(profile);

        var stepped = model.Step(
            GroundMotionState.AtRest(0.0, 0.0, 0.0),
            GroundSetpoint.Steer(0.0, profile.MaxSteeringAngleRad),
            Dt,
            GroundConditions.Unrestricted);

        stepped.SteeringAngleRad.Should().BeApproximately(profile.MaxSteeringRateRadPerSec * Dt, 1e-15);
        stepped.SteeringAngleRad.Should().BeLessThan(profile.MaxSteeringAngleRad,
            "the rate limit, not the lock, is what held on the first step");
    }

    /// <summary>The steering-angle limit binds once the rate limit has stopped being the binding one.</summary>
    [Fact]
    public void Steering_Angle_Limit_Binds()
    {
        var profile = GroundProfile.AckermannRover;
        var model = new AckermannDynamics(profile);

        var settled = Run(
            model, GroundMotionState.AtRest(0.0, 0.0, 0.0), GroundSetpoint.Steer(0.0, 40.0), 600);

        settled.SteeringAngleRad.Should().Be(profile.MaxSteeringAngleRad,
            "a clamp returns the bound itself, so this is exact rather than approximate");
    }

    /// <summary>The acceleration limit binds on the first step, and traction scales it.</summary>
    /// <param name="tractionCoefficient">Available grip, scaling the drivetrain's rate limit.</param>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.25)]
    public void Acceleration_Limit_Binds_And_Scales_With_Traction(double tractionCoefficient)
    {
        var profile = GroundProfile.AckermannRover;
        var model = new AckermannDynamics(profile);

        var stepped = model.Step(
            GroundMotionState.AtRest(0.0, 0.0, 0.0),
            GroundSetpoint.Steer(profile.MaxForwardSpeedMps, 0.0),
            Dt,
            new GroundConditions(double.PositiveInfinity, tractionCoefficient));

        stepped.ForwardSpeedMps.Should().BeApproximately(
            profile.MaxAccelerationMps2 * tractionCoefficient * Dt, 1e-15);
    }

    /// <summary>A stop request decelerates at the braking limit, which is not the acceleration limit.</summary>
    [Fact]
    public void Braking_Limit_Binds_And_Is_Not_The_Acceleration_Limit()
    {
        const double CruiseMps = 5.0;

        var profile = GroundProfile.AckermannRover;
        var model = new AckermannDynamics(profile);
        profile.MaxBrakingMps2.Should().NotBe(profile.MaxAccelerationMps2,
            "the two figures must differ or this case cannot tell which one bound");

        var moving = Run(
            model, GroundMotionState.AtRest(0.0, 0.0, 0.0), GroundSetpoint.Steer(CruiseMps, 0.0), 900);
        moving.ForwardSpeedMps.Should().BeApproximately(CruiseMps, SettleTolerance);

        var braked = model.Step(moving, GroundSetpoint.Stop, Dt, GroundConditions.Unrestricted);

        braked.ForwardSpeedMps.Should().BeApproximately(
            moving.ForwardSpeedMps - (profile.MaxBrakingMps2 * Dt), 1e-12);
    }

    /// <summary>The forward and reverse speed limits bind separately, each at its own figure.</summary>
    [Fact]
    public void Forward_And_Reverse_Speed_Limits_Bind_Separately()
    {
        var profile = GroundProfile.AckermannRover;
        var model = new AckermannDynamics(profile);
        var rest = GroundMotionState.AtRest(0.0, 0.0, 0.0);

        Run(model, rest, GroundSetpoint.Steer(1000.0, 0.0), 2400)
            .ForwardSpeedMps.Should().Be(profile.MaxForwardSpeedMps);

        Run(model, rest, GroundSetpoint.Steer(-1000.0, 0.0), 2400)
            .ForwardSpeedMps.Should().Be(-profile.MaxReverseSpeedMps,
                "reverse is its own, much lower ceiling and not a mirrored forward one");
    }

    /// <summary>The terrain-derived ceiling binds below the profile's own speed limit.</summary>
    [Fact]
    public void Terrain_Speed_Ceiling_Binds()
    {
        const double CeilingMps = 1.75;

        var profile = GroundProfile.AckermannRover;
        var model = new AckermannDynamics(profile);
        CeilingMps.Should().BeLessThan(profile.MaxForwardSpeedMps);

        Run(model,
            GroundMotionState.AtRest(0.0, 0.0, 0.0),
            GroundSetpoint.Steer(profile.MaxForwardSpeedMps, 0.0),
            2400,
            new GroundConditions(CeilingMps, 1.0))
            .ForwardSpeedMps.Should().Be(CeilingMps);
    }

    /// <summary>
    /// The lateral-acceleration limit binds at full lock, holding <c>v^2 tan(steer) / L</c> at
    /// exactly the profile's cornering figure.
    /// </summary>
    /// <remarks>
    /// The one limit that is not a clamp on the quantity it is expressed in: it acts on speed to
    /// bound an acceleration, so the check is on the derived acceleration and not on the speed
    /// the model happened to pick.
    /// </remarks>
    [Fact]
    public void Lateral_Acceleration_Limit_Binds_At_Full_Lock()
    {
        var profile = GroundProfile.AckermannRover;
        var model = new AckermannDynamics(profile);

        var settled = Run(
            model,
            GroundMotionState.AtRest(0.0, 0.0, 0.0),
            GroundSetpoint.Steer(profile.MaxForwardSpeedMps, profile.MaxSteeringAngleRad),
            2400);

        settled.ForwardSpeedMps.Should().BeLessThan(profile.MaxForwardSpeedMps,
            "the cornering ceiling, not the top speed, is what held here");
        settled.SteeringAngleRad.Should().Be(profile.MaxSteeringAngleRad);

        double lateralMps2 = settled.ForwardSpeedMps * settled.ForwardSpeedMps
            * Math.Tan(settled.SteeringAngleRad) / profile.WheelbaseM;

        lateralMps2.Should().BeApproximately(profile.MaxLateralAccelerationMps2, 1e-9);
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
        foreach (var model in AllModels())
        {
            Action act = () => model.Step(
                GroundMotionState.AtRest(0.0, 0.0, 0.0),
                GroundSetpoint.Stop,
                deltaSeconds,
                GroundConditions.Unrestricted);

            act.Should().Throw<ArgumentOutOfRangeException>()
                .Which.ParamName.Should().Be("deltaSeconds");
        }
    }

    /// <summary>A non-finite state or setpoint fails at the boundary rather than poisoning the pose.</summary>
    [Fact]
    public void Step_Rejects_A_Non_Finite_State_Or_Setpoint()
    {
        foreach (var model in AllModels())
        {
            Action badState = () => model.Step(
                new GroundMotionState(double.NaN, 0.0, 0.0, 0.0, 0.0, 0.0),
                GroundSetpoint.Stop,
                Dt,
                GroundConditions.Unrestricted);
            badState.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("state");

            Action badSetpoint = () => model.Step(
                GroundMotionState.AtRest(0.0, 0.0, 0.0),
                new GroundSetpoint(double.PositiveInfinity),
                Dt,
                GroundConditions.Unrestricted);
            badSetpoint.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("setpoint");
        }
    }

    /// <summary>
    /// Every combination of extreme-but-finite setpoint and degenerate conditions stays finite
    /// across a long run — full lock, reverse at the reverse limit, zero grip and a zero ceiling
    /// included.
    /// </summary>
    [Fact]
    public void Extreme_Finite_Inputs_Never_Produce_A_Non_Finite_State()
    {
        GroundSetpoint[] setpoints =
        [
            GroundSetpoint.Stop,
            GroundSetpoint.Steer(1e9, 1e9),
            GroundSetpoint.Steer(-1e9, -1e9),
            GroundSetpoint.Steer(-1000.0, GroundProfile.AckermannRover.MaxSteeringAngleRad),
            GroundSetpoint.Turn(1e9, 1e9),
            GroundSetpoint.Turn(-1e9, -1e9),
            new GroundSetpoint(double.MaxValue, double.MaxValue, double.MaxValue),
            new GroundSetpoint(double.Epsilon, -double.Epsilon, double.Epsilon),
        ];

        GroundConditions[] conditions =
        [
            GroundConditions.Unrestricted,
            new GroundConditions(double.NaN, double.NaN),
            new GroundConditions(-5.0, -5.0),
            new GroundConditions(0.0, 0.0),
            new GroundConditions(double.PositiveInfinity, double.MaxValue),
        ];

        var models = AllModels();

        for (int m = 0; m < models.Length; m++)
        {
            for (int s = 0; s < setpoints.Length; s++)
            {
                for (int c = 0; c < conditions.Length; c++)
                {
                    var state = Run(
                        models[m], GroundMotionState.AtRest(0.0, 0.0, 1.0), setpoints[s], 720, conditions[c]);

                    // Indices rather than the values themselves: FluentAssertions treats a
                    // reason as a format string, and a record's ToString is full of braces.
                    string because = $"model {models[m].ModelKey}, setpoint {s}, conditions {c}";

                    double.IsFinite(state.EastM).Should().BeTrue(because);
                    double.IsFinite(state.SouthM).Should().BeTrue(because);
                    double.IsFinite(state.HeadingRad).Should().BeTrue(because);
                    double.IsFinite(state.ForwardSpeedMps).Should().BeTrue(because);
                    double.IsFinite(state.YawRateRadPerSec).Should().BeTrue(because);
                    double.IsFinite(state.SteeringAngleRad).Should().BeTrue(because);
                    state.HeadingRad.Should().BeInRange(0.0, Math.Tau, because);
                }
            }
        }
    }

    /// <summary>A parked vehicle holding a stop does not wander, to the last bit, over a long idle.</summary>
    [Fact]
    public void A_Stopped_Vehicle_Holding_Stop_Does_Not_Drift()
    {
        var start = GroundMotionState.AtRest(1234.5, -678.25, 2.5);

        foreach (var model in AllModels())
        {
            Bits(Run(model, start, GroundSetpoint.Stop, 3600)).Should().Equal(Bits(start));
        }
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
        var models = AllModels();
        var replays = AllModels();

        for (int i = 0; i < models.Length; i++)
        {
            Bits(RunSchedule(models[i])).Should().Equal(Bits(RunSchedule(replays[i])));
        }
    }

    /// <summary>Drives a model through a fixed, varying setpoint schedule with no randomness in it.</summary>
    /// <param name="model">Model to drive.</param>
    /// <returns>The state after the schedule completes.</returns>
    private static GroundMotionState RunSchedule(IGroundDynamics model)
    {
        var state = GroundMotionState.AtRest(-7.5, 3.25, 0.4);

        for (int i = 0; i < 1500; i++)
        {
            // A deterministic function of the step index alone: fast enough that every limiter
            // stays engaged, and reproducible without a clock or a generator.
            double phase = i * 0.017;
            var setpoint = new GroundSetpoint(
                SpeedMps: 4.0 * Math.Sin(phase),
                SteeringAngleRad: 0.4 * Math.Cos(0.7 * phase),
                YawRateRadPerSec: 0.9 * Math.Sin(1.3 * phase));

            state = model.Step(state, setpoint, Dt, new GroundConditions(6.0, 0.8));
        }

        return state;
    }
}
