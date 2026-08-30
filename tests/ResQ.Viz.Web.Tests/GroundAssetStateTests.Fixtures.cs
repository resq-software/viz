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

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Fixtures and helpers for <see cref="GroundAssetStateTests"/>.
/// </summary>
/// <remarks>
/// Split out so the assertions file stays readable as a list of contracts, following the same
/// arrangement as <see cref="AssetContractTests"/>. Nothing here reads a clock, sleeps, or draws
/// from an unseeded generator: the timestep is a literal, both timestamps are literals, and the
/// terrain is an analytic plane whose elevation and normal are closed-form. That is what lets a
/// replay hash be a genuine determinism check rather than a flake waiting to happen.
/// </remarks>
public sealed partial class GroundAssetStateTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed every world and generator in this suite uses, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Steps a replayed run advances, long enough to exercise every command in the log.</summary>
    private const int ReplaySteps = 480;

    /// <summary>
    /// Tolerance, in metres per second, for comparing a published velocity against one recovered
    /// by differencing positions.
    /// </summary>
    /// <remarks>
    /// Set by single-precision cancellation rather than by physics, exactly as in
    /// <see cref="AirAssetTelemetryTests"/>: positions are <c>float</c> and reach hundreds of
    /// metres, so their difference loses a few units in the last place, and dividing by a 1/60 s
    /// timestep multiplies that by sixty.
    /// </remarks>
    private const float VelocityToleranceMps = 5e-3f;

    /// <summary>Tolerance in radians for angles the contact solver resolves in closed form.</summary>
    private const double AngleToleranceRad = 1e-4;

    /// <summary>Heading due north, in radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Heading due east, in radians clockwise from true north.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Frozen receive-time stamp, so a capture is a function of its inputs alone.</summary>
    private static readonly DateTimeOffset WallClockUtc = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    /// <summary>Spawn point used by the analytic-terrain cases, in metres.</summary>
    /// <remarks>The plane is defined everywhere, so the scene origin is as good as anywhere.</remarks>
    private static readonly Vector3 SyntheticSpawn = new(0f, 0f, 0f);

    /// <summary>Scene-frame spawn point used by the whole-world cases, in metres.</summary>
    /// <remarks>
    /// The alpine preset's east flank — the same ground <see cref="GroundScenarioTests"/> stages
    /// on, and therefore ground already known to sit well above the water surface and to settle a
    /// rover successfully.
    /// </remarks>
    private static readonly Vector3 RoverSpawn = new(640f, 0f, 300f);

    /// <summary>Scene-frame launch point for the drone in the non-perturbation case, in metres.</summary>
    private static readonly Vector3 DroneSpawn = new(640f, 130f, 300f);

    /// <summary>Long-range waypoint the drone is sent to, far enough never to be reached.</summary>
    private static readonly Vector3 DroneTarget = new(200f, 130f, -400f);

    // ─── Building and driving one rover ─────────────────────────────────────

    /// <summary>Places a rover on an analytic plane and returns a rig that can step it.</summary>
    /// <param name="ground">Terrain to settle onto and integrate over.</param>
    /// <param name="vehicleClass">Ground class to build; decides the motion model and the profile.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <param name="assetId">Identifier for the rover.</param>
    /// <returns>A rig holding the asset, its environment and its tick counter.</returns>
    private static RoverRig Rig(
        PlanarGround ground,
        VehicleClass vehicleClass = VehicleClass.AckermannRover,
        double headingRad = North,
        string assetId = "ugv-1") =>
        new(ground, vehicleClass, headingRad, assetId);

    /// <summary>Profile backing a ground vehicle class.</summary>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>The profile the dynamics for that class are built from.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The class has no ground profile.</exception>
    private static GroundProfile ProfileFor(VehicleClass vehicleClass) =>
        GroundProfile.ForVehicleClass(vehicleClass)
        ?? throw new ArgumentOutOfRangeException(
            nameof(vehicleClass), vehicleClass, "That class has no ground motion model.");

    /// <summary>Narrows a captured state's domain extension to its ground form.</summary>
    /// <param name="state">State captured from a ground asset.</param>
    /// <returns>The ground-domain state.</returns>
    private static GroundDomainState GroundState(AssetState state) =>
        state.DomainState.Should().BeOfType<GroundDomainState>().Subject;

    /// <summary>Yaw rate as published, recovered from the scene-frame angular twist.</summary>
    /// <remarks>
    /// Heading increases clockwise from north while scene yaw about <c>+Y</c> increases
    /// anticlockwise from it, so the wire carries the negated rate. Undoing that in one place
    /// keeps the sign convention from being restated — and mis-stated — per assertion.
    /// </remarks>
    /// <param name="state">Captured state to read.</param>
    /// <returns>Yaw rate in radians per second, positive to starboard.</returns>
    private static double PublishedYawRateRadPerSec(AssetState state) => -state.Twist.Angular.Y;

    /// <summary>Unit vector pointing along a heading, in the scene frame.</summary>
    /// <param name="headingRad">Heading in radians clockwise from true north.</param>
    /// <returns>A unit vector in <see cref="CoordinateFrame.LocalEus"/>.</returns>
    private static Vector3 HeadingVector(double headingRad) =>
        CoordinateFrames.BearingToEusVector(headingRad, 1.0);

    /// <summary>A validated drive command addressed to one rover.</summary>
    /// <param name="assetId">Rover the command is addressed to.</param>
    /// <param name="targetEus">Destination in the scene frame.</param>
    /// <param name="speedMps">Cruise speed, or null for the platform's default.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand DriveTo(
        string assetId, Vector3 targetEus, double? speedMps = null) =>
        new(
            Kind: AssetCommandKind.DriveTo,
            AssetId: assetId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity),
            SpeedMps: speedMps);

    /// <summary>A validated command that carries no target.</summary>
    /// <param name="assetId">Rover the command is addressed to.</param>
    /// <param name="kind">Command kind to issue.</param>
    /// <param name="speedMps">Speed the kind may carry, or null.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(
        string assetId, AssetCommandKind kind, double? speedMps = null) =>
        new(Kind: kind, AssetId: assetId, SpeedMps: speedMps);

    // ─── Whole-world fixtures ───────────────────────────────────────────────

    /// <summary>A world with a fixed seed, a fixed epoch and a frozen wall clock.</summary>
    /// <remarks>
    /// Every source of non-determinism the world could otherwise reach is pinned: the SDK
    /// generator's seed, the epoch every source time is derived from, and the only wall clock in
    /// the pipeline. Terrain and weather are the real implementations, because a determinism
    /// claim made over a stubbed environment would prove nothing about the one that ships.
    /// </remarks>
    /// <returns>A freshly constructed world holding no assets.</returns>
    private static AssetWorld CreateWorld() =>
        new(
            new TerrainNoiseService(),
            new UpdatableWeatherSystem(new WeatherConfig()),
            new AssetWorldOptions(
                Simulation: new SimulationConfig { Seed = FixedSeed },
                WorldEpochUtc: WorldEpochUtc,
                WallClock: new FixedClock(WallClockUtc)));

    /// <summary>Spawns a rover into a world through the real ground factory.</summary>
    /// <param name="world">World to spawn into; the rover binds to its environment sampler.</param>
    /// <param name="assetId">Identifier for the rover.</param>
    /// <param name="vehicleClass">Ground class to build.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <param name="spawnEus">Scene-frame spawn point, or null for <see cref="RoverSpawn"/>.</param>
    /// <returns>The registered asset.</returns>
    private static ISimulatedAsset AddRover(
        AssetWorld world,
        string assetId,
        VehicleClass vehicleClass = VehicleClass.AckermannRover,
        double headingRad = North,
        Vector3? spawnEus = null)
    {
        var asset = new GroundAssetFactory(world.Environment).Create(new AssetSpawnPlan(
            AssetId: assetId,
            VehicleClass: vehicleClass,
            Descriptor: AssetProfiles.Create(assetId, vehicleClass),
            PositionEus: spawnEus ?? RoverSpawn,
            HeadingRad: headingRad));

        world.AddAsset(asset);
        return asset;
    }

    /// <summary>Advances a world by a fixed number of steps at the SDK clock's timestep.</summary>
    /// <param name="world">World to advance.</param>
    /// <param name="steps">Number of steps.</param>
    private static void StepTimes(AssetWorld world, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            world.Step();
        }
    }

    // ─── Canonical hashing ──────────────────────────────────────────────────

    /// <summary>Hashes a captured state stream into one stable hex digest.</summary>
    /// <remarks>
    /// Every number goes through <c>G17</c> or <c>G9</c> round-trip formatting under the
    /// invariant culture, so the digest is taken over the exact bits the simulation produced
    /// rather than over a rounded rendering of them. A digest is the right shape for a replay
    /// assertion because it fails on <em>any</em> divergence — a hundredth step, a single field,
    /// a sign — instead of only on whichever fields a hand-written comparison happened to list.
    /// </remarks>
    /// <param name="states">States captured across a run, in the order they were captured.</param>
    /// <returns>An uppercase SHA-256 hex digest.</returns>
    private static string Hash(IEnumerable<AssetState> states)
    {
        var text = new StringBuilder();

        foreach (var state in states)
        {
            text.Append(state.AssetId).Append('|')
                .Append(state.SequenceNumber).Append('|')
                .Append(state.SourceTime.UtcTicks).Append('|')
                .Append(state.OperationalState).Append('|')
                .Append(state.Mode).Append('|')
                .Append(Text(state.Pose.Position)).Append('|')
                .Append(Text(state.Pose.Orientation)).Append('|')
                .Append(Text(state.Twist.Linear)).Append('|')
                .Append(Text(state.Twist.Angular)).Append('|')
                .Append(Text(state.Power.PercentRemaining)).Append('|')
                .Append(state.Health.Overall).Append('|')
                .Append(state.Health.Summary).Append('|')
                .Append(Text(state.DomainState))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Renders every field of a ground domain state, in declaration order.</summary>
    /// <param name="domainState">Domain extension to render; another arm renders as its type name.</param>
    /// <returns>A canonical, culture-invariant rendering.</returns>
    private static string Text(IAssetDomainState? domainState) => domainState switch
    {
        GroundDomainState ground => string.Join(
            ',',
            ground.Type,
            ground.IsMoving,
            Text(ground.HeadingRad),
            Text(ground.CourseOverGroundRad),
            Text(ground.GroundSpeedMps),
            Text(ground.SteeringAngleRad),
            Text(ground.RollRad),
            Text(ground.PitchRad),
            Text(ground.TerrainElevationM),
            Text(ground.SlopeRad),
            ground.SurfaceType,
            Text(ground.TractionCoefficient),
            Text(ground.DeratedSpeedLimitMps),
            Text(ground.RolloverRisk),
            ground.IsImmobilised,
            ground.LinkLossBehavior,
            Text(ground.PositionUncertaintyGrowthMps),
            ground.ImmobilisationReason ?? "-"),
        null => "-",
        _ => domainState.GetType().Name,
    };

    /// <summary>Round-trip rendering of a double.</summary>
    /// <param name="value">Value to render, or null.</param>
    /// <returns>A culture-invariant rendering that preserves every bit.</returns>
    private static string Text(double? value) =>
        value is { } number ? number.ToString("G17", CultureInfo.InvariantCulture) : "-";

    /// <summary>Round-trip rendering of a scene-frame vector.</summary>
    /// <param name="value">Vector to render.</param>
    /// <returns>A culture-invariant rendering that preserves every bit.</returns>
    private static string Text(Vector3 value) => string.Join(
        ',',
        value.X.ToString("G9", CultureInfo.InvariantCulture),
        value.Y.ToString("G9", CultureInfo.InvariantCulture),
        value.Z.ToString("G9", CultureInfo.InvariantCulture));

    /// <summary>Round-trip rendering of an attitude.</summary>
    /// <param name="value">Quaternion to render.</param>
    /// <returns>A culture-invariant rendering that preserves every bit.</returns>
    private static string Text(Quaternion value) => string.Join(
        ',',
        value.X.ToString("G9", CultureInfo.InvariantCulture),
        value.Y.ToString("G9", CultureInfo.InvariantCulture),
        value.Z.ToString("G9", CultureInfo.InvariantCulture),
        value.W.ToString("G9", CultureInfo.InvariantCulture));

    // ─── Test doubles ───────────────────────────────────────────────────────

    /// <summary>One rover on an analytic plane, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what <see cref="AssetWorld"/> does per step — sample the environment at the
    /// asset's pre-step position, build a context, call <see cref="IStepDrivenAsset.Step"/> —
    /// without a world, so a case can be stated in literals and every quantity in it is exactly
    /// known. The peer buffer is empty because no ground behaviour reads it, and the generator is
    /// seeded because the contract says an asset may draw only from the one on the context.
    /// </remarks>
    private sealed class RoverRig
    {
        private readonly Random _random = new(FixedSeed);

        /// <summary>Builds and settles a rover on a plane.</summary>
        /// <param name="ground">Terrain to settle onto.</param>
        /// <param name="vehicleClass">Ground class to build.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        /// <param name="assetId">Identifier for the rover.</param>
        public RoverRig(
            PlanarGround ground, VehicleClass vehicleClass, double headingRad, string assetId)
        {
            Ground = ground;
            Profile = ProfileFor(vehicleClass);
            Descriptor = AssetProfiles.Create(assetId, vehicleClass);
            Asset = new GroundAsset(
                Descriptor, GroundDynamics.For(Profile), ground, SyntheticSpawn, headingRad);
        }

        /// <summary>The rover under test.</summary>
        public GroundAsset Asset { get; }

        /// <summary>Envelope the rover is integrated within.</summary>
        public GroundProfile Profile { get; }

        /// <summary>Descriptor the rover publishes.</summary>
        public AssetDescriptor Descriptor { get; }

        /// <summary>Terrain the rover is driving over.</summary>
        public PlanarGround Ground { get; }

        /// <summary>World steps taken so far.</summary>
        public long Tick { get; private set; }

        /// <summary>Advances the rover by exactly one step.</summary>
        /// <returns>The scene-frame position the rover held before the step.</returns>
        public Vector3 Step()
        {
            var before = Asset.PositionEus;
            Tick++;

            Asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: Tick * Dt,
                Tick: Tick,
                Environment: Ground.Sample(before, Descriptor.Dimensions.FootprintRadiusM),
                Peers: [],
                Random: _random));

            return before;
        }

        /// <summary>Advances the rover by a fixed number of steps.</summary>
        /// <param name="steps">Number of steps.</param>
        public void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                Step();
            }
        }

        /// <summary>Projects the rover onto the wire at the current tick.</summary>
        /// <remarks>
        /// Both timestamps are derived from the fixed epoch rather than sampled, so two captures
        /// at the same tick are handed identical contexts and any difference between the results
        /// is the asset's own doing.
        /// </remarks>
        /// <returns>The captured state.</returns>
        public AssetState Capture() => Asset.Capture(new AssetCaptureContext(
            Environment: Ground,
            SimulationTimeSeconds: Tick * Dt,
            Tick: Tick,
            SourceTime: WorldEpochUtc.AddSeconds(Tick * Dt),
            ReceiveTime: WallClockUtc,
            Origin: null));

        /// <summary>Environment sample under the rover, as the contact solver sees it.</summary>
        /// <returns>The sample at the rover's current position.</returns>
        public EnvironmentSample SampleHere() =>
            Ground.Sample(Asset.PositionEus, GroundContactGeometry.NormalSpacingM(Profile));
    }

    /// <summary>Flat or uniformly sloping ground, with a closed-form elevation and normal.</summary>
    /// <remarks>
    /// A plane rising towards the east, so grade and cross-slope are decided purely by heading:
    /// a rover pointing east reads the whole gradient as grade and nothing as cross-slope, and
    /// one pointing north reads it exactly the other way round. That is the only terrain shape
    /// that separates the two with certainty, which is precisely what
    /// <see cref="GroundDomainState.PitchRad"/> and <see cref="GroundDomainState.RollRad"/> need
    /// proving about them.
    /// <para>
    /// Deliberately not the procedural terrain. A height field whose derivative is known only
    /// numerically can confirm that an angle is plausible; it cannot confirm that it is the right
    /// one, and it certainly cannot confirm which of two angles it landed in.
    /// </para>
    /// </remarks>
    private sealed class PlanarGround : IEnvironmentSampler
    {
        private readonly double _riseEastPerM;
        private readonly double _baseElevationM;
        private readonly SurfaceType _material;
        private readonly bool _isWater;

        /// <summary>Builds a plane.</summary>
        /// <param name="gradientRad">Uphill gradient towards the east, in radians. Zero is level.</param>
        /// <param name="material">Surface classification reported everywhere on the plane.</param>
        /// <param name="baseElevationM">Elevation at the scene origin, in metres.</param>
        public PlanarGround(
            double gradientRad = 0.0,
            SurfaceType material = SurfaceType.BareGround,
            double baseElevationM = 120.0)
        {
            GradientRad = gradientRad;
            _riseEastPerM = Math.Tan(gradientRad);
            _material = material;
            _baseElevationM = baseElevationM;
            _isWater = material == SurfaceType.Water;
            Normal = Vector3.Normalize(new Vector3((float)-_riseEastPerM, 1f, 0f));
        }

        /// <summary>Uphill gradient towards the east, in radians.</summary>
        public double GradientRad { get; }

        /// <summary>Unit up-normal of the plane, constant everywhere on it.</summary>
        public Vector3 Normal { get; }

        /// <inheritdoc />
        /// <remarks>Far below the plane, so nothing reads as water unless the material says so.</remarks>
        public double SeaLevelM => -1000.0;

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <inheritdoc />
        public double GetElevation(double x, double z) => _baseElevationM + (_riseEastPerM * x);

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Normal;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM)
        {
            double elevation = GetElevation(positionEus.X, positionEus.Z);

            return new EnvironmentSample(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: Vector3.Zero,
                TerrainElevationM: elevation,
                TerrainNormalEus: Normal,
                SurfaceMaterial: _material,
                WaterSurfaceElevationM: _isWater ? elevation + 1.0 : null,
                BathymetricElevationM: _isWater ? elevation : null,
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

    /// <summary>A wall clock that never moves, so a capture is a function of its inputs alone.</summary>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        /// <summary>Freezes the clock at an instant.</summary>
        /// <param name="now">Instant every read returns.</param>
        public FixedClock(DateTimeOffset now) => _now = now;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
