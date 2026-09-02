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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Tests;

// The fleet the budget cases are measured against, split from the cases the way the other v2
// suites are split: reading what a case asserts should not mean scrolling past how its fleet was
// built. The type's summary lives on the primary declaration in DifferEqualityBudgetTests.cs.
//
// Two properties here are load-bearing and neither is incidental. Every frame is rebuilt from the
// members, so no two frames share a collection instance — a fixture that handed the same list to
// both frames would let the differ's reference-equality shortcuts answer every question, and the
// suite would pass against a differ that compares nothing. And nothing about a held asset moves
// between frames except the volatile core and the pack, at the rates a real capture drains it at,
// because that is exactly the condition under which the carried channel was found empty.
public sealed partial class DifferEqualityBudgetTests
{
    private const string OriginId = "origin-eus";

    /// <summary>Session epoch every capture time in this suite is measured from.</summary>
    /// <remarks>
    /// Fixed rather than drawn from the clock, so a failure is replayable from the source alone.
    /// </remarks>
    private static readonly DateTimeOffset Epoch = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Serialises a wire record exactly as the hub would.</summary>
    /// <remarks>
    /// The equality of record for every round-trip assertion here, and stricter than a structural
    /// comparison in the way this suite needs: it distinguishes a null collection from an empty
    /// one, which the model treats as opposites, and it is the form a client actually parses, so a
    /// difference it tolerates is one no client can see.
    /// </remarks>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Everything a case varies about one fleet member.</summary>
    /// <remarks>
    /// The three energy figures are independent rather than derived from the percentage, so a
    /// case can move one past its quantum while the others stay put. A fixture that tied them
    /// together would let one comparison cover for a missing one.
    /// </remarks>
    private readonly record struct FleetMember(
        string Id,
        AssetDomain Domain,
        double Battery,
        double EnergyWh,
        double EnduranceSeconds,
        double DrawWatts,
        double DrainPerFrame);

    private static FleetMember Member(string id, AssetDomain domain, double battery) => new(
        Id: id,
        Domain: domain,
        Battery: battery,
        EnergyWh: 240.0,
        EnduranceSeconds: 1_800.0,
        DrawWatts: 180.0,
        DrainPerFrame: domain switch
        {
            AssetDomain.Air => AirDrainPerFrame,
            AssetDomain.Ground => GroundDrainPerFrame,
            _ => SurfaceDrainPerFrame,
        });

    /// <summary>One asset per domain, holding station, each draining at its measured rate.</summary>
    private static IReadOnlyList<FleetMember> HeldFleet() =>
    [
        Member("uav-1", AssetDomain.Air, 96.0),
        Member("uav-2", AssetDomain.Air, 71.5),
        Member("ugv-1", AssetDomain.Ground, 64.0),
        Member("usv-1", AssetDomain.Surface, 88.25),
    ];

    /// <summary>The same fleet as it reads after <paramref name="frames"/> frames of drain.</summary>
    private static IReadOnlyList<FleetMember> Drained(
        IReadOnlyList<FleetMember> fleet, long frames) =>
        [.. fleet.Select(m => m with { Battery = m.Battery - (frames * m.DrainPerFrame) })];

    /// <summary>Encodes a single transition between two one-frame-apart fleets.</summary>
    private static VizDeltaV2 Diff1(
        IReadOnlyList<FleetMember> before, IReadOnlyList<FleetMember> after) =>
        VizSnapshotDiffer.Diff(Frame(FrameId(0), 0, before), Frame(FrameId(1), 1, after), 1, 2);

    private static Guid FrameId(long tick) => new($"00000000-0000-0000-0000-{tick:D12}");

    private static DateTimeOffset TimeOf(long tick) => Epoch.AddMilliseconds(tick * 100);

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, WireOptions);

    /// <summary>One standing detection, stamped with the capture's own time.</summary>
    private static DetectionV2State DetectedAt(long tick, double confidence = 0.82) => new(
        DetectionId: "survivor-1",
        Type: "survivor",
        Pose: new FramedPose(
            CoordinateFrame.LocalEus, OriginId, new Vector3(12f, 0f, -4f), Quaternion.Identity),
        SourceAssetId: "uav-1",
        Confidence: confidence,
        DetectedAt: TimeOf(tick),
        SensorId: "eo-1");

    /// <summary>Assembles the frame a capture at <paramref name="tick"/> would publish.</summary>
    /// <remarks>
    /// Every collection is rebuilt from the members on every call, exactly as the real capture
    /// path does. Sharing an instance between two frames would let the differ's reference-equality
    /// shortcuts answer every question here and the suite would pass against a differ that
    /// compares nothing.
    /// </remarks>
    private static VizSnapshotV2 Frame(
        Guid frameId,
        long tick,
        IReadOnlyList<FleetMember> fleet,
        DetectionV2State? detection = null)
    {
        var at = TimeOf(tick);
        DetectionV2State[] detections = detection is null ? [] : [detection];

        return new VizSnapshotV2(
            SchemaVersion: VizSnapshotV2.CurrentSchemaVersion,
            FrameId: frameId,
            ServerTime: at,
            SimulationTimeSeconds: tick / 10.0,
            Tick: tick,
            Transport: new TransportState(Paused: false, Speed: 1, Tick: tick),
            Descriptors: [.. fleet.Select(Descriptor)],
            Assets: [.. fleet.Select(m => State(m, tick, at))],
            Tracks: [],
            Detections: detections,
            Hazards: [],
            Network: null,
            EnvironmentRevision: "env-1");
    }

    private static AssetDescriptor Descriptor(FleetMember member) => new(
        AssetId: member.Id,
        DisplayName: member.Id,
        Domain: member.Domain,
        VehicleClass: member.Domain switch
        {
            AssetDomain.Air => VehicleClass.Multirotor,
            AssetDomain.Ground => VehicleClass.AckermannRover,
            _ => VehicleClass.SurfaceVessel,
        },
        MobilityModel: "held",
        AgencyId: "agency-1",
        FleetId: "fleet-1",
        Vendor: "vendor",
        Model: "model",
        Capabilities: AssetCapability.Arm | AssetCapability.Navigate2D,
        Dimensions: new PhysicalDimensions(2.0, 1.5, 0.8, 24.0, 1.2),
        Motion: new MotionConstraints(0.0, 18.0, 0.0, true, 0.0, 0.0),
        VisualProfile: "default",
        Revision: 1);

    /// <summary>
    /// The state a held asset publishes: nothing moves except the volatile core and the pack.
    /// </summary>
    private static AssetState State(FleetMember member, long tick, DateTimeOffset at) => new(
        AssetId: member.Id,
        SourceTime: at,
        ReceiveTime: at,
        SequenceNumber: (ulong)tick + 1,
        Freshness: DataFreshness.Fresh,
        Pose: new FramedPose(
            CoordinateFrame.LocalEus,
            OriginId,
            new Vector3(10f, 30f, -5f),
            Quaternion.Identity,
            [0.25, 0.0, 0.0, 0.25]),
        Twist: new FramedTwist(
            CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero, OriginId, [0.1, 0.1]),
        OperationalState: OperationalState.Holding,
        Mode: "hold",
        Power: Power(member),
        Health: new HealthState(
            ComponentHealthStatus.Nominal,
            [new ComponentHealth("propulsion", ComponentHealthStatus.Nominal, "Within limits.")],
            [],
            "Nominal."),
        Link: new LinkState(
            LinkTransport.Mesh,
            IsConnected: true,
            LatencyMs: 18.0,
            MeshPath: [member.Id, "base"],
            LastHeardAt: at),
        Mission: new MissionState(MissionExecutionState.Idle),
        DomainState: null);

    /// <summary>The pack, rebuilt from scratch on every capture as all three domains do.</summary>
    private static PowerState Power(FleetMember member) => new(
        [
            new PowerSource(
                SourceId: "pack-a",
                Kind: PowerSourceKind.Battery,
                PercentRemaining: member.Battery,
                RemainingEnergyWh: member.EnergyWh,
                RemainingTime: TimeSpan.FromSeconds(member.EnduranceSeconds),
                DrawWatts: member.DrawWatts,
                VoltageV: 22.2),
        ],
        PercentRemaining: member.Battery,
        RemainingEnergyWh: member.EnergyWh,
        RemainingTime: TimeSpan.FromSeconds(member.EnduranceSeconds));
}
