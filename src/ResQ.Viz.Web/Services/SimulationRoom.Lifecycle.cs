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

/// <summary>Something outside a room that has to hear when the room's population changes.</summary>
/// <remarks>
/// The room owns what exists; a few things outside it hold state <em>about</em> what exists, and
/// that state is wrong the moment an asset goes away. A control lease is the case this was built
/// for: it names an asset, so an asset removed while somebody holds one leaves an operator
/// reading as in command of a vehicle that is not there.
/// <para>
/// <b>Every method is called with the room's lock released.</b> An observer is free to call back
/// into the room — the control authority's presence probe does exactly that — and raising one
/// under <c>_lock</c> would let a request thread that holds the observer's lock and a tick thread
/// that holds the room's lock wait on each other. That is why each notification sits after the
/// <c>lock</c> block at its call site rather than inside it: moving one in would be a deadlock,
/// not a tidy-up.
/// </para>
/// <para>
/// <b>An implementation must not throw and must not block.</b> These run on the request thread
/// that removed an asset and on the 60 Hz tick loop, so an exception from one would surface as a
/// failed removal or a stalled room — neither of which is the observer's to cause.
/// </para>
/// </remarks>
public interface IRoomLifecycleObserver
{
    /// <summary>Baselines this observer before it becomes visible to reset notifications.</summary>
    /// <remarks>
    /// Called once under the room lock. It must not call back into the room, block or throw. The
    /// baseline prevents an older outside-lock notification from being mistaken for a new reset by
    /// an observer first registered after a newer world was already committed.
    /// </remarks>
    /// <param name="revision">Current committed world revision.</param>
    void InitializeWorldRevision(long revision);

    /// <summary>One asset has been removed from the room.</summary>
    /// <remarks>
    /// Raised after the removal, so a probe run from here already reports the asset as gone.
    /// </remarks>
    /// <param name="assetId">Identifier of the asset that was removed.</param>
    void OnAssetRemoved(string assetId);

    /// <summary>The room has replaced its world, discarding every asset in it.</summary>
    /// <remarks>
    /// Raised after the swap. Every asset the previous world held is gone, including ids the new
    /// world may go on to reuse, so what an observer holds about the old population is stale in
    /// full rather than in part.
    /// </remarks>
    /// <param name="revision">
    /// Monotonic room-local world revision. An observer retaining state may ignore a notification
    /// older than one it has already applied, because callbacks run outside the room lock and can
    /// overlap across concurrent replacements.
    /// </param>
    void OnWorldReset(long revision);

    /// <summary>A periodic pass for state that lapses on its own with nobody watching.</summary>
    /// <remarks>
    /// Raised on a slow cadence from the room's tick loop, so a session nobody is reading still
    /// retires what expired in it. Deliberately not per-tick: it exists to bound how long a
    /// lapsed thing lingers, not to be accurate to the millisecond.
    /// </remarks>
    void OnUpkeep();
}

// Room lifecycle notification: how state that lives outside a room learns that the room's
// population changed under it.
//
// In its own file rather than in SimulationRoom.Assets.cs because it is not part of the asset
// surface — nothing here reads or writes world state and none of it takes the room lock. It is
// the outbound half: three call sites in the room (remove, reset, tick) and a list of listeners,
// every one of them running with the lock released.
//
// THE ONE RULE. Never notify from inside _lock. See IRoomLifecycleObserver for why: an observer
// may call back into this room, and a notification raised under the lock turns that callback into
// a lock-order inversion against every other thread that reaches the observer first.
public sealed partial class SimulationRoom
{
    /// <summary>Real ticks between upkeep passes: one second at the loop's 60 Hz.</summary>
    /// <remarks>
    /// Slow on purpose. Upkeep exists so that something which lapsed in an unattended session
    /// becomes a record within a second or so of lapsing rather than whenever somebody next asks.
    /// Running it every tick would take a second lock sixty times a second per room to discover
    /// nothing had changed, and would not make the answer any more true.
    /// </remarks>
    private const int UpkeepEveryNTicks = 60;

    // Copy-on-write. Subscription happens once per observer and notification happens on the tick
    // loop, so the read path takes no lock at all and cannot be what makes a tick late. Volatile
    // because the tick thread has to see an array published by the request thread that
    // subscribed; the gate serialises the copy so two concurrent subscriptions cannot lose one.
    private readonly object _lifecycleGate = new();
    private volatile IRoomLifecycleObserver[] _lifecycleObservers = [];
    private int _upkeepTick;
    // Guarded by the room lock. Captured with each committed world swap and carried through the
    // outside-lock notification so observers can reject an older callback that resumes late.
    private long _worldRevision;

    /// <summary>Subscribes an observer to this room's asset lifecycle.</summary>
    /// <remarks>
    /// Idempotent by reference: subscribing the same observer twice registers it once, so a
    /// caller that cannot cheaply tell whether it already subscribed does not end up revoking the
    /// same lease twice and writing two records for one removal.
    /// <para>
    /// The room holds the observer for the rest of its own life, which suits an observer whose
    /// lifetime is exactly the room's — the control authority's is. One with a shorter life would
    /// be kept alive by this list, so it should not subscribe.
    /// </para>
    /// </remarks>
    /// <param name="observer">Observer to subscribe.</param>
    /// <exception cref="ArgumentNullException"><paramref name="observer"/> is null.</exception>
    public void AddLifecycleObserver(IRoomLifecycleObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        lock (_lock)
        {
            lock (_lifecycleGate)
            {
                var current = _lifecycleObservers;
                if (Array.IndexOf(current, observer) >= 0)
                {
                    return;
                }

                // Baseline before publishing the observer into the copy-on-write array. A delayed
                // notification can snapshot it immediately after publication, so doing this in the
                // opposite order leaves a window where revision 1 reaches an observer created on 2.
                observer.InitializeWorldRevision(_worldRevision);

                var grown = new IRoomLifecycleObserver[current.Length + 1];
                Array.Copy(current, grown, current.Length);
                grown[^1] = observer;
                _lifecycleObservers = grown;
            }
        }
    }

    /// <summary>Tells every observer that one asset has been removed.</summary>
    /// <remarks>Call with <c>_lock</c> released, after the removal has happened.</remarks>
    /// <param name="assetId">Identifier of the removed asset.</param>
    private void NotifyAssetRemoved(string assetId)
    {
        var observers = _lifecycleObservers;
        for (var i = 0; i < observers.Length; i++)
        {
            observers[i].OnAssetRemoved(assetId);
        }
    }

    /// <summary>Tells every observer that the world has been replaced.</summary>
    /// <remarks>Call with <c>_lock</c> released, after the new world is installed.</remarks>
    private void NotifyWorldReset(long revision)
    {
        var observers = _lifecycleObservers;
        for (var i = 0; i < observers.Length; i++)
        {
            try
            {
                observers[i].OnWorldReset(revision);
            }
            catch (Exception ex)
            {
                // The world is already committed and cannot be rolled back. One observer is not
                // allowed to turn that success into an ambiguous request failure or prevent the
                // remaining observers from releasing state tied to the old population.
                _logger.LogError(
                    ex,
                    "[room {RoomId}] World-reset observer {ObserverType} failed at revision {Revision}.",
                    Id,
                    observers[i].GetType().Name,
                    revision);
            }
        }
    }

    /// <summary>Runs the periodic upkeep pass every <see cref="UpkeepEveryNTicks"/> real ticks.</summary>
    /// <remarks>
    /// Called from the tail of <see cref="Tick"/>, with <c>_lock</c> released. It counts real
    /// ticks rather than world steps because what it drives lapses against the wall clock: a
    /// paused session advances no simulated time at all, and a paused session is exactly the one
    /// an operator has walked away from.
    /// </remarks>
    private void NotifyUpkeep()
    {
        var observers = _lifecycleObservers;
        if (observers.Length == 0)
        {
            return;
        }

        if (Interlocked.Increment(ref _upkeepTick) % UpkeepEveryNTicks != 0)
        {
            return;
        }

        for (var i = 0; i < observers.Length; i++)
        {
            observers[i].OnUpkeep();
        }
    }
}
