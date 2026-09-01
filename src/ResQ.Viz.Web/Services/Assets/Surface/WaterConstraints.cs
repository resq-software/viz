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

namespace ResQ.Viz.Web.Services.Assets.Surface;

/// <summary>The navigable-water mask, the shoreline constraint and the route preview.</summary>
/// <remarks>
/// The mask is derived from the water-surface elevation and the bathymetric bed that
/// <see cref="EnvironmentSample"/> reports, by way of <see cref="UnderKeelClearance"/>. There is
/// deliberately no second notion of "is this navigable" here: the depth threshold is
/// <see cref="UnderKeelClearanceState.MinimumNavigableDepthM"/> and nothing else.
/// <para>
/// Every function is pure and takes samples rather than a sampler where it can, so the mask can
/// be exercised with literals. <see cref="CheckRoute"/> is the exception; it needs a sampler,
/// and it must be called with the owning room's lock held, because the sampler reads a terrain
/// height field another request may be replacing.
/// </para>
/// <para>
/// Advisory decision support. These are simulated hulls over a procedural bed: nothing here
/// asserts conformance with any navigation regulation, and nothing certifies autonomous
/// navigation.
/// </para>
/// </remarks>
public static class WaterConstraints
{
    /// <summary>Upper bound on samples per route, whatever its length.</summary>
    /// <remarks>
    /// A constant, so the bound stays a function of geometry alone. Past it the spacing widens
    /// rather than the count growing.
    /// </remarks>
    public const int MaxRouteSamples = 512;

    /// <summary>Risk assigned to a point the environment could not answer for.</summary>
    /// <remarks>
    /// Halfway. Unsurveyed water is neither clear nor refused, and scoring it at either end
    /// would make a planner either avoid it superstitiously or ignore it entirely.
    /// </remarks>
    private const double UnknownRiskWeight = 0.5;

    /// <summary>Fraction of hull length between consecutive route samples.</summary>
    /// <remarks>
    /// Half a hull length, so a shoal no larger than the vessel cannot fall between two samples
    /// unnoticed. Sampling at a full hull length would let exactly the obstruction that matters
    /// most slip through the gap.
    /// </remarks>
    private const double RouteSpacingFractionOfLength = 0.5;

    /// <summary>Finest route sampling spacing, in metres, whatever the hull.</summary>
    private const double MinRouteSampleSpacingM = 1.0;

    /// <summary>Coarsest route sampling spacing, in metres, whatever the hull.</summary>
    /// <remarks>
    /// Caps how much of the bed a very long hull is allowed to step over. Beyond this the
    /// preview stops describing the water it crosses.
    /// </remarks>
    private const double MaxRouteSampleSpacingM = 25.0;

    /// <summary>Finest terrain-normal half-spacing used while probing, in metres.</summary>
    private const double MinNormalSpacingM = 1.0;

    /// <summary>Stable machine-readable code for a reason.</summary>
    /// <param name="reason">Reason to encode.</param>
    /// <returns>A dotted lower-case token, e.g. <c>water.blocked.shallow</c>.</returns>
    public static string ReasonCode(WaterBlockReason reason) => reason switch
    {
        WaterBlockReason.DryLand => "water.blocked.land",
        WaterBlockReason.InsufficientDepth => "water.blocked.shallow",
        WaterBlockReason.Grounded => "water.blocked.aground",
        WaterBlockReason.ProhibitedZone => "water.blocked.zone",
        WaterBlockReason.MarginalDepth => "water.caution.shallow",
        WaterBlockReason.ZoneSpeedLimit => "water.caution.zone",
        WaterBlockReason.NoWaterData => "water.unknown.no-data",
        _ => "water.clear",
    };

    /// <summary>Distance between consecutive route samples for a hull, in metres.</summary>
    /// <param name="profile">Hull envelope to derive for.</param>
    /// <returns>Spacing in metres, clamped to the bounds documented above.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static double RouteSampleSpacingM(VesselWaterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        double length = double.IsFinite(profile.LengthOverallM) ? Math.Max(0.0, profile.LengthOverallM) : 0.0;
        return Math.Clamp(
            length * RouteSpacingFractionOfLength, MinRouteSampleSpacingM, MaxRouteSampleSpacingM);
    }

    /// <summary>Samples needed to cover a segment, derived from geometry and nothing else.</summary>
    /// <remarks>
    /// A function of length and spacing only. Nothing about the vessel's state, the water it is
    /// in or what an earlier sample found may reach this, or two replays of one scenario would
    /// do different amounts of work.
    /// </remarks>
    /// <param name="lengthM">Segment length in metres.</param>
    /// <param name="spacingM">Requested spacing in metres.</param>
    /// <returns>A count in <c>[2, <see cref="MaxRouteSamples"/>]</c>, or 1 for a degenerate segment.</returns>
    public static int SampleCount(double lengthM, double spacingM)
    {
        if (!double.IsFinite(lengthM) || lengthM <= CoordinateFrames.MinHorizontalMagnitude)
        {
            return 1;
        }

        double spacing = double.IsFinite(spacingM) && spacingM > 0.0 ? spacingM : MinRouteSampleSpacingM;
        return (int)Math.Clamp(Math.Ceiling(lengthM / spacing) + 1.0, 2.0, MaxRouteSamples);
    }

    /// <summary>Classifies one point of water for one hull.</summary>
    /// <remarks>
    /// A prohibited zone is tested before the depth: an operator-declared no-go area is a
    /// decision about where a vessel may go, and water it could physically float in does not
    /// overrule it. Everything after that comes from
    /// <see cref="UnderKeelClearance.Evaluate(VesselWaterProfile, EnvironmentSample)"/>, so the
    /// mask and the clearance warning can never disagree about the same hull over the same bed.
    /// </remarks>
    /// <param name="profile">Hull envelope to classify for.</param>
    /// <param name="sample">Environment sampled at the point.</param>
    /// <returns>The classification and the quantities behind it.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WaterSample Evaluate(VesselWaterProfile profile, EnvironmentSample sample)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sample);

        var clearance = UnderKeelClearance.Evaluate(profile, sample);
        var (navigability, reason) = Classify(sample, clearance);

        return new WaterSample(
            PositionEus: sample.PositionEus,
            Class: navigability,
            Reason: reason,
            IsWater: sample.IsWater,
            WaterSurfaceElevationM: sample.WaterSurfaceElevationM,
            BedElevationM: sample.TerrainElevationM,
            Clearance: clearance,
            AdvisorySpeedLimitMps: ZoneSpeedLimitMps(sample.Zones),
            RiskWeight: RiskWeight(navigability, clearance));
    }

    /// <summary>Whether a hull may plan to occupy a point.</summary>
    /// <remarks>
    /// A planning verdict and only that. The mask is cut at draft plus the advisory margin, so
    /// water a hull would float in perfectly well is refused once it is inside that margin — the
    /// refusal is conservative routing, not an assertion that anything has touched the bed. Ask
    /// <see cref="ContactAt"/> for that.
    /// </remarks>
    /// <param name="profile">Hull envelope to classify for.</param>
    /// <param name="sample">Environment sampled at the point.</param>
    /// <returns><see langword="true"/> when the point is navigable or merely cautionary.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static bool IsNavigable(VesselWaterProfile profile, EnvironmentSample sample) =>
        Evaluate(profile, sample).IsNavigable;

    /// <summary>What the hull is doing about the bed at an already-evaluated point.</summary>
    /// <remarks>
    /// The counterpart to <see cref="WaterSample.IsNavigable"/>, and pointedly <b>not</b> its
    /// negation. The two answer different questions, and a caller that needs one must not reach
    /// for the other:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="WaterSample.IsNavigable"/> is a <em>planning</em> verdict — may a hull
    ///     occupy this point with its advisory margin intact. It refuses water merely inside that
    ///     margin, and water under a prohibited zone, at neither of which is the hull touching
    ///     anything.
    ///   </description></item>
    ///   <item><description>
    ///     This is a <em>physical</em> claim about the hull and the bed, and only the clearance
    ///     band is entitled to make it.
    ///   </description></item>
    /// </list>
    /// Deriving grounding from the mask reports a vessel floating a hand's breadth inside its own
    /// advisory — under way, answering the helm, merely wanting more water — as having run
    /// aground; and reports a vessel turned back at the edge of a no-go zone, in any depth of
    /// water at all, as aground too. Those are different situations from a hull on the ground,
    /// and an operator told the wrong one acts on the wrong one.
    /// <para>
    /// Advisory throughout: the bed is a procedural height field, so this describes a simulated
    /// hull over a modelled bottom and asserts nothing about the safety of a real passage.
    /// </para>
    /// </remarks>
    /// <param name="sample">Point already evaluated for a hull.</param>
    /// <returns>Whether that hull is afloat, afloat inside its margin, or on the bed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is null.</exception>
    public static HullContactState ContactAt(WaterSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample.Clearance.Contact;
    }

    /// <summary>Applies the water mask to a proposed move, and reports what it met.</summary>
    /// <remarks>
    /// The shoreline constraint. A move into non-navigable water from navigable water is
    /// refused: the vessel is held at its origin and a <see cref="ShorelineContact"/> is
    /// produced for the caller to raise once, on that edge.
    /// <para>
    /// A vessel that is <b>already</b> aground or ashore is treated differently, and this is the
    /// point of the whole function. Refusing its moves as well would make grounding permanent —
    /// every direction out of a beach starts on the beach — so from a non-navigable position a
    /// move is allowed whenever it reaches navigable water <em>or</em> the bed under the
    /// destination is no higher than the bed under the origin. A vessel can therefore always
    /// retreat downhill towards the water, and can never drive further up the beach. The trade
    /// is that it may travel a short way across level ground while recovering, which is the
    /// price of not stranding it.
    /// </para>
    /// <para>
    /// This reports a condition, not an event. Events belong on transitions, which only the
    /// caller — holding the previous step's state — can detect.
    /// </para>
    /// </remarks>
    /// <param name="profile">Hull envelope.</param>
    /// <param name="origin">Environment sampled at the vessel's current position.</param>
    /// <param name="destination">Environment sampled at the proposed position.</param>
    /// <param name="speedMps">Speed along the direction of travel, in metres per second, for the contact record.</param>
    /// <returns>The accepted position, whether the move was refused, and any contact.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static WaterMotionResolution ResolveMotion(
        VesselWaterProfile profile,
        EnvironmentSample origin,
        EnvironmentSample destination,
        double speedMps)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);

        var from = Evaluate(profile, origin);
        var to = Evaluate(profile, destination);

        bool recovering = !from.IsNavigable
            && destination.TerrainElevationM <= origin.TerrainElevationM;

        if (to.IsNavigable || recovering)
        {
            return new WaterMotionResolution(to.PositionEus, IsBlocked: false, ShorelineContact.None, to);
        }

        double impact = double.IsFinite(speedMps) ? Math.Abs(speedMps) : 0.0;

        return new WaterMotionResolution(
            from.PositionEus,
            IsBlocked: true,
            new ShorelineContact(true, to.Reason, impact, from.PositionEus),
            from);
    }

    /// <summary>
    /// Removes the part of a refused move that drives further into shoaling water, leaving the
    /// part that runs along the edge or away from it.
    /// </summary>
    /// <remarks>
    /// <b>Why a refused move is not simply cancelled.</b> A hull held on a bank by a set sits
    /// exactly on the boundary the mask drew: the last position it was permitted is, by
    /// construction, the shallowest navigable one. From there <em>every</em> move carries some
    /// inshore component while the set runs, so cancelling whole moves pins the vessel for good —
    /// it can never build way, and a hull that cannot build way can never turn its bow off the
    /// bank either. That is a vessel which accepts every recovery order and executes none.
    /// <para>
    /// The constraint a shoal actually imposes is one-sided: a hull may not be pushed further up
    /// the slope, but nothing stops it running along the contour or back down it. So the move is
    /// projected onto the bed contour — the inshore component along the upslope direction is
    /// dropped and the rest is kept — which is the ordinary treatment of a contact constraint and
    /// is what lets a pinned hull work itself off under helm and throttle.
    /// </para>
    /// <para>
    /// The upslope direction is the horizontal part of the terrain normal, reversed. Where the
    /// bed is level that vector vanishes, there is no slope to slide along, and the move is
    /// returned unchanged for the caller to refuse outright. A move that is not driving inshore
    /// at all is likewise returned unchanged: it was refused for some other reason — dry land, a
    /// prohibited zone — and deflecting it would invent a way around a refusal that is not a
    /// slope.
    /// </para>
    /// <para>
    /// Pure geometry. Whether the deflected position is one the hull may actually occupy is the
    /// caller's question, because only the caller can sample it.
    /// </para>
    /// </remarks>
    /// <param name="fromEus">Position the move started at, in the scene frame.</param>
    /// <param name="toEus">Position the move was refused at, in the scene frame.</param>
    /// <param name="terrainNormalEus">Unit up-normal of the bed at the origin, in the scene frame.</param>
    /// <returns>
    /// The deflected destination, at <paramref name="fromEus"/>'s elevation; equal to
    /// <paramref name="toEus"/> when there is nothing to deflect against.
    /// </returns>
    public static Vector3 DeflectAlongEdge(Vector3 fromEus, Vector3 toEus, Vector3 terrainNormalEus)
    {
        double upEast = -terrainNormalEus.X;
        double upSouth = -terrainNormalEus.Z;
        double length = Math.Sqrt((upEast * upEast) + (upSouth * upSouth));

        if (!double.IsFinite(length) || length <= 0.0)
        {
            return toEus;
        }

        upEast /= length;
        upSouth /= length;

        double deltaEast = toEus.X - fromEus.X;
        double deltaSouth = toEus.Z - fromEus.Z;
        double inshore = (deltaEast * upEast) + (deltaSouth * upSouth);

        if (!double.IsFinite(inshore) || inshore <= 0.0)
        {
            return toEus;
        }

        return new Vector3(
            (float)(fromEus.X + deltaEast - (inshore * upEast)),
            fromEus.Y,
            (float)(fromEus.Z + deltaSouth - (inshore * upSouth)));
    }

    /// <summary>Sweeps a straight segment and reports whether a hull may transit it.</summary>
    /// <remarks>
    /// Every sample is taken, always, even once one has already blocked the route. Stopping at
    /// the first refusal would make the number of terrain queries a function of the water, so
    /// two replays of one scenario would do different amounts of work. The first blocking sample
    /// is recorded instead, which is what an early exit was wanted for anyway.
    /// <para>
    /// A straight-line sweep, not a search: it answers "may this hull transit from here to there
    /// in a straight line", which is what a click-to-transit target needs. Finding a way around
    /// a refusal is a planner's job and is not attempted here.
    /// </para>
    /// <para>
    /// Probes are taken at the sampler's water-surface elevation, where a vessel actually sits,
    /// so the wind read into each sample is the wind at the waterline.
    /// </para>
    /// </remarks>
    /// <param name="profile">Hull envelope to check for.</param>
    /// <param name="startEus">Segment start in the scene frame; the vertical component is ignored.</param>
    /// <param name="endEus">Segment end in the scene frame; the vertical component is ignored.</param>
    /// <param name="sampler">Environment sampler to query along the segment. Call under the owning room's lock.</param>
    /// <returns>What the sweep found.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static RouteWaterCheck CheckRoute(
        VesselWaterProfile profile, Vector3 startEus, Vector3 endEus, IEnvironmentSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sampler);

        var delta = new Vector3(endEus.X - startEus.X, 0f, endEus.Z - startEus.Z);
        double length = delta.Length();
        int count = SampleCount(length, RouteSampleSpacingM(profile));
        double spacing = count > 1 ? length / (count - 1) : 0.0;

        double normalSpacing = Math.Max(
            MinNormalSpacingM,
            double.IsFinite(profile.BeamM) ? profile.BeamM * 0.5 : 0.0);
        float surfaceY = (float)sampler.SeaLevelM;

        var sweep = new RouteSweep(spacing);

        for (int i = 0; i < count; i++)
        {
            float t = count > 1 ? (float)i / (count - 1) : 0f;
            float x = startEus.X + (delta.X * t);
            float z = startEus.Z + (delta.Z * t);
            var environment = sampler.Sample(new Vector3(x, surfaceY, z), normalSpacing);

            sweep.Accumulate(Evaluate(profile, environment), spacing * i);
        }

        return sweep.ToResult(length, count, spacing);
    }

    /// <summary>Turns an evaluated clearance and its zones into a planning verdict.</summary>
    /// <remarks>
    /// Three distinct situations legitimately map onto <see cref="WaterNavigability.Blocked"/>: a
    /// no-go zone, water inside the hull's advisory margin, and a bed the hull is already on.
    /// They stay distinguishable through <see cref="WaterSample.Reason"/> and through
    /// <see cref="ContactAt"/>, and a consumer that flattens them back into one bit is inventing
    /// a grounding out of a routing refusal. <see cref="WaterBlockReason.InsufficientDepth"/> in
    /// particular means "shallower than draft plus margin" — the hull is still afloat there.
    /// </remarks>
    private static (WaterNavigability Class, WaterBlockReason Reason) Classify(
        EnvironmentSample sample, UnderKeelClearanceState clearance)
    {
        for (int i = 0; i < sample.Zones.Count; i++)
        {
            if (sample.Zones[i].IsEntryProhibited)
            {
                return (WaterNavigability.Blocked, WaterBlockReason.ProhibitedZone);
            }
        }

        if (!sample.IsWater)
        {
            return (WaterNavigability.Blocked, WaterBlockReason.DryLand);
        }

        // Read off the clearance band rather than re-tested against the depth. Two copies of
        // "is there enough water here" is how a mask comes to admit a hull the clearance warning
        // is simultaneously calling unsafe.
        return clearance.Class switch
        {
            UnderKeelClearanceClass.Unknown => (WaterNavigability.Unknown, WaterBlockReason.NoWaterData),
            UnderKeelClearanceClass.Aground => (WaterNavigability.Blocked, WaterBlockReason.Grounded),
            UnderKeelClearanceClass.Critical =>
                (WaterNavigability.Blocked, WaterBlockReason.InsufficientDepth),
            UnderKeelClearanceClass.Marginal =>
                (WaterNavigability.Cautionary, WaterBlockReason.MarginalDepth),
            _ => ZoneSpeedLimitMps(sample.Zones) is not null
                ? (WaterNavigability.Cautionary, WaterBlockReason.ZoneSpeedLimit)
                : (WaterNavigability.Navigable, WaterBlockReason.None),
        };
    }

    /// <summary>Tightest advisory speed ceiling the zones at a point impose, or null.</summary>
    private static double? ZoneSpeedLimitMps(IReadOnlyList<EnvironmentZone> zones)
    {
        double? limit = null;

        for (int i = 0; i < zones.Count; i++)
        {
            if (zones[i].SpeedLimitMps is { } value && double.IsFinite(value) && value >= 0.0)
            {
                limit = limit is { } current ? Math.Min(current, value) : value;
            }
        }

        return limit;
    }

    /// <summary>Advisory risk of a point as a fraction in 0–1.</summary>
    /// <remarks>
    /// One at a refusal, and otherwise a linear ramp from one at zero clearance down to zero at
    /// the cautionary threshold, so a route that skirts a shoal scores above one that stays in
    /// open water without either being refused. Relative only: the number is for comparing
    /// routes, never for asserting that a given route is safe.
    /// </remarks>
    private static double RiskWeight(WaterNavigability navigability, UnderKeelClearanceState clearance)
    {
        if (navigability == WaterNavigability.Blocked)
        {
            return 1.0;
        }

        if (navigability == WaterNavigability.Unknown)
        {
            return UnknownRiskWeight;
        }

        double cautionary = clearance.SafeMarginM * UnderKeelClearance.CautionaryMarginMultiple;
        return cautionary > 0.0
            ? 1.0 - Math.Clamp(clearance.ClearanceM / cautionary, 0.0, 1.0)
            : 0.0;
    }

    /// <summary>Ranks classifications so the worst along a route can be tracked.</summary>
    /// <remarks>
    /// Explicit rather than leaning on the enum's declaration order, so reordering
    /// <see cref="WaterNavigability"/> for readability cannot silently change what a route
    /// reports. Unknown outranks cautionary: unsurveyed water deserves more attention than water
    /// already known to be merely tight.
    /// </remarks>
    private static int Severity(WaterNavigability value) => value switch
    {
        WaterNavigability.Blocked => 3,
        WaterNavigability.Unknown => 2,
        WaterNavigability.Cautionary => 1,
        _ => 0,
    };

    /// <summary>Running totals for one straight-segment sweep.</summary>
    /// <remarks>
    /// A mutable accumulator confined to a single call: it never escapes
    /// <see cref="CheckRoute"/>, so the sweep stays a pure function of its arguments while the
    /// loop body stays readable.
    /// </remarks>
    private sealed class RouteSweep(double spacingM)
    {
        private WaterNavigability _worstClass = WaterNavigability.Navigable;
        private WaterBlockReason _blockingReason = WaterBlockReason.None;
        private Vector3? _blockingPoint;
        private double _blockingDistanceM;
        private double _shallowestDepthM = double.PositiveInfinity;
        private double _minimumClearanceM = double.PositiveInfinity;
        private double _risk;
        private bool _hasPrevious;

        /// <summary>Folds one sample into the running totals.</summary>
        /// <remarks>
        /// Depth and clearance are tracked as two separate minima. They are not the same
        /// quantity and they do not have to occur at the same station: the shallowest water on a
        /// route and the tightest squeeze for this particular hull are different findings, and
        /// collapsing them loses whichever the operator needed.
        /// </remarks>
        /// <param name="sample">Point classification at this station.</param>
        /// <param name="distanceM">Distance along the segment to this station, in metres.</param>
        public void Accumulate(WaterSample sample, double distanceM)
        {
            _risk += (_hasPrevious ? spacingM : 0.0) * sample.RiskWeight;
            _hasPrevious = true;

            if (sample.Clearance.HasWaterData)
            {
                _shallowestDepthM = Math.Min(_shallowestDepthM, sample.Clearance.WaterDepthM);
                _minimumClearanceM = Math.Min(_minimumClearanceM, sample.Clearance.ClearanceM);
            }
            else
            {
                // Water the environment could not answer for. Folding in the worst case it could
                // hold — no column, the whole draft unsupported — keeps an unsurveyed stretch
                // from flattering a route's minima into looking better than they are known to be.
                _shallowestDepthM = Math.Min(_shallowestDepthM, 0.0);
                _minimumClearanceM = Math.Min(_minimumClearanceM, -sample.Clearance.DraftM);
            }

            if (Severity(sample.Class) > Severity(_worstClass))
            {
                _worstClass = sample.Class;
            }

            if (!sample.IsBlocked || _blockingPoint is not null)
            {
                return;
            }

            _blockingReason = sample.Reason;
            _blockingPoint = sample.PositionEus;
            _blockingDistanceM = distanceM;
        }

        /// <summary>Freezes the totals into an immutable result.</summary>
        /// <param name="lengthM">Horizontal segment length, in metres.</param>
        /// <param name="sampleCount">Samples taken along the segment.</param>
        /// <param name="sampleSpacingM">Distance between consecutive samples, in metres.</param>
        /// <returns>The sweep's findings.</returns>
        public RouteWaterCheck ToResult(double lengthM, int sampleCount, double sampleSpacingM) => new(
            IsNavigable: _blockingPoint is null,
            LengthM: lengthM,
            SampleCount: sampleCount,
            SampleSpacingM: sampleSpacingM,
            WorstClass: _worstClass,
            BlockingReason: _blockingReason,
            BlockingPointEus: _blockingPoint,
            BlockingDistanceM: _blockingDistanceM,
            ShallowestDepthM: double.IsFinite(_shallowestDepthM) ? _shallowestDepthM : 0.0,
            MinimumClearanceM: double.IsFinite(_minimumClearanceM) ? _minimumClearanceM : 0.0,
            AccumulatedRisk: _risk);
    }
}
