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
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

/// <summary>Fixtures and helpers for <see cref="SurfaceDynamicsTests"/>.</summary>
/// <remarks>
/// Split out so the assertion files read as a list of physical claims. Everything here is a
/// literal or a closed-form expression: no clock, no generator, and no reference to a recorded
/// trajectory, so nothing in this file can make a failing model look like a passing one.
/// </remarks>
public sealed partial class SurfaceDynamicsTests
{
    /// <summary>Every component of a state as raw bits, for an exact comparison.</summary>
    /// <param name="state">State to decompose.</param>
    /// <returns>The six components in declaration order; the first two are the position and the third the heading.</returns>
    private static long[] Bits(SurfaceMotionState state) =>
    [
        BitConverter.DoubleToInt64Bits(state.EastM),
        BitConverter.DoubleToInt64Bits(state.SouthM),
        BitConverter.DoubleToInt64Bits(state.HeadingRad),
        BitConverter.DoubleToInt64Bits(state.SurgeMps),
        BitConverter.DoubleToInt64Bits(state.SwayMps),
        BitConverter.DoubleToInt64Bits(state.YawRateRadPerSec),
    ];

    /// <summary>Steps a model repeatedly with one held setpoint in slack water and still air.</summary>
    /// <param name="model">Model to step.</param>
    /// <param name="state">Starting state.</param>
    /// <param name="setpoint">Setpoint to hold for every step.</param>
    /// <param name="steps">Number of fixed <see cref="Dt"/> steps to take.</param>
    /// <returns>The state after the last step.</returns>
    private static SurfaceMotionState Run(
        ISurfaceDynamics model, SurfaceMotionState state, SurfaceSetpoint setpoint, int steps) =>
        Run(model, state, setpoint, steps, SurfaceConditions.Calm);

    /// <summary>Steps a model repeatedly with one held setpoint and one set of conditions.</summary>
    /// <param name="model">Model to step.</param>
    /// <param name="state">Starting state.</param>
    /// <param name="setpoint">Setpoint to hold for every step.</param>
    /// <param name="steps">Number of fixed <see cref="Dt"/> steps to take.</param>
    /// <param name="conditions">Current, wind and external ceiling to hold.</param>
    /// <returns>The state after the last step.</returns>
    private static SurfaceMotionState Run(
        ISurfaceDynamics model,
        SurfaceMotionState state,
        SurfaceSetpoint setpoint,
        int steps,
        SurfaceConditions conditions)
    {
        for (int i = 0; i < steps; i++)
        {
            state = model.Step(state, setpoint, Dt, conditions);
        }

        return state;
    }

    /// <summary>Smallest signed turn between two bearings, in radians.</summary>
    /// <param name="endRad">Later bearing.</param>
    /// <param name="startRad">Earlier bearing.</param>
    /// <returns>The turn from <paramref name="startRad"/> to <paramref name="endRad"/> in <c>(-pi, pi]</c>.</returns>
    private static double AngleDelta(double endRad, double startRad)
    {
        double delta = CoordinateFrames.NormalizeAngle(endRad - startRad);
        return delta > Math.PI ? delta - Math.Tau : delta;
    }

    /// <summary>Horizontal distance between two poses, in metres.</summary>
    /// <param name="end">Later pose.</param>
    /// <param name="start">Earlier pose.</param>
    /// <returns>The distance made good in metres.</returns>
    private static double Displacement(in SurfaceMotionState end, in SurfaceMotionState start)
    {
        double eastM = end.EastM - start.EastM;
        double southM = end.SouthM - start.SouthM;

        return Math.Sqrt((eastM * eastM) + (southM * southM));
    }

    /// <summary>Tolerance on a traced radius, derived from the integrator's truncation error.</summary>
    /// <remarks>
    /// The midpoint rule steps a length of <c>v*dt</c> in exactly the right direction — the chord
    /// bisector of the arc — where the true chord is <c>2R sin(D/2)</c> with <c>D = omega*dt</c>.
    /// The iterates therefore lie exactly on a circle of radius
    /// <c>R*D / (2 sin(D/2)) = R*(1 + D^2/24 + O(D^4))</c>: a fixed outward bias of
    /// <c>R*D^2/24</c>, not a drift, and second order in the timestep as the scheme's order
    /// promises. What is returned is twice that bias plus a nanometre of rounding headroom, which
    /// makes it a statement about the scheme rather than a figure fitted to a run: halve the
    /// timestep and the assertion tightens fourfold on its own, and switching the integrator to
    /// explicit Euler widens the bias enough to fail it.
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
    /// circumradius to starboard of the <em>course</em> — not of the heading, because a turning
    /// hull crabs — and starboard of a bearing <c>c</c> is <c>(cos c, sin c)</c> in scene
    /// <c>(X, Z)</c> given that the bearing itself is <c>(sin c, -cos c)</c>. Fitting a circle to
    /// the samples instead would turn this into a check of the trajectory against itself, which
    /// would pass for any smooth curve.
    /// </remarks>
    /// <param name="model">Model to integrate.</param>
    /// <param name="settled">State at the start of the revolution, already at its steady solution.</param>
    /// <param name="setpoint">Setpoint held for the whole revolution.</param>
    /// <param name="steps">Steps in one revolution.</param>
    /// <param name="radiusM">Predicted path radius in metres.</param>
    /// <param name="yawRateRadPerSec">Steady yaw rate in radians per second.</param>
    /// <param name="crabRad">Angle between the heading and the course, in radians.</param>
    /// <returns>The smallest and largest radius seen, the distance from the start after one revolution, and the predicted polygon circumradius.</returns>
    private static (double MinRadiusM, double MaxRadiusM, double ClosureM, double PolygonRadiusM) TraceCircle(
        ISurfaceDynamics model,
        SurfaceMotionState settled,
        SurfaceSetpoint setpoint,
        int steps,
        double radiusM,
        double yawRateRadPerSec,
        double crabRad)
    {
        double headingStepRad = yawRateRadPerSec * Dt;
        double polygonRadiusM = radiusM * headingStepRad / (2.0 * Math.Sin(0.5 * headingStepRad));
        double courseRad = settled.HeadingRad + crabRad;

        double centreEastM = settled.EastM + (polygonRadiusM * Math.Cos(courseRad));
        double centreSouthM = settled.SouthM + (polygonRadiusM * Math.Sin(courseRad));

        double minRadiusM = double.PositiveInfinity;
        double maxRadiusM = 0.0;
        var state = settled;

        for (int i = 0; i < steps; i++)
        {
            state = model.Step(state, setpoint, Dt, SurfaceConditions.Calm);

            double east = state.EastM - centreEastM;
            double south = state.SouthM - centreSouthM;
            double radius = Math.Sqrt((east * east) + (south * south));

            minRadiusM = Math.Min(minRadiusM, radius);
            maxRadiusM = Math.Max(maxRadiusM, radius);
        }

        return (minRadiusM, maxRadiusM, Displacement(state, settled), polygonRadiusM);
    }

    /// <summary>Drives a model through a fixed, varying schedule with no randomness in it.</summary>
    /// <remarks>
    /// Every setpoint and every disturbance is a function of the step index alone: hard enough
    /// to keep the astern gate, both speed ceilings and the speed-dependent turn ceiling engaged
    /// throughout, and reproducible without a clock or a generator.
    /// </remarks>
    /// <param name="model">Model to drive.</param>
    /// <returns>The state after the schedule completes.</returns>
    private static SurfaceMotionState RunSchedule(ISurfaceDynamics model)
    {
        var state = SurfaceMotionState.DeadInTheWater(-7.5, 3.25, 0.4);

        for (int i = 0; i < 3000; i++)
        {
            double phase = i * 0.017;
            var setpoint = new SurfaceSetpoint(
                SurgeMps: 7.0 * Math.Sin(phase),
                YawRateRadPerSec: 0.9 * Math.Cos(1.3 * phase));

            var conditions = new SurfaceConditions(
                new Vector3((float)(0.8 * Math.Sin(0.3 * phase)), 0f, 0.4f),
                new Vector3(6f, 0f, (float)(4.0 * Math.Cos(0.2 * phase))),
                4.5);

            state = model.Step(state, setpoint, Dt, conditions);
        }

        return state;
    }
}
