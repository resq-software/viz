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
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

namespace ResQ.Viz.Web.Controllers;

// Where a request's coordinate frames and vertical datums are resolved: the scene's tie to the
// globe, geodetic targets projected onto it, and commanded altitudes rewritten onto its vertical
// axis. Split from SimV2Controller.Commands.cs because this is the only code in the controller
// that needs the environment — a configured origin and the terrain under an asset — and because
// a frame or datum error is the failure this whole surface exists to prevent, so it should be
// reviewable on its own rather than buried among length and idempotency checks.
//
// Everything here resolves BEFORE validation runs, for the same reason a point target in NED is
// converted first: the idempotency hash must be taken over a normalised request, so two spellings
// of one destination or one altitude are recognised as the same command rather than two.
public sealed partial class SimV2Controller
{
    /// <summary>
    /// Rewrites a commanded altitude onto the scene's vertical axis, refusing one whose datum is
    /// missing or unconvertible.
    /// </summary>
    /// <remarks>
    /// This is the only layer that can do the conversion: it needs the terrain elevation under
    /// the asset, which the pure validator has no access to and the executor deliberately does
    /// not sample for itself. Doing it here also means the idempotency hash is taken over the
    /// converted request, so "30 m above ground" and the mean-sea-level altitude it works out to
    /// are recognised as the same command rather than two.
    /// <para>
    /// Applied to <em>any</em> command carrying an altitude, not just <c>setAltitude</c>: a
    /// <c>takeoff</c> altitude reaches the same cast through the same field, and a rule that
    /// covered one and not the other would leave exactly one datum-ambiguous path open.
    /// </para>
    /// <para>
    /// An altitude that does not parse is left alone rather than refused here, so the validator
    /// reports <c>payload.parameterInvalid</c> against the field that is actually wrong instead
    /// of this layer complaining about a missing datum for a number that was never a number.
    /// </para>
    /// </remarks>
    /// <param name="assetId">Asset the command is aimed at; its position fixes the terrain sample.</param>
    /// <param name="parameters">Parameter bag as issued.</param>
    /// <param name="normalised">The bag to carry forward: rewritten when an altitude was converted.</param>
    /// <param name="failure">The response to send when the datum is missing or unconvertible.</param>
    /// <returns><see langword="true"/> when the command may proceed.</returns>
    private bool TryNormaliseAltitude(
        string assetId,
        IReadOnlyDictionary<string, string>? parameters,
        out IReadOnlyDictionary<string, string>? normalised,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        normalised = parameters;
        failure = null;

        if (parameters is null
            || !parameters.TryGetValue(CommandParameters.Altitude, out var rawAltitude)
            || !double.TryParse(
                rawAltitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var altitude)
            || !double.IsFinite(altitude))
        {
            return true;
        }

        parameters.TryGetValue(CommandParameters.VerticalReference, out var rawReference);

        if (string.IsNullOrWhiteSpace(rawReference))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandContractReasons.VerticalReferenceMissing,
                $"An altitude must name the datum it is measured against ({CommandVerticalReferences.SupportedNames}); "
                + "this asset publishes three altitudes at once and they differ by the terrain height.",
                assetId, field: $"parameters.{CommandParameters.VerticalReference}");
            return false;
        }

        if (!CommandVerticalReferences.TryParse(rawReference, out var reference))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest,
                CommandContractReasons.VerticalReferenceUnsupported,
                $"Vertical reference '{Sanitize(rawReference)}' is not one this simulation converts from; "
                + $"use {CommandVerticalReferences.SupportedNames}.",
                assetId, field: $"parameters.{CommandParameters.VerticalReference}");
            return false;
        }

        var sceneAltitude = CommandVerticalReferences.ToSceneAltitudeM(
            altitude, reference, TerrainElevationUnder(assetId));

        // "R" round-trips exactly, so the number the validator range-checks and the number the
        // executor flies are bit-for-bit the one computed here.
        normalised = new Dictionary<string, string>(parameters, StringComparer.Ordinal)
        {
            [CommandParameters.Altitude] = sceneAltitude.ToString("R", CultureInfo.InvariantCulture),
            [CommandParameters.VerticalReference] = nameof(VerticalReference.MeanSeaLevel),
        };

        return true;
    }

    /// <summary>Samples the terrain elevation under an asset, in scene-frame metres.</summary>
    /// <remarks>
    /// Zero for an asset this session does not hold. The command is about to be refused as
    /// <c>asset.notFound</c> by the validator, and returning a fabricated elevation for a vehicle
    /// that does not exist would only change which gate reports it.
    /// </remarks>
    /// <param name="assetId">Asset to sample under.</param>
    /// <returns>Terrain elevation in metres, or zero when the asset is unknown.</returns>
    private double TerrainElevationUnder(string assetId) =>
        Room.UseAssets(world =>
            world.TryGet(assetId, out var asset) && asset is not null
                ? world.Environment.GetElevation(asset.PositionEus.X, asset.PositionEus.Z)
                : 0.0);

    /// <summary>Projects a geodetic target onto the scene, or explains why it cannot be.</summary>
    /// <remarks>
    /// A structurally impossible position — a latitude of 800 degrees, an undeclared datum — is
    /// passed through untouched so the catalog refuses it with the token it already uses for
    /// every command. Only a position that could be projected is resolved here, which keeps this
    /// layer's failures about the <em>scene</em> and the catalog's about the <em>payload</em>.
    /// <para>
    /// An unanchored deployment answers 501 rather than 400 or 409. The request is well formed
    /// and the world is fine; this build simply has no origin tying its scene to the globe, which
    /// is a property of the deployment. The capability report withholds the geodetic shape on
    /// such a build, so a client that reads it never gets here.
    /// </para>
    /// </remarks>
    /// <param name="geo">Geodetic target as issued.</param>
    /// <param name="assetId">Asset the command is aimed at, for the problem body.</param>
    /// <param name="normalised">The scene-frame point target, or the untouched geodetic one.</param>
    /// <param name="failure">The response to send when the target cannot be resolved.</param>
    /// <returns><see langword="true"/> when the command may proceed.</returns>
    private bool TryResolveGeoTarget(
        GeoCommandTarget geo,
        string assetId,
        out CommandTarget? normalised,
        [NotNullWhen(false)] out ObjectResult? failure)
    {
        normalised = geo;
        failure = null;

        if (!IsProjectableGeo(geo))
        {
            return true;
        }

        if (!TryResolveLocalOrigin(out var origin) || origin is null)
        {
            failure = Failure(
                StatusCodes.Status501NotImplemented,
                CommandContractReasons.LocalOriginNotConfigured,
                "This deployment has no local origin configured, so its scene is not anchored to "
                + "the globe and a geodetic target cannot be resolved. Configure "
                + "Simulation:LocalOrigin, or send a scene-frame point target.",
                assetId, field: "target");
            return false;
        }

        if (!CommandGeoTargets.TryResolve(geo, origin, out var point, out var problem))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.TargetInvalid,
                $"The geodetic target cannot be resolved against origin '{origin.OriginId}': {problem}.",
                assetId, field: "target.position");
            return false;
        }

        if (!IsWithinWorld(point.Point.Position))
        {
            failure = Failure(
                StatusCodes.Status400BadRequest, CommandRejectionReasons.TargetInvalid,
                $"The geodetic target projects {MaxCoordinateM:N0} m or more from the scene origin.",
                assetId, field: "target.position");
            return false;
        }

        normalised = point;
        return true;
    }

    /// <summary>Whether a geodetic position is well formed enough to be worth projecting.</summary>
    /// <remarks>
    /// Deliberately the same conditions the catalog checks, so a position that fails here is
    /// certain to be refused there with a stable token rather than falling between the two.
    /// </remarks>
    private static bool IsProjectableGeo(GeoCommandTarget geo) =>
        // A body carrying "position": null binds the record's non-nullable field to null, so the
        // pattern is a real guard rather than a redundant one — without it a malformed request
        // would be answered with a 500 instead of the target-invalid the catalog is about to give.
        geo.Position is { } position
        && double.IsFinite(position.LatitudeDeg) && position.LatitudeDeg is >= -90.0 and <= 90.0
        && double.IsFinite(position.LongitudeDeg)
        && position.LongitudeDeg is > -180.0 and <= 180.0
        && double.IsFinite(position.VerticalMeters)
        && position.VerticalReference != VerticalReference.Unknown;

    /// <summary>Resolves the local origin the scene frame is anchored to, if one is configured.</summary>
    /// <remarks>
    /// Read from configuration rather than held on the room, because anchoring the scene to the
    /// globe is a deployment decision and not session state: two rooms on one host render the
    /// same terrain in the same place. Configuration comes from the request's service provider so
    /// this stays usable in a unit test that supplies its own.
    /// <para>
    /// An origin that is present but unusable — a non-finite number, a latitude past the pole
    /// limit where a tangent plane stops meaning anything, or a datum this build cannot convert —
    /// is treated as <em>absent</em>. Anchoring on a misconfigured origin would put every
    /// geodetic command somewhere real and wrong, which is worse than refusing them.
    /// </para>
    /// </remarks>
    /// <param name="origin">The configured origin on success, otherwise null.</param>
    /// <returns><see langword="true"/> when the scene is anchored.</returns>
    private bool TryResolveLocalOrigin(out LocalOrigin? origin)
    {
        origin = ReadLocalOrigin(HttpContext.RequestServices?.GetService<IConfiguration>());
        return origin is not null;
    }

    /// <summary>Binds and validates the <c>Simulation:LocalOrigin</c> section.</summary>
    /// <param name="configuration">Configuration root, or null when none is available.</param>
    /// <returns>A usable origin, or null when it is unconfigured or unusable.</returns>
    private static LocalOrigin? ReadLocalOrigin(IConfiguration? configuration)
    {
        var section = configuration?.GetSection("Simulation:LocalOrigin");
        if (section is null || !section.Exists())
        {
            return null;
        }

        if (section.GetValue<double?>("LatitudeDeg") is not { } latitude
            || section.GetValue<double?>("LongitudeDeg") is not { } longitude
            || !CommandVerticalReferences.TryParse(
                section.GetValue<string?>("VerticalReference"), out var reference))
        {
            return null;
        }

        // An unnamed origin is refused rather than defaulted: the id is what makes two local
        // positions comparable, and a scene anchored to an anonymous origin cannot be told apart
        // from one anchored somewhere else entirely.
        var originId = section.GetValue<string?>("OriginId");
        if (string.IsNullOrWhiteSpace(originId))
        {
            return null;
        }

        var vertical = section.GetValue("VerticalMeters", 0.0);
        var yaw = section.GetValue("YawRad", 0.0);

        var usable =
            double.IsFinite(latitude) && double.IsFinite(longitude) && double.IsFinite(vertical)
            && double.IsFinite(yaw)
            && Math.Abs(latitude) <= CoordinateFrames.MaxOriginLatitudeDeg
            && longitude is > -180.0 and <= 180.0;

        return usable
            ? new LocalOrigin(originId, latitude, longitude, vertical, reference, yaw)
            : null;
    }
}
