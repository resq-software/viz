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

namespace ResQ.Viz.Web.Services.Assets.Surface;

/// <summary>Turns a surface task into a setpoint: a position, a course, a station or a berth.</summary>
/// <remarks>
/// The whole of the guidance law for a vessel, and deliberately nothing else. It holds no
/// terrain, no sampler, no event queue and no command validation, so its behaviour can be driven
/// end to end from literals — a heading error, a distance and a drift in, a setpoint out — and
/// <see cref="SurfaceAsset"/> can be reasoned about without a control law underneath it. It is
/// the same split, for the same reason, that keeps <see cref="ISurfaceDynamics"/> down to
/// equations, and it is why the station-keeping and berthing laws live in
/// <see cref="StationKeeping"/> and <see cref="Docking"/> rather than inside this type.
/// <para>
/// <b>The arrival law is the integrator's own.</b> A displacement hull has no brake: cutting the
/// throttle leaves the surge running down its time constant, covering exactly
/// <c>v * tau_u</c> before it stops. Every approach here is therefore limited to
/// <c>(range - tolerance) / tau_u</c> rather than to a square-root braking profile, because the
/// square-root profile describes a vehicle with a service brake and this is not one. A guidance
/// law that plans against a deceleration the integrator does not deliver is the defect the
/// ground domain shipped, wearing different clothes.
/// </para>
/// <para>
/// <b>A hull needs way on to steer.</b> <see cref="SurfaceProfile.MaxYawRateAt"/> falls to zero
/// with speed, so every law here keeps a floor under the commanded surge while a turn is wanted.
/// Without it a vessel with a target abeam would cut its throttle to turn, lose steerage in the
/// process, and sit there unable to do either.
/// </para>
/// <para>
/// Deterministic and allocation-free: every member is arithmetic over its arguments and this
/// object's own fields — no clock, no substepping, no convergence test, and no iteration count
/// that varies with state.
/// </para>
/// <para>
/// Advisory. Refusing non-navigable water rests on a procedural bed and a quasi-static hull
/// envelope; it is decision support for an operator, never a guarantee that what it does permit
/// is safe to navigate, and it makes no claim about any navigation regulation.
/// </para>
/// </remarks>
public sealed partial class SurfaceNavigator
{
    /// <summary>Smallest arrival tolerance any hull uses, in metres.</summary>
    /// <remarks>
    /// A floor under the length-derived tolerance. Asking a vessel to stop within a metre of a
    /// point makes arrival depend on the last bits of the integration and on whatever the tide
    /// is doing, so it would creep past, stop, drift back, and never settle.
    /// </remarks>
    public const double MinArrivalToleranceM = 3.0;

    /// <summary>Proportional gain from heading error to commanded rate of turn, per second.</summary>
    /// <remarks>
    /// Half what a rover uses, because a hull answers its helm over a yaw time constant measured
    /// in seconds and a drivetrain gain merely saturates the rate limit and weaves. The
    /// integrator clamps the result to <see cref="SurfaceProfile.MaxYawRateAt"/> regardless, so
    /// this decides only how quickly the law asks for the turn it is going to get.
    /// </remarks>
    private const double HeadingGainPerSec = 0.6;

    /// <summary>Fraction of the commanded speed kept while turning hardest.</summary>
    /// <remarks>
    /// Without a floor, <c>cos(error)</c> reaches zero at ninety degrees and goes negative
    /// behind, so a target off the beam would stop the vessel — and a stopped hull has no flow
    /// over its rudder and cannot change heading at all. The floor is what turns "cannot reach
    /// it" into "steams round to it".
    /// </remarks>
    private const double MinManoeuvreSpeedFraction = 0.35;

    private readonly SurfaceProfile _profile;

    private Vector3 _targetEus;
    private bool _hasTarget;
    private double _cruiseSpeedMps;
    private double _commandedCourseRad;
    private StationKeepGoal? _stationKeep;
    private DockingPlan? _dockingPlan;

    /// <summary>Cruise speed to put back when the current manoeuvre ends, or null when none is scoped.</summary>
    /// <remarks>
    /// <b>A manoeuvre's speed limit is not a change of cruise speed.</b> A berth is left slowly
    /// because leaving a berth is delicate, not because anyone asked this vessel to be a slow
    /// vessel from now on — and writing the stand-off speed straight into
    /// <see cref="_cruiseSpeedMps"/> made it exactly the latter, so every later passage that did
    /// not name a speed inherited a fifteen-per-cent hull. Holding the previous setting here and
    /// putting it back in <see cref="ClearTask"/> scopes the limit to the leg that asked for it.
    /// </remarks>
    private double? _cruiseSpeedBeforeManoeuvreMps;

    /// <summary>Builds a navigator for one hull.</summary>
    /// <param name="profile">Envelope whose length, time constants and turning circle shape the guidance law.</param>
    /// <param name="safety">Operating policy, or null to derive it from the profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">The profile is not usable by a surface model.</exception>
    public SurfaceNavigator(SurfaceProfile profile, SurfaceSafetyPolicy? safety = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile.Validated(nameof(profile));

        Safety = safety ?? SurfaceSafetyPolicy.For(_profile);

        // A vessel has arrived when it is within its own length of the point. Asking for better
        // than that is asking for precision the hull's geometry does not have.
        ArrivalToleranceM = Math.Max(MinArrivalToleranceM, _profile.LengthM);
        _cruiseSpeedMps = _profile.MaxSpeedMps;
        _commandedCourseRad = 0.0;
    }

    /// <summary>Operating policy in force for this vessel.</summary>
    public SurfaceSafetyPolicy Safety { get; }

    /// <summary>How close the vessel must get before a target counts as reached, in metres.</summary>
    public double ArrivalToleranceM { get; }

    /// <summary>What the navigator is currently trying to do.</summary>
    public SurfaceGuidanceMode Mode { get; private set; } = SurfaceGuidanceMode.Idle;

    /// <summary>Stable lower-case token for <see cref="Mode"/>, for the wire's mode string.</summary>
    /// <remarks>Display and filtering only. Never branch behaviour on it; branch on <see cref="Mode"/>.</remarks>
    public string ModeToken => Mode switch
    {
        SurfaceGuidanceMode.Transiting => "transit",
        SurfaceGuidanceMode.Steering => "course",
        SurfaceGuidanceMode.Holding => "hold",
        SurfaceGuidanceMode.StationKeeping => "station-keep",
        SurfaceGuidanceMode.Docking => "dock",
        SurfaceGuidanceMode.Undocking => "undock",
        SurfaceGuidanceMode.Blocked => "blocked",
        SurfaceGuidanceMode.EmergencyStopped => "emergency-stop",
        _ => IsDocked ? "moored" : "idle",
    };

    /// <summary>Target being transited to, or <see langword="null"/> when none is assigned.</summary>
    public Vector3? TargetEus => _hasTarget ? _targetEus : null;

    /// <summary>Horizontal distance still to run, in metres. Zero without a target.</summary>
    public double RemainingDistanceM { get; private set; }

    /// <summary>Speed the guidance law is currently allowed to ask for, in metres per second.</summary>
    /// <remarks>
    /// The <em>effective</em> figure, so while a manoeuvre with its own speed limit is running
    /// this reads that limit rather than the standing setting. Never above the profile's own
    /// ceiling. Compare <see cref="StandingCruiseSpeedMps"/>, which is what the vessel goes back
    /// to when the manoeuvre ends.
    /// </remarks>
    public double CruiseSpeedMps => _cruiseSpeedMps;

    /// <summary>Cruise speed the vessel returns to once any scoped manoeuvre limit is lifted.</summary>
    /// <remarks>
    /// Equal to <see cref="CruiseSpeedMps"/> whenever no manoeuvre limit is in force, which is
    /// almost always. Published so the difference between "slowly, for this manoeuvre" and
    /// "slowly, from now on" is legible from outside rather than being a fact only this type
    /// knows — the two look identical from a single speed reading, and telling them apart is the
    /// whole point of scoping the limit.
    /// </remarks>
    public double StandingCruiseSpeedMps => _cruiseSpeedBeforeManoeuvreMps ?? _cruiseSpeedMps;

    /// <summary>True while a manoeuvre is holding the speed below the standing cruise setting.</summary>
    public bool IsManoeuvreSpeedInForce => _cruiseSpeedBeforeManoeuvreMps is not null;

    /// <summary>Course over ground being steered, in radians clockwise from true north.</summary>
    /// <remarks>Meaningful only in <see cref="SurfaceGuidanceMode.Steering"/>.</remarks>
    public double CommandedCourseRad => _commandedCourseRad;

    /// <summary>Why the water was refused, or <see cref="WaterBlockReason.None"/>.</summary>
    public WaterBlockReason BlockingReason { get; private set; } = WaterBlockReason.None;

    /// <summary>Station being held, or <see langword="null"/> when none is engaged.</summary>
    public StationKeepGoal? StationKeep => _stationKeep;

    /// <summary>Last evaluation of the station-keeping law, or <see cref="StationKeepOutcome.Disengaged"/>.</summary>
    public StationKeepOutcome StationKeepOutcome { get; private set; } = StationKeepOutcome.Disengaged;

    /// <summary>Berthing plan being flown, or <see langword="null"/> when none is.</summary>
    public DockingPlan? Berth => _dockingPlan;

    /// <summary>How far the berthing approach has got.</summary>
    public DockingProgress DockingProgress { get; private set; } = DockingProgress.Inactive;

    /// <summary>Why the last berthing approach was abandoned, or <see cref="DockingAbortReason.None"/>.</summary>
    public DockingAbortReason DockingAbortReason { get; private set; } = DockingAbortReason.None;

    /// <summary>
    /// True once a berthing approach has completed. <b>Only <see cref="Docking"/> can set it.</b>
    /// </summary>
    /// <remarks>
    /// A plain transit clears it and never sets it, however close to a berth its target happens
    /// to be: mooring requires a terminal position, a terminal heading and a terminal speed all
    /// at once, and a transit checks none of them. That asymmetry is the reason a dock is a
    /// structured operation rather than a <c>goTo</c> with a smaller tolerance.
    /// </remarks>
    public bool IsDocked { get; private set; }

    /// <summary>True while a control law is asking for thrust.</summary>
    /// <remarks>
    /// Read by the owning asset to tell a vessel that is <em>drifting</em> from one that is
    /// moving because it was told to. A vessel with the propeller stopped and two knots of
    /// ground speed is the case an operator has to be warned about, and it is invisible to any
    /// test that only looks at speed.
    /// </remarks>
    public bool IsUnderPower =>
        Mode is SurfaceGuidanceMode.Transiting or SurfaceGuidanceMode.Steering
            or SurfaceGuidanceMode.Docking or SurfaceGuidanceMode.Undocking
            or SurfaceGuidanceMode.StationKeeping
        || (Mode == SurfaceGuidanceMode.Holding && _stationKeep is not null);

    /// <summary>Shortest signed rotation from one bearing to another, in <c>(-pi, pi]</c>.</summary>
    /// <remarks>
    /// The surface domain's one definition, called by <see cref="StationKeeping"/> and
    /// <see cref="Docking"/> as well as by this type, so no two of the three can come to
    /// disagree about which way round a vessel should turn. Normalising to <c>[0, 2*pi)</c> and
    /// folding the upper half is what stops a vessel heading one degree east of north from
    /// swinging 359 degrees to reach a course one degree west of it.
    /// </remarks>
    /// <param name="targetRad">Bearing to turn towards, in radians clockwise from true north.</param>
    /// <param name="currentRad">Bearing currently held, in radians clockwise from true north.</param>
    /// <returns>The signed rotation in radians, positive to starboard.</returns>
    public static double ShortestTurnRad(double targetRad, double currentRad)
    {
        double delta = CoordinateFrames.NormalizeAngle(targetRad - currentRad);
        return delta > Math.PI ? delta - Math.Tau : delta;
    }

    /// <summary>Assigns a position and begins transiting to it.</summary>
    /// <remarks>
    /// Clears a latched block and any berthing approach, because a new destination is a new
    /// decision by the operator and the old refusal said nothing about this passage. Whether the
    /// new destination is reachable is checked before it gets here — see
    /// <see cref="SurfaceAsset.Apply"/> — and again by the look-ahead on every step after it.
    /// <para>
    /// It also clears <see cref="IsDocked"/>: a vessel told to go somewhere is no longer secured
    /// where it was, and it ends any manoeuvre speed limit the previous leg was flown under —
    /// a new passage is not governed by the terms of the one it replaced.
    /// </para>
    /// </remarks>
    /// <param name="targetEus">Destination in the scene frame; the vertical component is ignored.</param>
    /// <param name="speedMps">Cruise speed to use, or null to keep the standing cruise setting.</param>
    /// <exception cref="ArgumentException"><paramref name="targetEus"/> has a non-finite horizontal component.</exception>
    public void TransitTo(Vector3 targetEus, double? speedMps = null)
    {
        if (!float.IsFinite(targetEus.X) || !float.IsFinite(targetEus.Z))
        {
            throw new ArgumentException("A transit target must be finite.", nameof(targetEus));
        }

        CancelDocking(Assets.Surface.DockingAbortReason.OperatorCancelled);

        // Before the speed is applied, not after: ClearTask ends any manoeuvre limit and puts
        // the standing cruise speed back, so a speed named by this passage would otherwise be
        // set and then immediately restored away by the leg it was meant to govern.
        ClearTask();

        if (speedMps is { } requested)
        {
            SetCruiseSpeed(requested);
        }

        IsDocked = false;
        _targetEus = targetEus;
        _hasTarget = true;
        Mode = SurfaceGuidanceMode.Transiting;
    }

    /// <summary>Steers a commanded course over ground.</summary>
    /// <remarks>
    /// A <b>course</b>, not a heading, and the difference is the whole reason this command
    /// exists separately from a transit: the law closes the error between the commanded course
    /// and the course the vessel is actually making good, so in a cross-set the bow settles
    /// crabbed off the course by the drift angle and the vessel holds its track. A heading hold
    /// would point the bow correctly and set the vessel sideways down the tide.
    /// </remarks>
    /// <param name="courseRad">Course to steer, in radians clockwise from true north.</param>
    /// <param name="speedMps">Speed to make, or null to keep the standing cruise setting.</param>
    /// <exception cref="ArgumentException"><paramref name="courseRad"/> is not finite.</exception>
    public void SetCourse(double courseRad, double? speedMps = null)
    {
        if (!double.IsFinite(courseRad))
        {
            throw new ArgumentException("A commanded course must be finite.", nameof(courseRad));
        }

        CancelDocking(Assets.Surface.DockingAbortReason.OperatorCancelled);

        // Same ordering, and for the same reason, as TransitTo.
        ClearTask();

        if (speedMps is { } requested)
        {
            SetCruiseSpeed(requested);
        }

        IsDocked = false;
        _commandedCourseRad = CoordinateFrames.NormalizeAngle(courseRad);
        Mode = SurfaceGuidanceMode.Steering;
    }

    /// <summary>Sets the standing cruise speed, clamped into what the hull can sustain.</summary>
    /// <remarks>
    /// Clamped rather than refused: the profile ceiling is a physical fact, not a permission, so
    /// "as fast as you can" is the honest reading of a request above it. A non-finite or
    /// non-positive value is ignored, because direction is chosen by the command and never by
    /// the sign of a speed.
    /// <para>
    /// An explicit speed also <b>ends</b> any manoeuvre limit rather than sitting underneath it.
    /// Somebody naming a speed is naming the speed they want now; quietly holding a lower one
    /// and then restoring theirs a minute later would be a vessel disobeying and then changing
    /// its mind, which is far worse than either behaviour alone.
    /// </para>
    /// </remarks>
    /// <param name="speedMps">Requested speed in metres per second.</param>
    public void SetCruiseSpeed(double speedMps)
    {
        if (!double.IsFinite(speedMps) || speedMps <= 0.0)
        {
            return;
        }

        _cruiseSpeedBeforeManoeuvreMps = null;
        _cruiseSpeedMps = Math.Min(speedMps, _profile.MaxSpeedMps);
    }

    /// <summary>Suspends mission progress by the safest means this hull allows.</summary>
    /// <remarks>
    /// <b>Hold does not require a station-keeping capability, and must not.</b> It is the
    /// domain-neutral "stop working the mission and stay safe" command, and the assets that most
    /// need it are exactly the ones that cannot pin a position. A hull that can station-keep
    /// satisfies it by holding the spot; a hull that cannot satisfies it by stopping the
    /// propeller — and then drifts, which the published state says plainly rather than reporting
    /// a vessel as "holding" while it goes two hundred metres down the tide.
    /// <para>
    /// The transit target survives, so resuming autonomy picks the passage up where it was
    /// suspended.
    /// </para>
    /// </remarks>
    /// <param name="hereEus">Where the vessel is now, in the scene frame; becomes the station when one can be held.</param>
    public void Hold(Vector3 hereEus)
    {
        CancelDocking(Assets.Surface.DockingAbortReason.OperatorCancelled);

        _stationKeep = StationKeeping.IsSupportedBy(_profile)
            ? StationKeepGoal.For(_profile, hereEus)
            : null;

        BlockingReason = WaterBlockReason.None;
        Mode = SurfaceGuidanceMode.Holding;
    }

    /// <summary>Engages an explicit station keep on the goal supplied.</summary>
    /// <remarks>
    /// Distinct from <see cref="Hold"/>: this is the operator asking for a position to be
    /// actively held on stated terms, and it is refused outright by a hull that cannot hold one.
    /// The caller gates on <see cref="StationKeeping.IsSupportedBy"/> before calling; this method
    /// asserts nothing, because a guidance law is not where a capability refusal belongs.
    /// </remarks>
    /// <param name="goal">Station and the terms to hold it on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="goal"/> is null.</exception>
    public void EngageStationKeep(StationKeepGoal goal)
    {
        ArgumentNullException.ThrowIfNull(goal);

        CancelDocking(Assets.Surface.DockingAbortReason.OperatorCancelled);
        ClearTask();
        _stationKeep = goal.Validated(nameof(goal));
        Mode = SurfaceGuidanceMode.StationKeeping;
    }

    /// <summary>Begins a structured berthing approach.</summary>
    /// <remarks>
    /// The plan is fixed at the moment it is accepted, corridor and all, so an approach cannot
    /// quietly re-aim itself mid-manoeuvre. Re-issuing <c>dock</c> replaces the plan outright,
    /// which is a new approach rather than a correction to the old one.
    /// </remarks>
    /// <param name="plan">Geometry, staged limits and terms of the approach.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
    public void BeginDocking(DockingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        ClearTask();
        _dockingPlan = plan.Validated(nameof(plan));
        DockingProgress = Assets.Surface.DockingProgress.Begin;
        DockingAbortReason = Assets.Surface.DockingAbortReason.None;
        IsDocked = false;
        Mode = SurfaceGuidanceMode.Docking;
    }

    /// <summary>Releases from a berth and stands off to a released position.</summary>
    /// <remarks>
    /// Flown as an ordinary transit at a stand-off speed rather than as a state machine of its
    /// own: leaving a berth has none of the properties that make an arrival structured — there
    /// is no terminal pose to hit, no corridor to stay inside and nothing to overshoot — so
    /// giving it one would be ceremony rather than safety.
    /// <para>
    /// <b>The stand-off speed is a limit on this leg, not the vessel's new cruise speed.</b> It
    /// is applied through <see cref="ApplyManoeuvreSpeed"/>, which remembers what the cruise
    /// setting was and hands it back the moment the leg ends — see
    /// <see cref="_cruiseSpeedBeforeManoeuvreMps"/>. Passing it to <see cref="TransitTo"/> as an
    /// ordinary speed instead is what once left every later unqualified passage crawling at the
    /// departure speed, with nothing in the published state to explain why.
    /// </para>
    /// </remarks>
    /// <param name="standoffEus">Position to stand off to, in the scene frame.</param>
    /// <param name="speedMps">Speed to leave at, in metres per second. Ignored if it is not a usable speed.</param>
    /// <exception cref="ArgumentException"><paramref name="standoffEus"/> has a non-finite horizontal component.</exception>
    public void BeginUndocking(Vector3 standoffEus, double speedMps)
    {
        // Deliberately without a speed: TransitTo would make one the standing cruise setting.
        TransitTo(standoffEus);

        ApplyManoeuvreSpeed(speedMps);
        IsDocked = false;
        Mode = SurfaceGuidanceMode.Undocking;
    }

    /// <summary>Stops the propeller and gives up the current task.</summary>
    /// <remarks>
    /// <b>This does not stop the vessel.</b> It carries its way off over a surge time constant
    /// and then moves with the water and the wind for as long as it is left alone. The name is
    /// the domain-neutral one; the behaviour is the honest one for a hull, and the published
    /// speed over ground goes on reporting whatever the vessel is actually doing.
    /// </remarks>
    public void Stop()
    {
        CancelDocking(Assets.Surface.DockingAbortReason.OperatorCancelled);
        ClearTask();
        Mode = SurfaceGuidanceMode.Idle;
    }

    /// <summary>Latches the emergency-stop mode.</summary>
    /// <remarks>
    /// Takes effect on the very next <see cref="Sample"/>. What it then does is
    /// <see cref="SurfaceSafetyPolicy.EmergencyStop"/>'s decision and not this method's: a hull
    /// that can hold a position holds the one it was at, and a hull that cannot stops the
    /// propeller and drifts. The transit target is discarded either way, because silently
    /// resuming an interrupted passage after an emergency stop is the last thing anybody wants.
    /// </remarks>
    /// <param name="hereEus">Where the vessel is now, in the scene frame; becomes the station under a hold-station policy.</param>
    public void EmergencyStop(Vector3 hereEus)
    {
        CancelDocking(Assets.Surface.DockingAbortReason.OperatorCancelled);
        ClearTask();

        _stationKeep = Safety.EmergencyStop == SurfaceEmergencyStopBehaviour.HoldStation
            && StationKeeping.IsSupportedBy(_profile)
                ? StationKeepGoal.For(_profile, hereEus)
                : null;

        Mode = SurfaceGuidanceMode.EmergencyStopped;
    }

    /// <summary>Hands control back to autonomy.</summary>
    /// <remarks>
    /// Resumes a held passage when one survived, and otherwise idles. It never resurrects a
    /// target that <see cref="Stop"/>, <see cref="EmergencyStop"/> or a berthing approach
    /// discarded, because each of those was a deliberate decision to abandon it.
    /// </remarks>
    public void Resume()
    {
        // The station goes with the hold that created it. Leaving one engaged behind a mode
        // that never evaluates it would publish a station-keep state nothing was maintaining.
        _stationKeep = null;
        StationKeepOutcome = StationKeepOutcome.Disengaged;
        BlockingReason = WaterBlockReason.None;
        Mode = _hasTarget ? SurfaceGuidanceMode.Transiting : SurfaceGuidanceMode.Idle;
    }

    /// <summary>Re-measures the published hold against the position the vessel settled at.</summary>
    /// <remarks>
    /// Called by the owning asset once the water mask has had its say and the pose is final, so
    /// the error this navigator publishes and the position the frame carries describe the same
    /// vessel at the same instant. See <see cref="StationKeeping.Remeasure"/> for why only the
    /// measurement, and never the commanded correction, is redone.
    /// </remarks>
    /// <param name="positionEus">Position the vessel settled at this step, in the scene frame.</param>
    public void SettleStationKeep(Vector3 positionEus)
    {
        if (_stationKeep is { } goal)
        {
            StationKeepOutcome = StationKeeping.Remeasure(goal, StationKeepOutcome, positionEus);
        }
    }

    /// <summary>Latches the blocked mode after something outside the navigator refused the water.</summary>
    /// <remarks>
    /// The way a physical shoreline contact reaches guidance. Meeting the beach is discovered by
    /// <see cref="WaterConstraints.ResolveMotion"/> <em>after</em> the hull has been driven at
    /// it, too late for the look-ahead; without this the vessel would open the throttle again on
    /// the next step and keep doing so, raising a fresh contact each time.
    /// <para>
    /// A blocked vessel, unlike a blocked rover, does not stay where it was blocked: it stops
    /// the propeller and drifts. That is why the event this produces demands an operator rather
    /// than merely informing one.
    /// </para>
    /// </remarks>
    /// <param name="reason">Why the water is refused; published as <see cref="BlockingReason"/>.</param>
    /// <returns><see langword="true"/> when this call made the transition, so the caller raises exactly one event.</returns>
    public bool Block(WaterBlockReason reason)
    {
        if (Mode == SurfaceGuidanceMode.Blocked)
        {
            return false;
        }

        CancelDocking(Assets.Surface.DockingAbortReason.ObstructedApproach);
        ClearTask();
        BlockingReason = reason;
        Mode = SurfaceGuidanceMode.Blocked;
        return true;
    }

    /// <summary>Horizontal distance from the vessel to the assigned target, in metres.</summary>
    /// <param name="state">Pose to measure from.</param>
    /// <returns>Distance in metres; only meaningful while a target is assigned.</returns>
    private double PlanarDistanceTo(in SurfaceMotionState state)
    {
        double east = _targetEus.X - state.EastM;
        double south = _targetEus.Z - state.SouthM;
        return Math.Sqrt((east * east) + (south * south));
    }

    /// <summary>Drops the transit target, the station, the blocking reason and any manoeuvre speed limit.</summary>
    /// <remarks>
    /// One place, so no command can drop half of a task and leave the other half to be picked up
    /// by a later mode change. <see cref="IsDocked"/> is deliberately not cleared here — leaving
    /// a berth is a manoeuvre, not a bookkeeping side effect of assigning a new task — except
    /// where the caller clears it explicitly.
    /// <para>
    /// It is also where a scoped manoeuvre speed ends, because "the task is over or has been
    /// replaced" is exactly the lifetime such a limit has. That covers all of it: the arrival
    /// that completes a stand-off, an operator redirecting the vessel, a stop, an emergency
    /// stop, a fresh berthing approach and the shoreline latching a block. A limit that outlived
    /// any one of those would be a limit nothing had asked for.
    /// </para>
    /// </remarks>
    private void ClearTask()
    {
        _hasTarget = false;
        RemainingDistanceM = 0.0;
        _stationKeep = null;
        StationKeepOutcome = StationKeepOutcome.Disengaged;
        BlockingReason = WaterBlockReason.None;

        if (_cruiseSpeedBeforeManoeuvreMps is { } standing)
        {
            _cruiseSpeedMps = standing;
            _cruiseSpeedBeforeManoeuvreMps = null;
        }
    }

    /// <summary>Holds the speed down for the duration of one manoeuvre, remembering what to restore.</summary>
    /// <remarks>
    /// A <em>limit</em>, never a setting: it can only lower the speed in force, so a manoeuvre
    /// asking for four metres per second on a vessel already told to make two leaves it making
    /// two. Restoring the remembered value is <see cref="ClearTask"/>'s job, and the remembered
    /// value is taken only once, so a second call within the same manoeuvre tightens the limit
    /// without losing the standing setting behind it.
    /// </remarks>
    /// <param name="speedMps">Speed limit for the manoeuvre, in metres per second. Ignored if not usable.</param>
    private void ApplyManoeuvreSpeed(double speedMps)
    {
        if (!double.IsFinite(speedMps) || speedMps <= 0.0)
        {
            return;
        }

        _cruiseSpeedBeforeManoeuvreMps ??= _cruiseSpeedMps;
        _cruiseSpeedMps = Math.Min(speedMps, _cruiseSpeedMps);
    }

    /// <summary>Abandons any berthing approach in progress, naming why.</summary>
    /// <param name="reason">Condition that abandoned it.</param>
    private void CancelDocking(DockingAbortReason reason)
    {
        if (_dockingPlan is null && !DockingProgress.IsActive)
        {
            return;
        }

        DockingProgress = DockingProgress.AbortedFor(reason);
        DockingAbortReason = reason;
        _dockingPlan = null;
    }

    /// <summary>Packs a setpoint and the navigator's current state into an outcome.</summary>
    /// <param name="setpoint">Setpoint to integrate this step.</param>
    /// <param name="hasReachedTarget">True only on the call that completed the target.</param>
    /// <param name="hasBecomeBlocked">True only on the call that latched the blocked mode.</param>
    /// <returns>The outcome to hand back to the owning asset.</returns>
    private SurfaceGuidanceOutcome Outcome(
        SurfaceSetpoint setpoint, bool hasReachedTarget = false, bool hasBecomeBlocked = false) =>
        new(setpoint, Mode, RemainingDistanceM, hasReachedTarget, hasBecomeBlocked, BlockingReason);
}
