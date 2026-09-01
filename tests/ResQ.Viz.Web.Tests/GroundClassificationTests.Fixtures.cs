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
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;

namespace ResQ.Viz.Web.Tests;

// The analytic plane every case is driven over, the rover rig that steps one vehicle across it,
// and the small helpers the assertions read through. Split from the cases the way the other ground
// suites are split: reading what a case asserts should not mean scrolling past how its ground was
// built. The type's summary lives on the primary declaration in GroundClassificationTests.cs.
public sealed partial class GroundClassificationTests
{
    /// <summary>An environment sample on a plane rising due east, with no zones and no wind.</summary>
    /// <param name="gradientRad">Uphill gradient towards the east, in radians.</param>
    /// <param name="surface">Surface material reported everywhere on the plane.</param>
    /// <returns>A sample sitting on the plane at <see cref="Probe"/>.</returns>
    private static EnvironmentSample Plane(
        double gradientRad, SurfaceType surface = SurfaceType.BareGround) =>
        new EastwardSlope(gradientRad, surface).Sample(Probe, normalSpacingM: 1.0);

    /// <summary>Resolves contact from a fresh filter, so the measured normal passes through unsmoothed.</summary>
    /// <param name="profile">Platform to resolve for.</param>
    /// <param name="sample">Environment at the point.</param>
    /// <param name="headingRad">Direction of travel, radians clockwise from true north.</param>
    /// <returns>The resolved contact.</returns>
    private static TerrainContactState Resolve(
        GroundProfile profile, EnvironmentSample sample, double headingRad) =>
        TerrainContact.Resolve(
            sample.PositionEus, headingRad, profile, sample,
            deltaSeconds: 0.0, TerrainNormalFilter.Uninitialised).Contact;

    /// <summary>A validated drive command addressed to one rover.</summary>
    /// <param name="assetId">Rover the command is addressed to.</param>
    /// <param name="targetEus">Destination in the scene frame.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand DriveTo(string assetId, Vector3 targetEus) => new(
        Kind: AssetCommandKind.DriveTo,
        AssetId: assetId,
        Target: new FramedPose(
            CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity));

    /// <summary>Ground that is an exact plane tilted about the north–south axis.</summary>
    /// <remarks>
    /// <c>h(x, z) = tan(gradient) * x</c>, so the gradient is constant and the unit normal is
    /// closed-form. Heading east reads the whole gradient as grade and nothing as cross-slope;
    /// heading north reads it exactly the other way round, which is the only terrain shape that
    /// separates the two with certainty.
    /// </remarks>
    /// <param name="gradientRad">Uphill gradient towards the east, in radians.</param>
    /// <param name="material">Surface classification reported everywhere on the plane.</param>
    private sealed class EastwardSlope(
        double gradientRad, SurfaceType material = SurfaceType.BareGround) : IEnvironmentSampler
    {
        private readonly double _riseEastPerM = Math.Tan(gradientRad);
        private readonly Vector3 _normal =
            Vector3.Normalize(new Vector3((float)-Math.Tan(gradientRad), 1f, 0f));

        /// <inheritdoc />
        /// <remarks>Far below the plane, so nothing reads as water unless the material says so.</remarks>
        public double SeaLevelM => -1000.0;

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <inheritdoc />
        public double GetElevation(double x, double z) => _riseEastPerM * x;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => _normal;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM)
        {
            double elevation = GetElevation(positionEus.X, positionEus.Z);
            bool isWater = material == SurfaceType.Water;

            return new EnvironmentSample(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: Vector3.Zero,
                TerrainElevationM: elevation,
                TerrainNormalEus: _normal,
                SurfaceMaterial: material,
                WaterSurfaceElevationM: isWater ? elevation + 1.0 : null,
                BathymetricElevationM: isWater ? elevation : null,
                Zones: []);
        }
    }

    /// <summary>Still, clear air. Ground contact reads no wind, so this only has to be constant.</summary>
    private sealed class StillAir : IWindField
    {
        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }

    /// <summary>One rover on an analytic plane, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what the world does per step — sample the environment at the asset's pre-step
    /// position, build a context, step — without a world, so every quantity in a case is exactly
    /// known. The generator is seeded because the contract says an asset may draw only from the
    /// one on the context.
    /// </remarks>
    private sealed class RoverRig
    {
        private const string RoverId = "ugv-bank";

        private readonly Random _random = new(20260830);
        private readonly EastwardSlope _ground;

        private long _tick;

        /// <summary>Builds and settles an Ackermann rover on a plane.</summary>
        /// <param name="ground">Terrain to settle onto.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        public RoverRig(EastwardSlope ground, double headingRad)
        {
            _ground = ground;
            Profile = GroundProfile.AckermannRover;
            Asset = new GroundAsset(
                AssetProfiles.Create(RoverId, VehicleClass.AckermannRover),
                GroundDynamics.For(Profile),
                ground,
                Vector3.Zero,
                headingRad);
        }

        /// <summary>The rover under test.</summary>
        public GroundAsset Asset { get; }

        /// <summary>Envelope the rover is integrated within.</summary>
        public GroundProfile Profile { get; }

        /// <summary>Advances the rover by a fixed number of steps.</summary>
        /// <param name="steps">Number of steps.</param>
        public void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                _tick++;
                Asset.Step(new AssetStepContext(
                    DeltaSeconds: Dt,
                    SimulationTimeSeconds: _tick * Dt,
                    Tick: _tick,
                    Environment: _ground.Sample(
                        Asset.PositionEus, GroundContactGeometry.NormalSpacingM(Profile)),
                    Peers: [],
                    Random: _random));
            }
        }

        /// <summary>Projects the rover onto the wire at the current tick.</summary>
        /// <returns>The captured state.</returns>
        public AssetState Capture() => Asset.Capture(new AssetCaptureContext(
            Environment: _ground,
            SimulationTimeSeconds: _tick * Dt,
            Tick: _tick,
            SourceTime: WorldEpochUtc.AddSeconds(_tick * Dt),
            ReceiveTime: WorldEpochUtc.AddMinutes(5.0),
            Origin: null));
    }
}
