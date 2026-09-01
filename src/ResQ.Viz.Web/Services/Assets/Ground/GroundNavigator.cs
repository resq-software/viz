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

/// <summary>Turns a ground target into a setpoint: heading error to steering, distance to speed.</summary>
/// <remarks>
/// The whole of the guidance law for a ground vehicle, and deliberately nothing else. It holds no
/// terrain, no sampler, no event queue and no command validation, so its behaviour can be driven
/// end to end from literals — a heading error and a distance in, a setpoint out — and
/// <see cref="GroundAsset"/> can be reasoned about without a control law underneath it. It is the
/// same split, for the same reason, that keeps <see cref="IGroundDynamics"/> down to arithmetic.
/// <para>
/// Deterministic and allocation-free. Every member is arithmetic over its arguments and this
/// object's own fields: no clock, no substepping, no convergence test, and no iteration count
/// that varies with state. <see cref="Sample"/> returns a value type and allocates nothing.
/// </para>
/// <para>
/// Two control laws, selected once at construction by <see cref="GroundProfile.CanPivotTurn"/>. A
/// pivot-steered platform turns on the spot until it roughly points at the target and only then
/// drives, because that is both what it does and the shortest path. A steered platform cannot:
/// its yaw rate is proportional to its speed, so stopping to turn stops it turning. It arcs
/// instead, under pure pursuit, and keeps a floor under its speed so a target directly behind it
/// is reached by driving round rather than by sitting still at full lock.
/// </para>
/// <para>
/// Advisory. The refusal to enter <see cref="TraversabilityClass.Blocked"/> ground rests on a
/// procedural height field and a quasi-static platform envelope; it is decision support for an
/// operator, never a guarantee that what it does permit is safe to drive.
/// </para>
/// </remarks>
public sealed partial class GroundNavigator
{
    /// <summary>Smallest arrival tolerance any platform uses, in metres.</summary>
    /// <remarks>
    /// A floor under the footprint-derived tolerance. Asking a vehicle to stop within a few
    /// centimetres of a point makes arrival depend on the last bits of the integration, so it
    /// would creep past, brake, creep back, and never settle.
    /// </remarks>
    public const double MinArrivalToleranceM = 0.75;

    /// <summary>Proportional gain from heading error to commanded yaw rate, per second.</summary>
    private const double HeadingGainPerSec = 1.2;

    /// <summary>Heading error above which a pivot-steered platform turns before it drives, in radians.</summary>
    private const double PivotHeadingErrorRad = 0.35;

    /// <summary>Fraction of cruise speed a steered platform keeps while turning hardest.</summary>
    /// <remarks>
    /// Without a floor, <c>cos(error)</c> reaches zero at ninety degrees and goes negative behind,
    /// so a target off the beam would stop the vehicle — and a steered platform that is not moving
    /// cannot change its heading at all. The floor is what turns "cannot reach it" into "drives
    /// round to it".
    /// </remarks>
    private const double MinManoeuvreSpeedFraction = 0.25;

    /// <summary>Fraction of the braking limit the approach profile plans against.</summary>
    /// <remarks>
    /// Margin, not timidity. The achievable braking rate is scaled by traction, which the approach
    /// profile does not see, so planning against the full dry-ground figure would overshoot every
    /// wet arrival.
    /// </remarks>
    private const double ApproachBrakingFraction = 0.6;

    /// <summary>Seconds of travel the pure-pursuit look-ahead point is placed ahead by.</summary>
    private const double LookaheadSeconds = 1.5;

    /// <summary>Shortest pure-pursuit look-ahead distance, in metres.</summary>
    private const double MinLookaheadM = 2.0;

    /// <summary>Floor under the recovery crawl ceiling, in metres per second.</summary>
    /// <remarks>
    /// The derived ceiling collapses towards zero for a platform with a small footprint and a
    /// feeble brake, and a vehicle permitted to move at nought metres per second is not
    /// recoverable at all — which is the whole failure this floor exists to keep out of reach.
    /// A quarter of a metre per second is a slow walk: enough to leave the patch, small enough
    /// that nothing about the manoeuvre is energetic.
    /// </remarks>
    private const double MinRecoveryCeilingMps = 0.25;

    private readonly GroundProfile _profile;
    private readonly double _minLookaheadM;
    private readonly double _maxYawRateRadPerSec;

    private Vector3 _targetEus;
    private bool _hasTarget;
    private double _cruiseSpeedMps;
    private double _manualSpeedMps;
    private double _manualSteeringRad;

    /// <summary>Builds a navigator for one platform.</summary>
    /// <param name="profile">Envelope whose steering lock, braking rate and footprint shape the guidance law.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="ArgumentException">The profile is not usable by a ground model.</exception>
    public GroundNavigator(GroundProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile.Validated(nameof(profile));

        // A vehicle has arrived when its own footprint covers the point. Asking for better than
        // that is asking for precision the platform's geometry does not have.
        ArrivalToleranceM = Math.Max(MinArrivalToleranceM, _profile.FootprintRadiusM);

        // Never shorter than the wheelbase: a look-ahead point inside the vehicle makes the
        // pure-pursuit arc tighter than the steering lock can drive, and the law then sits at
        // full lock and weaves.
        _minLookaheadM = Math.Max(MinLookaheadM, _profile.WheelbaseM);

        // The yaw rate a skid-steer reaches with its sides at full opposite speed. A bound rather
        // than a target: the motion model clamps track speeds itself, and this only stops a large
        // heading error asking for a rate no drivetrain could chase.
        _maxYawRateRadPerSec = _profile.TrackWidthM > 0.0
            ? 2.0 * _profile.MaxForwardSpeedMps / _profile.TrackWidthM
            : _profile.MaxForwardSpeedMps;

        _cruiseSpeedMps = _profile.MaxForwardSpeedMps;

        // The fastest a recovery manoeuvre may run and still be arrested inside the vehicle's own
        // footprint on the worst grip the integrator will admit: v = sqrt(2 a s), with
        // a = MaxBrakingMps2 * GroundConditions.MinTractionCoefficient and s the footprint radius.
        // Derived from the platform rather than tuned, so a heavier-braked or longer vehicle backs
        // off faster without anyone re-picking a number. The profile's own reverse limit still
        // binds it afterwards — the motion model gates reverse off entirely for a platform that
        // declares none, so nothing here can hand one a reverse it does not have.
        RecoveryCeilingMps = Math.Max(
            MinRecoveryCeilingMps,
            Math.Sqrt(
                2.0 * _profile.MaxBrakingMps2 * GroundConditions.MinTractionCoefficient
                * _profile.FootprintRadiusM));
    }

    /// <summary>How close the vehicle must get before a target counts as reached, in metres.</summary>
    public double ArrivalToleranceM { get; }

    /// <summary>Speed ceiling, in metres per second, for an operator recovery off immobilising ground.</summary>
    /// <remarks>
    /// Read by <see cref="GroundAsset"/> as well as by the guidance law, so the ceiling the
    /// setpoint is clamped to and the ceiling the integrator is handed are the same number. Two
    /// copies of it would let a rover be commanded a crawl it is then not permitted to execute,
    /// which reads from outside exactly like the bricked vehicle this exists to prevent.
    /// <para>
    /// <b>Advisory.</b> It is a quasi-static stopping-distance estimate over a procedural height
    /// field, not a claim that reversing off this ground is safe.
    /// </para>
    /// </remarks>
    public double RecoveryCeilingMps { get; }

    /// <summary>What the navigator is currently trying to do.</summary>
    public GroundGuidanceMode Mode { get; private set; } = GroundGuidanceMode.Idle;

    /// <summary>Stable lower-case token for <see cref="Mode"/>, for the wire's mode string.</summary>
    /// <remarks>Display and filtering only. Never branch behaviour on it; branch on <see cref="Mode"/>.</remarks>
    public string ModeToken => Mode switch
    {
        GroundGuidanceMode.Driving => "drive",
        GroundGuidanceMode.Reversing => "reverse",
        GroundGuidanceMode.Manual => "manual",
        GroundGuidanceMode.Holding => "hold",
        GroundGuidanceMode.Parked => "park",
        GroundGuidanceMode.Blocked => "blocked",
        GroundGuidanceMode.EmergencyStopped => "emergency-stop",
        _ => "idle",
    };

    /// <summary>Whether a mode is direct operator input rather than autonomous progress.</summary>
    /// <remarks>
    /// The distinction immobilisation is allowed to act on, and the reason it is stated once here
    /// rather than restated at each site. Ground that will not carry a vehicle must stop it
    /// <em>driving itself</em> further into that ground; it must not also take away the controls,
    /// because backing out the way it came in is how a stuck vehicle is actually recovered. A
    /// second copy of this predicate in <see cref="GroundAsset"/> is how the guidance law and the
    /// integrator would come to disagree about whether a rover is being recovered — one
    /// commanding a crawl, the other holding the ceiling at zero, and the vehicle bricked between
    /// them.
    /// </remarks>
    /// <param name="mode">Guidance mode to classify.</param>
    /// <returns><see langword="true"/> when the mode is an operator's own input.</returns>
    public static bool IsOperatorRecovery(GroundGuidanceMode mode) =>
        mode is GroundGuidanceMode.Manual or GroundGuidanceMode.Reversing;

    /// <summary>Target being driven to, or <see langword="null"/> when none is assigned.</summary>
    public Vector3? TargetEus => _hasTarget ? _targetEus : null;

    /// <summary>Horizontal distance still to run, in metres. Zero without a target.</summary>
    public double RemainingDistanceM { get; private set; }

    /// <summary>Why the route was refused, or <see cref="TraversabilityReason.None"/>.</summary>
    public TraversabilityReason BlockingReason { get; private set; } = TraversabilityReason.None;

    /// <summary>Commanded cruise speed in metres per second; never above the profile's limit.</summary>
    public double CruiseSpeedMps => _cruiseSpeedMps;

    /// <summary>Assigns a target and begins driving to it.</summary>
    /// <remarks>
    /// Clears a latched block, because a new target is a new decision by the operator and the old
    /// refusal said nothing about this route. Whether the <em>new</em> target is reachable is
    /// checked before it gets here — see <see cref="GroundAsset.Apply"/> — and again by the
    /// look-ahead on every step after it.
    /// </remarks>
    /// <param name="targetEus">Destination in the scene frame; the vertical component is ignored.</param>
    /// <param name="speedMps">Cruise speed to use, or null to keep the current one.</param>
    /// <exception cref="ArgumentException"><paramref name="targetEus"/> has a non-finite horizontal component.</exception>
    public void DriveTo(Vector3 targetEus, double? speedMps = null)
    {
        if (!float.IsFinite(targetEus.X) || !float.IsFinite(targetEus.Z))
        {
            throw new ArgumentException("A drive target must be finite.", nameof(targetEus));
        }

        if (speedMps is { } requested)
        {
            SetCruiseSpeed(requested);
        }

        _targetEus = targetEus;
        _hasTarget = true;
        BlockingReason = TraversabilityReason.None;
        Mode = GroundGuidanceMode.Driving;
    }

    /// <summary>Sets the cruise speed, clamped into what the platform can sustain.</summary>
    /// <remarks>
    /// Clamped rather than refused: the profile ceiling is a physical fact, not a permission, so
    /// "as fast as you can" is the honest reading of a request above it. A non-finite or
    /// non-positive value is ignored, because direction is chosen by the command and never by the
    /// sign of a speed.
    /// </remarks>
    /// <param name="speedMps">Requested cruise speed in metres per second.</param>
    public void SetCruiseSpeed(double speedMps)
    {
        if (!double.IsFinite(speedMps) || speedMps <= 0.0)
        {
            return;
        }

        _cruiseSpeedMps = Math.Min(speedMps, _profile.MaxForwardSpeedMps);
    }

    /// <summary>Takes direct control of speed and steering angle.</summary>
    /// <remarks>
    /// Discards any target. A manual takeover that silently kept a waypoint would resume driving
    /// to it the moment autonomy was handed back, which is not what an operator taking the
    /// controls asked for, so <see cref="Resume"/> returns to idle rather than to a route.
    /// </remarks>
    /// <param name="speedMps">Requested longitudinal speed; negative requests reverse.</param>
    /// <param name="steeringAngleRad">Requested road-wheel angle in radians, positive to starboard.</param>
    /// <exception cref="ArgumentException">Either argument is not finite.</exception>
    public void SetManualControl(double speedMps, double steeringAngleRad)
    {
        if (!double.IsFinite(speedMps) || !double.IsFinite(steeringAngleRad))
        {
            throw new ArgumentException("Manual control inputs must be finite.", nameof(speedMps));
        }

        _manualSpeedMps = speedMps;
        _manualSteeringRad = Math.Clamp(
            steeringAngleRad, -_profile.MaxSteeringAngleRad, _profile.MaxSteeringAngleRad);
        _hasTarget = false;
        BlockingReason = TraversabilityReason.None;
        Mode = GroundGuidanceMode.Manual;
    }

    /// <summary>Backs the vehicle up in a straight line, with the steering centred.</summary>
    /// <remarks>
    /// Open loop and untargeted, which is what the command vocabulary describes: <c>reverse</c>
    /// carries no destination, so there is nothing to close a loop on. The vehicle backs up until
    /// it is told otherwise, until the ground behind it refuses, or until the terrain immobilises
    /// it. Whether the platform may reverse at all is the caller's gate, not this one's.
    /// </remarks>
    /// <param name="speedMps">Reverse speed magnitude, or null for the profile's reverse limit.</param>
    public void Reverse(double? speedMps = null)
    {
        double requested = speedMps is { } value && double.IsFinite(value) && value > 0.0
            ? value
            : _profile.MaxReverseSpeedMps;

        _manualSpeedMps = -Math.Min(requested, _profile.MaxReverseSpeedMps);
        _manualSteeringRad = 0.0;
        _hasTarget = false;
        BlockingReason = TraversabilityReason.None;
        Mode = GroundGuidanceMode.Reversing;
    }

    /// <summary>Suspends mission progress while keeping the target, so it can be resumed.</summary>
    /// <remarks>
    /// A rover satisfies <c>hold</c> by stopping and staying stopped: holding a spot on land costs
    /// it nothing and needs no station-keeping capability. That asymmetry with a displacement
    /// hull, which cannot hold a point at all, is exactly why <c>hold</c> is ungated in the
    /// command catalog.
    /// </remarks>
    public void Hold() => Mode = GroundGuidanceMode.Holding;

    /// <summary>Comes to a controlled stop and discards the target.</summary>
    /// <remarks>
    /// Discarding is what separates this from <see cref="Hold"/>. A stopped vehicle is awaiting
    /// new instructions, and resuming autonomy must not send it off to the waypoint the operator
    /// stopped it from reaching.
    /// </remarks>
    public void Stop()
    {
        _hasTarget = false;
        RemainingDistanceM = 0.0;
        BlockingReason = TraversabilityReason.None;
        Mode = GroundGuidanceMode.Idle;
    }

    /// <summary>Stops and secures the vehicle until it is explicitly released.</summary>
    public void Park()
    {
        _hasTarget = false;
        RemainingDistanceM = 0.0;
        BlockingReason = TraversabilityReason.None;
        Mode = GroundGuidanceMode.Parked;
    }

    /// <summary>Latches the emergency-stop mode: zero motion, steering centred.</summary>
    /// <remarks>
    /// Takes effect on the very next <see cref="Sample"/>, which returns
    /// <see cref="GroundSetpoint.Stop"/> unconditionally in this mode — so the drivetrain is
    /// commanded to zero within one step, whatever it was doing, and the drivetrain then chases
    /// that at the profile's braking rate. The target is discarded: after an emergency stop,
    /// silently resuming the interrupted route is the last thing anyone wants.
    /// </remarks>
    public void EmergencyStop()
    {
        _hasTarget = false;
        RemainingDistanceM = 0.0;
        _manualSpeedMps = 0.0;
        _manualSteeringRad = 0.0;
        BlockingReason = TraversabilityReason.None;
        Mode = GroundGuidanceMode.EmergencyStopped;
    }

    /// <summary>Hands control back to autonomy.</summary>
    /// <remarks>
    /// Resumes a held route when one survived, and otherwise idles. It never resurrects a target
    /// that <see cref="Stop"/>, <see cref="Park"/>, <see cref="EmergencyStop"/> or a manual
    /// takeover discarded, because each of those was a deliberate decision to abandon it.
    /// </remarks>
    public void Resume()
    {
        BlockingReason = TraversabilityReason.None;
        Mode = _hasTarget ? GroundGuidanceMode.Driving : GroundGuidanceMode.Idle;
    }

    /// <summary>Latches the blocked mode after something outside the navigator refused the route.</summary>
    /// <remarks>
    /// The way a physical impact reaches guidance. A struck step is discovered by
    /// <see cref="TerrainContact.TryDetectStepCollision"/> <em>after</em> the vehicle has moved,
    /// too late for the look-ahead; without this the vehicle would re-accelerate into the same
    /// obstruction on the next step and keep doing so, raising a fresh impact each time.
    /// </remarks>
    /// <param name="reason">Why the route is refused; published as <see cref="BlockingReason"/>.</param>
    /// <returns><see langword="true"/> when this call made the transition, so the caller raises exactly one event.</returns>
    public bool Block(TraversabilityReason reason)
    {
        if (Mode == GroundGuidanceMode.Blocked)
        {
            return false;
        }

        _hasTarget = false;
        BlockingReason = reason;
        Mode = GroundGuidanceMode.Blocked;
        return true;
    }
}
