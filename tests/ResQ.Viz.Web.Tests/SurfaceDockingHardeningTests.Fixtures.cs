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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

// The fixtures half of SurfaceDockingHardeningTests: the constants every case is stated against,
// the helpers that build a plan or a state, and the analytic sea one vessel is floated on. Split
// from the assertions so that file reads as a list of contracts, the arrangement
// StationKeepDockTests and GroundWiringHardeningTests already use; the suite's summary lives on
// the primary declaration in SurfaceDockingHardeningTests.cs.
public sealed partial class SurfaceDockingHardeningTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed for every generator in this suite, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Identifier every vessel in this suite is spawned with.</summary>
    private const string RigId = "usv-1";

    /// <summary>Heading due north, in radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Range the unit cases place a berthing plan's entry point at, in metres.</summary>
    private const double EntryRangeM = 80.0;

    /// <summary>
    /// Wind blowing due east, in metres per second: on the beam of a vessel heading north, and
    /// hard enough that the leeway it puts on exceeds the terminal speed limit outright.
    /// </summary>
    /// <remarks>
    /// Chosen against the hull rather than picked. Every case using it also asserts the premise
    /// from <see cref="SurfaceProfile.LeewayFraction"/> rather than from this literal, so a
    /// profile change that moved the threshold fails the premise instead of silently turning the
    /// case into one about nothing.
    /// </remarks>
    private const double BeamWindMps = 9.0;

    /// <summary>Steps a case lets a drifting hull settle for: thirty seconds, twelve sway time constants.</summary>
    private const int SettleSteps = 1800;

    /// <summary>Steps a case gives a vessel to reach full cruise: thirty seconds, five surge time constants.</summary>
    private const int CruiseSteps = 1800;

    /// <summary>Ceiling on the steps a short, already-alongside berthing case may take.</summary>
    private const int ShortApproachSteps = 120;

    /// <summary>Steps a case runs a departure for before judging how fast it is: five seconds.</summary>
    private const int DepartureSteps = 300;

    /// <summary>Share of the hull's top speed a berth is left at, mirroring <c>SurfaceAsset</c>'s own figure.</summary>
    /// <remarks>
    /// Restated here rather than read from the asset, because the asset's constant is private and
    /// a case that read it could not tell a changed departure speed from a broken one.
    /// </remarks>
    private const double UndockSpeedFraction = 0.15;

    /// <summary>Distance a vessel stands off a berth when undocking, in hull lengths.</summary>
    private const double UndockStandoffLengths = 4.0;

    /// <summary>Range a vessel already lying alongside is berthed from, in metres.</summary>
    /// <remarks>Inside the terminal tolerance of the shipped hull, which is half a beam plus a metre.</remarks>
    private const double AlongsideRangeM = 1.8;

    /// <summary>Range a berth is commanded from in the calm-water departure case, in metres.</summary>
    private const double CloseBerthRangeM = 1.0;

    /// <summary>Length of the passage a case gives a vessel to reach full cruise, in metres.</summary>
    /// <remarks>Far enough that the arrival coast limit never binds inside the run.</remarks>
    private const double LongPassageM = 400.0;

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Frozen receive-time stamp, so a capture is a function of its inputs alone.</summary>
    private static readonly DateTimeOffset WallClockUtc = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    /// <summary>The shipped workboat: the hull every case here is flown on.</summary>
    private static readonly SurfaceProfile Hull = SurfaceProfile.SurfaceVessel;

    /// <summary>Motion model for <see cref="Hull"/>, used to resolve velocities for the guidance law.</summary>
    private static readonly ISurfaceDynamics Dynamics = SurfaceDynamics.For(Hull);

    // ─── Plans, poses and one step of the berthing machine ───────────────

    /// <summary>A berthing plan onto the scene origin, approached from due south.</summary>
    /// <remarks>
    /// The centreline runs due north, so a vessel on the scene <c>Z</c> axis reports a lateral
    /// offset of exactly zero and, heading north, a heading error of exactly zero. Every term a
    /// case asserts is then a function of one coordinate.
    /// </remarks>
    /// <returns>The plan, derived from the shipped hull's own dimensions.</returns>
    private static DockingPlan BerthingPlan() =>
        DockingPlan.For(Hull, new Vector3(0f, 0f, (float)EntryRangeM), Vector3.Zero);

    /// <summary>A vessel on the berthing centreline at a range, with stated body velocities.</summary>
    /// <param name="rangeM">Range to the berth at the scene origin, in metres.</param>
    /// <param name="surgeMps">Water-relative speed along the bow, in metres per second.</param>
    /// <param name="swayMps">Water-relative speed to starboard, in metres per second.</param>
    /// <returns>The state to hand the berthing machine.</returns>
    private static SurfaceMotionState OnTheCentreline(
        double rangeM, double surgeMps, double swayMps = 0.0) =>
        new(0.0, rangeM, North, surgeMps, swayMps, 0.0);

    /// <summary>Advances the berthing machine by one step over clear water with a good fix.</summary>
    /// <param name="plan">Plan being flown.</param>
    /// <param name="progress">Progress carried in.</param>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <returns>The outcome of the step.</returns>
    private static DockingOutcome Advance(
        DockingPlan plan, DockingProgress progress, SurfaceMotionState state) =>
        Docking.Advance(Hull, plan, progress, state, Dt, isApproachClear: true, hasPositionFix: true);

    /// <summary>A point a stated distance due north of another, in the scene frame.</summary>
    /// <remarks>North is <c>-Z</c>, which is the one axis convention every figure here rests on.</remarks>
    /// <param name="fromEus">Point to measure from.</param>
    /// <param name="distanceM">Distance in metres.</param>
    /// <returns>The point.</returns>
    private static Vector3 Ahead(Vector3 fromEus, double distanceM) =>
        new(fromEus.X, fromEus.Y, fromEus.Z - (float)distanceM);

    /// <summary>Guidance input for slack water, still air and no external speed ceiling.</summary>
    /// <param name="state">Pose the velocities are resolved from.</param>
    /// <returns>The input to hand the guidance law.</returns>
    private static SurfaceGuidanceInput CalmInput(in SurfaceMotionState state) => new(
        DeltaSeconds: Dt,
        SpeedCeilingMps: double.PositiveInfinity,
        Velocities: Dynamics.Resolve(state, SurfaceConditions.Calm),
        PassiveDriftEus: Vector3.Zero,
        WindEus: Vector3.Zero);

    /// <summary>Narrows a captured state's domain extension to its surface form.</summary>
    /// <remarks>Named for the type rather than the domain, so it cannot read as the namespace.</remarks>
    /// <param name="state">State captured from a surface asset.</param>
    /// <returns>The surface-domain state.</returns>
    private static SurfaceDomainState SurfaceState(AssetState state) =>
        state.DomainState.Should().BeOfType<SurfaceDomainState>().Subject;

    /// <summary>A validated command carrying nothing but its kind.</summary>
    /// <param name="kind">Command kind to issue.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(AssetCommandKind kind) =>
        new(Kind: kind, AssetId: RigId);

    /// <summary>A validated command carrying a scene-frame target.</summary>
    /// <param name="kind">Command kind to issue.</param>
    /// <param name="targetEus">Target in the scene frame.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(AssetCommandKind kind, Vector3 targetEus) =>
        new(
            Kind: kind,
            AssetId: RigId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity));

    /// <summary>One vessel on an analytic sea, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what a world does per step — sample the environment at the asset's pre-step
    /// position, build a context, step the asset — without a world, so a case can be stated in
    /// literals. Events are drained every step rather than once at the end, because the asset's
    /// queue is deliberately bounded and a long run would otherwise drop the transitions these
    /// cases count.
    /// </remarks>
    private sealed class VesselRig
    {
        private readonly Random _random = new(FixedSeed);
        private readonly Sea _sea;

        /// <summary>Floats a vessel and prepares it to be stepped.</summary>
        /// <param name="sea">Water to float on.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        /// <param name="spawnEus">Scene-frame spawn point.</param>
        public VesselRig(Sea sea, double headingRad, Vector3 spawnEus)
        {
            _sea = sea;
            Asset = new SurfaceAsset(
                AssetProfiles.Create(RigId, VehicleClass.SurfaceVessel),
                SurfaceDynamics.For(Hull),
                sea,
                spawnEus,
                headingRad);
        }

        /// <summary>The vessel under test.</summary>
        public SurfaceAsset Asset { get; }

        /// <summary>World steps taken so far.</summary>
        public long Tick { get; private set; }

        /// <summary>Every event raised since the rig was built, in the order they were raised.</summary>
        public List<AssetEvent> Log { get; } = [];

        /// <summary>Advances the vessel by exactly one step and drains what it raised.</summary>
        public void Step()
        {
            var before = Asset.PositionEus;
            Tick++;

            Asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: Tick * Dt,
                Tick: Tick,
                Environment: _sea.Sample(before, Hull.FootprintRadiusM),
                Peers: [],
                Random: _random));

            Log.AddRange(Asset.DrainEvents());
        }

        /// <summary>Advances the vessel by a fixed number of steps.</summary>
        /// <param name="steps">Number of steps.</param>
        public void Run(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Step();
            }
        }

        /// <summary>Advances the vessel until an event is raised, or the step budget runs out.</summary>
        /// <remarks>
        /// A bounded loop over a literal budget, never a wait: the step count is the only clock
        /// here, so a run that never reaches its event fails on an assertion rather than hanging.
        /// </remarks>
        /// <param name="code">Event code to run until.</param>
        /// <param name="maxSteps">Most steps to take.</param>
        /// <returns>The number of steps actually taken.</returns>
        public int RunUntil(string code, int maxSteps)
        {
            for (int taken = 0; taken < maxSteps; taken++)
            {
                if (Log.Exists(e => e.Code == code))
                {
                    return taken;
                }

                Step();
            }

            return maxSteps;
        }

        /// <summary>Projects the vessel onto the wire at the current tick.</summary>
        /// <returns>The captured state.</returns>
        public AssetState Capture() => Asset.Capture(new AssetCaptureContext(
            Environment: _sea,
            SimulationTimeSeconds: Tick * Dt,
            Tick: Tick,
            SourceTime: WorldEpochUtc.AddSeconds(Tick * Dt),
            ReceiveTime: WallClockUtc,
            Origin: null));
    }

    /// <summary>Deep, flat water with a uniform current and a uniform wind.</summary>
    /// <remarks>
    /// Deliberately not the procedural terrain. A varying bed would put an under-keel clearance
    /// derate in the middle of an approach, and a varying set would make the disturbance a
    /// function of where the vessel drifted to — either of which turns a closed-form expectation
    /// into an approximation of one.
    /// </remarks>
    private sealed class Sea : IEnvironmentSampler
    {
        private readonly Vector3 _current;
        private readonly Vector3 _wind;
        private readonly double _bedElevationM;

        /// <summary>Builds a sea.</summary>
        /// <param name="currentEastMps">East-setting surface current in metres per second.</param>
        /// <param name="windEastMps">East-blowing wind in metres per second.</param>
        /// <param name="depthM">Water depth everywhere, in metres.</param>
        public Sea(double currentEastMps = 0.0, double windEastMps = 0.0, double depthM = 30.0)
        {
            _current = new Vector3((float)currentEastMps, 0f, 0f);
            _wind = new Vector3((float)windEastMps, 0f, 0f);
            _bedElevationM = -depthM;
            Wind = new UniformWind(_wind);
        }

        /// <inheritdoc />
        public double SeaLevelM => 0.0;

        /// <inheritdoc />
        public IWindField Wind { get; }

        /// <inheritdoc />
        public double GetElevation(double x, double z) => _bedElevationM;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) => new(
            PositionEus: positionEus,
            WindEus: _wind,
            Visibility: 1.0,
            Precipitation: 0.0,
            SurfaceCurrentEus: _current,
            TerrainElevationM: _bedElevationM,
            TerrainNormalEus: Vector3.UnitY,
            SurfaceMaterial: SurfaceType.Water,
            WaterSurfaceElevationM: SeaLevelM,
            BathymetricElevationM: _bedElevationM,
            Zones: []);
    }

    /// <summary>A wind field that blows the same way everywhere.</summary>
    private sealed class UniformWind : IWindField
    {
        private readonly Vector3 _wind;

        /// <summary>Builds a uniform wind field.</summary>
        /// <param name="wind">Wind velocity in the scene frame, in metres per second.</param>
        public UniformWind(Vector3 wind) => _wind = wind;

        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => _wind;
    }
}
