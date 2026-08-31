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

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Hubs;

// The delta stream's client-callable surface. Split from VizHub.cs so the file that owns the
// handshake, the room binding and the v1 contract stays about those things — the same reason
// SimulationRoom is split — and so that the compatibility rule below is stated in one place
// rather than inferred from two group names.
//
// THE COMPATIBILITY RULE, ONCE. Three tiers, and the first two are untouched by anything here.
// A v1 client is in the room group and receives ReceiveFrame; it never learns any of this
// exists. A v2 snapshot client additionally calls SubscribeSnapshots and receives a complete
// ReceiveSnapshotV2 every frame, exactly as it did before deltas existed, with no code change of
// any kind. Only a client that calls SubscribeDeltas trades those full snapshots for keyframes
// plus deltas — and a server that has never heard of SubscribeDeltas rejects the invoke, which
// is a supported configuration and not an error: the client stays on full snapshots, which is
// what it does today.
public sealed partial class VizHub
{
    /// <summary>Client method that receives a <see cref="VizDeltaV2"/>.</summary>
    /// <remarks>
    /// Distinct from <see cref="ReceiveSnapshotMethod"/> for the reason that one is distinct from
    /// <see cref="ReceiveFrameMethod"/>: a client must never sniff a payload to learn which shape
    /// it is holding. Keyframes arrive on <see cref="ReceiveSnapshotMethod"/> precisely so a
    /// keyframe needs no special handling — it is a complete frame, and the handler that reads
    /// the snapshot stream reads it unchanged.
    /// </remarks>
    public const string ReceiveDeltaMethod = "ReceiveDeltaV2";

    /// <summary>Most resync requests one connection may spend per <see cref="KeyframeRequestWindow"/>.</summary>
    /// <remarks>
    /// A generous ceiling on purpose. A healthy client asks once per gap and gaps are rare, so
    /// five in ten seconds is already pathological; the budget exists to make a broken or hostile
    /// client visible and cheap, not to police a normal one. A client that exhausts it is not
    /// stranded — the periodic keyframe re-establishes its picture within five seconds with no
    /// request at all, which is the same backstop a client that cannot ask relies on.
    /// </remarks>
    private const int MaxKeyframeRequestsPerWindow = 5;

    /// <summary>Sliding window over which <see cref="MaxKeyframeRequestsPerWindow"/> is counted.</summary>
    private static readonly TimeSpan KeyframeRequestWindow = TimeSpan.FromSeconds(10);

    /// <summary>HubCallerContext.Items key recording whether this connection receives deltas.</summary>
    private const string ConnectionDeltaKey = "sim.hub.deltas";

    /// <summary>HubCallerContext.Items key holding this connection's resync request budget.</summary>
    private const string ConnectionKeyframeBudgetKey = "sim.hub.keyframe.budget";

    /// <summary>HubCallerContext.Items key recording that this connection has had its opening keyframe.</summary>
    /// <remarks>
    /// A connection's <em>first</em> delta subscription is the only keyframe it can force without
    /// spending budget, and this is what makes "first" mean once per connection rather than once
    /// per invoke. Charging it would take a request off every healthy client for nothing: the
    /// opening keyframe happens exactly once, it is the connection's first frame rather than a
    /// resync, and reaching it at all costs a completed handshake. Every subsequent force from
    /// this connection — a re-subscribe, an unsubscribe/re-subscribe cycle, an explicit
    /// <see cref="RequestKeyframe"/> — comes out of the same budget.
    /// </remarks>
    private const string ConnectionOpeningKeyframeKey = "sim.hub.deltas.opened";

    /// <summary>Computes the group name carrying a single room's keyframes and deltas.</summary>
    /// <remarks>
    /// Disjoint from <see cref="SnapshotGroupName"/> rather than a subset of it, which is the one
    /// place the delta stream differs structurally from the snapshot stream it layers on. A
    /// connection in both groups would receive a complete snapshot <em>and</em> a delta
    /// describing that same frame, ten times a second — strictly more traffic than not having
    /// deltas at all.
    /// <para>
    /// Still a subset of <see cref="RoomGroupName"/>: a delta subscriber keeps receiving the v1
    /// frame, because a client migrating one panel at a time needs it.
    /// </para>
    /// </remarks>
    /// <param name="roomId">Room the group belongs to.</param>
    /// <returns>The group name.</returns>
    public static string DeltaGroupName(string roomId) => $"room:{roomId}:v2d";

    /// <summary>Trades this connection's full v2 snapshots for keyframes plus deltas.</summary>
    /// <remarks>
    /// <b>Subscribing is itself the resync.</b> Joining raises the room's join barrier, so the
    /// first frame this connection can act on is a complete one — which means a joiner needs no
    /// special-casing at all: joining, reconnecting and recovering from a gap all end in the same
    /// message, handled by the same code. See <see cref="SimulationRoom.BeginDeltaJoin"/> for why
    /// a barrier held across the group add is needed and simply setting the keyframe flag on
    /// either side of it is not.
    /// <para>
    /// The rejected alternative was unicasting a snapshot to the caller here. It has an
    /// unavoidable ordering race — SignalR does not order a send to one caller against a
    /// concurrent group send — so a joiner could receive the delta for sequence N+1 before its
    /// private keyframe for N, detect a gap that never happened and burn a round trip anyway.
    /// Forcing the shared keyframe is one message, no unicast path and no race, and it
    /// self-limits: twenty simultaneous joins cost at most one extra keyframe, because the flag
    /// is read and cleared once per broadcast tick rather than once per join.
    /// </para>
    /// <para>
    /// <b>Every force is metered, because forcing one is not a private act.</b> A keyframe goes
    /// to the whole delta group, so a client that can force one at will can hold every other
    /// subscriber in the room on full snapshots — the exact benefit the delta stream exists to
    /// provide, cancelled by one peer. Re-subscribing is therefore charged to the same
    /// per-connection budget <see cref="RequestKeyframe"/> spends from, and a re-subscribe that
    /// cannot pay is answered without forcing anything: the connection is already receiving the
    /// stream, so it loses nothing but the early answer and the periodic keyframe still reaches
    /// it within five seconds. Only the connection's first subscription is free, and only once —
    /// see <see cref="ConnectionOpeningKeyframeKey"/>.
    /// </para>
    /// <para>
    /// A <em>fresh</em> subscription that cannot pay is refused outright with a
    /// <see cref="HubException"/> rather than admitted without a keyframe. Admitting it would put
    /// a connection on the delta stream whose first message is a change to a picture it does not
    /// hold, which is the one thing this method exists to prevent; refusing leaves it exactly
    /// where it was, on full v2 snapshots, which carry strictly more than deltas do. This is what
    /// bounds a resubscribe loop: unsubscribing is free, so without it the cycle would force a
    /// room-wide rebuild on every pass.
    /// </para>
    /// <para>
    /// Unsubscribing restores whatever <see cref="SubscribeSnapshots"/> last asked for, so a
    /// client can fall back to full snapshots — after a merge it could not complete, say —
    /// without having to re-establish anything.
    /// </para>
    /// <para>
    /// Idempotent in both directions, and per connection: SignalR group membership does not
    /// survive a reconnect, so a client calls this again from its reconnected handler, which is
    /// the same rule the room group and the snapshot group already follow. Calling it again is
    /// also how a client asks for a fresh start, since a re-subscribe requests a keyframe.
    /// </para>
    /// </remarks>
    /// <param name="subscribed"><see langword="true"/> to receive keyframes and deltas; <see langword="false"/> to stop.</param>
    /// <returns>The schema version this server stamps into every keyframe and delta it sends.</returns>
    /// <exception cref="HubException">
    /// A fresh subscription was refused because this connection has exhausted its keyframe
    /// budget. The connection is left on whatever it was receiving before the call.
    /// </exception>
    public async Task<string> SubscribeDeltas(bool subscribed)
    {
        if (!Context.Items.TryGetValue(ConnectionRoomKey, out var roomObj) || roomObj is not SimulationRoom room)
        {
            _logger.LogWarning("SubscribeDeltas from {ConnectionId} with no bound room; ignoring.",
                Context.ConnectionId);
            return VizSnapshotV2.CurrentSchemaVersion;
        }

        var already = Context.Items.TryGetValue(ConnectionDeltaKey, out var flag) && flag is true;

        if (!subscribed)
        {
            if (already)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, DeltaGroupName(room.Id));
                Context.Items[ConnectionDeltaKey] = false;
                room.DecrementDeltaSubscribers();
                await RestoreSnapshotGroupAsync(room);
                LogSubscription(room, subscribed: false);
            }

            return VizSnapshotV2.CurrentSchemaVersion;
        }

        if (already)
        {
            // Re-subscribing is a resync request, not a no-op: a client that has lost its place
            // and re-invokes rather than calling RequestKeyframe must not be silently ignored —
            // and, for the same reason, must not reach the room's keyframe flag unmetered.
            TryForceKeyframe(room, "re-subscribe");
            return VizSnapshotV2.CurrentSchemaVersion;
        }

        if (!TryChargeOpeningKeyframe())
        {
            VizTelemetry.KeyframeRequestsRejected.Add(1);
            _logger.LogWarning(
                "Client {ConnectionId} exhausted its keyframe budget re-joining the delta stream for "
                + "room {RoomId}; refusing the subscription and leaving it on full snapshots.",
                Context.ConnectionId, room.Id);
            throw new HubException(
                "Delta subscription refused: this connection has exhausted its keyframe budget. "
                + "Full v2 snapshots are unaffected.");
        }

        // The barrier is raised before any membership change and lowered only once the resync
        // flag has been re-armed, so no frame decided from here on can be a delta.
        room.BeginDeltaJoin();
        try
        {
            await SuspendSnapshotGroupAsync(room);
            await Groups.AddToGroupAsync(Context.ConnectionId, DeltaGroupName(room.Id));
            Context.Items[ConnectionDeltaKey] = true;
            room.IncrementDeltaSubscribers();
        }
        finally
        {
            room.EndDeltaJoin();
        }

        VizTelemetry.KeyframesRequested.Add(1);
        LogSubscription(room, subscribed: true);
        return VizSnapshotV2.CurrentSchemaVersion;
    }

    /// <summary>Asks the server to make this room's next broadcast a full keyframe.</summary>
    /// <remarks>
    /// <b>What a client calls when it cannot place a delta on the frame it holds.</b> The gap
    /// test is an equality check and nothing more: a delta names the frame it applies to, so a
    /// client accepts it if and only if that is the frame it is holding. Everything else — a
    /// reordered delta, a descriptor it cannot resolve, a merge that threw — funnels into the
    /// same three steps: forget the held frame, keep rendering the last good picture, call this.
    /// <para>
    /// <b>Bounded by construction, then rate limited on top.</b> The server-side bound is the
    /// stronger of the two: the room holds a flag, not a queue, and reads it once per broadcast
    /// tick, so a client invoking this in a tight loop drives the room to a keyframe on every
    /// broadcast — which is exactly what a full-snapshot subscriber receives today. There is no
    /// input to this method that makes a room more expensive than not using deltas at all. The
    /// per-connection budget on top of that bounds what one client can do to <em>its peers</em>:
    /// a forced keyframe replaces the delta for every subscriber in the room, so an unmetered
    /// path here would let one client cancel the delta stream's benefit for all of them.
    /// </para>
    /// <para>
    /// <b>Every path that can force a keyframe is charged here.</b> This method and both
    /// keyframe-forcing branches of <see cref="SubscribeDeltas"/> share one budget and one
    /// accounting helper, because a limit one caller can walk around by invoking a different
    /// method is not a limit. The single exception is the server's own repair after a failed
    /// send — see <c>SimulationManager.BroadcastRoomAsync</c> — which no client can provoke at
    /// will and which must not be refusable.
    /// </para>
    /// <para>
    /// Rejected calls are safe to ignore: the periodic keyframe re-establishes the client's
    /// picture within five seconds regardless, so a rate-limited client recovers on the same path
    /// as one whose invoke never arrived.
    /// </para>
    /// <para>
    /// Restricted to connections actually receiving deltas. A full-snapshot subscriber already
    /// gets a complete frame ten times a second and has nothing to resynchronise, so answering it
    /// would let any connection in the room spend the room's keyframe budget.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the request was accepted and the next broadcast for this room
    /// will be a keyframe; <see langword="false"/> when the caller is not receiving deltas or has
    /// exhausted its budget.
    /// </returns>
    public Task<bool> RequestKeyframe()
    {
        if (!Context.Items.TryGetValue(ConnectionRoomKey, out var roomObj) || roomObj is not SimulationRoom room)
        {
            _logger.LogWarning("RequestKeyframe from {ConnectionId} with no bound room; ignoring.",
                Context.ConnectionId);
            return Task.FromResult(false);
        }

        if (!(Context.Items.TryGetValue(ConnectionDeltaKey, out var flag) && flag is true))
        {
            _logger.LogWarning(
                "RequestKeyframe from {ConnectionId} in room {RoomId}, which is not receiving deltas; ignoring.",
                Context.ConnectionId, room.Id);
            return Task.FromResult(false);
        }

        return Task.FromResult(TryForceKeyframe(room, "RequestKeyframe"));
    }

    /// <summary>Charges this connection for a keyframe and forces one if it could pay.</summary>
    /// <remarks>
    /// The single place a client-driven force reaches <see cref="SimulationRoom.RequestKeyframe"/>,
    /// so the budget, the accepted count and the rejected count cannot disagree about what
    /// happened. Every caller that can be reached from a hub invoke goes through here.
    /// </remarks>
    /// <param name="room">Room whose next broadcast would become a keyframe.</param>
    /// <param name="path">Hub surface the request arrived on, for the rejection log.</param>
    /// <returns><see langword="true"/> when the room was asked for a keyframe.</returns>
    private bool TryForceKeyframe(SimulationRoom room, string path)
    {
        if (!TryChargeKeyframeRequest())
        {
            VizTelemetry.KeyframeRequestsRejected.Add(1);
            _logger.LogWarning(
                "Client {ConnectionId} exceeded {Max} keyframe requests per {Seconds}s in room {RoomId} "
                + "via {Path}; rejecting. The periodic keyframe will resynchronise it regardless.",
                Context.ConnectionId, MaxKeyframeRequestsPerWindow,
                (int)KeyframeRequestWindow.TotalSeconds, room.Id, path);
            return false;
        }

        room.RequestKeyframe();
        VizTelemetry.KeyframesRequested.Add(1);
        return true;
    }

    /// <summary>Grants this connection its one free opening keyframe, or charges for the rest.</summary>
    /// <returns><see langword="true"/> when the connection may join the delta stream.</returns>
    private bool TryChargeOpeningKeyframe()
    {
        if (!(Context.Items.TryGetValue(ConnectionOpeningKeyframeKey, out var opened) && opened is true))
        {
            Context.Items[ConnectionOpeningKeyframeKey] = true;
            return true;
        }

        return TryChargeKeyframeRequest();
    }

    /// <summary>Records a subscription transition with the room's resulting audience.</summary>
    /// <param name="room">Room whose audience changed.</param>
    /// <param name="subscribed">Direction of the transition.</param>
    private void LogSubscription(SimulationRoom room, bool subscribed) =>
        _logger.LogInformation(
            "Client {ConnectionId} {Action} v2 deltas for room {RoomId} (deltas={Deltas}, snapshots={Snapshots}).",
            Context.ConnectionId, subscribed ? "subscribed to" : "unsubscribed from",
            room.Id, room.DeltaSubscriberCount, room.SnapshotSubscriberCount);

    /// <summary>Takes this connection out of the full-snapshot group, remembering that it was in it.</summary>
    /// <remarks>
    /// The client's opt-in is recorded separately from its group membership precisely so this is
    /// a suspension rather than a revocation — see <see cref="SubscribeSnapshots"/>.
    /// </remarks>
    /// <param name="room">Room whose snapshot group the connection is leaving.</param>
    private async Task SuspendSnapshotGroupAsync(SimulationRoom room)
    {
        if (!(Context.Items.TryGetValue(ConnectionSnapshotKey, out var flag) && flag is true))
        {
            return;
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, SnapshotGroupName(room.Id));
        Context.Items[ConnectionSnapshotKey] = false;
        room.DecrementSnapshotSubscribers();
    }

    /// <summary>Puts this connection back in the full-snapshot group if that is what it asked for.</summary>
    /// <param name="room">Room whose snapshot group the connection is rejoining.</param>
    private async Task RestoreSnapshotGroupAsync(SimulationRoom room)
    {
        var wanted = Context.Items.TryGetValue(ConnectionSnapshotIntentKey, out var intent) && intent is true;
        var counted = Context.Items.TryGetValue(ConnectionSnapshotKey, out var flag) && flag is true;
        if (!wanted || counted)
        {
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SnapshotGroupName(room.Id));
        Context.Items[ConnectionSnapshotKey] = true;
        room.IncrementSnapshotSubscribers();
    }

    /// <summary>Spends one unit of this connection's resync budget, if it has one left.</summary>
    /// <remarks>
    /// A fixed window rather than a token bucket: the quantity being limited is "how noisy is
    /// this client", the ceiling is far above any healthy usage, and a window that resets whole
    /// is a counter and a timestamp instead of a refill schedule. Per connection, so it dies with
    /// the connection and a reconnect starts fresh — which is correct, because a reconnect is
    /// itself a legitimate reason to want a keyframe.
    /// <para>
    /// SignalR serialises invocations from one connection by default, so the counter needs no
    /// synchronisation; it is reached only from this connection's own hub calls.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the request may proceed.</returns>
    private bool TryChargeKeyframeRequest()
    {
        var now = DateTimeOffset.UtcNow;
        if (!Context.Items.TryGetValue(ConnectionKeyframeBudgetKey, out var stored)
            || stored is not KeyframeRequestBudget budget)
        {
            budget = new KeyframeRequestBudget { WindowStart = now };
            Context.Items[ConnectionKeyframeBudgetKey] = budget;
        }

        if (now - budget.WindowStart >= KeyframeRequestWindow)
        {
            budget.WindowStart = now;
            budget.Count = 0;
        }

        if (budget.Count >= MaxKeyframeRequestsPerWindow)
        {
            return false;
        }

        budget.Count++;
        return true;
    }

    /// <summary>One connection's resync requests within the current window.</summary>
    private sealed class KeyframeRequestBudget
    {
        /// <summary>Start of the window <see cref="Count"/> is measured over.</summary>
        public DateTimeOffset WindowStart { get; set; }

        /// <summary>Requests charged so far in this window.</summary>
        public int Count { get; set; }
    }
}
