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

namespace ResQ.Viz.Web.Services;

// What a room knows about who is listening to which schema. Split out rather than added beside
// the live-connection counter in SimulationRoom.cs for the reason the other partials exist: that
// file owns the tick loop and was already at the size where adding to it is how a file stops
// being readable.
//
// Interlocked, not the room lock. These counters are touched from hub callbacks on arbitrary
// threads and read by the tick loop on every broadcast; taking the simulation lock to read a
// subscriber count would put hub traffic behind world stepping for no benefit, and there is
// nothing here to keep consistent with world state.
public sealed partial class SimulationRoom
{
    private int _snapshotSubscriberCount;

    /// <summary>Connections in this room currently receiving the v2 snapshot stream.</summary>
    /// <remarks>
    /// Always a subset of <see cref="ConnectionCount"/>: the v2 schema is opt-in, so a room full
    /// of v1 clients reports zero here. <see cref="SimulationManager"/> reads it on every
    /// broadcast tick and skips assembling a snapshot when it is zero — which is why a count is
    /// kept at all rather than group membership being left entirely to SignalR, since a hub
    /// context cannot be asked whether a group is empty.
    /// <para>
    /// Not a reaper input. A room with connections but no subscribers is perfectly healthy, and
    /// tying its lifetime to a schema nobody is obliged to use would reap sessions that are
    /// being watched.
    /// </para>
    /// </remarks>
    public int SnapshotSubscriberCount => Volatile.Read(ref _snapshotSubscriberCount);

    /// <summary>Increments the v2 subscriber counter when a connection opts into snapshots.</summary>
    /// <remarks>
    /// Called by <see cref="ResQ.Viz.Web.Hubs.VizHub.SubscribeSnapshots"/>, which is where the
    /// per-connection idempotency lives: this counter trusts its caller to have established that
    /// the connection was not already subscribed.
    /// </remarks>
    /// <returns>The subscriber count after the increment.</returns>
    public int IncrementSnapshotSubscribers()
    {
        Touch();
        return Interlocked.Increment(ref _snapshotSubscriberCount);
    }

    /// <summary>Decrements the v2 subscriber counter when a connection stops receiving snapshots.</summary>
    /// <remarks>
    /// Clamped at zero the same way <see cref="DecrementConnections"/> is, and for the same
    /// reason: a disconnect racing an unsubscribe must leave a truthful count rather than a
    /// negative one, which would then need two subscribers before the tick loop resumed building
    /// snapshots at all.
    /// </remarks>
    /// <returns>The subscriber count after the decrement, never below zero.</returns>
    public int DecrementSnapshotSubscribers()
    {
        Touch();
        var v = Interlocked.Decrement(ref _snapshotSubscriberCount);
        return v < 0 ? Interlocked.Exchange(ref _snapshotSubscriberCount, 0) : v;
    }
}
