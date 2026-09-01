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

using System.Diagnostics.Metrics;
using System.Numerics;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Tests;

// The world, the doubles and the chain assertions the delta cases are written against. Split
// from the cases the way the other v2 suites are split — reading what a case asserts should not
// mean scrolling past how its room was built. The type's summary lives on the primary
// declaration in DeltaStreamTests.cs.
//
// TWO DOUBLES AND NO MOCKS ON THE PAYLOAD PATH. RecordingBroadcaster keeps the published frames
// themselves, in order and with keyframes and deltas distinguishable, because every case here
// asserts on what a payload says rather than on the fact that a call happened — a
// verification-based double could not tell a delta that names the right base from one that does
// not. StaticAsset gives the ground and surface domains something to publish without dragging a
// height field or a water mask into a test about transport.
public sealed partial class DeltaStreamTests
{
    /// <summary>
    /// Room id for every case, chosen so the periodic keyframe cannot confuse a short run.
    /// </summary>
    /// <remarks>
    /// <see cref="SimulationRoom"/> staggers its periodic keyframe by an FNV-1a hash of the room
    /// id, so "every fiftieth frame" lands on a different frame in every room — which is the
    /// point of the stagger and is also a trap for a test that assumes frame two is a delta. This
    /// id hashes to phase 8, putting the room's first periodic keyframe at stream sequence 42;
    /// every run below stays well inside that, and <see cref="MaxChainFrames"/> is the guard rail.
    /// <para>
    /// The hash is stable across processes by construction — that is exactly why the production
    /// code spells FNV-1a out instead of calling <see cref="string.GetHashCode()"/>, which is
    /// randomised per process — so this is a fixed fact about this string rather than a property
    /// that can drift under the test.
    /// </para>
    /// </remarks>
    private const string RoomId = "chain-room";

    /// <summary>Longest run any case publishes, kept below this room's first periodic keyframe.</summary>
    private const int MaxChainFrames = 12;

    private const string AirId = "uav-1";
    private const string GroundId = "ugv-1";
    private const string SurfaceId = "usv-1";

    /// <summary>Asset spawned and removed mid-chain, so upserts and removals both carry something.</summary>
    private const string LateId = "ugv-late";

    /// <summary>Mirror of <c>VizHub</c>'s private key for the room bound to a connection.</summary>
    /// <remarks>
    /// Restated rather than imported because the hub keeps it private, which is right: it is an
    /// implementation detail of the connection lifetime, not a contract. Every case that uses it
    /// asserts on a subscriber count or a published frame afterwards, so a renamed key fails
    /// loudly here instead of quietly making a subscription case vacuous.
    /// </remarks>
    private const string ConnectionRoomKey = "sim.hub.room";

    /// <summary>Exported name of the backpressure drop counter.</summary>
    private const string DropCounterName = "resq.viz.frames_dropped_backpressure";

    private static readonly Vector3 AirSpawnEus = new(0f, 40f, 0f);
    private static readonly DateTimeOffset FixedInstant = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    private static SimulationRoom CreateRoom() =>
        new(id: RoomId, ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>A room holding one asset of each implemented domain.</summary>
    /// <remarks>
    /// The air asset is real, so a stepped run produces poses that genuinely move and deltas that
    /// genuinely carry upserts. The ground and surface assets are fixture assets: these cases are
    /// about what a broadcast publishes and what it names as its base, and a rover that needed a
    /// height field or a vessel that needed a water mask would fail them for reasons that have
    /// nothing to do with streaming.
    /// </remarks>
    private static SimulationRoom CreatePopulatedRoom()
    {
        var room = CreateRoom();
        room.AddDrone(AirId, AirSpawnEus, vendor: null);
        Register(room, new StaticAsset(AssetProfiles.Create(GroundId, VehicleClass.AckermannRover)));
        Register(room, new StaticAsset(AssetProfiles.Create(SurfaceId, VehicleClass.SurfaceVessel)));
        return room;
    }

    private static void Register(SimulationRoom room, ISimulatedAsset asset) =>
        room.TryAddAsset(asset, out var reasonCode).Should().BeTrue(
            "the fixture asset must register; refused with '{0}'", reasonCode);

    private static SimulationManager CreateManager(IFrameBroadcaster broadcaster) =>
        new(HubContext(), new VizFrameBuilder(), NullLoggerFactory.Instance, broadcaster);

    private static void Step(SimulationRoom room, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            room.StepOnce();
        }
    }

    /// <summary>A hub whose connection is already bound to <paramref name="room"/>.</summary>
    /// <remarks>
    /// Stands in for a completed handshake without constructing the HTTP feature the real one
    /// resolves its room cookie from — the path <see cref="VizHubTests"/> already covers.
    /// <para>
    /// The connection id is a parameter because the resync budget lives in the caller's own
    /// connection items: a case that needs to show one client cannot spend another's budget needs
    /// two genuinely distinct connections, not one hub called twice.
    /// </para>
    /// </remarks>
    /// <param name="room">Room the connection is bound to.</param>
    /// <param name="connectionId">Connection id the hub reports.</param>
    /// <returns>The hub and the group manager it drives membership through.</returns>
    private static (VizHub Hub, Mock<IGroupManager> Groups) CreateBoundHub(
        SimulationRoom room, string connectionId = "conn-1")
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        groups.Setup(g => g.RemoveFromGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(connectionId);
        context.Setup(c => c.Items).Returns(
            new Dictionary<object, object?> { [ConnectionRoomKey] = room });

        var hub = new VizHub(CreateSessions(), NullLogger<VizHub>.Instance)
        {
            Context = context.Object,
            Groups = groups.Object,
        };
        return (hub, groups);
    }

    private static RoomSessionService CreateSessions() =>
        new(new EphemeralDataProtectionProvider(),
            new SimulationManager(HubContext(), new VizFrameBuilder(), NullLoggerFactory.Instance),
            NullLogger<RoomSessionService>.Instance);

    /// <summary>A hub context that answers group lookups, for the constructions that need one.</summary>
    /// <remarks>
    /// Never actually sent through: the manager publishes via <see cref="RecordingBroadcaster"/>,
    /// and the hub cases drive group membership through <see cref="IGroupManager"/>.
    /// </remarks>
    private static IHubContext<VizHub> HubContext()
    {
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<VizHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    /// <summary>Runs one room through a fixed script of ticks, spawns and a removal.</summary>
    /// <remarks>
    /// The script is identical for every audience, which is what makes two runs comparable. The
    /// world is never stepped: what varies between the frames is a spawn and a removal, both
    /// deterministic, so a difference between two runs can only be the audience leaking into the
    /// stream rather than float noise from the physics.
    /// </remarks>
    /// <param name="broadcaster">Sink recording what the run published.</param>
    /// <param name="deltaSubscribers">Connections receiving keyframes and deltas.</param>
    /// <param name="snapshotSubscribers">Connections receiving full snapshots.</param>
    /// <param name="connections">Connections in the room overall.</param>
    /// <returns>A task that completes when the scripted run has finished.</returns>
    private static async Task DriveComparableRunAsync(
        RecordingBroadcaster broadcaster, int deltaSubscribers, int snapshotSubscribers, int connections)
    {
        var room = CreatePopulatedRoom();
        for (var i = 0; i < connections; i++)
        {
            room.IncrementConnections();
        }

        for (var i = 0; i < snapshotSubscribers; i++)
        {
            room.IncrementSnapshotSubscribers();
        }

        for (var i = 0; i < deltaSubscribers; i++)
        {
            room.IncrementDeltaSubscribers();
        }

        var manager = CreateManager(broadcaster);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        Register(room, new StaticAsset(AssetProfiles.Create(LateId, VehicleClass.AckermannRover)));
        await manager.BroadcastRoomAsync(room, CancellationToken.None);

        room.TryRemoveAsset(LateId, out _).Should().BeTrue();
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
        await manager.BroadcastRoomAsync(room, CancellationToken.None);
    }

    /// <summary>Asserts that a published run opens on a snapshot and that every delta names its predecessor.</summary>
    /// <remarks>
    /// Positional rather than self-referential: the base is checked against the frame that
    /// actually preceded this one <em>in the recording</em>, and the sequences against the
    /// recording's own indices. A producer that computed a coherent chain against the wrong
    /// frames would satisfy an assertion phrased in terms of the deltas alone.
    /// <para>
    /// Only valid for a run in which no tick was skipped, since a skip leaves the chain where it
    /// was while the recording does not grow — which is exactly the property the backpressure
    /// case asserts by hand instead of calling this.
    /// </para>
    /// </remarks>
    /// <param name="published">Frames published by one room, in order.</param>
    private static void AssertChainIsSound(IReadOnlyList<PublishedFrame> published)
    {
        published.Should().NotBeEmpty();
        published[0].IsKeyframe.Should().BeTrue(
            "a stream cannot open with a change to a picture the client does not hold");

        for (var i = 1; i < published.Count; i++)
        {
            if (published[i].Delta is not { } delta)
            {
                continue;
            }

            delta.BaseFrameId.Should().Be(published[i - 1].FrameId,
                "delta {0} must apply to the frame that immediately preceded it on the wire", i);
            delta.BaseSequence.Should().Be(i,
                "the base sequence is the chain key a client tests, and it is an equality check");
            delta.StreamSequence.Should().Be(i + 1,
                "the chain advances by exactly one per frame handed to the transport");
            delta.SchemaVersion.Should().Be(VizSnapshotV2.CurrentSchemaVersion,
                "a delta and the keyframes it interleaves with can never claim different schemas");
        }
    }

    /// <summary>Unwraps the delta a published frame is expected to carry.</summary>
    /// <remarks>
    /// A throw rather than a null-forgiving operator so a frame that turned out to be a keyframe
    /// names itself in the failure instead of arriving as a NullReferenceException three
    /// assertions later.
    /// </remarks>
    /// <param name="frame">Frame expected to be a delta.</param>
    /// <returns>The delta it carries.</returns>
    private static VizDeltaV2 DeltaOf(PublishedFrame frame) => frame.Delta switch
    {
        { } delta => delta,
        _ => throw new InvalidOperationException(
            $"Published frame {frame.FrameId} is a keyframe; a delta was expected."),
    };

    /// <summary>Unwraps the full snapshot a published frame is expected to carry.</summary>
    /// <param name="frame">Frame expected to be a keyframe.</param>
    /// <returns>The snapshot it carries.</returns>
    private static VizSnapshotV2 KeyframeOf(PublishedFrame frame) => frame.Keyframe switch
    {
        { } keyframe => keyframe,
        _ => throw new InvalidOperationException(
            $"Published frame {frame.FrameId} is a delta; a full snapshot was expected."),
    };

    /// <summary>One frame published on a room's delta chain, in whichever shape it took.</summary>
    /// <remarks>
    /// Exactly one of the two is non-null, mirroring <c>DeltaStreamFrame</c> on the server side.
    /// The projections below exist so a case can compare two runs without branching on the shape
    /// at every assertion: a keyframe's whole asset list and a delta's upsert list are both
    /// "what this frame said about assets", and a keyframe carries no sequence because a full
    /// snapshot does not put one on the wire.
    /// </remarks>
    /// <param name="Keyframe">The complete frame published, or null when a delta was.</param>
    /// <param name="Delta">The delta published, or null when a keyframe was.</param>
    private sealed record PublishedFrame(VizSnapshotV2? Keyframe, VizDeltaV2? Delta)
    {
        /// <summary>True when this frame was a full snapshot.</summary>
        public bool IsKeyframe => Keyframe is not null;

        /// <summary>Frame id, whichever shape carried it.</summary>
        public Guid FrameId => Keyframe?.FrameId ?? Delta?.FrameId ?? Guid.Empty;

        /// <summary>Simulation tick this frame describes.</summary>
        public long Tick => Keyframe?.Tick ?? Delta?.Tick ?? -1;

        /// <summary>Position in the chain, or null for a keyframe, which carries none on the wire.</summary>
        public long? StreamSequence => Delta?.StreamSequence;

        /// <summary>Position of the frame this one applies to, or null for a keyframe.</summary>
        public long? BaseSequence => Delta?.BaseSequence;

        /// <summary>Asset ids this frame published whole.</summary>
        public IReadOnlyList<string> UpsertedAssetIds => this switch
        {
            { Keyframe: { } keyframe } => keyframe.Assets.Select(a => a.AssetId).ToList(),
            { Delta: { } delta } => delta.Assets.Select(a => a.AssetId).ToList(),
            _ => [],
        };

        /// <summary>Descriptor ids this frame published.</summary>
        public IReadOnlyList<string> UpsertedDescriptorIds => this switch
        {
            { Keyframe: { } keyframe } => keyframe.Descriptors.Select(d => d.AssetId).ToList(),
            { Delta: { } delta } => delta.Descriptors.Select(d => d.AssetId).ToList(),
            _ => [],
        };

        /// <summary>Assets this frame declared gone. Always empty for a keyframe, which states the whole fleet.</summary>
        public IReadOnlyList<string> RemovedAssetIds => Delta?.RemovedAssetIds ?? [];

        /// <summary>Assets this frame re-stamped without re-sending.</summary>
        public IReadOnlyList<string> CarriedAssetIds =>
            Delta is { } delta ? delta.Carried.Select(c => c.AssetId).ToList() : [];
    }

    /// <summary>Records what a broadcast tick published, without a transport in the way.</summary>
    /// <remarks>
    /// Deliberately not a mock: these cases assert on the payloads themselves — the base a delta
    /// names, the ids it carries, the frame a chain reconstructs — and a verification-based
    /// double would only ever prove that a call happened.
    /// <para>
    /// Keyframes are kept apart from snapshots even though the payload shape is identical,
    /// because they go to different audiences: a case asserting that a full-snapshot subscriber is
    /// unaffected has to be able to tell "nothing reached the snapshot group" from "nothing was
    /// published at all". <see cref="Published"/> is the interleaved view of the delta chain, in
    /// the order the room advanced it.
    /// </para>
    /// </remarks>
    private sealed class RecordingBroadcaster : IFrameBroadcaster
    {
        /// <summary>Every v1 frame published, in order.</summary>
        public List<(string RoomId, VizFrame Frame)> Frames { get; } = [];

        /// <summary>Every v2 snapshot published to the full-snapshot group, in order.</summary>
        public List<(string RoomId, VizSnapshotV2 Snapshot)> Snapshots { get; } = [];

        /// <summary>Every v2 keyframe published to the delta group, in order.</summary>
        public List<(string RoomId, VizSnapshotV2 Snapshot)> Keyframes { get; } = [];

        /// <summary>Every v2 delta published, in order.</summary>
        public List<(string RoomId, VizDeltaV2 Delta)> Deltas { get; } = [];

        /// <summary>The delta chain as a client sees it: keyframes and deltas interleaved, in order.</summary>
        public List<PublishedFrame> Published { get; } = [];

        /// <inheritdoc />
        public Task BroadcastFrameAsync(string roomId, VizFrame frame, CancellationToken cancellationToken)
        {
            Frames.Add((roomId, frame));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BroadcastSnapshotAsync(
            string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add((roomId, snapshot));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BroadcastKeyframeAsync(
            string roomId, VizSnapshotV2 snapshot, CancellationToken cancellationToken)
        {
            Keyframes.Add((roomId, snapshot));
            Published.Add(new PublishedFrame(snapshot, null));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BroadcastDeltaAsync(
            string roomId, VizDeltaV2 delta, CancellationToken cancellationToken)
        {
            Deltas.Add((roomId, delta));
            Published.Add(new PublishedFrame(null, delta));
            return Task.CompletedTask;
        }
    }

    /// <summary>Sums one of the host's counters over the lifetime of the probe.</summary>
    /// <remarks>
    /// The instruments are static and process-wide, and xUnit runs test classes in parallel, so a
    /// probe can only ever establish a lower bound — a concurrent suite inflates the total and
    /// never hides a missing increment. That is enough for the property under test, which is that
    /// a dropped tick is counted at all rather than vanishing silently.
    /// </remarks>
    private sealed class CounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        /// <summary>Starts listening to one instrument on the host's meter.</summary>
        /// <param name="instrumentName">Exported name of the counter to sum.</param>
        public CounterProbe(string instrumentName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == VizTelemetry.ServiceName
                    && string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (_, measurement, _, _) => Interlocked.Add(ref _total, measurement));
            _listener.Start();
        }

        /// <summary>Measurements summed since the probe started.</summary>
        public long Total => Interlocked.Read(ref _total);

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A motionless asset of whatever domain its descriptor names.</summary>
    /// <remarks>
    /// Reports a fixed, fully-populated state and raises nothing, so it is stable across two runs
    /// of the same script — which is what lets the audience-independence case compare structure
    /// rather than floats. It exists so a broadcast has a ground and a surface asset to publish
    /// without dragging a terrain sampler, a water mask or a motion model into a test about what
    /// reaches the wire.
    /// </remarks>
    private sealed class StaticAsset : ISimulatedAsset
    {
        /// <summary>Builds the fixture asset from a descriptor.</summary>
        /// <param name="descriptor">Descriptor naming the id, class and domain.</param>
        public StaticAsset(AssetDescriptor descriptor) => Descriptor = descriptor;

        /// <inheritdoc />
        public string AssetId => Descriptor.AssetId;

        /// <inheritdoc />
        public AssetDomain Domain => Descriptor.Domain;

        /// <inheritdoc />
        public Vector3 PositionEus => Vector3.Zero;

        /// <inheritdoc />
        public AssetDescriptor Descriptor { get; }

        /// <inheritdoc />
        public AssetState Capture(in AssetCaptureContext context) =>
            new(
                AssetId: AssetId,
                SourceTime: FixedInstant,
                ReceiveTime: FixedInstant,
                SequenceNumber: 1,
                Freshness: DataFreshness.Fresh,
                Pose: new FramedPose(CoordinateFrame.LocalEus, null, PositionEus, Quaternion.Identity),
                Twist: new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),
                OperationalState: OperationalState.Ready,
                Mode: "idle",
                Power: new PowerState([], PercentRemaining: 100.0),
                Health: new HealthState(ComponentHealthStatus.Nominal, [], [], "Nominal."),
                Link: new LinkState(LinkTransport.Loopback, IsConnected: true, LastHeardAt: FixedInstant),
                Mission: null,
                DomainState: null);

        /// <inheritdoc />
        public AssetCommandResult Apply(in SimulatedAssetCommand command) => AssetCommandResult.Accepted;

        /// <inheritdoc />
        public IReadOnlyList<AssetEvent> DrainEvents() => [];
    }
}
