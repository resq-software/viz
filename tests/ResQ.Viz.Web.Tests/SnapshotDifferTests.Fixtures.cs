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
using System.Text.Json;
using FluentAssertions;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Tests;

// The wire records every case in this suite builds from, and the coverage counter the generated
// round trip reports through. Split from the cases the way the other v2 suites are split: reading
// what a case asserts should not mean scrolling past how its room was built. The type's summary
// lives on the primary declaration in SnapshotDifferTests.cs.
//
// Two properties below are load-bearing. Every frame is rebuilt from seeds, so no two frames
// share a collection instance — a fixture that handed the same list to both frames would let the
// differ's reference-equality shortcuts answer every question, and the suite would pass against a
// differ that compares nothing. And the volatile core — the timestamps, the sequence number, the
// link's last-heard stamp — advances for every asset on every frame exactly as a real capture
// stamps it, while observable state changes only when a case or the generator moves the asset.
// That is the condition the delta format exists for, so it is the condition the fixtures create.
public sealed partial class SnapshotDifferTests
{
    private const string OriginId = "origin-eus";

    private static readonly DateTimeOffset Epoch = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Serialises a wire record exactly as the hub would.</summary>
    /// <remarks>
    /// Used as the equality of record for the round-trip and purity cases. It is stricter than a
    /// structural comparison in the two ways that matter here: it distinguishes a null collection
    /// from an empty one, which this model treats as opposites, and it distinguishes two readings
    /// of the same instant recorded at different UTC offsets, which is precisely the exactness
    /// <c>Restamp</c> documents itself as preserving. It is also the form the client will parse,
    /// so a difference this test tolerates is a difference no client can see.
    /// </remarks>
    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, WireOptions);

    /// <summary>Everything the fixtures vary about one asset between frames.</summary>
    /// <remarks>
    /// Carries the descriptor's revision too, so an asset and its descriptor are added, bumped and
    /// removed as one thing and the two collections can never drift apart in a way the producer
    /// never would.
    /// </remarks>
    private readonly record struct AssetSeed(
        string Id,
        AssetDomain Domain,
        Vector3 Position,
        float Heading,
        double Battery,
        OperationalState Operational,
        ulong Sequence,
        long Revision);

    private readonly record struct TrackSeed(
        string Id,
        Vector3 Position,
        TrackClassification Classification,
        int UpdateCount,
        DateTimeOffset ObservedAt);

    private readonly record struct HazardSeed(
        string Id,
        Vector3 Centre,
        double RadiusM,
        HazardSeverity Severity,
        DateTimeOffset ObservedAt);

    private readonly record struct DetectionSeed(
        string Id,
        Vector3 Position,
        double Confidence,
        string SourceAssetId,
        DateTimeOffset DetectedAt);

    private readonly record struct LinkSeed(string Source, string Target, double Quality, double RangeM);

    /// <summary>A seed for one asset, stationary and healthy at the given place.</summary>
    private static AssetSeed Seeded(string id, AssetDomain domain, Vector3 position, long revision = 1) =>
        new(
            Id: id,
            Domain: domain,
            Position: position,
            Heading: 0.4f,
            Battery: 96.0,
            Operational: OperationalState.Active,
            Sequence: 1,
            Revision: revision);

    private static VehicleClass ClassOf(AssetDomain domain) => domain switch
    {
        AssetDomain.Air => VehicleClass.Multirotor,
        AssetDomain.Ground => VehicleClass.AckermannRover,
        _ => VehicleClass.SurfaceVessel,
    };

    private static string MobilityOf(AssetDomain domain) => domain switch
    {
        AssetDomain.Air => "multirotor",
        AssetDomain.Ground => "ackermann",
        _ => "displacement-hull",
    };

    private static string PrefixOf(AssetDomain domain) => domain switch
    {
        AssetDomain.Air => "uav",
        AssetDomain.Ground => "ugv",
        _ => "usv",
    };

    /// <summary>Builds the descriptor for a seed, with the display name keyed to the revision.</summary>
    /// <remarks>
    /// The name moves with the revision on purpose. A descriptor whose revision alone changed
    /// would be caught by full value equality anyway, but a bump that also changes a real field is
    /// the shape a re-configuration actually takes, and it makes an elision bug visible as a stale
    /// label rather than as a number nothing renders.
    /// </remarks>
    private static AssetDescriptor Descriptor(string id, AssetDomain domain, long revision) => new(
        AssetId: id,
        DisplayName: $"{id} (rev {revision})",
        Domain: domain,
        VehicleClass: ClassOf(domain),
        MobilityModel: MobilityOf(domain),
        AgencyId: "agency-1",
        FleetId: "fleet-1",
        Vendor: "vendor",
        Model: "model",
        Capabilities: AssetCapability.Arm | AssetCapability.Navigate2D | AssetCapability.MeshRelay,
        Dimensions: new PhysicalDimensions(2.0, 1.5, 0.8, 24.0, 1.2),
        Motion: new MotionConstraints(0.0, 18.0, 0.0, true, 0.0, 0.0),
        VisualProfile: $"{PrefixOf(domain)}-default",
        Revision: revision);

    /// <summary>Projects a seed into the state a capture at <paramref name="at"/> would publish.</summary>
    private static AssetState State(in AssetSeed seed, DateTimeOffset at) => new(
        AssetId: seed.Id,
        SourceTime: at,
        ReceiveTime: at,
        SequenceNumber: seed.Sequence,
        Freshness: DataFreshness.Fresh,
        Pose: new FramedPose(
            CoordinateFrame.LocalEus,
            OriginId,
            seed.Position,
            Quaternion.CreateFromYawPitchRoll(seed.Heading, 0f, 0f),
            [0.25, 0.0, 0.0, 0.25]),
        Twist: new FramedTwist(
            CoordinateFrame.LocalEus,
            new Vector3(MathF.Sin(seed.Heading) * 4f, 0f, -MathF.Cos(seed.Heading) * 4f),
            Vector3.Zero,
            OriginId,
            [0.1, 0.1]),
        OperationalState: seed.Operational,
        Mode: "auto",

        // Sources holds a freshly constructed element on every projection, exactly as all three
        // domain captures do. That single allocation is what makes record equality unusable and
        // the differ's element-wise walk necessary, and one case asserts it directly.
        Power: new PowerState(
            [new PowerSource("battery-1", PowerSourceKind.Battery, seed.Battery, DrawWatts: 180.0)],
            PercentRemaining: seed.Battery),
        Health: new HealthState(
            ComponentHealthStatus.Nominal,
            [new ComponentHealth("propulsion", ComponentHealthStatus.Nominal, "Within limits.")],
            [],
            "Nominal."),
        Link: new LinkState(
            LinkTransport.Mesh,
            IsConnected: true,
            LatencyMs: 18.0,
            MeshPath: [seed.Id, "base"],
            LastHeardAt: at),
        Mission: new MissionState(MissionExecutionState.Executing, RouteId: "route-1", WaypointCount: 4),
        DomainState: DomainState(seed));

    /// <summary>Builds the typed domain extension for a seed's domain.</summary>
    /// <remarks>
    /// The surface arm nests a <see cref="StationKeepState"/> whose target is a framed pose, which
    /// is the one place in the union where a collection hides below the top level. Including it in
    /// every generated room is what keeps the differ's surface branch on the tested path rather
    /// than on the path nothing exercises.
    /// </remarks>
    private static IAssetDomainState DomainState(in AssetSeed seed) => seed.Domain switch
    {
        AssetDomain.Air => new AirDomainState(
            IsAirborne: true,
            HeadingRad: seed.Heading,
            CourseOverGroundRad: seed.Heading,
            GroundSpeedMps: 4.0,
            ClimbRateMps: 0.0,
            AltitudeAboveGroundM: seed.Position.Y,
            AltitudeAboveLaunchM: seed.Position.Y,
            AltitudeMslM: seed.Position.Y,
            WindSpeedMps: 3.5,
            WindDirectionRad: 1.2,
            LinkLossBehavior: LinkLossBehavior.ReturnToBase,
            PositionUncertaintyGrowthMps: 0.05),
        AssetDomain.Ground => new GroundDomainState(
            IsMoving: true,
            HeadingRad: seed.Heading,
            CourseOverGroundRad: seed.Heading,
            GroundSpeedMps: 2.5,
            SteeringAngleRad: 0.05,
            RollRad: 0.0,
            PitchRad: 0.02,
            TerrainElevationM: seed.Position.Y,
            SlopeRad: 0.03,
            SurfaceType: "BareGround",
            TractionCoefficient: 0.8,
            DeratedSpeedLimitMps: 3.0,
            RolloverRisk: 0.1,
            IsImmobilised: false,
            LinkLossBehavior: LinkLossBehavior.StopAndHold,
            PositionUncertaintyGrowthMps: 0.0),
        _ => new SurfaceDomainState(
            HeadingRad: seed.Heading,
            CourseOverGroundRad: seed.Heading,
            SpeedOverGroundMps: 3.0,
            SpeedThroughWaterMps: 3.2,
            SurgeMps: 3.0,
            SwayMps: 0.1,
            YawRateRadPerSec: 0.01,
            WaterSurfaceElevationM: 0.0,
            WaterDepthM: 12.0,
            DraftM: 0.6,
            UnderKeelClearanceM: 11.4,
            HasUnsafeUnderKeelClearance: false,
            CurrentSpeedMps: 0.4,
            CurrentDirectionRad: 2.0,
            WindSpeedMps: 5.0,
            WindDirectionRad: 0.8,
            IsInsideWaterMask: true,
            LinkLossBehavior: LinkLossBehavior.DriftAndAlert,
            PositionUncertaintyGrowthMps: 0.4,
            StationKeep: new StationKeepState(
                IsEngaged: true,
                Target: new FramedPose(
                    CoordinateFrame.LocalEus, OriginId, seed.Position, Quaternion.Identity, [0.5]),
                ToleranceRadiusM: 8.0,
                HeadingPolicy: StationKeepHeadingPolicy.IntoCurrent)),
    };

    private static ExternalTrackState Track(in TrackSeed seed) => new(
        TrackId: seed.Id,
        Classification: seed.Classification,
        Pose: new FramedPose(
            CoordinateFrame.LocalEus, OriginId, seed.Position, Quaternion.Identity, [0.4]),
        Twist: new FramedTwist(
            CoordinateFrame.LocalEus, new Vector3(1f, 0f, 0f), Vector3.Zero, OriginId, [0.3]),
        Sources: [new TrackSource("ais-1", TrackSourceKind.Transponder, seed.ObservedAt, 0.9)],
        Quality: new TrackQuality(0.85, PositionAccuracyM: 12.0, UpdateCount: seed.UpdateCount),
        LastUpdateTime: seed.ObservedAt,
        Freshness: DataFreshness.Fresh,
        Label: seed.Id);

    private static HazardV2State Hazard(in HazardSeed seed) => new(
        HazardId: seed.Id,
        Type: "fire",
        Centre: new FramedPose(
            CoordinateFrame.LocalEus, OriginId, seed.Centre, Quaternion.Identity, [1.0]),
        RadiusM: seed.RadiusM,
        Severity: seed.Severity,
        AffectedDomains: [AssetDomain.Air, AssetDomain.Ground],
        BaseHeightM: 0.0,
        TopHeightM: 60.0,
        ObservedAt: seed.ObservedAt,
        Label: seed.Id);

    private static DetectionV2State Detection(in DetectionSeed seed) => new(
        DetectionId: seed.Id,
        Type: "survivor",
        Pose: new FramedPose(
            CoordinateFrame.LocalEus, OriginId, seed.Position, Quaternion.Identity, [0.6]),
        SourceAssetId: seed.SourceAssetId,
        Confidence: seed.Confidence,
        DetectedAt: seed.DetectedAt,
        SensorId: "eo-1",
        Label: seed.Id);

    private static NetworkState Network(IReadOnlyList<LinkSeed> links, bool isPartitioned)
    {
        IReadOnlyList<NetworkLinkState> up =
        [
            .. links.Select(l => new NetworkLinkState(
                l.Source, l.Target, LinkTransport.Mesh, l.Quality, RangeM: l.RangeM)),
        ];

        IReadOnlyList<string> members =
            [.. links.Select(l => l.Source).Distinct(StringComparer.Ordinal)];

        return new NetworkState(up, isPartitioned, [members], BackhaulAvailable: true);
    }

    private static DateTimeOffset TimeOf(long tick) => Epoch.AddMilliseconds(tick * 100);

    /// <summary>Assembles one whole frame from seeds, at the time implied by its tick.</summary>
    private static VizSnapshotV2 Room(
        Guid frameId,
        long tick,
        IReadOnlyList<AssetSeed> assets,
        IReadOnlyList<TrackSeed>? tracks = null,
        IReadOnlyList<DetectionSeed>? detections = null,
        IReadOnlyList<HazardSeed>? hazards = null,
        NetworkState? network = null,
        string environmentRevision = "env-1",
        TransportState? transport = null,
        ScenarioSessionState? scenario = null)
    {
        var at = TimeOf(tick);

        return new VizSnapshotV2(
            SchemaVersion: VizSnapshotV2.CurrentSchemaVersion,
            FrameId: frameId,
            ServerTime: at,
            SimulationTimeSeconds: tick / 60.0,
            Tick: tick,
            Transport: transport ?? new TransportState(Paused: false, Speed: 1, Tick: tick),
            Descriptors: [.. assets.Select(a => Descriptor(a.Id, a.Domain, a.Revision))],
            Assets: [.. assets.Select(a => State(a, at))],
            Tracks: [.. (tracks ?? Array.Empty<TrackSeed>()).Select(t => Track(t))],
            Detections: [.. (detections ?? Array.Empty<DetectionSeed>()).Select(d => Detection(d))],
            Hazards: [.. (hazards ?? Array.Empty<HazardSeed>()).Select(h => Hazard(h))],
            Network: network,
            EnvironmentRevision: environmentRevision,
            Scenario: scenario);
    }

    /// <summary>Counts the transition shapes a generated run actually produced.</summary>
    /// <remarks>
    /// A property test over generated data is only as strong as the data. A run that happened
    /// never to remove an asset, never to clear the mesh and never to bump a descriptor would
    /// still pass the round trip, prove nothing about those paths, and say nothing about it. These
    /// counters turn that silence into a failure, so tuning the generator cannot quietly hollow
    /// out the property it feeds.
    /// </remarks>
    private sealed class DeltaCoverage
    {
        private int _elidedFrames;
        private int _arrivals;
        private int _removals;
        private int _descriptorChanges;
        private int _trackChanges;
        private int _hazardChanges;
        private int _detectionChanges;
        private int _networkChanges;
        private int _environmentChanges;
        private int _transportChanges;

        /// <summary>Records the shape of one encoded transition.</summary>
        /// <param name="previous">The frame the delta was computed against.</param>
        /// <param name="delta">The delta it produced.</param>
        public void Observe(VizSnapshotV2 previous, VizDeltaV2 delta)
        {
            var held = previous.Assets.Select(a => a.AssetId).ToHashSet(StringComparer.Ordinal);

            _elidedFrames += (delta.Assets.Count == 0 && delta.Carried.Count > 0) ? 1 : 0;
            _arrivals += delta.Assets.Any(a => !held.Contains(a.AssetId)) ? 1 : 0;
            _removals += delta.RemovedAssetIds.Count > 0 ? 1 : 0;
            _descriptorChanges += delta.Descriptors.Count > 0 ? 1 : 0;
            _trackChanges += (delta.Tracks.Count + delta.RemovedTrackIds.Count) > 0 ? 1 : 0;
            _hazardChanges += (delta.Hazards.Count + delta.RemovedHazardIds.Count) > 0 ? 1 : 0;
            _detectionChanges += delta.DetectionsChanged ? 1 : 0;
            _networkChanges += (delta.Network is not null || delta.NetworkCleared) ? 1 : 0;
            _environmentChanges += delta.EnvironmentRevision is not null ? 1 : 0;
            _transportChanges += delta.Transport is not null ? 1 : 0;
        }

        /// <summary>Fails when the run never exercised one of the shapes it is meant to cover.</summary>
        public void AssertEveryShapeWasExercised()
        {
            _elidedFrames.Should().BePositive("a run where no asset was ever elided would not test the format");
            _arrivals.Should().BePositive("an asset must arrive mid-stream");
            _removals.Should().BePositive("an asset must depart mid-stream");
            _descriptorChanges.Should().BePositive("a descriptor must be reconfigured mid-stream");
            _trackChanges.Should().BePositive("an external track must change mid-stream");
            _hazardChanges.Should().BePositive("a hazard zone must change mid-stream");
            _detectionChanges.Should().BePositive("the detection list must change mid-stream");
            _networkChanges.Should().BePositive("the mesh must change or be cleared mid-stream");
            _environmentChanges.Should().BePositive("the environment revision must move mid-stream");
            _transportChanges.Should().BePositive("the transport triple must resist elision at least once");
        }
    }
}
