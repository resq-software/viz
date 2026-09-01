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

/// <summary>Low-pass filter state for the terrain normal under one vehicle.</summary>
/// <remarks>
/// The filter's memory lives here, in a value the caller owns and threads through, rather than
/// as a field inside the contact solver. That is what keeps
/// <see cref="IStepDrivenAsset.Step"/> a pure function of its context and the asset's own
/// state: the solver is handed the previous state and returns the next one, so the same inputs
/// always produce the same outputs and a test can drive a whole filter response with literals.
/// </remarks>
/// <param name="NormalEus">Last filtered unit normal in the scene frame.</param>
/// <param name="IsInitialised">False until the first sample, so the filter starts on the terrain rather than easing onto it from vertical.</param>
public readonly record struct TerrainNormalFilter(Vector3 NormalEus, bool IsInitialised)
{
    /// <summary>A filter that has seen nothing yet.</summary>
    public static TerrainNormalFilter Uninitialised => default;

    /// <summary>Folds one measured normal into the filter.</summary>
    /// <remarks>
    /// First-order low pass with the coefficient derived from the timestep,
    /// <c>alpha = 1 - exp(-dt / tau)</c>, so the response is the same whether the world runs at
    /// 60 Hz or 10 Hz. A fixed per-step alpha — the obvious shortcut — would make a rover
    /// visibly steadier at a lower tick rate, which is a physics result that depends on frame
    /// rate and therefore a bug.
    /// <para>
    /// The blend is a linear interpolation followed by a renormalisation rather than a
    /// spherical one: successive terrain normals differ by a few degrees at most, where the two
    /// agree to well under the precision anything downstream cares about, and the cheap form
    /// has no branch on the angle between them.
    /// </para>
    /// </remarks>
    /// <param name="measuredNormalEus">Unit terrain normal sampled this step.</param>
    /// <param name="deltaSeconds">Timestep in seconds. Non-positive values pass the measurement straight through.</param>
    /// <param name="timeConstantSeconds">Filter time constant in seconds; see <see cref="GroundContactGeometry.NormalFilterTimeConstantSeconds"/>.</param>
    /// <returns>The filter state after this sample.</returns>
    public TerrainNormalFilter Blend(
        Vector3 measuredNormalEus, double deltaSeconds, double timeConstantSeconds)
    {
        var measured = Normalise(measuredNormalEus, Vector3.UnitY);

        if (!IsInitialised || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0.0
            || !double.IsFinite(timeConstantSeconds) || timeConstantSeconds <= 0.0)
        {
            return new TerrainNormalFilter(measured, true);
        }

        double alpha = 1.0 - Math.Exp(-deltaSeconds / timeConstantSeconds);
        var blended = Vector3.Lerp(NormalEus, measured, (float)Math.Clamp(alpha, 0.0, 1.0));
        return new TerrainNormalFilter(Normalise(blended, measured), true);
    }

    /// <summary>Normalises a vector, falling back when it is degenerate rather than propagating a NaN.</summary>
    private static Vector3 Normalise(Vector3 value, Vector3 fallback)
    {
        float length = value.Length();
        return float.IsFinite(length) && length > 1e-6f ? value / length : fallback;
    }
}

/// <summary>Which constraint is binding on a vehicle at one point on the ground.</summary>
/// <remarks>
/// A typed cause rather than only a message, so a planner can map it onto its own vocabulary
/// without matching English and without re-deriving what the contact solver already worked out.
/// <see cref="TerrainContactState.LimitReason"/> renders the same value as a stable token for
/// logs and for the wire.
/// </remarks>
public enum TerrainLimit
{
    /// <summary>Nothing is binding; the vehicle is at its nominal speed for this surface.</summary>
    None,

    /// <summary>The point is water. A ground vehicle cannot be here at all.</summary>
    Water,

    /// <summary>
    /// Cross-slope has reached the platform's declared operational limit, but not the angle at
    /// which it is taken to tip. <b>Advisory.</b>
    /// </summary>
    /// <remarks>
    /// This is the lower of the two cross-slope bands and it deliberately does not refuse
    /// anything. <see cref="GroundProfile.MaxSafeCrossSlopeRad"/> is an operating limit set with
    /// margin in hand, so a vehicle past it is being <em>advised</em> to get off the bank, not
    /// told it may not. Treating it as a refusal is what strands a rover: every heading it could
    /// leave on crosses the same slope, so a hard block on this band removes the only way out of
    /// it. The refusal band is <see cref="CrossSlopeUnstable"/>.
    /// </remarks>
    CrossSlope,

    /// <summary>Grade exceeds what the platform can climb.</summary>
    Grade,

    /// <summary>Grip is insufficient for the slope, or absent altogether.</summary>
    Traction,

    /// <summary>Passable, with the grade costing the most speed.</summary>
    GradeDerate,

    /// <summary>Passable, with the cross-slope costing the most speed.</summary>
    CrossSlopeDerate,

    /// <summary>Passable, with the surface material costing the most speed.</summary>
    SurfaceDerate,

    /// <summary>Passable, with a zone's advisory speed ceiling binding.</summary>
    ZoneSpeedLimit,

    /// <summary>
    /// Cross-slope has reached the platform's inferred static stability angle — the physical
    /// band, not the operational one.
    /// </summary>
    /// <remarks>
    /// Beyond <see cref="GroundContactGeometry.StaticStabilityAngleRad"/> the quasi-static model
    /// no longer says "close to the limit", it says "past it", and that is the band a route
    /// preview may refuse outright. Appended to the enum rather than inserted beside
    /// <see cref="CrossSlope"/> so no existing member is renumbered.
    /// <para>
    /// Still advisory in the sense every figure here is: the angle is inferred from an
    /// operational limit rather than from a mass distribution, ignores suspension travel and
    /// load shift, and asserts nothing about a real vehicle.
    /// </para>
    /// </remarks>
    CrossSlopeUnstable,
}

/// <summary>Advisory classification of a vehicle's relationship with the ground beneath it.</summary>
/// <remarks>Decision support for an operator. None of these values asserts a safety guarantee.</remarks>
public enum TerrainContactStatus
{
    /// <summary>The vehicle can move at its full speed for this surface.</summary>
    WithinLimits,

    /// <summary>The vehicle can move, but its advisory speed ceiling has been reduced.</summary>
    SpeedDerated,

    /// <summary>Cross-slope is at or past the platform's operational cross-slope limit.</summary>
    /// <remarks>
    /// Covers <b>both</b> cross-slope bands, because the finding an operator has to act on is the
    /// same in each: this vehicle is leaning further than it should be.
    /// <see cref="TerrainContactState.Limit"/> says which band —
    /// <see cref="TerrainLimit.CrossSlope"/> for the advisory one,
    /// <see cref="TerrainLimit.CrossSlopeUnstable"/> for the one past the inferred tipping
    /// angle — and <see cref="TerrainContactState.RolloverRiskFraction"/> quantifies it. The
    /// status is deliberately not split, so nothing downstream that today treats
    /// <c>RolloverRisk</c> as "raise the alarm" can start missing the worse of the two cases.
    /// </remarks>
    RolloverRisk,

    /// <summary>The vehicle cannot make progress here at all.</summary>
    Immobilised,
}

/// <summary>Everything terrain contact resolves for one vehicle at one point, for one step.</summary>
/// <remarks>
/// Grade and cross-slope are carried as separate signed quantities rather than as one slope
/// magnitude because they gate different failures: grade decides whether the vehicle can climb
/// at all, cross-slope decides whether it rolls over. A single "18 degrees" cannot answer
/// either question, and answering it wrongly is the difference between a slow route and a
/// vehicle on its roof.
/// </remarks>
/// <param name="PositionEus">Settled body-origin position in the scene frame, in metres.</param>
/// <param name="OrientationEusFromFlu">Attitude mapping FLU body axes into the scene frame.</param>
/// <param name="FilteredNormalEus">Filtered unit terrain normal the attitude was resolved from.</param>
/// <param name="GradeRad">Pitch along the direction of travel, in radians. Positive is nose-up, climbing.</param>
/// <param name="CrossSlopeRad">Roll across the direction of travel, in radians. Positive is starboard-down.</param>
/// <param name="SlopeRad">Magnitude of the terrain gradient, in radians, independent of heading.</param>
/// <param name="Surface">Traction row for the material under the vehicle.</param>
/// <param name="TractionCoefficient">Available traction after weather derating, as a fraction in 0–1.</param>
/// <param name="RolloverRiskFraction">Cross-slope as a fraction of the static stability angle, clamped to 0–1. Advisory.</param>
/// <param name="SafeSpeedMps">Advisory speed ceiling after derating, in metres per second. Zero when immobilised.</param>
/// <param name="HasRolloverRisk">True when cross-slope has passed the platform's operational limit. Advisory; see <see cref="TerrainLimit.CrossSlope"/>.</param>
/// <param name="IsImmobilised">True when the vehicle cannot make progress.</param>
/// <param name="Status">Single-value summary; see the type's remarks for precedence.</param>
/// <param name="Limit">Typed cause of the binding constraint.</param>
public sealed record TerrainContactState(
    Vector3 PositionEus,
    Quaternion OrientationEusFromFlu,
    Vector3 FilteredNormalEus,
    double GradeRad,
    double CrossSlopeRad,
    double SlopeRad,
    SurfaceTraction Surface,
    double TractionCoefficient,
    double RolloverRiskFraction,
    double SafeSpeedMps,
    bool HasRolloverRisk,
    bool IsImmobilised,
    TerrainContactStatus Status,
    TerrainLimit Limit)
{
    /// <summary>Stable machine-readable token for <see cref="Limit"/>, or null when nothing binds.</summary>
    /// <remarks>
    /// Derived rather than stored so the token and the typed cause cannot disagree. Code branches
    /// on <see cref="Limit"/>; this is for logs, events and the wire.
    /// </remarks>
    public string? LimitReason => Limit switch
    {
        TerrainLimit.Water => "ground.blocked.water",
        TerrainLimit.CrossSlope => "ground.rollover.cross-slope",
        TerrainLimit.CrossSlopeUnstable => "ground.rollover.cross-slope.unstable",
        TerrainLimit.Grade => "ground.immobilised.grade",
        TerrainLimit.Traction => "ground.immobilised.traction",
        TerrainLimit.GradeDerate => "ground.derated.grade",
        TerrainLimit.CrossSlopeDerate => "ground.derated.cross-slope",
        TerrainLimit.SurfaceDerate => "ground.derated.surface",
        TerrainLimit.ZoneSpeedLimit => "ground.derated.zone",
        _ => null,
    };

    /// <summary>True when cross-slope has reached the platform's inferred static stability angle.</summary>
    /// <remarks>
    /// Derived from <see cref="RolloverRiskFraction"/>, which is the cross-slope measured against
    /// that angle and clamped at one, rather than from <see cref="Limit"/>. That matters: the
    /// limit carries a precedence — water outranks everything — so a vehicle in water on a
    /// vertical bank would report <see cref="TerrainLimit.Water"/> and this flag would go quiet
    /// exactly when the lean is worst. The fraction has no precedence and cannot.
    /// <para>
    /// This is the band a route preview may refuse. It remains <b>advisory</b>: see
    /// <see cref="GroundContactGeometry.StaticStabilityAngleRad"/> for what the angle is and is
    /// not.
    /// </para>
    /// </remarks>
    public bool IsBeyondStaticStability => RolloverRiskFraction >= 1.0;
}

/// <summary>A resolved contact plus the filter state to carry into the next step.</summary>
/// <param name="Contact">What the solver resolved this step.</param>
/// <param name="Filter">Filter state the caller must store and pass back next step.</param>
public readonly record struct TerrainContactResult(TerrainContactState Contact, TerrainNormalFilter Filter);

/// <summary>A physical impact between a vehicle and the terrain.</summary>
/// <remarks>
/// Deliberately a different type, produced by a different function, from anything in
/// <see cref="Traversability"/>. A traversability block is a <em>planning</em> outcome: it is
/// discovered before the vehicle moves, it costs nothing, and it makes a command rejectable. A
/// collision is a <em>physical</em> outcome: it has already happened, it has an impact speed,
/// and it belongs in the event stream. Sharing one code path between them would mean either
/// raising collision events while previewing a route the operator never accepted, or silently
/// downgrading a real impact to "this route is expensive".
/// </remarks>
/// <param name="HasCollided">True when the vehicle struck terrain it cannot mount.</param>
/// <param name="StepHeightM">Height of the obstructing rise, in metres.</param>
/// <param name="ImpactSpeedMps">Speed along the direction of travel at the moment of contact, in metres per second.</param>
/// <param name="Code">Stable event code, or null when nothing was struck.</param>
public readonly record struct GroundStepCollision(
    bool HasCollided,
    double StepHeightM,
    double ImpactSpeedMps,
    string? Code)
{
    /// <summary>Event code raised when a vehicle strikes a step taller than it can mount.</summary>
    public const string StepCode = "ground.collision.step";

    /// <summary>Nothing was struck.</summary>
    public static GroundStepCollision None => new(false, 0.0, 0.0, null);
}
