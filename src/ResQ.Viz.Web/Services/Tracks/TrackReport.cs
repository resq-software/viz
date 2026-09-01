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
using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Tracks;

/// <summary>Why a track report was refused, in the form the boundary reports it.</summary>
/// <param name="ReasonCode">Stable code from <see cref="TrackProblems"/>.</param>
/// <param name="Message">Operator-facing explanation. Render it; never parse it.</param>
/// <param name="Field">Dotted path of the offending field, when the refusal is attributable to one.</param>
public sealed record TrackReportRejection(string ReasonCode, string Message, string? Field = null);

/// <summary>One validated observation of a contact, ready to be fused.</summary>
/// <remarks>
/// Produced only by <see cref="TryCreate"/>, so holding one is proof that the identifier is
/// well formed, the pose names a frame this session can resolve, every coordinate is finite and
/// bounded, and every confidence is in range. The store therefore never re-checks a payload, and
/// nothing downstream has to decide what to do with a contact at infinity.
/// <para>
/// Observation time is carried in <b>simulated</b> seconds, not as an instant. Ageing is what
/// this value exists for, ageing is driven by simulation time, and a report that arrived with a
/// wall-clock stamp would age differently on a paused session, a fast-forwarded one and a replay
/// of the same scenario.
/// </para>
/// </remarks>
/// <param name="TrackId">Identifier of the contact being reported.</param>
/// <param name="Classification">What the source believes the contact is.</param>
/// <param name="Pose">Frame-qualified pose, in a local Cartesian frame.</param>
/// <param name="Twist">Frame-qualified velocity, or null when the source observed none.</param>
/// <param name="SourceId">Identifier of the reporting sensor or feed.</param>
/// <param name="SourceKind">How that source observes.</param>
/// <param name="SourceQuality">Confidence the source places in its own contribution, in 0-1, or null.</param>
/// <param name="Confidence">Confidence the contact is real, in 0-1, before ageing discounts it.</param>
/// <param name="ObservedAtSimulationTimeSeconds">Simulation time the observation was made at.</param>
/// <param name="PositionAccuracyM">One-sigma horizontal position accuracy in metres, or null.</param>
/// <param name="VelocityAccuracyMps">One-sigma velocity accuracy in metres per second, or null.</param>
/// <param name="Label">Operator-facing label, or null.</param>
/// <param name="Transponder">Cooperative broadcast identity, or null for a non-cooperative contact.</param>
public sealed record TrackReport(
    string TrackId,
    TrackClassification Classification,
    FramedPose Pose,
    FramedTwist? Twist,
    string SourceId,
    TrackSourceKind SourceKind,
    double? SourceQuality,
    double Confidence,
    double ObservedAtSimulationTimeSeconds,
    double? PositionAccuracyM = null,
    double? VelocityAccuracyMps = null,
    string? Label = null,
    TransponderIdentity? Transponder = null)
{
    /// <summary>Longest an identifier may be, matching the limit the asset surface applies.</summary>
    public const int MaxIdentifierLength = 64;

    /// <summary>Longest a free-text label or broadcast string may be.</summary>
    public const int MaxLabelLength = 64;

    /// <summary>Furthest from the scene origin a reported contact may be plotted, in metres.</summary>
    public const double MaxCoordinateM = 20_000.0;

    /// <summary>
    /// Fastest a reported contact may be travelling, in metres per second.
    /// </summary>
    /// <remarks>
    /// A sanity bound on a number the geometry divides by, not a claim about what can fly. A
    /// velocity of 1e30 produces a closest approach in femtoseconds and a bearing that is pure
    /// rounding noise, and refusing it at the boundary is cheaper than defending every consumer.
    /// </remarks>
    public const double MaxSpeedMps = 1_000.0;

    /// <summary>Oldest an observation may be stamped, in simulated seconds before now.</summary>
    public const double MaxObservationBacklogSeconds = 86_400.0;

    /// <summary>Neutral confidence used when neither the report nor its source declared one.</summary>
    /// <remarks>
    /// Mid-scale rather than 1.0: a source that says nothing about its confidence has not said
    /// it is certain, and defaulting to certainty would let an unqualified feed outrank a
    /// calibrated one that honestly reported 0.8.
    /// </remarks>
    public const double DefaultConfidence = 0.5;

    /// <summary>Fallback source identifier for a report that named no sensor.</summary>
    public const string UnattributedSourceId = "unattributed";

    private static readonly char[] IdentifierExtraChars = ['-', '_', '.'];

    private static readonly char[] LabelExtraChars =
        [' ', '-', '_', '.', ',', '\'', '&', '(', ')', '+', '/'];

    /// <summary>Validates one incoming report and converts it to the fusable form.</summary>
    /// <remarks>
    /// Pure: the only thing it reads beyond its arguments is the current simulation time, which
    /// is passed in. Nothing is mutated, so a refusal cannot leave a half-applied report behind.
    /// <para>
    /// Frames are handled exactly as the spawn boundary handles them. A local Cartesian pose
    /// converts by a pure basis change and is accepted; a geodetic one needs a
    /// <see cref="LocalOrigin"/> the session does not carry, and plotting a contact against an
    /// assumed origin is the silent failure the framed model exists to prevent; a body frame is
    /// not a location at all. The same reasoning refuses a body-frame velocity, because
    /// resolving one needs an attitude a contact usually has not declared.
    /// </para>
    /// </remarks>
    /// <param name="request">Incoming request body, which may be null.</param>
    /// <param name="nowSimulationTimeSeconds">Session's current simulation time, in seconds.</param>
    /// <param name="report">The validated report on success, otherwise null.</param>
    /// <param name="rejection">The coded refusal on failure, otherwise null.</param>
    /// <returns><see langword="true"/> when the report is usable.</returns>
    public static bool TryCreate(
        TrackReportRequest? request,
        double nowSimulationTimeSeconds,
        [NotNullWhen(true)] out TrackReport? report,
        [NotNullWhen(false)] out TrackReportRejection? rejection)
    {
        report = null;

        if (request is null)
        {
            rejection = new TrackReportRejection(
                TrackProblems.RequestInvalid, "A track report body is required.");
            return false;
        }

        if (!IsIdentifier(request.TrackId))
        {
            rejection = new TrackReportRejection(
                TrackProblems.TrackIdInvalid,
                $"A track id must be 1-{MaxIdentifierLength} characters of letters, digits, '-', '_' or '.'.",
                "trackId");
            return false;
        }

        rejection = ValidatePose(request.Pose)
            ?? ValidateTwist(request.Twist)
            ?? ValidateQuality(request)
            ?? ValidateText(request)
            ?? ValidateEnumerations(request);
        if (rejection is not null)
        {
            return false;
        }

        if (!TryResolveObservationTime(
            request.ObservedAtSimulationTimeSeconds, nowSimulationTimeSeconds, out double observedAt))
        {
            rejection = new TrackReportRejection(
                TrackProblems.ObservationTimeInvalid,
                "The observation time must be finite, no later than the session's current simulation time "
                    + $"and no more than {MaxObservationBacklogSeconds:N0} s before it.",
                "observedAtSimulationTimeSeconds");
            return false;
        }

        report = new TrackReport(
            TrackId: request.TrackId,
            Classification: request.Classification,
            Pose: request.Pose,
            Twist: request.Twist,
            SourceId: string.IsNullOrWhiteSpace(request.SourceId) ? UnattributedSourceId : request.SourceId,
            SourceKind: request.SourceKind,
            SourceQuality: request.SourceQuality,
            Confidence: request.Confidence ?? request.SourceQuality ?? DefaultConfidence,
            ObservedAtSimulationTimeSeconds: observedAt,
            PositionAccuracyM: request.PositionAccuracyM,
            VelocityAccuracyMps: request.VelocityAccuracyMps,
            Label: string.IsNullOrWhiteSpace(request.Label) ? null : request.Label,
            Transponder: request.Transponder);

        rejection = null;
        return true;
    }

    /// <summary>Refuses any enum on this request that names no declared member.</summary>
    /// <remarks>
    /// <c>System.Text.Json</c> binds a numeric enum without checking that the number names a
    /// member: a body carrying <c>"classification": 9999</c> and <c>"sourceKind": -3</c>
    /// deserialises cleanly, and so does a transponder kind or a geodetic datum outside its
    /// enum. Every other field on this request is validated, so these four were the only route
    /// by which a value outside the model reached the fusion store and then the snapshot
    /// broadcast to every client — and a client narrowing on the discriminator has no case for
    /// 9999, so it drops the contact or draws it as whatever its switch falls through to.
    /// <para>
    /// The geodetic datum inside the pose is checked here rather than in
    /// <see cref="ValidatePose"/> because the two gates answer different questions.
    /// <see cref="ValidatePose"/> resolves the contact's <em>position</em>, which is carried in
    /// the local Cartesian frame; <see cref="FramedPose.Geo"/> is echoed on to the wire by
    /// <c>ExternalTrackStore</c> without being read at all, and untouched is exactly why it has
    /// to be right on the way in.
    /// </para>
    /// <para>
    /// Reported as <see cref="TrackProblems.RequestInvalid"/> with the offending field named: an
    /// undefined enum is a body that could not be bound to a value this model has.
    /// </para>
    /// </remarks>
    /// <param name="request">Incoming request body, already past the structural gates.</param>
    /// <returns>A coded refusal, or null when every enum names a declared member.</returns>
    private static TrackReportRejection? ValidateEnumerations(TrackReportRequest request)
    {
        if (!Enum.IsDefined(request.Classification))
        {
            return UndeclaredEnum("classification", nameof(TrackClassification), (long)request.Classification);
        }

        if (!Enum.IsDefined(request.SourceKind))
        {
            return UndeclaredEnum("sourceKind", nameof(TrackSourceKind), (long)request.SourceKind);
        }

        if (request.Transponder is { } transponder && !Enum.IsDefined(transponder.Kind))
        {
            return UndeclaredEnum(
                "transponder.kind", nameof(TransponderKind), (long)transponder.Kind);
        }

        return request.Pose is { Geo: { } geo } && !Enum.IsDefined(geo.VerticalReference)
            ? UndeclaredEnum(
                "pose.geo.verticalReference", nameof(VerticalReference), (long)geo.VerticalReference)
            : null;
    }

    private static TrackReportRejection UndeclaredEnum(string field, string enumName, long value) =>
        new(
            TrackProblems.RequestInvalid,
            $"'{field}' must name a declared {enumName}; {value} does not.",
            field);

    private static TrackReportRejection? ValidatePose(FramedPose? pose)
    {
        if (pose is null)
        {
            return new TrackReportRejection(
                TrackProblems.RequestInvalid, "A frame-qualified pose is required.", "pose");
        }

        if (!CoordinateFrames.IsSpecified(pose.Frame))
        {
            return new TrackReportRejection(
                TrackProblems.PoseFrameUnspecified,
                "The reported pose must name its coordinate frame; a bare position is not a location.",
                "pose.frame");
        }

        if (!CoordinateFrames.IsLocalCartesian(pose.Frame))
        {
            return new TrackReportRejection(
                TrackProblems.PoseInvalid,
                $"Frame '{pose.Frame}' cannot be resolved to a contact position; use localEus, localEnu or localNed.",
                "pose.frame");
        }

        // A zero quaternion is the absence of an attitude rather than a bad one, and a contact
        // routinely has no attitude to report, so it is substituted before the structural check.
        var candidate = pose.Orientation.Equals(default(Quaternion))
            ? pose with { Orientation = Quaternion.Identity }
            : pose;

        if (!CoordinateFrames.TryValidate(candidate, out var error))
        {
            return new TrackReportRejection(
                TrackProblems.PoseInvalid, $"The reported pose is not usable: {error}.", "pose");
        }

        return IsWithinWorld(
            CoordinateFrames.TransformVector(candidate.Position, candidate.Frame, CoordinateFrame.LocalEus))
            ? null
            : new TrackReportRejection(
                TrackProblems.PoseInvalid,
                $"Contact coordinates must be finite and within {MaxCoordinateM:N0} m of the scene origin.",
                "pose.position");
    }

    private static TrackReportRejection? ValidateTwist(FramedTwist? twist)
    {
        if (twist is null)
        {
            return null;
        }

        if (!CoordinateFrames.IsLocalCartesian(twist.Frame))
        {
            return new TrackReportRejection(
                TrackProblems.TwistInvalid,
                $"Frame '{twist.Frame}' cannot be resolved to a contact velocity; use localEus, localEnu or localNed.",
                "twist.frame");
        }

        if (!CoordinateFrames.TryValidate(twist, out var error))
        {
            return new TrackReportRejection(
                TrackProblems.TwistInvalid, $"The reported velocity is not usable: {error}.", "twist");
        }

        return twist.Linear.Length() <= MaxSpeedMps
            ? null
            : new TrackReportRejection(
                TrackProblems.TwistInvalid,
                $"A reported speed must be at most {MaxSpeedMps:N0} m/s.",
                "twist.linear");
    }

    private static TrackReportRejection? ValidateQuality(TrackReportRequest request) =>
        !IsUnitInterval(request.Confidence) ? Quality("confidence", "in 0-1")
        : !IsUnitInterval(request.SourceQuality) ? Quality("sourceQuality", "in 0-1")
        : !IsNonNegative(request.PositionAccuracyM) ? Quality("positionAccuracyM", "finite and not negative")
        : !IsNonNegative(request.VelocityAccuracyMps) ? Quality("velocityAccuracyMps", "finite and not negative")
        : null;

    private static TrackReportRejection Quality(string field, string requirement) =>
        new(TrackProblems.QualityInvalid, $"'{field}' must be {requirement}.", field);

    private static TrackReportRejection? ValidateText(TrackReportRequest request)
    {
        if (request.SourceId is { Length: > 0 } sourceId && !IsIdentifier(sourceId))
        {
            return new TrackReportRejection(
                TrackProblems.MetadataInvalid,
                $"A source id must be 1-{MaxIdentifierLength} characters of letters, digits, '-', '_' or '.'.",
                "sourceId");
        }

        return LabelFailure("label", request.Label) ?? TransponderFailure(request.Transponder);
    }

    private static TrackReportRejection? TransponderFailure(TransponderIdentity? identity) =>
        identity is null
            ? null
            : LabelFailure("transponder.identifier", identity.Identifier)
                ?? LabelFailure("transponder.callSign", identity.CallSign)
                ?? LabelFailure("transponder.code", identity.Code)
                ?? LabelFailure("transponder.registration", identity.Registration)
                ?? LabelFailure("transponder.navigationStatus", identity.NavigationStatus)
                ?? LabelFailure("transponder.operator", identity.Operator);

    private static TrackReportRejection? LabelFailure(string field, string? value) =>
        IsAcceptableLabel(value)
            ? null
            : new TrackReportRejection(
                TrackProblems.MetadataInvalid,
                $"'{field}' must be at most {MaxLabelLength} characters of letters, digits, spaces "
                    + "or the punctuation - _ . , ' & ( ) + / .",
                field);

    private static bool TryResolveObservationTime(double? declared, double now, out double observedAt)
    {
        if (!double.IsFinite(now))
        {
            observedAt = 0.0;
            return false;
        }

        if (declared is not { } value)
        {
            observedAt = now;
            return true;
        }

        observedAt = value;
        return double.IsFinite(value)
            && value <= now
            && value >= now - MaxObservationBacklogSeconds;
    }

    private static bool IsIdentifier([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxIdentifierLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || IdentifierExtraChars.Contains(c));

    private static bool IsAcceptableLabel(string? value) =>
        value is null
        || (value.Length <= MaxLabelLength
            && value.All(c => char.IsAsciiLetterOrDigit(c) || LabelExtraChars.Contains(c)));

    private static bool IsUnitInterval(double? value) =>
        value is not { } v || (double.IsFinite(v) && v is >= 0.0 and <= 1.0);

    private static bool IsNonNegative(double? value) =>
        value is not { } v || (double.IsFinite(v) && v >= 0.0);

    private static bool IsWithinWorld(Vector3 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
        && Math.Abs(position.X) <= MaxCoordinateM
        && Math.Abs(position.Y) <= MaxCoordinateM
        && Math.Abs(position.Z) <= MaxCoordinateM;
}
