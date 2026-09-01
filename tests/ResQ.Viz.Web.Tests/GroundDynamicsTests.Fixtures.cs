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

using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets.Ground;

namespace ResQ.Viz.Web.Tests;

/// <summary>Fixtures and helpers for <see cref="GroundDynamicsTests"/>.</summary>
/// <remarks>
/// Split out so the assertion files read as a list of physical claims. Everything here is a
/// literal or a closed-form expression: no clock, no generator, and no reference to a recorded
/// trajectory, so nothing in this file can make a failing model look like a passing one.
/// </remarks>
public sealed partial class GroundDynamicsTests
{
    /// <summary>Both shipped motion models, freshly constructed.</summary>
    /// <returns>One bicycle model and one skid-steer model.</returns>
    private static IGroundDynamics[] AllModels() =>
    [
        new AckermannDynamics(GroundProfile.AckermannRover),
        new DifferentialDynamics(GroundProfile.DifferentialRover),
    ];

    /// <summary>Every component of a state as raw bits, for an exact comparison.</summary>
    /// <param name="state">State to decompose.</param>
    /// <returns>The six components in declaration order; the first two are the position.</returns>
    private static long[] Bits(GroundMotionState state) =>
    [
        BitConverter.DoubleToInt64Bits(state.EastM),
        BitConverter.DoubleToInt64Bits(state.SouthM),
        BitConverter.DoubleToInt64Bits(state.HeadingRad),
        BitConverter.DoubleToInt64Bits(state.ForwardSpeedMps),
        BitConverter.DoubleToInt64Bits(state.YawRateRadPerSec),
        BitConverter.DoubleToInt64Bits(state.SteeringAngleRad),
    ];

    /// <summary>Steps a model repeatedly with one held setpoint on unrestricted ground.</summary>
    /// <param name="model">Model to step.</param>
    /// <param name="state">Starting state.</param>
    /// <param name="setpoint">Setpoint to hold for every step.</param>
    /// <param name="steps">Number of fixed <see cref="Dt"/> steps to take.</param>
    /// <returns>The state after the last step.</returns>
    private static GroundMotionState Run(
        IGroundDynamics model, GroundMotionState state, GroundSetpoint setpoint, int steps) =>
        Run(model, state, setpoint, steps, GroundConditions.Unrestricted);

    /// <summary>Steps a model repeatedly with one held setpoint and one set of conditions.</summary>
    /// <param name="model">Model to step.</param>
    /// <param name="state">Starting state.</param>
    /// <param name="setpoint">Setpoint to hold for every step.</param>
    /// <param name="steps">Number of fixed <see cref="Dt"/> steps to take.</param>
    /// <param name="conditions">Terrain-derived ceiling and traction to hold.</param>
    /// <returns>The state after the last step.</returns>
    private static GroundMotionState Run(
        IGroundDynamics model,
        GroundMotionState state,
        GroundSetpoint setpoint,
        int steps,
        GroundConditions conditions)
    {
        for (int i = 0; i < steps; i++)
        {
            state = model.Step(state, setpoint, Dt, conditions);
        }

        return state;
    }

    /// <summary>Smallest signed turn between two headings, in radians.</summary>
    /// <param name="endRad">Later heading.</param>
    /// <param name="startRad">Earlier heading.</param>
    /// <returns>The turn from <paramref name="startRad"/> to <paramref name="endRad"/> in <c>(-pi, pi]</c>.</returns>
    private static double AngleDelta(double endRad, double startRad)
    {
        double delta = CoordinateFrames.NormalizeAngle(endRad - startRad);
        return delta > Math.PI ? delta - Math.Tau : delta;
    }

    /// <summary>Tolerance on a traced radius, derived from the integrator's truncation error.</summary>
    /// <remarks>
    /// The midpoint rule steps a length of <c>v*dt</c> in exactly the right direction — the chord
    /// bisector of the arc — where the true chord is <c>2R sin(D/2)</c> with <c>D = omega*dt</c>.
    /// The iterates therefore lie exactly on a circle of radius
    /// <c>R*D / (2 sin(D/2)) = R*(1 + D^2/24 + O(D^4))</c>: a fixed outward bias of
    /// <c>R*D^2/24</c>, not a drift, and second order in the timestep as the scheme's order
    /// promises. On the 240 Hz circles traced here <c>D</c> is 2.6e-3 rad, so the bias is around
    /// 1.5 micrometres on the Ackermann case. What is returned is twice that bias plus a
    /// nanometre of rounding headroom, which makes it a statement about the scheme rather than a
    /// figure fitted to a run: halve the timestep and the assertion tightens fourfold on its own,
    /// and switching the integrator to explicit Euler widens the bias enough to fail it.
    /// </remarks>
    /// <param name="radiusM">Commanded path radius in metres.</param>
    /// <param name="yawRateRadPerSec">Commanded yaw rate in radians per second.</param>
    /// <returns>The permitted deviation in metres.</returns>
    private static double RadiusTolerance(double radiusM, double yawRateRadPerSec)
    {
        double headingStepRad = yawRateRadPerSec * Dt;

        return (2.0 * radiusM * headingStepRad * headingStepRad / 24.0) + 1e-9;
    }

    /// <summary>Integrates a full turn and measures the traced radius and the closure error.</summary>
    /// <remarks>
    /// The centre is derived, not fitted. For a right-hand turn it lies exactly one polygon
    /// circumradius to starboard, and starboard of heading <c>h</c> is <c>(cos h, sin h)</c> in
    /// scene <c>(X, Z)</c> given that forward is <c>(sin h, -cos h)</c>. Fitting a circle to the
    /// samples instead would turn this into a check of the trajectory against itself, which would
    /// pass for any smooth curve.
    /// </remarks>
    /// <param name="model">Model to integrate.</param>
    /// <param name="settled">State at the start of the revolution, already at its setpoint.</param>
    /// <param name="setpoint">Setpoint held for the whole revolution.</param>
    /// <param name="steps">Steps in one revolution.</param>
    /// <param name="radiusM">Commanded path radius in metres.</param>
    /// <param name="yawRateRadPerSec">Commanded yaw rate in radians per second.</param>
    /// <returns>The smallest and largest radius seen, the distance from the start after one revolution, and the predicted polygon circumradius.</returns>
    private static (double MinRadiusM, double MaxRadiusM, double ClosureM, double PolygonRadiusM) TraceCircle(
        IGroundDynamics model,
        GroundMotionState settled,
        GroundSetpoint setpoint,
        int steps,
        double radiusM,
        double yawRateRadPerSec)
    {
        double headingStepRad = yawRateRadPerSec * Dt;
        double polygonRadiusM = radiusM * headingStepRad / (2.0 * Math.Sin(0.5 * headingStepRad));

        double centreEastM = settled.EastM + (polygonRadiusM * Math.Cos(settled.HeadingRad));
        double centreSouthM = settled.SouthM + (polygonRadiusM * Math.Sin(settled.HeadingRad));

        double minRadiusM = double.PositiveInfinity;
        double maxRadiusM = 0.0;
        var state = settled;

        for (int i = 0; i < steps; i++)
        {
            state = model.Step(state, setpoint, Dt, GroundConditions.Unrestricted);

            double east = state.EastM - centreEastM;
            double south = state.SouthM - centreSouthM;
            double radius = Math.Sqrt((east * east) + (south * south));

            minRadiusM = Math.Min(minRadiusM, radius);
            maxRadiusM = Math.Max(maxRadiusM, radius);
        }

        double closureEastM = state.EastM - settled.EastM;
        double closureSouthM = state.SouthM - settled.SouthM;

        return (
            minRadiusM,
            maxRadiusM,
            Math.Sqrt((closureEastM * closureEastM) + (closureSouthM * closureSouthM)),
            polygonRadiusM);
    }
}
