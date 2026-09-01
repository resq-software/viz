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

namespace ResQ.Viz.Web.Services;

/// <summary>A validated command, translated into the typed form an asset model consumes.</summary>
/// <remarks>
/// Produced only by <see cref="CommandCatalog.Validate"/>, so holding one is proof that every
/// gate passed: the kind exists, the payload parsed, the asset resolved, the capability is
/// declared, the domain matches, the operational state permits it and the position is fresh
/// enough. Downstream code takes an intent rather than an envelope precisely so it cannot
/// accidentally act on an unvalidated request, and so nothing re-parses parameter strings.
/// </remarks>
/// <param name="CommandId">Identifier of the command this intent came from.</param>
/// <param name="AssetId">Asset to act on.</param>
/// <param name="Domain">Domain of that asset, resolved from its descriptor.</param>
/// <param name="Kind">Validated command kind from <see cref="CommandKinds"/>.</param>
/// <param name="RequiredCapabilities">Capabilities the command was gated on, carried for audit.</param>
/// <param name="Target">Validated target, or null for a command that needs none.</param>
/// <param name="Frame">Frame positional parameters are expressed in, or null when none was declared.</param>
/// <param name="Constraints">Validated per-command motion limits, or null to use the asset's own.</param>
/// <param name="Deadline">Instant after which executing the command is pointless.</param>
/// <param name="SpeedMps">Commanded speed in metres per second, range-checked against the asset's limits.</param>
/// <param name="AltitudeM">Commanded altitude in metres.</param>
/// <param name="CourseRad">Commanded course, normalised to [0, 2pi) radians clockwise from true north.</param>
/// <param name="SteeringAngleRad">Commanded steering angle in radians; positive turns to starboard.</param>
/// <param name="RadiusM">Loiter radius or station-keeping tolerance radius in metres.</param>
public sealed record CommandIntent(
    Guid CommandId,
    string AssetId,
    AssetDomain Domain,
    string Kind,
    AssetCapability RequiredCapabilities,
    CommandTarget? Target,
    CoordinateFrame? Frame,
    MotionConstraints? Constraints,
    DateTimeOffset? Deadline,
    double? SpeedMps = null,
    double? AltitudeM = null,
    double? CourseRad = null,
    double? SteeringAngleRad = null,
    double? RadiusM = null);

/// <summary>Outcome of validating one command: an accepted intent, or a coded rejection.</summary>
/// <remarks>
/// Deliberately not a bare <see cref="bool"/> plus out-parameters. A rejection has to carry a
/// stable code, operator-facing prose and, where it applies, the field at fault — and it has
/// to be impossible to read the intent from a rejected result.
/// </remarks>
public sealed record CommandValidationResult
{
    private CommandValidationResult(
        Guid commandId, string assetId, CommandIntent? intent, string? reasonCode, string? message, string? field)
    {
        CommandId = commandId;
        AssetId = assetId;
        Intent = intent;
        ReasonCode = reasonCode;
        Message = message;
        Field = field;
    }

    /// <summary>Command this outcome refers to.</summary>
    public Guid CommandId { get; }

    /// <summary>Asset the command was aimed at, as the issuer named it.</summary>
    public string AssetId { get; }

    /// <summary>Translated intent when accepted, otherwise <see langword="null"/>.</summary>
    public CommandIntent? Intent { get; }

    /// <summary>Stable code from <see cref="CommandRejectionReasons"/> when rejected.</summary>
    public string? ReasonCode { get; }

    /// <summary>Operator-facing explanation when rejected. Render it; never parse it.</summary>
    public string? Message { get; }

    /// <summary>Dotted path of the offending field when the rejection is attributable to one.</summary>
    public string? Field { get; }

    /// <summary>True when the command passed every gate.</summary>
    [MemberNotNullWhen(true, nameof(Intent))]
    public bool IsAccepted => Intent is not null;

    /// <summary>Wraps an accepted intent.</summary>
    /// <param name="intent">The translated command.</param>
    /// <returns>An accepted result.</returns>
    public static CommandValidationResult Accept(CommandIntent intent) =>
        new(intent.CommandId, intent.AssetId, intent, null, null, null);

    /// <summary>Builds a rejection. Carries no intent, so nothing downstream can act on it.</summary>
    /// <param name="commandId">Command being rejected.</param>
    /// <param name="assetId">Asset the command named.</param>
    /// <param name="reasonCode">Stable code from <see cref="CommandRejectionReasons"/>.</param>
    /// <param name="message">Operator-facing explanation.</param>
    /// <param name="field">Dotted path of the offending field, when there is one.</param>
    /// <returns>A rejected result.</returns>
    public static CommandValidationResult Reject(
        Guid commandId, string assetId, string reasonCode, string message, string? field = null) =>
        new(commandId, assetId, null, reasonCode, message, field);

    /// <summary>Projects this outcome to the status record the issuer receives.</summary>
    /// <param name="nowUtc">Instant validation completed; becomes the accept time.</param>
    /// <returns>An accepted or rejected <see cref="CommandResult"/>.</returns>
    public CommandResult ToCommandResult(DateTimeOffset nowUtc) => IsAccepted
        ? CommandResult.Accepted(CommandId, nowUtc)
        : CommandResult.Rejected(CommandId, ReasonCode ?? string.Empty, Message ?? string.Empty);

    /// <summary>Projects a rejection to a problem-details body.</summary>
    /// <param name="traceId">Correlation identifier tying the response to server logs.</param>
    /// <returns>The problem body describing why the command was refused.</returns>
    /// <exception cref="InvalidOperationException">The command was accepted, so there is no problem to report.</exception>
    public CommandProblemDetails ToProblem(string? traceId = null)
    {
        if (IsAccepted)
        {
            throw new InvalidOperationException("An accepted command has no problem to report.");
        }

        var code = ReasonCode ?? string.Empty;
        var detail = Message ?? string.Empty;
        var errors = new List<CommandFieldError>(1);
        if (Field is not null)
        {
            errors.Add(new CommandFieldError(Field, code, detail));
        }

        return new CommandProblemDetails(code, TitleFor(code), detail, traceId, AssetId, CommandId, errors);
    }

    private static string TitleFor(string code) => code switch
    {
        _ when code.StartsWith("payload.", StringComparison.Ordinal) => "Invalid command payload",
        _ when code.StartsWith("deadline.", StringComparison.Ordinal) => "Command deadline expired",
        _ when code.StartsWith("asset.", StringComparison.Ordinal) => "Asset unavailable",
        _ when code.StartsWith("capability.", StringComparison.Ordinal) => "Capability not declared",
        _ when code.StartsWith("domain.", StringComparison.Ordinal) => "Command does not apply to this domain",
        _ when code.StartsWith("state.", StringComparison.Ordinal) => "Operational state does not permit this command",
        _ when code.StartsWith("position.", StringComparison.Ordinal) => "Position report is not fresh enough",
        _ when code.StartsWith("idempotency.", StringComparison.Ordinal) => "Idempotency key conflict",
        _ => "Command rejected",
    };
}
