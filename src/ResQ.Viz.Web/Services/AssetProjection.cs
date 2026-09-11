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

/// <summary>Request to place one asset of a given vehicle class into a running session.</summary>
/// <remarks>
/// The domain-neutral successor to <see cref="SpawnDroneRequest"/>. It names a vehicle class
/// rather than a free-text model token, because the class decides the capability set, the
/// motion envelope and the physics model — and a spawn that picks those from an unvalidated
/// string is a spawn that can produce a rover declaring <see cref="AssetCapability.Takeoff"/>.
/// <para>
/// The position is a <see cref="FramedPose"/>, not a bare <c>float[3]</c>: three plausible
/// numbers look identical in every frame. <see cref="AssetProjection.ToAssetSpawnRequest"/>
/// stamps <see cref="CoordinateFrame.LocalEus"/> onto a v1 request — the frame v1 always meant
/// but never said.
/// </para>
/// </remarks>
/// <param name="VehicleClass">Mobility archetype to spawn; must be one <see cref="Assets.AssetProfiles"/> supports.</param>
/// <param name="Pose">Frame-qualified spawn pose. Orientation is a request an asset with no heading authority may ignore.</param>
/// <param name="AssetId">Requested identifier, or null to let the server mint one.</param>
/// <param name="DisplayName">Operator-facing name, or null to fall back to the identifier.</param>
/// <param name="Vendor">Equipment maker, for vendor-specific visual treatment. Null or empty means unattributed.</param>
/// <param name="Model">Vendor's model designation, or null when unknown.</param>
/// <param name="AgencyId">Owning agency, for multi-agency scenarios.</param>
/// <param name="FleetId">Fleet or group, for bulk selection and tasking.</param>
public sealed record AssetSpawnRequest(
    VehicleClass VehicleClass,
    FramedPose Pose,
    string? AssetId = null,
    string? DisplayName = null,
    string? Vendor = null,
    string? Model = null,
    string? AgencyId = null,
    string? FleetId = null);

/// <summary>Translates between the v2 asset model and the v1 drone-only wire contract.</summary>
/// <remarks>
/// <b>Not on the serving path, and deliberately staying that way.</b> This is the reference
/// projection the v1 compatibility tests measure against; no production code calls any method
/// here.
/// <para>
/// <b>Decided 2026-09-10: these will not be wired up.</b> They are a better implementation of
/// what production hand-rolls, and the obvious move would be to connect them — but v1 is being
/// removed after its deprecation cycle, and attaching new machinery to a surface on its way out
/// buys a migration nobody wants. The three v1-to-v2 COMMAND adapters that lived here were
/// deleted on the same reasoning; these remain because the compatibility tests are still the
/// written record of what v1 means while v1 still ships. They go when it does.
/// </para>
/// <para>
/// So do not "fix" the duplication by calling into this from SimulationRoom or VizFrameBuilder.
/// The duplication is known, is recorded below, and is guarded instead by tests that drive the
/// real room — see <c>GetSnapshot_Publishes_Velocity_Components_In_The_Order_The_Drone_Moved</c>,
/// which measures the shipped mapping rather than comparing two implementations that both sit
/// off the serving path.
/// </para>
/// <para>
/// The previous summary claimed the opposite — "v1 survives as a projection rather than as a
/// parallel code path: two populations kept in step by hand drift, one population with a filter
/// cannot". v1 survives precisely AS the parallel hand-written path:
/// <c>SimulationRoom.CaptureDroneSnapshots</c> builds the drone states,
/// <c>VizFrameBuilder</c> builds the v1 frame and its detections, and
/// <c>SimController.SendCommand</c> switches straight onto <c>FlightCommand</c>. The drift that
/// summary said could not happen had already happened: the hand-written mapping set
/// <c>Vendor</c> and the reference did not, and transposing two velocity components in the
/// shipped capture went unnoticed by the whole suite.
/// </para>
/// <para>
/// <b>The filter would be the safety property, if this were on the serving path.</b> Nothing
/// exercises it today — production is air-only structurally, because the world's drone list can
/// only hold air assets. Every v1 surface assumes its list holds drones and
/// nothing else — the spawn endpoint caps on its length, the command and fault endpoints use it
/// as an existence check, and the frame builder iterates it to attribute detections. A rover
/// leaking into that list changes four behaviours at once and throws nothing, so the projection
/// gates on <see cref="AssetDescriptor.Domain"/> and refuses a non-air descriptor outright
/// rather than emitting a best-effort entry.
/// </para>
/// <para>
/// Nothing here mutates. Every method builds a fresh value from its arguments, so a caller
/// holding the room lock can project inside it and hand the result out safely.
/// </para>
/// </remarks>
public static class AssetProjection
{
    /// <summary>v1 status string for a drone that is off the ground.</summary>
    private const string FlyingStatus = "flying";

    /// <summary>v1 status string for a drone resting on its support surface.</summary>
    private const string LandedStatus = "landed";

    /// <summary>
    /// Rotation taking the SDK's body axes (forward <c>+Z</c>, left <c>+X</c>, up <c>+Y</c>)
    /// back out of an FLU-referenced attitude.
    /// </summary>
    /// <remarks>
    /// The conjugate of the basis change <c>AirAsset</c> composes on the way in, so capture and
    /// this projection are algebraic inverses: <c>(A·B)·B*</c> is <c>A</c>, the product being
    /// associative and <c>B</c> unit. The float round trip can differ from the SDK's own
    /// quaternion in the last ulp, but it stays unit and names the same rotation. It is needed
    /// because v1 clients apply this quaternion to a mesh whose nose points along <c>+Z</c>;
    /// publishing the FLU-referenced attitude would look right in a hover and be visibly wrong
    /// the moment the airframe banked.
    /// </remarks>
    private static readonly Quaternion FluFromSdkBody = new(0.5f, 0.5f, 0.5f, 0.5f);

    /// <summary>Projects the air assets of a v2 frame onto the v1 drone list.</summary>
    /// <remarks>
    /// Order is preserved from <paramref name="states"/>, which the asset world publishes in
    /// spawn order; filtering a stable order leaves a stable order, so the v1 list is the same
    /// sequence it has always been.
    /// <para>
    /// A state whose descriptor is absent is skipped rather than guessed at, so the v1 adapter
    /// must be fed a frame whose descriptors are complete — a delta frame would under-report the
    /// drone list. Skipping is nonetheless the safe failure direction: the alternative is
    /// publishing an asset of unknown domain as a drone, the exact leak this projection exists
    /// to prevent.
    /// </para>
    /// </remarks>
    /// <param name="descriptors">Descriptors for the assets in <paramref name="states"/>.</param>
    /// <param name="states">Asset states, in publication order.</param>
    /// <returns>v1 drone states for the air assets only, in the same relative order.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IReadOnlyList<DroneVizState> ToDroneVizStates(
        IReadOnlyList<AssetDescriptor> descriptors,
        IReadOnlyList<AssetState> states)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(states);

        var byId = new Dictionary<string, AssetDescriptor>(descriptors.Count, StringComparer.Ordinal);
        for (var i = 0; i < descriptors.Count; i++)
        {
            // Indexer rather than Add: a frame that repeats a descriptor is a producer bug, and
            // throwing here would drop a whole broadcast rather than one duplicated entry.
            byId[descriptors[i].AssetId] = descriptors[i];
        }

        var result = new List<DroneVizState>(states.Count);
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            if (byId.TryGetValue(state.AssetId, out var descriptor)
                && descriptor.Domain == AssetDomain.Air)
            {
                result.Add(ToDroneVizState(state, descriptor));
            }
        }

        return result;
    }

    /// <summary>Projects a whole v2 snapshot onto the v1 drone list.</summary>
    /// <param name="snapshot">Snapshot to project. Its descriptors must be complete.</param>
    /// <returns>v1 drone states for the air assets only.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static IReadOnlyList<DroneVizState> ToDroneVizStates(VizSnapshotV2 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ToDroneVizStates(snapshot.Descriptors, snapshot.Assets);
    }

    /// <summary>Projects one air asset onto its v1 drone state.</summary>
    /// <remarks>
    /// Every field reproduces what v1 published from the SDK drone directly. Status and armed
    /// both derive from one airborne bit, because they were one bit in v1 — computing them
    /// independently is how a landed drone ends up reported as armed.
    /// </remarks>
    /// <param name="state">State of an air asset, posed and twisted in the scene frame.</param>
    /// <param name="descriptor">That asset's descriptor; supplies the vendor tag and the domain gate.</param>
    /// <returns>The v1 drone state.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The descriptor is not an air descriptor, or the pose or twist is not expressed in
    /// <see cref="CoordinateFrame.LocalEus"/>. v1 has no field to name a frame, so a
    /// differently-framed state cannot be published on it without silently relabelling the
    /// numbers.
    /// </exception>
    public static DroneVizState ToDroneVizState(AssetState state, AssetDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Domain != AssetDomain.Air)
        {
            throw new ArgumentException(
                $"The v1 contract carries air assets only; '{descriptor.AssetId}' is in domain "
                + $"'{descriptor.Domain}'.",
                nameof(descriptor));
        }

        RequireSceneFrame(state.Pose.Frame, state.AssetId, nameof(AssetState.Pose));
        RequireSceneFrame(state.Twist.Frame, state.AssetId, nameof(AssetState.Twist));

        var position = state.Pose.Position;
        var velocity = state.Twist.Linear;
        var rotation = Quaternion.Multiply(state.Pose.Orientation, FluFromSdkBody);
        bool airborne = IsAirborne(state);

        return new DroneVizState(
            Id: state.AssetId,
            Pos: [position.X, position.Y, position.Z],
            Rot: [rotation.X, rotation.Y, rotation.Z, rotation.W],
            Vel: [velocity.X, velocity.Y, velocity.Z],

            // v1's battery is a bare double with no way to say "not measured". An air asset
            // always reports a metered pack, so the fallback is unreachable in practice, and it
            // reads flat rather than full so an unmetered source shows up instead of hiding.
            Battery: state.Power.PercentRemaining ?? 0.0,
            Status: airborne ? FlyingStatus : LandedStatus,
            Armed: airborne,
            Vendor: descriptor.Vendor);
    }

    /// <summary>Projects v2 detections onto the v1 detection list.</summary>
    /// <param name="detections">Detections to project.</param>
    /// <returns>v1 detection states, in the same order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detections"/> is null.</exception>
    public static IReadOnlyList<DetectionVizState> ToDetectionVizStates(
        IReadOnlyList<DetectionV2State> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);

        var result = new DetectionVizState[detections.Count];
        for (var i = 0; i < detections.Count; i++)
        {
            result[i] = ToDetectionVizState(detections[i]);
        }

        return result;
    }

    /// <summary>Projects one v2 detection onto its v1 form.</summary>
    /// <remarks>
    /// The reporting asset lands in <see cref="DetectionVizState.DroneId"/>, the only field v1
    /// has, so a detection reported by a rover arrives attributed to a "drone" id that is not a
    /// drone. That is the honest limit of the v1 shape: dropping the detection instead would
    /// lose a casualty sighting, which is worse than mislabelling who found it. v1 clients use
    /// the field only to draw a line back to the reporter, and resolve it against a list that
    /// will not contain the id, so the line is simply not drawn.
    /// </remarks>
    /// <param name="detection">Detection to project; its pose must be in the scene frame.</param>
    /// <returns>The v1 detection state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="detection"/> is null.</exception>
    /// <exception cref="ArgumentException">The pose is not expressed in <see cref="CoordinateFrame.LocalEus"/>.</exception>
    public static DetectionVizState ToDetectionVizState(DetectionV2State detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        RequireSceneFrame(detection.Pose.Frame, detection.DetectionId, nameof(DetectionV2State.Pose));

        var position = detection.Pose.Position;

        return new DetectionVizState(
            Id: detection.DetectionId,
            Type: detection.Type,
            Pos: [position.X, position.Y, position.Z],
            DroneId: detection.SourceAssetId,
            Confidence: detection.Confidence);
    }

    /// <summary>Adapts a v1 spawn request into its v2 equivalent.</summary>
    /// <remarks>
    /// v1 can only ever have meant a multirotor: the endpoint is <c>POST /api/sim/drone</c> and
    /// the server ignored the model token entirely, spawning the SDK's quadrotor whatever was
    /// asked for. The class is therefore fixed rather than parsed, and an unrecognised
    /// <see cref="SpawnDroneRequest.Model"/> is carried through as metadata rather than
    /// rejected — rejecting it now would break a client that has always been able to send it.
    /// </remarks>
    /// <param name="request">The v1 request.</param>
    /// <param name="assetId">Identifier to spawn under, or null to let the server mint one.</param>
    /// <param name="vendor">Vendor tag to attach, or null when unattributed. v1 has no vendor field.</param>
    /// <param name="originId">Local origin the scene frame is anchored to, or null when unanchored.</param>
    /// <returns>An air/multirotor spawn request at the same scene-frame position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">The position is not a finite 3-element array.</exception>
    public static AssetSpawnRequest ToAssetSpawnRequest(
        SpawnDroneRequest request,
        string? assetId = null,
        string? vendor = null,
        string? originId = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Position is not { Length: 3 })
        {
            throw new ArgumentException(
                "Position must be a 3-element array [X, Y, Z].", nameof(request));
        }

        var position = new Vector3(request.Position[0], request.Position[1], request.Position[2]);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            throw new ArgumentException("Position contains a non-finite value.", nameof(request));
        }

        return new AssetSpawnRequest(
            VehicleClass: VehicleClass.Multirotor,

            // Identity orientation: v1 never carried a spawn attitude, and inventing a heading
            // here would make a spawned drone face a direction nobody asked for.
            Pose: new FramedPose(
                CoordinateFrame.LocalEus, originId, position, Quaternion.Identity),
            AssetId: assetId,
            Vendor: vendor,
            Model: request.Model);
    }

    /// <summary>Whether a captured air state describes a drone that is off the ground.</summary>
    /// <remarks>
    /// Read from the air domain extension when there is one, because that is where the flight
    /// model's own landed bit surfaces unchanged. The fallback covers a state carrying no domain
    /// extension and treats only the definitely-not-moving states as landed, since v1's armed
    /// flag has always meant "under power" rather than "healthy".
    /// </remarks>
    /// <param name="state">State to inspect.</param>
    /// <returns><see langword="true"/> when the asset is airborne.</returns>
    private static bool IsAirborne(AssetState state) => state.DomainState is AirDomainState air
        ? air.IsAirborne
        : state.OperationalState is not (OperationalState.Standby or OperationalState.Offline
            or OperationalState.Unknown);

    /// <summary>Rejects a frame the v1 contract cannot describe.</summary>
    /// <param name="frame">Frame the value was expressed in.</param>
    /// <param name="id">Identifier of the asset or detection, for the message.</param>
    /// <param name="member">Name of the offending member, for the message.</param>
    /// <exception cref="ArgumentException"><paramref name="frame"/> is not the scene frame.</exception>
    private static void RequireSceneFrame(CoordinateFrame frame, string id, string member)
    {
        if (frame != CoordinateFrame.LocalEus)
        {
            throw new ArgumentException(
                $"{member} for '{id}' is expressed in '{frame}'. The v1 contract has no field "
                + $"naming a frame, so only '{CoordinateFrame.LocalEus}' can be projected onto it.");
        }
    }
}
