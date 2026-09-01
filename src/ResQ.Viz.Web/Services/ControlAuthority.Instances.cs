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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services;

/// <summary>Reports which asset <i>instance</i>, if any, currently holds an id.</summary>
/// <remarks>
/// The authority's entire view of what exists. It answers with an identity rather than a
/// <see cref="bool"/> so that "removed, and another created under the same id" is
/// distinguishable from "never went away" — the difference between a lease that ends with the
/// vehicle it named and one that silently transfers to that vehicle's replacement.
/// <para>
/// Two rules bind an implementation. It must return the <em>same</em> token for as long as one
/// instance stays registered, or every sweep would revoke leases that are perfectly good; and it
/// must never reuse a token for a later instance, or the confusion this exists to prevent comes
/// straight back. It is called with the authority's lock held, so it must be a cheap lookup that
/// does not call back into the authority.
/// </para>
/// </remarks>
/// <param name="assetId">Asset id to resolve.</param>
/// <returns>The instance token, or null when no asset currently holds that id.</returns>
public delegate string? AssetInstanceProbe(string assetId);

/// <content>
/// Asset instance identity: what a lease is actually bound to, and how the authority asks.
/// <para>
/// Split from the four operations and from the lease lifetime because it answers a question
/// neither of them does — not "who may command this" and not "for how long", but "is the thing
/// in front of us still the thing the lease was taken over". An asset id cannot answer that: ids
/// are chosen by whoever spawns a vehicle, and one comes back the moment somebody reuses the
/// name.
/// </para>
/// </content>
public sealed partial class ControlAuthority
{
    /// <summary>Instance token stood in for callers that can only answer "does this id exist".</summary>
    /// <remarks>
    /// One constant for every asset, so an id-only probe behaves exactly as it did before
    /// instances existed: a lease survives as long as the id does. A visible token rather than an
    /// empty string, so a lease carrying it says plainly — on the wire and in a test failure —
    /// that nothing bound it to a particular vehicle.
    /// </remarks>
    private const string UnidentifiedInstance = "instance.unidentified";

    /// <summary>Adapts an existence check to a probe that cannot tell instances apart.</summary>
    /// <remarks>
    /// A static factory rather than a lambda in a constructor body, because a delegating
    /// constructor runs it before any body: passing null still fails with the argument name the
    /// caller wrote, instead of surfacing later as a null reference on the first acquire.
    /// </remarks>
    /// <param name="assetExists">Existence check to adapt.</param>
    /// <returns>A probe answering with one shared token for every asset that exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assetExists"/> is null.</exception>
    private static AssetInstanceProbe Unidentified(Func<string, bool> assetExists)
    {
        ArgumentNullException.ThrowIfNull(assetExists);
        return assetId => assetExists(assetId) ? UnidentifiedInstance : null;
    }

    /// <summary>The instance currently holding an id, or null when nothing does.</summary>
    /// <remarks>
    /// Every presence decision goes through here, so a blank id is refused without the probe ever
    /// seeing it, and a probe that answers with blank text reads as "nothing there" rather than
    /// as an instance whose token happens to be empty — which would compare equal to the next
    /// such answer and make two different assets look like one.
    /// <para>Must be called with <c>_gate</c> held.</para>
    /// </remarks>
    /// <param name="assetId">Asset id to resolve.</param>
    /// <returns>The instance token, or null.</returns>
    private string? ResolveInstance(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        var instance = _assetInstance(assetId);
        return string.IsNullOrWhiteSpace(instance) ? null : instance;
    }

    /// <summary>Whether the asset now registered under an id is the one a lease was taken over.</summary>
    /// <remarks>
    /// The comparison every sweep turns on. A lease named a vehicle, not a string, so an id that
    /// has been recycled answers <see langword="false"/> here and the lease ends, rather than
    /// following the name onto whatever was spawned next.
    /// <para>Must be called with <c>_gate</c> held.</para>
    /// </remarks>
    /// <param name="assetId">Asset id the lease covers.</param>
    /// <param name="lease">Lease being checked.</param>
    /// <returns><see langword="false"/> when the asset is gone, or has been replaced.</returns>
    private bool StillNamesTheSameInstance(string assetId, ControlLease lease) =>
        ResolveInstance(assetId) is { } instance
        && string.Equals(instance, lease.AssetInstanceId, StringComparison.Ordinal);
}
