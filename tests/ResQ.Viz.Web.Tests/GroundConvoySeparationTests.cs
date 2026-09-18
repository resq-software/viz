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
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Two rovers sharing ground: that the one behind stops for the one in front instead of driving
/// through it.
/// </summary>
/// <remarks>
/// Peer poses have been frozen pre-step and handed to every asset since the asset world was
/// written, precisely so "the first separation, collision or closest-point-of-approach advisory
/// has one obviously correct place to read from". Nothing ever read them. A rover's look-ahead
/// probe — its own stopping distance, laid off along its direction of travel — asked the terrain
/// whether it could continue and never asked the vehicles, so two rovers on the same track closed
/// to interpenetration with neither slowing and neither raising anything.
/// <para>
/// The fix rides the probe that was already there. Same reach, same travel heading, against the
/// peer list instead of the height field, feeding the one speed ceiling that binds autonomous and
/// manual driving alike.
/// </para>
/// </remarks>
public sealed class GroundConvoySeparationTests
{
    private const double Dt = 0.1;
    private const int RandomSeed = 20260918;
    private const double PlateauElevationM = 40.0;
    private const string LeadId = "rover-lead";
    private const string FollowerId = "rover-follower";

    /// <summary>Far enough that the follower reaches cruise before it closes.</summary>
    private const double StartSeparationM = 60.0;

    // ─── The separation ─────────────────────────────────────────────────────

    /// <summary>A rover driving at a stopped rover stops behind it rather than through it.</summary>
    /// <remarks>
    /// The lead is parked across the follower's route with a target well beyond it, so the
    /// follower has every reason to keep going and only the separation to stop it. The assertion
    /// is on the clear ground between footprints, which is what an observer sees: positive means
    /// a gap, zero means touching, negative means one model inside the other.
    /// </remarks>
    [Fact]
    public void A_Rover_Stops_Behind_A_Stopped_Rover_Instead_Of_Driving_Through_It()
    {
        var convoy = new Convoy();
        convoy.Follower.Apply(DriveTo(FollowerId, NorthOf(StartSeparationM + 40.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 120.0);

        convoy.GapM.Should().BeGreaterThan(
            0.0,
            "the follower's own stopping distance is what the probe is laid off over, so a "
            + "vehicle inside it is one the follower could not stop short of");
    }

    /// <summary>It stops close behind, not at a range that reads as a fault.</summary>
    /// <remarks>
    /// The other half of the same property, and the one that keeps the fix from being a blunt
    /// exclusion radius: a convoy that halts fifty metres apart is as wrong as one that overlaps,
    /// just less obviously. The ceiling degrades with the square root of the gap rather than
    /// switching, so the follower creeps in and settles near the standoff.
    /// </remarks>
    [Fact]
    public void It_Settles_Close_Behind_Rather_Than_Far_Back()
    {
        var convoy = new Convoy();
        convoy.Follower.Apply(DriveTo(FollowerId, NorthOf(StartSeparationM + 40.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 120.0);

        convoy.GapM.Should().BeLessThan(6.0);
    }

    /// <summary>The hold is reported once, as its own event, not as an immobilisation.</summary>
    /// <remarks>
    /// A vehicle that has simply stopped is indistinguishable from a broken one unless it says
    /// why. It must not say <c>ground.immobilised</c>: that means the surface will not carry the
    /// vehicle and calls for an operator, whereas this clears itself the moment the road does.
    /// </remarks>
    [Fact]
    public void The_Hold_Is_Reported_Once_And_Is_Not_An_Immobilisation()
    {
        var convoy = new Convoy();
        convoy.Follower.Apply(DriveTo(FollowerId, NorthOf(StartSeparationM + 40.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 120.0);

        var codes = convoy.FollowerEvents.Select(e => e.Code).ToList();

        codes.Should().Contain("ground.holdingForVehicle");
        codes.Count(c => c == "ground.holdingForVehicle").Should().Be(
            1, "a level reported every tick is how an event log becomes unreadable");
        codes.Should().NotContain("ground.immobilised");
    }

    /// <summary>A rover with open ground ahead is not slowed at all.</summary>
    /// <remarks>
    /// The control. A separation rule that also slows a vehicle with nothing in front of it would
    /// pass every test above while making the fleet useless, so the same run without a lead has
    /// to reach the platform's own cruise speed.
    /// </remarks>
    [Fact]
    public void A_Rover_With_Clear_Ground_Is_Not_Slowed()
    {
        var convoy = new Convoy(withLead: false);
        convoy.Follower.Apply(DriveTo(FollowerId, NorthOf(StartSeparationM + 40.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 30.0);

        convoy.FollowerSpeedMps.Should().BeGreaterThan(
            0.5 * Profile.MaxForwardSpeedMps, "there is nothing in front of it");
    }

    /// <summary>A rover abeam on a parallel track is passed, not braked for.</summary>
    /// <remarks>
    /// The case a radius test gets wrong, asserted through the whole asset rather than against
    /// the geometry alone: two rovers working alongside each other must not deadlock.
    /// </remarks>
    [Fact]
    public void A_Rover_On_A_Parallel_Track_Does_Not_Slow_This_One()
    {
        var convoy = new Convoy(leadOffsetEastM: 25.0);
        convoy.Follower.Apply(DriveTo(FollowerId, NorthOf(StartSeparationM + 40.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 30.0);

        convoy.FollowerSpeedMps.Should().BeGreaterThan(0.5 * Profile.MaxForwardSpeedMps);
        convoy.FollowerEvents.Select(e => e.Code).Should().NotContain("ground.holdingForVehicle");
    }

    /// <summary>A vehicle already at the standoff stays there instead of creeping in.</summary>
    /// <remarks>
    /// The feedback trap this fix nearly shipped with, and the reason the probe is laid off over
    /// a <em>gap</em> rather than a centre-to-centre range. A stopped vehicle's look-ahead has
    /// collapsed to its own footprint — no speed means no braking term and no reaction term — so
    /// a reach that did not cover the standoff would drop the vehicle ahead out of the corridor
    /// at precisely the moment this one came to rest behind it. The ceiling would lift, the rover
    /// would accelerate, the peer would reappear, and it would oscillate its way into contact.
    /// <para>
    /// Spawned already at the standoff rather than driven there, so the assertion is about
    /// visibility at rest and not about how the approach was flown.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(SpawnableGroundClasses))]
    public void A_Vehicle_Parked_At_The_Standoff_Does_Not_Creep_In(VehicleClass vehicleClass)
    {
        var convoy = new Convoy(vehicleClass: vehicleClass, atStandoff: true);
        double before = convoy.GapM;

        convoy.Follower.Apply(DriveTo(FollowerId, NorthOf(StartSeparationM + 40.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 20.0);

        convoy.GapM.Should().BeGreaterThan(
            0.0, "it began with clear ground and had a vehicle in front of it the whole time");
        convoy.GapM.Should().BeLessThan(
            before + 1.0, "nor should it have backed away from something it can see");
    }

    /// <summary>Every vehicle class with both a ground motion model and an asset descriptor.</summary>
    /// <remarks>
    /// Derived rather than listed, so a rover class added later is covered without anyone
    /// remembering to extend this file — which is the only way an invariant about "every
    /// platform" stays true. <c>LeggedRover</c> is excluded automatically: it has a ground
    /// profile but no asset descriptor, so nothing in the build can spawn one.
    /// </remarks>
    /// <returns>One row per spawnable ground class.</returns>
    public static TheoryData<VehicleClass> SpawnableGroundClasses()
    {
        var data = new TheoryData<VehicleClass>();

        foreach (var vehicleClass in Enum.GetValues<VehicleClass>())
        {
            if (GroundProfile.ForVehicleClass(vehicleClass) is null)
            {
                continue;
            }

            try
            {
                AssetProfiles.Create("probe", vehicleClass);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            data.Add(vehicleClass);
        }

        return data;
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────

    private static GroundProfile Profile => GroundProfile.ForVehicleClass(VehicleClass.TrackedRover)
        ?? throw new InvalidOperationException("The tracked rover has no ground motion model.");

    private static Vector3 NorthOf(double metres) => new(0f, 0f, (float)-metres);

    private static SimulatedAssetCommand DriveTo(string assetId, Vector3 targetEus) => new(
        Kind: AssetCommandKind.DriveTo,
        AssetId: assetId,
        Target: new FramedPose(CoordinateFrame.LocalEus, null, targetEus, Quaternion.Identity),
        SpeedMps: null,
        CommandId: Guid.NewGuid());

    /// <summary>A follower at the origin and, unless suppressed, a lead parked ahead of it.</summary>
    /// <remarks>
    /// Both rovers are stepped against one peer list built from the poses they held at the start
    /// of the step, which is the contract the asset world itself implements: asset <c>N</c> can
    /// never observe asset <c>N-1</c>'s post-step position, so the result does not depend on
    /// registry order.
    /// </remarks>
    private sealed class Convoy
    {
        private readonly Random _random = new(RandomSeed);
        private readonly Plateau _ground = new();
        private readonly GroundAsset? _lead;
        private readonly List<AssetEvent> _followerEvents = [];

        public Convoy(
            bool withLead = true,
            double leadOffsetEastM = 0.0,
            VehicleClass vehicleClass = VehicleClass.TrackedRover,
            bool atStandoff = false)
        {
            _vehicleClass = vehicleClass;
            _profile = GroundProfile.ForVehicleClass(vehicleClass)
                ?? throw new InvalidOperationException($"{vehicleClass} has no ground motion model.");

            Follower = Spawn(FollowerId, Vector3.Zero);

            // Centre to centre: the standoff is clear ground between footprints, so the two radii
            // go back in to place the lead where the follower should settle.
            double separation = atStandoff
                ? (2.0 * Follower.Descriptor.Dimensions.FootprintRadiusM)
                    + GroundNavigator.PeerStandoffM
                : StartSeparationM;

            _lead = withLead
                ? Spawn(LeadId, new Vector3((float)leadOffsetEastM, 0f, (float)-separation))
                : null;
        }

        private readonly VehicleClass _vehicleClass;
        private readonly GroundProfile _profile;

        public GroundAsset Follower { get; }

        public IReadOnlyList<AssetEvent> FollowerEvents => _followerEvents;

        public double FollowerSpeedMps => Math.Abs(Capture(Follower).GroundSpeedMps);

        /// <summary>Clear ground between the two footprints, in metres. Negative means overlapping.</summary>
        public double GapM
        {
            get
            {
                if (_lead is null)
                {
                    return double.PositiveInfinity;
                }

                var delta = _lead.PositionEus - Follower.PositionEus;

                return Math.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z))
                    - Follower.Descriptor.Dimensions.FootprintRadiusM
                    - _lead.Descriptor.Dimensions.FootprintRadiusM;
            }
        }

        public void Run(double seconds)
        {
            int steps = (int)Math.Round(seconds / Dt);

            for (int i = 1; i <= steps; i++)
            {
                var peers = Poses();
                double time = i * Dt;

                Step(Follower, peers, time, i);
                _followerEvents.AddRange(Follower.DrainEvents());

                if (_lead is not null)
                {
                    Step(_lead, peers, time, i);
                    _lead.DrainEvents();
                }
            }
        }

        private GroundAsset Spawn(string id, Vector3 positionEus) => new(
            AssetProfiles.Create(id, _vehicleClass),
            GroundDynamics.For(_profile),
            _ground,
            positionEus,
            headingRad: 0.0);

        private IReadOnlyList<PeerPose> Poses()
        {
            var poses = new List<PeerPose> { PoseOf(Follower) };

            if (_lead is not null)
            {
                poses.Add(PoseOf(_lead));
            }

            return poses;
        }

        private static PeerPose PoseOf(GroundAsset asset) => new(
            asset.AssetId,
            AssetDomain.Ground,
            asset.PositionEus,
            asset.Descriptor.Dimensions.FootprintRadiusM);

        private void Step(GroundAsset asset, IReadOnlyList<PeerPose> peers, double time, long tick) =>
            asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: time,
                Tick: tick,
                Environment: _ground.Sample(
                    asset.PositionEus, asset.Descriptor.Dimensions.FootprintRadiusM),
                Peers: peers,
                Random: _random));

        private GroundDomainState Capture(GroundAsset asset) =>
            asset.Capture(new AssetCaptureContext(
                Environment: _ground,
                SimulationTimeSeconds: 0.0,
                Tick: 0,
                SourceTime: DateTimeOffset.UnixEpoch,
                ReceiveTime: DateTimeOffset.UnixEpoch,
                Origin: null)).DomainState as GroundDomainState
            ?? throw new InvalidOperationException("A ground asset must capture a ground state.");
    }

    /// <summary>Flat, dry, level ground with nothing in it to refuse.</summary>
    /// <remarks>
    /// Deliberately featureless: every refusal in these cases has to come from the other vehicle,
    /// so a terrain that could also stop the rover would make the result ambiguous.
    /// </remarks>
    private sealed class Plateau : IEnvironmentSampler
    {
        public double SeaLevelM => PlateauElevationM - 100.0;

        public IWindField Wind { get; } = new NoWind();

        public double GetElevation(double x, double z) => PlateauElevationM;

        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) => new(
            PositionEus: positionEus,
            WindEus: Vector3.Zero,
            Visibility: 1.0,
            Precipitation: 0.0,
            SurfaceCurrentEus: Vector3.Zero,
            TerrainElevationM: PlateauElevationM,
            TerrainNormalEus: Vector3.UnitY,
            SurfaceMaterial: SurfaceType.BareGround,
            WaterSurfaceElevationM: null,
            BathymetricElevationM: null,
            Zones: []);

        /// <summary>No wind, no rain, unlimited visibility.</summary>
        private sealed class NoWind : IWindField
        {
            public double Visibility => 1.0;

            public double Precipitation => 0.0;

            public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
        }
    }
}
