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
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

/// <summary>Fixtures and helpers for <see cref="StationKeepDockTests"/>.</summary>
/// <remarks>
/// Split from the assertions so that file reads as a list of contracts, following the same
/// arrangement <see cref="GroundDynamicsTests"/> and <see cref="GroundAssetStateTests"/> use.
/// Everything here is a literal or a closed-form expression: the timestep is fixed, both
/// timestamps are derived from a fixed epoch, the generator is seeded, and the water is an
/// analytic sea whose depth, current and wind are constants. Nothing reads a clock, sleeps, or
/// draws from an unseeded source, so every quantity a case asserts is one that can be worked out
/// on paper rather than merely observed.
/// <para>
/// The one deliberately synthetic object is <see cref="HoldingHull"/>. Both shipped profiles set
/// <see cref="SurfaceProfile.CanStationKeep"/> false — one screw and one rudder cannot pin a spot
/// against a set — so a hull that can hold station has to be constructed here to exercise the law
/// at all. It is the shipped workboat with that one fact changed and the holding power that
/// <see cref="SurfaceProfile.Validated"/> requires to go with it, which is exactly the shape a
/// thruster-equipped profile would take when one is added.
/// </para>
/// </remarks>
public sealed partial class StationKeepDockTests
{
    /// <summary>Fixed integration timestep, in seconds. Matches the world's default 60 Hz.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed for every generator in this suite, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Identifier every vessel in this suite is spawned with.</summary>
    private const string RigId = "usv-1";

    /// <summary>Heading due north, in radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Heading due east, in radians clockwise from true north.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>Heading due west, in radians clockwise from true north.</summary>
    /// <remarks>The reciprocal of an east-setting current, so a hull bowing into one holds this.</remarks>
    private const double West = 3.0 * Math.PI / 2.0;

    /// <summary>Heading the control-law cases hold, in radians clockwise from true north.</summary>
    /// <remarks>
    /// Deliberately not a cardinal point and not the answer to any heading policy under test, so
    /// a policy that silently fell back to "keep what you have" cannot be mistaken for one that
    /// worked. The one case where that fallback <em>is</em> the contract asserts this value.
    /// </remarks>
    private const double LawHeadingRad = 0.75;

    /// <summary>Heading a fixed-heading hold is asked for, in radians clockwise from true north.</summary>
    private const double FixedHeadingRad = 1.0;

    /// <summary>An east-setting current a hold can comfortably stem, in metres per second.</summary>
    /// <remarks>
    /// Chosen against the hull rather than picked: coupled into the water column it is about
    /// 0.46 m/s of set, roughly a tenth of the effort the hold is permitted, so the case is about
    /// the hold working rather than about how close to its limit it is.
    /// </remarks>
    private const double ModerateCurrentMps = 0.5;

    /// <summary>An east-setting current past anything this hull can hold against, in metres per second.</summary>
    /// <remarks>
    /// Coupled, it demands 5.52 m/s against an allowance of 4.5 — the profile's top speed times
    /// <see cref="StationKeepGoal.DefaultMaxEffortFraction"/> — so saturation is reached by the
    /// disturbance alone, with the vessel still exactly on station.
    /// </remarks>
    private const double OverwhelmingCurrentMps = 6.0;

    /// <summary>Steps a station-keeping case runs for: sixty seconds, ten surge time constants.</summary>
    private const int HoldSteps = 3600;

    /// <summary>Steps a case runs after an emergency stop: thirty seconds, five time constants.</summary>
    private const int DriftSteps = 1800;

    /// <summary>Steps a case runs to establish a passage before interrupting it.</summary>
    private const int UnderWaySteps = 600;

    /// <summary>Range a berthing approach is commanded from, in metres.</summary>
    /// <remarks>
    /// Nine hull lengths: past the corridor entry at six, so the approach genuinely passes
    /// through all three stages rather than starting inside one of them.
    /// </remarks>
    private const double DockRunM = 60.0;

    /// <summary>Range the control-law berthing cases place their entry point at, in metres.</summary>
    private const double BerthingEntryM = 80.0;

    /// <summary>Ceiling on the steps an approach or transit case runs before it gives up.</summary>
    /// <remarks>
    /// Two minutes. The plan's own time budget for the 60 m run is 120 s, so a case that
    /// exhausts this budget has already been abandoned by the state machine, and the assertion
    /// that follows the loop says so on a literal rather than hanging.
    /// </remarks>
    private const int MaxApproachSteps = 7200;

    /// <summary>Event code an engaged emergency stop raises. Not a published constant.</summary>
    private const string EmergencyStopCode = "surface.emergencyStop";

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Frozen receive-time stamp, so a capture is a function of its inputs alone.</summary>
    private static readonly DateTimeOffset WallClockUtc = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    /// <summary>The shipped workboat: a displacement hull that cannot hold a station.</summary>
    private static readonly SurfaceProfile DisplacementHull = SurfaceProfile.SurfaceVessel;

    /// <summary>The same hull with the propulsion to hold a station, and the power draw to match.</summary>
    /// <remarks>
    /// Only the two figures that have to move together are changed, so this profile still passes
    /// <see cref="SurfaceProfile.Validated"/> — which refuses a hull quoting a holding power it
    /// cannot spend, and would therefore refuse the half-edit that only set the flag.
    /// </remarks>
    private static readonly SurfaceProfile HoldingHull =
        SurfaceProfile.SurfaceVessel with { CanStationKeep = true, StationKeepPowerW = 900.0 };

    /// <summary>Motion model for <see cref="HoldingHull"/>, used to resolve velocities for the law.</summary>
    private static readonly ISurfaceDynamics HoldingDynamics = SurfaceDynamics.For(HoldingHull);

    /// <summary>A zone that denies a position fix to anything inside it.</summary>
    private static readonly EnvironmentZone[] PositionDeniedZone =
    [
        new EnvironmentZone(
            ZoneId: "gnss-shadow",
            Kind: SurfaceAsset.PositionDeniedZoneKind,
            IsEntryProhibited: false,
            SpeedLimitMps: null,
            Advisory: "Advisory: no position fix inside this zone."),
    ];

    private static readonly EnvironmentZone[] NoZones = [];

    // ─── Station-keeping control law ────────────────────────────────────────

    /// <summary>A vessel dead in the water at a scene-frame offset, on a stated heading.</summary>
    /// <param name="eastM">Scene <c>X</c> coordinate in metres.</param>
    /// <param name="southM">Scene <c>Z</c> coordinate in metres.</param>
    /// <param name="headingRad">Heading in radians clockwise from true north.</param>
    /// <returns>The state to hand the law.</returns>
    private static SurfaceMotionState At(double eastM, double southM, double headingRad = West) =>
        SurfaceMotionState.DeadInTheWater(eastM, southM, headingRad);

    /// <summary>Runs one evaluation of the station-keeping law over literal conditions.</summary>
    /// <remarks>
    /// The disturbance is assembled the way <see cref="SurfaceAsset"/> assembles it — the coupled
    /// current out of the motion model plus <see cref="SurfaceProfile.LeewayFraction"/> of the
    /// wind — rather than being invented here, so a case cannot pass by feeding the law a
    /// disturbance the integrator would never produce.
    /// </remarks>
    /// <param name="goal">Station and the terms it is held on.</param>
    /// <param name="state">Pose and body velocities of the vessel.</param>
    /// <param name="currentEastMps">East-setting surface current in metres per second.</param>
    /// <param name="windSouthMps">South-blowing wind in metres per second.</param>
    /// <param name="hasPositionFix">False to take the position fix away.</param>
    /// <returns>The outcome the law produced.</returns>
    private static StationKeepOutcome Evaluate(
        StationKeepGoal goal,
        SurfaceMotionState state,
        double currentEastMps,
        double windSouthMps = 0.0,
        bool hasPositionFix = true)
    {
        var conditions = new SurfaceConditions(
            new Vector3((float)currentEastMps, 0f, 0f),
            new Vector3(0f, 0f, (float)windSouthMps),
            HoldingHull.MaxSpeedMps);

        var velocities = HoldingDynamics.Resolve(state, conditions);

        float leeway = (float)HoldingHull.LeewayFraction;
        var passiveDrift = new Vector3(
            velocities.DriftVelocityEus.X + (conditions.WindEus.X * leeway),
            0f,
            velocities.DriftVelocityEus.Z + (conditions.WindEus.Z * leeway));

        return StationKeeping.Evaluate(
            HoldingHull,
            goal,
            new StationKeepInput(
                State: state,
                Velocities: velocities,
                PassiveDriftEus: passiveDrift,
                WindEus: conditions.WindEus,
                SpeedCeilingMps: conditions.SpeedCeilingMps,
                HasPositionFix: hasPositionFix));
    }

    /// <summary>Speed a hull is actually carried at by a surface current, in metres per second.</summary>
    /// <remarks>
    /// A hull with draft sits in the sheared column beneath the surface, so it makes good rather
    /// less than the surface value. Reading the coupling off the profile keeps the expectation a
    /// function of the hull rather than a number copied out of a passing run.
    /// </remarks>
    /// <param name="profile">Hull whose coupling applies.</param>
    /// <param name="currentMps">Surface current speed in metres per second.</param>
    /// <returns>The coupled drift speed.</returns>
    private static double CoupledDriftMps(SurfaceProfile profile, double currentMps) =>
        currentMps * profile.PassiveCurrentCoupling;

    // ─── Berthing state machine ─────────────────────────────────────────────

    /// <summary>A berthing plan onto the scene origin, approached from due south.</summary>
    /// <remarks>
    /// The centreline runs due north, so a vessel on it reports a lateral offset of exactly zero
    /// and, on heading <see cref="North"/>, a heading error of exactly zero. Every stage boundary
    /// is then a pure function of the scene <c>Z</c> coordinate, which is what lets the stage
    /// cases be stated as single numbers.
    /// </remarks>
    /// <returns>The plan, derived from the shipped hull's own dimensions.</returns>
    private static DockingPlan BerthingPlan() => DockingPlan.For(
        DisplacementHull,
        vesselEus: new Vector3(0f, 0f, (float)BerthingEntryM),
        berthEus: Vector3.Zero);

    /// <summary>A vessel on the berthing centreline at a range, making way towards the berth.</summary>
    /// <param name="rangeM">Range to the berth in metres.</param>
    /// <param name="surgeMps">Water-relative speed along the bow, in metres per second.</param>
    /// <param name="headingRad">Heading in radians clockwise from true north.</param>
    /// <param name="eastM">Offset from the centreline in metres; zero is on it.</param>
    /// <returns>The state to hand the state machine.</returns>
    private static SurfaceMotionState OnTheCentreline(
        double rangeM, double surgeMps, double headingRad = North, double eastM = 0.0) =>
        new(
            EastM: eastM,
            SouthM: rangeM,
            HeadingRad: headingRad,
            SurgeMps: surgeMps,
            SwayMps: 0.0,
            YawRateRadPerSec: 0.0);

    /// <summary>The staged speed ceiling a plan puts in force for a stage.</summary>
    /// <remarks>
    /// Restated from the plan's own published fields rather than from the machine's private
    /// mapping, so a stage wired to the wrong ceiling fails here instead of agreeing with itself.
    /// </remarks>
    /// <param name="plan">Plan carrying the staged limits.</param>
    /// <param name="phase">Stage in force.</param>
    /// <returns>The ceiling in metres per second.</returns>
    private static double CeilingFor(DockingPlan plan, DockingPhase phase) => phase switch
    {
        DockingPhase.Approach => plan.ApproachSpeedMps,
        DockingPhase.Corridor => plan.CorridorSpeedMps,
        DockingPhase.Final => plan.FinalSpeedMps,
        _ => 0.0,
    };

    /// <summary>Progress for an approach that has been running, carrying its own history.</summary>
    /// <param name="elapsedSeconds">Simulated seconds since the operation began.</param>
    /// <param name="closestRangeM">Smallest range reached so far, in metres.</param>
    /// <returns>Progress in an active stage.</returns>
    private static DockingProgress Running(double elapsedSeconds, double closestRangeM) =>
        new(DockingPhase.Approach, elapsedSeconds, closestRangeM, DockingAbortReason.None);

    // ─── Commands ───────────────────────────────────────────────────────────

    /// <summary>A validated command carrying nothing but its kind.</summary>
    /// <param name="kind">Command kind to issue.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(AssetCommandKind kind) =>
        new(Kind: kind, AssetId: RigId);

    /// <summary>A validated command carrying a scene-frame target.</summary>
    /// <param name="kind">Command kind to issue.</param>
    /// <param name="targetEus">Target in the scene frame.</param>
    /// <param name="headingRad">Heading or course the kind may carry, or null.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(
        AssetCommandKind kind, Vector3 targetEus, double? headingRad = null) =>
        new(
            Kind: kind,
            AssetId: RigId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity),
            HeadingRad: headingRad);

    /// <summary>Every command kind a descriptor's capability report offers this asset.</summary>
    /// <remarks>
    /// Built the way the capability report is built: the catalog's own domain list and its own
    /// any-of / all-of capability rule, never a hand-written second table. A promise made here
    /// has to be one <see cref="SurfaceAsset.Apply"/> keeps.
    /// </remarks>
    /// <param name="descriptor">Descriptor whose domain and capabilities decide the offer.</param>
    /// <returns>The advertised kinds, in the catalog's registration order.</returns>
    private static IReadOnlyList<string> Advertised(AssetDescriptor descriptor) => CommandCatalog.All
        .Where(d => d.AppliesTo(descriptor.Domain) && d.IsSatisfiedBy(descriptor.Capabilities))
        .Select(d => d.Kind)
        .ToList();

    // ─── Reading a captured state ───────────────────────────────────────────

    /// <summary>Narrows a captured state's domain extension to its surface form.</summary>
    /// <param name="state">State captured from a surface asset.</param>
    /// <returns>The surface-domain state.</returns>
    private static SurfaceDomainState SurfaceState(AssetState state) =>
        state.DomainState.Should().BeOfType<SurfaceDomainState>().Subject;

    /// <summary>Narrows a published station-keep goal, failing rather than dereferencing a null.</summary>
    /// <param name="state">Surface-domain state to read.</param>
    /// <returns>The station-keep state.</returns>
    private static StationKeepState HoldState(SurfaceDomainState state) =>
        state.StationKeep.Should().BeOfType<StationKeepState>().Subject;

    /// <summary>Horizontal separation between two scene-frame points, in metres.</summary>
    /// <param name="a">First point.</param>
    /// <param name="b">Second point.</param>
    /// <returns>The distance in the scene's horizontal plane, in metres.</returns>
    private static double Planar(Vector3 a, Vector3 b)
    {
        double east = a.X - b.X;
        double south = a.Z - b.Z;
        return Math.Sqrt((east * east) + (south * south));
    }

    /// <summary>Smallest signed turn between two bearings, in radians.</summary>
    /// <param name="endRad">Bearing turned to.</param>
    /// <param name="startRad">Bearing turned from.</param>
    /// <returns>The turn in <c>(-pi, pi]</c>, positive to starboard.</returns>
    private static double AngleDelta(double endRad, double startRad)
    {
        double delta = CoordinateFrames.NormalizeAngle(endRad - startRad);
        return delta > Math.PI ? delta - Math.Tau : delta;
    }

    // ─── Building and driving one vessel ────────────────────────────────────

    /// <summary>Floats a vessel on an analytic sea and returns a rig that can step it.</summary>
    /// <param name="sea">Water to float on and integrate over.</param>
    /// <param name="profile">Hull envelope, or null for the shipped displacement hull.</param>
    /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
    /// <param name="spawnEus">Scene-frame spawn point, or null for the scene origin.</param>
    /// <param name="declareStationKeep">
    /// True to add <see cref="AssetCapability.StationKeep"/> to the descriptor. Off by default,
    /// because that is what a shipped surface descriptor actually declares.
    /// </param>
    /// <returns>A rig holding the vessel, its water and its tick counter.</returns>
    private static VesselRig Rig(
        Sea sea,
        SurfaceProfile? profile = null,
        double headingRad = North,
        Vector3? spawnEus = null,
        bool declareStationKeep = false) =>
        new(sea, profile ?? DisplacementHull, headingRad, spawnEus ?? Vector3.Zero, declareStationKeep);

    /// <summary>One vessel on an analytic sea, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what <see cref="AssetWorld"/> does per step — sample the environment at the
    /// asset's pre-step position, build a context, call <see cref="IStepDrivenAsset.Step"/> —
    /// without a world, so a case can be stated in literals. The event queue is drained on every
    /// step into <see cref="Log"/> rather than once at the end, because the asset's queue is
    /// deliberately bounded and a long run would otherwise start dropping the very transitions
    /// these cases count.
    /// </remarks>
    private sealed class VesselRig
    {
        private readonly Random _random = new(FixedSeed);

        /// <summary>Floats a vessel and prepares it to be stepped.</summary>
        /// <param name="sea">Water to float on.</param>
        /// <param name="profile">Hull envelope to integrate within.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        /// <param name="spawnEus">Scene-frame spawn point.</param>
        /// <param name="declareStationKeep">True to declare a station-keeping capability.</param>
        public VesselRig(
            Sea sea,
            SurfaceProfile profile,
            double headingRad,
            Vector3 spawnEus,
            bool declareStationKeep)
        {
            Sea = sea;
            Profile = profile;

            var shipped = AssetProfiles.Create(RigId, VehicleClass.SurfaceVessel);
            Descriptor = declareStationKeep
                ? shipped with { Capabilities = shipped.Capabilities | AssetCapability.StationKeep }
                : shipped;

            Asset = new SurfaceAsset(
                Descriptor, SurfaceDynamics.For(profile), sea, spawnEus, headingRad);
        }

        /// <summary>The vessel under test.</summary>
        public SurfaceAsset Asset { get; }

        /// <summary>Envelope the vessel is integrated within.</summary>
        public SurfaceProfile Profile { get; }

        /// <summary>Descriptor the vessel publishes.</summary>
        public AssetDescriptor Descriptor { get; }

        /// <summary>Water the vessel is floating on.</summary>
        public Sea Sea { get; }

        /// <summary>World steps taken so far.</summary>
        public long Tick { get; private set; }

        /// <summary>Every event raised since the rig was built, in the order they were raised.</summary>
        public List<AssetEvent> Log { get; } = [];

        /// <summary>Advances the vessel by exactly one step and drains what it raised.</summary>
        /// <returns>The scene-frame position the vessel held before the step.</returns>
        public Vector3 Step()
        {
            var before = Asset.PositionEus;
            Tick++;

            Asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: Tick * Dt,
                Tick: Tick,
                Environment: Sea.Sample(before, Profile.FootprintRadiusM),
                Peers: [],
                Random: _random));

            Log.AddRange(Asset.DrainEvents());
            return before;
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
        /// in this suite, so a run that never reaches its event fails on an assertion rather than
        /// hanging.
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
        /// <remarks>
        /// Both timestamps are derived from the fixed epoch rather than sampled, so two captures
        /// at the same tick are handed identical contexts.
        /// </remarks>
        /// <returns>The captured state.</returns>
        public AssetState Capture() => Asset.Capture(new AssetCaptureContext(
            Environment: Sea,
            SimulationTimeSeconds: Tick * Dt,
            Tick: Tick,
            SourceTime: WorldEpochUtc.AddSeconds(Tick * Dt),
            ReceiveTime: WallClockUtc,
            Origin: null));
    }

    /// <summary>Deep, flat water with a uniform current and a uniform wind.</summary>
    /// <remarks>
    /// Deliberately not the procedural terrain. A bed whose depth varies puts an under-keel
    /// clearance derate in the middle of a berthing approach, and a set whose direction varies
    /// makes the disturbance a hold is fighting a function of where the hold drifted to — either
    /// of which turns a closed-form expectation into an approximation of one. Here the depth, the
    /// set and the wind are the same everywhere, so the only thing that changes across a run is
    /// the vessel.
    /// <para>
    /// <see cref="IsPositionDenied"/> is the one thing that moves, and it is the only mechanism
    /// this simulation has for taking a vessel's position quality away — a zone rather than a
    /// receiver model, exactly as <see cref="SurfaceAsset.PositionDeniedZoneKind"/> documents.
    /// Toggling it changes neither the water surface nor the bed, so it cannot be mistaken for
    /// the environment being replaced under the hull.
    /// </para>
    /// </remarks>
    private sealed class Sea : IEnvironmentSampler
    {
        private readonly Vector3 _current;
        private readonly Vector3 _wind;
        private readonly double _bedElevationM;

        /// <summary>Builds a sea.</summary>
        /// <param name="currentEastMps">East-setting surface current in metres per second.</param>
        /// <param name="currentSouthMps">South-setting surface current in metres per second.</param>
        /// <param name="windEastMps">East-blowing wind in metres per second.</param>
        /// <param name="windSouthMps">South-blowing wind in metres per second.</param>
        /// <param name="depthM">Water depth everywhere, in metres.</param>
        public Sea(
            double currentEastMps = 0.0,
            double currentSouthMps = 0.0,
            double windEastMps = 0.0,
            double windSouthMps = 0.0,
            double depthM = 30.0)
        {
            _current = new Vector3((float)currentEastMps, 0f, (float)currentSouthMps);
            _wind = new Vector3((float)windEastMps, 0f, (float)windSouthMps);
            _bedElevationM = -depthM;
            Wind = new UniformWind(_wind);
        }

        /// <summary>True while every point of this sea denies a position fix.</summary>
        public bool IsPositionDenied { get; set; }

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
            Zones: IsPositionDenied ? PositionDeniedZone : NoZones);
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
