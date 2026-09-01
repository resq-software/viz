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

/// <summary>Request body for issuing one command to one asset over the v2 REST surface.</summary>
/// <remarks>
/// The wire shape of <see cref="AssetCommandEnvelope"/> minus the fields the server is
/// authoritative for. <see cref="AssetCommandEnvelope.AssetId"/> comes from the route, so a
/// body that disagreed with the URL cannot be expressed; <see cref="AssetCommandEnvelope.IssuedAt"/>
/// is stamped on arrival, because a client clock is not evidence of when the server received
/// anything.
/// <para>
/// <paramref name="IdempotencyKey"/> is required rather than optional. Commands cross a
/// network, and a client that retries after a timeout must be able to say "this is the same
/// request" — without a key the server cannot tell a retry from a second, deliberate stop, and
/// the safe default (execute both) is the wrong one for anything that moves.
/// </para>
/// <para>
/// There is deliberately no bare <c>target: [x, y, z]</c>. <paramref name="Target"/> is the
/// closed <see cref="CommandTarget"/> union, whose point form carries a
/// <see cref="FramedPose"/> and therefore names its coordinate frame. A v2 boundary rejects
/// <see cref="CoordinateFrame.Unspecified"/> rather than assuming the scene frame.
/// </para>
/// </remarks>
/// <param name="Kind">Command kind; one of the tokens in <c>CommandKinds</c>, matched ordinally.</param>
/// <param name="IdempotencyKey">Issuer-chosen key identifying the logical request behind retries.</param>
/// <param name="IssuerId">
/// Identity of the operator or service issuing the command. Optional on the wire: this
/// deployment has no identity provider, so the server falls back to the session's own room
/// identity rather than pretending to have authenticated anyone.
/// </param>
/// <param name="CommandId">
/// Client-minted identifier for this attempt, or null to let the server mint one. Supplying it
/// lets a client correlate a response it never received with the command resource it can poll.
/// </param>
/// <param name="Deadline">Instant after which executing the command is pointless; null means no deadline.</param>
/// <param name="ControlLeaseId">Lease proving control authority, when the deployment uses leases.</param>
/// <param name="Frame">Frame positional parameters are expressed in. Never <see cref="CoordinateFrame.Unspecified"/>.</param>
/// <param name="Target">Where the command is aimed, or null for a command that needs no target.</param>
/// <param name="Constraints">Per-command motion limits overriding the asset's defaults, or null.</param>
/// <param name="Parameters">Command-specific scalars as invariant-culture strings, or null.</param>
public sealed record AssetCommandRequest(
    string Kind,
    string IdempotencyKey,
    string? IssuerId = null,
    Guid? CommandId = null,
    DateTimeOffset? Deadline = null,
    string? ControlLeaseId = null,
    CoordinateFrame? Frame = null,
    CommandTarget? Target = null,
    MotionConstraints? Constraints = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>Descriptors and states for the assets in one session.</summary>
/// <remarks>
/// The two lists are returned side by side rather than merged, matching
/// <see cref="VizSnapshotV2"/>: a client caches descriptors by
/// <see cref="AssetDescriptor.AssetId"/> and refreshes on a
/// <see cref="AssetDescriptor.Revision"/> increase, so merging them here would train callers
/// on a shape the streaming path does not use.
/// </remarks>
/// <param name="Descriptors">Descriptor for every asset in <paramref name="Assets"/>, in spawn order.</param>
/// <param name="Assets">State for every asset, in the same spawn order.</param>
/// <param name="Tick">Simulation tick the states were captured on.</param>
/// <param name="SimulationTimeSeconds">Simulated time the states were captured at, in seconds.</param>
public sealed record AssetInventoryResponse(
    IReadOnlyList<AssetDescriptor> Descriptors,
    IReadOnlyList<AssetState> Assets,
    long Tick,
    double SimulationTimeSeconds);

/// <summary>One asset's descriptor paired with its current state.</summary>
/// <param name="Descriptor">Metadata describing what the asset is.</param>
/// <param name="State">The asset's state as of <paramref name="Tick"/>.</param>
/// <param name="Tick">Simulation tick the state was captured on.</param>
public sealed record AssetDetailResponse(
    AssetDescriptor Descriptor,
    AssetState State,
    long Tick);

/// <summary>Result of placing one asset into a running session.</summary>
/// <param name="AssetId">Identifier the asset was registered under, minted by the server when the request left it null.</param>
/// <param name="Descriptor">The descriptor the asset was created with.</param>
public sealed record AssetSpawnResponse(
    string AssetId,
    AssetDescriptor Descriptor);

/// <summary>One command an asset is declared able to accept, and the shape it takes.</summary>
/// <remarks>
/// Projected from the command catalog against this asset's declared capabilities, so a client
/// renders exactly the affordances that will not be rejected. The alternative — a UI that
/// switches on vehicle class — reinvents the capability gate in a second place, where it drifts.
/// </remarks>
/// <param name="Kind">Wire token from <c>CommandKinds</c>.</param>
/// <param name="RequiredCapabilities">Capability names the command is gated on.</param>
/// <param name="CapabilityMatch">Whether all or any of the required capabilities must be declared.</param>
/// <param name="RequiresTarget">True when omitting the target is an error.</param>
/// <param name="AllowedTargetKinds">Target shapes this command accepts; empty when it takes none.</param>
/// <param name="RequiredParameters">Parameter keys that must be supplied.</param>
/// <param name="RequiresFreshPosition">True when a stale position report blocks the command.</param>
/// <param name="StatePolicy">Operational states the command may be issued in.</param>
public sealed record AssetCommandCapability(
    string Kind,
    IReadOnlyList<string> RequiredCapabilities,
    string CapabilityMatch,
    bool RequiresTarget,
    IReadOnlyList<string> AllowedTargetKinds,
    IReadOnlyList<string> RequiredParameters,
    bool RequiresFreshPosition,
    string StatePolicy);

/// <summary>What one asset declares it can do, and what data it publishes.</summary>
/// <remarks>
/// Both halves are derived, never stored: the command list comes from the catalog filtered by
/// the descriptor's capability mask and domain, and the data features come from inspecting the
/// asset's most recent state. That is what stops this endpoint drifting from the validator that
/// actually accepts or refuses a command.
/// </remarks>
/// <param name="AssetId">Asset these capabilities belong to.</param>
/// <param name="Domain">Medium the asset operates in.</param>
/// <param name="VehicleClass">Mobility archetype.</param>
/// <param name="Capabilities">Raw declared capability mask, for a client that wants to test bits.</param>
/// <param name="CapabilityNames">The same mask as names, for display and for logs.</param>
/// <param name="Motion">Speed, turn and station-keeping limits, so a task allocator can reject an impossible assignment.</param>
/// <param name="Commands">Commands this asset will accept, in catalog order.</param>
/// <param name="DataFeatures">
/// Tokens naming the optional data this asset actually publishes (e.g. <c>mission</c>,
/// <c>pose.geo</c>, <c>domain.surface</c>). Absent data is normal, and a client that renders a
/// panel for a field the asset never reports shows an empty box forever.
/// </param>
public sealed record AssetCapabilitiesResponse(
    string AssetId,
    AssetDomain Domain,
    VehicleClass VehicleClass,
    AssetCapability Capabilities,
    IReadOnlyList<string> CapabilityNames,
    MotionConstraints Motion,
    IReadOnlyList<AssetCommandCapability> Commands,
    IReadOnlyList<string> DataFeatures);

/// <summary>Stable machine-readable codes for failures on the v2 asset endpoints.</summary>
/// <remarks>
/// Deliberately separate from <see cref="CommandRejectionReasons"/>, which is about refusing a
/// command. These describe refusing to create, find or remove an asset, and they follow the
/// same convention: the code is the contract, the prose beside it is not.
/// </remarks>
public static class AssetProblems
{
    /// <summary>The request body was absent or could not be bound.</summary>
    public const string RequestInvalid = "spawn.requestInvalid";

    /// <summary>No default profile exists for the requested vehicle class.</summary>
    public const string VehicleClassUnsupported = "spawn.vehicleClassUnsupported";

    /// <summary>The spawn pose declared no coordinate frame, or a frame this boundary cannot resolve.</summary>
    public const string PoseFrameUnspecified = "spawn.poseFrameUnspecified";

    /// <summary>The spawn pose carried a non-finite or out-of-range coordinate.</summary>
    public const string PoseInvalid = "spawn.poseInvalid";

    /// <summary>The requested asset identifier is malformed.</summary>
    public const string AssetIdInvalid = "spawn.assetIdInvalid";

    /// <summary>The requested asset identifier is already in use in this session.</summary>
    public const string AssetIdTaken = "spawn.assetIdTaken";

    /// <summary>The session already holds as many assets, or as many drones, as it permits.</summary>
    public const string CapacityReached = "spawn.capacityReached";

    /// <summary>
    /// A supplied descriptor field cannot yet be applied to an asset of this domain, and was
    /// refused rather than silently dropped.
    /// </summary>
    public const string FieldNotSupported = "spawn.fieldNotSupported";

    /// <summary>No factory is registered that can build the requested vehicle class.</summary>
    public const string MobilityModelUnavailable = "spawn.mobilityModelUnavailable";

    /// <summary>No asset with the requested identifier exists in this session.</summary>
    public const string AssetNotFound = "asset.notFound";

    /// <summary>The asset exists but cannot be removed; air assets belong to the flight world.</summary>
    public const string AssetNotRemovable = "asset.notRemovable";

    /// <summary>No command with the requested identifier is known to this session.</summary>
    public const string CommandNotFound = "command.notFound";

    /// <summary>The asset refused the translated command after validation had accepted it.</summary>
    public const string CommandNotExecutable = "command.notExecutable";
}
