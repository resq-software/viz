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

using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Turns one <see cref="RoomAssetFrame"/> into the two wire shapes a session publishes: the v1
/// <see cref="VizFrame"/> and the v2 <see cref="VizSnapshotV2"/>.
/// </summary>
/// <remarks>
/// There are two publishers of a v2 frame — <c>GET /api/v2/sim/snapshot</c> and the 10 Hz
/// SignalR broadcast — and exactly one assembly, here. A second copy of this projection would
/// not stay identical: the last defect on this path was a frame whose detections and asset poses
/// came from different readings, and a fix applied to a polled frame and not to a streamed one
/// is that same defect with a longer fuse.
/// <para>
/// <b>One frame is one reading.</b> Everything published from a call here comes from the single
/// <see cref="SimulationRoom.CaptureAssetFrame"/> passed in, including the v1 drone projection
/// the detections are derived from. Nothing in this type takes a lock, reads a room, or reaches
/// for a clock other than the caller's: given the same capture and the same server time it
/// produces the same frame, which is what lets the broadcast path publish a v1 and a v2 message
/// that provably describe the same tick.
/// </para>
/// <para>
/// Static and stateless deliberately. The controller and the broadcast loop each already hold
/// the configured <see cref="VizFrameBuilder"/> the survivor and hazard data lives on, and
/// threading a second injected service through both — one of them a hot 60 Hz loop — buys
/// nothing when there is no state to hold.
/// </para>
/// </remarks>
public static class VizSnapshotV2Builder
{
    /// <summary>Builds the v1 frame for a capture.</summary>
    /// <remarks>
    /// The v1 frame is not a by-product of the v2 one: it is still the message v1 clients
    /// receive, and it is also where a v2 frame's detections and hazards come from. Building it
    /// through this one mapping is what keeps the two surfaces from disagreeing about which
    /// field of the capture feeds which argument — <paramref name="capture"/> carries the
    /// transport triple and the sim time together, so no caller has to pair them by hand.
    /// </remarks>
    /// <param name="frames">The configured builder holding this deployment's survivor and hazard data.</param>
    /// <param name="capture">One atomic reading of a room.</param>
    /// <returns>The v1 frame for that reading.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frames"/> or <paramref name="capture"/> is null.</exception>
    public static VizFrame BuildLegacyFrame(VizFrameBuilder frames, RoomAssetFrame capture)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(capture);

        return frames.Build(
            capture.Drones,
            capture.SimulationTimeSeconds,
            capture.BackhaulKilled,
            capture.Transport.Paused,
            capture.Transport.Speed,
            capture.Transport.Tick,
            capture.ScenarioKey);
    }

    /// <summary>Builds the v2 frame for a capture, reusing the v1 frame already built from it.</summary>
    /// <remarks>
    /// <paramref name="legacy"/> is passed in rather than rebuilt because the broadcast path
    /// needs it anyway — it is the v1 message — and building it twice would both cost a second
    /// detection pass per room per broadcast and, worse, invite the two frames to be built from
    /// two different captures. Callers that only want a v2 frame use the single-capture overload
    /// below.
    /// <para>
    /// <paramref name="legacy"/> must have been built from <paramref name="capture"/>. Passing a
    /// frame from a different reading reintroduces exactly the split this type exists to
    /// prevent, and nothing here can detect it: the two are structurally compatible and only the
    /// tick they describe differs.
    /// </para>
    /// </remarks>
    /// <param name="capture">One atomic reading of a room.</param>
    /// <param name="legacy">The v1 frame built from that same reading.</param>
    /// <param name="serverTime">Wall-clock time this frame is being assembled at.</param>
    /// <returns>A full v2 frame — <see cref="VizSnapshotV2.DescriptorsComplete"/> is true.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capture"/> or <paramref name="legacy"/> is null.</exception>
    public static VizSnapshotV2 Build(
        RoomAssetFrame capture, VizFrame legacy, DateTimeOffset serverTime)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(legacy);

        return new VizSnapshotV2(
            SchemaVersion: VizSnapshotV2.CurrentSchemaVersion,
            FrameId: Guid.NewGuid(),
            ServerTime: serverTime,
            SimulationTimeSeconds: capture.SimulationTimeSeconds,
            Tick: capture.Transport.Tick,
            Transport: capture.Transport,
            Descriptors: capture.Descriptors,
            Assets: capture.Assets,

            // The contacts this session is observing but does not control, taken from the same
            // reading as the assets beside them so a client can draw a geometry between the two
            // without silently mixing two ticks. The age each was captured with stays on the
            // track surface — GET /api/v2/sim/tracks — because a frame publishes a picture and
            // the ages are what a consumer needs to decide how much of it to believe; a client
            // that only reads frames still has ExternalTrackState.Freshness and LastUpdateTime.
            // Nothing here carries a capability or a command endpoint, and nothing may render a
            // command affordance on one.
            Tracks: capture.Tracks.Select(t => t.Track).ToList(),

            // Detections are attributed to the asset that made them, so they are derived from
            // the v1 projection rather than recomputed: one detector, two wire shapes.
            Detections: legacy.Detections.Select(d => ToDetectionV2(d, serverTime)).ToList(),
            Hazards: legacy.Hazards.Select(ToHazardV2).ToList(),

            // The one comms fact this build has, plus an explicit unknown for everything it
            // does not. See BuildNetworkState for what was checked and why nothing more is
            // published.
            Network: BuildNetworkState(capture),
            EnvironmentRevision: capture.EnvironmentRevision,
            DescriptorsComplete: true);
    }

    /// <summary>Builds the v2 frame for a capture, building the v1 frame it needs on the way.</summary>
    /// <remarks>
    /// For callers that publish only a v2 frame. The broadcast loop uses the overload above
    /// instead, because it sends the v1 frame as well and must not build it twice.
    /// </remarks>
    /// <param name="frames">The configured builder holding this deployment's survivor and hazard data.</param>
    /// <param name="capture">One atomic reading of a room.</param>
    /// <param name="serverTime">Wall-clock time this frame is being assembled at.</param>
    /// <returns>A full v2 frame.</returns>
    public static VizSnapshotV2 Build(
        VizFrameBuilder frames, RoomAssetFrame capture, DateTimeOffset serverTime) =>
        Build(capture, BuildLegacyFrame(frames, capture), serverTime);

    /// <summary>Reports the backhaul, and reports mesh connectivity as unknown.</summary>
    /// <remarks>
    /// The decision lives in a named method rather than inline at the call site because
    /// <c>Links: []</c> and <c>IsPartitioned: null</c> read like an unfinished stub, and the next
    /// person to see them should find out here that they are the deliberate answer rather than
    /// fill them in with something worse.
    /// <para>
    /// <b>What was checked.</b> Every asset this build can spawn reports
    /// <c>LinkState(LinkTransport.Loopback, IsConnected: true)</c> with no
    /// <see cref="LinkState.MeshPath"/> — <c>AirAsset</c>, <c>GroundAsset</c> and
    /// <c>SurfaceAsset</c> each construct it that way, because the source is in-process and no
    /// transport sits between the asset and the server. Nothing under <c>Services</c> models
    /// radio range, propagation or occlusion, and <c>ResQ.Mavlink.Mesh</c>, though referenced by
    /// the project, is never wired into a room. There is no asset-to-asset link anywhere in a
    /// capture, so none is published: the alternative is inventing a mesh picture an operator
    /// would read as observed.
    /// </para>
    /// <para>
    /// <b><see cref="NetworkState.IsPartitioned"/> is null, meaning UNKNOWN.</b> Not false.
    /// False is the claim that connectivity was assessed and the mesh provably has one
    /// component, and this server never looked — an operator reading "mesh healthy" off that
    /// would be reading a fabricated all-clear. Null is the third state the field is nullable
    /// for, and a client must render it as unknown rather than as good news.
    /// <see cref="NetworkState.Partitions"/> is null for the same reason: empty is a real answer
    /// meaning no asset has a link at all, which is a stronger claim than "not computed".
    /// </para>
    /// <para>
    /// <b><see cref="NetworkState.Links"/> empty does not mean zero links are up.</b> The field
    /// is a non-nullable list, so the contract gives it no way to say "not assessed" the way the
    /// partition tri-state can. A consumer decides whether the mesh picture is known from
    /// <see cref="NetworkState.IsPartitioned"/> and never from <c>Links.Count</c>.
    /// </para>
    /// <para>
    /// <b>Partition state is not derived from the backhaul, in either direction.</b> The v1
    /// frame this same type builds does make that conflation — <see cref="VizFrameBuilder.Build"/>
    /// maps its <c>partitioned</c> argument straight onto <see cref="MeshVizState.Partitioned"/>
    /// and <see cref="BuildLegacyFrame"/> passes <see cref="RoomAssetFrame.BackhaulKilled"/> into
    /// it, so a v1 client raises a partition banner for a cut uplink. That behaviour is kept for
    /// v1 clients that already depend on it and is deliberately not inherited here. A fully
    /// connected mesh with its backhaul cut is a healthy mesh nobody outside can hear; a mesh
    /// split in two can still have backhaul on one side. They are different incidents with
    /// different responses, and making the two fields exact complements would destroy the
    /// distinction the pair exists to carry.
    /// </para>
    /// <para>
    /// <b>An asset-reported mesh route would still not be a link set.</b> If a future asset
    /// populates <see cref="LinkState.MeshPath"/>, its hops are one route currently in use, not
    /// the graph; publishing them as <see cref="NetworkState.Links"/> would assert that the links
    /// up are exactly the routes in flight. And <see cref="NetworkLinkState.Quality"/> is a
    /// non-nullable 0–1 measure, so every synthesised hop would have to carry a per-hop quality
    /// that one end-to-end reading cannot supply. Both are fabrications, so a mesh route changes
    /// nothing here.
    /// </para>
    /// <para>
    /// <b>When a propagation model lands</b>, this method is the single place to change:
    /// populate <see cref="NetworkState.Links"/> from it and compute the components here, on the
    /// server. Components must not be left to the client — a client recomputing them from a
    /// delta frame with unchanged links omitted finds partitions that do not exist.
    /// </para>
    /// </remarks>
    /// <param name="capture">One atomic reading of a room.</param>
    /// <returns>
    /// The session's network state: a real <see cref="NetworkState.BackhaulAvailable"/>, and
    /// unknown for everything this build does not measure.
    /// </returns>
    private static NetworkState BuildNetworkState(RoomAssetFrame capture) =>
        new(
            // Nothing to publish, which is not the same as "no links up" — see remarks.
            Links: [],

            // UNKNOWN. Never false, and never a restatement of the backhaul flag.
            IsPartitioned: null,

            // Not computed. Null rather than empty, which would claim no asset has a link.
            Partitions: null,

            // The one comms fact a capture actually carries.
            BackhaulAvailable: !capture.BackhaulKilled);

    /// <summary>Lifts a v1 detection into the frame-qualified v2 shape.</summary>
    /// <remarks>
    /// The reporting field becomes <see cref="DetectionV2State.SourceAssetId"/>: the v1 producer
    /// only ever attributes to a drone, but the field name is no longer an assumption baked into
    /// the contract, so a rover or a vessel reporting one needs no wire change.
    /// </remarks>
    private static DetectionV2State ToDetectionV2(DetectionVizState detection, DateTimeOffset detectedAt) =>
        new(
            DetectionId: detection.Id,
            Type: detection.Type,
            Pose: SceneFramePose(detection.Pos),
            SourceAssetId: detection.DroneId,
            Confidence: Math.Clamp(detection.Confidence, 0.0, 1.0),
            DetectedAt: detectedAt);

    /// <summary>Lifts a v1 hazard zone into the frame-qualified v2 shape.</summary>
    /// <remarks>
    /// The v1 severity is a free string, so it is parsed rather than cast, and an unrecognised
    /// value becomes <see cref="HazardSeverity.Unknown"/> instead of a silently wrong level.
    /// <paramref name="hazard"/> declares no affected domains, and null means "assume it affects
    /// everything" — the safe reading when the source does not say.
    /// </remarks>
    private static HazardV2State ToHazardV2(HazardVizState hazard) =>
        new(
            HazardId: hazard.Id,
            Type: hazard.Type,
            Centre: SceneFramePose(hazard.Center),
            RadiusM: hazard.Radius,
            Severity: Enum.TryParse<HazardSeverity>(hazard.Severity, ignoreCase: true, out var severity)
                ? severity
                : HazardSeverity.Unknown,
            AffectedDomains: null);

    /// <summary>Wraps a v1 position array as a scene-frame pose with no rotation.</summary>
    /// <remarks>
    /// The scene frame is the frame v1 always meant and never said; stamping it here is the
    /// whole of the v1-to-v2 lift for a point. A malformed array becomes the origin rather than
    /// throwing, matching how the v1 hazard builder already handles one.
    /// </remarks>
    private static FramedPose SceneFramePose(float[]? components) =>
        new(
            Frame: CoordinateFrame.LocalEus,
            OriginId: null,
            Position: components is { Length: 3 }
                ? new Vector3(components[0], components[1], components[2])
                : Vector3.Zero,
            Orientation: Quaternion.Identity);
}
