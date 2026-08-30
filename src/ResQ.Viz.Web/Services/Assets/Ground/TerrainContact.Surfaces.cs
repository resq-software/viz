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

using ResQ.Simulation.Engine.Environment;

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>How a surface material behaves under a wheel or a track.</summary>
/// <remarks>
/// Two independent coefficients, because they gate different failures. Traction is what the
/// vehicle can push against: too little for the slope it is on and it spins, which is an
/// <em>immobilisation</em>. Rolling resistance is what it must overcome to keep moving: more of
/// it and the vehicle still arrives, just slower, which is a <em>derate</em>. Collapsing the
/// pair into one "difficulty" number loses that distinction and turns every soft surface into a
/// stop.
/// <para>
/// <see cref="IsTraversable"/> is not a low value of either coefficient. Water is not slow
/// ground for a wheeled or tracked vehicle — it is ground that is not there — so it is a
/// blocked cell, and no amount of lowering a speed ceiling may reach it.
/// </para>
/// <para>
/// This table is the <b>only</b> traction source in the ground stack. The figures a vehicle is
/// advised of and the grip it is integrated at are the same figures because they are the same
/// lookup: <see cref="TerrainContact.Resolve"/> reads this row, and
/// <see cref="GroundConditions.From(TerrainContactState)"/> projects what it resolved. An earlier
/// arrangement had <c>GroundConditions.From</c> carry a second copy of these numbers with a
/// different grade fade and no rolling resistance, which is worse than one curve however good
/// either curve is — and a comment here claiming they agreed was worse still, because it made the
/// disagreement invisible.
/// </para>
/// <para>
/// The rolling-resistance and traversability columns have no counterpart on the integrator side,
/// and should not grow one: the integrator is asked to make a bad attempt behave badly, whereas
/// refusing the attempt outright is a decision for the asset and for
/// <see cref="Traversability"/>.
/// </para>
/// </remarks>
/// <param name="Surface">Material this row describes.</param>
/// <param name="TractionCoefficient">Available tyre or track friction as a fraction in 0–1.</param>
/// <param name="RollingResistanceCoefficient">Fraction of tractive effort lost to rolling resistance, in 0–1.</param>
/// <param name="IsTraversable">False when a ground vehicle cannot occupy this material at all.</param>
public readonly record struct SurfaceTraction(
    SurfaceType Surface,
    double TractionCoefficient,
    double RollingResistanceCoefficient,
    bool IsTraversable)
{
    /// <summary>Fraction of a vehicle's flat-ground top speed this surface alone permits.</summary>
    /// <remarks>
    /// Traction is measured against <see cref="GroundSurfaces.ReferenceTractionCoefficient"/> —
    /// dry pavement, the best surface in the table — so the factor is one there and falls off as
    /// grip is lost. Rolling resistance is then subtracted directly, being already a fraction. A
    /// non-traversable surface yields zero rather than a small number, so no arithmetic
    /// downstream can turn it back into "very slow".
    /// </remarks>
    public double SpeedFactor => IsTraversable
        ? Math.Clamp(TractionCoefficient / GroundSurfaces.ReferenceTractionCoefficient, 0.0, 1.0)
            * Math.Clamp(1.0 - RollingResistanceCoefficient, 0.0, 1.0)
        : 0.0;
}

/// <summary>The documented traction table every ground mobility calculation reads from.</summary>
/// <remarks>
/// Advisory figures for decision support, chosen as defensible mid-range values for each class
/// of ground rather than measured for any particular vehicle. They live in one place so that a
/// change to "how bad is vegetation" moves the speed ceiling, the immobilisation test and the
/// route cost together instead of drifting between three copies.
/// </remarks>
public static class GroundSurfaces
{
    /// <summary>Traction of the best surface in the table; the denominator every other is scaled against.</summary>
    public const double ReferenceTractionCoefficient = 0.95;

    /// <summary>Fraction of traction lost at full precipitation intensity.</summary>
    /// <remarks>
    /// Wet ground grips worse. Applied multiplicatively to the table value, so a heavy shower
    /// derates a speed ceiling and can, on already-marginal ground, tip a vehicle into the
    /// immobilised classification — which is the honest outcome, not a modelling accident.
    /// </remarks>
    public const double PrecipitationTractionLoss = 0.25;

    /// <summary>Traction below which a vehicle is treated as unable to move at all.</summary>
    /// <remarks>
    /// A hard floor beneath the slope-dependent test in <see cref="TerrainContact"/>. Grip this
    /// low will not turn a wheel on the level, let alone on a grade.
    /// </remarks>
    public const double ImmobilisingTractionCoefficient = 0.05;

    /// <summary>Traction and resistance for a surface material.</summary>
    /// <param name="surface">Material classified by the environment sampler.</param>
    /// <returns>The row for <paramref name="surface"/>; unknown materials fall back to vegetation.</returns>
    public static SurfaceTraction For(SurfaceType surface) => surface switch
    {
        // Sealed pavement: the reference surface. Best grip, least resistance.
        SurfaceType.Urban => new SurfaceTraction(SurfaceType.Urban, 0.95, 0.02, true),

        // Compacted soil, gravel and rock: good grip, noticeably more resistance than pavement.
        SurfaceType.BareGround => new SurfaceTraction(SurfaceType.BareGround, 0.85, 0.05, true),

        // Water is a blocked cell, not a slow one. Zero traction and full resistance are recorded
        // for completeness; the traversability flag is what callers must branch on.
        SurfaceType.Water => new SurfaceTraction(SurfaceType.Water, 0.0, 1.0, false),

        // Vegetation: soft, uneven, and the conservative default for anything unclassified.
        _ => new SurfaceTraction(SurfaceType.Vegetation, 0.75, 0.10, true),
    };
}

/// <summary>Figures terrain contact needs that <see cref="GroundProfile"/> does not declare.</summary>
/// <remarks>
/// Every value here is <b>derived</b> from the profile, not measured, and each is documented
/// with the inference it rests on. They are kept together, and out of the profile, for two
/// reasons: the profile is the integrator's contract and should not grow fields only the contact
/// solver reads, and a derived figure that looks like a declared one invites someone to tune it
/// as though it were surveyed.
/// <para>
/// If a profile later declares a real ride height or a real centre-of-mass height, the
/// corresponding helper here should be deleted rather than left as a silent second opinion.
/// </para>
/// </remarks>
public static class GroundContactGeometry
{
    /// <summary>
    /// Fraction of the physical tipping angle that a profile's declared operational cross-slope
    /// limit represents.
    /// </summary>
    /// <remarks>
    /// <see cref="GroundProfile.MaxSafeCrossSlopeRad"/> is an operating limit set with margin in
    /// hand, not the angle at which the vehicle goes over. Recovering the tipping angle by
    /// dividing it back out is an inference, and it is the honest one available: the profile
    /// carries no track width to centre-of-mass-height ratio, and inventing one from the
    /// footprint would produce the same angle for every platform, which is worse than useless as
    /// a discriminator.
    /// </remarks>
    public const double OperationalCrossSlopeMargin = 0.6;

    /// <summary>Smallest useful terrain-normal half-spacing, in metres.</summary>
    /// <remarks>Mirrors the environment sampler's own floor: below it the height field's high-frequency octaves dominate.</remarks>
    private const double MinNormalSpacingM = 0.25;

    /// <summary>Speed the normal filter's time constant is derived against, in metres per second.</summary>
    /// <remarks>
    /// A fixed reference rather than the vehicle's live speed. Deriving the smoothing window from
    /// live speed would make the published attitude a function of history in a way that is
    /// awkward to replay and impossible to unit-test with literals. A constant keeps the window a
    /// property of the vehicle's geometry alone.
    /// </remarks>
    private const double SmoothingReferenceSpeedMps = 2.0;

    /// <summary>Coarsest route sampling spacing, in metres, whatever the footprint.</summary>
    private const double MinRouteSampleSpacingM = 0.5;

    /// <summary>Height of the published body origin above the terrain surface, in metres.</summary>
    /// <remarks>
    /// Taken as the running gear's ground clearance, for which
    /// <see cref="GroundProfile.MaxStepHeightM"/> is the closest declared proxy: a platform that
    /// mounts a step of a given height sits about that far off the ground. The published pose is
    /// therefore the chassis underside, and the client's geometry is positioned relative to it.
    /// </remarks>
    /// <param name="profile">Platform to derive for.</param>
    /// <returns>Ride height in metres.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static double RideHeightM(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Math.Max(0.0, profile.MaxStepHeightM);
    }

    /// <summary>Cross-slope at which the platform is taken to tip, in radians.</summary>
    /// <remarks>
    /// An <b>advisory</b> reference for how close a vehicle is to its limit, never a certified
    /// tipping threshold: it is quasi-static, ignores suspension travel and load shift, and is
    /// inferred from the operational limit rather than from a mass distribution. See
    /// <see cref="OperationalCrossSlopeMargin"/>.
    /// </remarks>
    /// <param name="profile">Platform to derive for.</param>
    /// <returns>The inferred static stability angle in radians.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static double StaticStabilityAngleRad(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Math.Min(profile.MaxSafeCrossSlopeRad / OperationalCrossSlopeMargin, Math.PI / 2.0);
    }

    /// <summary>Half-spacing to sample the terrain normal at, in metres.</summary>
    /// <remarks>
    /// The footprint radius, floored: a vehicle rides on its whole contact patch, so sampling the
    /// normal far finer than that patch makes it chatter on procedural noise, which shows up as a
    /// stationary rover twitching in pitch and roll.
    /// </remarks>
    /// <param name="profile">Platform to derive for.</param>
    /// <returns>Central-difference half-spacing in metres.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static double NormalSpacingM(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Math.Max(MinNormalSpacingM, profile.FootprintRadiusM);
    }

    /// <summary>Time constant of the terrain-normal low-pass filter, in seconds.</summary>
    /// <remarks>Geometry only — how long the footprint takes to cross its own radius at a reference speed.</remarks>
    /// <param name="profile">Platform to derive for.</param>
    /// <returns>Filter time constant in seconds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static double NormalFilterTimeConstantSeconds(GroundProfile profile) =>
        NormalSpacingM(profile) / SmoothingReferenceSpeedMps;

    /// <summary>Spacing between route samples, in metres.</summary>
    /// <remarks>
    /// Derived from the footprint so a wide vehicle is not asked about detail it cannot fit
    /// between, and — critically — so the sample count along a segment is a function of geometry
    /// alone. A spacing that varied with speed, battery or terrain would make two replays of the
    /// same route disagree about how many samples they took.
    /// </remarks>
    /// <param name="profile">Platform to derive for.</param>
    /// <returns>Sample spacing in metres.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static double RouteSampleSpacingM(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Math.Max(MinRouteSampleSpacingM, profile.FootprintRadiusM);
    }
}
