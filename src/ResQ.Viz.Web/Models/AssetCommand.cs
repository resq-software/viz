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

using System.Text.Json.Serialization;

namespace ResQ.Viz.Web.Models;

/// <summary>The shapes a <see cref="CommandTarget"/> can take.</summary>
/// <remarks>
/// A flags enum rather than a bare discriminator because the question command validation
/// actually asks is set membership — "does <c>followRoute</c> accept this shape of target?" —
/// and a mask answers it with one test per command definition instead of a <c>switch</c>
/// repeated at every call site.
/// </remarks>
[Flags]
public enum CommandTargetKinds
{
    /// <summary>No target is accepted. Supplying one is a payload error, not a silent no-op.</summary>
    None = 0,

    /// <summary>A frame-qualified local point; see <see cref="PointCommandTarget"/>.</summary>
    Point = 1 << 0,

    /// <summary>A geodetic point; see <see cref="GeoCommandTarget"/>.</summary>
    Geo = 1 << 1,

    /// <summary>Another asset or station by identifier; see <see cref="AssetCommandTarget"/>.</summary>
    Asset = 1 << 2,

    /// <summary>A stored route by identifier; see <see cref="RouteCommandTarget"/>.</summary>
    Route = 1 << 3,
}

/// <summary>Where a command is aimed: a framed point, a geodetic point, an asset or a route.</summary>
/// <remarks>
/// A closed union rather than a bag of nullable fields on <see cref="AssetCommandEnvelope"/>.
/// "Go to these coordinates", "dock with that vessel" and "run route R7" are different
/// requests with different validity rules, and flattening them into one record makes every
/// invalid combination representable — <c>goTo</c> with a route id and no position, for
/// instance, which then has to be caught by hand at each consumer.
/// <para>
/// The wire discriminator is a <c>type</c> property carrying <c>"point"</c>, <c>"geo"</c>,
/// <c>"asset"</c> or <c>"route"</c>, matching the convention
/// <see cref="IAssetDomainState"/> already uses so the TypeScript client narrows the same way
/// on both unions. Server-side branching uses <see cref="Kind"/> instead of that string: it is
/// a flags value, so a command definition can declare the set of shapes it accepts.
/// </para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PointCommandTarget), PointCommandTarget.Discriminator)]
[JsonDerivedType(typeof(GeoCommandTarget), GeoCommandTarget.Discriminator)]
[JsonDerivedType(typeof(AssetCommandTarget), AssetCommandTarget.Discriminator)]
[JsonDerivedType(typeof(RouteCommandTarget), RouteCommandTarget.Discriminator)]
public abstract record CommandTarget
{
    /// <summary>Which shape this target is, for capability- and definition-level gating.</summary>
    [JsonIgnore]
    public abstract CommandTargetKinds Kind { get; }
}

/// <summary>A target expressed as a frame-qualified local point.</summary>
/// <remarks>
/// Carries a full <see cref="FramedPose"/> rather than a bare position so the frame and the
/// origin it was computed against travel with the numbers. Orientation is a request, not a
/// guarantee: an asset without heading authority ignores it.
/// </remarks>
/// <param name="Point">Frame-qualified position, and optionally the orientation to arrive with.</param>
/// <param name="AcceptanceRadiusM">
/// Distance in metres inside which the point counts as reached. Null lets the executing model
/// pick its own tolerance, which is honest because that tolerance is vehicle-specific.
/// </param>
public sealed record PointCommandTarget(
    FramedPose Point,
    double? AcceptanceRadiusM = null) : CommandTarget
{
    /// <summary>Wire discriminator for <see cref="PointCommandTarget"/>.</summary>
    public const string Discriminator = "point";

    /// <inheritdoc />
    [JsonIgnore]
    public override CommandTargetKinds Kind => CommandTargetKinds.Point;
}

/// <summary>A target expressed as a geodetic position.</summary>
/// <remarks>
/// Kept distinct from <see cref="PointCommandTarget"/> because a geodetic point has to be
/// resolved against a <see cref="LocalOrigin"/> before anything can drive to it, and that
/// resolution can fail. Accepting both shapes and converting at the edge is what lets a chart
/// plotter and the scene issue the same command.
/// </remarks>
/// <param name="Position">Geodetic position with an explicitly named vertical datum.</param>
/// <param name="AcceptanceRadiusM">Distance in metres inside which the point counts as reached.</param>
public sealed record GeoCommandTarget(
    GeoPosition Position,
    double? AcceptanceRadiusM = null) : CommandTarget
{
    /// <summary>Wire discriminator for <see cref="GeoCommandTarget"/>.</summary>
    public const string Discriminator = "geo";

    /// <inheritdoc />
    [JsonIgnore]
    public override CommandTargetKinds Kind => CommandTargetKinds.Geo;
}

/// <summary>A target expressed as another asset or station, by identifier.</summary>
/// <remarks>
/// Resolved when the command executes rather than when it is issued, so a moving target stays
/// a moving target instead of being frozen into a stale position at issue time.
/// </remarks>
/// <param name="AssetId">Identifier of the asset or station being aimed at.</param>
/// <param name="StandoffM">Distance in metres to stop short of the referenced asset; null uses the model's own.</param>
public sealed record AssetCommandTarget(
    string AssetId,
    double? StandoffM = null) : CommandTarget
{
    /// <summary>Wire discriminator for <see cref="AssetCommandTarget"/>.</summary>
    public const string Discriminator = "asset";

    /// <inheritdoc />
    [JsonIgnore]
    public override CommandTargetKinds Kind => CommandTargetKinds.Asset;
}

/// <summary>A target expressed as a stored route, by identifier.</summary>
/// <param name="RouteId">Identifier of the route to execute.</param>
/// <param name="StartWaypointIndex">
/// Zero-based waypoint to resume from. Null starts at the beginning. Present so a resumed
/// route does not have to be re-issued as a truncated copy under a new identifier.
/// </param>
public sealed record RouteCommandTarget(
    string RouteId,
    int? StartWaypointIndex = null) : CommandTarget
{
    /// <summary>Wire discriminator for <see cref="RouteCommandTarget"/>.</summary>
    public const string Discriminator = "route";

    /// <inheritdoc />
    [JsonIgnore]
    public override CommandTargetKinds Kind => CommandTargetKinds.Route;
}

/// <summary>A request for an asset to do something. A resource, not a fire-and-forget message.</summary>
/// <remarks>
/// The envelope is domain-neutral: <paramref name="Kind"/> is a string from
/// <c>CommandKinds</c> and every command-specific number lives in
/// <paramref name="Parameters"/> or <paramref name="Target"/>. That is what lets one
/// validation path gate air, ground and surface commands on declared capability rather than
/// on a <c>switch</c> over vehicle class.
/// <para>
/// <paramref name="IdempotencyKey"/> is mandatory because commands cross a network: a client
/// that retries after a timeout must be able to say "this is the same request", and the
/// server must be able to tell that from "this is a second, deliberate stop".
/// </para>
/// </remarks>
/// <param name="CommandId">Server- or client-minted identifier for this specific attempt.</param>
/// <param name="AssetId">Asset the command is aimed at.</param>
/// <param name="Kind">Command kind; one of the tokens in <c>CommandKinds</c>, matched ordinally.</param>
/// <param name="IssuedAt">When the issuer created the command.</param>
/// <param name="Deadline">After this instant the command is pointless; null means no deadline.</param>
/// <param name="IssuerId">Identity of the operator or service that issued the command.</param>
/// <param name="ControlLeaseId">
/// Lease proving the issuer currently holds control authority over the asset. Null when the
/// deployment does not use leases. Enforced above this layer, where the lease registry lives.
/// </param>
/// <param name="IdempotencyKey">Issuer-chosen key identifying the logical request behind retries.</param>
/// <param name="Frame">
/// Frame any positional parameters are expressed in. Never
/// <see cref="CoordinateFrame.Unspecified"/>: a declared-but-unspecified frame is rejected
/// rather than defaulted.
/// </param>
/// <param name="Target">Where the command is aimed, or null for a command that needs no target.</param>
/// <param name="Constraints">Per-command motion limits overriding the asset's defaults, or null.</param>
/// <param name="Parameters">Command-specific scalars as invariant-culture strings, or null.</param>
public sealed record AssetCommandEnvelope(
    Guid CommandId,
    string AssetId,
    string Kind,
    DateTimeOffset IssuedAt,
    DateTimeOffset? Deadline,
    string IssuerId,
    string? ControlLeaseId,
    string IdempotencyKey,
    CoordinateFrame? Frame,
    CommandTarget? Target,
    MotionConstraints? Constraints,
    IReadOnlyDictionary<string, string>? Parameters);

/// <summary>Lifecycle of a single command.</summary>
/// <remarks>
/// Transport acknowledgement is not physical completion, and this enum keeps them apart.
/// <see cref="Accepted"/> means the command passed validation and was handed to the asset;
/// <see cref="Succeeded"/> means the asset actually did the thing. A UI that treats an
/// acknowledgement as completion shows a vessel as docked while it is still manoeuvring.
/// </remarks>
public enum CommandState
{
    /// <summary>Received, not yet validated.</summary>
    Requested,

    /// <summary>Validated and handed to the asset. Says nothing about physical progress.</summary>
    Accepted,

    /// <summary>Refused during validation. Terminal, and by contract free of side effects.</summary>
    Rejected,

    /// <summary>The asset is executing it.</summary>
    InProgress,

    /// <summary>The asset completed it. Terminal.</summary>
    Succeeded,

    /// <summary>Execution started and could not be completed. Terminal.</summary>
    Failed,

    /// <summary>Withdrawn by an operator or superseded by a later command. Terminal.</summary>
    Cancelled,

    /// <summary>The deadline passed before completion. Terminal.</summary>
    TimedOut,
}

/// <summary>Current status of a command, as reported back to whoever issued it.</summary>
/// <remarks>
/// One record covers the whole lifecycle so a client subscribes to a single shape instead of
/// correlating an acknowledgement DTO with a progress DTO with an error DTO.
/// </remarks>
/// <param name="CommandId">Identifier of the command this result describes.</param>
/// <param name="State">Where the command has got to.</param>
/// <param name="AcceptedAt">
/// When validation accepted the command. Null while still <see cref="CommandState.Requested"/>
/// and for anything <see cref="CommandState.Rejected"/> — a rejected command was never
/// accepted, and reporting an accept time for one is how audit trails go wrong.
/// </param>
/// <param name="ProgressPercent">Completion as 0–100. Stays 0 for a command that never started.</param>
/// <param name="Message">Operator-facing text. Render it; never parse it.</param>
/// <param name="ReasonCode">
/// Stable machine-readable rejection or failure code from <see cref="CommandRejectionReasons"/>,
/// or null when there is nothing to explain. This is the field tests and dashboards key on.
/// </param>
public sealed record CommandResult(
    Guid CommandId,
    CommandState State,
    DateTimeOffset? AcceptedAt,
    double ProgressPercent,
    string? Message = null,
    string? ReasonCode = null)
{
    /// <summary>True once the command can no longer change state.</summary>
    [JsonIgnore]
    public bool IsTerminal => State is CommandState.Rejected or CommandState.Succeeded
        or CommandState.Failed or CommandState.Cancelled or CommandState.TimedOut;

    /// <summary>Builds the result for a command that passed validation.</summary>
    /// <param name="commandId">Command that was accepted.</param>
    /// <param name="acceptedAt">Instant validation completed.</param>
    /// <param name="message">Optional operator-facing note.</param>
    /// <returns>An <see cref="CommandState.Accepted"/> result at zero progress.</returns>
    public static CommandResult Accepted(Guid commandId, DateTimeOffset acceptedAt, string? message = null) =>
        new(commandId, CommandState.Accepted, acceptedAt, 0, message);

    /// <summary>Builds the result for a command refused during validation.</summary>
    /// <param name="commandId">Command that was refused.</param>
    /// <param name="reasonCode">Stable code from <see cref="CommandRejectionReasons"/>.</param>
    /// <param name="message">Operator-facing explanation.</param>
    /// <returns>A terminal <see cref="CommandState.Rejected"/> result with no accept time.</returns>
    public static CommandResult Rejected(Guid commandId, string reasonCode, string message) =>
        new(commandId, CommandState.Rejected, null, 0, message, reasonCode);

    /// <summary>Builds an in-progress update for a command already accepted.</summary>
    /// <param name="commandId">Command being executed.</param>
    /// <param name="acceptedAt">Instant the command was originally accepted.</param>
    /// <param name="progressPercent">Completion as 0–100; clamped to that range.</param>
    /// <param name="message">Optional operator-facing note.</param>
    /// <returns>An <see cref="CommandState.InProgress"/> result.</returns>
    public static CommandResult Progress(
        Guid commandId, DateTimeOffset acceptedAt, double progressPercent, string? message = null) =>
        new(commandId, CommandState.InProgress, acceptedAt, Math.Clamp(progressPercent, 0, 100), message);
}

/// <summary>Stable machine-readable codes explaining why a command was refused.</summary>
/// <remarks>
/// String tokens rather than an enum for two reasons. They survive JSON without depending on
/// enum-serialisation settings, matching how <c>CoordinateFrames.TryValidate</c> already
/// reports failures; and every rejection path gets its own token, so a test can assert
/// <i>which</i> gate rejected a command rather than only that something did. Issuing
/// <c>takeoff</c> to a rover is <see cref="DomainNotApplicable"/> when the rover happens to
/// declare <see cref="AssetCapability.Takeoff"/>, and <see cref="CapabilityNotDeclared"/>
/// when it does not — different bugs, different codes.
/// </remarks>
public static class CommandRejectionReasons
{
    /// <summary>The command kind was empty or whitespace.</summary>
    public const string KindMissing = "payload.kindMissing";

    /// <summary>The command kind is not in the catalog.</summary>
    public const string KindUnknown = "payload.kindUnknown";

    /// <summary>The envelope carried no asset identifier.</summary>
    public const string AssetIdMissing = "payload.assetIdMissing";

    /// <summary>The envelope carried no issuer identity.</summary>
    public const string IssuerMissing = "payload.issuerMissing";

    /// <summary>The envelope carried no idempotency key.</summary>
    public const string IdempotencyKeyMissing = "payload.idempotencyKeyMissing";

    /// <summary>A coordinate frame was supplied but is unspecified or undefined.</summary>
    public const string FrameUnspecified = "payload.frameUnspecified";

    /// <summary>The command requires a target and none was supplied.</summary>
    public const string TargetMissing = "payload.targetMissing";

    /// <summary>A target was supplied but is structurally unusable.</summary>
    public const string TargetInvalid = "payload.targetInvalid";

    /// <summary>The target's shape is not one this command accepts.</summary>
    public const string TargetKindUnsupported = "payload.targetKindUnsupported";

    /// <summary>A parameter this command requires was absent.</summary>
    public const string ParameterMissing = "payload.parameterMissing";

    /// <summary>A parameter was present but did not parse as a finite number.</summary>
    public const string ParameterInvalid = "payload.parameterInvalid";

    /// <summary>A parameter parsed but falls outside what the asset can physically do.</summary>
    public const string ParameterOutOfRange = "payload.parameterOutOfRange";

    /// <summary>Supplied motion constraints are self-contradictory or non-physical.</summary>
    public const string ConstraintsInvalid = "payload.constraintsInvalid";

    /// <summary>The command's deadline had already passed when it was validated.</summary>
    public const string DeadlineExpired = "deadline.expired";

    /// <summary>No descriptor exists for the target asset.</summary>
    public const string AssetNotFound = "asset.notFound";

    /// <summary>The asset is known but has reported no state to validate against.</summary>
    public const string AssetStateUnavailable = "asset.stateUnavailable";

    /// <summary>Envelope, descriptor and state do not all name the same asset.</summary>
    public const string AssetIdMismatch = "asset.idMismatch";

    /// <summary>The asset does not declare the capability this command requires.</summary>
    public const string CapabilityNotDeclared = "capability.notDeclared";

    /// <summary>The command does not apply to the asset's domain.</summary>
    public const string DomainNotApplicable = "domain.notApplicable";

    /// <summary>The asset's operational state does not permit this command right now.</summary>
    public const string StateNotPermitted = "state.notPermitted";

    /// <summary>The command needs a current position and the asset's last report is not fresh.</summary>
    public const string PositionStale = "position.stale";

    /// <summary>The idempotency key was reused for a materially different payload.</summary>
    public const string IdempotencyKeyReuse = "idempotency.keyReuse";

    /// <summary>The request body could not be deserialised at all.</summary>
    /// <remarks>
    /// Distinct from every other code here because it is raised by the model binder rather than
    /// by the validator: the request never became an <see cref="AssetCommandEnvelope"/>, so there
    /// is no kind, no asset and no target to name a more specific reason against. It is
    /// nonetheless a rejection carrying the same guarantee as the rest — nothing in the
    /// simulation was touched — which is why it belongs in this catalogue rather than escaping
    /// as an unhandled fault.
    /// </remarks>
    public const string PayloadMalformed = "payload.malformed";
}

/// <summary>One field-level problem inside a rejected command.</summary>
/// <param name="Field">Dotted path of the offending field, e.g. "target" or "parameters.speed".</param>
/// <param name="Code">Stable code from <see cref="CommandRejectionReasons"/>.</param>
/// <param name="Message">Operator-facing explanation for this field.</param>
public sealed record CommandFieldError(string Field, string Code, string Message);

/// <summary>An RFC 9457-shaped error body for a refused command.</summary>
/// <remarks>
/// Deliberately a plain record rather than ASP.NET's <c>ProblemDetails</c>: command validation
/// is a pure function with no HTTP dependency, and its result has to be assertable in a unit
/// test and serialisable over SignalR as well as over REST.
/// <para>
/// <paramref name="Code"/> is the contract; <paramref name="Title"/> and
/// <paramref name="Detail"/> are prose and may be reworded at any time.
/// </para>
/// </remarks>
/// <param name="Code">Stable machine-readable code from <see cref="CommandRejectionReasons"/>.</param>
/// <param name="Title">Short human-readable summary of the problem class.</param>
/// <param name="Detail">Human-readable explanation of this specific occurrence.</param>
/// <param name="TraceId">Correlation identifier tying this response to server logs and traces.</param>
/// <param name="AssetId">Asset the refused command was aimed at, when known.</param>
/// <param name="CommandId">Command that was refused, when known.</param>
/// <param name="Errors">Per-field problems. Empty when the rejection is not attributable to one field.</param>
public sealed record CommandProblemDetails(
    string Code,
    string Title,
    string Detail,
    string? TraceId = null,
    string? AssetId = null,
    Guid? CommandId = null,
    IReadOnlyList<CommandFieldError>? Errors = null);
