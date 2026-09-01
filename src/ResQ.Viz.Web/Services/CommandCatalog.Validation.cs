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

using System.Globalization;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

public static partial class CommandCatalog
{
    /// <summary>Largest steering angle any modelled ground platform accepts, in radians.</summary>
    private const double MaxSteeringAngleRad = Math.PI / 2;

    /// <summary>Parameter keys parsed as numbers, checked whenever they are present.</summary>
    private static readonly string[] NumericParameters =
    [
        CommandParameters.Speed,
        CommandParameters.Altitude,
        CommandParameters.Course,
        CommandParameters.Steering,
        CommandParameters.Radius,
    ];

    /// <summary>
    /// Decides whether a command may be executed, and if so translates it into a
    /// <see cref="CommandIntent"/>.
    /// </summary>
    /// <remarks>
    /// A pure function: no clock (the instant is a parameter), no logging, no I/O, no mutation
    /// of anything reachable from its arguments. That is what makes "a rejection produces no
    /// side effects" a property of the code rather than a promise, and what lets every gate be
    /// tested with literals.
    /// <para>
    /// Gates run in a fixed order — payload, deadline, asset resolution, capability, domain,
    /// operational state, position freshness — and the first failure wins. The order is part of
    /// the contract: it is what makes the reason code deterministic when a request is wrong in
    /// more than one way, so a test can assert which gate fired.
    /// </para>
    /// <para>
    /// Issuer authentication and control-lease enforcement sit <b>above</b> this layer, where
    /// the identity provider and lease registry live. <see cref="AssetCommandEnvelope.IssuerId"/>
    /// is checked for presence here, never for authority; a validator that pretended to check
    /// authority it cannot see would be worse than one that visibly does not.
    /// </para>
    /// </remarks>
    /// <param name="envelope">The command as issued.</param>
    /// <param name="descriptor">Current descriptor for the target asset, or null when unknown.</param>
    /// <param name="state">Latest state for the target asset, or null when it has reported none.</param>
    /// <param name="nowUtc">Instant to judge the deadline against.</param>
    /// <returns>An accepted intent, or a rejection carrying a stable reason code.</returns>
    public static CommandValidationResult Validate(
        AssetCommandEnvelope envelope,
        AssetDescriptor? descriptor,
        AssetState? state,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!TryGet(envelope.Kind, out var definition))
        {
            var code = string.IsNullOrWhiteSpace(envelope.Kind)
                ? CommandRejectionReasons.KindMissing
                : CommandRejectionReasons.KindUnknown;
            return Reject(envelope, code, $"Command kind '{envelope.Kind}' is not recognised.", "kind");
        }

        if (ValidatePayload(envelope, definition) is { } payloadFailure)
        {
            return payloadFailure;
        }

        if (envelope.Deadline is { } deadline && deadline <= nowUtc)
        {
            return Reject(
                envelope, CommandRejectionReasons.DeadlineExpired,
                $"Deadline {deadline:O} had already passed at {nowUtc:O}.", "deadline");
        }

        if (descriptor is null)
        {
            return Reject(
                envelope, CommandRejectionReasons.AssetNotFound,
                $"No asset '{envelope.AssetId}' is registered.", "assetId");
        }

        if (state is null)
        {
            return Reject(
                envelope, CommandRejectionReasons.AssetStateUnavailable,
                $"Asset '{envelope.AssetId}' has reported no state to validate against.", "assetId");
        }

        if (!string.Equals(descriptor.AssetId, envelope.AssetId, StringComparison.Ordinal)
            || !string.Equals(state.AssetId, envelope.AssetId, StringComparison.Ordinal))
        {
            return Reject(
                envelope, CommandRejectionReasons.AssetIdMismatch,
                "Envelope, descriptor and state do not all name the same asset.", "assetId");
        }

        if (!definition.IsSatisfiedBy(descriptor.Capabilities))
        {
            return Reject(
                envelope, CommandRejectionReasons.CapabilityNotDeclared,
                $"Asset '{envelope.AssetId}' does not declare {definition.RequiredCapabilities} "
                + $"({definition.Match}), which '{definition.Kind}' requires.", "kind");
        }

        if (!definition.AppliesTo(descriptor.Domain))
        {
            return Reject(
                envelope, CommandRejectionReasons.DomainNotApplicable,
                $"'{definition.Kind}' does not apply to a {descriptor.Domain} asset.", "kind");
        }

        if (!definition.PermitsState(state.OperationalState))
        {
            return Reject(
                envelope, CommandRejectionReasons.StateNotPermitted,
                $"'{definition.Kind}' cannot be issued while the asset is {state.OperationalState}.", "kind");
        }

        if (definition.RequiresFreshPosition && state.Freshness != DataFreshness.Fresh)
        {
            return Reject(
                envelope, CommandRejectionReasons.PositionStale,
                $"'{definition.Kind}' needs a current position; the last report is {state.Freshness}.",
                "assetId");
        }

        return Translate(envelope, definition, descriptor);
    }

    private static CommandValidationResult Reject(
        AssetCommandEnvelope envelope, string reasonCode, string message, string? field = null) =>
        CommandValidationResult.Reject(envelope.CommandId, envelope.AssetId, reasonCode, message, field);

    // Gate 1: is the request structurally a command at all? Nothing here consults the asset,
    // so a malformed payload is rejected identically whether or not the asset exists.
    private static CommandValidationResult? ValidatePayload(
        AssetCommandEnvelope envelope, CommandDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(envelope.AssetId))
        {
            return Reject(envelope, CommandRejectionReasons.AssetIdMissing, "No asset was named.", "assetId");
        }

        if (string.IsNullOrWhiteSpace(envelope.IssuerId))
        {
            return Reject(envelope, CommandRejectionReasons.IssuerMissing, "No issuer was named.", "issuerId");
        }

        if (string.IsNullOrWhiteSpace(envelope.IdempotencyKey))
        {
            return Reject(
                envelope, CommandRejectionReasons.IdempotencyKeyMissing,
                "An idempotency key is required so a retry can be told from a repeat.", "idempotencyKey");
        }

        if (envelope.Frame is { } frame && !CoordinateFrames.IsSpecified(frame))
        {
            return Reject(
                envelope, CommandRejectionReasons.FrameUnspecified,
                $"Coordinate frame '{frame}' is not a declared frame.", "frame");
        }

        if (envelope.Constraints is { } constraints && !AreConstraintsUsable(constraints))
        {
            return Reject(
                envelope, CommandRejectionReasons.ConstraintsInvalid,
                "Motion constraints are non-physical or self-contradictory.", "constraints");
        }

        return ValidateTarget(envelope, definition) ?? ValidateParameters(envelope, definition);
    }

    private static CommandValidationResult? ValidateTarget(
        AssetCommandEnvelope envelope, CommandDefinition definition)
    {
        if (envelope.Target is not { } target)
        {
            return definition.RequiresTarget
                ? Reject(
                    envelope, CommandRejectionReasons.TargetMissing,
                    $"'{definition.Kind}' requires a target.", "target")
                : null;
        }

        if ((definition.AllowedTargets & target.Kind) == CommandTargetKinds.None)
        {
            return Reject(
                envelope, CommandRejectionReasons.TargetKindUnsupported,
                $"'{definition.Kind}' does not accept a {target.Kind} target.", "target");
        }

        // Reason tokens, not prose: the caller sees them inside the message, and a test can
        // match on them without string-matching English.
        var problem = target switch
        {
            PointCommandTarget p => CoordinateFrames.TryValidate(p.Point, out var poseError)
                ? RadiusProblem(p.AcceptanceRadiusM, "target.acceptanceRadius.invalid")
                : poseError,
            GeoCommandTarget g => GeoProblem(g),
            AssetCommandTarget a when string.IsNullOrWhiteSpace(a.AssetId) => "target.assetId.missing",
            AssetCommandTarget a => RadiusProblem(a.StandoffM, "target.standoff.invalid", allowZero: true),
            RouteCommandTarget r when string.IsNullOrWhiteSpace(r.RouteId) => "target.routeId.missing",
            RouteCommandTarget { StartWaypointIndex: < 0 } => "target.waypointIndex.invalid",
            RouteCommandTarget => null,
            _ => "target.kind.unknown",
        };

        return problem is null
            ? null
            : Reject(
                envelope, CommandRejectionReasons.TargetInvalid,
                $"Command target is not usable: {problem}.", "target");
    }

    private static CommandValidationResult? ValidateParameters(
        AssetCommandEnvelope envelope, CommandDefinition definition)
    {
        var parameters = envelope.Parameters;

        foreach (var key in definition.RequiredParameters)
        {
            if (parameters is null || !parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return Reject(
                    envelope, CommandRejectionReasons.ParameterMissing,
                    $"'{definition.Kind}' requires the '{key}' parameter.", $"parameters.{key}");
            }
        }

        if (parameters is null)
        {
            return null;
        }

        // A datum this simulation cannot convert from is refused even though the boundary has
        // already normalised the altitude: an envelope that names one is either from a client
        // that skipped the boundary or from one whose datum was silently dropped, and both are
        // bugs worth surfacing rather than executing.
        if (parameters.TryGetValue(CommandParameters.VerticalReference, out var datum)
            && !CommandVerticalReferences.TryParse(datum, out _))
        {
            return Reject(
                envelope, CommandContractReasons.VerticalReferenceUnsupported,
                $"Vertical reference '{datum}' is not one this simulation converts from; "
                + $"use {CommandVerticalReferences.SupportedNames}.",
                $"parameters.{CommandParameters.VerticalReference}");
        }

        // Unknown keys are ignored rather than rejected, so an older server keeps working
        // against a newer client that sends a parameter it has no use for yet.
        foreach (var key in NumericParameters)
        {
            if (parameters.TryGetValue(key, out var raw) && !TryParseFinite(raw, out _))
            {
                return Reject(
                    envelope, CommandRejectionReasons.ParameterInvalid,
                    $"Parameter '{key}' is not a finite number.", $"parameters.{key}");
            }
        }

        return null;
    }

    // The final step: every gate has passed, so the strings become typed values and the
    // numbers are checked against what this particular asset can physically do.
    private static CommandValidationResult Translate(
        AssetCommandEnvelope envelope, CommandDefinition definition, AssetDescriptor descriptor)
    {
        var parameters = envelope.Parameters;
        var speed = ReadDouble(parameters, CommandParameters.Speed);
        var altitude = ReadDouble(parameters, CommandParameters.Altitude);
        var course = ReadDouble(parameters, CommandParameters.Course);
        var steering = ReadDouble(parameters, CommandParameters.Steering);
        var radius = ReadDouble(parameters, CommandParameters.Radius);

        if (speed is { } commandedSpeed && IsSpeedOutOfRange(commandedSpeed, descriptor.Motion, envelope.Constraints))
        {
            return Reject(
                envelope, CommandRejectionReasons.ParameterOutOfRange,
                $"Speed {commandedSpeed} m/s is outside what '{descriptor.AssetId}' can hold "
                + $"({descriptor.Motion.MinSpeedMps}–{descriptor.Motion.MaxSpeedMps} m/s).",
                $"parameters.{CommandParameters.Speed}");
        }

        // Range-checked here for the same reason speed is: the executor casts it to a float and
        // substitutes it into the scene, where 1e300 becomes +Infinity, the asset's position
        // becomes NaN and the room's frame broadcast dies with it. By this point the altitude is
        // expressed against the scene datum — the API boundary converts a reference-qualified one
        // there, where the terrain under the asset is known.
        if (altitude is { } height
            && (height < MinCommandedAltitudeM || height > MaxCommandedAltitudeM))
        {
            return Reject(
                envelope, CommandContractReasons.AltitudeOutOfRange,
                $"Altitude {height} m is outside the scene's vertical envelope "
                + $"({MinCommandedAltitudeM}–{MaxCommandedAltitudeM} m).",
                $"parameters.{CommandParameters.Altitude}");
        }

        if (steering is { } angle && Math.Abs(angle) > MaxSteeringAngleRad)
        {
            return Reject(
                envelope, CommandRejectionReasons.ParameterOutOfRange,
                $"Steering angle {angle} rad exceeds +/-{MaxSteeringAngleRad} rad.",
                $"parameters.{CommandParameters.Steering}");
        }

        if (radius is { } metres && metres <= 0)
        {
            return Reject(
                envelope, CommandRejectionReasons.ParameterOutOfRange,
                $"Radius {metres} m must be positive.", $"parameters.{CommandParameters.Radius}");
        }

        return CommandValidationResult.Accept(new CommandIntent(
            envelope.CommandId,
            envelope.AssetId,
            descriptor.Domain,
            definition.Kind,
            definition.RequiredCapabilities,
            envelope.Target,
            envelope.Frame,
            envelope.Constraints,
            envelope.Deadline,
            speed,
            altitude,
            course is { } bearing ? NormaliseHeading(bearing) : null,
            steering,
            radius));
    }

    // A commanded zero is only reachable for an asset that can actually stop: a displacement
    // hull with a non-zero minimum speed and no station-keeping loses steerage way at zero.
    private static bool IsSpeedOutOfRange(double speed, MotionConstraints motion, MotionConstraints? overrides)
    {
        if (speed < 0)
        {
            return true;
        }

        var ceiling = overrides is null ? motion.MaxSpeedMps : Math.Min(motion.MaxSpeedMps, overrides.MaxSpeedMps);
        if (speed > ceiling)
        {
            return true;
        }

        return speed == 0
            ? motion.MinSpeedMps > 0 && !motion.CanStationKeep
            : speed < motion.MinSpeedMps;
    }

    private static string? GeoProblem(GeoCommandTarget target)
    {
        var p = target.Position;
        if (!double.IsFinite(p.LatitudeDeg) || p.LatitudeDeg is < -90 or > 90)
        {
            return "target.geo.latitude.outOfRange";
        }

        if (!double.IsFinite(p.LongitudeDeg) || p.LongitudeDeg is <= -180 or > 180)
        {
            return "target.geo.longitude.outOfRange";
        }

        if (!double.IsFinite(p.VerticalMeters))
        {
            return "target.geo.vertical.notFinite";
        }

        return p.VerticalReference == VerticalReference.Unknown
            ? "target.geo.verticalReference.unknown"
            : RadiusProblem(target.AcceptanceRadiusM, "target.acceptanceRadius.invalid");
    }

    private static string? RadiusProblem(double? value, string token, bool allowZero = false) =>
        value is { } v && (!double.IsFinite(v) || v < 0 || (v == 0 && !allowZero)) ? token : null;

    private static bool AreConstraintsUsable(MotionConstraints c) =>
        double.IsFinite(c.MinSpeedMps) && double.IsFinite(c.MaxSpeedMps)
        && double.IsFinite(c.MinTurnRadiusM) && double.IsFinite(c.PassiveDriftMps)
        && double.IsFinite(c.StationKeepCostW)
        && c.MinSpeedMps >= 0 && c.MaxSpeedMps > 0 && c.MinSpeedMps <= c.MaxSpeedMps
        && c.MinTurnRadiusM >= 0 && c.PassiveDriftMps >= 0 && c.StationKeepCostW >= 0;

    private static double? ReadDouble(IReadOnlyDictionary<string, string>? parameters, string key) =>
        parameters is not null && parameters.TryGetValue(key, out var raw) && TryParseFinite(raw, out var value)
            ? value
            : null;

    private static bool TryParseFinite(string raw, out double value) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    // Headings are clockwise from true north and wrap; normalising here means nothing
    // downstream has to guess whether -pi/2 and 3pi/2 are the same command. They are.
    private static double NormaliseHeading(double radians)
    {
        var wrapped = radians % Math.Tau;
        return wrapped < 0 ? wrapped + Math.Tau : wrapped;
    }
}
