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
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Services;

/// <content>
/// Patrol-route construction for the two surface-bound domains.
/// <para>
/// Every candidate leg is put through the <em>same</em> sweep the operator path uses —
/// <see cref="Traversability.CheckRoute"/> for a rover, <see cref="WaterConstraints.CheckRoute"/>
/// for a vessel — so a route this file emits is one the platform's own command gate will accept.
/// That ordering is the point. The asset-side gate probes only the <i>destination</i>, because a
/// click-to-drive target is all an operator gives it; a coordinator that proposed a destination on
/// good ground across a ravine would have every command accepted and every rover stopped against
/// the first refusal mid-leg, with nothing on the wire saying why. Sweeping the whole leg here
/// means a route is drivable end to end, or it is not offered.
/// </para>
/// </content>
public sealed partial class GroundSurfaceCoordinator
{
    /// <summary>Bearings tried when fitting a patrol ring, in degrees clockwise from north.</summary>
    /// <remarks>
    /// Twelve gives a 30° step: fine enough that a valley or a shoreline usually leaves several
    /// legs standing, coarse enough that a full fit costs a bounded number of sweeps per asset —
    /// and it is paid once, on assignment, rather than on every pass.
    /// </remarks>
    private static readonly double[] RingBearingsDeg =
        [0, 30, 60, 90, 120, 150, 180, 210, 240, 270, 300, 330];

    /// <summary>Ring radii tried, largest first, as a fraction of the nominal leg length.</summary>
    /// <remarks>
    /// A rover boxed in by terrain, or a vessel in a narrow inlet, has no route at the nominal
    /// radius and a perfectly good one at a third of it. Shrinking is what keeps such a platform
    /// working its own patch instead of parked for the session, and the smallest rung is still far
    /// enough that an arrival is a real transit rather than a twitch.
    /// </remarks>
    private static readonly double[] RadiusFractions = [1.0, 0.6, 0.35];

    /// <summary>Seconds of travel a patrol leg is sized to take at cruise speed.</summary>
    /// <remarks>
    /// The ring radius is derived from this and the platform's own cruise speed rather than fixed
    /// in metres, so a 2 m/s rover and a 6 m/s vessel both get legs that read as purposeful
    /// movement instead of the rover crawling a single leg for the whole session.
    /// </remarks>
    private const double TargetLegSeconds = 45.0;

    /// <summary>Smallest ring radius worth patrolling, in metres.</summary>
    private const double MinRingRadiusM = 40.0;

    /// <summary>Largest ring radius in metres, so a fast vessel stays in its own operating area.</summary>
    private const double MaxRingRadiusM = 260.0;

    /// <summary>Fewest waypoints that make a patrol rather than an out-and-back twitch.</summary>
    private const int MinRouteWaypoints = 3;

    /// <summary>Fraction of <see cref="MotionConstraints.MaxSpeedMps"/> treated as cruise.</summary>
    /// <remarks>
    /// The envelope is not the cruise: sizing legs at the ceiling would size them for a speed the
    /// platform reaches only unladen on flat ground, so a rover would be handed legs it then spent
    /// the session failing to finish and timing out on.
    /// </remarks>
    private const double CruiseFractionOfMax = 0.6;

    /// <summary>
    /// Fits a closed patrol ring around an asset's current position, keeping only the waypoints
    /// whose inbound leg its own domain says it may traverse.
    /// </summary>
    /// <param name="asset">Asset to route; must be a ground or surface platform.</param>
    /// <param name="sampler">Environment to sweep each candidate leg against.</param>
    /// <returns>
    /// A cyclic route of at least <see cref="MinRouteWaypoints"/> waypoints, or an empty list when
    /// no radius produced enough drivable legs — a rover walled in on every bearing, or a vessel
    /// with no navigable water within reach.
    /// </returns>
    private static IReadOnlyList<Vector3> BuildPatrolRoute(
        ISimulatedAsset asset, IEnvironmentSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(sampler);

        var anchor = asset.PositionEus;
        double nominal = NominalRadiusM(asset);

        foreach (double fraction in RadiusFractions)
        {
            double radius = Math.Max(MinRingRadiusM, nominal * fraction);
            var route = FitRing(asset, sampler, anchor, radius);
            if (route.Count >= MinRouteWaypoints)
            {
                return route;
            }
        }

        return [];
    }

    /// <summary>Ring radius for one platform, from its cruise speed and the target leg duration.</summary>
    /// <param name="asset">Asset whose motion envelope sets the scale.</param>
    /// <returns>A radius in <c>[<see cref="MinRingRadiusM"/>, <see cref="MaxRingRadiusM"/>]</c>.</returns>
    private static double NominalRadiusM(ISimulatedAsset asset)
    {
        double cruise = Math.Max(0.5, asset.Descriptor.Motion.MaxSpeedMps * CruiseFractionOfMax);
        return Math.Clamp(cruise * TargetLegSeconds, MinRingRadiusM, MaxRingRadiusM);
    }

    /// <summary>
    /// Walks the candidate bearings once, keeping a waypoint when the leg from the previously kept
    /// one reaches it, so the survivors form a circuit that is drivable in the order flown.
    /// </summary>
    /// <remarks>
    /// Validating each leg against its <i>predecessor</i> rather than against the anchor is what
    /// makes the result a circuit rather than a star: the asset never returns to the middle. The
    /// closing leg back to the first waypoint is swept too, so the loop can be run indefinitely
    /// without one pass nobody checked.
    /// </remarks>
    /// <param name="asset">Asset to route.</param>
    /// <param name="sampler">Environment to sweep against.</param>
    /// <param name="anchor">Centre of the ring — the asset's position at assignment.</param>
    /// <param name="radiusM">Ring radius in metres.</param>
    /// <returns>The surviving waypoints in circuit order, or empty when too few survived.</returns>
    private static List<Vector3> FitRing(
        ISimulatedAsset asset, IEnvironmentSampler sampler, Vector3 anchor, double radiusM)
    {
        var kept = new List<Vector3>(RingBearingsDeg.Length);
        var from = anchor;

        foreach (double bearingDeg in RingBearingsDeg)
        {
            var candidate = OnRing(anchor, radiusM, bearingDeg, sampler);
            if (LegIsPassable(asset, sampler, from, candidate))
            {
                kept.Add(candidate);
                from = candidate;
            }
        }

        // The circuit only closes if the last kept waypoint can reach the first. When it cannot,
        // dropping the tail beats shipping a loop with one leg nobody swept: the asset still
        // patrols, over a slightly shorter arc.
        while (kept.Count >= MinRouteWaypoints
            && !LegIsPassable(asset, sampler, kept[^1], kept[0]))
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept.Count >= MinRouteWaypoints ? kept : [];
    }

    /// <summary>Point on the ring at a bearing, dropped onto the surface beneath it.</summary>
    /// <param name="anchor">Ring centre.</param>
    /// <param name="radiusM">Ring radius in metres.</param>
    /// <param name="bearingDeg">Bearing clockwise from true north, in degrees.</param>
    /// <param name="sampler">Environment supplying the terrain height.</param>
    /// <returns>A scene-frame position.</returns>
    private static Vector3 OnRing(
        Vector3 anchor, double radiusM, double bearingDeg, IEnvironmentSampler sampler)
    {
        var offset = CoordinateFrames.BearingToEusVector(bearingDeg * Math.PI / 180.0, radiusM);
        float x = anchor.X + offset.X;
        float z = anchor.Z + offset.Z;
        return new Vector3(x, (float)sampler.GetElevation(x, z), z);
    }

    /// <summary>Whether one leg passes the sweep its domain applies to a whole route.</summary>
    /// <param name="asset">Asset the leg is for.</param>
    /// <param name="sampler">Environment to sweep against.</param>
    /// <param name="fromEus">Leg start in the scene frame.</param>
    /// <param name="toEus">Leg end in the scene frame.</param>
    /// <returns><see langword="true"/> when the whole leg is passable, not merely its endpoint.</returns>
    private static bool LegIsPassable(
        ISimulatedAsset asset, IEnvironmentSampler sampler, Vector3 fromEus, Vector3 toEus) =>
        asset switch
        {
            GroundAsset ground =>
                Traversability.CheckRoute(ground.Profile, fromEus, toEus, sampler).IsTraversable,
            SurfaceAsset surface =>
                WaterConstraints.CheckRoute(
                    VesselWaterProfile.From(surface.Profile), fromEus, toEus, sampler).IsNavigable,
            // Anything else is not this coordinator's to route. False rather than true keeps an
            // unrecognised platform parked instead of tasked along a leg nobody swept.
            _ => false,
        };

    /// <summary>The command that sends one asset to a point, in its own domain's vocabulary.</summary>
    /// <param name="asset">Asset to task.</param>
    /// <param name="targetEus">Destination in the scene frame.</param>
    /// <returns>
    /// The command, or <see langword="null"/> for a domain this coordinator does not drive.
    /// </returns>
    private static SimulatedAssetCommand? MoveCommand(ISimulatedAsset asset, Vector3 targetEus)
    {
        var kind = asset.Domain switch
        {
            AssetDomain.Ground => AssetCommandKind.DriveTo,
            AssetDomain.Surface => AssetCommandKind.TransitTo,
            _ => AssetCommandKind.Unspecified,
        };

        if (kind == AssetCommandKind.Unspecified)
        {
            return null;
        }

        // LocalEus with no origin id: the frame the routes were sampled in, and the one the
        // executor resolves without a geodetic anchor. Identity orientation, because these carry a
        // destination rather than an attitude — the navigator picks the heading that gets there.
        var pose = new FramedPose(
            CoordinateFrame.LocalEus, OriginId: null, targetEus, Quaternion.Identity);

        return new SimulatedAssetCommand(
            kind, asset.AssetId, Target: pose, CommandId: Guid.NewGuid());
    }
}
