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

// The value half of the water constraints: the vocabulary a mask, a route preview and a
// shoreline contact are reported in. Split from the functions in WaterConstraints.cs so neither
// file outgrows a reading, and because these types are what a consumer binds against while the
// functions are what the surface domain calls.

/// <summary>How a planner should treat one point of water for one hull.</summary>
/// <remarks>
/// Four values, for the same reason the ground domain needs four: a planner that cannot tell
/// <see cref="Unknown"/> from <see cref="Blocked"/> either refuses every unsurveyed patch or
/// sails confidently into it, and both are wrong in opposite directions.
/// </remarks>
public enum WaterNavigability
{
    /// <summary>Open water with the hull's margin intact.</summary>
    Navigable,

    /// <summary>Navigable, but close enough to a limit that an operator should be told.</summary>
    Cautionary,

    /// <summary>Not enough information to classify. Neither an invitation nor a refusal.</summary>
    Unknown,

    /// <summary>Not navigable by this hull. A route through here must be refused.</summary>
    Blocked,
}

/// <summary>Why a point of water got the classification it did.</summary>
/// <remarks>
/// An enum with stable string codes rather than prose, so the UI can explain a refused target
/// and a test can assert the cause without matching English.
/// </remarks>
public enum WaterBlockReason
{
    /// <summary>Nothing constrains this point.</summary>
    None,

    /// <summary>Dry land. There is no water column here at all.</summary>
    DryLand,

    /// <summary>Water, but shallower than the hull's draft plus its safe margin.</summary>
    InsufficientDepth,

    /// <summary>Water shallower than the draft alone: the hull would be on the bed.</summary>
    Grounded,

    /// <summary>A zone that prohibits entry covers this point, whatever the depth.</summary>
    ProhibitedZone,

    /// <summary>Navigable, but clearance is inside the band worth mentioning. Advisory only.</summary>
    MarginalDepth,

    /// <summary>Navigable, but a zone imposes an advisory speed ceiling here.</summary>
    ZoneSpeedLimit,

    /// <summary>The environment reported no usable water data for this point.</summary>
    NoWaterData,
}

/// <summary>One evaluated point of water, for one hull.</summary>
/// <remarks>
/// Carries the clearance state whole rather than flattening it, so depth, draft and clearance
/// stay three separate quantities all the way to the consumer.
/// </remarks>
/// <param name="PositionEus">Point evaluated, in the scene frame.</param>
/// <param name="Class">How a planner should treat it.</param>
/// <param name="Reason">Why it got that classification.</param>
/// <param name="IsWater">Whether the environment reported water here at all.</param>
/// <param name="WaterSurfaceElevationM">Water-surface elevation in metres, or null on dry land.</param>
/// <param name="BedElevationM">Bed or ground elevation under the point, in metres.</param>
/// <param name="Clearance">Depth, draft and under-keel clearance, evaluated for this hull.</param>
/// <param name="AdvisorySpeedLimitMps">Tightest zone speed ceiling here, in metres per second, or null when none applies.</param>
/// <param name="RiskWeight">Advisory risk of this point as a fraction in 0–1, where 1 is refused.</param>
public sealed record WaterSample(
    Vector3 PositionEus,
    WaterNavigability Class,
    WaterBlockReason Reason,
    bool IsWater,
    double? WaterSurfaceElevationM,
    double BedElevationM,
    UnderKeelClearanceState Clearance,
    double? AdvisorySpeedLimitMps,
    double RiskWeight)
{
    /// <summary>True when a hull may occupy this point.</summary>
    /// <remarks>Cautionary counts as navigable: it is advice, not a refusal.</remarks>
    public bool IsNavigable => Class is WaterNavigability.Navigable or WaterNavigability.Cautionary;

    /// <summary>True when a route may not pass through this point.</summary>
    public bool IsBlocked => Class == WaterNavigability.Blocked;

    /// <summary>Stable machine-readable code for <see cref="Reason"/>.</summary>
    public string ReasonCode => WaterConstraints.ReasonCode(Reason);
}

/// <summary>What a deterministic sweep along a straight water segment found.</summary>
/// <remarks>
/// A planning product, so the UI can preview a transit and refuse a target before the command
/// is accepted rather than dispatching a vessel and watching it stop halfway. It carries no
/// notion of impact: a segment that crosses a beach is <see cref="WaterNavigability.Blocked"/>
/// here, and only becomes a <see cref="ShorelineContact"/> if a vessel is actually driven into
/// it.
/// <para>
/// Advisory throughout, over a procedural bed rather than a survey. A navigable verdict means
/// nothing sampled contradicts the hull's envelope; it is not an assurance the passage is safe,
/// and it makes no claim about any navigation regulation.
/// </para>
/// </remarks>
/// <param name="IsNavigable">True when no sample along the segment blocks it.</param>
/// <param name="LengthM">Horizontal length of the segment, in metres.</param>
/// <param name="SampleCount">Number of samples taken. A function of geometry alone.</param>
/// <param name="SampleSpacingM">Distance between consecutive samples, in metres.</param>
/// <param name="WorstClass">Worst classification any sample received.</param>
/// <param name="BlockingReason">Reason the first blocking sample gave, or <see cref="WaterBlockReason.None"/>.</param>
/// <param name="BlockingPointEus">Where the first blocking sample sits, or null when the route is clear.</param>
/// <param name="BlockingDistanceM">Distance along the segment to the first blocking sample, in metres.</param>
/// <param name="ShallowestDepthM">Smallest water column found, in metres. Zero where the route crosses dry land.</param>
/// <param name="MinimumClearanceM">Smallest under-keel clearance found, in metres. Negative where the hull would be into the bed.</param>
/// <param name="AccumulatedRisk">Risk integrated over the segment, in risk-metres. Comparable between routes, meaningful only in relative terms.</param>
public sealed record RouteWaterCheck(
    bool IsNavigable,
    double LengthM,
    int SampleCount,
    double SampleSpacingM,
    WaterNavigability WorstClass,
    WaterBlockReason BlockingReason,
    Vector3? BlockingPointEus,
    double BlockingDistanceM,
    double ShallowestDepthM,
    double MinimumClearanceM,
    double AccumulatedRisk)
{
    /// <summary>Stable machine-readable code for <see cref="BlockingReason"/>.</summary>
    public string BlockingReasonCode => WaterConstraints.ReasonCode(BlockingReason);
}

/// <summary>A vessel meeting the edge of navigable water.</summary>
/// <remarks>
/// Deliberately a different type, from a different function, from anything a route preview
/// produces. A blocked route sample is a <em>planning</em> outcome: found before the vessel
/// moves, costing nothing, and making a command rejectable. A contact is a <em>physical</em>
/// outcome: it has happened, it has a speed, and it belongs in the event stream. One code path
/// for both would either raise contacts while previewing a route nobody accepted or downgrade a
/// real grounding to "this route is expensive".
/// <para>
/// Every contact is recoverable. Running aground stops a vessel; it does not disable it, and
/// nothing here may be used to refuse the commands that back it into deeper water.
/// </para>
/// </remarks>
/// <param name="HasContacted">True when the vessel was stopped by the edge of navigable water.</param>
/// <param name="Reason">What stopped it.</param>
/// <param name="ImpactSpeedMps">Speed along the direction of travel at the moment of contact, in metres per second.</param>
/// <param name="PositionEus">Where the vessel was held, in the scene frame.</param>
public readonly record struct ShorelineContact(
    bool HasContacted,
    WaterBlockReason Reason,
    double ImpactSpeedMps,
    Vector3 PositionEus)
{
    /// <summary>Event code raised when a vessel is stopped by dry land.</summary>
    public const string ShorelineCode = "surface.collision.shoreline";

    /// <summary>Event code raised when a vessel is stopped by water too shallow for its hull.</summary>
    public const string ShoalCode = "surface.collision.shoal";

    /// <summary>Event code raised when a vessel is turned back by a prohibited zone.</summary>
    public const string ZoneCode = "surface.blocked.zone";

    /// <summary>Nothing was met.</summary>
    public static ShorelineContact None => default;

    /// <summary>Stable event code for this contact, or null when nothing was met.</summary>
    public string? Code => Reason switch
    {
        WaterBlockReason.DryLand => ShorelineCode,
        WaterBlockReason.InsufficientDepth or WaterBlockReason.Grounded => ShoalCode,
        WaterBlockReason.ProhibitedZone => ZoneCode,
        _ => null,
    };

    /// <summary>How much operator attention this contact deserves.</summary>
    /// <remarks>
    /// Striking the shore or a shoal is an alert; being turned back at the edge of a zone is a
    /// warning, because nothing has been hit.
    /// </remarks>
    public AssetEventSeverity Severity => Reason switch
    {
        WaterBlockReason.DryLand or WaterBlockReason.InsufficientDepth or WaterBlockReason.Grounded
            => AssetEventSeverity.Alert,
        _ => AssetEventSeverity.Warning,
    };

    /// <summary>Always true: every state reachable through a contact has a way out of it.</summary>
    /// <remarks>
    /// Stated as a property so the guarantee is visible where a caller might otherwise be
    /// tempted to latch a vessel into a permanent fault. A grounded vessel keeps a derated but
    /// non-zero speed ceiling and must keep accepting every command an operator would use to
    /// recover it.
    /// </remarks>
    public bool IsRecoverable => true;
}

/// <summary>Where a vessel ends up after the water mask has had its say.</summary>
/// <param name="PositionEus">Accepted horizontal position in the scene frame. Vertical placement on the water surface is the caller's.</param>
/// <param name="IsBlocked">True when the proposed move was refused and the vessel was held.</param>
/// <param name="Contact">The contact to raise, or <see cref="ShorelineContact.None"/>.</param>
/// <param name="Accepted">The water sample at the accepted position.</param>
public readonly record struct WaterMotionResolution(
    Vector3 PositionEus,
    bool IsBlocked,
    ShorelineContact Contact,
    WaterSample Accepted)
{
    /// <summary>True when the accepted position is not navigable — the vessel is aground or ashore.</summary>
    /// <remarks>
    /// Derived from the accepted sample rather than stored, so the flag cannot drift from the
    /// classification it summarises. A caller raising an event on this must compare it against
    /// the previous step's value: raised on a level rather than on an edge it would fire on
    /// every tick for as long as the vessel sat there.
    /// </remarks>
    public bool IsAground => !Accepted.IsNavigable;
}

