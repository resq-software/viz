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

/// <summary>Every command kind the system accepts, as wire tokens.</summary>
/// <remarks>
/// Tokens are camelCase and matched <b>ordinally</b>: <c>"GoTo"</c> is not <c>"goTo"</c>. Case
/// folding would make the wire contract depend on the server's culture, and a command that
/// silently succeeds under one culture and fails under another is worse than one that always
/// fails. Kinds are grouped common / air / ground / surface, but the grouping is documentation —
/// the domains a kind actually applies to live in its <see cref="CommandDefinition"/>.
/// </remarks>
public static class CommandKinds
{
    /// <summary>Cease motion and hold whatever position results. Never gated.</summary>
    public const string Stop = "stop";
    /// <summary>Cut propulsion immediately, accepting an uncontrolled stop. Never gated.</summary>
    public const string EmergencyStop = "emergencyStop";
    /// <summary>Suspend the current plan and hold, keeping it resumable.</summary>
    public const string Hold = "hold";
    /// <summary>Hand control back to autonomy and resume the suspended plan.</summary>
    public const string ResumeAutonomy = "resumeAutonomy";
    /// <summary>Navigate to a point, domain-neutrally.</summary>
    public const string GoTo = "goTo";
    /// <summary>Execute a stored route.</summary>
    /// <remarks>
    /// Reserved but <b>not registered</b> in <see cref="CommandCatalog"/>: nothing advertises it
    /// and nothing accepts it. The comment where its row belongs says why, and what restores it.
    /// </remarks>
    public const string FollowRoute = "followRoute";
    /// <summary>Navigate to the asset's base, launch point or rally point.</summary>
    public const string ReturnToBase = "returnToBase";
    /// <summary>Set the commanded speed setpoint, in metres per second.</summary>
    public const string SetSpeed = "setSpeed";

    /// <summary>Leave the support surface and climb to a safe height. Air only.</summary>
    public const string Takeoff = "takeoff";
    /// <summary>Descend and touch down. Air only.</summary>
    public const string Land = "land";
    /// <summary>Set the commanded altitude, in metres. Air only.</summary>
    public const string SetAltitude = "setAltitude";
    /// <summary>Hold an airborne pattern about a point. Air only.</summary>
    public const string Loiter = "loiter";

    /// <summary>Drive to a point on the terrain surface. Ground only.</summary>
    public const string DriveTo = "driveTo";
    /// <summary>Set the steering angle directly, in radians. Ground only.</summary>
    public const string SetSteering = "setSteering";
    /// <summary>Drive backwards along the longitudinal axis. Ground only.</summary>
    public const string Reverse = "reverse";
    /// <summary>Stop and secure in place until explicitly released. Ground only.</summary>
    public const string Park = "park";

    /// <summary>Transit to a point on the water surface. Surface only.</summary>
    public const string TransitTo = "transitTo";
    /// <summary>Steer a commanded course over ground, in radians clockwise from true north. Surface only.</summary>
    public const string SetCourse = "setCourse";
    /// <summary>Actively hold a position against current and wind. Surface only.</summary>
    public const string StationKeep = "stationKeep";
    /// <summary>Approach and secure to a dock or mooring. Surface only.</summary>
    public const string Dock = "dock";
    /// <summary>Release from a dock or mooring and stand off. Surface only.</summary>
    public const string Undock = "undock";
}

/// <summary>Keys used in <see cref="AssetCommandEnvelope.Parameters"/>.</summary>
/// <remarks>
/// Values are invariant-culture decimal strings. A string bag rather than typed fields per
/// kind keeps the envelope one shape on the wire; the validator parses and range-checks the
/// handful each command actually needs and hands the results on as typed
/// <see cref="CommandIntent"/> properties, so nothing downstream re-parses strings.
/// </remarks>
public static class CommandParameters
{
    /// <summary>Commanded speed in metres per second.</summary>
    public const string Speed = "speed";
    /// <summary>Commanded altitude in metres.</summary>
    public const string Altitude = "altitude";
    /// <summary>Commanded course in radians clockwise from true north.</summary>
    public const string Course = "course";
    /// <summary>Commanded steering angle in radians; positive turns to starboard.</summary>
    public const string Steering = "steering";
    /// <summary>Loiter radius or station-keeping tolerance radius, in metres.</summary>
    public const string Radius = "radius";

    /// <summary>Vertical datum <see cref="Altitude"/> is measured against.</summary>
    /// <remarks>
    /// A <see cref="Models.VerticalReference"/> member name, matched case-insensitively — never a
    /// number, because the enum's numbering is an implementation detail and a wire contract keyed
    /// on it would break the moment a member is inserted.
    /// <para>
    /// Mandatory whenever <see cref="Altitude"/> is present. An asset publishes an above-ground,
    /// a mean-sea-level and a scene altitude simultaneously, and they differ by the terrain
    /// elevation under it — up to about 120 m in this scene. An operator who reads the
    /// above-ground figure and commands that number would, without this key, fly into the
    /// hillside. A bare altitude is therefore refused rather than assumed.
    /// </para>
    /// </remarks>
    public const string VerticalReference = "verticalReference";
}

/// <summary>How a command's declared capability requirement is tested.</summary>
public enum CapabilityMatch
{
    /// <summary>The asset must declare every required capability.</summary>
    All,

    /// <summary>
    /// The asset must declare at least one. This is what lets <c>goTo</c> require
    /// "some navigation" without forcing a rover to claim three-dimensional navigation.
    /// </summary>
    Any,
}

/// <summary>Which operational states a command may be issued in.</summary>
/// <remarks>
/// A named policy rather than a per-command state list: the interesting distinctions are few,
/// and a list per command drifts out of sync the moment a state is added to
/// <see cref="OperationalState"/>.
/// </remarks>
public enum OperationalStatePolicy
{
    /// <summary>
    /// Permitted in every state, including <see cref="OperationalState.Faulted"/> and
    /// <see cref="OperationalState.Offline"/>. Reserved for stop commands: refusing to stop
    /// because the asset is already unhappy is exactly backwards.
    /// </summary>
    Always,

    /// <summary>Permitted whenever the asset is reachable — anything but unknown or offline.</summary>
    Responsive,

    /// <summary>
    /// Permitted only when the asset is under command and could move: standby, ready, active,
    /// holding or returning. Excludes recovering, so a landing or docking asset must be
    /// interrupted with a stop before it is retasked.
    /// </summary>
    Operable,

    /// <summary>Permitted only while stationary and cleared: standby or ready.</summary>
    Stationary,
}

/// <summary>Everything the validator needs to know about one command kind.</summary>
/// <remarks>
/// The registry is the single place that answers "may this asset be told this?", which is what
/// keeps capability gating out of controllers, hubs and the simulation core. Adding a command
/// is a row here plus a token in <see cref="CommandKinds"/>; no call site changes.
/// </remarks>
/// <param name="Kind">Wire token from <see cref="CommandKinds"/>.</param>
/// <param name="RequiredCapabilities">Capability mask the asset must declare, or <see cref="AssetCapability.None"/> for an ungated command.</param>
/// <param name="Match">Whether all or any of <paramref name="RequiredCapabilities"/> must be declared.</param>
/// <param name="Domains">Domains the command applies to. Issuing it to any other domain is rejected.</param>
/// <param name="AllowedTargets">Target shapes accepted. <see cref="CommandTargetKinds.None"/> means supplying a target is an error.</param>
/// <param name="RequiresTarget">True when omitting the target is an error.</param>
/// <param name="RequiresFreshPosition">True when the command cannot be executed from a stale position report.</param>
/// <param name="StatePolicy">Operational states the command may be issued in.</param>
/// <param name="RequiredParameters">Keys from <see cref="CommandParameters"/> that must be present.</param>
public sealed record CommandDefinition(
    string Kind,
    AssetCapability RequiredCapabilities,
    CapabilityMatch Match,
    IReadOnlyList<AssetDomain> Domains,
    CommandTargetKinds AllowedTargets,
    bool RequiresTarget,
    bool RequiresFreshPosition,
    OperationalStatePolicy StatePolicy,
    IReadOnlyList<string> RequiredParameters)
{
    /// <summary>Whether this command applies to <paramref name="domain"/>.</summary>
    /// <param name="domain">Domain of the asset the command is aimed at.</param>
    /// <returns><see langword="true"/> when the command is meaningful in that domain.</returns>
    public bool AppliesTo(AssetDomain domain) => Domains.Contains(domain);

    /// <summary>Whether <paramref name="declared"/> satisfies this command's capability requirement.</summary>
    /// <param name="declared">Capability mask from the asset's descriptor.</param>
    /// <returns><see langword="true"/> when the command may proceed to the next gate.</returns>
    public bool IsSatisfiedBy(AssetCapability declared) =>
        RequiredCapabilities == AssetCapability.None
        || (Match == CapabilityMatch.All
            ? (declared & RequiredCapabilities) == RequiredCapabilities
            : (declared & RequiredCapabilities) != AssetCapability.None);

    /// <summary>Whether this command may be issued to an asset in <paramref name="state"/>.</summary>
    /// <param name="state">Asset's current coarse operational state.</param>
    /// <returns><see langword="true"/> when the state policy permits the command.</returns>
    public bool PermitsState(OperationalState state) => StatePolicy switch
    {
        OperationalStatePolicy.Always => true,
        OperationalStatePolicy.Responsive =>
            state is not (OperationalState.Unknown or OperationalState.Offline),
        OperationalStatePolicy.Operable =>
            state is OperationalState.Standby or OperationalState.Ready or OperationalState.Active
                or OperationalState.Holding or OperationalState.Returning,
        OperationalStatePolicy.Stationary =>
            state is OperationalState.Standby or OperationalState.Ready,
        _ => false,
    };
}

/// <summary>
/// The registry of command kinds, and the gate every command passes through before anything
/// in the simulation is touched.
/// </summary>
/// <remarks>
/// Validation itself lives in <c>CommandCatalog.Validation.cs</c>, following the same
/// partial-class split <see cref="CoordinateFrames"/> uses: the table of what exists and the
/// procedure that enforces it are separate concerns and read better apart.
/// </remarks>
public static partial class CommandCatalog
{
    /// <summary>Highest commanded altitude the scene accepts, in scene-frame metres.</summary>
    /// <remarks>
    /// Not an arbitrary round number: it is the same 20 km box a positional target must already
    /// sit inside at the REST boundary, which is itself documented as generously past the 4 km
    /// terrain extent so a scenario may stage an asset off the map. Bounding the scalar and the
    /// target's <c>Y</c> identically is the point — a looser scalar bound would make
    /// <c>setAltitude</c> a way to reach a position <c>goTo</c> refuses, and an unbounded one lets
    /// <c>1e300</c> become <c>+Infinity</c> on the cast to <see cref="float"/>, which turns the
    /// asset's position into <c>NaN</c> and takes the whole room's frame broadcast down with it.
    /// </remarks>
    public const double MaxCommandedAltitudeM = 20_000.0;

    /// <summary>Lowest commanded altitude the scene accepts, in scene-frame metres.</summary>
    /// <remarks>
    /// Symmetric with <see cref="MaxCommandedAltitudeM"/> for the same reason, and deliberately
    /// below zero: the scene datum is mean sea level, and the canyon preset puts the water
    /// surface 60 m under it, so a legitimate low altitude is negative.
    /// </remarks>
    public const double MinCommandedAltitudeM = -MaxCommandedAltitudeM;

    /// <summary>Capability mask meaning "navigates in some number of dimensions".</summary>
    private const AssetCapability AnyNavigation = AssetCapability.Navigate2D | AssetCapability.Navigate3D;

    private static readonly AssetDomain[] MobileDomains =
        [AssetDomain.Air, AssetDomain.Ground, AssetDomain.Surface];

    private static readonly AssetDomain[] AirOnly = [AssetDomain.Air];

    private static readonly AssetDomain[] GroundOnly = [AssetDomain.Ground];

    private static readonly AssetDomain[] SurfaceOnly = [AssetDomain.Surface];

    private static readonly CommandDefinition[] Ordered = BuildDefinitions();

    private static readonly Dictionary<string, CommandDefinition> Registry =
        Ordered.ToDictionary(d => d.Kind, StringComparer.Ordinal);

    /// <summary>Every registered command definition, in a fixed registration order.</summary>
    /// <remarks>
    /// An array rather than the dictionary's values, so anything that enumerates the catalog —
    /// a capability matrix in the UI, a golden test over the whole table — sees the same order
    /// every run. Dictionary iteration order is not a contract.
    /// </remarks>
    public static IReadOnlyList<CommandDefinition> All => Ordered;

    /// <summary>Looks up a command kind.</summary>
    /// <remarks>Ordinal, case-sensitive: an unrecognised casing is an unknown kind, not a near miss.</remarks>
    /// <param name="kind">Wire token from the envelope; may be <see langword="null"/> or empty.</param>
    /// <param name="definition">The matching definition on success, otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the kind is registered.</returns>
    public static bool TryGet(string? kind, [NotNullWhen(true)] out CommandDefinition? definition)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            definition = null;
            return false;
        }

        return Registry.TryGetValue(kind, out definition);
    }

    private static CommandDefinition[] BuildDefinitions()
    {
        // Common — every mobile domain. Stop and emergencyStop are ungated on purpose: they
        // reduce energy in the system, so no capability, state or freshness check may block them.
        CommandDefinition[] definitions =
        [
            Def(CommandKinds.Stop, statePolicy: OperationalStatePolicy.Always),
            Def(CommandKinds.EmergencyStop, statePolicy: OperationalStatePolicy.Always),
            // Hold is the domain-NEUTRAL "stop making mission progress, stay safe" command, so it
            // is ungated on purpose and the executor's capability table agrees. Requiring
            // StationKeep would make it unissuable to exactly the assets that most need it: a
            // displacement hull cannot hold a fixed point, but it can and must stop working the
            // mission, and it satisfies the command by the safest means its profile allows.
            // Actively pinning a position against wind and current is a different command —
            // stationKeep — and that one does require the capability.
            Def(CommandKinds.Hold, statePolicy: OperationalStatePolicy.Responsive),
            Def(CommandKinds.ResumeAutonomy),
            Def(CommandKinds.GoTo, AnyNavigation, CapabilityMatch.Any,
                targets: CommandTargetKinds.Point | CommandTargetKinds.Geo,
                requiresTarget: true, requiresFreshPosition: true),
            // followRoute is NOT registered, and its absence is the contract rather than an
            // oversight. Its one target shape is a CommandTargetKinds.Route — a stored route named
            // by identifier — and this build has no route store for that identifier to name, so
            // AssetCommandTranslator refuses every route target handed to it. While the row was
            // registered, all three mobile domains therefore advertised "run route R7" and refused
            // every such request, after the idempotency key had already been claimed. Same broken
            // promise as dock's Asset target and setSteering's whole row, withdrawn for the same
            // reason: an honest contract beats a control whose only outcome is a rejection.
            //
            // Register it again in the commit that gives a route somewhere to live and somewhere
            // to travel — a per-room store the translator resolves an identifier against, and a
            // translated command carrying the resolved waypoint list rather than the single pose
            // SimulatedAssetCommand holds today. The executors are the small part: every navigator
            // already tracks to a target, so a route is a sequence of them.
            // SurfaceAsset.ApplyFollowRoute is where one lands; CrossDomainInvariantTests fails
            // the moment this is advertised again without the store behind it.
            Def(CommandKinds.ReturnToBase, AnyNavigation, CapabilityMatch.Any, requiresFreshPosition: true),
            Def(CommandKinds.SetSpeed, AnyNavigation | AssetCapability.ManualControl, CapabilityMatch.Any,
                requiredParameters: [CommandParameters.Speed]),

            // Air. Rejected outright for ground and surface assets: the domain list is the gate
            // that still fires even if a descriptor wrongly declares Takeoff or Land.
            Def(CommandKinds.Takeoff, AssetCapability.Takeoff, domains: AirOnly,
                statePolicy: OperationalStatePolicy.Stationary),
            // Land accepts NO target. The kinematic flight model this simulation runs has a
            // single setpoint and no way to sequence "fly there, then descend": it can go to a
            // point, or it can land where it is, and it latches its landed flag only on the
            // latter. Advertising a target it would discard turned "land at this point" into a
            // 202 followed by a landing in place, which is worse than a refusal because nothing
            // anywhere says the point was ignored. Restore the target when the flight model can
            // honour it, not before.
            Def(CommandKinds.Land, AssetCapability.Land, domains: AirOnly,
                statePolicy: OperationalStatePolicy.Responsive),
            Def(CommandKinds.SetAltitude, AssetCapability.Navigate3D, domains: AirOnly,
                requiredParameters: [CommandParameters.Altitude]),

            // Loiter's target IS honoured: a loiter about a point is flown as a hold over that
            // point. The model has no orbit primitive, so the radius parameter is advisory and
            // not flown — which is why loiter does not require one.
            Def(CommandKinds.Loiter, AssetCapability.Navigate3D, domains: AirOnly,
                targets: CommandTargetKinds.Point | CommandTargetKinds.Geo),

            // Ground.
            Def(CommandKinds.DriveTo, AssetCapability.Navigate2D, domains: GroundOnly,
                targets: CommandTargetKinds.Point | CommandTargetKinds.Geo,
                requiresTarget: true, requiresFreshPosition: true),
            // setSteering is NOT registered, and its absence is the contract rather than an
            // oversight. Every ground profile declares ManualControl, so a row here would be
            // advertised to every rover — and every rover refuses it, because
            // SimulatedAssetCommand carries no steering field for the angle to travel in and a
            // pivot-steered platform has no steering linkage to aim anyway. An advertised
            // command whose only possible outcome is a rejection puts a control on screen that
            // cannot work, which is the same dishonesty that made hold demand StationKeep and
            // land advertise a target it discarded. Register it in the same commit that gives
            // the angle somewhere to travel, not before; GroundAsset.ApplySetSteering names
            // exactly what is missing, and GroundWiringHardeningTests holds the two sets equal.
            Def(CommandKinds.Reverse, AssetCapability.Reverse, domains: GroundOnly),
            Def(CommandKinds.Park, domains: GroundOnly, statePolicy: OperationalStatePolicy.Responsive),

            // Surface.
            Def(CommandKinds.TransitTo, AssetCapability.Navigate2D, domains: SurfaceOnly,
                targets: CommandTargetKinds.Point | CommandTargetKinds.Geo,
                requiresTarget: true, requiresFreshPosition: true),
            Def(CommandKinds.SetCourse, AssetCapability.ManualControl, domains: SurfaceOnly,
                requiredParameters: [CommandParameters.Course]),
            Def(CommandKinds.StationKeep, AssetCapability.StationKeep, domains: SurfaceOnly,
                targets: CommandTargetKinds.Point | CommandTargetKinds.Geo, requiresFreshPosition: true),
            // Dock takes a berth as a POSITION. The Asset shape used to be advertised here and
            // accepted by this validator, and then refused by AssetCommandTranslator for every
            // request that carried one: nothing in this build resolves an identifier to a pose,
            // and there is nothing for it to resolve to either, because every VehicleClass
            // AssetProfiles can spawn is a vehicle — a pier or a mooring cannot exist as an
            // asset at all. So a client that rendered the capability report drew a
            // "dock to <asset>" control whose only possible outcome was a 409, raised after the
            // idempotency key had already been claimed. Withdrawing the shape turns that into an
            // immediate 400 naming the target field, which is the same call already made for
            // land's discarded target and for setSteering. Restore it in the commit that adds
            // both a fixed-domain berth asset and a resolver the translator can reach — and
            // resolve it there, after the idempotency hash is taken, so that "dock to pier-1"
            // keeps one stable request identity however the berth moves.
            Def(CommandKinds.Dock, AssetCapability.Dock, domains: SurfaceOnly,
                targets: CommandTargetKinds.Point | CommandTargetKinds.Geo,
                requiresTarget: true, requiresFreshPosition: true),
            Def(CommandKinds.Undock, AssetCapability.Dock, domains: SurfaceOnly,
                statePolicy: OperationalStatePolicy.Stationary),
        ];

        return definitions;
    }

    private static CommandDefinition Def(
        string kind,
        AssetCapability capabilities = AssetCapability.None,
        CapabilityMatch match = CapabilityMatch.All,
        IReadOnlyList<AssetDomain>? domains = null,
        CommandTargetKinds targets = CommandTargetKinds.None,
        bool requiresTarget = false,
        bool requiresFreshPosition = false,
        OperationalStatePolicy statePolicy = OperationalStatePolicy.Operable,
        IReadOnlyList<string>? requiredParameters = null) =>
        new(kind, capabilities, match, domains ?? MobileDomains, targets, requiresTarget,
            requiresFreshPosition, statePolicy, requiredParameters ?? []);
}
