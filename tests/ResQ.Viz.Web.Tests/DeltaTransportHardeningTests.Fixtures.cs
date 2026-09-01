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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ResQ.Viz.Web.Hubs;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Tests;

// The room, the doubles and the staged interleavings the hardening cases are written against.
// Split from the cases the way the other v2 suites are split - reading what a case asserts should
// not mean scrolling past how its room was built. The type's summary lives on the primary
// declaration in DeltaTransportHardeningTests.cs.
//
// NO MOCKS ON THE PAYLOAD PATH. RecordingBroadcaster keeps the published frames themselves, split
// by stream and with keyframes and deltas distinguishable, because every case here asserts on
// which stream published what shape - a verification-based double could only prove that a call
// happened. StalledV2Broadcaster, beside the cases in the other half of this class, is the same
// idea with its v2 sends left permanently in flight, for the one case that needs a slow peer
// rather than a staged one. The group manager is a mock precisely because one case needs to hook
// the group add, which is the only await a broadcast can land inside.
public sealed partial class DeltaTransportHardeningTests
{
    /// <summary>Room id for every case, chosen so the periodic keyframe cannot confuse a short run.</summary>
    /// <remarks>
    /// <see cref="SimulationRoom"/> staggers its periodic keyframe by an FNV-1a hash of the room
    /// id, so "every fiftieth frame" lands on a different frame in every room. This id hashes to
    /// phase 3, putting the first periodic keyframe at stream sequence 47; no case below publishes
    /// more than a dozen frames. The hash is stable across processes by construction — that is
    /// why the production code spells FNV-1a out instead of calling
    /// <see cref="string.GetHashCode()"/>, which is randomised per process.
    /// </remarks>
    private const string RoomId = "hardening-room";

    /// <summary>Mirror of <c>VizHub</c>'s private key for the room bound to a connection.</summary>
    /// <remarks>
    /// Restated rather than imported because the hub keeps it private, which is right: it is an
    /// implementation detail of the connection lifetime, not a contract. Every case that uses it
    /// asserts on a published frame afterwards, so a renamed key fails loudly here instead of
    /// quietly making a subscription case vacuous.
    /// </remarks>
    private const string ConnectionRoomKey = "sim.hub.room";

    /// <summary>Exported name of the backpressure drop counter, which both streams tag.</summary>
    private const string DropCounterName = "resq.viz.frames_dropped_backpressure";

    private const string AirId = "uav-1";

    private static readonly Vector3 AirSpawnEus = new(0f, 40f, 0f);

    // ─── Fixtures ───────────────────────────────────────────────────────────

    private static SimulationRoom CreateRoom() =>
        new(id: RoomId, ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>A room holding one real air asset, so a capture has something to project.</summary>
    private static SimulationRoom CreatePopulatedRoom()
    {
        var room = CreateRoom();
        room.AddDrone(AirId, AirSpawnEus, vendor: null);
        return room;
    }

    private static SimulationManager CreateManager(IFrameBroadcaster broadcaster) =>
        new(HubContext(), new VizFrameBuilder(), NullLoggerFactory.Instance, broadcaster);

    /// <summary>A hub whose connection is already bound to <paramref name="room"/>.</summary>
    /// <remarks>
    /// Stands in for a completed handshake without constructing the HTTP feature the real one
    /// resolves its room cookie from — the path <see cref="VizHubTests"/> already covers.
    /// <para>
    /// <paramref name="onAddToGroup"/> is what makes the join race stageable: the group add is the
    /// only await inside the subscription during which a broadcast can land, so running one from
    /// there reproduces the interleaving deterministically instead of racing a background loop.
    /// </para>
    /// </remarks>
    /// <param name="room">Room the connection is bound to.</param>
    /// <param name="connectionId">Connection id the hub reports.</param>
    /// <param name="onAddToGroup">Work performed while the group add is in flight, if any.</param>
    /// <returns>The hub and the group manager it drives membership through.</returns>
    private static (VizHub Hub, Mock<IGroupManager> Groups) CreateBoundHub(
        SimulationRoom room, string connectionId = "conn-1", Func<Task>? onAddToGroup = null)
    {
        var groups = new Mock<IGroupManager>();
        groups.Setup(g => g.AddToGroupAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => onAddToGroup?.Invoke() ?? Task.CompletedTask);
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
    private static IHubContext<VizHub> HubContext()
    {
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<VizHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    /// <summary>Unwraps the delta a published frame is expected to carry.</summary>
    /// <param name="frame">Frame expected to be a delta.</param>
    /// <returns>The delta it carries.</returns>
    private static VizDeltaV2 DeltaOf(PublishedFrame frame) => frame.Delta switch
    {
        { } delta => delta,
        _ => throw new InvalidOperationException("Published frame is a keyframe; a delta was expected."),
    };

    /// <summary>One frame published on a room's delta chain, in whichever shape it took.</summary>
    /// <param name="Keyframe">The complete frame published, or null when a delta was.</param>
    /// <param name="Delta">The delta published, or null when a keyframe was.</param>
    private sealed record PublishedFrame(VizSnapshotV2? Keyframe, VizDeltaV2? Delta)
    {
        /// <summary>True when this frame was a full snapshot.</summary>
        public bool IsKeyframe => Keyframe is not null;
    }

    /// <summary>Records what a broadcast tick published, without a transport in the way.</summary>
    /// <remarks>
    /// Deliberately not a mock: these cases assert on which stream published and in what shape,
    /// and a verification-based double would only ever prove that a call happened.
    /// </remarks>
    private sealed class RecordingBroadcaster : IFrameBroadcaster
    {
        /// <summary>Every v1 frame published, in order.</summary>
        public List<(string RoomId, VizFrame Frame)> Frames { get; } = [];

        /// <summary>Every v2 snapshot published to the full-snapshot group, in order.</summary>
        public List<(string RoomId, VizSnapshotV2 Snapshot)> Snapshots { get; } = [];

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
            Published.Add(new PublishedFrame(snapshot, null));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BroadcastDeltaAsync(
            string roomId, VizDeltaV2 delta, CancellationToken cancellationToken)
        {
            Published.Add(new PublishedFrame(null, delta));
            return Task.CompletedTask;
        }
    }

    /// <summary>Sums one of the host's counters, counting only measurements carrying one tag.</summary>
    /// <remarks>
    /// The two streams share <c>resq.viz.frames_dropped_backpressure</c> and are told apart by a
    /// <c>stream</c> tag, so a probe that ignored tags could not tell "the v1 frame was skipped"
    /// from "the delta chain was skipped" — which is the entire distinction these cases exist to
    /// assert.
    /// <para>
    /// The instruments are static and process-wide, and xUnit runs test classes in parallel, so a
    /// probe can only ever establish a lower bound: a concurrent suite inflates the total and
    /// never hides a missing increment.
    /// </para>
    /// </remarks>
    private sealed class TaggedCounterProbe : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        /// <summary>Starts listening to one instrument, filtered to one tag value.</summary>
        /// <param name="instrumentName">Exported name of the counter to sum.</param>
        /// <param name="tagKey">Tag key that selects the stream.</param>
        /// <param name="tagValue">Tag value to count.</param>
        public TaggedCounterProbe(string instrumentName, string tagKey, string tagValue)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == VizTelemetry.ServiceName
                    && string.Equals(instrument.Name, instrumentName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, tagKey, StringComparison.Ordinal)
                        && string.Equals(tag.Value as string, tagValue, StringComparison.Ordinal))
                    {
                        Interlocked.Add(ref _total, measurement);
                        return;
                    }
                }
            });
            _listener.Start();
        }

        /// <summary>Measurements carrying the probe's tag, summed since it started.</summary>
        public long Total => Interlocked.Read(ref _total);

        /// <inheritdoc />
        public void Dispose() => _listener.Dispose();
    }
}
