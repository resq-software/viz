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

/// <summary>How a planner should treat one patch of ground for one platform.</summary>
/// <remarks>
/// Four values rather than a passable/impassable bit, because a planner that cannot tell
/// <see cref="Unknown"/> from <see cref="Blocked"/> either refuses to route through unsurveyed
/// ground or drives confidently into it. Both are wrong, and they are wrong in opposite
/// directions.
/// </remarks>
public enum TraversabilityClass
{
    /// <summary>Passable at or near the platform's nominal speed for this surface.</summary>
    Traversable,

    /// <summary>Passable, but slow enough that a planner should prefer a way around.</summary>
    Costly,

    /// <summary>Not enough information to classify. Neither an invitation nor a refusal.</summary>
    Unknown,

    /// <summary>Not passable by this platform. A route through here must be refused.</summary>
    Blocked,
}

/// <summary>Why a patch of ground got the classification it did.</summary>
/// <remarks>
/// Kept as an enum with stable string codes rather than prose, so the UI can explain a refused
/// target and a test can assert on the cause without matching English.
/// </remarks>
public enum TraversabilityReason
{
    /// <summary>Nothing constrains this patch.</summary>
    None,

    /// <summary>Water. A ground vehicle cannot occupy it at any speed.</summary>
    Water,

    /// <summary>A zone that prohibits entry covers this patch.</summary>
    ProhibitedZone,

    /// <summary>Grade steeper than the platform can climb, even heading straight up it.</summary>
    GradeExceeded,

    /// <summary>
    /// Cross-slope past the platform's inferred static stability angle on this heading — the
    /// physical band, not the operational one.
    /// </summary>
    /// <remarks>
    /// Raised from <see cref="TerrainLimit.CrossSlopeUnstable"/> only. Merely reaching
    /// <see cref="GroundProfile.MaxSafeCrossSlopeRad"/> is <see cref="RolloverRiskAdvisory"/>
    /// instead, and is costly rather than blocked: that limit is set with margin in hand, and
    /// refusing it outright leaves a vehicle already on the bank with no heading to leave by.
    /// </remarks>
    CrossSlopeExceeded,

    /// <summary>A vertical step taller than the platform can mount.</summary>
    StepHeightExceeded,

    /// <summary>Traction too low for the platform to make progress.</summary>
    LowTraction,

    /// <summary>Passable, but the grade costs enough speed to be worth avoiding.</summary>
    SteepGrade,

    /// <summary>Passable, but the cross-slope costs enough speed to be worth avoiding.</summary>
    SteepCrossSlope,

    /// <summary>Passable, but the surface itself is slow going.</summary>
    PoorSurface,

    /// <summary>Passable, but a zone imposes a speed ceiling here.</summary>
    ZoneSpeedLimit,

    /// <summary>The terrain reported no usable data for this patch.</summary>
    NoTerrainData,

    /// <summary>
    /// Passable, but cross-slope has reached the platform's operational limit and the rollover
    /// advisory is standing.
    /// </summary>
    /// <remarks>
    /// Named separately from <see cref="SteepCrossSlope"/>, which is an ordinary speed derate, so
    /// the UI can say "this route leans the vehicle past its operating limit" rather than "this
    /// route is slow" — the two deserve very different words in front of an operator. Named
    /// separately from <see cref="CrossSlopeExceeded"/> so downgrading the classification from
    /// blocked to costly does not also downgrade what the operator is told. Appended to the enum
    /// so no existing member is renumbered.
    /// </remarks>
    RolloverRiskAdvisory,
}

/// <summary>One evaluated point on the ground, for one platform.</summary>
/// <param name="PositionEus">Point evaluated, in the scene frame.</param>
/// <param name="Class">How a planner should treat it.</param>
/// <param name="Reason">Why it got that classification.</param>
/// <param name="GradeRad">Signed pitch along the evaluated heading, in radians.</param>
/// <param name="CrossSlopeRad">Signed roll across the evaluated heading, in radians.</param>
/// <param name="SlopeRad">Heading-independent terrain gradient magnitude, in radians.</param>
/// <param name="TractionCoefficient">Available traction as a fraction in 0–1.</param>
/// <param name="SafeSpeedMps">Advisory speed ceiling here, in metres per second.</param>
/// <param name="CostMultiplier">Cost of a metre here relative to a metre of flat pavement. Infinite when blocked.</param>
public sealed record TraversabilitySample(
    Vector3 PositionEus,
    TraversabilityClass Class,
    TraversabilityReason Reason,
    double GradeRad,
    double CrossSlopeRad,
    double SlopeRad,
    double TractionCoefficient,
    double SafeSpeedMps,
    double CostMultiplier)
{
    /// <summary>True when a route may not pass through this point.</summary>
    public bool IsBlocked => Class == TraversabilityClass.Blocked;

    /// <summary>Stable machine-readable code for <see cref="Reason"/>.</summary>
    public string ReasonCode => Traversability.ReasonCode(Reason);
}

/// <summary>What a deterministic sweep along a straight segment found.</summary>
/// <remarks>
/// This is what lets the UI preview a route and refuse a blocked target <b>before</b> the
/// command is accepted, rather than dispatching a rover and watching it stop halfway. It is a
/// planning product and carries no notion of impact: a route that crosses a wall is
/// <see cref="TraversabilityClass.Blocked"/> here, and only becomes a collision if a vehicle is
/// actually driven into it. See <see cref="GroundStepCollision"/> for that other outcome.
/// <para>
/// Advisory throughout. A traversable verdict means nothing in the sampled height field
/// contradicts the platform's profile; it is not a guarantee that the route is safe to drive.
/// </para>
/// </remarks>
/// <param name="IsTraversable">True when no sample along the segment blocks it.</param>
/// <param name="LengthM">Horizontal length of the segment, in metres.</param>
/// <param name="SampleCount">Number of samples taken. A function of geometry alone.</param>
/// <param name="SampleSpacingM">Distance between consecutive samples, in metres.</param>
/// <param name="WorstClass">Worst classification any sample received.</param>
/// <param name="BlockingReason">Reason the first blocking sample gave, or <see cref="TraversabilityReason.None"/>.</param>
/// <param name="BlockingPointEus">Where the first blocking sample sits, or null when the route is clear.</param>
/// <param name="BlockingDistanceM">Distance along the segment to the first blocking sample, in metres.</param>
/// <param name="WorstGradeRad">Signed grade of largest magnitude encountered, in radians.</param>
/// <param name="WorstCrossSlopeRad">Signed cross-slope of largest magnitude encountered, in radians.</param>
/// <param name="WorstStepHeightM">Tallest rise between consecutive samples, in metres.</param>
/// <param name="AccumulatedCost">Route length in equivalent flat-pavement metres. Meaningful only when traversable.</param>
/// <param name="AdvisoryTransitSeconds">Rough transit time at the derated ceilings, in seconds.</param>
public sealed record RouteTraversability(
    bool IsTraversable,
    double LengthM,
    int SampleCount,
    double SampleSpacingM,
    TraversabilityClass WorstClass,
    TraversabilityReason BlockingReason,
    Vector3? BlockingPointEus,
    double BlockingDistanceM,
    double WorstGradeRad,
    double WorstCrossSlopeRad,
    double WorstStepHeightM,
    double AccumulatedCost,
    double AdvisoryTransitSeconds);
