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
using ResQ.Viz.Web.Services.Tracks;

namespace ResQ.Viz.Web.Services;

/// <summary>One locked reading of the contacts a session is observing.</summary>
/// <remarks>
/// Sampled in a single acquisition for the same reason <see cref="RoomAssetFrame"/> is: the ages
/// in <paramref name="Tracks"/> are computed against <paramref name="SimulationTimeSeconds"/>, so
/// fetching the two through separate locked getters would publish ages measured against a clock
/// the 60 Hz loop has since moved.
/// <para>
/// The counters travel with the tracks because they are what makes the store's bounds
/// observable. A client watching <paramref name="DroppedTrackCount"/> climb knows contacts are
/// being retired; one watching <paramref name="RejectedReportCount"/> climb knows a source is
/// reporting faster than the session will retain.
/// </para>
/// </remarks>
/// <param name="Tracks">Held tracks with their ages, freshest observation first.</param>
/// <param name="SimulationTimeSeconds">Simulation time the ages were computed at, in seconds.</param>
/// <param name="Capacity">Most tracks this session retains at once.</param>
/// <param name="DroppedTrackCount">Tracks retired so far, by ageing out or by eviction.</param>
/// <param name="RejectedReportCount">Reports refused so far, whether out of order or over capacity.</param>
public sealed record RoomTrackFrame(
    IReadOnlyList<AgedExternalTrack> Tracks,
    double SimulationTimeSeconds,
    int Capacity,
    long DroppedTrackCount,
    long RejectedReportCount);

// The external-track surface of a room: the contacts a session observes but does not control.
//
// Split from SimulationRoom.Assets.cs the way that file is split from SimulationRoom.cs, and for
// the same reason — a separate concern with a separate hazard. Assets are simulated; tracks are
// reported. The two share this type's single _lock and nothing else.
//
// THE ONE RULE IS THE SAME. Every member here takes the room's lock and returns a value or a
// materialised copy. The store synchronises itself, but the simulation time its ageing is
// measured against lives on the world, so reading the two together is the only way an age and
// the clock behind it can agree.
//
// NOTHING HERE IS COMMANDABLE. There is deliberately no member that sends anything to a track,
// and no wire route that could reach one if there were: a contact is an observation.
public sealed partial class SimulationRoom
{
    // Created on first use rather than in a field initialiser, because the store stamps
    // ExternalTrackState.LastUpdateTime from the session epoch plus simulated time and the epoch
    // is CreatedAtUtc, which a field initialiser runs too early to read. Every access below is
    // under _lock, so the lazy construction is not itself a race.
    private ExternalTrackStore? _tracks;

    /// <summary>This session's track store, created on first use.</summary>
    /// <remarks>Must be read with <c>_lock</c> held.</remarks>
    private ExternalTrackStore TrackStore =>
        _tracks ??= new ExternalTrackStore(simulationEpoch: CreatedAtUtc);

    /// <summary>Fuses one validated observation into this session's picture.</summary>
    /// <remarks>
    /// The only way a track enters a session, and it takes an already-validated
    /// <see cref="TrackReport"/> rather than a request body: the boundary decides what a
    /// well-formed observation is, and the room decides what to do with one. A refused report
    /// leaves the store exactly as it found it.
    /// <para>
    /// The room's lock is taken even though the store has a gate of its own, because ingest is
    /// paired with reads of the world clock elsewhere in this file, and one order of acquisition
    /// is what keeps that free of surprises.
    /// </para>
    /// </remarks>
    /// <param name="report">A validated report; see <see cref="TrackReport.TryCreate"/>.</param>
    /// <returns>What the store did with it, and the resulting track when it was accepted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public TrackIngestResult IngestTrackReport(TrackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        TrackIngestResult result;
        lock (_lock)
        {
            result = TrackStore.Ingest(report);
        }

        Touch();
        return result;
    }

    /// <summary>Captures every held track and the store's bounds in one locked reading.</summary>
    /// <remarks>
    /// A pure read: tracks are aged for display without being retired, so calling it twice
    /// changes nothing. Contacts already past the store's drop window are omitted rather than
    /// shown as merely lost, so a consumer sees the same population whether or not the tick loop
    /// has swept since they expired.
    /// </remarks>
    /// <returns>A fully materialised frame that stays valid after the lock is released.</returns>
    public RoomTrackFrame CaptureTrackFrame()
    {
        lock (_lock)
        {
            return CaptureTracks();
        }
    }

    /// <summary>Looks up one contact by identifier.</summary>
    /// <param name="trackId">Track to fetch.</param>
    /// <param name="track">The aged track on success, otherwise null.</param>
    /// <returns><see langword="true"/> when the session holds the track and it has not expired.</returns>
    public bool TryGetTrack(string trackId, [NotNullWhen(true)] out AgedExternalTrack? track)
    {
        lock (_lock)
        {
            return TrackStore.TryGet(trackId, _assets.SimulationTimeSeconds, out track);
        }
    }

    /// <summary>Builds the track half of a reading.</summary>
    /// <remarks>Must be called with <c>_lock</c> held; every collection returned is materialised.</remarks>
    /// <returns>The tracks and the store's counters, as of the world's current simulation time.</returns>
    private RoomTrackFrame CaptureTracks()
    {
        double now = _assets.SimulationTimeSeconds;
        var store = TrackStore;

        return new RoomTrackFrame(
            Tracks: store.Snapshot(now),
            SimulationTimeSeconds: now,
            Capacity: store.Options.MaxTracks,
            DroppedTrackCount: store.DroppedTrackCount,
            RejectedReportCount: store.RejectedReportCount);
    }

    /// <summary>Ages every held contact to the world clock, retiring the ones that have expired.</summary>
    /// <remarks>
    /// Called from the tick loop beside <see cref="BufferAssetEvents"/>, and for the same reason:
    /// a session nobody is reading is exactly the one that runs for hours, and retirement that
    /// happened only when someone asked would leave expired contacts holding capacity a live one
    /// then could not have. The store's own cap is what makes this a tidy-up rather than the one
    /// thing standing between the session and a leak.
    /// <para>
    /// Deterministic: the sweep is a function of the simulated time it is given, so a paused
    /// session ages nothing and a replay ages identically. The freshness transitions it produces
    /// are returned by the store rather than queued inside it, and are discarded here — the v2
    /// wire carries no track-event channel yet, and dropping them is honest where accumulating
    /// them would be a leak.
    /// </para>
    /// <para>
    /// Null-conditional rather than through <see cref="TrackStore"/>, so a session that never
    /// reports a contact never allocates a store: this runs on every tick of every room, and
    /// creating one here would build a store for every session in the process to sweep nothing.
    /// </para>
    /// <para>Must be called with <c>_lock</c> held.</para>
    /// </remarks>
    private void AdvanceTracks() => _tracks?.Advance(_assets.SimulationTimeSeconds);

    /// <summary>Forgets every contact and resets the store's counters.</summary>
    /// <remarks>
    /// Called from <see cref="Reset"/>. Simulated time restarts with the world, so a store that
    /// survived a reset would measure every later report against a high-water mark from the
    /// previous run and refuse the lot as out of order.
    /// <para>Must be called with <c>_lock</c> held.</para>
    /// </remarks>
    private void ClearTracks() => _tracks?.Clear();
}
