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
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

// The analytic basin every case is floated on, the rig that steps one vessel over it, and the
// canonical rendering the determinism cases hash. Split from the assertions the way the ground
// suites are split: reading what a case pins should not mean scrolling past how its water was
// built. The type's summary lives on the primary declaration in SurfaceWaterTests.cs.
public sealed partial class SurfaceWaterTests
{
    /// <summary>Fixed integration timestep in seconds. Matches the world's 60 Hz asset pass.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed every generator in this suite uses, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Water-surface elevation the basin starts at, in metres.</summary>
    /// <remarks>
    /// Zero, so a depth and an easting convert into one another by inspection. The preset-change
    /// case moves it, which is the only reason it is a variable at all.
    /// </remarks>
    private const double SeaLevelM = 0.0;

    /// <summary>Bed elevation where the basin's east coordinate is zero, in metres.</summary>
    private const double BedAtOriginM = -10.0;

    /// <summary>Rise of the bed per metre travelled east. A one-in-twenty beach.</summary>
    /// <remarks>
    /// Chosen with <see cref="BedAtOriginM"/> so that every depth this suite names lands on an
    /// exactly representable easting: depth <c>d</c> sits at <c>20 * (10 - d)</c> metres east.
    /// The bed is planar, so its normal is recovered exactly by central differences at any
    /// spacing, and two hulls compared with one another are provably over the same ground.
    /// </remarks>
    private const double BedRisePerMetreEast = 0.05;

    /// <summary>East coordinate at which the beach becomes dry land, in metres.</summary>
    private const double DryLandEastingM = 200.0;

    /// <summary>Heading due north, radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Heading due east, radians clockwise from true north. Towards the beach.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>Heading due west, radians clockwise from true north. Towards deep water.</summary>
    private const double West = 3.0 * Math.PI / 2.0;

    /// <summary>Tolerance on a depth or a clearance the plane geometry pins exactly, in metres.</summary>
    /// <remarks>
    /// Set by the single-precision round trip a position makes through <see cref="Vector3"/>,
    /// not by physics: the bed is linear, so the only error in a depth is the error in the
    /// easting it was read at.
    /// </remarks>
    private const double DepthToleranceM = 1e-3;

    /// <summary>Most events one drain may return once the bounded queue has had to drop some.</summary>
    /// <remarks>
    /// The queue holds sixty-four and <see cref="SurfaceAsset.DrainEvents"/> appends one notice
    /// saying how many were lost, so a drain from a saturated queue returns sixty-five. Written
    /// out because it is the contract a per-asset collection has to satisfy — bounded, and never
    /// silently lossy — and a test asserting merely "fewer than the tick count" would pass
    /// against a queue that grew into the thousands.
    /// </remarks>
    private const int MaxDrainedEvents = 65;

    /// <summary>Wall-clock instant simulation time zero corresponds to.</summary>
    private static readonly DateTimeOffset WorldEpochUtc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Frozen receive-time stamp, so a capture is a function of its inputs alone.</summary>
    private static readonly DateTimeOffset WallClockUtc = new(2026, 1, 1, 0, 5, 0, TimeSpan.Zero);

    /// <summary>No zones apply.</summary>
    private static readonly EnvironmentZone[] NoZones = [];

    /// <summary>A zone that denies entry, whatever the water under it.</summary>
    private static readonly EnvironmentZone[] Prohibited =
        [new EnvironmentZone("nogo-1", "restricted", IsEntryProhibited: true)];

    /// <summary>The hull every case in this suite is measured against.</summary>
    private static SurfaceProfile Profile => SurfaceProfile.SurfaceVessel;

    /// <summary>The water-relevant projection of <see cref="Profile"/>.</summary>
    private static VesselWaterProfile WaterProfile => VesselWaterProfile.From(Profile);

    /// <summary>The advisory safe under-keel margin <see cref="Profile"/> keeps, in metres.</summary>
    private static double SafeMarginM => UnderKeelClearance.SafeMarginForDraft(Profile.DraftM);

    // ─── Building water ─────────────────────────────────────────────────────

    /// <summary>A basin with a one-in-twenty beach to the east and nothing else going on.</summary>
    /// <param name="currentEus">Surface current in the scene frame, in metres per second.</param>
    /// <param name="zones">Zone source, or null for none.</param>
    /// <returns>The sampler.</returns>
    private static Basin Water(Vector3 currentEus = default, IZoneSource? zones = null) =>
        new(currentEus, zones);

    /// <summary>Declares a no-entry zone over a band of eastings.</summary>
    /// <param name="fromEastM">East coordinate the band starts at, in metres.</param>
    /// <param name="toEastM">East coordinate the band ends at, in metres.</param>
    /// <returns>A zone source covering that band and nothing else.</returns>
    private static IZoneSource ProhibitedBand(double fromEastM, double toEastM) =>
        new PredicateZones((x, _) => x >= fromEastM && x <= toEastM ? Prohibited : NoZones);

    /// <summary>Declares an advisory speed ceiling over a band of eastings.</summary>
    /// <param name="fromEastM">East coordinate the band starts at, in metres.</param>
    /// <param name="toEastM">East coordinate the band ends at, in metres.</param>
    /// <param name="limitMps">Ceiling inside the band, in metres per second.</param>
    /// <returns>A zone source covering that band and nothing else.</returns>
    private static IZoneSource SpeedLimitBand(double fromEastM, double toEastM, double limitMps)
    {
        EnvironmentZone[] limited =
            [new EnvironmentZone("slow-1", "no-wake", IsEntryProhibited: false, limitMps)];

        return new PredicateZones((x, _) => x >= fromEastM && x <= toEastM ? limited : NoZones);
    }

    /// <summary>Scene-frame point at a given depth, on the basin's centreline.</summary>
    /// <param name="water">Basin to resolve against.</param>
    /// <param name="depthM">Water column wanted, in metres.</param>
    /// <returns>A point in the scene frame, at the water surface.</returns>
    private static Vector3 At(Basin water, double depthM) =>
        new((float)water.EastingForDepthM(depthM), (float)water.SeaLevelM, 0f);

    /// <summary>Samples the basin at a given depth, as the world samples under a hull.</summary>
    /// <param name="water">Basin to sample.</param>
    /// <param name="depthM">Water column wanted, in metres.</param>
    /// <returns>A fully populated sample.</returns>
    private static EnvironmentSample SampleAtDepth(Basin water, double depthM) =>
        water.Sample(At(water, depthM), Profile.FootprintRadiusM);

    // ─── Commands ───────────────────────────────────────────────────────────

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

    /// <summary>A validated course command addressed to one vessel.</summary>
    /// <param name="assetId">Vessel the command is addressed to.</param>
    /// <param name="courseRad">Course to steer, radians clockwise from true north.</param>
    /// <param name="speedMps">Speed to make, or null for the hull's default.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand SetCourse(
        string assetId, double courseRad, double? speedMps = null) =>
        new(
            Kind: AssetCommandKind.SetCourse,
            AssetId: assetId,
            HeadingRad: courseRad,
            SpeedMps: speedMps);

    /// <summary>A validated command that carries no target.</summary>
    /// <param name="assetId">Vessel the command is addressed to.</param>
    /// <param name="kind">Command kind to issue.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand Command(string assetId, AssetCommandKind kind) =>
        new(Kind: kind, AssetId: assetId);

    // ─── Shared assertions ──────────────────────────────────────────────────

    /// <summary>Asserts depth, draft and clearance are three numbers and the third is the difference.</summary>
    /// <remarks>
    /// Applied wherever a state is published, because the failure it guards is silent: a client
    /// handed a clearance that does not equal depth less draft cannot tell which of the three is
    /// wrong, and will happily report a hull clear of a bed it is sitting on.
    /// </remarks>
    /// <param name="state">Surface domain state to check.</param>
    private static void AssertClearanceIsDepthLessDraft(SurfaceDomainState state)
    {
        state.DraftM.Should().BeApproximately(Profile.DraftM, 1e-9,
            "the published draft is the hull's own, not a figure derived from the water");

        state.UnderKeelClearanceM.Should().BeApproximately(
            state.WaterDepthM - state.DraftM, 1e-9,
            "depth, draft and clearance are three separately published quantities and the third "
            + "is exactly the difference of the first two");
    }

    /// <summary>Asserts a vessel still answers the commands an operator would recover it with.</summary>
    /// <remarks>
    /// The invariant this whole suite exists to defend. A rover that became immobilised once
    /// refused every command including the ones that freed it; afloat that is worse, because a
    /// vessel nothing can move does not stay where it stopped. Every command exercised here is
    /// one the catalog advertises to this hull, so accepting them is exactly the set the
    /// capability report promises — no more and no less.
    /// </remarks>
    /// <param name="rig">Rig holding the vessel.</param>
    /// <param name="recoveryTargetEus">Somewhere navigable the vessel must agree to go.</param>
    private static void AssertStillCommandable(VesselRig rig, Vector3 recoveryTargetEus)
    {
        string id = rig.Asset.AssetId;

        rig.Apply(Command(id, AssetCommandKind.Stop)).IsAccepted.Should().BeTrue(
            "'stop' is permitted in every operational state and is the always-reachable release");

        rig.Apply(Command(id, AssetCommandKind.ResumeAutonomy)).IsAccepted.Should().BeTrue(
            "handing control back must never be refused by the state it is being handed back from");

        rig.Apply(Command(id, AssetCommandKind.Hold)).IsAccepted.Should().BeTrue(
            "hold is the domain-neutral 'stop working the mission' command and is ungated");

        rig.Apply(SetCourse(id, West, Profile.MaxSpeedMps)).IsAccepted.Should().BeTrue(
            "a helm order is how an operator drives a vessel off the ground by hand");

        rig.Apply(TransitTo(id, recoveryTargetEus, Profile.MaxSpeedMps)).IsAccepted.Should().BeTrue(
            "a transit into deeper water is precisely the command that recovers the vessel");

        rig.Capture().OperationalState.Should().NotBe(OperationalState.Faulted,
            "a grounding is recoverable, and Faulted is excluded by the command catalog's "
            + "operable policy — publishing it would refuse the very commands that recover the hull");
    }

    // ─── Canonical rendering ────────────────────────────────────────────────

    /// <summary>Hashes a route sweep into one stable hex digest.</summary>
    /// <remarks>
    /// Every number goes through round-trip formatting under the invariant culture, so the digest
    /// is taken over the bits the sweep produced rather than over a rounded rendering of them. A
    /// digest is the right shape for a replay assertion because it fails on <em>any</em>
    /// divergence rather than only on the fields a hand-written comparison happened to list.
    /// </remarks>
    /// <param name="check">Sweep to render.</param>
    /// <returns>An uppercase SHA-256 hex digest.</returns>
    private static string Hash(RouteWaterCheck check)
    {
        string text = string.Join(
            '|',
            check.IsNavigable,
            Text(check.LengthM),
            check.SampleCount.ToString(CultureInfo.InvariantCulture),
            Text(check.SampleSpacingM),
            check.WorstClass,
            check.BlockingReason,
            check.BlockingReasonCode,
            check.BlockingPointEus is { } point ? Text(point) : "-",
            Text(check.BlockingDistanceM),
            Text(check.ShallowestDepthM),
            Text(check.MinimumClearanceM),
            Text(check.AccumulatedRisk));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Round-trip rendering of a double.</summary>
    /// <param name="value">Value to render.</param>
    /// <returns>A culture-invariant rendering that preserves every bit.</returns>
    private static string Text(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    /// <summary>Round-trip rendering of a scene-frame vector.</summary>
    /// <param name="value">Vector to render.</param>
    /// <returns>A culture-invariant rendering that preserves every bit.</returns>
    private static string Text(Vector3 value) => string.Join(
        ',',
        value.X.ToString("G9", CultureInfo.InvariantCulture),
        value.Y.ToString("G9", CultureInfo.InvariantCulture),
        value.Z.ToString("G9", CultureInfo.InvariantCulture));

    // ─── Test doubles ───────────────────────────────────────────────────────

    /// <summary>A basin whose bed is an exact plane shelving up towards the east.</summary>
    /// <remarks>
    /// <c>h(x, z) = BedAtOriginM + BedRisePerMetreEast * x</c>, with the water surface at a level
    /// the case may move. Water is derived from elevation against that level exactly as
    /// <see cref="EnvironmentSampler"/> derives it, so the mask under test is fed the same shape
    /// of sample the shipped sampler produces — but over a bed whose depth at every point is
    /// known in closed form, which a procedural height field cannot give.
    /// <para>
    /// Deliberately not the procedural terrain. A test over noise can confirm that a depth is
    /// plausible; it cannot confirm that a hull was refused at exactly the depth its draft and
    /// its margin imply, and that is the whole question here.
    /// </para>
    /// <para>
    /// Deterministic: every member is a pure function of position and the level currently in
    /// force. The current is a constant rather than a field, so a drift case is arithmetic
    /// rather than an observation. <see cref="Samples"/> counts probes, so a sweep can be shown
    /// to do an amount of work fixed by geometry rather than by what it finds.
    /// </para>
    /// </remarks>
    private sealed class Basin : IEnvironmentSampler
    {
        private readonly Vector3 _currentEus;
        private readonly IZoneSource _zones;
        private readonly Vector3 _normal;

        /// <summary>Builds a basin whose surface starts at <see cref="SurfaceWaterTests.SeaLevelM"/>.</summary>
        /// <param name="currentEus">Surface current in the scene frame, in metres per second.</param>
        /// <param name="zones">Zone source, or null for none.</param>
        public Basin(Vector3 currentEus = default, IZoneSource? zones = null)
        {
            _currentEus = currentEus;
            _zones = zones ?? EmptyZoneSource.Instance;
            _normal = Vector3.Normalize(new Vector3((float)-BedRisePerMetreEast, 1f, 0f));
            SeaLevelM = SurfaceWaterTests.SeaLevelM;
        }

        /// <inheritdoc />
        public double SeaLevelM { get; private set; }

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <summary>Probes taken since this basin was built.</summary>
        public int Samples { get; private set; }

        /// <summary>Moves the water surface, as a terrain-preset switch does.</summary>
        /// <param name="seaLevelM">New water-surface elevation in metres.</param>
        public void SetSeaLevel(double seaLevelM) => SeaLevelM = seaLevelM;

        /// <inheritdoc />
        public double GetElevation(double x, double z) => BedAtOriginM + (BedRisePerMetreEast * x);

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => _normal;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM)
        {
            Samples++;

            double elevation = GetElevation(positionEus.X, positionEus.Z);
            bool isWater = elevation < SeaLevelM;

            return new EnvironmentSample(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: isWater ? _currentEus : Vector3.Zero,
                TerrainElevationM: elevation,
                TerrainNormalEus: _normal,
                SurfaceMaterial: isWater ? SurfaceType.Water : SurfaceType.BareGround,
                WaterSurfaceElevationM: isWater ? SeaLevelM : null,
                BathymetricElevationM: isWater ? elevation : null,
                Zones: _zones.GetZones(positionEus.X, positionEus.Z));
        }

        /// <summary>Water column at an easting, in metres. Non-positive where the bed is dry.</summary>
        /// <param name="eastM">East coordinate in metres.</param>
        /// <returns>The depth in metres, against the level currently in force.</returns>
        public double DepthAtEastingM(double eastM) => SeaLevelM - GetElevation(eastM, 0.0);

        /// <summary>East coordinate at which the column is a given depth, in metres.</summary>
        /// <remarks>Resolved against the level in force, so it moves when the sea does.</remarks>
        /// <param name="depthM">Water column wanted, in metres.</param>
        /// <returns>The east coordinate in metres.</returns>
        public double EastingForDepthM(double depthM) =>
            (SeaLevelM - BedAtOriginM - depthM) / BedRisePerMetreEast;
    }

    /// <summary>Dead calm and perfectly clear, so nothing under test is derated by the weather.</summary>
    private sealed class StillAir : IWindField
    {
        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }

    /// <summary>Resolves zones from a pure predicate over position.</summary>
    /// <remarks>
    /// Position only, so the same query always returns the same answer: a zone source that
    /// consulted a clock or a counter would make a route sweep unrepeatable.
    /// </remarks>
    /// <param name="resolve">Zones applying at a scene-frame east and south coordinate.</param>
    private sealed class PredicateZones(
        Func<double, double, IReadOnlyList<EnvironmentZone>> resolve) : IZoneSource
    {
        /// <inheritdoc />
        public IReadOnlyList<EnvironmentZone> GetZones(double x, double z) => resolve(x, z);
    }

    /// <summary>One vessel on one basin, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what <see cref="AssetWorld"/> does per step — sample the environment at the
    /// asset's pre-step position with its descriptor's footprint radius, build a context, call
    /// <see cref="IStepDrivenAsset.Step"/> — without a world, so a case can be stated in depths
    /// and every quantity in it is exactly known. The peer buffer is empty because no surface
    /// behaviour reads it, and the generator is seeded because the contract says an asset may
    /// draw only from the one on the context.
    /// </remarks>
    private sealed class VesselRig
    {
        private readonly Random _random = new(FixedSeed);

        /// <summary>Floats a vessel at a chosen depth on a basin.</summary>
        /// <param name="water">Basin to float on and integrate over.</param>
        /// <param name="spawnDepthM">Water column at the spawn point, in metres.</param>
        /// <param name="headingRad">Initial heading in radians clockwise from true north.</param>
        /// <param name="assetId">Identifier for the vessel.</param>
        public VesselRig(
            Basin water, double spawnDepthM, double headingRad = North, string assetId = "usv-1")
        {
            Water = water;
            Descriptor = AssetProfiles.Create(assetId, VehicleClass.SurfaceVessel);
            Asset = new SurfaceAsset(
                Descriptor,
                SurfaceDynamics.For(Profile),
                water,
                At(water, spawnDepthM),
                headingRad);
        }

        /// <summary>The vessel under test.</summary>
        public SurfaceAsset Asset { get; }

        /// <summary>Water the vessel is floating on.</summary>
        public Basin Water { get; }

        /// <summary>Descriptor the vessel publishes.</summary>
        public AssetDescriptor Descriptor { get; }

        /// <summary>World steps taken so far.</summary>
        public long Tick { get; private set; }

        /// <summary>Water column under the vessel now, in metres.</summary>
        public double DepthHereM => Water.DepthAtEastingM(Asset.PositionEus.X);

        /// <summary>Advances the vessel by exactly one step.</summary>
        public void Step()
        {
            var before = Asset.PositionEus;
            Tick++;

            Asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: Tick * Dt,
                Tick: Tick,
                Environment: Water.Sample(before, Descriptor.Dimensions.FootprintRadiusM),
                Peers: [],
                Random: _random));
        }

        /// <summary>Advances the vessel by a fixed number of steps, draining nothing.</summary>
        /// <param name="steps">Number of steps.</param>
        public void Run(int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                Step();
            }
        }

        /// <summary>Advances the vessel by a fixed number of steps, draining as it goes.</summary>
        /// <param name="steps">Number of steps.</param>
        /// <returns>Every event raised across those steps, in order.</returns>
        public IReadOnlyList<AssetEvent> RunCollecting(int steps)
        {
            var collected = new List<AssetEvent>();

            for (int i = 0; i < steps; i++)
            {
                Step();
                collected.AddRange(Asset.DrainEvents());
            }

            return collected;
        }

        /// <summary>Advances until an event code appears, or a hard bound is reached.</summary>
        /// <remarks>
        /// Bounded, and driven by state this rig itself produced: it reads no clock, cannot spin,
        /// and two runs take the same number of steps and see the same events.
        /// </remarks>
        /// <param name="code">Event code to run until.</param>
        /// <param name="maxSteps">Hard bound on steps taken.</param>
        /// <returns>Every event raised, in order, up to the step that produced <paramref name="code"/>.</returns>
        public IReadOnlyList<AssetEvent> RunUntil(string code, int maxSteps)
        {
            var collected = new List<AssetEvent>();

            for (int i = 0; i < maxSteps; i++)
            {
                Step();
                collected.AddRange(Asset.DrainEvents());

                if (collected.Any(raised => raised.Code == code))
                {
                    return collected;
                }
            }

            return collected;
        }

        /// <summary>Removes and returns every event raised since the last drain.</summary>
        /// <returns>Events in the order they were raised.</returns>
        public IReadOnlyList<AssetEvent> Drain() => Asset.DrainEvents();

        /// <summary>Applies a validated command to the vessel.</summary>
        /// <param name="command">Command to apply.</param>
        /// <returns>Acceptance, or a rejection carrying a machine-readable reason.</returns>
        public AssetCommandResult Apply(SimulatedAssetCommand command) => Asset.Apply(command);

        /// <summary>Projects the vessel onto the wire at the current tick.</summary>
        /// <remarks>
        /// Both timestamps are derived from the fixed epoch rather than sampled, so two captures
        /// at the same tick are handed identical contexts and any difference is the asset's own.
        /// </remarks>
        /// <returns>The captured state.</returns>
        public AssetState Capture() => Asset.Capture(new AssetCaptureContext(
            Environment: Water,
            SimulationTimeSeconds: Tick * Dt,
            Tick: Tick,
            SourceTime: WorldEpochUtc.AddSeconds(Tick * Dt),
            ReceiveTime: WallClockUtc,
            Origin: null));

        /// <summary>Narrows the captured state's domain extension to its surface form.</summary>
        /// <returns>The surface domain state.</returns>
        public SurfaceDomainState SurfaceState() =>
            Capture().DomainState.Should().BeOfType<SurfaceDomainState>().Subject;
    }
}
