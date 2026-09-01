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

using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Tracks;

// How a report becomes a held track, and how a held track becomes a published one.
//
// Split from the store's public surface the way the ground domain splits its dynamics from its
// asset: the members here are the fusion and projection rules, and they are worth reading on
// their own without the locking and bookkeeping around them.
//
// Everything here runs under the store's gate, and everything here is pure with respect to the
// caller: Project builds a fresh state each time, so nothing a caller holds can be mutated by
// the next report.
public sealed partial class ExternalTrackStore
{
    /// <summary>Furthest from the epoch a simulated instant is allowed to be stamped, in seconds.</summary>
    /// <remarks>
    /// Roughly thirty years, and present only so that a nonsensical simulation time produces a
    /// clamped timestamp instead of an overflow inside <see cref="DateTimeOffset"/>. Reports are
    /// bounded long before they reach here; this guards the projection, not the payload.
    /// </remarks>
    private const double MaxSimulatedInstantSeconds = 1e9;

    /// <summary>One contact as the store holds it between reports.</summary>
    /// <remarks>
    /// A mutable class rather than a record, deliberately: fusion updates a contact in place
    /// under the gate, and every value that leaves the store is a fresh projection, so no caller
    /// ever observes this object. Poses and twists are stored already converted to the scene
    /// frame, so consumers compare like with like instead of each re-deriving a basis change.
    /// </remarks>
    private sealed class TrackEntry
    {
        public required string TrackId { get; init; }

        public required long Sequence { get; init; }

        public required TrackClassification Classification { get; set; }

        public required FramedPose Pose { get; set; }

        public required FramedTwist Twist { get; set; }

        public required double ReportedConfidence { get; set; }

        public required double ObservedAtSimulationTimeSeconds { get; set; }

        public double? PositionAccuracyM { get; set; }

        public double? VelocityAccuracyMps { get; set; }

        public string? Label { get; set; }

        public TransponderIdentity? Transponder { get; set; }

        public DataFreshness PublishedFreshness { get; set; } = DataFreshness.Fresh;

        public int UpdateCount { get; set; }

        /// <summary>Contributing sources, most recently updated first and bounded in length.</summary>
        public List<TrackSource> Sources { get; } = [];
    }

    /// <summary>Builds a new held track from its first report.</summary>
    private TrackEntry Create(TrackReport report, long sequence)
    {
        var entry = new TrackEntry
        {
            TrackId = report.TrackId,
            Sequence = sequence,
            Classification = report.Classification,
            Pose = ToScenePose(report.Pose),
            Twist = ToSceneTwist(report.Twist),
            ReportedConfidence = Math.Clamp(report.Confidence, 0.0, 1.0),
            ObservedAtSimulationTimeSeconds = report.ObservedAtSimulationTimeSeconds,
            PositionAccuracyM = report.PositionAccuracyM,
            VelocityAccuracyMps = report.VelocityAccuracyMps,
            Label = report.Label,
            Transponder = report.Transponder,
            UpdateCount = 1,
        };

        RecordSource(entry, report);
        return entry;
    }

    /// <summary>Folds a newer report into a track already held.</summary>
    /// <remarks>
    /// Last-writer-wins for everything the new observation actually measured, with the two
    /// documented exceptions — an <see cref="TrackClassification.Unknown"/> classification and a
    /// null label or identity are absences, and an absence overwrites nothing.
    /// </remarks>
    private void Fuse(TrackEntry entry, TrackReport report)
    {
        entry.Pose = ToScenePose(report.Pose);
        entry.Twist = ToSceneTwist(report.Twist);
        entry.ReportedConfidence = Math.Clamp(report.Confidence, 0.0, 1.0);
        entry.ObservedAtSimulationTimeSeconds = report.ObservedAtSimulationTimeSeconds;
        entry.PositionAccuracyM = report.PositionAccuracyM;
        entry.VelocityAccuracyMps = report.VelocityAccuracyMps;
        entry.UpdateCount++;

        if (report.Classification != TrackClassification.Unknown)
        {
            entry.Classification = report.Classification;
        }

        if (report.Label is not null)
        {
            entry.Label = report.Label;
        }

        if (report.Transponder is not null)
        {
            entry.Transponder = report.Transponder;
        }

        RecordSource(entry, report);
    }

    /// <summary>Moves the reporting source to the front of the track's source list.</summary>
    /// <remarks>
    /// Bounded by <see cref="ExternalTrackStoreOptions.MaxSourcesPerTrack"/>, dropping from the
    /// tail — the least recently heard from. Without the bound a single track fed by a feed that
    /// mints a new source identifier per plot would grow forever while looking, from the outside,
    /// like one well-observed contact.
    /// </remarks>
    private void RecordSource(TrackEntry entry, TrackReport report)
    {
        entry.Sources.RemoveAll(s => string.Equals(s.SourceId, report.SourceId, StringComparison.Ordinal));
        entry.Sources.Insert(0, new TrackSource(
            report.SourceId,
            report.SourceKind,
            ToInstant(report.ObservedAtSimulationTimeSeconds),
            report.SourceQuality));

        while (entry.Sources.Count > Options.MaxSourcesPerTrack)
        {
            entry.Sources.RemoveAt(entry.Sources.Count - 1);
        }
    }

    /// <summary>Ages one held track without changing it.</summary>
    /// <param name="entry">Track to evaluate.</param>
    /// <param name="now">Simulation time to evaluate at, in seconds.</param>
    /// <param name="ageSeconds">
    /// Signed simulated seconds since the observation. Negative when a source stamped its report
    /// ahead of the session, which the curve reports as
    /// <see cref="DataFreshness.Unknown"/> rather than inventing an age for.
    /// </param>
    /// <returns>The freshness band, discount and expiry for that age.</returns>
    private TrackAgeEvaluation EvaluateEntry(TrackEntry entry, double now, out double ageSeconds)
    {
        ageSeconds = ExternalTrackAging.AgeSeconds(entry.ObservedAtSimulationTimeSeconds, now);
        return ExternalTrackAging.Evaluate(ageSeconds, Options);
    }

    /// <summary>Whether a held track has aged past the point the session retains it.</summary>
    private bool IsExpired(TrackEntry entry, double now) =>
        EvaluateEntry(entry, now, out _).IsExpired;

    /// <summary>Builds the published view of one held track, with its age made explicit.</summary>
    /// <remarks>
    /// The confidence on the wire is the reported confidence <em>after</em> the ageing discount,
    /// and the reported value travels beside it on
    /// <see cref="AgedExternalTrack.ReportedConfidence"/>. Two quantities, two fields: a consumer
    /// that wants to know how good the source claims to be and one that wants to know how much to
    /// trust this picture are asking different questions.
    /// </remarks>
    private AgedExternalTrack Project(TrackEntry entry, double now)
    {
        var evaluation = EvaluateEntry(entry, now, out double ageSeconds);

        var quality = new TrackQuality(
            Confidence: Math.Clamp(entry.ReportedConfidence * evaluation.ConfidenceFactor, 0.0, 1.0),
            PositionAccuracyM: entry.PositionAccuracyM,
            VelocityAccuracyMps: entry.VelocityAccuracyMps,
            UpdateCount: entry.UpdateCount,
            IsFused: entry.Sources.Count > 1);

        var state = new ExternalTrackState(
            TrackId: entry.TrackId,
            Classification: entry.Classification,
            Pose: entry.Pose,
            Twist: entry.Twist,
            Sources: entry.Sources.ToList(),
            Quality: quality,
            LastUpdateTime: ToInstant(entry.ObservedAtSimulationTimeSeconds),
            Freshness: evaluation.Freshness,
            Label: entry.Label,
            Transponder: entry.Transponder);

        return new AgedExternalTrack(
            state,
            Math.Max(0.0, ageSeconds),
            entry.ObservedAtSimulationTimeSeconds,
            entry.ReportedConfidence);
    }

    /// <summary>Converts a reported pose into the scene frame.</summary>
    /// <remarks>
    /// The covariance is dropped when the frame changes rather than carried across: it is
    /// expressed against the source frame's axes, and re-labelling it without rotating it would
    /// draw an uncertainty ellipse pointing the wrong way. An all-zero orientation is preserved
    /// as-is, because that is how <see cref="FramedPose.Orientation"/> says "no attitude was
    /// declared" — and a contact usually has none to declare.
    /// </remarks>
    private static FramedPose ToScenePose(FramedPose pose)
    {
        if (pose.Frame == CoordinateFrame.LocalEus)
        {
            return pose;
        }

        bool declaredOrientation = !pose.Orientation.Equals(default(Quaternion));

        return new FramedPose(
            CoordinateFrame.LocalEus,
            pose.OriginId,
            CoordinateFrames.TransformVector(pose.Position, pose.Frame, CoordinateFrame.LocalEus),
            declaredOrientation
                ? CoordinateFrames.ConvertOrientationReference(
                    pose.Orientation, pose.Frame, CoordinateFrame.LocalEus)
                : default,
            Covariance: null,
            Geo: pose.Geo);
    }

    /// <summary>Converts a reported velocity into the scene frame, or synthesises an unobserved one.</summary>
    /// <remarks>
    /// A report with no twist publishes a zero velocity, because
    /// <see cref="ExternalTrackState.Twist"/> is always present on the wire — and leaves
    /// <see cref="TrackQuality.VelocityAccuracyMps"/> null, which is where the existing contract
    /// puts the difference between "not moving" and "motion not reported". A consumer that reads
    /// a closing rate off a zero velocity without checking that accuracy is reading a number
    /// nobody measured.
    /// </remarks>
    private static FramedTwist ToSceneTwist(FramedTwist? twist)
    {
        if (twist is null)
        {
            return new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero);
        }

        if (twist.Frame == CoordinateFrame.LocalEus)
        {
            return twist;
        }

        return new FramedTwist(
            CoordinateFrame.LocalEus,
            CoordinateFrames.TransformVector(twist.Linear, twist.Frame, CoordinateFrame.LocalEus),
            CoordinateFrames.TransformVector(twist.Angular, twist.Frame, CoordinateFrame.LocalEus),
            OriginId: twist.OriginId,
            Covariance: null);
    }

    /// <summary>Maps simulated seconds onto the instant published on the wire.</summary>
    private DateTimeOffset ToInstant(double simulationTimeSeconds)
    {
        double clamped = double.IsFinite(simulationTimeSeconds)
            ? Math.Clamp(simulationTimeSeconds, -MaxSimulatedInstantSeconds, MaxSimulatedInstantSeconds)
            : 0.0;
        return _epoch.AddSeconds(clamped);
    }
}
