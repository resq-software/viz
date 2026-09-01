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

namespace ResQ.Viz.Web.Models;

/// <summary>Request body for injecting one observation of a contact the session does not control.</summary>
/// <remarks>
/// The ingest side of <see cref="ExternalTrackState"/>, and deliberately the <b>only</b> way a
/// track enters a session. There is no companion "track command" request and there never will
/// be: a track is something a sensor or a feed reports, not something anyone drives. Every
/// field here describes an observation, so nothing on this type can be mistaken for control
/// authority, and a client that renders affordances from a request shape finds none to render.
/// <para>
/// One report is one observation, not the whole track. The store fuses repeated reports of the
/// same <paramref name="TrackId"/> — see <c>ExternalTrackStore</c> — so a caller sends what its
/// sensor just saw and never has to reconstruct the track's history to update it.
/// </para>
/// <para>
/// <paramref name="Pose"/> is a <see cref="FramedPose"/> rather than a bare coordinate triple
/// for the reason every v2 boundary insists on: three plausible numbers look identical in every
/// frame, and a contact plotted in the wrong frame is a contact drawn somewhere it is not.
/// </para>
/// </remarks>
/// <param name="TrackId">
/// Stable identifier for the contact within the session. Lives in its own identifier space:
/// it is never matched against an <see cref="AssetDescriptor.AssetId"/>, and colliding with one
/// grants nothing, because no command path resolves a track at all.
/// </param>
/// <param name="Pose">
/// Frame-qualified position and orientation of the contact. The frame must be named — a
/// <see cref="CoordinateFrame.Unspecified"/> report is refused rather than assumed to be in the
/// scene frame.
/// </param>
/// <param name="Twist">
/// Frame-qualified velocity, or null when the source observed none. A null report publishes a
/// zero velocity — <see cref="ExternalTrackState.Twist"/> is always present — and leaves
/// <see cref="TrackQuality.VelocityAccuracyMps"/> absent, which is where this contract puts the
/// difference between "not moving" and "motion not reported". The two look identical on a plan
/// display and mean opposite things to anyone reading a closing rate off them.
/// </param>
/// <param name="Classification">
/// What the contact is believed to be. <see cref="TrackClassification.Unknown"/> is the absence
/// of a claim, so a report carrying it never erases a classification an earlier source made.
/// </param>
/// <param name="SourceId">
/// Identifier of the reporting sensor or feed, or null to attribute the report to the caller
/// generically. Sources are carried per observation because a fused track routinely mixes a
/// sparse identity-bearing report with a dense anonymous one.
/// </param>
/// <param name="SourceKind">How this source observes.</param>
/// <param name="SourceQuality">
/// Confidence this source places in its own contribution, in 0-1, or null when it reports none.
/// Absent must stay distinguishable from zero.
/// </param>
/// <param name="Confidence">
/// Confidence that the contact is real, in 0-1, or null to inherit <paramref name="SourceQuality"/>
/// and failing that a neutral default. This is the value the store <em>degrades</em> as the
/// report ages; the number the caller supplied is preserved separately on
/// <see cref="AgedExternalTrack.ReportedConfidence"/> so the two are never confused.
/// </param>
/// <param name="ObservedAtSimulationTimeSeconds">
/// Simulation time the observation was made at, in seconds, or null to stamp it with the
/// session's current simulation time on arrival. Simulation time rather than a wall clock: it is
/// what ageing is measured against, and a replay of the same reports must age them identically.
/// </param>
/// <param name="PositionAccuracyM">One-sigma horizontal position accuracy in metres, or null when unreported.</param>
/// <param name="VelocityAccuracyMps">One-sigma velocity accuracy in metres per second, or null when unreported.</param>
/// <param name="Label">Operator-facing label. Display only; never parsed, and never used to resolve anything.</param>
/// <param name="Transponder">Cooperative broadcast identity, or null for a non-cooperative contact.</param>
public sealed record TrackReportRequest(
    string TrackId,
    FramedPose Pose,
    FramedTwist? Twist = null,
    TrackClassification Classification = TrackClassification.Unknown,
    string? SourceId = null,
    TrackSourceKind SourceKind = TrackSourceKind.Unknown,
    double? SourceQuality = null,
    double? Confidence = null,
    double? ObservedAtSimulationTimeSeconds = null,
    double? PositionAccuracyM = null,
    double? VelocityAccuracyMps = null,
    string? Label = null,
    TransponderIdentity? Transponder = null);

/// <summary>Stable machine-readable codes for failures on the external-track surface.</summary>
/// <remarks>
/// Separate from <see cref="AssetProblems"/> and <see cref="CommandRejectionReasons"/> because
/// tracks are a separate concern with separate failure modes, and following the same convention
/// as both: the code is the contract, the prose beside it is not.
/// </remarks>
public static class TrackProblems
{
    /// <summary>The request body was absent or could not be bound.</summary>
    public const string RequestInvalid = "track.requestInvalid";

    /// <summary>The track identifier is missing or malformed.</summary>
    public const string TrackIdInvalid = "track.trackIdInvalid";

    /// <summary>The reported pose declared no coordinate frame.</summary>
    public const string PoseFrameUnspecified = "track.poseFrameUnspecified";

    /// <summary>The reported pose carried a non-finite or out-of-range coordinate.</summary>
    public const string PoseInvalid = "track.poseInvalid";

    /// <summary>The reported velocity is structurally unusable, or names a frame a velocity cannot live in.</summary>
    public const string TwistInvalid = "track.twistInvalid";

    /// <summary>A free-text field is over-long or outside the allowed character set.</summary>
    public const string MetadataInvalid = "track.metadataInvalid";

    /// <summary>A confidence or accuracy value is non-finite or outside its permitted range.</summary>
    public const string QualityInvalid = "track.qualityInvalid";

    /// <summary>The observation time is non-finite or outside the window the session will accept.</summary>
    public const string ObservationTimeInvalid = "track.observationTimeInvalid";

    /// <summary>
    /// The report was older than the observation already held for this track, so it was
    /// discarded rather than allowed to move the contact backwards.
    /// </summary>
    public const string ReportOutOfOrder = "track.reportOutOfOrder";

    /// <summary>The session already holds as many tracks as it retains, and none was staler than this report.</summary>
    public const string CapacityReached = "track.capacityReached";

    /// <summary>No track with the requested identifier is held by this session.</summary>
    public const string NotFound = "track.notFound";

    /// <summary>
    /// A command was addressed to a track identifier.
    /// </summary>
    /// <remarks>
    /// This code exists so that mis-addressing a command is answered plainly instead of as a
    /// confusing "asset not found", and for no other reason. It confers nothing: tracks carry no
    /// <see cref="AssetCapability"/>, every command gate keys on capability, and no command path
    /// resolves the track identifier space at all. A contact is observed, never driven.
    /// </remarks>
    public const string NotCommandable = "track.notCommandable";
}

/// <summary>One held track together with how old the observation behind it is.</summary>
/// <remarks>
/// Age is published as a number rather than left to be derived from
/// <see cref="ExternalTrackState.LastUpdateTime"/> and a frame timestamp, because a consumer that
/// has to compute staleness is a consumer that can forget to. Anything downstream that reads a
/// geometry off a contact — a range, a bearing, a closing rate — is only as good as the age
/// beside it, and an advisory whose staleness is invisible is worse than no advisory.
/// <para>
/// <see cref="ReportedConfidence"/> and the confidence inside
/// <see cref="ExternalTrackState.Quality"/> are two different quantities and both are published:
/// the first is what the source claimed when it last reported, the second is that claim after
/// ageing has discounted it. Collapsing them would either overstate a stale contact or
/// permanently understate a source that reports well.
/// </para>
/// </remarks>
/// <param name="Track">The fused track, with freshness and discounted confidence already applied.</param>
/// <param name="AgeSeconds">
/// Simulated seconds between the newest observation fused into the track and the moment this
/// view was taken. Never negative.
/// </param>
/// <param name="ObservedAtSimulationTimeSeconds">Simulation time of that newest observation.</param>
/// <param name="ReportedConfidence">Confidence the source last claimed, in 0-1, before ageing discounted it.</param>
public sealed record AgedExternalTrack(
    ExternalTrackState Track,
    double AgeSeconds,
    double ObservedAtSimulationTimeSeconds,
    double ReportedConfidence)
{
    /// <summary>True when the observation is no longer inside its expected reporting interval.</summary>
    /// <remarks>
    /// A convenience for display code, not a licence to act: a track that is not degraded is
    /// still an observation, and nothing about it is a decision.
    /// </remarks>
    public bool IsDegraded => Track.Freshness != DataFreshness.Fresh;
}

/// <summary>Result of accepting one track report into a session.</summary>
/// <param name="TrackId">Track the report was fused into.</param>
/// <param name="Track">The track as it stands after the report, with its age.</param>
/// <param name="Created">True when this report started a new track rather than updating one.</param>
/// <param name="EvictedTrackId">
/// Track discarded to make room for a new one, or null when nothing was evicted. Surfaced rather
/// than hidden so a caller can see that the session is at its retention limit.
/// </param>
public sealed record TrackReportResponse(
    string TrackId,
    AgedExternalTrack Track,
    bool Created,
    string? EvictedTrackId = null);

/// <summary>Every track a session currently holds, as of one simulation time.</summary>
/// <remarks>
/// The counters are cumulative for the life of the session and exist to make the store's bounds
/// observable: a client that sees <paramref name="DroppedTrackCount"/> climbing knows contacts
/// are being retired, and one that sees <paramref name="RejectedReportCount"/> climbing knows a
/// source is reporting faster than the session will retain.
/// </remarks>
/// <param name="Tracks">Held tracks, freshest observation first.</param>
/// <param name="SimulationTimeSeconds">Simulation time the ages in <paramref name="Tracks"/> were computed at.</param>
/// <param name="Capacity">Most tracks this session retains at once.</param>
/// <param name="DroppedTrackCount">Tracks retired so far, by ageing out or by eviction.</param>
/// <param name="RejectedReportCount">Reports refused so far, whether stale, out of order or over capacity.</param>
public sealed record TrackInventoryResponse(
    IReadOnlyList<AgedExternalTrack> Tracks,
    double SimulationTimeSeconds,
    int Capacity,
    long DroppedTrackCount,
    long RejectedReportCount);
