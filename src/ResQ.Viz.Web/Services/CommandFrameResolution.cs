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
using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

// Resolving what a command MEANS before deciding whether it may run: the datum an altitude is
// quoted against, the origin a geodetic target is projected through, and the stable codes for
// refusing either. Split from the catalog and the translator because those two answer "may this
// asset be told this?" while everything here answers "where and against what surface is this?" —
// and because a frame or datum error is the failure the whole v2 boundary exists to prevent, so
// it should be reviewable on its own.

/// <summary>
/// Rejection codes raised by the catalog's parameter translation and by
/// <see cref="AssetCommandTranslator"/>, extending <see cref="CommandRejectionReasons"/>.
/// </summary>
/// <remarks>
/// Same convention as its companion: the code is the contract, the prose beside it is not, and
/// the prefix names the <em>class</em> of failure. That prefix is load-bearing — the HTTP layer
/// maps <c>payload.</c> to 400 and everything else to a state or configuration status — so a
/// server-side misconfiguration must never borrow a <c>payload.</c> code and blame the caller
/// for it.
/// </remarks>
public static class CommandContractReasons
{
    /// <summary>A commanded altitude parsed but lies outside the scene's vertical envelope.</summary>
    /// <remarks>
    /// Distinct from <see cref="CommandRejectionReasons.ParameterOutOfRange"/> so an operator
    /// interface can tell "outside the scene" from "faster than this vehicle goes" without
    /// reading prose.
    /// </remarks>
    public const string AltitudeOutOfRange = "payload.altitudeOutOfRange";

    /// <summary>An altitude was supplied without naming the datum it is measured against.</summary>
    public const string VerticalReferenceMissing = "payload.verticalReferenceMissing";

    /// <summary>The named vertical datum is not one this simulation can convert from.</summary>
    public const string VerticalReferenceUnsupported = "payload.verticalReferenceUnsupported";

    /// <summary>
    /// A geodetic target was supplied but no <see cref="LocalOrigin"/> is configured, so the
    /// scene is not anchored to the globe and the point cannot be resolved.
    /// </summary>
    /// <remarks>
    /// A <c>configuration.</c> code, deliberately not a <c>payload.</c> one: the request is well
    /// formed and the deployment is incomplete. Blaming the caller for that sends an operator
    /// hunting for a typo in a correct command. The capability report withholds
    /// <see cref="CommandTargetKinds.Geo"/> on such a deployment, so a conforming client never
    /// reaches this.
    /// </remarks>
    public const string LocalOriginNotConfigured = "configuration.localOriginMissing";

    /// <summary>
    /// The target's shape is well formed and advertised, but this simulation has no way to
    /// resolve it to a position — an asset-referenced or route-referenced target.
    /// </summary>
    /// <remarks>
    /// Not <see cref="CommandRejectionReasons.TargetKindUnsupported"/>, which means "this command
    /// does not accept that shape" and is a caller error answered with 400. This one means "this
    /// build cannot resolve that shape", which is a limitation of the server and is answered as a
    /// conflict with what the session can currently do.
    /// </remarks>
    public const string TargetNotResolvable = "target.notResolvable";

    /// <summary>A validated kind reached translation with no executable counterpart.</summary>
    /// <remarks>
    /// Unreachable while the catalog and <see cref="AssetCommandTranslator.ToAssetCommandKind"/>
    /// agree, which a test pins. It exists so a half-registered command fails against the server
    /// rather than being reported to the caller as a malformed payload.
    /// <para>
    /// Deliberately not <c>command.notExecutable</c>, which
    /// <see cref="AssetProblems.CommandNotExecutable"/> already owns for "the asset refused this".
    /// Two failures sharing one token would make the code unusable for telling them apart, which
    /// is the only thing a machine-readable code is for.
    /// </para>
    /// </remarks>
    public const string KindNotExecutable = "command.kindNotExecutable";
}
/// <summary>Parses and applies the vertical datum a commanded altitude is quoted against.</summary>
/// <remarks>
/// Pure and free of HTTP, so the datum arithmetic that decides whether a drone clears a ridge or
/// flies into it is testable with literals. Only the three references an operator actually
/// commands against are convertible; the rest are refused rather than approximated, because a
/// wrong conversion between ellipsoidal, water-surface and chart datums is indistinguishable from
/// a right one until something hits terrain.
/// </remarks>
public static class CommandVerticalReferences
{
    /// <summary>Datums <see cref="ToSceneAltitudeM"/> can convert from, in wire form.</summary>
    /// <remarks>Rendered into rejection prose so an operator is told what to send instead.</remarks>
    public const string SupportedNames = "meanSeaLevel, aboveGround or terrain";

    /// <summary>Parses a wire token into a datum this simulation converts from.</summary>
    /// <remarks>
    /// Case-insensitive on member names only. <see cref="VerticalReference.Unknown"/> is refused
    /// like any other unsupported value: "not declared" is precisely the state this parameter
    /// exists to eliminate, so accepting it would reintroduce the ambiguity.
    /// </remarks>
    /// <param name="token">Wire value of <see cref="CommandParameters.VerticalReference"/>.</param>
    /// <param name="reference">The parsed datum on success.</param>
    /// <returns><see langword="true"/> when the token names a convertible datum.</returns>
    public static bool TryParse(string? token, out VerticalReference reference)
    {
        reference = VerticalReference.Unknown;

        // Numeric tokens are refused deliberately: the enum's numbering is an implementation
        // detail, and a client that sent "3" would silently follow it if a member were inserted.
        var trimmed = token?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !char.IsAsciiLetter(trimmed[0])
            || !Enum.TryParse(trimmed, ignoreCase: true, out VerticalReference parsed))
        {
            return false;
        }

        reference = parsed;
        return IsSupported(parsed);
    }

    /// <summary>Whether a datum can be converted to the scene's vertical axis.</summary>
    /// <param name="reference">Datum to test.</param>
    /// <returns><see langword="true"/> for mean sea level, above ground and terrain.</returns>
    public static bool IsSupported(VerticalReference reference) =>
        reference is VerticalReference.MeanSeaLevel or VerticalReference.AboveGround
            or VerticalReference.Terrain;

    /// <summary>Converts an altitude onto the scene's vertical axis.</summary>
    /// <remarks>
    /// The scene's <c>Y</c> datum is mean sea level — it is the datum terrain elevations are
    /// quoted against, and the one an air asset publishes <c>AltitudeMslM</c> in — so a
    /// mean-sea-level altitude passes through unchanged.
    /// <para>
    /// <see cref="VerticalReference.AboveGround"/> and <see cref="VerticalReference.Terrain"/>
    /// coincide here and both add the terrain elevation under the asset. They stay distinct
    /// values because their <em>sources</em> differ — a radar altimeter versus this simulation's
    /// own surface model — and a scene carrying a surveyed surface would convert them apart.
    /// </para>
    /// </remarks>
    /// <param name="altitudeM">Commanded altitude in metres, positive up.</param>
    /// <param name="reference">Datum the altitude is quoted against; must be supported.</param>
    /// <param name="terrainElevationM">Scene-frame terrain elevation under the asset, in metres.</param>
    /// <returns>The equivalent scene-frame <c>Y</c>, in metres.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reference"/> is not convertible.</exception>
    public static double ToSceneAltitudeM(
        double altitudeM, VerticalReference reference, double terrainElevationM) => reference switch
        {
            VerticalReference.MeanSeaLevel => altitudeM,
            VerticalReference.AboveGround or VerticalReference.Terrain => altitudeM + terrainElevationM,
            _ => throw new ArgumentOutOfRangeException(
                nameof(reference), reference,
                $"Only {SupportedNames} convert onto the scene's vertical axis."),
        };
}

/// <summary>Resolves geodetic command targets against the scene's <see cref="LocalOrigin"/>.</summary>
/// <remarks>
/// Separate from <see cref="AssetCommandTranslator"/> because resolution needs the origin, which
/// is deployment configuration rather than anything the translation layer can see, and separate
/// from the controller because the arithmetic must be testable without an HTTP context. The
/// projection itself is <see cref="CoordinateFrames.GeoToLocalEus"/>; nothing here re-derives it.
/// </remarks>
public static class CommandGeoTargets
{
    /// <summary>Re-expresses a geodetic target as a scene-frame point target.</summary>
    /// <remarks>
    /// The resolved point keeps the geodetic position it came from, so a client that issued a
    /// chart position sees its own numbers echoed back instead of having to reverse the
    /// projection. Orientation is identity: a geodetic target carries no heading, and inventing
    /// one would be a heading the operator never asked for.
    /// <para>
    /// Datums must match. A geodetic vertical and the origin's vertical are only subtractable
    /// when they are measured from the same surface, and quietly differencing an above-ground
    /// height against a mean-sea-level origin is exactly the error this model exists to prevent.
    /// </para>
    /// </remarks>
    /// <param name="target">Geodetic target from the envelope.</param>
    /// <param name="origin">Configured origin the scene frame is anchored to.</param>
    /// <param name="resolved">The equivalent scene-frame point target on success.</param>
    /// <param name="problem">A stable token naming what was wrong, on failure.</param>
    /// <returns><see langword="true"/> when the target resolved.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static bool TryResolve(
        GeoCommandTarget target,
        LocalOrigin origin,
        [NotNullWhen(true)] out PointCommandTarget? resolved,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(origin);

        resolved = null;
        var position = target.Position;

        if (position.VerticalReference != origin.VerticalReference)
        {
            problem = "target.geo.verticalReference.mismatch";
            return false;
        }

        Vector3 positionEus;
        try
        {
            positionEus = CoordinateFrames.GeoToLocalEus(position, origin);
        }
        catch (ArgumentException)
        {
            // Raised for a non-finite component, or an origin too close to a pole for a tangent
            // plane to mean anything. Both are refusals rather than faults, so they travel back
            // as a reason code instead of a 500.
            problem = "target.geo.notProjectable";
            return false;
        }

        resolved = new PointCommandTarget(
            new FramedPose(
                Frame: CoordinateFrame.LocalEus,
                OriginId: origin.OriginId,
                Position: positionEus,
                Orientation: Quaternion.Identity,
                Covariance: null,
                Geo: position),
            target.AcceptanceRadiusM);

        problem = null;
        return true;
    }
}
