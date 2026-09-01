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

/// <summary>Authoritative transport state of the simulation loop: paused, speed and tick.</summary>
/// <remarks>
/// v1 carries these same three values as loose optional parameters on <see cref="VizFrame"/>.
/// They are grouped here because they are one atomic reading — the server samples all three
/// under a single lock, and a client that pairs a fresh tick with a stale paused flag draws a
/// transport bar that contradicts the server it is watching. Grouping also lets a later delta
/// frame replace or omit the whole triple instead of three independently-versioned fields.
/// </remarks>
/// <param name="Paused">True while the simulation loop is not advancing.</param>
/// <param name="Speed">Speed multiplier currently in effect; 1 is real time.</param>
/// <param name="Tick">
/// Monotonic simulation tick counter. Lets a client tell a genuinely new frame from a repeat
/// of the same one while paused or single-stepping, without diffing the whole payload.
/// </param>
public sealed record TransportState(
    bool Paused,
    int Speed,
    long Tick);

// ── Detections and hazards ─────────────────────────────────────────────────────

/// <summary>Something an asset's sensors found.</summary>
/// <remarks>
/// The reporting field is <paramref name="SourceAssetId"/>, not <c>DroneId</c> as in
/// <see cref="DetectionVizState"/>: any domain detects. A rover finds a casualty its camera
/// can see from the road and a vessel finds one in the water, and naming the field after one
/// domain is exactly how the rest of the system quietly grows an air-only assumption.
/// </remarks>
/// <param name="DetectionId">Stable identifier for the detected object or event.</param>
/// <param name="Type">What was detected (e.g. "survivor", "fire", "debris"). Used for filtering and iconography.</param>
/// <param name="Pose">
/// Frame-qualified position of the detection. Orientation is the identity rotation unless the
/// sensor genuinely resolved one — a detection is a point, not an oriented body.
/// </param>
/// <param name="SourceAssetId">
/// <see cref="AssetDescriptor.AssetId"/> of the asset that reported it, in any domain.
/// </param>
/// <param name="Confidence">
/// Detector confidence as a fraction in <c>[0, 1]</c>, where 0 is no confidence and 1 is
/// certainty. Producers clamp to that range; consumers may assume it. A detector with no
/// confidence model reports 1 rather than an out-of-range sentinel.
/// </param>
/// <param name="DetectedAt">When the detection was made.</param>
/// <param name="SensorId">Identifier of the sensor aboard the source asset, when known.</param>
/// <param name="Label">Operator-facing label. Display only.</param>
public sealed record DetectionV2State(
    string DetectionId,
    string Type,
    FramedPose Pose,
    string SourceAssetId,
    double Confidence,
    DateTimeOffset DetectedAt,
    string? SensorId = null,
    string? Label = null);

/// <summary>How serious a hazard zone is.</summary>
/// <remarks>
/// Typed rather than the free string <see cref="HazardVizState.Severity"/> carries, so the
/// client cannot silently fail to match a value. The v1 adapter projects each member to its
/// lower-cased name, which reproduces the existing <c>"medium"</c> wire value exactly.
/// </remarks>
public enum HazardSeverity
{
    /// <summary>Severity not assessed.</summary>
    Unknown,

    /// <summary>Noted; no operational restriction implied.</summary>
    Low,

    /// <summary>Avoid where practical.</summary>
    Medium,

    /// <summary>Avoid; entering requires a deliberate decision.</summary>
    High,

    /// <summary>Do not enter.</summary>
    Extreme,
}

/// <summary>A hazard zone: fire, flood, shallow water, exclusion area.</summary>
/// <remarks>
/// The centre is a <see cref="FramedPose"/> rather than the bare <c>float[3]</c> of
/// <see cref="HazardVizState"/>. A hazard supplied by an external feed arrives in that feed's
/// frame, and a hazard that names no frame is a hazard that will eventually be drawn in the
/// wrong place — the failure is silent, because three plausible numbers look identical in
/// every frame.
/// <para>
/// The zone is modelled as a horizontal disc of <paramref name="RadiusM"/> with an optional
/// vertical extent, not as a sphere: a flood has no ceiling and a fire's plume is not
/// symmetric about its centre. <paramref name="AffectedDomains"/> exists because a hazard is
/// rarely universal — shallow water stops a vessel, is irrelevant to a drone, and may be a
/// route for a rover — and gating on it beats each client inventing its own rule.
/// </para>
/// </remarks>
/// <param name="HazardId">Stable identifier for the zone.</param>
/// <param name="Type">Hazard kind (e.g. "fire", "flood", "shallow-water", "exclusion").</param>
/// <param name="Centre">Frame-qualified centre of the zone. Orientation is identity for a radially symmetric zone.</param>
/// <param name="RadiusM">Horizontal radius of the zone in metres.</param>
/// <param name="Severity">How serious the zone is.</param>
/// <param name="AffectedDomains">
/// Domains the hazard actually constrains. Null means "assume it affects everything", which
/// is the safe reading when a source does not say.
/// </param>
/// <param name="BaseHeightM">Lower vertical bound relative to the centre, in metres, when the zone has one.</param>
/// <param name="TopHeightM">Upper vertical bound relative to the centre, in metres, when the zone has one.</param>
/// <param name="ObservedAt">When the zone was last confirmed or updated.</param>
/// <param name="Label">Operator-facing label. Display only.</param>
public sealed record HazardV2State(
    string HazardId,
    string Type,
    FramedPose Centre,
    double RadiusM,
    HazardSeverity Severity,
    IReadOnlyList<AssetDomain>? AffectedDomains = null,
    double? BaseHeightM = null,
    double? TopHeightM = null,
    DateTimeOffset? ObservedAt = null,
    string? Label = null);

// ── Network ────────────────────────────────────────────────────────────────────

/// <summary>One directed link between two assets in the communications mesh.</summary>
/// <remarks>
/// Endpoints are <see cref="AssetDescriptor.AssetId"/> strings, never the <c>int[][]</c>
/// index pairs of <see cref="MeshVizState"/>. Index pairs encode a position in one particular
/// list, so they are only meaningful while every consumer reconstructs exactly that list, in
/// exactly that order. The moment the asset collection is filtered — by domain, by agency, by
/// selection — or split across two frames, or delta-encoded with unchanged entries omitted,
/// every index silently addresses a different asset and the mesh renders links between the
/// wrong pair. Nothing throws; the picture is simply wrong. String pairs cost a few bytes and
/// survive filtering, splitting, reordering and partial frames.
/// <para>
/// A link is directed, so an asymmetric radio path (a relay that hears a rover but cannot
/// reach it) is expressible. Symmetric paths appear as two entries rather than one flagged
/// entry, so a consumer never has to decide whether to mirror.
/// </para>
/// </remarks>
/// <param name="SourceAssetId">Asset transmitting on this link.</param>
/// <param name="TargetAssetId">Asset receiving on this link.</param>
/// <param name="Transport">Bearer carrying the link.</param>
/// <param name="Quality">Normalised link quality in 0–1, where 0 is unusable and 1 is clean.</param>
/// <param name="RssiDbm">Received signal strength in dBm, for bearers that expose it.</param>
/// <param name="LatencyMs">One-way latency in milliseconds, when measured.</param>
/// <param name="PacketLossRatio">Observed loss as a fraction in 0–1. Null and 0 are opposites: no data versus no loss.</param>
/// <param name="RangeM">Slant range between the endpoints in metres, when computed.</param>
/// <param name="IsOccluded">
/// True when terrain or structure blocks line of sight between the endpoints. Carried
/// separately from <paramref name="Quality"/> because the cause matters operationally: an
/// occluded link comes back by moving, a noisy one does not.
/// </param>
public sealed record NetworkLinkState(
    string SourceAssetId,
    string TargetAssetId,
    LinkTransport Transport,
    double Quality,
    double? RssiDbm = null,
    double? LatencyMs = null,
    double? PacketLossRatio = null,
    double? RangeM = null,
    bool IsOccluded = false);

/// <summary>State of the communications mesh across the whole session.</summary>
/// <remarks>
/// Partition membership is reported explicitly rather than left for the client to derive from
/// <paramref name="Links"/>. A client that recomputes connected components from a delta frame
/// with omitted links will find partitions that do not exist, and the operator-visible
/// consequence — "these four assets are cut off" — is too load-bearing to leave to a
/// reconstruction that only works on complete frames.
/// </remarks>
/// <param name="Links">Directed links currently up, in a stable order.</param>
/// <param name="IsPartitioned">
/// True when the mesh has more than one connected component, false when it provably has one,
/// and <see langword="null"/> when this server does not compute connectivity at all.
/// <para>
/// Nullable because the third case is real and is not the same as the second. A deployment
/// that models no radio propagation knows nothing about components, and answering
/// <c>false</c> there would be a fabricated all-clear — an operator reading "mesh healthy"
/// off a server that never looked. Null says "not assessed", which a client can render as
/// unknown rather than as good news. It is emphatically <em>not</em> a restatement of
/// <paramref name="BackhaulAvailable"/>: deriving one from the other makes the two fields
/// exact complements and destroys the distinction the pair exists to carry.
/// </para>
/// </param>
/// <param name="Partitions">
/// Asset identifiers grouped by connected component, largest group first. Null when the
/// server does not compute components; empty is a meaningful answer only when no asset has a
/// link at all.
/// </param>
/// <param name="BackhaulAvailable">
/// True while a route to the operations centre exists. Distinct from
/// <paramref name="IsPartitioned"/>: a fully connected mesh with its backhaul cut is a
/// perfectly healthy mesh that nobody outside it can hear, and a mesh split in two can still
/// have backhaul on one side of the split.
/// </param>
public sealed record NetworkState(
    IReadOnlyList<NetworkLinkState> Links,
    bool? IsPartitioned,
    IReadOnlyList<IReadOnlyList<string>>? Partitions = null,
    bool BackhaulAvailable = true);

// ── Snapshot ───────────────────────────────────────────────────────────────────

/// <summary>The v2 frame broadcast to clients, replacing <see cref="VizFrame"/>.</summary>
/// <remarks>
/// Descriptors and states are separate lists rather than one list of fat objects. A
/// descriptor changes when an asset is spawned or reconfigured; a state changes ten times a
/// second. Keeping them apart is what lets a later delta frame send only the states and omit
/// every descriptor whose <see cref="AssetDescriptor.Revision"/> the client already holds —
/// with them merged, the wire would repeat dimensions, capabilities and motion limits at
/// stream rate forever. Clients cache descriptors by
/// <see cref="AssetDescriptor.AssetId"/> and refresh on a revision increase.
/// <para>
/// Tracks are a separate list from <see cref="Assets"/> because they are not commandable; see
/// <see cref="ExternalTrackState"/> for why that separation is structural rather than
/// cosmetic.
/// </para>
/// </remarks>
/// <param name="SchemaVersion">
/// Version of this frame's shape, stamped from <see cref="CurrentSchemaVersion"/>. Carried on
/// the wire rather than assumed, so a client connected across a server upgrade can detect the
/// change instead of misreading a field.
/// </param>
/// <param name="FrameId">Unique id for this frame, for correlating logs and client-side traces.</param>
/// <param name="ServerTime">Wall-clock time the frame was assembled.</param>
/// <param name="SimulationTimeSeconds">Simulated time in seconds since the session started.</param>
/// <param name="Tick">Simulation tick this frame was captured on. Duplicated inside <paramref name="Transport"/> for the transport bar.</param>
/// <param name="Transport">Authoritative paused/speed/tick state of the loop.</param>
/// <param name="Descriptors">
/// Descriptors for assets in this frame. Complete when <paramref name="DescriptorsComplete"/>
/// is true; otherwise only those whose revision changed.
/// </param>
/// <param name="Assets">State for every asset in the session, in a stable order.</param>
/// <param name="Tracks">Observed contacts we do not control.</param>
/// <param name="Detections">What assets' sensors found this frame.</param>
/// <param name="Hazards">Hazard zones currently in effect.</param>
/// <param name="Network">Mesh state, or null when the session does not model comms.</param>
/// <param name="EnvironmentRevision">
/// Opaque revision of the environment payload — terrain, weather and the shared sea-level
/// datum — which the client fetches separately and caches. It changes when a preset,
/// heightmap or weather configuration changes, and it is what tells the client its cached
/// environment is stale. Never parse it; compare it.
/// </param>
/// <param name="DescriptorsComplete">
/// True when <paramref name="Descriptors"/> covers every asset in <paramref name="Assets"/>.
/// False marks a delta frame carrying only changed descriptors, so a client knows a missing
/// descriptor means "unchanged" and not "asset removed". Defaulted to true so full-frame
/// producers and their tests stay unchanged.
/// </param>
public sealed record VizSnapshotV2(
    string SchemaVersion,
    Guid FrameId,
    DateTimeOffset ServerTime,
    double SimulationTimeSeconds,
    long Tick,
    TransportState Transport,
    IReadOnlyList<AssetDescriptor> Descriptors,
    IReadOnlyList<AssetState> Assets,
    IReadOnlyList<ExternalTrackState> Tracks,
    IReadOnlyList<DetectionV2State> Detections,
    IReadOnlyList<HazardV2State> Hazards,
    NetworkState? Network,
    string EnvironmentRevision,
    bool DescriptorsComplete = true)
{
    /// <summary>Schema version this build produces.</summary>
    /// <remarks>
    /// Named <c>CurrentSchemaVersion</c> rather than <c>SchemaVersion</c> because the record
    /// already declares a property of that name; the constant is the value producers stamp
    /// into it. Bump it whenever a field is removed or its meaning changes — additive
    /// optional fields do not require a bump, since a client reading an older schema simply
    /// does not see them.
    /// </remarks>
    public const string CurrentSchemaVersion = "2.0";
}
