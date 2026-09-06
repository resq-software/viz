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
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Services;

/// <summary>
/// Autonomous tasking for the ground and surface fleets: the counterpart to
/// <see cref="SwarmCoordinator"/>, which flies the air fleet and only the air fleet.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> Every mixed scenario placed rovers and vessels, gave them full
/// dynamics, navigators, traversability and under-keel models — and then nothing ever told them to
/// go anywhere. A scenario's ground assets sat at their spawn for the whole session while the
/// aircraft flew routes overhead, so a three-domain picture rendered as one moving domain and two
/// parked ones. The gap was tasking, not capability.
/// <para>
/// <b>Commands, not back doors.</b> Waypoints are issued as ordinary
/// <see cref="AssetCommandKind.DriveTo"/> / <see cref="AssetCommandKind.TransitTo"/> commands
/// through the same dispatch an operator's click travels down, so capability gates, the
/// safe-action policy and every domain refusal apply unchanged. This coordinator cannot drive a
/// rover anywhere an operator could not, and a refusal it earns is one it is told about.
/// </para>
/// <para>
/// <b>Runs at 2 Hz</b>, on the same phase as the air coordinator, but issues a command only when
/// an asset's target actually changes rather than on every pass — <c>DriveTo</c> hands the
/// navigator a persistent goal, so re-sending it twice a second would be audit noise describing no
/// new intent.
/// </para>
/// </remarks>
public sealed partial class GroundSurfaceCoordinator
{
    /// <summary>Metres within which a rover counts as having arrived at its waypoint.</summary>
    /// <remarks>
    /// Comfortably larger than the navigator's own stopping tolerance. A coordinator demanding a
    /// tighter arrival than the platform can achieve would hold an asset on a leg it had already
    /// finished until the timeout, which reads as a vehicle parked on its waypoint.
    /// </remarks>
    public const double GroundArrivalRadiusM = 15.0;

    /// <summary>Metres within which a vessel counts as having arrived at its waypoint.</summary>
    /// <remarks>Wider than the ground radius, because a displacement hull cannot stop on a point.</remarks>
    public const double SurfaceArrivalRadiusM = 32.0;

    /// <summary>Simulated seconds before a leg is abandoned and the next waypoint taken.</summary>
    /// <remarks>
    /// The escape hatch for a leg swept as passable that the platform nonetheless cannot finish —
    /// bogged on a grade it climbs only unladen, or held off a waypoint by wind. Long enough that a
    /// slow rover on its longest leg finishes honestly first.
    /// </remarks>
    public const double WaypointTimeoutSeconds = 150.0;

    /// <summary>Per-asset patrol state, keyed by asset id.</summary>
    private readonly Dictionary<string, PatrolRole> _roles = new(StringComparer.Ordinal);

    /// <summary>Assets under manual control, which this coordinator issues nothing to.</summary>
    /// <remarks>
    /// The same contract <see cref="SwarmCoordinator"/> keeps for drones, and for the same reason:
    /// an operator command — or a safe action that fired and took the asset off autonomy — must not
    /// be overwritten by the next pass half a simulated second later.
    /// </remarks>
    private readonly HashSet<string> _manual = new(StringComparer.Ordinal);

    /// <summary>Routable assets that this pass could not fit any drivable route for.</summary>
    private readonly HashSet<string> _unrouted = new(StringComparer.Ordinal);

    /// <summary>
    /// Assets that are under autonomy but holding because no drivable route could be fitted.
    /// </summary>
    /// <remarks>
    /// An asset that gets no route is skipped silently: it simply never moves. On the smooth
    /// procedural terrain this was written against that is rare enough to go unnoticed, but real
    /// elevation makes it common, and a whole fleet sitting still is indistinguishable from a
    /// working scenario unless something reports it. Exposed rather than logged so a test can
    /// assert on it and a frame can publish it — the point is that it stops being invisible.
    /// <para>
    /// Reset and rebuilt every <see cref="Tick"/>, so it reflects the current pass rather than
    /// accumulating.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string> UnroutedAssetIds => _unrouted;

    /// <summary>One asset's route, its place in it, and any diversion overriding it.</summary>
    private sealed class PatrolRole(IReadOnlyList<Vector3> route)
    {
        /// <summary>Cyclic patrol waypoints; empty when no drivable ring could be fitted.</summary>
        public IReadOnlyList<Vector3> Route { get; } = route;

        /// <summary>Index of the waypoint currently being driven to.</summary>
        public int Index { get; set; }

        /// <summary>Simulated time the current leg was issued at.</summary>
        public double LegStartedAt { get; set; }

        /// <summary>A cross-domain diversion that outranks the patrol until it is reached.</summary>
        public Vector3? Diversion { get; set; }

        /// <summary>Last target actually accepted, so an unchanged one is not re-sent.</summary>
        public Vector3? Commanded { get; set; }
    }

    /// <summary>Takes an asset off autonomous tasking until it is explicitly reattached.</summary>
    /// <param name="assetId">Asset to detach.</param>
    public void DetachManual(string assetId)
    {
        ArgumentException.ThrowIfNullOrEmpty(assetId);
        _manual.Add(assetId);
    }

    /// <summary>Returns an asset to autonomous tasking, discarding its stale route.</summary>
    /// <remarks>
    /// The route is dropped rather than resumed because the asset has been elsewhere since: its
    /// ring is centred on a position it no longer holds, and the leg back to that ring was never
    /// swept from where it now stands. The next pass fits a fresh ring around its real position,
    /// which is both correct and only one fit.
    /// </remarks>
    /// <param name="assetId">Asset to reattach.</param>
    public void AttachAuto(string assetId)
    {
        ArgumentException.ThrowIfNullOrEmpty(assetId);
        _manual.Remove(assetId);
        _roles.Remove(assetId);
    }

    /// <summary>Drops all tasking state — for a scenario change or a session reset.</summary>
    public void ResetState()
    {
        _roles.Clear();
        _manual.Clear();
    }

    /// <summary>
    /// Sends the nearest eligible ground or surface asset to a point of interest, which is how the
    /// three domains come to work one problem rather than three.
    /// </summary>
    /// <remarks>
    /// The air fleet finds things; the surface-bound fleets are what can reach them. A detection
    /// reported from the air diverts the closest platform that can actually get there — the
    /// candidate's route is swept before it is committed, so a survivor spotted across a ravine
    /// diverts the vessel that can round it rather than the rover that would grind into it. An
    /// asset already diverted is left alone, so one contact cannot drag the whole fleet onto a
    /// single point.
    /// </remarks>
    /// <param name="targetEus">Point of interest in the scene frame.</param>
    /// <param name="assets">Fleet to choose from.</param>
    /// <param name="sampler">Environment used to sweep the candidate's route.</param>
    /// <returns>Id of the asset diverted, or <see langword="null"/> when none could reach it.</returns>
    public string? DivertNearest(
        Vector3 targetEus, IReadOnlyList<ISimulatedAsset> assets, IEnvironmentSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(sampler);

        ISimulatedAsset? best = null;
        double bestDistance = double.MaxValue;

        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            if (!IsRoutable(asset) || _manual.Contains(asset.AssetId))
            {
                continue;
            }

            // Already on a diversion: leave it. Otherwise the newest contact always wins and an
            // asset gets pulled off a target it was about to reach.
            if (_roles.TryGetValue(asset.AssetId, out var held) && held.Diversion is not null)
            {
                continue;
            }

            double distance = HorizontalDistance(asset.PositionEus, targetEus);
            if (distance >= bestDistance)
            {
                continue;
            }

            // Sweep before committing: nearest in a straight line is not nearest over the ground.
            if (LegIsPassable(asset, sampler, asset.PositionEus, targetEus))
            {
                best = asset;
                bestDistance = distance;
            }
        }

        if (best is null)
        {
            return null;
        }

        var role = _roles.TryGetValue(best.AssetId, out var existing)
            ? existing
            : Assign(best, sampler);
        role.Diversion = targetEus;
        return best.AssetId;
    }

    /// <summary>Advances every autonomous ground and surface asset by one coordination pass.</summary>
    /// <param name="simTimeSeconds">Simulated time, used for leg timeouts.</param>
    /// <param name="assets">Every asset in the world; other domains are skipped.</param>
    /// <param name="sampler">Environment used to fit and sweep routes.</param>
    /// <param name="dispatch">
    /// Command sink — the room's own dispatch, so commands travel the operator path.
    /// </param>
    public void Tick(
        double simTimeSeconds,
        IReadOnlyList<ISimulatedAsset> assets,
        IEnvironmentSampler sampler,
        Func<SimulatedAssetCommand, AssetCommandResult> dispatch)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(dispatch);

        _unrouted.Clear();

        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            if (!IsRoutable(asset) || _manual.Contains(asset.AssetId))
            {
                continue;
            }

            if (!_roles.TryGetValue(asset.AssetId, out var role))
            {
                role = Assign(asset, sampler);
                role.LegStartedAt = simTimeSeconds;
            }

            // No drivable ring could be fitted from this asset's position — walled in, or aground.
            // It holds rather than being sent along a leg nothing swept; the next scenario or
            // operator move earns it a fresh fit.
            if (role.Route.Count == 0 && role.Diversion is null)
            {
                _unrouted.Add(asset.AssetId);
                continue;
            }

            AdvanceIfDone(asset, role, simTimeSeconds);
            IssueIfChanged(asset, role, simTimeSeconds, dispatch);
        }

        PruneDeparted(assets);
    }

    /// <summary>Fits and records a fresh route for one asset.</summary>
    /// <param name="asset">Asset to route.</param>
    /// <param name="sampler">Environment to sweep against.</param>
    /// <returns>The newly recorded role.</returns>
    private PatrolRole Assign(ISimulatedAsset asset, IEnvironmentSampler sampler)
    {
        var role = new PatrolRole(BuildPatrolRoute(asset, sampler));
        _roles[asset.AssetId] = role;
        return role;
    }

    /// <summary>Retires a diversion or steps to the next waypoint, on arrival or on timeout.</summary>
    /// <param name="asset">Asset being advanced.</param>
    /// <param name="role">Its current tasking state.</param>
    /// <param name="simTimeSeconds">Simulated time.</param>
    private static void AdvanceIfDone(ISimulatedAsset asset, PatrolRole role, double simTimeSeconds)
    {
        var target = role.Diversion ?? role.Route[role.Index];
        double arrival = asset.Domain == AssetDomain.Surface
            ? SurfaceArrivalRadiusM
            : GroundArrivalRadiusM;

        bool arrived = HorizontalDistance(asset.PositionEus, target) <= arrival;
        bool expired = simTimeSeconds - role.LegStartedAt > WaypointTimeoutSeconds;

        if (!arrived && !expired)
        {
            return;
        }

        if (role.Diversion is not null)
        {
            // Reached the point of interest, or gave up on it: fall back to the standing patrol
            // rather than stopping, so a diverted asset rejoins the picture instead of parking
            // wherever the contact happened to be.
            role.Diversion = null;
        }
        else if (role.Route.Count > 0)
        {
            role.Index = (role.Index + 1) % role.Route.Count;
        }

        role.LegStartedAt = simTimeSeconds;
    }

    /// <summary>Sends the current target, but only when it differs from the last one accepted.</summary>
    /// <param name="asset">Asset to task.</param>
    /// <param name="role">Its current tasking state.</param>
    /// <param name="simTimeSeconds">Simulated time.</param>
    /// <param name="dispatch">Command sink.</param>
    private static void IssueIfChanged(
        ISimulatedAsset asset,
        PatrolRole role,
        double simTimeSeconds,
        Func<SimulatedAssetCommand, AssetCommandResult> dispatch)
    {
        Vector3? target = role.Diversion
            ?? (role.Route.Count > 0 ? role.Route[role.Index] : null);
        if (target is not { } destination || role.Commanded == destination)
        {
            return;
        }

        if (MoveCommand(asset, destination) is not { } command)
        {
            return;
        }

        var result = dispatch(command);

        // A refusal must not be recorded as commanded, or the asset would sit on a target it never
        // accepted until the timeout expired. Leaving `Commanded` untouched lets the next pass
        // retry, and the timeout still moves it on when the refusal is permanent.
        if (result.IsAccepted)
        {
            role.Commanded = destination;
            role.LegStartedAt = simTimeSeconds;
        }
    }

    /// <summary>Drops role state for assets that have left the world.</summary>
    /// <remarks>
    /// Without this the dictionary is a slow leak across a long session of spawns and removals, and
    /// a recycled id would inherit a route fitted around a position it never held.
    /// </remarks>
    /// <param name="assets">The assets currently in the world.</param>
    private void PruneDeparted(IReadOnlyList<ISimulatedAsset> assets)
    {
        if (_roles.Count == 0)
        {
            return;
        }

        var live = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < assets.Count; i++)
        {
            live.Add(assets[i].AssetId);
        }

        foreach (var id in _roles.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _roles.Remove(id);
        }
    }

    /// <summary>Whether this coordinator is the one that tasks the given asset.</summary>
    /// <param name="asset">Asset to classify.</param>
    /// <returns><see langword="true"/> for a ground or surface platform.</returns>
    private static bool IsRoutable(ISimulatedAsset asset) =>
        asset.Domain is AssetDomain.Ground or AssetDomain.Surface;

    /// <summary>Plan-view distance between two scene-frame points, ignoring height.</summary>
    /// <remarks>
    /// Horizontal deliberately: a rover on a slope and a vessel in a swell are both metres away
    /// vertically from a waypoint they are standing on, and counting that would stop either from
    /// ever arriving.
    /// </remarks>
    /// <param name="a">First point.</param>
    /// <param name="b">Second point.</param>
    /// <returns>Distance in metres in the horizontal plane.</returns>
    private static double HorizontalDistance(Vector3 a, Vector3 b)
    {
        double dx = a.X - b.X;
        double dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }
}
