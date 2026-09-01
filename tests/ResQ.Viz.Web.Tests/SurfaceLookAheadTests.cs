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
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Where the navigability probe is laid off, and how far — the geometry that decides which water
/// a vessel's passage is refused against.
/// </summary>
/// <remarks>
/// Every case here turns on the one fact that separates this domain from the other two: a vessel
/// does not travel along its heading and does not travel at the speed its log reads. Under any
/// set it crabs, so a probe laid off along the bow at speed through the water asks about water
/// the hull will never enter and says nothing about the water it is being carried into. These
/// cases pin the probe to the <em>track</em> — course and speed over ground — and pin the reach
/// to the ground the hull makes good over its coast horizon.
/// <para>
/// The basin is analytic rather than procedural for the same reason
/// <see cref="SurfaceWaterTests"/>'s is: over noise one can show that a refusal is plausible, but
/// only over a plane can one show that it happened at the depth and the distance the geometry
/// implies. The sampler records every probe the asset takes, which is what makes "where did it
/// look" an assertion rather than an inference from behaviour.
/// </para>
/// </remarks>
public sealed class SurfaceLookAheadTests
{
    /// <summary>Fixed integration timestep in seconds. Matches the world's 60 Hz asset pass.</summary>
    private const double Dt = 1.0 / 60.0;

    /// <summary>Seed every generator in this suite uses, so a failure reproduces exactly.</summary>
    private const int FixedSeed = 20260830;

    /// <summary>Water-surface elevation the basin sits at, in metres.</summary>
    private const double SeaLevelM = 0.0;

    /// <summary>Bed elevation where the basin's east coordinate is zero, in metres.</summary>
    private const double BedAtOriginM = -10.0;

    /// <summary>Rise of the bed per metre travelled east. A one-in-twenty beach.</summary>
    /// <remarks>
    /// Chosen so a depth converts into an easting by inspection: depth <c>d</c> sits at
    /// <c>20 * (10 - d)</c> metres east. The bed is planar and varies with easting alone, so a
    /// probe laid off due north samples exactly the depth the hull is already in — which is what
    /// makes a bow-aligned probe provably blind to a beach the vessel is being set onto.
    /// </remarks>
    private const double BedRisePerMetreEast = 0.05;

    /// <summary>Heading and course due north, radians clockwise from true north.</summary>
    private const double North = 0.0;

    /// <summary>Steps run before a measurement, so the first-order surge has settled.</summary>
    /// <remarks>
    /// Sixty seconds is ten surge time constants: the remaining error is <c>e^-10</c> of the
    /// commanded speed, four parts in a hundred thousand, which is two orders below every
    /// tolerance asserted here. Settling matters because the reach is a multiple of the speed,
    /// so measuring mid-acceleration would compare two numbers taken at different instants.
    /// </remarks>
    private const int SettleSteps = 3600;

    /// <summary>Cruise speed every commanded transit here is given, in metres per second.</summary>
    /// <remarks>Half the hull's maximum, so a following set can be added without hitting a ceiling.</remarks>
    private const double CruiseSpeedMps = 3.0;

    /// <summary>The hull every case in this suite is measured against.</summary>
    private static SurfaceProfile Profile => SurfaceProfile.SurfaceVessel;

    /// <summary>The water-relevant projection of <see cref="Profile"/>.</summary>
    private static VesselWaterProfile WaterProfile => VesselWaterProfile.From(Profile);

    /// <summary>Horizon the probe reaches over: one surge time constant plus the reaction allowance.</summary>
    /// <remarks>
    /// The integrator's own closed form. With the throttle cut a first-order surge relaxes over
    /// <see cref="SurfaceProfile.SurgeTimeConstantSec"/>, and two further steps pass before a
    /// commanded change of speed reaches the water — one for the probe to reach the navigator's
    /// next setpoint, one for the hull to begin answering it.
    /// </remarks>
    private static double LookaheadHorizonSec => Profile.SurgeTimeConstantSec + (2.0 * Dt);

    // ─── Where the probe is laid off ────────────────────────────────────────

    /// <summary>
    /// Under a beam set the probe is laid off along the track the vessel is actually making, not
    /// along the bow.
    /// </summary>
    /// <remarks>
    /// The crab angle here is not incidental — it is arithmetic. A hull making
    /// <c>CruiseSpeedMps</c> through the water with a beam drift of <c>c</c> across it travels at
    /// <c>atan(c / u)</c> off its own bow, near a quarter turn of a right angle at these figures,
    /// and the probe lands more than a hull length off the bow line as a result.
    /// <para>
    /// The expected course is rebuilt here from the published heading, the published speed
    /// through water and the basin's own current — never read back off
    /// <see cref="SurfaceDomainState.CourseOverGroundRad"/> — so this asserts the geometry rather
    /// than asserting that one field equals itself. It is cross-checked against the track the
    /// hull physically made across the same step, which is the definition an operator would use.
    /// </para>
    /// </remarks>
    [Fact]
    public void Under_A_Beam_Set_The_Probe_Follows_The_Track_And_Not_The_Bow()
    {
        var current = new Vector3(1.5f, 0f, 0f);
        var water = new Basin(current);
        var rig = new VesselRig(water, spawnDepthM: 8.0);

        rig.Apply(TransitTo(rig.Asset.AssetId, water.At(depthM: 8.0, northM: 4000.0)))
            .IsAccepted.Should().BeTrue();

        rig.Run(SettleSteps);

        var before = rig.SurfaceState();
        var from = rig.Asset.PositionEus;
        var probe = rig.StepAndReadProbe();
        var to = rig.Asset.PositionEus;

        double expectedCourse = CoordinateFrames.BearingFromEusVector(
            CoordinateFrames.BearingToEusVector(before.HeadingRad, before.SpeedThroughWaterMps)
                + (current * (float)Profile.PassiveCurrentCoupling),
            before.HeadingRad);

        double crabRad = Math.Abs(SurfaceNavigator.ShortestTurnRad(expectedCourse, before.HeadingRad));

        crabRad.Should().BeApproximately(
            Math.Atan2(
                current.X * Profile.PassiveCurrentCoupling, before.SpeedThroughWaterMps),
            0.02,
            "the crab angle a beam set produces is arithmetic: the cross-set over the speed "
            + "through the water, which at these figures is some twenty-five degrees");

        Bearing(from, probe).Should().BeApproximately(
            expectedCourse, 0.02,
            "the probe is laid off along the course made good, which is where this vessel is "
            + "going — a probe along the bow inspects water it will never enter");

        Bearing(from, probe).Should().BeApproximately(
            Bearing(from, to), 0.02,
            "and the course made good is the track the hull physically made across this step, "
            + "which is the definition an operator would recognise");

        OffsetFromBowLine(from, probe, before.HeadingRad).Should().BeGreaterThan(
            Profile.LengthM,
            "a probe on the bow line would be more than a hull length away from the water this "
            + "vessel is actually about to occupy");
    }

    /// <summary>The probe reaches as far as the vessel makes good over the ground, not as far as it swims.</summary>
    /// <remarks>
    /// Two runs differing only in a following set. The commanded speed through the water is the
    /// same in both, so a reach computed from the surge cannot tell them apart — and a hull with
    /// two metres a second of set under her is committed to half as much water again in the same
    /// six seconds. The exact reach is asserted, not merely its ordering, because "longer" would pass
    /// against any quantity that happened to grow.
    /// </remarks>
    [Fact]
    public void The_Probe_Reach_Scales_With_Speed_Over_Ground()
    {
        var slack = MeasureReach(Vector3.Zero);
        var following = MeasureReach(new Vector3(0f, 0f, -2.0f));

        slack.ReachM.Should().BeApproximately(
            ExpectedReachM(slack.SpeedOverGroundMps), 0.05,
            "in slack water the ground made good and the water flowing past are the same number, "
            + "and the reach is a hull radius plus that speed over the coast horizon");

        following.ReachM.Should().BeApproximately(
            ExpectedReachM(following.SpeedOverGroundMps), 0.05,
            "with the set under her the vessel covers ground faster than her log reads, and the "
            + "probe has to reach the water she will actually be in");

        (following.ReachM - Profile.FootprintRadiusM).Should().BeApproximately(
            (slack.ReachM - Profile.FootprintRadiusM)
                * (following.SpeedOverGroundMps / slack.SpeedOverGroundMps),
            0.05,
            "the travelled part of the reach is proportional to speed over ground and to nothing "
            + "else");

        following.ReachM.Should().BeGreaterThan(
            slack.ReachM + 10.0,
            "the two vessels are making the same speed through the water, so a reach that read "
            + "the surge would report these two identical");
    }

    // ─── What the probe refuses ─────────────────────────────────────────────

    /// <summary>
    /// A vessel set sideways onto a shoal has the passage refused while it is still in navigable
    /// water, even though its bow never points at the shoal.
    /// </summary>
    /// <remarks>
    /// The set here is stronger than the hull can stem, so the vessel cannot correct its way back
    /// onto the line however it steers: it holds its bow to the north and goes east regardless.
    /// That is the case a bow-aligned probe is blind to by construction — the bed varies with
    /// easting alone, so a probe laid off due north samples exactly the depth the hull is already
    /// in and reports open water all the way onto the beach, and the first thing anybody hears
    /// about the shoal is the hull meeting it.
    /// <para>
    /// Advisory: refusing the passage is decision support over a modelled bed, not an assurance
    /// that the water the probe permitted is safe.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Vessel_Crabbing_Onto_A_Shoal_Stops_Short_Of_Water_Its_Bow_Never_Pointed_At()
    {
        var water = new Basin(new Vector3(4.0f, 0f, 0f));
        var rig = new VesselRig(water, spawnDepthM: 5.0);

        rig.Apply(TransitTo(rig.Asset.AssetId, water.At(depthM: 5.0, northM: 4000.0)))
            .IsAccepted.Should().BeTrue();

        var raised = rig.RunUntil(SurfaceAsset.BlockedCode, maxSteps: 4000);

        raised.Should().Contain(
            e => e.Code == SurfaceAsset.BlockedCode,
            "the water the vessel is being set into has to be refused, and nothing about the "
            + "bow direction ever will be");

        raised.Should().NotContain(
            e => e.Code == ShorelineContact.ShoalCode || e.Code == ShorelineContact.ShorelineCode,
            "the passage is refused by the look-ahead, so the hull meets nothing");

        double blockedEastingM = water.EastingForDepthM(
            UnderKeelClearance.MinimumNavigableDepthM(WaterProfile));

        rig.Asset.PositionEus.X.Should().BeLessThan(
            (float)(blockedEastingM - (2.0 * Profile.LengthM)),
            "stopping short means being clear of the refused water by more than twice the hull's "
            + "own length when the way comes off, not stopping on the edge of it");

        var state = rig.SurfaceState();

        state.WaterDepthM.Should().BeGreaterThan(
            UnderKeelClearance.MinimumNavigableDepthM(WaterProfile),
            "the vessel is refused while it still has its advisory margin intact");

        Math.Abs(SurfaceNavigator.ShortestTurnRad(state.CourseOverGroundRad, state.HeadingRad))
            .Should().BeGreaterThan(
                0.5,
                "the whole point of the case is that the bow and the track are half a radian "
                + "apart — with them aligned the old geometry would have found the shoal too");
    }

    /// <summary>
    /// A hull already inside its own clearance advisory is not refused by its own track, so it can
    /// still be driven off the shoal the set has pinned it against.
    /// </summary>
    /// <remarks>
    /// The counterweight to the case above, and the reason the probe stands down once the hull is
    /// in the band its clearance advisory covers. A vessel pinned on the boundary the mask drew
    /// sits within a reach of it, so <em>every</em> track it can make starts with an inshore
    /// component — including the tracks that end in deep water. A probe that kept refusing there
    /// would take the throttle away from the only manoeuvre that recovers the vessel, which is
    /// this domain's version of the immobilised rover: a hull that accepts every recovery order
    /// and executes none. Nothing is given up by standing down, because the mask refuses a move
    /// into blocked water whether the probe spoke or not.
    /// </remarks>
    [Fact]
    public void A_Hull_Inside_Its_Clearance_Advisory_Is_Not_Refused_By_Its_Own_Track()
    {
        var water = new Basin(new Vector3(0.35f, 0f, 0f));
        var rig = new VesselRig(water, spawnDepthM: 1.2);

        rig.RunUntil(ShorelineContact.ShoalCode, maxSteps: 4000).Should().Contain(
            e => e.Code == ShorelineContact.ShoalCode,
            "this case is about a pinned hull, so it has to be pinned first");

        float pinnedEastM = rig.Asset.PositionEus.X;

        rig.Apply(TransitTo(rig.Asset.AssetId, water.At(depthM: 6.0, northM: 0.0), Profile.MaxSpeedMps))
            .IsAccepted.Should().BeTrue();

        rig.Run(SettleSteps);

        rig.Asset.ModeToken.Should().NotBe(
            "blocked",
            "the look-ahead defers to the mask and the derate once the hull is in the band they "
            + "own; latching a block there would strand the vessel on the bank for good");

        rig.Asset.PositionEus.X.Should().BeLessThan(
            pinnedEastM - (float)Profile.LengthM,
            "and deferring is only worth anything if the vessel actually gets off: a minute "
            + "under command has to move it further than its own length into deeper water");
    }

    // ─── Shared measurement ─────────────────────────────────────────────────

    /// <summary>Runs one settled vessel and measures the reach of the probe it takes next.</summary>
    /// <param name="currentEus">Surface current in the scene frame, in metres per second.</param>
    /// <returns>The reach in metres and the speed over ground it was taken at.</returns>
    private static (double ReachM, double SpeedOverGroundMps) MeasureReach(Vector3 currentEus)
    {
        var water = new Basin(currentEus);
        var rig = new VesselRig(water, spawnDepthM: 8.0);

        rig.Apply(TransitTo(rig.Asset.AssetId, water.At(depthM: 8.0, northM: 6000.0)))
            .IsAccepted.Should().BeTrue();

        rig.Run(SettleSteps);

        double speedOverGroundMps = rig.SurfaceState().SpeedOverGroundMps;
        var from = rig.Asset.PositionEus;

        return (Distance(from, rig.StepAndReadProbe()), speedOverGroundMps);
    }

    /// <summary>The reach a hull making a given speed over the ground is committed to, in metres.</summary>
    /// <param name="speedOverGroundMps">Speed made good over the ground, in metres per second.</param>
    /// <returns>A hull radius plus the ground covered over the coast horizon.</returns>
    private static double ExpectedReachM(double speedOverGroundMps) =>
        Profile.FootprintRadiusM + (speedOverGroundMps * LookaheadHorizonSec);

    /// <summary>Horizontal bearing from one scene-frame point to another.</summary>
    /// <param name="fromEus">Point measured from.</param>
    /// <param name="toEus">Point measured to.</param>
    /// <returns>The bearing in radians clockwise from true north.</returns>
    private static double Bearing(Vector3 fromEus, Vector3 toEus) =>
        CoordinateFrames.BearingFromEusVector(toEus - fromEus);

    /// <summary>Horizontal distance between two scene-frame points, in metres.</summary>
    /// <param name="fromEus">Point measured from.</param>
    /// <param name="toEus">Point measured to.</param>
    /// <returns>The distance in metres, ignoring the vertical component.</returns>
    private static double Distance(Vector3 fromEus, Vector3 toEus)
    {
        double east = (double)toEus.X - fromEus.X;
        double south = (double)toEus.Z - fromEus.Z;
        return Math.Sqrt((east * east) + (south * south));
    }

    /// <summary>Perpendicular distance from a point to the ray running out along the bow, in metres.</summary>
    /// <remarks>
    /// The measurement that says plainly how wrong a bow-aligned probe would be: it is zero for
    /// every probe the old geometry could produce, whatever its reach.
    /// </remarks>
    /// <param name="fromEus">Vessel position in the scene frame.</param>
    /// <param name="pointEus">Point to measure, in the scene frame.</param>
    /// <param name="headingRad">Heading in radians clockwise from true north.</param>
    /// <returns>The offset in metres.</returns>
    private static double OffsetFromBowLine(Vector3 fromEus, Vector3 pointEus, double headingRad)
    {
        var bow = CoordinateFrames.BearingToEusVector(headingRad, 1.0);
        var offset = pointEus - fromEus;

        // The component of the offset across the bow line: |offset| sin(angle between), taken as
        // the magnitude of the 2-D cross product so no quadrant has to be reasoned about.
        return Math.Abs(((double)offset.X * bow.Z) - ((double)offset.Z * bow.X));
    }

    /// <summary>A validated transit command addressed to one vessel.</summary>
    /// <param name="assetId">Vessel the command is addressed to.</param>
    /// <param name="targetEus">Destination in the scene frame.</param>
    /// <param name="speedMps">Cruise speed, or the suite's default.</param>
    /// <returns>The translated command an asset executes.</returns>
    private static SimulatedAssetCommand TransitTo(
        string assetId, Vector3 targetEus, double? speedMps = null) =>
        new(
            Kind: AssetCommandKind.TransitTo,
            AssetId: assetId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity),
            SpeedMps: speedMps ?? CruiseSpeedMps);

    // ─── Test doubles ───────────────────────────────────────────────────────

    /// <summary>A basin whose bed is an exact plane shelving up towards the east.</summary>
    /// <remarks>
    /// <c>h(x, z) = BedAtOriginM + BedRisePerMetreEast * x</c> under a level surface, with a
    /// constant current so a set is arithmetic rather than an observation. Deterministic: every
    /// member is a pure function of position.
    /// <para>
    /// It records the points the asset probes, which is what lets these cases assert where the
    /// vessel looked rather than infer it from what it then did. The rig deliberately takes its
    /// own pre-step sample through <see cref="Basin.SampleQuietly"/> so the recording holds the asset's
    /// probes and nothing else — the alternative, indexing past the world's own sample, would
    /// quietly start measuring the wrong thing the day the step order changed.
    /// </para>
    /// </remarks>
    /// <param name="currentEus">Surface current in the scene frame, in metres per second.</param>
    private sealed class Basin(Vector3 currentEus) : IEnvironmentSampler
    {
        private readonly List<Vector3> _probes = [];

        /// <inheritdoc />
        public double SeaLevelM => SurfaceLookAheadTests.SeaLevelM;

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <summary>Points the asset under test has sampled, oldest first.</summary>
        public IReadOnlyList<Vector3> Probes => _probes;

        /// <inheritdoc />
        public double GetElevation(double x, double z) => BedAtOriginM + (BedRisePerMetreEast * x);

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) =>
            Vector3.Normalize(new Vector3((float)-BedRisePerMetreEast, 1f, 0f));

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM)
        {
            _probes.Add(positionEus);
            return SampleQuietly(positionEus);
        }

        /// <summary>Samples without recording, for probes the world rather than the asset takes.</summary>
        /// <param name="positionEus">Point to sample, in the scene frame.</param>
        /// <returns>A fully populated sample.</returns>
        public EnvironmentSample SampleQuietly(Vector3 positionEus)
        {
            double elevation = GetElevation(positionEus.X, positionEus.Z);
            bool isWater = elevation < SeaLevelM;

            return new EnvironmentSample(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: isWater ? currentEus : Vector3.Zero,
                TerrainElevationM: elevation,
                TerrainNormalEus: GetTerrainNormal(positionEus.X, positionEus.Z, 0.0),
                SurfaceMaterial: isWater ? SurfaceType.Water : SurfaceType.BareGround,
                WaterSurfaceElevationM: isWater ? SeaLevelM : null,
                BathymetricElevationM: isWater ? elevation : null,
                Zones: []);
        }

        /// <summary>Forgets every recorded probe.</summary>
        public void ForgetProbes() => _probes.Clear();

        /// <summary>East coordinate at which the water column is a given depth, in metres.</summary>
        /// <param name="depthM">Water column wanted, in metres.</param>
        /// <returns>The east coordinate in metres.</returns>
        public double EastingForDepthM(double depthM) =>
            (SeaLevelM - BedAtOriginM - depthM) / BedRisePerMetreEast;

        /// <summary>A scene-frame point at a chosen depth and a chosen distance north.</summary>
        /// <param name="depthM">Water column wanted, in metres.</param>
        /// <param name="northM">Distance north of the basin's origin, in metres.</param>
        /// <returns>A point in the scene frame, at the water surface.</returns>
        public Vector3 At(double depthM, double northM) =>
            new((float)EastingForDepthM(depthM), (float)SeaLevelM, (float)-northM);
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

    /// <summary>One vessel on one basin, plus the tick counter that drives it.</summary>
    /// <remarks>
    /// Mirrors what <see cref="AssetWorld"/> does per step — sample the environment at the asset's
    /// pre-step position, build a context, call <see cref="IStepDrivenAsset.Step"/> — without a
    /// world, so a case can be stated in depths and every quantity in it is exactly known.
    /// </remarks>
    private sealed class VesselRig
    {
        private readonly Random _random = new(FixedSeed);
        private readonly Basin _water;
        private long _tick;

        /// <summary>Floats a vessel at a chosen depth on a basin, heading north.</summary>
        /// <param name="water">Basin to float on and integrate over.</param>
        /// <param name="spawnDepthM">Water column at the spawn point, in metres.</param>
        public VesselRig(Basin water, double spawnDepthM)
        {
            _water = water;
            Asset = new SurfaceAsset(
                AssetProfiles.Create("usv-1", VehicleClass.SurfaceVessel),
                SurfaceDynamics.For(Profile),
                water,
                water.At(spawnDepthM, northM: 0.0),
                North);
        }

        /// <summary>The vessel under test.</summary>
        public SurfaceAsset Asset { get; }

        /// <summary>Advances the vessel by exactly one step.</summary>
        public void Step()
        {
            var before = Asset.PositionEus;
            _tick++;

            Asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: _tick * Dt,
                Tick: _tick,
                Environment: _water.SampleQuietly(before),
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

        /// <summary>Advances until an event code appears, or a hard bound is reached.</summary>
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

        /// <summary>Takes one step and returns the point the vessel probed ahead of itself.</summary>
        /// <remarks>
        /// The navigability probe is the first sample a powered vessel takes inside its own step —
        /// it is built before the motion is integrated, because the setpoint depends on its
        /// verdict — so the first recorded point is it. The recording is cleared first so nothing
        /// an earlier step took can be mistaken for this one's.
        /// </remarks>
        /// <returns>The probed point in the scene frame.</returns>
        public Vector3 StepAndReadProbe()
        {
            _water.ForgetProbes();
            Step();

            _water.Probes.Should().NotBeEmpty(
                "a vessel under power probes the water ahead of it on every step");

            return _water.Probes[0];
        }

        /// <summary>Applies a validated command to the vessel.</summary>
        /// <param name="command">Command to apply.</param>
        /// <returns>Acceptance, or a rejection carrying a machine-readable reason.</returns>
        public AssetCommandResult Apply(SimulatedAssetCommand command) => Asset.Apply(command);

        /// <summary>Narrows the captured state's domain extension to its surface form.</summary>
        /// <returns>The surface domain state.</returns>
        public SurfaceDomainState SurfaceState() => Asset.Capture(new AssetCaptureContext(
            Environment: _water,
            SimulationTimeSeconds: _tick * Dt,
            Tick: _tick,
            SourceTime: DateTimeOffset.UnixEpoch.AddSeconds(_tick * Dt),
            ReceiveTime: DateTimeOffset.UnixEpoch,
            Origin: null))
            .DomainState.Should().BeOfType<SurfaceDomainState>().Subject;
    }
}
