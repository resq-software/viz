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

using System.Numerics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>
/// Frozen pose of one asset, as every other asset sees it for the whole of one world step.
/// </summary>
/// <remarks>
/// The buffer these fill is captured once per step, <b>before any asset integrates</b>, so
/// asset <c>N</c> can never observe asset <c>N-1</c>'s post-step position. Without that, the
/// result of a step would depend on registry order the moment two assets interact, and step
/// order would stop being a function of (domain, spawn index) alone.
/// <para>
/// Nothing consumes peer poses yet. They are mandated now so the first separation, collision
/// or closest-point-of-approach advisory has one obviously correct place to read from, instead
/// of reaching into the live registry mid-step.
/// </para>
/// <para>
/// Deliberately deferred: asset-to-drone interaction — a rover blocked by a landed drone, a
/// vessel blocked by a rover on a jetty — is not modelled. Air poses are published here, but
/// no asset acts on them. That is a scope decision, not an oversight: a blocking relation
/// needs a shared obstacle representation that does not exist yet.
/// </para>
/// </remarks>
/// <param name="AssetId">Identifier of the asset this pose belongs to.</param>
/// <param name="Domain">Medium that asset operates in, so a reader can filter without a lookup.</param>
/// <param name="PositionEus">Position in the scene frame (<see cref="CoordinateFrame.LocalEus"/>), in metres.</param>
/// <param name="FootprintRadiusM">Conservative bounding radius, from <see cref="PhysicalDimensions.FootprintRadiusM"/>.</param>
public readonly record struct PeerPose(
    string AssetId,
    AssetDomain Domain,
    Vector3 PositionEus,
    double FootprintRadiusM);

/// <summary>Everything an asset is allowed to read while integrating one step.</summary>
/// <remarks>
/// Deliberately narrow. There is no <c>SimulationWorld</c>, no <c>SimulatedDrone</c>, no swarm
/// coordinator and no weather system reachable from here, so a ground or surface asset
/// structurally cannot perturb air physics, advance the weather a second time, or draw from
/// the SDK's random stream.
/// <para>
/// There is also no wall clock and no <see cref="TimeProvider"/>: a step is a pure function of
/// this context and the asset's own state, which is what makes a recorded run replayable.
/// Wall-clock stamping happens in <see cref="ISimulatedAsset.Capture"/> instead.
/// </para>
/// </remarks>
/// <param name="DeltaSeconds">Integration timestep in seconds. Always greater than zero — the world skips the asset pass otherwise.</param>
/// <param name="SimulationTimeSeconds">Simulation time at the <em>end</em> of this step, in seconds.</param>
/// <param name="Tick">World step counter at the end of this step.</param>
/// <param name="Environment">Environment sampled at this asset's pre-step position.</param>
/// <param name="Peers">Frozen poses of every asset in the world, including this one. See <see cref="PeerPose"/>.</param>
/// <param name="Random">
/// Deterministic generator owned by the asset world. Separate from the SDK world's own
/// generator, so adding a rover cannot shift a single drone trajectory.
/// </param>
public readonly record struct AssetStepContext(
    double DeltaSeconds,
    double SimulationTimeSeconds,
    long Tick,
    EnvironmentSample Environment,
    IReadOnlyList<PeerPose> Peers,
    Random Random);

/// <summary>Everything an asset is allowed to read while projecting itself onto the wire.</summary>
/// <remarks>
/// Capture is where derived, observation-shaped values belong — freshness, operational state,
/// altitudes, uncertainty growth — because they are a projection of state, not a physics step.
/// Both timestamps are supplied rather than read from a clock, so a captured frame stays a
/// function of its inputs.
/// </remarks>
/// <param name="Environment">Sampler the asset queries at its own position for terrain, water and wind.</param>
/// <param name="SimulationTimeSeconds">Simulation time the captured state refers to.</param>
/// <param name="Tick">World step counter the captured state refers to.</param>
/// <param name="SourceTime">
/// When the asset itself observed this state: the world epoch plus
/// <paramref name="SimulationTimeSeconds"/>. Derived, not sampled, so it replays.
/// </param>
/// <param name="ReceiveTime">When the server took delivery of it. The only wall-clock value in the pipeline.</param>
/// <param name="Origin">
/// Local origin the scene frame is anchored to, or <see langword="null"/> when the scene is
/// unanchored. When present, poses also carry a <see cref="GeoPosition"/> so a consumer that
/// only speaks WGS84 need not resolve the origin itself.
/// </param>
public readonly record struct AssetCaptureContext(
    IEnvironmentSampler Environment,
    double SimulationTimeSeconds,
    long Tick,
    DateTimeOffset SourceTime,
    DateTimeOffset ReceiveTime,
    LocalOrigin? Origin);

/// <summary>A command an asset can be asked to execute, after validation and translation.</summary>
/// <remarks>
/// The wire carries a command kind as a string; this enum is the translated form the
/// simulation executes, so an unrecognised or unsupported kind is rejected at the translation
/// boundary rather than reaching an asset. Members are grouped by the domain that introduced
/// each kind, but gating is by declared capability, never by domain.
/// </remarks>
public enum AssetCommandKind
{
    /// <summary>Not a command. Present so a default-constructed value fails validation.</summary>
    Unspecified = 0,

    /// <summary>Cease movement in a controlled way and await further instruction.</summary>
    Stop,

    /// <summary>Cease movement immediately, accepting an uncontrolled stop.</summary>
    EmergencyStop,

    /// <summary>Hold the current position or pattern.</summary>
    Hold,

    /// <summary>Return control to onboard autonomy after a manual takeover.</summary>
    ResumeAutonomy,

    /// <summary>Navigate to a position; frame and vertical reference come from the target pose.</summary>
    GoTo,

    /// <summary>Execute an assigned route.</summary>
    FollowRoute,

    /// <summary>Navigate to the base, launch point or rally point.</summary>
    ReturnToBase,

    /// <summary>Change the commanded cruise speed without changing the destination.</summary>
    SetSpeed,

    /// <summary>Leave the support surface under own power.</summary>
    Takeoff,

    /// <summary>Perform a controlled descent onto a support surface.</summary>
    Land,

    /// <summary>Change commanded altitude, keeping the horizontal position.</summary>
    SetAltitude,

    /// <summary>Orbit or hold a pattern about a point.</summary>
    Loiter,

    /// <summary>Drive to a ground position.</summary>
    DriveTo,

    /// <summary>Command a steering angle directly.</summary>
    SetSteering,

    /// <summary>Drive backwards along the longitudinal axis.</summary>
    Reverse,

    /// <summary>Stop and secure at the current position.</summary>
    Park,

    /// <summary>Transit to a position on the water.</summary>
    TransitTo,

    /// <summary>Steer a commanded course over ground.</summary>
    SetCourse,

    /// <summary>Actively hold a position against wind and current.</summary>
    StationKeep,

    /// <summary>Approach and secure to a dock or mooring.</summary>
    Dock,

    /// <summary>Release from a dock or mooring and stand off.</summary>
    Undock,
}

/// <summary>A validated, domain-translated command handed to a single asset.</summary>
/// <remarks>
/// This is the <em>output</em> of the command pipeline, not its input: issuer resolution,
/// payload and deadline validation, idempotency, lease checking and capability gating all
/// happen before one of these is constructed. An asset still re-checks
/// <see cref="SimulatedAssetCommand.IsSatisfiedBy"/> as a last line of defence, because a
/// capability check that only ever runs in one place is one refactor away from not running at
/// all — and because the v1 compatibility adapter builds these directly, without the v2 gate.
/// </remarks>
/// <param name="Kind">What the asset is being asked to do.</param>
/// <param name="AssetId">Asset the command is addressed to.</param>
/// <param name="Target">Frame-qualified destination, for the kinds that navigate. Null otherwise.</param>
/// <param name="SpeedMps">Commanded speed in metres per second, or null to use the asset's default.</param>
/// <param name="HeadingRad">Commanded heading or course in radians clockwise from true north, or null to leave it free.</param>
/// <param name="AltitudeM">Commanded altitude in metres, meaningful for <see cref="AssetCommandKind.SetAltitude"/>.</param>
/// <param name="CommandId">
/// Identifier of the originating envelope, carried through for correlation only. Never used as
/// a sort key: a <see cref="Guid"/> has no meaningful order, so ordering by one would make
/// execution order depend on random bytes.
/// </param>
/// <param name="AltitudeReference">
/// Datum <paramref name="AltitudeM"/> is measured against. The API boundary converts a commanded
/// altitude onto the scene's vertical axis — the only place the terrain under the asset is known
/// — and stamps <see cref="VerticalReference.MeanSeaLevel"/>, the scene's own datum. Anything
/// else reaching an executor means the command bypassed that boundary, which an asset refuses
/// rather than guessing: above-ground and mean-sea-level altitudes differ by the terrain height,
/// up to about 120 m in this scene, and picking the wrong one flies a drone into a hillside.
/// </param>
public readonly record struct SimulatedAssetCommand(
    AssetCommandKind Kind,
    string AssetId,
    FramedPose? Target = null,
    double? SpeedMps = null,
    double? HeadingRad = null,
    double? AltitudeM = null,
    Guid CommandId = default,
    VerticalReference AltitudeReference = VerticalReference.Unknown)
{
    /// <summary>Capability an asset must declare before this command may be executed.</summary>
    /// <remarks>
    /// Read from <see cref="CommandCatalog"/> rather than restated here, so the executor's gate
    /// cannot drift from the one the validator applied and the capability report advertised. A
    /// second, hand-maintained copy is what produced the bug this replaced: the catalog offered
    /// <c>hold</c> to every mobile asset while this table demanded
    /// <see cref="AssetCapability.StationKeep"/>, so a displacement-hull vessel — which
    /// deliberately cannot hold station — was advertised a command it would then refuse.
    /// <para>
    /// Only an <see cref="CapabilityMatch.All"/> requirement is reported, because this is a mask
    /// and callers test it with an AND. An any-of requirement such as <c>goTo</c>'s "some kind of
    /// navigation" is not expressible as one, and reporting the union would demand both two- and
    /// three-dimensional navigation of a rover that legitimately declares only the first. Use
    /// <see cref="IsSatisfiedBy"/>, which honours the match rule, wherever the full gate is
    /// wanted; this property is deliberately never stricter than that.
    /// </para>
    /// </remarks>
    public AssetCapability RequiredCapability =>
        Definition is { Match: CapabilityMatch.All } definition
            ? definition.RequiredCapabilities
            : AssetCapability.None;

    /// <summary>The catalog row backing this command, or null for a kind nothing registered.</summary>
    private CommandDefinition? Definition =>
        CommandCatalog.TryGet(AssetCommandTranslator.ToCatalogKind(Kind), out var definition)
            ? definition
            : null;

    /// <summary>Whether an asset declaring <paramref name="declared"/> may execute this command.</summary>
    /// <remarks>
    /// The check an asset should prefer: it applies the catalog's own any-of/all-of rule, so it
    /// accepts exactly the set the capability report advertises — no more and no less. A kind
    /// with no catalog row is refused, because a command nothing registered is not one an asset
    /// should be inventing a gate for.
    /// </remarks>
    /// <param name="declared">Capability mask from the asset's descriptor.</param>
    /// <returns><see langword="true"/> when the command may proceed.</returns>
    public bool IsSatisfiedBy(AssetCapability declared) =>
        Definition is { } definition && definition.IsSatisfiedBy(declared);
}

/// <summary>Outcome of handing a command to an asset.</summary>
/// <remarks>
/// A rejection carries a stable token — <c>capability.missing</c>, <c>command.unsupported</c>,
/// <c>command.target.missing</c> — never prose, so the API layer can map it to a response and
/// a test can assert on it without matching English. Rejection is always side-effect free.
/// </remarks>
/// <param name="IsAccepted">True when the asset took the command on.</param>
/// <param name="Reason">Machine-readable rejection token, or null when accepted.</param>
public readonly record struct AssetCommandResult(bool IsAccepted, string? Reason)
{
    /// <summary>The asset accepted the command.</summary>
    public static AssetCommandResult Accepted => new(true, null);

    /// <summary>Builds a rejection carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">Stable machine-readable token, e.g. <c>capability.missing</c>.</param>
    /// <returns>A rejected result.</returns>
    public static AssetCommandResult Rejected(string reason) => new(false, reason);
}

/// <summary>How much operator attention a simulation event deserves.</summary>
public enum AssetEventSeverity
{
    /// <summary>Recorded for context; no action implied.</summary>
    Info,

    /// <summary>Worth an operator's attention but not mission-limiting on its own.</summary>
    Warning,

    /// <summary>Safety-relevant; the asset needs intervention.</summary>
    Alert,
}

/// <summary>A discrete thing that happened to an asset, drained once and then forgotten.</summary>
/// <remarks>
/// Events are queued by the asset and drained by the world at frame-assembly time rather than
/// pushed through a callback, because a callback raised mid-step could re-enter the owning room
/// while its lock is held. Draining is destructive: an event delivered twice would be counted
/// twice.
/// <para>
/// <paramref name="Code"/> is the contract — alerting and tests key on it — while
/// <paramref name="Message"/> is free to be rewritten for readability at any time.
/// </para>
/// </remarks>
/// <param name="AssetId">Asset the event was raised against.</param>
/// <param name="Code">Stable machine-readable code, e.g. <c>ground.immobilised</c>.</param>
/// <param name="Severity">How serious the event is.</param>
/// <param name="Message">Operator-facing description of this occurrence.</param>
/// <param name="SimulationTimeSeconds">Simulation time the event was raised at.</param>
/// <param name="Tick">World step the event was raised on.</param>
public sealed record AssetEvent(
    string AssetId,
    string Code,
    AssetEventSeverity Severity,
    string Message,
    double SimulationTimeSeconds,
    long Tick);

/// <summary>An entity the asset world tracks, commands and publishes.</summary>
/// <remarks>
/// Read and command surface only. Advancing physics lives on <see cref="IStepDrivenAsset"/>,
/// and that split is load-bearing rather than cosmetic: air assets are integrated by the SDK's
/// own world, so an air asset genuinely has no step of its own. Giving every asset a
/// <c>Step</c> would force a no-op implementation on the air side that reads like dead code
/// and invites someone to "fix" it by moving flight physics into it — which is exactly how
/// drone trajectories would drift away from the pinned SDK behaviour.
/// <para>
/// Implementations perform no synchronisation. Every member is called under the owning room's
/// single lock, and no live mutable collection ever leaves this interface.
/// </para>
/// </remarks>
public interface ISimulatedAsset
{
    /// <summary>Stable identifier, unique across every domain in the world.</summary>
    string AssetId { get; }

    /// <summary>Medium this asset operates in. Always matches <see cref="Descriptor"/>.</summary>
    AssetDomain Domain { get; }

    /// <summary>Current position in the scene frame (<see cref="CoordinateFrame.LocalEus"/>), in metres.</summary>
    /// <remarks>
    /// Exposed separately from <see cref="Capture"/> so the world can fill its frozen peer-pose
    /// buffer without building a full state record — and therefore without a wall-clock stamp —
    /// for every asset on every step.
    /// </remarks>
    Vector3 PositionEus { get; }

    /// <summary>Metadata describing what this asset is. Changes rarely.</summary>
    AssetDescriptor Descriptor { get; }

    /// <summary>Projects the asset's current state onto the wire model.</summary>
    /// <remarks>
    /// Pure with respect to physics: capture never integrates and never advances the world. It
    /// is idempotent within a tick, so calling it twice for the same
    /// <see cref="AssetCaptureContext.Tick"/> yields the same state and raises no duplicate
    /// events.
    /// </remarks>
    /// <param name="context">Times, origin and environment sampler for this capture.</param>
    /// <returns>A freshly built, fully owned state record.</returns>
    AssetState Capture(in AssetCaptureContext context);

    /// <summary>Applies a validated command, or rejects it with no side effects.</summary>
    /// <param name="command">The translated command; its asset id must match this asset.</param>
    /// <returns>Acceptance, or a rejection carrying a machine-readable reason.</returns>
    AssetCommandResult Apply(in SimulatedAssetCommand command);

    /// <summary>Removes and returns every event raised since the last drain.</summary>
    /// <returns>Events in the order they were raised. Empty when nothing happened.</returns>
    IReadOnlyList<AssetEvent> DrainEvents();
}

/// <summary>An asset whose motion the asset world integrates itself.</summary>
/// <remarks>
/// Ground and surface assets only. The SDK's world knows nothing about them and would in any
/// case skip anything reporting <c>HasLanded</c> — permanently true for a rover sitting on the
/// terrain — so their integration has to live on our side of the boundary.
/// </remarks>
public interface IStepDrivenAsset : ISimulatedAsset
{
    /// <summary>Advances this asset by one step.</summary>
    /// <remarks>
    /// Must be a pure function of <paramref name="context"/> and the asset's own state: no wall
    /// clock, no adaptive substepping, no convergence-based early exit, and no iteration count
    /// that depends on state. Peer poses are read-only and frozen, so writing to another asset
    /// from here is not possible and must not be made possible.
    /// </remarks>
    /// <param name="context">Timestep, environment sample, frozen peer poses and the world's generator.</param>
    void Step(in AssetStepContext context);
}
