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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Controllers;

// The asset endpoints of the v2 surface: listing, spawning, fetching, removing, and reporting
// what an asset is declared able to do. Split from the command endpoints because the two answer
// different questions — "what exists" versus "what may I ask it to do" — and reviewing a change
// to one should not mean paging through the other. The type's summary, its route attributes and
// its limits live on the primary declaration in SimV2Controller.cs.
public sealed partial class SimV2Controller
{
    /// <summary>Lists every asset in the session: descriptors and current states.</summary>
    /// <param name="domain">Optional domain filter. <see cref="AssetDomain.Unspecified"/> is refused.</param>
    /// <returns>Descriptors and states in spawn order, with the tick they were captured on.</returns>
    [HttpGet("assets")]
    public IActionResult GetAssets([FromQuery] AssetDomain? domain = null)
    {
        if (domain is { } filter && (!Enum.IsDefined(filter) || filter == AssetDomain.Unspecified))
        {
            return Failure(
                StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
                $"'{filter}' is not a filterable asset domain.", field: "domain");
        }

        var frame = Room.CaptureAssetFrame();
        var descriptors = frame.Descriptors;
        var states = frame.Assets;

        if (domain is { } wanted)
        {
            var keep = descriptors.Where(d => d.Domain == wanted).ToList();
            var ids = keep.Select(d => d.AssetId).ToHashSet(StringComparer.Ordinal);
            descriptors = keep;
            states = states.Where(s => ids.Contains(s.AssetId)).ToList();
        }

        return Ok(new AssetInventoryResponse(
            descriptors, states, frame.Transport.Tick, frame.SimulationTimeSeconds));
    }

    /// <summary>Places one asset of a declared vehicle class into the running session.</summary>
    /// <remarks>
    /// The domain is derived from <see cref="AssetSpawnRequest.VehicleClass"/> through
    /// <see cref="AssetProfiles.DomainFor"/> rather than taken from the request, so a caller
    /// cannot declare a domain that contradicts the class it asked for. Capabilities, envelope
    /// and motion limits come from the same profile table, which is what stops a spawn producing
    /// a rover that declares <see cref="AssetCapability.Takeoff"/>.
    /// <para>
    /// Which domains actually succeed is a fact about the deployment, not about this method. An
    /// air spawn goes to the SDK's flight world; everything else needs a registered
    /// <see cref="IAssetFactory"/> that answers for the class, and gets
    /// <see cref="AssetProblems.MobilityModelUnavailable"/> when none does. Ground and surface
    /// models are both registered, so a rover and a vessel both spawn — and enabling surface was
    /// exactly that registration, with not one line of this endpoint changed, which is the
    /// property the mechanism exists for. The reserved subsurface classes still have no motion
    /// model and are still refused the same way.
    /// </para>
    /// </remarks>
    /// <param name="request">Vehicle class, frame-qualified spawn pose and optional metadata.</param>
    /// <returns>201 with the minted identifier and descriptor, or a problem describing the refusal.</returns>
    [HttpPost("assets")]
    [EnableRateLimiting("destructive")]
    public IActionResult SpawnAsset([FromBody] AssetSpawnRequest? request)
    {
        if (request is null)
        {
            return Failure(
                StatusCodes.Status400BadRequest, AssetProblems.RequestInvalid,
                "A spawn request body is required.");
        }

        if (!Enum.IsDefined(request.VehicleClass) || !AssetProfiles.IsSupported(request.VehicleClass))
        {
            return Failure(
                StatusCodes.Status400BadRequest, AssetProblems.VehicleClassUnsupported,
                $"Vehicle class '{request.VehicleClass}' has no simulation profile.",
                field: "vehicleClass");
        }

        if (!TryResolveSpawnPose(request.Pose, out var positionEus, out var headingRad, out var poseFailure))
        {
            return poseFailure;
        }

        if (!TryResolveAssetId(request.AssetId, request.VehicleClass, out var assetId, out var idFailure))
        {
            return idFailure;
        }

        var room = Room;
        var domain = AssetProfiles.DomainFor(request.VehicleClass);
        var (assetCount, droneCount) = room.UseAssets(world => (world.AssetCount, world.DroneCount));

        if (assetCount >= MaxAssetCount)
        {
            return Failure(
                StatusCodes.Status429TooManyRequests, AssetProblems.CapacityReached,
                $"Maximum asset count ({MaxAssetCount}) reached.", assetId);
        }

        if (domain == AssetDomain.Air && droneCount >= MaxDroneCount)
        {
            return Failure(
                StatusCodes.Status429TooManyRequests, AssetProblems.CapacityReached,
                $"Maximum drone count ({MaxDroneCount}) reached.", assetId);
        }

        return domain == AssetDomain.Air
            ? SpawnAirAsset(room, request, assetId, positionEus)
            : SpawnNonAirAsset(room, request, assetId, positionEus, headingRad);
    }

    /// <summary>Returns one asset's descriptor and current state.</summary>
    /// <param name="id">Asset identifier.</param>
    /// <returns>The descriptor and state, or 404 when the session holds no such asset.</returns>
    [HttpGet("assets/{id}")]
    public IActionResult GetAsset(string id)
    {
        var frame = Room.CaptureAssetFrame();
        var descriptor = frame.Descriptors.FirstOrDefault(d => d.AssetId == id);
        var state = frame.Assets.FirstOrDefault(s => s.AssetId == id);

        return descriptor is null || state is null
            ? Failure(
                StatusCodes.Status404NotFound, AssetProblems.AssetNotFound,
                $"No asset '{Sanitize(id)}' exists in this session.", id)
            : Ok(new AssetDetailResponse(descriptor, state, frame.Transport.Tick));
    }

    /// <summary>Removes a ground or surface asset from the session.</summary>
    /// <remarks>
    /// Air assets are refused with 409 rather than quietly ignored: the flight world owns their
    /// lifetime, and telling an operator a drone is gone while it keeps flying is worse than
    /// telling them it cannot be removed.
    /// </remarks>
    /// <param name="id">Asset identifier.</param>
    /// <returns>204 on removal, 404 when unknown, 409 when the asset cannot be removed.</returns>
    [HttpDelete("assets/{id}")]
    [EnableRateLimiting("destructive")]
    public IActionResult RemoveAsset(string id)
    {
        if (Room.TryRemoveAsset(id, out var reasonCode))
        {
            _logger.LogInformation(
                "[room {RoomId}] Asset {AssetId} removed (trace {TraceId}).",
                Room.Id, Sanitize(id), TraceId);
            return NoContent();
        }

        return reasonCode == AssetProblems.AssetNotFound
            ? Failure(
                StatusCodes.Status404NotFound, reasonCode,
                $"No asset '{Sanitize(id)}' exists in this session.", id)
            : Failure(
                StatusCodes.Status409Conflict, reasonCode,
                $"Asset '{Sanitize(id)}' belongs to the flight world and cannot be removed; reset the session instead.",
                id);
    }

    /// <summary>Reports what an asset is declared able to do, and what data it publishes.</summary>
    /// <remarks>
    /// Both halves are derived — the command list from the catalog filtered by this asset's
    /// declared capabilities and domain, the data features from its latest state — so a client
    /// that renders exactly these affordances issues exactly the commands the validator accepts.
    /// </remarks>
    /// <param name="id">Asset identifier.</param>
    /// <returns>The capability report, or 404 when the session holds no such asset.</returns>
    [HttpGet("assets/{id}/capabilities")]
    public IActionResult GetAssetCapabilities(string id)
    {
        var frame = Room.CaptureAssetFrame();
        var descriptor = frame.Descriptors.FirstOrDefault(d => d.AssetId == id);
        var state = frame.Assets.FirstOrDefault(s => s.AssetId == id);

        if (descriptor is null || state is null)
        {
            return Failure(
                StatusCodes.Status404NotFound, AssetProblems.AssetNotFound,
                $"No asset '{Sanitize(id)}' exists in this session.", id);
        }

        var anchored = TryResolveLocalOrigin(out var origin);

        var commands = CommandCatalog.All
            .Where(d => d.AppliesTo(descriptor.Domain) && d.IsSatisfiedBy(descriptor.Capabilities))
            .Select(d => new AssetCommandCapability(
                Kind: d.Kind,
                RequiredCapabilities: CapabilityNames(d.RequiredCapabilities),
                CapabilityMatch: d.Match.ToString(),
                RequiresTarget: d.RequiresTarget,
                AllowedTargetKinds: TargetKindNames(AdvertisedTargets(d.AllowedTargets, anchored)),
                RequiredParameters: d.RequiredParameters,
                RequiresFreshPosition: d.RequiresFreshPosition,
                StatePolicy: d.StatePolicy.ToString()))
            .ToList();

        var features = DataFeatures(state).ToList();
        if (anchored && origin is not null)
        {
            // The scene's tie to the globe is environment metadata a client needs before it
            // offers geodetic entry at all, and the origin id is what makes two local positions
            // comparable. Named here, not merely implied by the geo target shape appearing.
            features.Add($"frame.localOrigin:{origin.OriginId}");
        }

        return Ok(new AssetCapabilitiesResponse(
            AssetId: descriptor.AssetId,
            Domain: descriptor.Domain,
            VehicleClass: descriptor.VehicleClass,
            Capabilities: descriptor.Capabilities,
            CapabilityNames: CapabilityNames(descriptor.Capabilities),
            Motion: descriptor.Motion,
            Commands: commands,
            DataFeatures: features));
    }

    /// <summary>Drops target shapes this deployment cannot resolve from an advertisement.</summary>
    /// <remarks>
    /// A capability report is a promise, so it must not name a shape the very next request would
    /// be refused for. A geodetic target needs a configured <see cref="LocalOrigin"/>; without
    /// one the scene is not anchored to the globe and every geodetic command is refused with a
    /// configuration-class code. Withholding the shape is what stops a client that renders
    /// exactly these affordances from offering an entry field that cannot work.
    /// </remarks>
    /// <param name="allowed">Shapes the catalog declares for the command.</param>
    /// <param name="anchored">Whether this deployment has a usable local origin.</param>
    /// <returns>The shapes this deployment will actually accept.</returns>
    private static CommandTargetKinds AdvertisedTargets(CommandTargetKinds allowed, bool anchored) =>
        anchored ? allowed : allowed & ~CommandTargetKinds.Geo;
}
