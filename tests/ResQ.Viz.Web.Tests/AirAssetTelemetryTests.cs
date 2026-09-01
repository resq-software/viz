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
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Entities;
using ResQ.Simulation.Engine.Environment;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Ground velocity and airspeed are different quantities, and an air asset must publish both
/// correctly under wind.
/// </summary>
/// <remarks>
/// The load-bearing fact is that the two SDK flight models store <em>different</em> things in the
/// one <c>DronePhysicsState.Velocity</c> field: <see cref="KinematicFlightModel"/> integrates
/// <c>position += velocity*dt + wind*dt</c> and keeps only the commanded (air-relative) velocity,
/// while <see cref="QuadrotorFlightModel"/> feeds wind in as a force and keeps the resulting
/// ground velocity. A room can be configured for either, so the tests below run against both.
/// <para>
/// Every assertion is anchored to the one quantity that is not a matter of convention: the actual
/// per-tick position delta. Whatever the model stores internally, the published ground velocity
/// has to be the thing the asset's own position is moving at, or a client that differentiates
/// position and a client that reads the twist disagree about the same aircraft.
/// </para>
/// <para>
/// Determinism: a fixed timestep, a constant wind field, flat terrain, a fixed epoch and no wall
/// clock. Neither flight model draws on a random source, so a run is reproducible.
/// </para>
/// </remarks>
public sealed class AirAssetTelemetryTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Steps taken before the measured tick, to let the quadrotor's PD loop settle.</summary>
    private const int SettlingSteps = 120;

    /// <summary>Cruise altitude, high enough that the integrators' ground clamp never engages.</summary>
    private const float CruiseAltitudeM = 60.0f;

    /// <summary>
    /// Tolerance, in metres per second, for comparing a published velocity against one recovered
    /// by differencing positions.
    /// </summary>
    /// <remarks>
    /// Set by single-precision cancellation, not by physics. Positions are <c>float</c> and reach
    /// tens of metres, so their difference loses a few units in the last place, and dividing by a
    /// 1/60 s timestep multiplies that by sixty. Still three orders of magnitude tighter than the
    /// wind-sized error this file exists to catch.
    /// </remarks>
    private const float PositionDeltaToleranceMps = 5e-3f;

    /// <summary>
    /// Constant wind: an easterly component, a southerly component and a slight updraught, so a
    /// mistake on any one axis shows up rather than cancelling.
    /// </summary>
    private static readonly Vector3 Wind = new(3.0f, 0.5f, -1.0f);

    /// <summary>Fixed epoch for timestamps, so no test reads a clock.</summary>
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The published ground velocity is exactly the velocity the asset's position is changing at.
    /// </summary>
    /// <remarks>
    /// This is the assertion that fails when <c>State.Velocity</c> is published as-is for the
    /// kinematic model: that field is the air-relative velocity there, so it is short of the true
    /// ground velocity by the whole wind vector.
    /// </remarks>
    /// <param name="modelType">Flight model the room is configured for.</param>
    [Theory]
    [InlineData(FlightModelType.Kinematic)]
    [InlineData(FlightModelType.Quadrotor)]
    public void GroundVelocity_Matches_The_Actual_Position_Delta(FlightModelType modelType)
    {
        var (state, previousPosition, currentPosition) = FlyOneMeasuredTick(modelType);

        var expected = (currentPosition - previousPosition) / (float)Dt;

        state.Twist.Frame.Should().Be(CoordinateFrame.LocalEus);
        state.Twist.Linear.X.Should().BeApproximately(expected.X, PositionDeltaToleranceMps);
        state.Twist.Linear.Y.Should().BeApproximately(expected.Y, PositionDeltaToleranceMps);
        state.Twist.Linear.Z.Should().BeApproximately(expected.Z, PositionDeltaToleranceMps);

        var air = AirState(state);
        air.GroundSpeedMps.Should().BeApproximately(
            CoordinateFrames.SpeedOverGround(expected), PositionDeltaToleranceMps);
        air.ClimbRateMps.Should().BeApproximately(expected.Y, PositionDeltaToleranceMps);
    }

    /// <summary>
    /// Airspeed is the ground velocity minus the wind, and under a real wind the two speeds are
    /// visibly different numbers rather than the same number reported twice.
    /// </summary>
    /// <param name="modelType">Flight model the room is configured for.</param>
    [Theory]
    [InlineData(FlightModelType.Kinematic)]
    [InlineData(FlightModelType.Quadrotor)]
    public void Airspeed_Differs_From_Ground_Speed_By_The_Wind(FlightModelType modelType)
    {
        var (state, previousPosition, currentPosition) = FlyOneMeasuredTick(modelType);

        var groundVelocity = (currentPosition - previousPosition) / (float)Dt;
        double expectedAirspeed = CoordinateFrames.SpeedOverGround(groundVelocity - Wind);

        var air = AirState(state);
        air.AirspeedMps.Should().NotBeNull();
        air.AirspeedMps!.Value.Should().BeApproximately(expectedAirspeed, PositionDeltaToleranceMps);

        air.WindSpeedMps.Should().BeApproximately(CoordinateFrames.SpeedOverGround(Wind), 1e-6);
        air.AirspeedMps!.Value.Should().NotBeApproximately(
            air.GroundSpeedMps, 0.1, "a crosswind and a tailwind must separate the two speeds");
    }

    /// <summary>
    /// Ground speed and airspeed are not swapped: with this tailwind the asset is moving over the
    /// ground faster than it is moving through the air.
    /// </summary>
    /// <remarks>
    /// Deliberately an ordering assertion rather than an arithmetic one. Subtracting the wind from
    /// a velocity that is already air-relative — the inverted reading — lands the two speeds the
    /// same distance apart but the wrong way round, so only direction catches it.
    /// </remarks>
    /// <param name="modelType">Flight model the room is configured for.</param>
    [Theory]
    [InlineData(FlightModelType.Kinematic)]
    [InlineData(FlightModelType.Quadrotor)]
    public void A_Tailwind_Makes_Ground_Speed_Exceed_Airspeed(FlightModelType modelType)
    {
        var air = AirState(FlyOneMeasuredTick(modelType).State);

        air.GroundSpeedMps.Should().BeGreaterThan(air.AirspeedMps!.Value);
    }

    /// <summary>
    /// Course over ground is derived from the ground velocity, so a crosswind moves it away from
    /// the direction the airframe is actually being driven.
    /// </summary>
    /// <param name="modelType">Flight model the room is configured for.</param>
    [Theory]
    [InlineData(FlightModelType.Kinematic)]
    [InlineData(FlightModelType.Quadrotor)]
    public void Course_Over_Ground_Follows_The_Ground_Velocity(FlightModelType modelType)
    {
        var (state, previousPosition, currentPosition) = FlyOneMeasuredTick(modelType);

        var groundVelocity = (currentPosition - previousPosition) / (float)Dt;
        double expectedCourse = CoordinateFrames.BearingFromEusVector(groundVelocity);

        AirState(state).CourseOverGroundRad.Should().BeApproximately(expectedCourse, 1e-2);
    }

    /// <summary>
    /// The kinematic model's stored velocity is air-relative, which is why it cannot be published
    /// as the ground velocity.
    /// </summary>
    /// <remarks>
    /// Pins the SDK behaviour the fix depends on. Should a future SDK bump change the integrator
    /// to fold wind into the stored velocity, this fails first and names the reason, instead of
    /// the telemetry quietly gaining a wind-sized bias again.
    /// </remarks>
    [Fact]
    public void The_Kinematic_Model_Stores_An_Air_Relative_Velocity()
    {
        var drone = Spawn(FlightModelType.Kinematic);
        var before = drone.FlightModel.State.Position;
        drone.Step(Dt, Wind);
        var after = drone.FlightModel.State.Position;

        var measured = (after - before) / (float)Dt;
        var stored = drone.FlightModel.State.Velocity;

        (measured - stored).Length().Should().BeApproximately(Wind.Length(), PositionDeltaToleranceMps,
            "the integrator adds wind*dt to position without ever storing it");
    }

    /// <summary>
    /// The quadrotor model's stored velocity is already the ground velocity, because wind enters
    /// it as a force rather than as a position offset.
    /// </summary>
    [Fact]
    public void The_Quadrotor_Model_Stores_A_Ground_Velocity()
    {
        var drone = Spawn(FlightModelType.Quadrotor);
        var before = drone.FlightModel.State.Position;
        drone.Step(Dt, Wind);
        var after = drone.FlightModel.State.Position;

        var measured = (after - before) / (float)Dt;

        (measured - drone.FlightModel.State.Velocity).Length().Should().BeLessThan(PositionDeltaToleranceMps);
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    /// <summary>Creates a drone cruising level under a long-range waypoint command.</summary>
    /// <param name="modelType">Flight model to back the drone with.</param>
    /// <returns>A drone that has been commanded but not yet stepped.</returns>
    private static SimulatedDrone Spawn(FlightModelType modelType)
    {
        var drone = new SimulatedDrone(
            "air-1", new Vector3(0f, CruiseAltitudeM, 0f), modelType);

        // A target far enough away that neither model's arrival threshold is reached, so the
        // commanded velocity stays constant and the measured tick is not a decelerating one.
        drone.SendCommand(FlightCommand.GoTo(new Vector3(5000f, CruiseAltitudeM, 0f)));
        return drone;
    }

    /// <summary>Flies a drone to a settled cruise, then captures across one measured tick.</summary>
    /// <param name="modelType">Flight model to back the drone with.</param>
    /// <returns>The captured state, and the positions bracketing the measured tick.</returns>
    private static (AssetState State, Vector3 Previous, Vector3 Current) FlyOneMeasuredTick(
        FlightModelType modelType)
    {
        var drone = Spawn(modelType);
        var environment = new ConstantEnvironment(Wind);
        var asset = new AirAsset(
            drone, AssetProfiles.Create(drone.Id, VehicleClass.Multirotor));

        for (int i = 0; i < SettlingSteps; i++)
        {
            drone.Step(Dt, Wind);
        }

        var previous = drone.FlightModel.State.Position;
        drone.Step(Dt, Wind);
        var current = drone.FlightModel.State.Position;

        double simulationTime = (SettlingSteps + 1) * Dt;
        var context = new AssetCaptureContext(
            Environment: environment,
            SimulationTimeSeconds: simulationTime,
            Tick: SettlingSteps + 1,
            SourceTime: Epoch.AddSeconds(simulationTime),
            ReceiveTime: Epoch.AddSeconds(simulationTime),
            Origin: null);

        return (asset.Capture(context), previous, current);
    }

    /// <summary>Narrows a captured state's domain extension to its air form.</summary>
    /// <param name="state">State captured from an air asset.</param>
    /// <returns>The air-domain state.</returns>
    private static AirDomainState AirState(AssetState state) =>
        state.DomainState.Should().BeOfType<AirDomainState>().Subject;

    /// <summary>A windy but otherwise featureless atmosphere over flat, dry ground.</summary>
    /// <remarks>
    /// Constant in space and time so the wind the integrator was handed and the wind the capture
    /// samples are the same vector by construction. That removes the only term that could explain
    /// a discrepancy other than the one under test.
    /// </remarks>
    private sealed class ConstantEnvironment : IEnvironmentSampler
    {
        private readonly Vector3 _wind;

        /// <summary>Creates an environment with a uniform wind.</summary>
        /// <param name="wind">Wind velocity, in metres per second, in the scene frame.</param>
        public ConstantEnvironment(Vector3 wind)
        {
            _wind = wind;
            Wind = new ConstantWind(wind);
        }

        /// <inheritdoc />
        /// <remarks>Far below the terrain, so nothing in the scene reads as water.</remarks>
        public double SeaLevelM => -1000.0;

        /// <inheritdoc />
        public IWindField Wind { get; }

        /// <inheritdoc />
        public double GetElevation(double x, double z) => 0.0;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) =>
            new(
                PositionEus: positionEus,
                WindEus: _wind,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: Vector3.Zero,
                TerrainElevationM: 0.0,
                TerrainNormalEus: Vector3.UnitY,
                SurfaceMaterial: SurfaceType.BareGround,
                WaterSurfaceElevationM: null,
                BathymetricElevationM: null,
                Zones: []);
    }

    /// <summary>A uniform wind field with clear air.</summary>
    private sealed class ConstantWind : IWindField
    {
        private readonly Vector3 _wind;

        /// <summary>Creates a uniform wind field.</summary>
        /// <param name="wind">Wind velocity, in metres per second, in the scene frame.</param>
        public ConstantWind(Vector3 wind) => _wind = wind;

        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => _wind;
    }
}
