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
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Two vessels sharing water: that the one astern eases for the one ahead rather than passing
/// through it.
/// </summary>
/// <remarks>
/// The same gap the ground domain had, in the domain where it is harder to close. A hull has no
/// brake — with the throttle cut its surge relaxes exponentially over its own time constant —
/// so the protection available is to ask for less way in time, and the standoff has to absorb
/// the way the vessel still carries after it has asked for none.
/// <para>
/// Two things differ from the ground fix and both come from this file's own stated convention.
/// The corridor follows the <em>track</em> rather than the bow, because a hull under a set is
/// not going where it is pointing; and the room the corridor finds is a closure rate over the
/// ground, so the set's component towards the vessel ahead has to come off it before it can be
/// used as a bound on surge. Probing a ground distance at a water-relative speed mis-states the
/// reach by the whole of the set, which is a mistake this domain has already shipped once.
/// </para>
/// </remarks>
public sealed class SurfaceConvoySeparationTests
{
    private const double Dt = 1.0 / 60.0;
    private const string LeadId = "vessel-lead";
    private const string FollowerId = "vessel-follower";

    /// <summary>Far enough that the follower is at cruise before it closes.</summary>
    /// <remarks>
    /// Every target in this suite sits thousands of metres beyond the lead, so no run ends with
    /// an arrival. A vessel that has reached its waypoint reports no way on, which reads exactly
    /// like one that was slowed — the first version of these cases measured that instead.
    /// </remarks>
    private const double StartSeparationM = 220.0;

    /// <summary>A vessel under way does not pass through a stopped one.</summary>
    [Fact]
    public void A_Vessel_Does_Not_Pass_Through_The_One_Ahead()
    {
        var water = new Basin();
        var convoy = new Convoy(water);

        convoy.Follower.Apply(TransitTo(FollowerId, NorthOf(StartSeparationM + 5000.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 300.0);

        convoy.GapM.Should().BeGreaterThan(
            0.0, "a hull that cannot stop has to ask for less way in time");
    }

    /// <summary>It closes up, rather than stopping a scenario away.</summary>
    /// <remarks>
    /// The other half. A convoy that halts two hundred metres apart is as wrong as one that
    /// overlaps, just less visibly — the ceiling degrades with the room rather than switching,
    /// so the follower should settle near its standoff.
    /// </remarks>
    [Fact]
    public void It_Closes_Up_Rather_Than_Stopping_A_Scenario_Away()
    {
        var water = new Basin();
        var convoy = new Convoy(water);

        convoy.Follower.Apply(TransitTo(FollowerId, NorthOf(StartSeparationM + 5000.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 300.0);

        convoy.GapM.Should().BeLessThan(60.0);
    }

    /// <summary>A vessel with open water ahead is not slowed at all.</summary>
    /// <remarks>
    /// The control. A separation rule that also slows a vessel with nothing in front of it would
    /// pass every case above while making the fleet useless.
    /// </remarks>
    [Fact]
    public void A_Vessel_With_Open_Water_Is_Not_Slowed()
    {
        var water = new Basin();
        var convoy = new Convoy(water, withLead: false);

        convoy.Follower.Apply(TransitTo(FollowerId, NorthOf(StartSeparationM + 5000.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 120.0);

        convoy.FollowerSpeedMps.Should().BeGreaterThan(
            0.5 * SurfaceProfile.SurfaceVessel.MaxSpeedMps, "there is nothing in front of it");
    }

    /// <summary>A vessel abeam on a parallel track is passed, not eased for.</summary>
    /// <remarks>
    /// The case a plain radius gets wrong, and the reason the probe projects onto a corridor.
    /// Two vessels working a search pattern alongside each other must not stall each other.
    /// </remarks>
    [Fact]
    public void A_Vessel_On_A_Parallel_Track_Does_Not_Slow_This_One()
    {
        var water = new Basin();
        var convoy = new Convoy(water, leadOffsetEastM: 120.0);

        convoy.Follower.Apply(TransitTo(FollowerId, NorthOf(StartSeparationM + 5000.0)))
            .IsAccepted.Should().BeTrue();

        convoy.Run(seconds: 120.0);

        convoy.FollowerSpeedMps.Should().BeGreaterThan(
            0.5 * SurfaceProfile.SurfaceVessel.MaxSpeedMps);
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────

    private static Vector3 NorthOf(double metres) => new(0f, 0f, (float)-metres);

    private static SimulatedAssetCommand TransitTo(string assetId, Vector3 targetEus) => new(
        Kind: AssetCommandKind.GoTo,
        AssetId: assetId,
        Target: new FramedPose(CoordinateFrame.LocalEus, null, targetEus, Quaternion.Identity),
        SpeedMps: null,
        CommandId: Guid.NewGuid());

    /// <summary>A follower at the origin and, unless suppressed, a vessel stopped ahead of it.</summary>
    /// <remarks>
    /// Both are stepped against one peer list built from the poses they held at the start of the
    /// step, which is the contract the asset world itself implements: a vessel can never observe
    /// another's post-step position, so the result does not depend on registry order.
    /// </remarks>
    private sealed class Convoy
    {
        private readonly Random _random = new(20260920);
        private readonly Basin _water;
        private readonly SurfaceAsset? _lead;

        public Convoy(Basin water, bool withLead = true, double leadOffsetEastM = 0.0)
        {
            _water = water;
            Follower = Spawn(FollowerId, Vector3.Zero);

            _lead = withLead
                ? Spawn(LeadId, new Vector3(
                    (float)leadOffsetEastM, 0f, (float)-StartSeparationM))
                : null;
        }

        public SurfaceAsset Follower { get; }

        public double FollowerSpeedMps => Math.Abs(Capture(Follower).SpeedOverGroundMps);

        /// <summary>Clear water between the two footprints, in metres. Negative means overlapping.</summary>
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

            for (var i = 1; i <= steps; i++)
            {
                var peers = Poses();
                double time = i * Dt;

                Step(Follower, peers, time, i);
                Follower.DrainEvents();

                if (_lead is not null)
                {
                    Step(_lead, peers, time, i);
                    _lead.DrainEvents();
                }
            }
        }

        private SurfaceAsset Spawn(string id, Vector3 positionEus) => new(
            AssetProfiles.Create(id, VehicleClass.SurfaceVessel),
            SurfaceDynamics.For(SurfaceProfile.SurfaceVessel),
            _water,
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

        private static PeerPose PoseOf(SurfaceAsset asset) => new(
            asset.AssetId,
            AssetDomain.Surface,
            asset.PositionEus,
            asset.Descriptor.Dimensions.FootprintRadiusM);

        private void Step(SurfaceAsset asset, IReadOnlyList<PeerPose> peers, double time, long tick) =>
            asset.Step(new AssetStepContext(
                DeltaSeconds: Dt,
                SimulationTimeSeconds: time,
                Tick: tick,
                Environment: _water.Sample(
                    asset.PositionEus, asset.Descriptor.Dimensions.FootprintRadiusM),
                Peers: peers,
                Random: _random));

        private SurfaceDomainState Capture(SurfaceAsset asset) =>
            asset.Capture(new AssetCaptureContext(
                Environment: _water,
                SimulationTimeSeconds: 0.0,
                Tick: 0,
                SourceTime: DateTimeOffset.UnixEpoch,
                ReceiveTime: DateTimeOffset.UnixEpoch,
                Origin: null)).DomainState as SurfaceDomainState
            ?? throw new InvalidOperationException("A surface asset must capture a surface state.");
    }

    /// <summary>Deep, still, unbounded water, so nothing but the other vessel can slow a hull.</summary>
    private sealed class Basin : IEnvironmentSampler
    {
        public double SeaLevelM => 0.0;

        public IWindField Wind { get; } = new NoWind();

        public double GetElevation(double x, double z) => -40.0;

        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) => new(
            PositionEus: positionEus,
            WindEus: Vector3.Zero,
            Visibility: 1.0,
            Precipitation: 0.0,
            SurfaceCurrentEus: Vector3.Zero,
            TerrainElevationM: -40.0,
            TerrainNormalEus: Vector3.UnitY,
            SurfaceMaterial: SurfaceType.Water,
            WaterSurfaceElevationM: 0.0,
            BathymetricElevationM: -40.0,
            Zones: []);

        private sealed class NoWind : IWindField
        {
            public double Visibility => 1.0;

            public double Precipitation => 0.0;

            public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
        }
    }
}
