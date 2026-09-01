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

/// <summary>Which stage of a berthing approach a vessel has reached.</summary>
/// <remarks>
/// The stages exist so the speed limit can tighten as the vessel closes and so an abort has
/// somewhere to abort <em>to</em>. They are not decoration: <see cref="Approach"/> tolerates
/// being off the centreline because the vessel is still lining up, while
/// <see cref="Corridor"/> and <see cref="Final"/> do not, and that difference is the whole
/// reason a dock is a structured operation rather than a transit with a smaller tolerance.
/// </remarks>
public enum DockingPhase
{
    /// <summary>No docking operation is running.</summary>
    Inactive,

    /// <summary>Closing on the corridor entry. Off-centreline error is expected and not an abort.</summary>
    Approach,

    /// <summary>Inside the approach corridor, tracking the centreline at a reduced speed.</summary>
    Corridor,

    /// <summary>The last few hull lengths, at the slowest stage speed, lining up on the terminal pose.</summary>
    Final,

    /// <summary>Secured at the terminal pose. Only reachable through this state machine.</summary>
    Moored,

    /// <summary>Abandoned. The vessel is stopped, safe and fully commandable.</summary>
    Aborted,
}

/// <summary>Why a docking operation was abandoned.</summary>
/// <remarks>
/// Every one of these is a condition an operator has to be told about by name. "The dock
/// failed" is not actionable; "the approach corridor is obstructed" and "the vessel ran out of
/// time" call for completely different responses.
/// </remarks>
public enum DockingAbortReason
{
    /// <summary>Nothing was aborted.</summary>
    None,

    /// <summary>The operation exceeded the time its own geometry allowed for it.</summary>
    Timeout,

    /// <summary>The vessel left the approach corridor after entering it.</summary>
    OutsideCorridor,

    /// <summary>The berth or the water on the way to it stopped being navigable.</summary>
    ObstructedApproach,

    /// <summary>Position quality was lost, so the terminal pose can no longer be trusted.</summary>
    PositionLost,

    /// <summary>The vessel passed the berth and is opening the range in the final stage.</summary>
    Overshoot,

    /// <summary>An operator command superseded the operation.</summary>
    OperatorCancelled,
}

/// <summary>The geometry, the staged limits and the terms a berthing approach is flown on.</summary>
/// <remarks>
/// <b>A dock is not a <c>goTo</c> with a tighter tolerance.</b> A transit is finished when the
/// vessel is near a point; a dock is finished when it is at a <em>pose</em> — position and
/// heading — having arrived along a defined line, slowly enough to stop, inside a time budget,
/// with named conditions that abandon the attempt. A plain transit satisfies none of that and
/// must never be treated as completing one.
/// <para>
/// Every stage length and every stage speed here is derived from the hull rather than tuned.
/// Berthing approaches are described in ship lengths and in fractions of manoeuvring speed
/// because that is what actually scales: a 9 m hull needs a longer run and a wider corridor
/// than a 6.5 m one, and hard-coded metres would silently be wrong for both.
/// </para>
/// <para>
/// Advisory. This is a simulation of a berthing approach over a procedural bed, not a berthing
/// procedure, and nothing here asserts that any approach is safe or compliant with anything.
/// </para>
/// </remarks>
/// <param name="BerthEus">Terminal position, in the scene frame.</param>
/// <param name="BerthHeadingRad">Terminal heading, in radians clockwise from true north.</param>
/// <param name="EntryEus">Point the corridor is entered at, in the scene frame. Defines the centreline with <paramref name="BerthEus"/>.</param>
/// <param name="CorridorHalfWidthM">Half-width of the corridor, in metres. Leaving it after entry aborts the operation.</param>
/// <param name="CorridorLengthM">Range at which the corridor stage begins, in metres.</param>
/// <param name="FinalLengthM">Range at which the final stage begins, in metres.</param>
/// <param name="ApproachSpeedMps">Speed ceiling outside the corridor, in metres per second.</param>
/// <param name="CorridorSpeedMps">Speed ceiling inside the corridor, in metres per second.</param>
/// <param name="FinalSpeedMps">Speed ceiling in the final stage, in metres per second.</param>
/// <param name="TimeoutSeconds">Time budget for the whole operation, in seconds.</param>
/// <param name="TerminalToleranceM">Distance from the berth inside which the vessel counts as arrived, in metres.</param>
/// <param name="TerminalHeadingToleranceRad">Heading error the terminal pose tolerates, in radians.</param>
/// <param name="TerminalSpeedMps">
/// Speed of approach to the berth at or below which the vessel counts as secured, in metres per
/// second. This limits the rate at which the hull and the berth converge, which is a
/// <em>ground-relative</em> rate because the berth is a fixed point of the scene. It is
/// deliberately not a limit on speed through the water; see <see cref="Docking.Advance"/> for
/// why confusing the two makes a vessel impossible to berth in any wind.
/// </param>
public sealed record DockingPlan(
    Vector3 BerthEus,
    double BerthHeadingRad,
    Vector3 EntryEus,
    double CorridorHalfWidthM,
    double CorridorLengthM,
    double FinalLengthM,
    double ApproachSpeedMps,
    double CorridorSpeedMps,
    double FinalSpeedMps,
    double TimeoutSeconds,
    double TerminalToleranceM,
    double TerminalHeadingToleranceRad,
    double TerminalSpeedMps)
{
    /// <summary>Range at which the corridor begins, in hull lengths.</summary>
    public const double CorridorLengths = 6.0;

    /// <summary>Range at which the final stage begins, in hull lengths.</summary>
    public const double FinalLengths = 2.0;

    /// <summary>Corridor half-width, in hull beams.</summary>
    /// <remarks>
    /// Two beams either side. Wide enough that ordinary steering error inside a first-order
    /// yaw response does not abort a sound approach; narrow enough that a vessel set sideways
    /// by a cross-current is stopped rather than carried into whatever the berth is attached to.
    /// </remarks>
    public const double CorridorBeams = 2.0;

    /// <summary>Share of the hull's top speed permitted outside the corridor.</summary>
    private const double ApproachSpeedFraction = 0.50;

    /// <summary>Share of the hull's top speed permitted inside the corridor.</summary>
    private const double CorridorSpeedFraction = 0.25;

    /// <summary>Share of the hull's top speed permitted in the final stage.</summary>
    private const double FinalSpeedFraction = 0.12;

    /// <summary>Multiple of the ideal run time the operation is given before it times out.</summary>
    /// <remarks>
    /// Three times the time the whole approach would take at its corridor speed in slack water.
    /// A budget derived from the geometry rather than a fixed number of seconds, so a long
    /// approach is not abandoned halfway and a short one is not left running for minutes; the
    /// factor is the allowance for a foul tide, for the turn onto the centreline, and for the
    /// hull taking a surge time constant to answer each change of speed.
    /// </remarks>
    private const double TimeoutFactor = 3.0;

    /// <summary>Floor under the time budget, in seconds.</summary>
    private const double MinTimeoutSeconds = 60.0;

    /// <summary>Builds a plan for one hull berthing at one point, from the hull's own dimensions.</summary>
    /// <remarks>
    /// The corridor centreline runs from where the vessel is when the command is accepted to the
    /// berth, and the terminal heading is that same bearing: the vessel arrives bow-on along the
    /// line it approached down. That is the honest thing to derive when no berth orientation has
    /// been supplied — inventing one would put the hull alongside a pontoon whose direction this
    /// simulation does not model.
    /// </remarks>
    /// <param name="profile">Hull that will fly the approach.</param>
    /// <param name="vesselEus">Where the vessel is as the operation begins, in the scene frame.</param>
    /// <param name="berthEus">Terminal position, in the scene frame.</param>
    /// <param name="berthHeadingRad">Terminal heading, or null to arrive along the approach bearing.</param>
    /// <returns>A validated plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">A derived term is not usable, which means an input was not finite.</exception>
    public static DockingPlan For(
        SurfaceProfile profile, Vector3 vesselEus, Vector3 berthEus, double? berthHeadingRad = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var run = new Vector3(berthEus.X - vesselEus.X, 0f, berthEus.Z - vesselEus.Z);
        double approachBearing = CoordinateFrames.BearingFromEusVector(run, 0.0);
        double runLengthM = run.Length();

        double corridorSpeed = profile.MaxSpeedMps * CorridorSpeedFraction;

        return new DockingPlan(
            BerthEus: berthEus,
            BerthHeadingRad: berthHeadingRad is { } heading && double.IsFinite(heading)
                ? CoordinateFrames.NormalizeAngle(heading)
                : approachBearing,
            EntryEus: vesselEus,
            CorridorHalfWidthM: CorridorBeams * profile.BeamM,
            CorridorLengthM: CorridorLengths * profile.LengthM,
            FinalLengthM: FinalLengths * profile.LengthM,
            ApproachSpeedMps: profile.MaxSpeedMps * ApproachSpeedFraction,
            CorridorSpeedMps: corridorSpeed,
            FinalSpeedMps: profile.MaxSpeedMps * FinalSpeedFraction,
            TimeoutSeconds: Math.Max(
                MinTimeoutSeconds,
                corridorSpeed > 0.0 ? TimeoutFactor * runLengthM / corridorSpeed : MinTimeoutSeconds),

            // Half a beam plus a metre: close enough alongside to have a line ashore, and not so
            // close that the arrival depends on the last bits of a single-precision position.
            TerminalToleranceM: (0.5 * profile.BeamM) + 1.0,

            // Ten degrees. A hull that answers its helm over a yaw time constant will not hold
            // better than this at a berthing speed, and demanding that it does would leave the
            // operation running until it timed out beside a berth it had already reached.
            TerminalHeadingToleranceRad: 10.0 * Math.PI / 180.0,

            // Half the final-stage ceiling: the hull is still closing on the berth as it is
            // secured, just no faster than half the speed the last stage permitted. Note what
            // this figure is compared against — the closing rate on the berth, never the log
            // reading — because a limit this small is below the leeway a fresh breeze alone
            // puts into a hull's speed through the water.
            TerminalSpeedMps: profile.MaxSpeedMps * FinalSpeedFraction * 0.5).Validated(nameof(profile));
    }

    /// <summary>Unit vector along the corridor centreline, from the entry towards the berth.</summary>
    /// <remarks>
    /// Falls back to the terminal heading when the entry and the berth coincide, which happens
    /// when a dock is commanded from on top of the berth. A zero-length centreline would
    /// otherwise produce a NaN lateral offset and abort the operation on the first step.
    /// </remarks>
    public Vector3 CentrelineEus
    {
        get
        {
            var run = new Vector3(BerthEus.X - EntryEus.X, 0f, BerthEus.Z - EntryEus.Z);
            double length = run.Length();

            return length > 0.0
                ? new Vector3((float)(run.X / length), 0f, (float)(run.Z / length))
                : CoordinateFrames.BearingToEusVector(BerthHeadingRad, 1.0);
        }
    }

    /// <summary>Throws unless every term of the plan is usable.</summary>
    /// <param name="paramName">Parameter name to attribute the failure to.</param>
    /// <returns>This plan, so the check can be inlined into an assignment.</returns>
    /// <exception cref="ArgumentException">A term is non-finite, or a required term is not positive.</exception>
    public DockingPlan Validated(string paramName)
    {
        if (!float.IsFinite(BerthEus.X) || !float.IsFinite(BerthEus.Z)
            || !float.IsFinite(EntryEus.X) || !float.IsFinite(EntryEus.Z)
            || !double.IsFinite(BerthHeadingRad))
        {
            throw new ArgumentException("A docking plan's geometry must be finite.", paramName);
        }

        if (!double.IsFinite(CorridorHalfWidthM) || CorridorHalfWidthM <= 0.0
            || !double.IsFinite(TerminalToleranceM) || TerminalToleranceM <= 0.0
            || !double.IsFinite(TimeoutSeconds) || TimeoutSeconds <= 0.0)
        {
            throw new ArgumentException(
                "A docking plan needs a positive corridor width, terminal tolerance and time budget.",
                paramName);
        }

        if (!double.IsFinite(ApproachSpeedMps) || ApproachSpeedMps <= 0.0
            || !double.IsFinite(CorridorSpeedMps) || CorridorSpeedMps <= 0.0
            || !double.IsFinite(FinalSpeedMps) || FinalSpeedMps <= 0.0)
        {
            throw new ArgumentException(
                "Every docking stage needs a positive speed ceiling; a stage that permits no way "
                + "on cannot be steered out of, let alone completed.",
                paramName);
        }

        return this;
    }
}

/// <summary>Where a berthing approach last fixed the vessel, and the step that followed the fix.</summary>
/// <remarks>
/// Carried between steps so <see cref="Docking.Advance"/> can measure how fast the hull is
/// actually closing on the berth. A berth does not move, so that rate is ground-relative, and
/// the only way to obtain a ground-relative rate from a 3-DOF state — whose surge and sway are
/// water-relative by construction — is to difference two positions. Differencing where the
/// vessel really got to also captures every influence at once: the set of the tide, the leeway
/// the wind puts on, the sideslip out of a turn, and any deflection the water mask applied.
/// <see cref="SurfaceAsset"/> publishes its ground track from the same difference and for the
/// same reason.
/// <para>
/// <see cref="IntervalSeconds"/> is the length of the step integrated <em>after</em> this fix
/// was taken, not the step about to be integrated, so a run whose timestep varies still divides
/// the displacement by the interval that produced it.
/// </para>
/// </remarks>
/// <param name="EastM">Scene <c>X</c> coordinate the fix was taken at, in metres.</param>
/// <param name="SouthM">Scene <c>Z</c> coordinate the fix was taken at, in metres.</param>
/// <param name="IntervalSeconds">Length of the step integrated after the fix, in seconds. Zero means no fix has been taken.</param>
public readonly record struct DockingFix(double EastM, double SouthM, double IntervalSeconds)
{
    /// <summary>No fix taken, which is what the first step of every approach starts from.</summary>
    public static DockingFix None => default;

    /// <summary>True when this fix can be differenced against a later position.</summary>
    /// <remarks>
    /// A zero or non-finite interval means there is nothing to divide by, which is the honest
    /// reading of "no measurement yet" rather than an error: the very first step of an approach
    /// has taken one fix and needs two.
    /// </remarks>
    public bool IsUsable =>
        IntervalSeconds > 0.0 && double.IsFinite(EastM) && double.IsFinite(SouthM);

    /// <summary>Takes a fix at the pose a step is about to be integrated from.</summary>
    /// <param name="state">Pose the fix is taken at.</param>
    /// <param name="intervalSeconds">Length of the step about to be integrated, in seconds.</param>
    /// <returns>The fix to carry into the next step.</returns>
    public static DockingFix At(in SurfaceMotionState state, double intervalSeconds) =>
        new(state.EastM, state.SouthM, Math.Max(0.0, intervalSeconds));
}

/// <summary>How far one docking operation has got, carried between steps.</summary>
/// <remarks>
/// A value rather than mutable controller state, so <see cref="Docking.Advance"/> stays a pure
/// function and a recorded run replays. <paramref name="ClosestRangeM"/> is a running minimum
/// rather than the previous range, because a hull surging on a swell opens and closes the range
/// by a few centimetres every second and comparing against the last value alone would call that
/// an overshoot.
/// </remarks>
/// <param name="Phase">Stage reached.</param>
/// <param name="ElapsedSeconds">Simulated seconds since the operation began.</param>
/// <param name="ClosestRangeM">Smallest range to the berth reached so far, in metres.</param>
/// <param name="AbortReason">Why the operation was abandoned, or <see cref="DockingAbortReason.None"/>.</param>
/// <param name="PreviousFix">
/// Where the vessel was at the previous step, and how long the step between then and now was.
/// Optional so that a case stating an approach in literals still constructs, and defaulted to
/// <see cref="DockingFix.None"/>, which <see cref="Docking.Advance"/> reads as "no ground-track
/// measurement available yet".
/// </param>
public readonly record struct DockingProgress(
    DockingPhase Phase,
    double ElapsedSeconds,
    double ClosestRangeM,
    DockingAbortReason AbortReason,
    DockingFix PreviousFix = default)
{
    /// <summary>A freshly begun operation, at the approach stage with nothing elapsed.</summary>
    public static DockingProgress Begin =>
        new(DockingPhase.Approach, 0.0, double.PositiveInfinity, DockingAbortReason.None);

    /// <summary>Nothing running.</summary>
    public static DockingProgress Inactive =>
        new(DockingPhase.Inactive, 0.0, double.PositiveInfinity, DockingAbortReason.None);

    /// <summary>True while the operation is still being flown.</summary>
    public bool IsActive =>
        Phase is DockingPhase.Approach or DockingPhase.Corridor or DockingPhase.Final;

    /// <summary>True once the vessel is secured at the terminal pose.</summary>
    public bool IsMoored => Phase == DockingPhase.Moored;

    /// <summary>True once the operation has been abandoned.</summary>
    public bool HasAborted => Phase == DockingPhase.Aborted;

    /// <summary>Abandons the operation, naming why.</summary>
    /// <param name="reason">Condition that abandoned it.</param>
    /// <returns>Progress in the aborted phase.</returns>
    public DockingProgress AbortedFor(DockingAbortReason reason) =>
        this with { Phase = DockingPhase.Aborted, AbortReason = reason };
}

/// <summary>What one step of a docking operation produced.</summary>
/// <param name="Setpoint">What to ask the actuators for this step.</param>
/// <param name="Progress">Progress to carry into the next step.</param>
/// <param name="RangeM">Range to the berth, in metres.</param>
/// <param name="LateralOffsetM">Distance from the corridor centreline, in metres. Always non-negative.</param>
/// <param name="HeadingErrorRad">Signed error against the terminal heading, in radians.</param>
/// <param name="SpeedLimitMps">Stage speed ceiling in force this step, in metres per second.</param>
/// <param name="ApproachSpeedMps">
/// Rate the vessel is closing on the berth, in metres per second; negative while opening. This
/// is the quantity <see cref="DockingPlan.TerminalSpeedMps"/> is compared against, published so
/// a caller can see the number the decision was made on rather than infer it from a log
/// reading that measures something else.
/// </param>
/// <param name="HasMoored">True only on the step that secured the vessel.</param>
/// <param name="HasAborted">True only on the step that abandoned the operation.</param>
public readonly record struct DockingOutcome(
    SurfaceSetpoint Setpoint,
    DockingProgress Progress,
    double RangeM,
    double LateralOffsetM,
    double HeadingErrorRad,
    double SpeedLimitMps,
    double ApproachSpeedMps,
    bool HasMoored,
    bool HasAborted);

/// <summary>The berthing state machine: staged limits, a corridor, a time budget and named aborts.</summary>
/// <remarks>
/// Pure arithmetic over a plan, a progress value and a vessel state. No sampler, no event queue
/// and no command validation, so the whole operation can be driven from literals.
/// <para>
/// <b>An abort always leaves the vessel safe and commandable.</b> It stops the propeller, sets
/// <see cref="DockingPhase.Aborted"/>, and does nothing else: it latches no fault, refuses no
/// command, and leaves the caller free to transit away, try again, or hold. A hull is never made
/// harder to recover by the thing that was meant to protect it.
/// </para>
/// <para>
/// <b>Only this machine can moor a vessel.</b> Reaching <see cref="DockingPhase.Moored"/>
/// requires the range, the heading and the speed of approach to the berth all to be inside
/// their terminal tolerances at once, which a transit never checks and therefore never
/// satisfies. The third of those is the rate the hull and the berth converge — see
/// <see cref="Advance"/> — and emphatically not the speed the hull makes through the water.
/// </para>
/// <para>
/// Advisory throughout, as <see cref="DockingPlan"/> says.
/// </para>
/// </remarks>
public static class Docking
{
    /// <summary>Event code raised when a docking operation begins.</summary>
    public const string StartedCode = "surface.docking.started";

    /// <summary>Event code raised on the step that secures the vessel.</summary>
    public const string MooredCode = "surface.docking.moored";

    /// <summary>Event code raised on the step that abandons the operation.</summary>
    public const string AbortedCode = "surface.docking.aborted";

    /// <summary>Refusal token for a hull that is not fitted to dock.</summary>
    /// <remarks>
    /// Structural, and unreachable while <see cref="SurfaceProfile.CanDock"/> and
    /// <see cref="Models.AssetCapability.Dock"/> agree; the second gate that still fires if they ever
    /// stop agreeing.
    /// </remarks>
    public const string UnsupportedReason = "capability.dock.unsupported";

    /// <summary>Refusal token for an undock issued to a vessel that is not secured anywhere.</summary>
    /// <remarks>
    /// Deliberately <em>not</em> structural: it is a fact about this moment, not about the
    /// build. Dock the vessel and the same command lands.
    /// </remarks>
    public const string NotDockedReason = "surface.dock.notDocked";

    /// <summary>Proportional gain from heading error to commanded rate of turn, per second.</summary>
    /// <remarks>The same figure the station-keeping law uses, and for the same reason: a hull answers slowly.</remarks>
    private const double HeadingGainPerSec = 0.6;

    /// <summary>Extra range past the closest approach that counts as an overshoot, in hull lengths.</summary>
    /// <remarks>
    /// One length. A hull surging on a swell opens the range by centimetres constantly, and a
    /// margin smaller than the vessel would abort a sound approach on the sea state alone.
    /// </remarks>
    private const double OvershootLengths = 1.0;

    /// <summary>Whether a hull is fitted to approach and secure to a dock or mooring.</summary>
    /// <param name="profile">Hull to test.</param>
    /// <returns><see langword="true"/> when the hull may be asked to dock.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static bool IsSupportedBy(SurfaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.CanDock;
    }

    /// <summary>Stable machine-readable code for an abort reason.</summary>
    /// <param name="reason">Reason to encode.</param>
    /// <returns>A lower-case token, or <c>none</c>.</returns>
    public static string ReasonCode(DockingAbortReason reason) => reason switch
    {
        DockingAbortReason.Timeout => "docking.timeout",
        DockingAbortReason.OutsideCorridor => "docking.outsideCorridor",
        DockingAbortReason.ObstructedApproach => "docking.obstructedApproach",
        DockingAbortReason.PositionLost => "docking.positionLost",
        DockingAbortReason.Overshoot => "docking.overshoot",
        DockingAbortReason.OperatorCancelled => "docking.operatorCancelled",
        _ => "none",
    };

    /// <summary>Advances a docking operation by exactly one step.</summary>
    /// <remarks>
    /// <b>Order, and why it is this order.</b> The abort conditions are tested before the
    /// completion test, so an operation that has already left the corridor cannot be moored by
    /// coincidentally passing through the terminal tolerance on its way past. The stage is
    /// resolved from range alone, so it is a function of geometry rather than of history and
    /// cannot get stuck in a stage the vessel has left.
    /// <para>
    /// <b>The terminal speed is tested against the approach speed, not against the log.</b> The
    /// berth is a fixed point of the scene, so "slowly enough to stop" is a statement about the
    /// rate the hull and the berth converge — a ground-relative rate — and
    /// <see cref="BerthApproachSpeedMps"/> is where it is measured. Speed through the water is a
    /// different quantity: it excludes the set the whole water column is moving at, and the
    /// integrator holds a floor under it of <see cref="SurfaceProfile.LeewayFraction"/> of the
    /// wind speed, because a beam wind pushes a hull sideways through the water whether or not
    /// it is going anywhere. Testing the terminal condition on the log therefore made mooring
    /// impossible above roughly <c>TerminalSpeedMps / LeewayFraction</c> of wind — some 7.4 m/s
    /// for the shipped workboat — while the vessel sat alongside its berth going nowhere. The
    /// air domain once published airspeed where ground speed belonged; this is that mistake in a
    /// different fluid, and the guard against repeating it is to name, at every site, which of
    /// the two a requirement is actually about.
    /// </para>
    /// <para>
    /// <b>The speed command is the smaller of the stage ceiling and the coast limit.</b> A
    /// displacement hull has no brake: cutting the throttle leaves it running down its surge
    /// time constant, covering exactly <c>v * tau_u</c> before it stops. Commanding a speed
    /// whose coast distance exceeds the remaining range is therefore commanding an overshoot,
    /// however small the stage ceiling is. Deriving the limit from the integrator's own time
    /// constant is what keeps the documented approach and the flown approach the same one.
    /// </para>
    /// <para>
    /// Deterministic: fixed arithmetic, no clock — the elapsed time is accumulated from the
    /// caller's timestep — no iteration and no convergence test.
    /// </para>
    /// </remarks>
    /// <param name="profile">Hull flying the approach.</param>
    /// <param name="plan">Geometry, staged limits and terms.</param>
    /// <param name="progress">Progress carried in from the previous step.</param>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <param name="deltaSeconds">Timestep in seconds.</param>
    /// <param name="isApproachClear">False when the berth or the water on the way to it is no longer navigable.</param>
    /// <param name="hasPositionFix">False once position quality has been lost.</param>
    /// <returns>The setpoint, the progress to carry forward, and the transition flags for one step.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static DockingOutcome Advance(
        SurfaceProfile profile,
        DockingPlan plan,
        in DockingProgress progress,
        in SurfaceMotionState state,
        double deltaSeconds,
        bool isApproachClear,
        bool hasPositionFix)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(plan);

        double east = plan.BerthEus.X - state.EastM;
        double south = plan.BerthEus.Z - state.SouthM;
        double range = Math.Sqrt((east * east) + (south * south));

        double headingError = SurfaceNavigator.ShortestTurnRad(plan.BerthHeadingRad, state.HeadingRad);
        double lateral = LateralOffsetM(plan, state);
        double approach = BerthApproachSpeedMps(plan, in progress, in state, range);

        if (!progress.IsActive)
        {
            return new DockingOutcome(
                SurfaceSetpoint.Drift, progress, range, lateral, headingError, 0.0, approach,
                HasMoored: false, HasAborted: false);
        }

        double elapsed = progress.ElapsedSeconds + Math.Max(0.0, deltaSeconds);
        double closest = Math.Min(progress.ClosestRangeM, range);
        var phase = StageFor(plan, range);
        var running = progress with
        {
            Phase = phase,
            ElapsedSeconds = elapsed,
            ClosestRangeM = closest,
            PreviousFix = DockingFix.At(in state, deltaSeconds),
        };

        if (AbortReasonFor(profile, plan, in running, range, lateral, isApproachClear, hasPositionFix)
            is { } abort)
        {
            return new DockingOutcome(
                SurfaceSetpoint.Drift, running.AbortedFor(abort), range, lateral, headingError,
                0.0, approach, HasMoored: false, HasAborted: true);
        }

        // The magnitude, not the signed rate: a hull being carried away from its berth is no
        // more secured than one arriving too fast, and only the magnitude says so.
        if (range <= plan.TerminalToleranceM
            && Math.Abs(headingError) <= plan.TerminalHeadingToleranceRad
            && Math.Abs(approach) <= plan.TerminalSpeedMps)
        {
            return new DockingOutcome(
                SurfaceSetpoint.Drift,
                running with { Phase = DockingPhase.Moored },
                range, lateral, headingError, 0.0, approach,
                HasMoored: true, HasAborted: false);
        }

        double ceiling = SpeedCeilingFor(plan, phase);
        double coast = Math.Max(0.0, range - plan.TerminalToleranceM) / profile.SurgeTimeConstantSec;
        double surge = Math.Min(ceiling, coast);

        // Steer at the berth until the final stage, then onto the terminal heading. Switching
        // at the final stage rather than at the berth is what puts the hull on the terminal
        // heading before it arrives, instead of arriving and then swinging alongside.
        double steerTo = phase == DockingPhase.Final
            ? plan.BerthHeadingRad
            : CoordinateFrames.BearingFromEusVector(
                new Vector3((float)east, 0f, (float)south), state.HeadingRad);

        double yaw = Math.Clamp(
            SurfaceNavigator.ShortestTurnRad(steerTo, state.HeadingRad) * HeadingGainPerSec,
            -profile.MaxYawRateRadPerSec,
            profile.MaxYawRateRadPerSec);

        return new DockingOutcome(
            new SurfaceSetpoint(surge, yaw), running, range, lateral, headingError, ceiling,
            approach, HasMoored: false, HasAborted: false);
    }

    /// <summary>How fast the vessel is closing on the berth, in metres per second.</summary>
    /// <remarks>
    /// Signed: positive while closing, negative while opening. Motion <em>across</em> the line to
    /// the berth is deliberately not counted, because that is what the corridor constrains — a
    /// hull set bodily sideways leaves the corridor and is abandoned long before it could be
    /// mistakenly secured — while this figure answers the one question a terminal speed limit
    /// asks, which is how hard the hull and the berth are converging.
    /// <para>
    /// Measured by differencing the position the vessel actually reached over the step that got
    /// it there. That estimate already contains the tide, the leeway, the sideslip out of a turn
    /// and any deflection the water mask applied, so nothing has to be plumbed in alongside it
    /// and nothing can be left out by a caller that forgot a term.
    /// </para>
    /// <para>
    /// An approach that has taken only one fix has nothing to difference, so its first step
    /// falls back to resolving the water-relative velocity onto the bearing to the berth. That
    /// is exact in slack water and a one-step approximation otherwise, and it is never used
    /// again after that step — which is why <see cref="DockingProgress.Begin"/> may be handed to
    /// this machine from literals and still produce the closing rate those literals describe.
    /// </para>
    /// </remarks>
    /// <param name="plan">Plan carrying the berth.</param>
    /// <param name="progress">Progress carrying the previous fix, if one has been taken.</param>
    /// <param name="state">Pose and body velocities at the start of the step.</param>
    /// <param name="rangeM">Range to the berth, in metres.</param>
    /// <returns>The closing rate in metres per second; zero at zero range, where no direction closes.</returns>
    private static double BerthApproachSpeedMps(
        DockingPlan plan, in DockingProgress progress, in SurfaceMotionState state, double rangeM)
    {
        // At zero range every direction closes on the berth equally, so there is no bearing to
        // resolve onto. The vessel is, by definition, there.
        if (!double.IsFinite(rangeM) || rangeM <= 0.0)
        {
            return 0.0;
        }

        double towardsEast = (plan.BerthEus.X - state.EastM) / rangeM;
        double towardsSouth = (plan.BerthEus.Z - state.SouthM) / rangeM;

        var fix = progress.PreviousFix;
        double east;
        double south;

        if (fix.IsUsable)
        {
            east = (state.EastM - fix.EastM) / fix.IntervalSeconds;
            south = (state.SouthM - fix.SouthM) / fix.IntervalSeconds;
        }
        else
        {
            // Body axes into the scene frame: the bow points along (sin h, -cos h) and starboard
            // lies along (cos h, sin h), because north is -Z. The same basis
            // SurfaceDynamics integrates the pose with, restated here rather than reached for,
            // so this file stays arithmetic over its arguments.
            double sin = Math.Sin(state.HeadingRad);
            double cos = Math.Cos(state.HeadingRad);

            east = (state.SurgeMps * sin) + (state.SwayMps * cos);
            south = (-state.SurgeMps * cos) + (state.SwayMps * sin);
        }

        return (east * towardsEast) + (south * towardsSouth);
    }

    /// <summary>Stage the vessel is in, from range alone.</summary>
    /// <param name="plan">Plan whose stage boundaries apply.</param>
    /// <param name="rangeM">Range to the berth, in metres.</param>
    /// <returns>The stage.</returns>
    private static DockingPhase StageFor(DockingPlan plan, double rangeM) =>
        rangeM > plan.CorridorLengthM ? DockingPhase.Approach
        : rangeM > plan.FinalLengthM ? DockingPhase.Corridor
        : DockingPhase.Final;

    /// <summary>Speed ceiling in force for a stage, in metres per second.</summary>
    /// <param name="plan">Plan carrying the staged limits.</param>
    /// <param name="phase">Stage in force.</param>
    /// <returns>The ceiling in metres per second.</returns>
    private static double SpeedCeilingFor(DockingPlan plan, DockingPhase phase) => phase switch
    {
        DockingPhase.Approach => plan.ApproachSpeedMps,
        DockingPhase.Corridor => plan.CorridorSpeedMps,
        _ => plan.FinalSpeedMps,
    };

    /// <summary>Perpendicular distance from the corridor centreline, in metres.</summary>
    /// <remarks>
    /// The magnitude of the component of the berth-relative offset perpendicular to the
    /// centreline. Always non-negative: which side of the line the vessel is on is not what the
    /// corridor constrains.
    /// </remarks>
    /// <param name="plan">Plan whose centreline applies.</param>
    /// <param name="state">Vessel pose.</param>
    /// <returns>Distance in metres.</returns>
    private static double LateralOffsetM(DockingPlan plan, in SurfaceMotionState state)
    {
        var line = plan.CentrelineEus;
        double east = state.EastM - plan.BerthEus.X;
        double south = state.SouthM - plan.BerthEus.Z;
        double along = (east * line.X) + (south * line.Z);

        double offEast = east - (along * line.X);
        double offSouth = south - (along * line.Z);

        return Math.Sqrt((offEast * offEast) + (offSouth * offSouth));
    }

    /// <summary>The first abort condition that applies, or null when the approach is sound.</summary>
    /// <remarks>
    /// Ordered by how much they invalidate: an obstructed berth or a lost fix makes the terminal
    /// pose meaningless, a corridor departure makes the approach unsound, an overshoot makes it
    /// unrecoverable in this attempt, and a timeout is the backstop for everything that goes
    /// slowly wrong without tripping any of the others.
    /// <para>
    /// The corridor test is deliberately skipped in <see cref="DockingPhase.Approach"/>: a
    /// vessel closing on the corridor entry from off to one side is doing exactly what that
    /// stage is for, and aborting it there would make a dock issuable only from a vessel already
    /// lined up.
    /// </para>
    /// </remarks>
    /// <param name="profile">Hull flying the approach, read for the overshoot margin.</param>
    /// <param name="plan">Plan whose terms apply.</param>
    /// <param name="progress">Progress including this step's stage and elapsed time.</param>
    /// <param name="rangeM">Range to the berth, in metres.</param>
    /// <param name="lateralOffsetM">Distance from the centreline, in metres.</param>
    /// <param name="isApproachClear">False when the berth or the water to it is no longer navigable.</param>
    /// <param name="hasPositionFix">False once position quality has been lost.</param>
    /// <returns>The reason to abort, or null.</returns>
    private static DockingAbortReason? AbortReasonFor(
        SurfaceProfile profile,
        DockingPlan plan,
        in DockingProgress progress,
        double rangeM,
        double lateralOffsetM,
        bool isApproachClear,
        bool hasPositionFix)
    {
        if (!isApproachClear)
        {
            return DockingAbortReason.ObstructedApproach;
        }

        if (!hasPositionFix)
        {
            return DockingAbortReason.PositionLost;
        }

        if (progress.Phase != DockingPhase.Approach && lateralOffsetM > plan.CorridorHalfWidthM)
        {
            return DockingAbortReason.OutsideCorridor;
        }

        if (progress.Phase == DockingPhase.Final
            && rangeM > progress.ClosestRangeM + (OvershootLengths * profile.LengthM))
        {
            return DockingAbortReason.Overshoot;
        }

        return progress.ElapsedSeconds > plan.TimeoutSeconds ? DockingAbortReason.Timeout : null;
    }
}
