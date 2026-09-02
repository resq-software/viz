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

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>Classifies ground for a specific platform, and checks straight routes across it.</summary>
/// <remarks>
/// Evaluated against a <see cref="GroundProfile"/> rather than against "a rover", because a
/// narrow tracked robot and a wide wheeled rover disagree about the same ground: a twenty-eight
/// degree slope is a route for one and a refusal for the other. Anything that classified ground
/// once, for all vehicles, would have to pick whose answer to be wrong for.
/// <para>
/// This is the <b>planning</b> layer and it is entirely advisory. It never mutates an asset,
/// never raises an event and never reports a collision — a physical impact is
/// <see cref="TerrainContact.TryDetectStepCollision"/>, a separate function producing a separate
/// type, because a preview that raised collision events would report impacts for routes nobody
/// ever drove.
/// </para>
/// </remarks>
public static class Traversability
{
    /// <summary>Upper bound on samples per route, whatever its length.</summary>
    /// <remarks>
    /// A constant, so the bound is still a function of geometry alone. It caps the work a very
    /// long preview can do; past it the spacing widens rather than the count growing.
    /// </remarks>
    public const int MaxRouteSamples = 512;

    /// <summary>Fraction of nominal speed below which a passable patch is called costly.</summary>
    /// <remarks>
    /// Set below what flat vegetation alone costs. The terrain service classifies most of this
    /// scene as vegetation, so a threshold above that would paint nearly every cell costly and
    /// the distinction would stop carrying information.
    /// </remarks>
    private const double CostlySpeedFraction = 0.60;

    /// <summary>Cost multiplier assigned to a blocked sample when accumulating a route total.</summary>
    /// <remarks>
    /// Finite so a blocked route still yields a comparable number instead of an infinity that
    /// poisons every downstream sum. The route's <see cref="RouteTraversability.IsTraversable"/>
    /// flag, not its cost, is what refuses the target.
    /// </remarks>
    private const double BlockedCostMultiplier = 1000.0;

    /// <summary>Stable machine-readable code for a reason.</summary>
    /// <param name="reason">Reason to encode.</param>
    /// <returns>A dotted lower-case token, e.g. <c>traversability.blocked.water</c>.</returns>
    public static string ReasonCode(TraversabilityReason reason) => reason switch
    {
        TraversabilityReason.Water => "traversability.blocked.water",
        TraversabilityReason.ProhibitedZone => "traversability.blocked.zone",
        TraversabilityReason.GradeExceeded => "traversability.blocked.grade",
        TraversabilityReason.CrossSlopeExceeded => "traversability.blocked.cross-slope",
        TraversabilityReason.StepHeightExceeded => "traversability.blocked.step-height",
        TraversabilityReason.LowTraction => "traversability.blocked.traction",
        TraversabilityReason.SteepGrade => "traversability.costly.grade",
        TraversabilityReason.SteepCrossSlope => "traversability.costly.cross-slope",
        TraversabilityReason.PoorSurface => "traversability.costly.surface",
        TraversabilityReason.ZoneSpeedLimit => "traversability.costly.zone",
        TraversabilityReason.RolloverRiskAdvisory => "traversability.costly.rollover-risk",
        TraversabilityReason.NoTerrainData => "traversability.unknown.no-data",
        _ => "traversability.clear",
    };

    /// <summary>Classifies a point without committing to a direction of travel.</summary>
    /// <remarks>
    /// Evaluated along the line of steepest ascent — the heading that maximises grade and
    /// zeroes cross-slope. That is the platform's best case for crossing the cell, so a
    /// <see cref="TraversabilityClass.Blocked"/> verdict here means no heading works, which is
    /// the only honest direction-free answer. A slope the platform can climb but not cross is
    /// reported as <see cref="TraversabilityClass.Costly"/> instead, because reaching it commits
    /// the vehicle to one heading.
    /// </remarks>
    /// <param name="profile">Platform to classify for.</param>
    /// <param name="sample">Environment sampled at the point.</param>
    /// <returns>The classification and the quantities behind it.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static TraversabilitySample Evaluate(GroundProfile profile, EnvironmentSample sample)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sample);

        var evaluated = Evaluate(
            profile, sample, TerrainContact.SteepestAscentHeadingRad(sample));

        // Climbable head-on but not crossable: every heading except straight up or straight down
        // the fall line exceeds the platform's cross-slope limit, so reaching this cell commits
        // the vehicle to one direction. Passable, but not freely.
        if (evaluated.Class != TraversabilityClass.Traversable
            || sample.SlopeRad <= profile.MaxSafeCrossSlopeRad)
        {
            return evaluated;
        }

        return evaluated with
        {
            Class = TraversabilityClass.Costly,
            Reason = TraversabilityReason.SteepCrossSlope,
        };
    }

    /// <summary>Classifies a point for a vehicle travelling on a given heading.</summary>
    /// <remarks>
    /// Resolved through <see cref="TerrainContact.Resolve"/> with an
    /// <see cref="TerrainNormalFilter.Uninitialised"/> filter and a zero timestep, so the
    /// measured normal passes through unsmoothed. That is deliberate: a planning answer must not
    /// depend on which vehicle asked or on where it had just been, or the same route would
    /// preview differently for two rovers standing side by side.
    /// </remarks>
    /// <param name="profile">Platform to classify for.</param>
    /// <param name="sample">Environment sampled at the point.</param>
    /// <param name="headingRad">Direction of travel, radians clockwise from true north.</param>
    /// <returns>The classification and the quantities behind it.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="headingRad"/> is not finite.</exception>
    public static TraversabilitySample Evaluate(
        GroundProfile profile, EnvironmentSample sample, double headingRad)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sample);

        if (!double.IsFinite(sample.TerrainElevationM) || !double.IsFinite(sample.SlopeRad))
        {
            return Unknown(sample);
        }

        var contact = TerrainContact.Resolve(
            sample.PositionEus, headingRad, profile, sample,
            deltaSeconds: 0.0, TerrainNormalFilter.Uninitialised).Contact;

        var (traversabilityClass, reason) = ClassifyPoint(profile, sample, contact);

        return new TraversabilitySample(
            PositionEus: sample.PositionEus,
            Class: traversabilityClass,
            Reason: reason,
            GradeRad: contact.GradeRad,
            CrossSlopeRad: contact.CrossSlopeRad,
            SlopeRad: sample.SlopeRad,
            TractionCoefficient: contact.TractionCoefficient,
            SafeSpeedMps: contact.SafeSpeedMps,
            CostMultiplier: traversabilityClass == TraversabilityClass.Blocked
                ? double.PositiveInfinity
                : CostMultiplier(profile, contact.SafeSpeedMps));
    }

    /// <summary>Sweeps a straight segment and reports whether a platform may drive it.</summary>
    /// <remarks>
    /// Every sample is taken, always, even after one has already blocked the route. Stopping at
    /// the first refusal would make the number of terrain queries a function of the terrain, so
    /// two replays of the same scenario would do different amounts of work and could hash
    /// differently. The first blocking sample is recorded instead, which is what the caller
    /// wanted from an early exit anyway.
    /// <para>
    /// A straight-line sweep, not a search: it answers "may this platform drive from here to
    /// there in a straight line", which is what a click-to-drive target needs. Finding a way
    /// around a refusal is a planner's job and is not attempted here.
    /// </para>
    /// </remarks>
    /// <param name="profile">Platform to check for.</param>
    /// <param name="startEus">Segment start in the scene frame; the vertical component is ignored.</param>
    /// <param name="endEus">Segment end in the scene frame; the vertical component is ignored.</param>
    /// <param name="sampler">Environment sampler to query along the segment.</param>
    /// <returns>What the sweep found.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static RouteTraversability CheckRoute(
        GroundProfile profile, Vector3 startEus, Vector3 endEus, IEnvironmentSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(sampler);

        var delta = new Vector3(endEus.X - startEus.X, 0f, endEus.Z - startEus.Z);
        double length = delta.Length();
        int count = SampleCount(length, GroundContactGeometry.RouteSampleSpacingM(profile));
        double spacing = count > 1 ? length / (count - 1) : 0.0;
        double heading = CoordinateFrames.BearingFromEusVector(delta);

        double normalSpacing = GroundContactGeometry.NormalSpacingM(profile);
        var sweep = new RouteSweep(profile, spacing);

        for (int i = 0; i < count; i++)
        {
            float t = count > 1 ? (float)i / (count - 1) : 0f;
            float x = startEus.X + (delta.X * t);
            float z = startEus.Z + (delta.Z * t);
            var probe = new Vector3(x, (float)sampler.GetElevation(x, z), z);
            var environment = sampler.Sample(probe, normalSpacing);

            sweep.Accumulate(Evaluate(profile, environment, heading), probe.Y, spacing * i);
        }

        return sweep.ToResult(length, count, spacing);
    }

    /// <summary>Samples needed to cover a segment, derived from geometry and nothing else.</summary>
    /// <param name="lengthM">Segment length in metres.</param>
    /// <param name="spacingM">Requested spacing in metres.</param>
    /// <returns>A count in <c>[2, <see cref="MaxRouteSamples"/>]</c>, or 1 for a degenerate segment.</returns>
    public static int SampleCount(double lengthM, double spacingM)
    {
        if (!double.IsFinite(lengthM) || lengthM <= CoordinateFrames.MinHorizontalMagnitude)
        {
            return 1;
        }

        double spacing = double.IsFinite(spacingM) && spacingM > 0.0 ? spacingM : 1.0;
        return (int)Math.Clamp(Math.Ceiling(lengthM / spacing) + 1.0, 2.0, MaxRouteSamples);
    }

    /// <summary>Cost of a metre here relative to a metre of flat pavement.</summary>
    private static double CostMultiplier(GroundProfile profile, double safeSpeedMps) =>
        safeSpeedMps > 0.0
            ? Math.Clamp(profile.MaxForwardSpeedMps / safeSpeedMps, 1.0, BlockedCostMultiplier)
            : BlockedCostMultiplier;

    /// <summary>Turns a resolved contact into a planning verdict.</summary>
    /// <remarks>
    /// A prohibited zone is tested before the physics: an operator-declared no-go area is a
    /// decision about where a vehicle may go, and ground it could physically cross does not
    /// overrule it.
    /// </remarks>
    private static (TraversabilityClass Class, TraversabilityReason Reason) ClassifyPoint(
        GroundProfile profile, EnvironmentSample sample, TerrainContactState contact)
    {
        for (int i = 0; i < sample.Zones.Count; i++)
        {
            if (sample.Zones[i].IsEntryProhibited)
            {
                return (TraversabilityClass.Blocked, TraversabilityReason.ProhibitedZone);
            }
        }

        // The physics verdict is read off the contact solver's typed cause rather than
        // recomputed from the same angles. Two copies of "is this too steep" is how a preview
        // ends up promising a route the vehicle then refuses to drive.
        var blocking = BlockingReason(contact);
        if (blocking != TraversabilityReason.None)
        {
            return (TraversabilityClass.Blocked, blocking);
        }

        // The rollover advisory band is costly by rule, not by arithmetic. The contact solver
        // already crawls the ceiling here, which would reach the same verdict through the
        // threshold below, but stating it makes the classification independent of how the two
        // constants happen to be tuned — and this is the one verdict that must not quietly become
        // Traversable, because it is the one carrying a standing advisory to the operator.
        if (contact.HasRolloverRisk)
        {
            return (TraversabilityClass.Costly, TraversabilityReason.RolloverRiskAdvisory);
        }

        return contact.SafeSpeedMps < profile.MaxForwardSpeedMps * CostlySpeedFraction
            ? (TraversabilityClass.Costly, CostlyReason(contact))
            : (TraversabilityClass.Traversable, TraversabilityReason.None);
    }

    /// <summary>The refusal a contact implies, or <see cref="TraversabilityReason.None"/>.</summary>
    /// <remarks>
    /// Only the <em>physical</em> cross-slope band refuses. Reaching
    /// <see cref="GroundProfile.MaxSafeCrossSlopeRad"/> — <see cref="TerrainLimit.CrossSlope"/> —
    /// is an operating advisory with margin still in hand, and blocking on it made the advisory
    /// absolute: a vehicle on a bank crosses that same bank on every heading it could leave by,
    /// so refusing them all left it with nowhere to go. That band is
    /// <see cref="TraversabilityClass.Costly"/> with
    /// <see cref="TraversabilityReason.RolloverRiskAdvisory"/>, which routes a planner round the
    /// lean without stranding anything on it. Past
    /// <see cref="GroundContactGeometry.StaticStabilityAngleRad"/> —
    /// <see cref="TerrainLimit.CrossSlopeUnstable"/> — the model says the platform is over rather
    /// than close, and that is a refusal worth making.
    /// </remarks>
    private static TraversabilityReason BlockingReason(TerrainContactState contact) => contact.Limit switch
    {
        TerrainLimit.Water => TraversabilityReason.Water,
        TerrainLimit.Grade => TraversabilityReason.GradeExceeded,
        TerrainLimit.CrossSlopeUnstable => TraversabilityReason.CrossSlopeExceeded,
        TerrainLimit.Traction => TraversabilityReason.LowTraction,
        _ => TraversabilityReason.None,
    };

    /// <summary>Which of the passable-but-slow causes dominates.</summary>
    /// <remarks>
    /// Read straight off the derating factor the contact solver found binding, rather than
    /// re-deriving it from the angles. Re-deriving it is how a preview comes to blame the grade
    /// for a slowdown the surface actually caused, because the two tests were tuned apart.
    /// </remarks>
    private static TraversabilityReason CostlyReason(TerrainContactState contact) => contact.Limit switch
    {
        TerrainLimit.GradeDerate => TraversabilityReason.SteepGrade,
        TerrainLimit.CrossSlopeDerate => TraversabilityReason.SteepCrossSlope,
        TerrainLimit.ZoneSpeedLimit => TraversabilityReason.ZoneSpeedLimit,
        _ => TraversabilityReason.PoorSurface,
    };

    /// <summary>Ranks classifications so the worst along a route can be tracked.</summary>
    /// <remarks>
    /// Explicit rather than leaning on the enum's declaration order, so reordering
    /// <see cref="TraversabilityClass"/> for readability cannot silently change which verdict a
    /// route reports. Unknown outranks costly: unsurveyed ground deserves more attention than
    /// ground already known to be merely slow.
    /// </remarks>
    private static int Severity(TraversabilityClass value) => value switch
    {
        TraversabilityClass.Blocked => 3,
        TraversabilityClass.Unknown => 2,
        TraversabilityClass.Costly => 1,
        _ => 0,
    };

    /// <summary>A point the terrain could not answer for.</summary>
    private static TraversabilitySample Unknown(EnvironmentSample sample) => new(
        PositionEus: sample.PositionEus,
        Class: TraversabilityClass.Unknown,
        Reason: TraversabilityReason.NoTerrainData,
        GradeRad: 0.0,
        CrossSlopeRad: 0.0,
        SlopeRad: 0.0,
        TractionCoefficient: 0.0,
        SafeSpeedMps: 0.0,
        CostMultiplier: BlockedCostMultiplier);

    /// <summary>Running totals for one straight-segment sweep.</summary>
    /// <remarks>
    /// A mutable accumulator confined to a single call: it never escapes
    /// <see cref="CheckRoute"/>, so the sweep stays a pure function of its arguments while the
    /// loop body stays readable. Step height is folded in here because it is the one quantity a
    /// single point cannot express — it needs the previous sample.
    /// </remarks>
    private sealed class RouteSweep(GroundProfile profile, double spacingM)
    {
        private double _previousElevationM;
        private bool _hasPrevious;

        private TraversabilityClass _worstClass = TraversabilityClass.Traversable;
        private TraversabilityReason _blockingReason = TraversabilityReason.None;
        private Vector3? _blockingPoint;
        private double _blockingDistanceM;
        private double _worstGradeRad;
        private double _worstCrossSlopeRad;
        private double _worstStepHeightM;
        private double _cost;

        /// <summary>Folds one sample, and the rise onto it, into the running totals.</summary>
        /// <remarks>
        /// A rise counts as a step only when it clears the platform's step height <b>and</b>
        /// exceeds what it could have climbed over the sample spacing. Testing the step height
        /// alone would call every slope steeper than a few degrees a wall, because the samples
        /// are metres apart and the rise between them grows with the spacing.
        /// </remarks>
        /// <param name="sample">Point classification at this station.</param>
        /// <param name="elevationM">Terrain elevation at this station, in metres.</param>
        /// <param name="distanceM">Distance along the segment to this station, in metres.</param>
        public void Accumulate(TraversabilitySample sample, double elevationM, double distanceM)
        {
            double rise = _hasPrevious ? elevationM - _previousElevationM : 0.0;
            double segmentM = _hasPrevious ? spacingM : 0.0;
            _previousElevationM = elevationM;
            _hasPrevious = true;

            _worstStepHeightM = Math.Max(_worstStepHeightM, Math.Abs(rise));
            _worstGradeRad = Larger(_worstGradeRad, sample.GradeRad);
            _worstCrossSlopeRad = Larger(_worstCrossSlopeRad, sample.CrossSlopeRad);
            _cost += segmentM * Math.Min(sample.CostMultiplier, BlockedCostMultiplier);

            double climbableM = segmentM * Math.Tan(profile.MaxClimbableGradeRad);
            bool stepBlocks = Math.Abs(rise) > profile.MaxStepHeightM && Math.Abs(rise) > climbableM;
            var effective = stepBlocks ? TraversabilityClass.Blocked : sample.Class;

            if (Severity(effective) > Severity(_worstClass))
            {
                _worstClass = effective;
            }

            if (effective != TraversabilityClass.Blocked || _blockingPoint is not null)
            {
                return;
            }

            _blockingReason = stepBlocks ? TraversabilityReason.StepHeightExceeded : sample.Reason;
            _blockingPoint = sample.PositionEus;
            _blockingDistanceM = distanceM;
        }

        /// <summary>Freezes the totals into an immutable result.</summary>
        /// <param name="lengthM">Horizontal segment length, in metres.</param>
        /// <param name="sampleCount">Samples taken along the segment.</param>
        /// <param name="sampleSpacingM">Distance between consecutive samples, in metres.</param>
        /// <returns>The sweep's findings.</returns>
        public RouteTraversability ToResult(double lengthM, int sampleCount, double sampleSpacingM) => new(
            IsTraversable: _blockingPoint is null,
            LengthM: lengthM,
            SampleCount: sampleCount,
            SampleSpacingM: sampleSpacingM,
            WorstClass: _worstClass,
            BlockingReason: _blockingReason,
            BlockingPointEus: _blockingPoint,
            BlockingDistanceM: _blockingDistanceM,
            WorstGradeRad: _worstGradeRad,
            WorstCrossSlopeRad: _worstCrossSlopeRad,
            WorstStepHeightM: _worstStepHeightM,
            AccumulatedCost: _cost,
            AdvisoryTransitSeconds:
                profile.MaxForwardSpeedMps > 0.0 ? _cost / profile.MaxForwardSpeedMps : 0.0);

        /// <summary>Keeps the signed value of larger magnitude, so a downhill worst case survives.</summary>
        private static double Larger(double current, double candidate) =>
            Math.Abs(candidate) > Math.Abs(current) ? candidate : current;
    }
}
