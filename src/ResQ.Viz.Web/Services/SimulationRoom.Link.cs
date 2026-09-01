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

namespace ResQ.Viz.Web.Services;

// The command link, as a thing an operator can take away.
//
// AssetWorld owns the lever and the safe-action layer behind it owns the consequences; this file
// is only the room's half of the wiring, and it follows the one rule the rest of the asset
// surface follows — take the room's single lock, return a value, hand out nothing live.
//
// WHY IT IS PER ASSET AND NOT PER SESSION. The room already has a session-wide backhaul kill, and
// it is a different fact: the backhaul is the path out of the mesh, and cutting it says nothing
// about whether any particular vehicle is still being heard. Link loss is per bearer, and the
// whole point of the per-domain fallback policy is that two assets in the same session diverge
// when one of them goes quiet. A session-wide flag could not express that.
public sealed partial class SimulationRoom
{
    /// <summary>Takes one asset's command link down, or brings it back up.</summary>
    /// <remarks>
    /// The operator-facing lever for a link loss. What follows is not decided here: the world's
    /// safe-action sweep sees the silence on its next pass and issues whatever that asset's own
    /// declared behaviour asks for, which is why an air asset returns, a rover stops and a vessel
    /// drifts from the same call.
    /// <para>
    /// Restoring a link moves nothing. The asset is left wherever its fallback took it, under
    /// operator control, because the server cannot know what the operator now wants and guessing
    /// would move a vehicle nobody asked to move.
    /// </para>
    /// <para>
    /// <b>It also decides whether the asset can be commanded at all.</b> The v2 command path reads
    /// this flag as its last gate and refuses anything addressed to an asset whose link is down
    /// with <see cref="AssetLinkReasons.Unreachable"/> — a command it cannot hear must not come
    /// back acknowledged. That gate keeps no state of its own, so this method is the whole of the
    /// condition: restoring the link makes the asset commandable again with nothing left over.
    /// </para>
    /// <para>
    /// The warning logged here is the world's own record that the flag moved, and it is the only
    /// one when the lever is pulled from outside HTTP. A change made through the REST route also
    /// lands on the session's decision trail, carrying the actor, the trace id and the lease that
    /// was in force — see <c>SimV2Controller.Link.cs</c>.
    /// </para>
    /// </remarks>
    /// <param name="assetId">Asset whose link is changing.</param>
    /// <param name="available">False to hold the link down, true to restore it.</param>
    /// <param name="changed"><see langword="true"/> when this call actually changed the link's state.</param>
    /// <returns><see langword="true"/> when the session holds such an asset.</returns>
    public bool TrySetAssetLinkAvailable(string assetId, bool available, out bool changed)
    {
        changed = false;

        if (string.IsNullOrWhiteSpace(assetId))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_assets.TryGet(assetId, out _))
            {
                return false;
            }

            changed = _assets.SetLinkAvailable(assetId, available);
        }

        Touch();

        if (changed)
        {
            _logger.LogWarning(
                "[room {RoomId}] Command link for asset {AssetId} is now {LinkState}.",
                Id, LogSafe(assetId), available ? "up" : "DOWN");
        }

        return true;
    }

    /// <summary>Whether one asset's command link is currently up.</summary>
    /// <remarks>
    /// Answers the link the operator controls, not the link the asset reports. An asset publishes
    /// its own connectivity from inside its telemetry and has no way to know it is being ignored,
    /// so the two are different questions and only this one has an operator on the other end.
    /// </remarks>
    /// <param name="assetId">Asset to ask about.</param>
    /// <param name="available"><see langword="true"/> unless the link is being held down.</param>
    /// <returns><see langword="true"/> when the session holds such an asset.</returns>
    public bool TryGetAssetLinkAvailable(string assetId, out bool available)
    {
        available = true;

        if (string.IsNullOrWhiteSpace(assetId))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_assets.TryGet(assetId, out _))
            {
                return false;
            }

            available = _assets.IsLinkAvailable(assetId);
        }

        return true;
    }
}

/// <summary>Stable codes naming what happened to a command link, and what it stopped.</summary>
/// <remarks>
/// Same convention as <see cref="CommandContractReasons"/>, and in the same layer for the same
/// reason: the lever lives here, so the vocabulary describing it does too, and both the REST
/// boundary and any later consumer read one copy.
/// <para>
/// The <c>link.</c> prefix is load-bearing. It is not a <c>payload.</c> class, so
/// <c>SimV2Controller</c>'s status map answers a refusal with 409 Conflict — the request was well
/// formed and conflicts with the state the session is in, which is exactly what an unreachable
/// asset is. A caller that fixed its payload would get the same answer; what has to change is the
/// link.
/// </para>
/// </remarks>
public static class AssetLinkReasons
{
    /// <summary>A command was refused because the asset's command link is being held down.</summary>
    /// <remarks>
    /// Distinct from every other refusal on the command path, and deliberately so. Nothing is
    /// wrong with the request, the issuer holds the asset, and the asset could execute it — it
    /// simply cannot hear it. An operator interface that showed this as a capability or authority
    /// problem would send somebody hunting for the wrong fix.
    /// </remarks>
    public const string Unreachable = "link.unreachable";

    /// <summary>An operator held an asset's command link down.</summary>
    public const string HeldDown = "link.heldDown";

    /// <summary>An operator brought an asset's command link back up.</summary>
    public const string Restored = "link.restored";

    /// <summary>
    /// A request to cut a link was refused because this deployment reports a live control path.
    /// </summary>
    /// <remarks>
    /// Injecting a fault into a simulation and injecting one into something attached to a vehicle
    /// are different acts, and only the first is what this route is for. Restoring a link is never
    /// refused by this gate — see <c>SimV2Controller.Link.cs</c>.
    /// </remarks>
    public const string FaultInjectionNotPermitted = "link.faultInjectionNotPermitted";
}
