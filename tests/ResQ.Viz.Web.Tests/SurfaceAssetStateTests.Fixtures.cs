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
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Fixtures and helpers for <see cref="SurfaceAssetStateTests"/>.
/// </summary>
/// <remarks>
/// Split out so the assertions file reads as a list of contracts, following the arrangement
/// <see cref="GroundAssetStateTests"/> established. Nothing here reads a clock, sleeps, or draws
/// from an unseeded generator: the timestep is a literal, both timestamps are literals, the
/// analytic basin's depth, current and wind are constants, and the whole-world cases pin the
/// seed, the epoch and the only wall clock in the pipeline. That is what lets a replay hash be a
/// genuine determinism check rather than a flake waiting to happen.
/// <para>
/// The whole-world cases run on the <c>coastal</c> terrain preset, the only preset whose water
/// surface sits above the datum, and stage their assets on the coordinates the shipped
/// <c>coastal-search</c> and <c>coastal-transit</c> presets already document as deep water and
/// as dry, gentle shore. Reusing those points rather than inventing new ones means a change that
/// moves the bathymetry breaks the presets and these tests together, instead of leaving one of
/// them quietly staging a hull on a beach.
/// </para>
/// </remarks>
public sealed partial class SurfaceAssetStateTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed every world and generator in this suite uses, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Steps a replayed run advances, long enough to exercise every command in the log.</summary>
    private const int ReplaySteps = 480;

    /// <summary>
    /// Steps taken before measuring a vessel under helm: long enough for the hull to have way on
    /// and be answering, short enough that it is still mid-turn.
    /// </summary>
    /// <remarks>
    /// Three seconds against a six-second surge constant and a two-and-a-half second yaw
    /// constant. A vessel that had already steadied on its new course would report a yaw rate and
    /// a sway of nearly zero, and a field that reads zero because the manoeuvre finished proves
    /// nothing at all about whether it is wired up.
    /// </remarks>
    private const int TurningSteps = 180;

    /// <summary>Steps a vessel is left unattended for, to observe drift and the absence of repeats.</summary>
    private const int DriftSteps = 600;

    /// <summary>
    /// Tolerance, in metres per second, for comparing a published velocity against one recovered
    /// by differencing positions.
    /// </summary>
    /// <remarks>
    /// Set by single-precision cancellation rather than by physics, exactly as in
    /// <see cref="GroundAssetStateTests"/>: positions are <c>float</c> and reach hundreds of
    /// metres, so their difference loses a few units in the last place, and dividing by a 1/60 s
    /// timestep multiplies that by sixty.
    /// </remarks>
    private const float VelocityToleranceMps = 5e-3f;

    /// <summary>Tolerance in radians for angles both sides of a comparison compute in closed form.</summary>
    private const double AngleToleranceRad = 1e-4;

    /// <summary>Tolerance in metres per second for a quantity recomputed from the same constants.</summary>
    private const double DerivedToleranceMps = 1e-6;

    /// <summary>Heading due north, in radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Heading due east, in radians clockwise from true north.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>Heading due west, in radians clockwise from true north.</summary>
    private const double West = 3.0 * Math.PI / 2.0;

    /// <summary>Terrain preset the whole-world cases run on; the only one with water above the datum.</summary>
    private const string CoastalPreset = "coastal";

    /// <summary>Water column in the deep analytic basin, in metres.</summary>
    /// <remarks>
    /// Comfortably past twice the workboat's safe margin above its draft, so the hull classifies
    /// as <see cref="UnderKeelClearanceClass.Safe"/> and no derate is in force. A case about what
    /// a vessel publishes must not be quietly running against a shoal.
    /// </remarks>
    private const double BasinDepthM = 8.0;

    /// <summary>Water column in the shoal basin, in metres. Less than the workboat's draft.</summary>
    private const double ShoalDepthM = 0.40;

    /// <summary>Water-surface elevation of the analytic basins, in metres.</summary>
    /// <remarks>
    /// Deliberately not zero. A hull's height is the water surface it floats on, and a basin at
    /// the datum could not tell a published elevation from an unset field.
    /// </remarks>
    private const double BasinSeaLevelM = 2.0;

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Frozen receive-time stamp, so a capture is a function of its inputs alone.</summary>
    private static readonly DateTimeOffset WallClockUtc = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    /// <summary>Spawn point used by the analytic-basin cases, in metres.</summary>
    /// <remarks>The basin is uniform everywhere, so the scene origin is as good as anywhere.</remarks>
    private static readonly Vector3 SyntheticSpawn = Vector3.Zero;

    /// <summary>Steady set of the analytic basin's current, in metres per second.</summary>
    private static readonly Vector3 SteadySetEus = new(0.30f, 0f, 0.20f);

    /// <summary>Steady breeze over the analytic basin, in metres per second.</summary>
    private static readonly Vector3 SteadyBreezeEus = new(4.0f, 0f, -3.0f);

    /// <summary>Destination for the analytic-basin transit: far astern, so the hull must wear round.</summary>
    /// <remarks>
    /// Offset off the exact reciprocal on purpose. A target dead astern sits on the singularity
    /// where a shortest-turn calculation may legitimately go either way, and a case that pinned
    /// which way it went would be pinning an arbitrary tie-break rather than the guidance law.
    /// </remarks>
    private static readonly Vector3 SternwardTarget = new(-600f, 0f, -50f);

    /// <summary>Scene-frame spawn point for the whole-world vessel, from the shipped transit preset.</summary>
    private static readonly Vector3 VesselSpawn = new(-1000f, 0f, -250f);

    /// <summary>Scene-frame spawn point for the second whole-world vessel, from the same preset.</summary>
    private static readonly Vector3 SecondVesselSpawn = new(-1000f, 0f, -320f);

    /// <summary>Scene-frame spawn point for the unattended vessel, from the shipped search preset.</summary>
    private static readonly Vector3 DriftingVesselSpawn = new(-775f, 0f, -100f);

    /// <summary>Scene-frame spawn point for the rover: the search preset's dry, gentle shore.</summary>
    private static readonly Vector3 RoverSpawn = new(-525f, 0f, 0f);

    /// <summary>Scene-frame launch point for the drone, from the same preset's overwatch station.</summary>
    private static readonly Vector3 DroneSpawn = new(-700f, 140f, -180f);

    /// <summary>Waypoint the drone is sent to, far enough never to be reached inside a run.</summary>
    private static readonly Vector3 DroneTarget = new(-560f, 130f, 60f);

    // ─── Building and driving one vessel ────────────────────────────────────

    /// <summary>Floats a vessel on an analytic basin and returns a rig that can step it.</summary>
    /// <param name="water">Basin to float on and integrate over.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <param name="profile">Hull envelope, or null for the shipped workboat.</param>
    /// <param name="extraCapabilities">Capabilities to add to the shipped descriptor's mask.</param>
    /// <param name="assetId">Identifier for the vessel.</param>
    /// <returns>A rig holding the asset, its basin and its tick counter.</returns>
    private static VesselRig Rig(
        OpenWater water,
        double headingRad = North,
        SurfaceProfile? profile = null,
        AssetCapability extraCapabilities = AssetCapability.None,
        string assetId = "usv-1") =>
        new(water, headingRad, profile, extraCapabilities, assetId);

    /// <summary>Narrows a captured state's domain extension to its surface form.</summary>
    /// <param name="state">State captured from a surface asset.</param>
    /// <returns>The surface-domain state.</returns>
    private static SurfaceDomainState SurfaceState(AssetState state) =>
        state.DomainState.Should().BeOfType<SurfaceDomainState>().Subject;

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

    /// <summary>A validated transit command addressed to one vessel.</summary>
    /// <param name="assetId">Vessel the command is addressed to.</param>
    /// <param name="targetEus">Destination in the scene frame.</param>
    /// <param name="speedMps">Cruise speed, or null for the hull's default.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand TransitTo(
        string assetId, Vector3 targetEus, double? speedMps = null) =>
        new(
            Kind: AssetCommandKind.TransitTo,
            AssetId: assetId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity),
            SpeedMps: speedMps);

    /// <summary>A validated command that carries no target.</summary>
    /// <param name="assetId">Asset the command is addressed to.</param>
    /// <param name="kind">Command kind to issue.</param>
    /// <param name="speedMps">Speed the kind may carry, or null.</param>
    /// <param name="headingRad">Course or heading the kind may carry, or null.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(
        string assetId,
        AssetCommandKind kind,
        double? speedMps = null,
        double? headingRad = null) =>
        new(Kind: kind, AssetId: assetId, SpeedMps: speedMps, HeadingRad: headingRad);

    // ─── Whole-world fixtures ───────────────────────────────────────────────

    /// <summary>A coastal world with a fixed seed, a fixed epoch and a frozen wall clock.</summary>
    /// <remarks>
    /// Every source of non-determinism the world could otherwise reach is pinned: the SDK
    /// generator's seed, the epoch every source time is derived from, and the only wall clock in
    /// the pipeline. Terrain and weather are the real implementations, because a determinism
    /// claim made over a stubbed environment would prove nothing about the one that ships.
    /// <para>
    /// The water level is installed alongside the terrain preset, never separately. Every preset
    /// carries its own water level, and a world whose bed came from one preset and whose water
    /// came from another would float vessels on hillsides — the same coupling
    /// <see cref="AssetWorld.SetSeaLevelForPreset"/> exists to keep honest at runtime.
    /// </para>
    /// </remarks>
    /// <returns>A freshly constructed world holding no assets.</returns>
    private static AssetWorld CreateWorld()
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset(CoastalPreset);

        return new AssetWorld(
            terrain,
            new UpdatableWeatherSystem(new WeatherConfig()),
            new AssetWorldOptions(
                Simulation: new SimulationConfig { Seed = FixedSeed },
                WorldEpochUtc: WorldEpochUtc,
                WallClock: new FixedClock(WallClockUtc),
                SeaLevelM: SeaLevel.CoastalM));
    }

    /// <summary>Spawns a vessel into a world through the real surface factory.</summary>
    /// <param name="world">World to spawn into; the vessel binds to its environment sampler.</param>
    /// <param name="assetId">Identifier for the vessel.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <param name="spawnEus">Scene-frame spawn point, or null for <see cref="VesselSpawn"/>.</param>
    /// <returns>The registered asset.</returns>
    private static ISimulatedAsset AddVessel(
        AssetWorld world,
        string assetId,
        double headingRad = North,
        Vector3? spawnEus = null)
    {
        var asset = new SurfaceAssetFactory(world.Environment).Create(new AssetSpawnPlan(
            AssetId: assetId,
            VehicleClass: VehicleClass.SurfaceVessel,
            Descriptor: AssetProfiles.Create(assetId, VehicleClass.SurfaceVessel),
            PositionEus: spawnEus ?? VesselSpawn,
            HeadingRad: headingRad));

        world.AddAsset(asset);
        return asset;
    }

    /// <summary>Spawns a rover into a world through the real ground factory.</summary>
    /// <param name="world">World to spawn into; the rover binds to its environment sampler.</param>
    /// <param name="assetId">Identifier for the rover.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <returns>The registered asset.</returns>
    private static ISimulatedAsset AddRover(
        AssetWorld world, string assetId, double headingRad = West)
    {
        var asset = new GroundAssetFactory(world.Environment).Create(new AssetSpawnPlan(
            AssetId: assetId,
            VehicleClass: VehicleClass.AckermannRover,
            Descriptor: AssetProfiles.Create(assetId, VehicleClass.AckermannRover),
            PositionEus: RoverSpawn,
            HeadingRad: headingRad));

        world.AddAsset(asset);
        return asset;
    }

    /// <summary>Adds the overwatch drone and sends it on its long leg.</summary>
    /// <param name="world">World to spawn into.</param>
    /// <param name="assetId">Identifier for the drone.</param>
    private static void AddDrone(AssetWorld world, string assetId)
    {
        world.AddDrone(assetId, DroneSpawn);
        world.Drones[^1].SendCommand(FlightCommand.GoTo(DroneTarget));
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

    /// <summary>The captured state of one named asset.</summary>
    /// <param name="world">World to read.</param>
    /// <param name="assetId">Asset to find.</param>
    /// <returns>That asset's state.</returns>
    private static AssetState StateOf(AssetWorld world, string assetId) =>
        world.States.Should().ContainSingle(state => state.AssetId == assetId).Which;

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

    /// <summary>Renders a domain extension for the digest.</summary>
    /// <remarks>
    /// The surface arm is exhaustive because this suite's whole subject is what a vessel
    /// publishes. The air and ground arms carry the fields their own domains diverge on; the rest
    /// of a drone's or a rover's divergence already reaches the digest through the pose, the
    /// twist, the power and the health summary rendered beside it.
    /// </remarks>
    /// <param name="domainState">Domain extension to render.</param>
    /// <returns>A canonical, culture-invariant rendering.</returns>
    private static string Text(IAssetDomainState? domainState) => domainState switch
    {
        SurfaceDomainState surface => string.Join(
            ',',
            surface.Type,
            Text(surface.HeadingRad),
            Text(surface.CourseOverGroundRad),
            Text(surface.SpeedOverGroundMps),
            Text(surface.SpeedThroughWaterMps),
            Text(surface.SurgeMps),
            Text(surface.SwayMps),
            Text(surface.YawRateRadPerSec),
            Text(surface.WaterSurfaceElevationM),
            Text(surface.WaterDepthM),
            Text(surface.DraftM),
            Text(surface.UnderKeelClearanceM),
            surface.HasUnsafeUnderKeelClearance,
            Text(surface.CurrentSpeedMps),
            Text(surface.CurrentDirectionRad),
            Text(surface.WindSpeedMps),
            Text(surface.WindDirectionRad),
            surface.IsInsideWaterMask,
            surface.LinkLossBehavior,
            Text(surface.PositionUncertaintyGrowthMps),
            Text(surface.StationKeep),
            Text(surface.HeaveM),
            Text(surface.RollRad),
            Text(surface.PitchRad)),

        GroundDomainState ground => string.Join(
            ',',
            ground.Type,
            ground.IsMoving,
            Text(ground.HeadingRad),
            Text(ground.GroundSpeedMps),
            Text(ground.SteeringAngleRad),
            Text(ground.RollRad),
            Text(ground.PitchRad),
            Text(ground.TerrainElevationM),
            ground.SurfaceType,
            Text(ground.DeratedSpeedLimitMps),
            ground.IsImmobilised,
            Text(ground.PositionUncertaintyGrowthMps)),

        AirDomainState air => string.Join(
            ',',
            air.Type,
            air.IsAirborne,
            Text(air.HeadingRad),
            Text(air.GroundSpeedMps),
            Text(air.ClimbRateMps),
            Text(air.AltitudeAboveGroundM),
            Text(air.AltitudeMslM),
            Text(air.AirspeedMps),
            Text(air.PositionUncertaintyGrowthMps)),

        _ => "-",
    };

    /// <summary>Renders a station-keeping goal and its quality.</summary>
    /// <param name="station">Station-keep state to render, or null when no hold is engaged.</param>
    /// <returns>A canonical, culture-invariant rendering.</returns>
    private static string Text(StationKeepState? station) => station is null
        ? "-"
        : string.Join(
            ';',
            station.IsEngaged,
            station.Target is { } target ? Text(target.Position) : "-",
            Text(station.ToleranceRadiusM),
            station.HeadingPolicy,
            Text(station.HeadingSetpointRad),
            Text(station.PositionErrorM),
            station.IsDegraded,
            station.DegradedReason ?? "-");

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

    /// <summary>One vessel on an analytic basin, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what <see cref="AssetWorld"/> does per step — sample the environment at the
    /// asset's pre-step position, build a context, call <see cref="IStepDrivenAsset.Step"/> —
    /// without a world, so a case can be stated in literals and every quantity in it is exactly
    /// known. The peer buffer is empty because no surface behaviour reads it, and the generator
    /// is seeded because the contract says an asset may draw only from the one on the context.
    /// </remarks>
    private sealed class VesselRig
    {
        private readonly Random _random = new(FixedSeed);

        /// <summary>Floats a vessel and prepares it to be stepped.</summary>
        /// <param name="water">Basin to float on.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        /// <param name="profile">Hull envelope, or null for the shipped workboat.</param>
        /// <param name="extraCapabilities">Capabilities to add to the shipped descriptor's mask.</param>
        /// <param name="assetId">Identifier for the vessel.</param>
        public VesselRig(
            OpenWater water,
            double headingRad,
            SurfaceProfile? profile,
            AssetCapability extraCapabilities,
            string assetId)
        {
            Water = water;
            Profile = profile ?? SurfaceProfile.SurfaceVessel;
            AssetId = assetId;

            var shipped = AssetProfiles.Create(assetId, VehicleClass.SurfaceVessel);
            Descriptor = extraCapabilities == AssetCapability.None
                ? shipped
                : shipped with { Capabilities = shipped.Capabilities | extraCapabilities };

            Asset = new SurfaceAsset(
                Descriptor, SurfaceDynamics.For(Profile), water, SyntheticSpawn, headingRad);
        }

        /// <summary>The vessel under test.</summary>
        public SurfaceAsset Asset { get; }

        /// <summary>Envelope the vessel is integrated within.</summary>
        public SurfaceProfile Profile { get; }

        /// <summary>Descriptor the vessel publishes.</summary>
        public AssetDescriptor Descriptor { get; }

        /// <summary>Identifier commands are addressed to.</summary>
        public string AssetId { get; }

        /// <summary>Water the vessel is floating on.</summary>
        public OpenWater Water { get; }

        /// <summary>World steps taken so far.</summary>
        public long Tick { get; private set; }

        /// <summary>Simulation time at the current tick, in seconds.</summary>
        public double SimulationTimeSeconds => Tick * Dt;

        /// <summary>Advances the vessel by exactly one step.</summary>
        /// <returns>The scene-frame position the vessel held before the step.</returns>
        public Vector3 Step()
        {
            var before = Asset.PositionEus;
            Tick++;

            Asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: SimulationTimeSeconds,
                Tick: Tick,
                Environment: Water.Sample(before, Descriptor.Dimensions.FootprintRadiusM),
                Peers: [],
                Random: _random));

            return before;
        }

        /// <summary>Advances the vessel by a fixed number of steps.</summary>
        /// <param name="steps">Number of steps.</param>
        public void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                Step();
            }
        }

        /// <summary>Projects the vessel onto the wire at the current tick.</summary>
        /// <remarks>
        /// Both timestamps are derived from the fixed epoch rather than sampled, so two captures
        /// at the same tick are handed identical contexts and any difference between the results
        /// is the asset's own doing.
        /// </remarks>
        /// <returns>The captured state.</returns>
        public AssetState Capture() => Asset.Capture(new AssetCaptureContext(
            Environment: Water,
            SimulationTimeSeconds: SimulationTimeSeconds,
            Tick: Tick,
            SourceTime: WorldEpochUtc.AddSeconds(SimulationTimeSeconds),
            ReceiveTime: WallClockUtc,
            Origin: null));

        /// <summary>Issues a command and asserts it was accepted.</summary>
        /// <param name="command">Command to issue.</param>
        public void Send(SimulatedAssetCommand command) =>
            Asset.Apply(command).IsAccepted.Should().BeTrue(
                $"'{command.Kind}' must be accepted for this case to mean anything");
    }

    /// <summary>A basin of uniform depth, with a steady set and a steady breeze.</summary>
    /// <remarks>
    /// Deliberately not the procedural terrain. Depth, current and wind are the three inputs
    /// every quantity a vessel publishes is derived from, and a height field whose derivative is
    /// known only numerically can confirm that a published depth is plausible but never that it
    /// is the right one.
    /// <para>
    /// Uniform in every direction, so the environment under the hull is unchanged from step to
    /// step however far the vessel drifts. That matters for more than convenience: a vessel
    /// re-baselines when the world beneath it is replaced, and a basin whose depth varied with
    /// position would make every ordinary step look like a terrain-preset switch.
    /// </para>
    /// </remarks>
    private sealed class OpenWater : IEnvironmentSampler
    {
        private readonly double _bedElevationM;
        private readonly IReadOnlyList<EnvironmentZone> _zones;

        /// <summary>Builds a basin.</summary>
        /// <param name="depthM">Water column everywhere, in metres.</param>
        /// <param name="currentEus">Surface current everywhere, in metres per second.</param>
        /// <param name="windEus">Wind everywhere, in metres per second.</param>
        /// <param name="seaLevelM">Water-surface elevation, in metres.</param>
        /// <param name="zones">Zones applying everywhere, or null for none.</param>
        public OpenWater(
            double depthM = BasinDepthM,
            Vector3? currentEus = null,
            Vector3? windEus = null,
            double seaLevelM = BasinSeaLevelM,
            IReadOnlyList<EnvironmentZone>? zones = null)
        {
            DepthM = depthM;
            _bedElevationM = seaLevelM - depthM;
            _zones = zones ?? [];
            SeaLevelM = seaLevelM;
            CurrentEus = currentEus ?? Vector3.Zero;
            WindEus = windEus ?? Vector3.Zero;
            Wind = new SteadyAir(WindEus);
        }

        /// <inheritdoc />
        public double SeaLevelM { get; }

        /// <inheritdoc />
        public IWindField Wind { get; }

        /// <summary>Surface current everywhere in this basin, in metres per second.</summary>
        public Vector3 CurrentEus { get; }

        /// <summary>Wind everywhere over this basin, in metres per second.</summary>
        public Vector3 WindEus { get; }

        /// <summary>Water column everywhere in this basin, in metres.</summary>
        public double DepthM { get; }

        /// <inheritdoc />
        public double GetElevation(double x, double z) => _bedElevationM;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) =>
            new(
                PositionEus: positionEus,
                WindEus: WindEus,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: CurrentEus,
                TerrainElevationM: _bedElevationM,
                TerrainNormalEus: Vector3.UnitY,
                SurfaceMaterial: SurfaceType.Water,
                WaterSurfaceElevationM: SeaLevelM,
                BathymetricElevationM: _bedElevationM,
                Zones: _zones);
    }

    /// <summary>A steady, clear atmosphere. Constant everywhere, so a case can state its wind.</summary>
    private sealed class SteadyAir : IWindField
    {
        private readonly Vector3 _wind;

        /// <summary>Fixes the wind.</summary>
        /// <param name="windEus">Wind velocity every query returns, in metres per second.</param>
        public SteadyAir(Vector3 windEus) => _wind = windEus;

        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => _wind;
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
