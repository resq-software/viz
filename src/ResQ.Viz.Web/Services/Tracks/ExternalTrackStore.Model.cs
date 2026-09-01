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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Tracks;

/// <summary>What happened to one report offered to the store.</summary>
public enum TrackIngestOutcome
{
    /// <summary>The report started a track the session was not previously holding.</summary>
    Created,

    /// <summary>The report was fused into a track the session already held.</summary>
    Updated,

    /// <summary>
    /// The report was observed no later than the observation already held, so it was discarded
    /// rather than allowed to move the contact backwards in time.
    /// </summary>
    RejectedOutOfOrder,

    /// <summary>
    /// The session is holding as many tracks as it retains and every one of them carries a
    /// newer observation than this report, so there was nothing it would be right to evict.
    /// </summary>
    RejectedCapacity,
}

/// <summary>How old an observation is allowed to get, and how its confidence decays as it does.</summary>
/// <remarks>
/// Every window is measured in <b>simulated</b> seconds. Nothing here reads a wall clock, which
/// is what lets the same sequence of reports replay to the same tracks with the same ages.
/// <para>
/// The defaults suit a session fed by a 1 Hz feed: a contact goes overdue after five seconds,
/// is no longer worth reading a geometry from after twenty, and is forgotten after a minute of
/// silence. They are not a safety claim about any real sensor.
/// </para>
/// </remarks>
/// <param name="FreshWindowSeconds">Age up to which an observation is inside its expected reporting interval.</param>
/// <param name="StaleWindowSeconds">Age beyond which an observation is too old to read a geometry from.</param>
/// <param name="DropAfterSeconds">Age at which a track is retired from the session entirely.</param>
/// <param name="MinConfidenceFactor">
/// Floor the ageing discount decays to, in 0-1. Deliberately above zero: a very old contact is
/// still evidence that something was there, and a confidence of exactly zero reads as "there is
/// nothing here", which is a stronger claim than silence supports.
/// </param>
/// <param name="MaxTracks">Most tracks the session retains at once.</param>
/// <param name="MaxSourcesPerTrack">Most contributing sources one track retains.</param>
/// <param name="ClockToleranceSeconds">
/// How far ahead of the session's own simulation time an observation may be stamped before its
/// age stops being meaningful. Inside the tolerance the age is treated as zero; beyond it the
/// freshness is reported as <see cref="DataFreshness.Unknown"/> rather than invented.
/// </param>
public sealed record ExternalTrackStoreOptions(
    double FreshWindowSeconds = 5.0,
    double StaleWindowSeconds = 20.0,
    double DropAfterSeconds = 60.0,
    double MinConfidenceFactor = 0.2,
    int MaxTracks = 256,
    int MaxSourcesPerTrack = 8,
    double ClockToleranceSeconds = 1.0)
{
    /// <summary>The defaults, as a shared instance.</summary>
    public static ExternalTrackStoreOptions Default { get; } = new();

    /// <summary>Returns a copy with every field forced into a usable range.</summary>
    /// <remarks>
    /// Clamped rather than rejected, on the same principle a malformed scenario row is skipped
    /// rather than thrown from: a configuration mistake must not leave a session half-built with
    /// no track store at all. The windows are forced to be non-decreasing, so the curve in
    /// <see cref="ExternalTrackAging.Evaluate"/> cannot be handed an interval that runs backwards.
    /// </remarks>
    /// <returns>A normalised copy; this instance is never modified.</returns>
    public ExternalTrackStoreOptions Normalized()
    {
        double fresh = Sanitize(FreshWindowSeconds, 5.0);
        double stale = Math.Max(fresh, Sanitize(StaleWindowSeconds, 20.0));
        double drop = Math.Max(stale, Sanitize(DropAfterSeconds, 60.0));
        double floorFactor = double.IsFinite(MinConfidenceFactor)
            ? Math.Clamp(MinConfidenceFactor, 0.0, 1.0)
            : 0.2;
        double tolerance = Sanitize(ClockToleranceSeconds, 1.0);

        return new ExternalTrackStoreOptions(
            fresh,
            stale,
            drop,
            floorFactor,
            Math.Clamp(MaxTracks, 1, 4096),
            Math.Clamp(MaxSourcesPerTrack, 1, 64),
            tolerance);
    }

    private static double Sanitize(double value, double fallback) =>
        double.IsFinite(value) && value >= 0.0 ? value : fallback;
}

/// <summary>What an age means: how fresh the observation is, and how far to discount it.</summary>
/// <param name="Freshness">Freshness band the age falls in.</param>
/// <param name="ConfidenceFactor">Multiplier applied to the reported confidence, in 0-1.</param>
/// <param name="IsExpired">True when the track should be retired from the session.</param>
public readonly record struct TrackAgeEvaluation(
    DataFreshness Freshness,
    double ConfidenceFactor,
    bool IsExpired);

/// <summary>The one place an observation's age turns into a freshness band and a discount.</summary>
/// <remarks>
/// Pure, public and used by every caller that needs the answer — the store's snapshot path, its
/// ageing sweep and its ingest path all route through <see cref="Evaluate"/>. That matters more
/// than it looks: a decay curve written down in one place and re-implemented inline somewhere
/// else is a curve that disagrees with itself, and the disagreement shows up as a contact whose
/// displayed confidence does not match the confidence it was retired on.
/// <para>
/// The curve, in full:
/// </para>
/// <list type="bullet">
/// <item><description>Age below zero by more than the clock tolerance: freshness
/// <see cref="DataFreshness.Unknown"/>, no discount. An observation stamped in the session's
/// future has no meaningful age, and guessing one would be worse than admitting it.</description></item>
/// <item><description>Age up to <see cref="ExternalTrackStoreOptions.FreshWindowSeconds"/>:
/// <see cref="DataFreshness.Fresh"/>, no discount.</description></item>
/// <item><description>Age up to <see cref="ExternalTrackStoreOptions.StaleWindowSeconds"/>:
/// <see cref="DataFreshness.Stale"/>, discount falling linearly from 1 to
/// <see cref="ExternalTrackStoreOptions.MinConfidenceFactor"/> across the band.</description></item>
/// <item><description>Beyond that: <see cref="DataFreshness.Lost"/>, held at the floor, and
/// expired once the age passes <see cref="ExternalTrackStoreOptions.DropAfterSeconds"/>.</description></item>
/// </list>
/// </remarks>
public static class ExternalTrackAging
{
    /// <summary>Evaluates one age against the ageing curve.</summary>
    /// <param name="ageSeconds">Simulated seconds since the observation was made.</param>
    /// <param name="options">Windows and floor to evaluate against; normalised by the caller.</param>
    /// <returns>The freshness band, the confidence discount and whether the track has expired.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public static TrackAgeEvaluation Evaluate(double ageSeconds, ExternalTrackStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!double.IsFinite(ageSeconds) || ageSeconds < -options.ClockToleranceSeconds)
        {
            return new TrackAgeEvaluation(DataFreshness.Unknown, 1.0, IsExpired: false);
        }

        double age = Math.Max(0.0, ageSeconds);

        if (age <= options.FreshWindowSeconds)
        {
            return new TrackAgeEvaluation(DataFreshness.Fresh, 1.0, IsExpired: false);
        }

        if (age <= options.StaleWindowSeconds)
        {
            double band = options.StaleWindowSeconds - options.FreshWindowSeconds;
            double progress = band <= 0.0 ? 1.0 : (age - options.FreshWindowSeconds) / band;
            double factor = 1.0 - (progress * (1.0 - options.MinConfidenceFactor));
            return new TrackAgeEvaluation(
                DataFreshness.Stale, Math.Clamp(factor, options.MinConfidenceFactor, 1.0), IsExpired: false);
        }

        return new TrackAgeEvaluation(
            DataFreshness.Lost, options.MinConfidenceFactor, age > options.DropAfterSeconds);
    }

    /// <summary>Signed age of an observation at a given simulation time.</summary>
    /// <remarks>
    /// Deliberately <b>not</b> floored at zero. A negative age means a source stamped its report
    /// ahead of the session's own simulation time, and <see cref="Evaluate"/> is the one place
    /// that decides what to do about it — flooring here would make that branch unreachable and
    /// leave the curve documenting behaviour nothing could ever produce.
    /// </remarks>
    /// <param name="observedAtSimulationTimeSeconds">Simulation time the observation was made at.</param>
    /// <param name="nowSimulationTimeSeconds">Simulation time to measure the age at.</param>
    /// <returns>Simulated seconds elapsed, negative for a report from the session's future.</returns>
    public static double AgeSeconds(
        double observedAtSimulationTimeSeconds, double nowSimulationTimeSeconds)
    {
        double age = nowSimulationTimeSeconds - observedAtSimulationTimeSeconds;
        return double.IsFinite(age) ? age : 0.0;
    }
}

/// <summary>A track crossing from one freshness band into another.</summary>
/// <remarks>
/// Raised only when the band actually changes. A per-tick "still stale" notice would put sixty
/// messages a second per contact into a queue that has to be drained by someone, which is the
/// same mistake as reporting a standing condition as if it were an event.
/// </remarks>
/// <param name="TrackId">Track whose freshness changed.</param>
/// <param name="Previous">Band the track was last published in.</param>
/// <param name="Current">Band it has moved into.</param>
/// <param name="AgeSeconds">Age of the newest observation when the change was noticed, never negative.</param>
/// <param name="SimulationTimeSeconds">Simulation time the change was noticed at.</param>
public sealed record TrackFreshnessTransition(
    string TrackId,
    DataFreshness Previous,
    DataFreshness Current,
    double AgeSeconds,
    double SimulationTimeSeconds);

/// <summary>What one ageing sweep changed.</summary>
/// <remarks>
/// Both lists are materialised copies taken under the store's own gate, and both are empty in
/// the ordinary case where nothing crossed a band. A caller drains this per sweep; nothing
/// accumulates inside the store waiting for someone who may never come.
/// </remarks>
/// <param name="Transitions">Freshness changes noticed by this sweep, in track insertion order.</param>
/// <param name="DroppedTrackIds">Tracks retired by this sweep, in the order they were retired.</param>
/// <param name="SimulationTimeSeconds">Simulation time the sweep ran at.</param>
public sealed record TrackAgingResult(
    IReadOnlyList<TrackFreshnessTransition> Transitions,
    IReadOnlyList<string> DroppedTrackIds,
    double SimulationTimeSeconds)
{
    /// <summary>A sweep that changed nothing, at the given simulation time.</summary>
    /// <param name="simulationTimeSeconds">Simulation time the sweep ran at.</param>
    /// <returns>An empty result.</returns>
    public static TrackAgingResult Empty(double simulationTimeSeconds) =>
        new([], [], simulationTimeSeconds);

    /// <summary>True when neither a freshness band nor the held population changed.</summary>
    public bool IsUnchanged => Transitions.Count == 0 && DroppedTrackIds.Count == 0;
}

/// <summary>Outcome of offering one report to the store.</summary>
/// <param name="Outcome">What the store did with it.</param>
/// <param name="TrackId">Track the report named.</param>
/// <param name="Track">
/// The track as it stands after the report, or null when the report was refused. Null on
/// refusal is deliberate: a caller cannot accidentally publish a track the store did not accept.
/// </param>
/// <param name="EvictedTrackId">Track discarded to make room, or null when nothing was evicted.</param>
/// <param name="ReasonCode">Stable code from <see cref="TrackProblems"/> when refused.</param>
/// <param name="Message">Operator-facing explanation when refused. Render it; never parse it.</param>
public sealed record TrackIngestResult(
    TrackIngestOutcome Outcome,
    string TrackId,
    AgedExternalTrack? Track,
    string? EvictedTrackId = null,
    string? ReasonCode = null,
    string? Message = null)
{
    /// <summary>True when the report was fused into the session's picture.</summary>
    public bool IsAccepted => Track is not null;
}
