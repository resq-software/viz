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

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>The nearest peer lying in a vehicle's path, and how much clear water or ground is left.</summary>
/// <remarks>
/// Surface-to-surface, not centre-to-centre: both footprint radii are already taken out, so a
/// consumer comparing against a standoff is comparing two quantities of the same kind. Negative
/// means the footprints already overlap, which is a state the simulation can reach — an asset
/// spawned on top of another, or two that closed faster than one step could arrest — and which a
/// consumer must be able to see rather than have clamped away.
/// </remarks>
/// <param name="AssetId">Identifier of the peer, empty when there is none.</param>
/// <param name="GapM">Clear distance along the direction of travel, in metres. Infinite when there is no peer.</param>
/// <param name="PositionEus">The peer's frozen position in the scene frame.</param>
public readonly record struct PeerContact(string AssetId, double GapM, Vector3 PositionEus)
{
    /// <summary>Nothing in the way.</summary>
    public static readonly PeerContact None =
        new(string.Empty, double.PositiveInfinity, Vector3.Zero);

    /// <summary>Whether a peer was found in the corridor at all.</summary>
    public bool Exists => !double.IsPositiveInfinity(GapM);
}

/// <summary>
/// Finds the nearest peer inside the corridor a vehicle is about to drive through.
/// </summary>
/// <remarks>
/// Shared by the ground and surface domains because the geometry is the same in both and the
/// consequence of getting it subtly different in two places is two vehicles that disagree about
/// whether they are about to hit each other.
/// <para>
/// The corridor, not a radius. A disc around the vehicle reports every peer that is close,
/// including the one that has just been safely passed and the one abeam on a parallel track;
/// braking for those is how a fleet deadlocks in an open field. What matters is what lies
/// <em>ahead along the direction of travel</em>, within a swept width both hulls have to fit
/// through, which is what this projects.
/// </para>
/// <para>
/// Reads only the frozen pre-step poses in <see cref="AssetStepContext.Peers"/>, so it is a pure
/// function of the context and cannot depend on registry order — the property the whole
/// pre-step freeze exists to protect. Same-domain only: cross-domain blocking is a documented
/// scope deferral on <see cref="PeerPose"/>, and it needs a shared obstacle representation that
/// does not exist yet.
/// </para>
/// </remarks>
public static class PeerSeparation
{
    /// <summary>Nearest peer ahead within <paramref name="reachM"/>, or <see cref="PeerContact.None"/>.</summary>
    /// <remarks>
    /// The returned gap is measured along the travel direction and has both footprints removed,
    /// so zero means the two hulls are touching.
    /// </remarks>
    /// <param name="peers">Frozen poses of every asset in the world, including the caller's own.</param>
    /// <param name="selfId">The caller's asset id, so it does not find itself.</param>
    /// <param name="domain">Only peers in this domain are considered.</param>
    /// <param name="selfPositionEus">The caller's pre-step position in the scene frame.</param>
    /// <param name="selfFootprintM">The caller's bounding radius, in metres.</param>
    /// <param name="travelHeadingRad">Direction of travel, radians clockwise from true north. Not the heading while reversing.</param>
    /// <param name="reachM">How much clear ground ahead to care about, in metres. Measured as a gap, so it is directly comparable to the value returned.</param>
    /// <returns>The nearest blocking peer, or <see cref="PeerContact.None"/>.</returns>
    public static PeerContact NearestAhead(
        IReadOnlyList<PeerPose> peers,
        string selfId,
        AssetDomain domain,
        Vector3 selfPositionEus,
        double selfFootprintM,
        double travelHeadingRad,
        double reachM)
    {
        ArgumentNullException.ThrowIfNull(peers);

        if (peers.Count == 0 || !double.IsFinite(reachM) || reachM <= 0.0)
        {
            return PeerContact.None;
        }

        // Scene frame: +X is east and -Z is north, which is the convention every probe in the
        // asset code already lays off with.
        double aheadX = Math.Sin(travelHeadingRad);
        double aheadZ = -Math.Cos(travelHeadingRad);

        var nearest = PeerContact.None;

        for (int i = 0; i < peers.Count; i++)
        {
            var peer = peers[i];

            if (peer.Domain != domain || string.Equals(peer.AssetId, selfId, StringComparison.Ordinal))
            {
                continue;
            }

            double deltaX = peer.PositionEus.X - selfPositionEus.X;
            double deltaZ = peer.PositionEus.Z - selfPositionEus.Z;

            double along = (deltaX * aheadX) + (deltaZ * aheadZ);
            if (along <= 0.0)
            {
                continue;
            }

            // Perpendicular offset from the track. Both hulls have to pass through the same
            // corridor, so the width that matters is the sum of the two radii — a peer further
            // off the track than that will be passed clear even though it is close.
            double width = selfFootprintM + peer.FootprintRadiusM;
            double crossX = deltaX - (along * aheadX);
            double crossZ = deltaZ - (along * aheadZ);

            if (((crossX * crossX) + (crossZ * crossZ)) >= width * width)
            {
                continue;
            }

            // Against the gap, not the centre-to-centre range, so the caller's reach means the
            // same thing as the value it gets back. That matters more than it looks: a caller
            // passing its stopping distance shrinks its reach as it slows, and a reach measured
            // centre-to-centre would drop the vehicle it is holding station behind out of the
            // corridor the moment it came to rest — lifting the ceiling, letting it creep in,
            // and oscillating it into the very contact this exists to prevent.
            double gap = along - width;
            if (gap > reachM)
            {
                continue;
            }

            if (!nearest.Exists || gap < nearest.GapM)
            {
                nearest = new PeerContact(peer.AssetId, gap, peer.PositionEus);
            }
        }

        return nearest;
    }
}
