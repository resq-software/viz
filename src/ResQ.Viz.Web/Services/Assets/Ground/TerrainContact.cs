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

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>Settles a ground vehicle onto the terrain and reports what the ground allows.</summary>
/// <remarks>
/// Pure and allocation-light: every function here is a function of its arguments alone, with no
/// clock, no sampling and no hidden state — the normal filter's memory is threaded through by
/// the caller. That is what lets a whole slope response be driven from literals in a test, and
/// what keeps <see cref="IStepDrivenAsset.Step"/> replayable.
/// <para>
/// Everything published from here is <b>advisory decision support</b>. The speed ceiling, the
/// rollover fraction and the immobilisation flag are quasi-static estimates from a rigid-body
/// approximation over a procedural height field. They are there to tell an operator where to
/// look; none of them is a safety guarantee, and no wording downstream may present them as one.
/// </para>
/// <para>
/// <b>Cross-slope is two bands, not one, and the distinction is load-bearing.</b>
/// <see cref="GroundProfile.MaxSafeCrossSlopeRad"/> is an <em>operating</em> limit carrying
/// margin, so reaching it is advice: the vehicle is still mobile, its ceiling drops to a crawl,
/// and it keeps every heading it had — which it must, because every heading off a bank crosses
/// the bank, so refusing them all is how a rover ends up stranded on one.
/// <see cref="GroundContactGeometry.StaticStabilityAngleRad"/> is the inferred <em>physical</em>
/// tipping angle, and only that band is a refusal a route preview may act on. The two are
/// reported as <see cref="TerrainLimit.CrossSlope"/> and
/// <see cref="TerrainLimit.CrossSlopeUnstable"/>, and neither sets
/// <see cref="TerrainContactState.IsImmobilised"/>: whether a vehicle may be <em>sent</em> onto a
/// lean is a planning decision, taken in <see cref="Traversability"/>, not a claim that the one
/// already there cannot drive.
/// </para>
/// </remarks>
public static class TerrainContact
{
    /// <summary>Fraction of speed surrendered at the platform's full grade limit.</summary>
    private const double GradeDerateWeight = 0.8;

    /// <summary>Fraction of speed surrendered at the platform's full cross-slope limit.</summary>
    private const double CrossSlopeDerateWeight = 0.6;

    /// <summary>Floor on any single derating factor, so a legal cell never reaches zero speed.</summary>
    /// <remarks>
    /// Zero is reserved for "cannot move". Letting a derate reach it would make a merely
    /// difficult cell indistinguishable from a blocked one, which is exactly the collapse the
    /// water case exists to prevent.
    /// </remarks>
    private const double MinDerateFactor = 0.15;

    /// <summary>Speed difference, in metres per second, below which a derate is not worth reporting.</summary>
    private const double DerateEpsilonMps = 1e-3;

    /// <summary>Smallest limit angle a derate may divide by, in radians.</summary>
    private const double MinLimitAngleRad = 1e-6;

    /// <summary>
    /// Fraction of the platform's nominal speed a vehicle is advised down to once cross-slope has
    /// reached its operational limit.
    /// </summary>
    /// <remarks>
    /// The rollover-risk band is advisory, and an advisory that changes nothing is not worth
    /// publishing. Capping the ceiling here is what makes the advice actionable without making it
    /// a refusal: the vehicle keeps a way off the bank — and it is the only way off, since every
    /// heading out of a lean crosses the same slope — but it takes it at a crawl, where lateral
    /// acceleration adds least to the overturning moment the lean has already spent.
    /// <para>
    /// Chosen below <see cref="Traversability"/>'s costly threshold on purpose, so a point in this
    /// band is <see cref="TraversabilityClass.Costly"/> by arithmetic as well as by the explicit
    /// rule, and a planner routes round it whenever there is a way round.
    /// </para>
    /// </remarks>
    private const double RolloverAdvisorySpeedFraction = 0.25;

    /// <summary>
    /// Margin by which available traction must exceed the slope's demand before the vehicle is
    /// considered able to climb it.
    /// </summary>
    /// <remarks>
    /// A vehicle on a grade needs a friction coefficient of at least <c>tan(grade)</c> just to
    /// hold position, and more than that to accelerate or steer. The margin is what separates
    /// "theoretically balanced" from "actually makes progress", and it is why a rover can be
    /// power-capable of a slope and still bog down on it in the wet.
    /// </remarks>
    private const double TractionDemandMargin = 1.15;

    /// <summary>Resolves the vehicle's pose and mobility on the ground under it.</summary>
    /// <remarks>
    /// The published height is the terrain elevation plus the profile's ride height, never the
    /// result of integrating gravity. A ground vehicle in permanent contact with the surface has
    /// no free-fall phase to integrate, and pretending otherwise buys a settling transient, a
    /// spring constant to tune and a rover that sinks through a hillside when the timestep grows.
    /// </remarks>
    /// <param name="planarPositionEus">Where the vehicle is horizontally; the Y component is ignored and replaced.</param>
    /// <param name="headingRad">Direction the vehicle points, radians clockwise from true north.</param>
    /// <param name="profile">Geometry and limits of this platform.</param>
    /// <param name="environment">Environment sampled at this position, already carrying elevation, normal and slope.</param>
    /// <param name="deltaSeconds">Timestep in seconds, used only to derive the normal filter's coefficient.</param>
    /// <param name="filter">Normal-filter state from the previous step; pass <see cref="TerrainNormalFilter.Uninitialised"/> on the first.</param>
    /// <returns>The resolved contact and the filter state to carry forward.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> or <paramref name="environment"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="headingRad"/> is not finite.</exception>
    public static TerrainContactResult Resolve(
        Vector3 planarPositionEus,
        double headingRad,
        GroundProfile profile,
        EnvironmentSample environment,
        double deltaSeconds,
        TerrainNormalFilter filter)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(environment);

        var nextFilter = filter.Blend(
            environment.TerrainNormalEus,
            deltaSeconds,
            GroundContactGeometry.NormalFilterTimeConstantSeconds(profile));

        ResolveBodyBasis(headingRad, nextFilter.NormalEus, out var forward, out var left, out var up);

        // The normal resolved into the body axes: the forward axis tilts by the grade, the
        // lateral axis tilts by the cross-slope. Reading them off the basis rather than
        // composing Euler angles is what keeps the two from swapping on a steep bank.
        double grade = Math.Asin(Math.Clamp(forward.Y, -1.0, 1.0));
        double crossSlope = Math.Asin(Math.Clamp(left.Y, -1.0, 1.0));

        var surface = GroundSurfaces.For(environment.SurfaceMaterial);
        bool isWater = !surface.IsTraversable || environment.IsWater;
        double traction = surface.TractionCoefficient * (1.0
            - (GroundSurfaces.PrecipitationTractionLoss * Math.Clamp(environment.Precipitation, 0.0, 1.0)));

        double stability = Math.Max(
            GroundContactGeometry.StaticStabilityAngleRad(profile), MinLimitAngleRad);
        double rolloverFraction = Math.Clamp(Math.Abs(crossSlope) / stability, 0.0, 1.0);

        // Two bands, named separately because they mean different things. The lower one is the
        // profile's declared operating limit, which is set with margin in hand and is therefore
        // advice; the upper one is the angle the platform is inferred to tip at, which is the
        // only cross-slope a route preview has any business refusing. Collapsing them — the bug
        // this pair replaces — turned the advisory into an absolute block and stranded a rover on
        // a bank, because every heading off a bank crosses the bank.
        bool hasRolloverRisk = Math.Abs(crossSlope) >= profile.MaxSafeCrossSlopeRad;
        bool isBeyondStability = Math.Abs(crossSlope) >= stability;

        bool gradeExceeded = Math.Abs(grade) > profile.MaxClimbableGradeRad;

        // Grip has to beat what the slope demands, not merely be present: tan(grade) is the
        // coefficient needed to hold station on it, and a margin above that is needed to move.
        double demandedTraction = Math.Abs(Math.Tan(grade)) * TractionDemandMargin;
        bool tractionLost = !isWater
            && (traction < GroundSurfaces.ImmobilisingTractionCoefficient || traction < demandedTraction);

        bool immobilised = isWater || gradeExceeded || tractionLost;

        var derateLimit = TerrainLimit.None;
        double safeSpeed = immobilised
            ? 0.0
            : DerateSpeed(
                profile, surface, traction, grade, crossSlope, environment.Zones, out derateLimit);

        // The rollover advisory buys a slower vehicle, never a stopped one. Zero is reserved for
        // "cannot move", and a vehicle that cannot move cannot leave the bank it is leaning on.
        if (!immobilised && hasRolloverRisk)
        {
            safeSpeed = Math.Min(
                safeSpeed, profile.MaxForwardSpeedMps * RolloverAdvisorySpeedFraction);
        }

        var (status, limit) = Classify(
            isWater, isBeyondStability, hasRolloverRisk, gradeExceeded, tractionLost,
            safeSpeed < profile.MaxForwardSpeedMps - DerateEpsilonMps, derateLimit);

        var contact = new TerrainContactState(
            PositionEus: new Vector3(
                planarPositionEus.X,
                (float)(environment.TerrainElevationM + GroundContactGeometry.RideHeightM(profile)),
                planarPositionEus.Z),
            OrientationEusFromFlu: OrientationFromBasis(forward, left, up),
            FilteredNormalEus: nextFilter.NormalEus,
            GradeRad: grade,
            CrossSlopeRad: crossSlope,
            SlopeRad: environment.SlopeRad,
            Surface: surface,
            TractionCoefficient: Math.Clamp(traction, 0.0, 1.0),
            RolloverRiskFraction: rolloverFraction,
            SafeSpeedMps: safeSpeed,
            HasRolloverRisk: hasRolloverRisk,
            IsImmobilised: immobilised,
            Status: status,
            Limit: limit);

        return new TerrainContactResult(contact, nextFilter);
    }

    /// <summary>Detects a physical impact with terrain the platform cannot mount.</summary>
    /// <remarks>
    /// A <em>collision</em>, not a planning cost — see <see cref="GroundStepCollision"/> for why
    /// the two never share a code path. A rise only counts when it is both taller than the
    /// platform can step over and steeper than it could have climbed over the distance
    /// travelled: a half-metre gained over ten metres is a hill, the same half-metre gained over
    /// ten centimetres is a wall. A drop is never a collision here; falling is a different
    /// failure and is not modelled this pass.
    /// </remarks>
    /// <param name="profile">Geometry and limits of this platform.</param>
    /// <param name="previousElevationM">Terrain elevation the vehicle came from, in metres.</param>
    /// <param name="currentElevationM">Terrain elevation the vehicle moved onto, in metres.</param>
    /// <param name="travelledDistanceM">Horizontal distance covered between the two, in metres.</param>
    /// <param name="speedMps">Speed along the direction of travel at contact, in metres per second.</param>
    /// <param name="collision">The impact on success, otherwise <see cref="GroundStepCollision.None"/>.</param>
    /// <returns><see langword="true"/> when the vehicle struck an unmountable step.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static bool TryDetectStepCollision(
        GroundProfile profile,
        double previousElevationM,
        double currentElevationM,
        double travelledDistanceM,
        double speedMps,
        out GroundStepCollision collision)
    {
        ArgumentNullException.ThrowIfNull(profile);

        collision = GroundStepCollision.None;

        if (!double.IsFinite(previousElevationM) || !double.IsFinite(currentElevationM)
            || !double.IsFinite(travelledDistanceM) || !double.IsFinite(speedMps))
        {
            return false;
        }

        double rise = currentElevationM - previousElevationM;
        double climbable = Math.Max(travelledDistanceM, 0.0) * Math.Tan(profile.MaxClimbableGradeRad);

        if (rise <= profile.MaxStepHeightM || rise <= climbable)
        {
            return false;
        }

        collision = new GroundStepCollision(true, rise, Math.Abs(speedMps), GroundStepCollision.StepCode);
        return true;
    }

    /// <summary>Bearing of the line of steepest ascent at a sampled point.</summary>
    /// <remarks>
    /// The heading that maximises grade and zeroes cross-slope, which is the platform's best case
    /// for crossing the cell and therefore the only honest direction-free answer: a refusal on
    /// this heading means no heading works. Shared rather than re-derived so the planning layer
    /// and the derating reduction cannot drift apart about which direction "no direction in
    /// particular" means.
    /// <para>
    /// On level ground the projected gradient is degenerate and the bearing falls back to north,
    /// which costs nothing: with no slope, every heading gives the same grade and cross-slope.
    /// </para>
    /// </remarks>
    /// <param name="environment">Environment sampled at the point.</param>
    /// <returns>Bearing in radians clockwise from true north.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is null.</exception>
    public static double SteepestAscentHeadingRad(EnvironmentSample environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var uphill = new Vector3(
            -environment.TerrainNormalEus.X, 0f, -environment.TerrainNormalEus.Z);

        return CoordinateFrames.BearingFromEusVector(uphill);
    }

    /// <summary>Builds the vehicle's FLU body axes on a tilted surface, expressed in the scene frame.</summary>
    /// <remarks>
    /// The level heading direction is projected onto the terrain plane, then the lateral and up
    /// axes are recovered by cross products. Building the basis this way guarantees an
    /// orthonormal, right-handed triad on any slope, which composing a yaw with a pitch and a
    /// roll does not: those two disagree the moment grade and cross-slope are both non-zero.
    /// </remarks>
    private static void ResolveBodyBasis(
        double headingRad, Vector3 normalEus, out Vector3 forward, out Vector3 left, out Vector3 up)
    {
        var level = CoordinateFrames.BearingToEusVector(headingRad, 1.0);
        var normal = Normalise(normalEus, Vector3.UnitY);

        forward = Normalise(level - (normal * Vector3.Dot(level, normal)), level);
        left = Normalise(Vector3.Cross(normal, forward), Vector3.Cross(Vector3.UnitY, level));
        up = Normalise(Vector3.Cross(forward, left), normal);
    }

    /// <summary>Quaternion mapping FLU body axes into the scene frame, from an orthonormal triad.</summary>
    /// <remarks>
    /// <see cref="Matrix4x4"/> uses the row-vector convention — <c>r = v * M</c> — so each
    /// <b>row</b> holds one body axis expressed in the scene frame. That is the transpose of the
    /// column-vector convention used inside <c>CoordinateFrames</c>, and silently mixing the two
    /// is the transposed-rotation bug that yields an attitude correct on the level and mirrored
    /// on a bank. Writing the rows explicitly and letting the runtime derive the quaternion keeps
    /// the conversion in one convention end to end.
    /// </remarks>
    private static Quaternion OrientationFromBasis(Vector3 forward, Vector3 left, Vector3 up)
    {
        var basis = Matrix4x4.Identity;

        basis.M11 = forward.X;
        basis.M12 = forward.Y;
        basis.M13 = forward.Z;
        basis.M21 = left.X;
        basis.M22 = left.Y;
        basis.M23 = left.Z;
        basis.M31 = up.X;
        basis.M32 = up.Y;
        basis.M33 = up.Z;

        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
    }

    /// <summary>Advisory speed ceiling once grade, cross-slope, surface and zones are accounted for.</summary>
    /// <remarks>
    /// Traction is passed in already derated for weather rather than read from
    /// <see cref="SurfaceTraction.SpeedFactor"/>, which knows nothing about the current
    /// precipitation. Zone limits are folded in here so the single ceiling an asset publishes is
    /// the one it will actually obey; a zone is advisory guidance, not a compliance assertion.
    /// </remarks>
    private static double DerateSpeed(
        GroundProfile profile,
        SurfaceTraction surface,
        double traction,
        double gradeRad,
        double crossSlopeRad,
        IReadOnlyList<EnvironmentZone> zones,
        out TerrainLimit bindingLimit)
    {
        double gradeFactor = LimitFactor(
            Math.Abs(gradeRad), profile.MaxClimbableGradeRad, GradeDerateWeight);
        double crossFactor = LimitFactor(
            Math.Abs(crossSlopeRad), profile.MaxSafeCrossSlopeRad, CrossSlopeDerateWeight);
        double surfaceFactor =
            Math.Clamp(traction / GroundSurfaces.ReferenceTractionCoefficient, 0.0, 1.0)
            * Math.Clamp(1.0 - surface.RollingResistanceCoefficient, 0.0, 1.0);

        bindingLimit = gradeFactor <= crossFactor && gradeFactor <= surfaceFactor
            ? TerrainLimit.GradeDerate
            : crossFactor <= surfaceFactor ? TerrainLimit.CrossSlopeDerate : TerrainLimit.SurfaceDerate;

        double speed = profile.MaxForwardSpeedMps * gradeFactor * crossFactor * surfaceFactor;
        double ceiling = ZoneSpeedCeiling(zones);

        if (ceiling < speed)
        {
            bindingLimit = TerrainLimit.ZoneSpeedLimit;
            return ceiling;
        }

        return speed;
    }

    /// <summary>Lowest speed ceiling any zone at this point imposes, or infinity when none do.</summary>
    private static double ZoneSpeedCeiling(IReadOnlyList<EnvironmentZone> zones)
    {
        double ceiling = double.PositiveInfinity;

        for (int i = 0; i < zones.Count; i++)
        {
            if (zones[i].SpeedLimitMps is { } limit && limit >= 0.0 && limit < ceiling)
            {
                ceiling = limit;
            }
        }

        return ceiling;
    }

    /// <summary>Linear derating factor for how far a measured angle has consumed its limit.</summary>
    private static double LimitFactor(double magnitudeRad, double limitRad, double weight) =>
        Math.Clamp(
            1.0 - (weight * Math.Clamp(magnitudeRad / Math.Max(limitRad, MinLimitAngleRad), 0.0, 1.0)),
            MinDerateFactor,
            1.0);

    /// <summary>Collapses the individual flags into one status and one typed cause.</summary>
    /// <remarks>
    /// Precedence is deliberate. Water comes first because a vehicle that is not on ground has
    /// no slope to roll down. The two cross-slope bands then outrank the immobilisation causes,
    /// because a vehicle stopped on a steep cross-slope is still about to tip and that is the
    /// finding an operator has to act on — the worse band first, so a lean past the inferred
    /// tipping angle is never reported as merely at the operational limit. The individual flags
    /// stay on <see cref="TerrainContactState"/> so nothing is lost to this ordering.
    /// <para>
    /// Note what neither cross-slope arm sets: <see cref="TerrainContactState.IsImmobilised"/>.
    /// A leaning vehicle is still making progress, and telling it otherwise would zero its speed
    /// ceiling and take away the only way off the bank. Whether a route may be <em>sent</em> onto
    /// one is a separate question, answered by <see cref="Traversability"/> from
    /// <see cref="TerrainLimit.CrossSlopeUnstable"/>.
    /// </para>
    /// </remarks>
    private static (TerrainContactStatus Status, TerrainLimit Limit) Classify(
        bool isWater, bool isBeyondStability, bool hasRolloverRisk, bool gradeExceeded,
        bool tractionLost, bool isDerated, TerrainLimit derateLimit) =>
        (isWater, isBeyondStability, hasRolloverRisk, gradeExceeded, tractionLost) switch
        {
            (true, _, _, _, _) => (TerrainContactStatus.Immobilised, TerrainLimit.Water),
            (_, true, _, _, _) =>
                (TerrainContactStatus.RolloverRisk, TerrainLimit.CrossSlopeUnstable),
            (_, _, true, _, _) => (TerrainContactStatus.RolloverRisk, TerrainLimit.CrossSlope),
            (_, _, _, true, _) => (TerrainContactStatus.Immobilised, TerrainLimit.Grade),
            (_, _, _, _, true) => (TerrainContactStatus.Immobilised, TerrainLimit.Traction),
            _ => isDerated
                ? (TerrainContactStatus.SpeedDerated, derateLimit)
                : (TerrainContactStatus.WithinLimits, TerrainLimit.None),
        };

    /// <summary>Normalises a vector, falling back rather than propagating a NaN attitude.</summary>
    private static Vector3 Normalise(Vector3 value, Vector3 fallback)
    {
        float length = value.Length();
        return float.IsFinite(length) && length > 1e-6f ? value / length : fallback;
    }
}
