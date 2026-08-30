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

using System.Diagnostics.CodeAnalysis;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Tracks;

/// <summary>The contacts one session is observing but does not control.</summary>
/// <remarks>
/// Owned by a session so its lifetime is exactly that session's: track identifiers are chosen by
/// whoever reports them and are only unique within the scope that hands them out, so a
/// process-wide store would let one room's contact collide with another's.
/// <para>
/// <b>Nothing here is commandable, and nothing here can become commandable.</b> The store fuses
/// observations and ages them; it exposes no command entry point, and
/// <see cref="ExternalTrackState"/> carries no <see cref="AssetCapability"/> for a command gate
/// to test. A contact is something a sensor saw.
/// </para>
/// <para>
/// Synchronised on its own gate rather than a simulation lock, like the command log: fusing a
/// report touches no world state, and every member returns a value or a materialised copy, so a
/// caller never holds a view onto state the next report can change underneath it.
/// </para>
/// <para>
/// <b>Deterministic.</b> Every age, every freshness band and every drop is a function of
/// simulated seconds passed in by the caller. No member reads a wall clock, and the instants
/// published on the wire are derived from the session epoch plus simulated time, so replaying a
/// scenario reproduces the same picture down to the timestamps.
/// </para>
/// <para>
/// <b>Bounded.</b> A chatty source cannot grow the store without limit: tracks are capped at
/// <see cref="ExternalTrackStoreOptions.MaxTracks"/> with a stalest-first drop policy, the
/// sources retained per track are capped as well, and everything that gets dropped is counted so
/// the pressure is visible rather than silent.
/// </para>
/// </remarks>
public sealed partial class ExternalTrackStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackEntry> _tracks = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _epoch;

    private long _sequence;
    private long _droppedTrackCount;
    private long _rejectedReportCount;
    private double _lastKnownSimulationTimeSeconds;

    /// <summary>Creates an empty store.</summary>
    /// <param name="options">
    /// Ageing windows and bounds, or null for <see cref="ExternalTrackStoreOptions.Default"/>.
    /// Normalised on the way in, so a misconfigured window clamps instead of leaving a session
    /// with no track store at all.
    /// </param>
    /// <param name="simulationEpoch">
    /// Instant simulated time zero corresponds to, used to stamp
    /// <see cref="ExternalTrackState.LastUpdateTime"/>. Defaults to the Unix epoch rather than
    /// "now" so that a store constructed twice from the same inputs publishes the same instants.
    /// </param>
    public ExternalTrackStore(
        ExternalTrackStoreOptions? options = null, DateTimeOffset? simulationEpoch = null)
    {
        Options = (options ?? ExternalTrackStoreOptions.Default).Normalized();
        _epoch = simulationEpoch ?? DateTimeOffset.UnixEpoch;
    }

    /// <summary>Ageing windows and bounds this store enforces, already normalised.</summary>
    public ExternalTrackStoreOptions Options { get; }

    /// <summary>How many tracks the store currently holds.</summary>
    public int Count
    {
        get { lock (_gate) { return _tracks.Count; } }
    }

    /// <summary>Tracks retired so far, by ageing out or by eviction under capacity pressure.</summary>
    public long DroppedTrackCount
    {
        get { lock (_gate) { return _droppedTrackCount; } }
    }

    /// <summary>Reports refused so far, whether out of order or over capacity.</summary>
    public long RejectedReportCount
    {
        get { lock (_gate) { return _rejectedReportCount; } }
    }

    /// <summary>Fuses one validated observation into the session's picture.</summary>
    /// <remarks>
    /// Repeated reports of the same identifier are fused last-writer-wins, with the observation
    /// time as the tiebreak: a report observed <em>before</em> the one already held is discarded,
    /// because a late-arriving old plot dragging a contact backwards is worse than no update.
    /// Equal observation times accept, so a source that stamps at its own resolution can still
    /// correct itself.
    /// <para>
    /// Two fields are exceptions to last-writer-wins, and both for the same reason — the absence
    /// of a claim is not a claim. A report carrying
    /// <see cref="TrackClassification.Unknown"/> does not erase a classification an earlier
    /// source made, and a null label or transponder identity does not erase one already known.
    /// Accuracies are <em>not</em> an exception: they describe the observation they arrived with,
    /// so a report that omits them leaves the track with none rather than inheriting a precision
    /// nobody claimed for this plot.
    /// </para>
    /// </remarks>
    /// <param name="report">A validated report; see <see cref="TrackReport.TryCreate"/>.</param>
    /// <returns>What the store did with it, and the resulting track when it was accepted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
    public TrackIngestResult Ingest(TrackReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        lock (_gate)
        {
            _lastKnownSimulationTimeSeconds = Math.Max(
                _lastKnownSimulationTimeSeconds, report.ObservedAtSimulationTimeSeconds);
            double now = _lastKnownSimulationTimeSeconds;

            if (_tracks.TryGetValue(report.TrackId, out var existing))
            {
                if (report.ObservedAtSimulationTimeSeconds < existing.ObservedAtSimulationTimeSeconds)
                {
                    _rejectedReportCount++;
                    return new TrackIngestResult(
                        TrackIngestOutcome.RejectedOutOfOrder, report.TrackId, null,
                        ReasonCode: TrackProblems.ReportOutOfOrder,
                        Message: "The report is older than the observation already held for this track.");
                }

                Fuse(existing, report);
                return new TrackIngestResult(
                    TrackIngestOutcome.Updated, report.TrackId, Project(existing, now));
            }

            string? evicted = MakeRoomFor(report, now);
            if (evicted is null && _tracks.Count >= Options.MaxTracks)
            {
                _rejectedReportCount++;
                return new TrackIngestResult(
                    TrackIngestOutcome.RejectedCapacity, report.TrackId, null,
                    ReasonCode: TrackProblems.CapacityReached,
                    Message: $"The session retains at most {Options.MaxTracks} tracks, and none is "
                        + "older than this report.");
            }

            var entry = Create(report, ++_sequence);
            entry.PublishedFreshness = EvaluateEntry(entry, now, out _).Freshness;
            _tracks[report.TrackId] = entry;
            return new TrackIngestResult(
                TrackIngestOutcome.Created, report.TrackId, Project(entry, now), evicted);
        }
    }

    /// <summary>Ages every held track to a simulation time, retiring the ones that have expired.</summary>
    /// <remarks>
    /// The only mutating read of the clock, and the only place a freshness transition is raised.
    /// Transitions fire on a band change and nowhere else, so a contact that sits stale for a
    /// minute produces one notice rather than one per tick, and the returned lists are handed to
    /// the caller rather than queued inside the store — nothing accumulates here waiting to be
    /// drained.
    /// </remarks>
    /// <param name="nowSimulationTimeSeconds">Simulation time to age to, in seconds.</param>
    /// <returns>The freshness changes and retirements this sweep produced.</returns>
    public TrackAgingResult Advance(double nowSimulationTimeSeconds)
    {
        if (!double.IsFinite(nowSimulationTimeSeconds))
        {
            return TrackAgingResult.Empty(nowSimulationTimeSeconds);
        }

        lock (_gate)
        {
            _lastKnownSimulationTimeSeconds = Math.Max(
                _lastKnownSimulationTimeSeconds, nowSimulationTimeSeconds);

            List<TrackFreshnessTransition>? transitions = null;
            List<string>? dropped = null;

            foreach (var entry in _tracks.Values.OrderBy(e => e.Sequence).ToList())
            {
                var evaluation = EvaluateEntry(entry, nowSimulationTimeSeconds, out double age);

                if (evaluation.IsExpired)
                {
                    _tracks.Remove(entry.TrackId);
                    _droppedTrackCount++;
                    (dropped ??= []).Add(entry.TrackId);
                    continue;
                }

                if (evaluation.Freshness != entry.PublishedFreshness)
                {
                    (transitions ??= []).Add(new TrackFreshnessTransition(
                        entry.TrackId, entry.PublishedFreshness, evaluation.Freshness,
                        Math.Max(0.0, age), nowSimulationTimeSeconds));
                    entry.PublishedFreshness = evaluation.Freshness;
                }
            }

            if (transitions is null && dropped is null)
            {
                return TrackAgingResult.Empty(nowSimulationTimeSeconds);
            }

            IReadOnlyList<TrackFreshnessTransition> raised = transitions ?? [];
            IReadOnlyList<string> retired = dropped ?? [];
            return new TrackAgingResult(raised, retired, nowSimulationTimeSeconds);
        }
    }

    /// <summary>Every held track as of one simulation time, freshest observation first.</summary>
    /// <remarks>
    /// A pure read: it ages the tracks for display without retiring anything, so calling it twice
    /// changes nothing. Tracks already past
    /// <see cref="ExternalTrackStoreOptions.DropAfterSeconds"/> are omitted rather than shown as
    /// merely lost, so a consumer sees the same population whether or not
    /// <see cref="Advance"/> has run since they expired.
    /// </remarks>
    /// <param name="nowSimulationTimeSeconds">Simulation time to compute ages at, in seconds.</param>
    /// <returns>A materialised list that stays valid after the store changes.</returns>
    public IReadOnlyList<AgedExternalTrack> Snapshot(double nowSimulationTimeSeconds)
    {
        lock (_gate)
        {
            return _tracks.Values
                .Where(entry => !IsExpired(entry, nowSimulationTimeSeconds))
                .Select(entry => Project(entry, nowSimulationTimeSeconds))
                .OrderByDescending(view => view.ObservedAtSimulationTimeSeconds)
                .ThenBy(view => view.Track.TrackId, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Looks up one track by identifier.</summary>
    /// <param name="trackId">Track to fetch.</param>
    /// <param name="nowSimulationTimeSeconds">Simulation time to compute the age at, in seconds.</param>
    /// <param name="track">The aged track on success, otherwise null.</param>
    /// <returns><see langword="true"/> when the track is held and has not expired.</returns>
    public bool TryGet(
        string trackId,
        double nowSimulationTimeSeconds,
        [NotNullWhen(true)] out AgedExternalTrack? track)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var entry))
            {
                track = null;
                return false;
            }

            if (IsExpired(entry, nowSimulationTimeSeconds))
            {
                track = null;
                return false;
            }

            track = Project(entry, nowSimulationTimeSeconds);
            return true;
        }
    }

    /// <summary>Forgets one track, whatever its age.</summary>
    /// <param name="trackId">Track to forget.</param>
    /// <returns><see langword="true"/> when a track was removed.</returns>
    public bool Remove(string trackId)
    {
        lock (_gate)
        {
            if (!_tracks.Remove(trackId))
            {
                return false;
            }

            _droppedTrackCount++;
            return true;
        }
    }

    /// <summary>Forgets every track and resets the counters.</summary>
    /// <remarks>
    /// Called when the session resets. The clock reference is reset too: after a reset the
    /// simulation time starts again, and a retained high-water mark would make every subsequent
    /// report look like it arrived from the distant past.
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _tracks.Clear();
            _sequence = 0;
            _droppedTrackCount = 0;
            _rejectedReportCount = 0;
            _lastKnownSimulationTimeSeconds = 0.0;
        }
    }

    /// <summary>Drops expired tracks and, if still full, the stalest track older than the report.</summary>
    /// <remarks>
    /// The stalest-first policy has one guard: a track is only evicted when its observation is
    /// genuinely older than the incoming one. Without it, a source spraying unique identifiers
    /// would evict a well-observed contact to make room for its own noise; with it, the store
    /// refuses the new report instead and the refusal is counted where an operator can see it.
    /// </remarks>
    private string? MakeRoomFor(TrackReport report, double now)
    {
        if (_tracks.Count < Options.MaxTracks)
        {
            return null;
        }

        foreach (var entry in _tracks.Values.Where(e => IsExpired(e, now)).ToList())
        {
            _tracks.Remove(entry.TrackId);
            _droppedTrackCount++;
        }

        if (_tracks.Count < Options.MaxTracks)
        {
            return null;
        }

        var stalest = _tracks.Values
            .OrderBy(e => e.ObservedAtSimulationTimeSeconds)
            .ThenBy(e => e.Sequence)
            .First();

        if (stalest.ObservedAtSimulationTimeSeconds >= report.ObservedAtSimulationTimeSeconds)
        {
            return null;
        }

        _tracks.Remove(stalest.TrackId);
        _droppedTrackCount++;
        return stalest.TrackId;
    }
}
