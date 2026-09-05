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

using System.Diagnostics.CodeAnalysis;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

/// <summary>One atomic reading of everything a v2 frame needs from a room.</summary>
/// <remarks>
/// Sampled under the room's single lock in one call rather than assembled from
/// <see cref="SimulationRoom.IsPaused"/>, <see cref="SimulationRoom.TickCount"/> and a separate
/// state capture. Three independently-locked getters can interleave with the 60 Hz tick loop and
/// produce a frame whose transport bar contradicts its own asset positions — the same reason
/// <see cref="SimulationRoom.TransportSnapshot"/> exists for v1.
/// <para>
/// Every collection here is a materialised copy, so the reading stays valid after the lock is
/// released. That is a correctness requirement, not an optimisation note: the world performs no
/// synchronisation of its own, and a lazily-evaluated query returned from inside the lock would
/// be enumerated outside it with nothing to catch the race.
/// </para>
/// </remarks>
/// <param name="Transport">Paused, speed and tick, sampled together with the states below.</param>
/// <param name="SimulationTimeSeconds">Simulated time the states refer to, in seconds.</param>
/// <param name="EnvironmentRevision">Opaque revision of the terrain, weather and sea-level configuration.</param>
/// <param name="BackhaulKilled">Whether the simulated backhaul link is currently cut.</param>
/// <param name="Descriptors">Descriptor for every asset, in spawn order.</param>
/// <param name="Assets">State for every asset, in the same spawn order.</param>
/// <param name="Drones">
/// The v1-shaped projection of the air population, sampled in this same reading. It is carried
/// here rather than fetched by a second call to <see cref="SimulationRoom.GetSnapshot"/> because
/// the v2 frame derives its detections from it: a detection is attributed to the asset that made
/// it and its confidence falls off with range, so drone poses read one lock acquisition later
/// than <paramref name="Assets"/> put a frame's detections and its asset poses up to
/// <c>speed</c> world steps apart while the frame still claimed a single
/// <paramref name="Transport"/> tick.
/// </param>
/// <param name="Tracks">
/// The contacts this session is observing but does not control, aged against
/// <paramref name="SimulationTimeSeconds"/> in this same reading. Carried here for the same
/// reason <paramref name="Drones"/> is: a frame that plotted contacts read one lock acquisition
/// after its assets would put the two pictures up to <c>speed</c> world steps apart while still
/// claiming a single tick, and any closing geometry drawn between them would be wrong by exactly
/// that much. They are <em>not</em> assets — no capabilities, no control authority, no command
/// endpoint — and nothing downstream may render a command affordance on one.
/// </param>
public sealed record RoomAssetFrame(
    TransportState Transport,
    double SimulationTimeSeconds,
    string EnvironmentRevision,
    bool BackhaulKilled,
    IReadOnlyList<AssetDescriptor> Descriptors,
    IReadOnlyList<AssetState> Assets,
    IReadOnlyList<DroneSnapshot> Drones,
    IReadOnlyList<AgedExternalTrack> Tracks);

// The multi-domain asset surface: everything the v2 API and the v2 frame pipeline need from a
// room, and nothing the v1 path uses. Split from SimulationRoom.cs the way CommandCatalog and
// CoordinateFrames are split — the session host and the asset surface are separate concerns and
// read better apart — and so that adding v2 did not grow the file that owns the tick loop.
//
// THE ONE RULE. AssetWorld performs no synchronisation. Every member here takes the room's
// single lock, and every one of them returns a value or a materialised copy. Nothing hands out
// the world itself, a live collection from it, or a lazy query over it. UseAssets<T> is the
// deliberate exception and carries its own warning.
//
// Spawning a non-air asset runs the other way round — a callback INTO the lock, because building
// one samples terrain — and lives in SimulationRoom.Spawn.cs.
public sealed partial class SimulationRoom
{
    /// <summary>Most asset events one session buffers before the oldest are dropped.</summary>
    /// <remarks>
    /// Roughly half a minute of 10 Hz frames' worth of transitions for a busy session, which is
    /// generous for a consumer that drains every frame and finite for one that never drains at
    /// all. A cap is not optional: transitions are raised by ordinary flying — every landing,
    /// every takeoff, every low-battery latch — so an unbounded list is a leak that grows with
    /// uptime rather than with load.
    /// </remarks>
    private const int MaxBufferedAssetEvents = 256;

    private static readonly AssetEvent[] NoAssetEvents = [];

    // Bounded FIFO of events swept off the assets by the tick loop. Guarded by _lock like every
    // other piece of world state; a Queue is the right shape because delivery is in raise order
    // and the drop policy discards from the head.
    private readonly Queue<AssetEvent> _assetEvents = new();
    private long _droppedAssetEvents;

    /// <summary>Commands this session has issued, and the idempotency keys they claimed.</summary>
    /// <remarks>
    /// Synchronised on its own gate rather than the simulation lock: recording a command result
    /// touches no world state, and widening the tick loop's lock to cover it would add latency
    /// for nothing.
    /// </remarks>
    public AssetCommandLog Commands => _commands;

    /// <summary>
    /// Opaque revision of the environment configuration — terrain preset, uploaded heightmap,
    /// weather and the sea level that goes with the preset.
    /// </summary>
    /// <remarks>
    /// Stamped into <see cref="VizSnapshotV2.EnvironmentRevision"/> so a client can tell that its
    /// separately-fetched, cached environment payload is stale. Never parse it; compare it.
    /// </remarks>
    public string EnvironmentRevision
    {
        get { lock (_lock) return FormatEnvironmentRevision(_environmentRevision); }
    }

    /// <summary>Captures everything a v2 frame needs from this room in one locked reading.</summary>
    /// <remarks>
    /// Transport, environment revision, descriptors, asset states, the observed contacts
    /// <em>and</em> the v1 drone projection are all sampled inside a single acquisition. Anything
    /// a snapshot publishes has to come from here: a second locked read taken beside this one is
    /// not a second half of the same frame, it is a different frame, and at eight times speed the
    /// tick loop advances up to eight world steps between the two.
    /// <para>
    /// Tracks are read through <see cref="CaptureTracks"/> rather than fetched separately, so the
    /// ages published beside a contact are measured against the very tick the assets were sampled
    /// on. That is what lets a consumer draw a closing geometry between an asset and a contact
    /// without silently mixing two readings.
    /// </para>
    /// </remarks>
    /// <returns>A fully materialised frame that stays valid after the lock is released.</returns>
    public RoomAssetFrame CaptureAssetFrame()
    {
        lock (_lock)
        {
            return new RoomAssetFrame(
                Transport: new TransportState(_paused, _speed, _assets.TickCount),
                SimulationTimeSeconds: _assets.SimulationTimeSeconds,
                EnvironmentRevision: FormatEnvironmentRevision(_environmentRevision),
                BackhaulKilled: _backhaulKilled,
                Descriptors: _assets.Descriptors,
                Assets: _assets.States,
                Drones: CaptureDroneSnapshots(),
                Tracks: CaptureTracks().Tracks);
        }
    }

    /// <summary>Runs <paramref name="reader"/> against the asset world while holding the room lock.</summary>
    /// <remarks>
    /// The escape hatch for callers that need something the typed members above do not expose —
    /// a count, a single descriptor, a targeted lookup — without the world instance ever
    /// escaping this type.
    /// <para>
    /// <b>Return a value or a copy, never a view.</b> Anything <paramref name="reader"/> returns
    /// outlives the lock. Returning a live collection, or a lazy LINQ query that will be
    /// enumerated later, reintroduces exactly the race the single-lock design exists to prevent,
    /// and it does so with no compile error and no visible symptom until a tick lands mid-read.
    /// </para>
    /// <para>
    /// <paramref name="reader"/> must not call back into this room, block, or advance the world:
    /// it runs on whichever thread called, with the tick loop's lock held.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">Type of the value being read out.</typeparam>
    /// <param name="reader">Projection over the world. Must return a value or a materialised copy.</param>
    /// <returns>Whatever <paramref name="reader"/> produced.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    public T UseAssets<T>(Func<AssetWorld, T> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (_lock)
        {
            return reader(_assets);
        }
    }

    /// <summary>Registers an already-built ground or surface asset.</summary>
    /// <remarks>
    /// For an asset that was constructed without touching the world — a test double, a replayed
    /// fixture. Anything built by an <see cref="IAssetFactory"/> must go through
    /// <see cref="TrySpawnAsset"/> instead: a factory samples terrain while it builds, and this
    /// method only takes the lock once the sampling has already happened.
    /// <para>
    /// Air assets do not come through here — their lifetime belongs to the SDK's flight world,
    /// which <see cref="AddDrone(string, System.Numerics.Vector3, string)"/> is the only correct
    /// way into. Passing one is a programming error and the world throws, rather than this
    /// method reporting it as a caller-fixable rejection.
    /// </para>
    /// </remarks>
    /// <param name="asset">Asset to register; ground and surface assets should implement <see cref="IStepDrivenAsset"/>.</param>
    /// <param name="reasonCode">Stable code from <see cref="AssetProblems"/> when registration was refused.</param>
    /// <returns><see langword="true"/> when the asset was registered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="asset"/> is an air asset.</exception>
    public bool TryAddAsset(ISimulatedAsset asset, [NotNullWhen(false)] out string? reasonCode)
    {
        ArgumentNullException.ThrowIfNull(asset);

        lock (_lock)
        {
            // Checked here rather than caught from the world, so a duplicate id is an ordinary
            // answer the API can turn into a 409 instead of an exception on a hot path.
            if (_assets.TryGet(asset.AssetId, out _))
            {
                reasonCode = AssetProblems.AssetIdTaken;
                return false;
            }

            _assets.AddAsset(asset);
        }

        Touch();
        _logger.LogInformation(
            "[room {RoomId}] Asset {AssetId} added: domain={Domain}, class={VehicleClass}.",
            Id, LogSafe(asset.AssetId), asset.Domain, asset.Descriptor.VehicleClass);
        reasonCode = null;
        return true;
    }

    /// <summary>Removes a ground or surface asset from the session.</summary>
    /// <remarks>
    /// Air assets cannot be removed: the SDK's world exposes no removal, and dropping our view
    /// while the drone kept flying would leave a vehicle that is simulated but invisible. That is
    /// reported as <see cref="AssetProblems.AssetNotRemovable"/> rather than silently succeeding,
    /// because an operator who asked for an asset to be gone must not be told it is.
    /// </remarks>
    /// <param name="assetId">Identifier of the asset to remove.</param>
    /// <param name="reasonCode">Stable code from <see cref="AssetProblems"/> when removal was refused.</param>
    /// <returns><see langword="true"/> when the asset was removed.</returns>
    public bool TryRemoveAsset(string assetId, [NotNullWhen(false)] out string? reasonCode)
    {
        lock (_lock)
        {
            if (!_assets.TryGet(assetId, out var existing) || existing is null)
            {
                reasonCode = AssetProblems.AssetNotFound;
                return false;
            }

            if (!_assets.RemoveAsset(assetId))
            {
                reasonCode = AssetProblems.AssetNotRemovable;
                return false;
            }
        }

        // Outside the lock, and only once the asset is really gone: an observer may call back
        // into this room. See IRoomLifecycleObserver.
        NotifyAssetRemoved(assetId);
        Touch();
        _logger.LogInformation("[room {RoomId}] Asset {AssetId} removed.", Id, LogSafe(assetId));
        reasonCode = null;
        return true;
    }

    /// <summary>Routes a validated, translated command to its asset.</summary>
    /// <remarks>
    /// Mirrors <see cref="SendCommand"/>'s manual-override rule for air assets, and does it only
    /// once the asset has accepted: the swarm coordinator's 2 Hz pass would otherwise overwrite
    /// an operator command on the next tick, and detaching a drone whose command was then
    /// rejected would leave it manually held with nothing holding it.
    /// </remarks>
    /// <param name="command">Command produced by <see cref="AssetCommandTranslator.TryTranslate"/>.</param>
    /// <returns>Acceptance, or a rejection carrying a machine-readable reason.</returns>
    public AssetCommandResult SendAssetCommand(in SimulatedAssetCommand command)
    {
        AssetCommandResult result;

        lock (_lock)
        {
            result = _assets.SendCommand(in command);

            if (result.IsAccepted && _assets.TryGet(command.AssetId, out var asset))
            {
                bool resuming = command.Kind == AssetCommandKind.ResumeAutonomy;

                // Each domain's own coordinator, and only that one. Ground and surface used to
                // fall through here entirely, which was harmless exactly as long as nothing
                // tasked them: the moment they had a coordinator, an operator who drove a rover
                // somewhere would have watched it turn back onto its patrol on the next 2 Hz pass.
                switch (asset.Domain)
                {
                    case AssetDomain.Air when resuming:
                        _swarm.AttachAuto(command.AssetId);
                        break;
                    case AssetDomain.Air:
                        _swarm.DetachManual(command.AssetId);
                        break;
                    case AssetDomain.Ground or AssetDomain.Surface when resuming:
                        _groundSurface.AttachAuto(command.AssetId);
                        break;
                    case AssetDomain.Ground or AssetDomain.Surface:
                        _groundSurface.DetachManual(command.AssetId);
                        break;
                    default:
                        break;
                }
            }
        }

        Touch();
        return result;
    }

    /// <summary>Events buffered for this session but not yet delivered, oldest first.</summary>
    /// <remarks>
    /// A read, not a drain — for a caller that wants to know whether anything is waiting without
    /// consuming it. Use <see cref="DrainAssetEvents"/> to take delivery.
    /// </remarks>
    public int PendingAssetEventCount
    {
        get { lock (_lock) return _assetEvents.Count; }
    }

    /// <summary>Events this room discarded because nothing collected them in time.</summary>
    /// <remarks>
    /// Monotonic for the life of the world, reset with it. Non-zero means a consumer fell behind
    /// far enough that the oldest events are gone: the buffer stayed bounded, which is the point,
    /// but the record is no longer complete and anything counting events from it is undercounting.
    /// Exposed rather than merely logged so the drop is assertable instead of anecdotal.
    /// </remarks>
    public long DroppedAssetEventCount
    {
        get { lock (_lock) return _droppedAssetEvents; }
    }

    /// <summary>Removes and returns every event raised by every asset since the last drain.</summary>
    /// <remarks>
    /// Destructive by design — an event delivered twice would be counted twice — so exactly one
    /// consumer should call it per frame. Events are drained rather than pushed through a
    /// callback because a callback raised mid-step would re-enter this room with its lock held.
    /// <para>
    /// Assets raise events during capture, so this first sweeps anything raised since the last
    /// tick into the session buffer and then hands the whole buffer over. That ordering is what
    /// makes a drain complete: without it, an event raised by a REST capture taken between two
    /// ticks would sit on its asset until the next tick and be reported one frame late.
    /// </para>
    /// <para>
    /// No production consumer calls this yet — the v2 wire carries no event channel — and that is
    /// precisely why <see cref="BufferAssetEvents"/> runs on the tick loop instead. Delivery is
    /// the missing half; the bound is what makes its absence survivable rather than a leak, and
    /// <see cref="DroppedAssetEventCount"/> is what makes the gap visible while it lasts.
    /// </para>
    /// </remarks>
    /// <returns>Events in the order they were raised, empty when nothing happened.</returns>
    public IReadOnlyList<AssetEvent> DrainAssetEvents()
    {
        lock (_lock)
        {
            BufferAssetEvents();

            if (_assetEvents.Count == 0)
            {
                return NoAssetEvents;
            }

            var drained = _assetEvents.ToArray();
            _assetEvents.Clear();
            return drained;
        }
    }

    /// <summary>Sweeps every asset's raised events into the bounded session buffer.</summary>
    /// <remarks>
    /// Called from the tick loop, which is what stops a per-asset list growing without bound in a
    /// long-lived room. Nothing in production has to be listening for that to hold: assets are
    /// drained whether or not a v2 client ever attaches, and the buffer they are swept into is
    /// itself capped at <see cref="MaxBufferedAssetEvents"/>.
    /// <para>
    /// <b>Drop policy: oldest first, counted.</b> When the buffer is full the oldest event is
    /// discarded to make room, and <see cref="DroppedAssetEventCount"/> records that it happened.
    /// Dropping the oldest rather than the newest keeps the most recent picture — which is what
    /// an operator who just reconnected needs — and counting the loss is what stops a silently
    /// truncated history being mistaken for a quiet session.
    /// </para>
    /// <para>Must be called with <c>_lock</c> held.</para>
    /// </remarks>
    private void BufferAssetEvents()
    {
        var raised = _assets.DrainEvents();

        for (var i = 0; i < raised.Count; i++)
        {
            if (_assetEvents.Count >= MaxBufferedAssetEvents)
            {
                _assetEvents.Dequeue();
                _droppedAssetEvents++;
            }

            _assetEvents.Enqueue(raised[i]);
        }
    }

    /// <summary>Discards the buffered events and the drop tally along with the world they describe.</summary>
    /// <remarks>Must be called with <c>_lock</c> held.</remarks>
    private void ClearAssetEventBuffer()
    {
        _assetEvents.Clear();
        _droppedAssetEvents = 0;
    }

    /// <summary>Renders the environment counter as the opaque token clients compare.</summary>
    /// <remarks>Prefixed rather than bare so a caller that tries to parse it fails immediately and visibly.</remarks>
    /// <param name="revision">Monotonic counter bumped on every environment mutation.</param>
    /// <returns>The token stamped into a v2 frame.</returns>
    private static string FormatEnvironmentRevision(long revision) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"env-{revision}");
}
