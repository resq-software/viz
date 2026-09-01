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
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets.Surface;

// The value half of the surface guidance layer: the modes, the operating policy, and the two
// records the control law reads and writes. Split from the law itself in SurfaceNavigator.cs so
// neither file outgrows a reading, and because these types are what a consumer binds against
// while the law is what the surface domain calls.

/// <summary>What the surface guidance law is currently trying to do.</summary>
/// <remarks>
/// A guidance mode, not an <see cref="OperationalState"/>: this says which control law is
/// running, while the operational state says how a command validator should treat the asset.
/// <see cref="SurfaceAsset"/> maps one onto the other, and the interesting decision lives in
/// that mapping — a vessel stopped by the shoreline is not faulted, because the commands that
/// recover it are the ones a fault would refuse.
/// </remarks>
public enum SurfaceGuidanceMode
{
    /// <summary>No target and no manual input. The propeller is stopped and the vessel drifts.</summary>
    Idle,

    /// <summary>Closing on a position under the autonomous guidance law.</summary>
    Transiting,

    /// <summary>Steering a commanded course over ground.</summary>
    Steering,

    /// <summary>Mission progress suspended by the safest means the profile allows. The target is retained.</summary>
    Holding,

    /// <summary>Actively holding a station against wind and current.</summary>
    StationKeeping,

    /// <summary>Flying a structured berthing approach; see <see cref="Docking"/>.</summary>
    Docking,

    /// <summary>Standing off from a berth to a released position.</summary>
    Undocking,

    /// <summary>Refusing to continue because the water ahead is not navigable.</summary>
    Blocked,

    /// <summary>Latched by an emergency stop. See <see cref="SurfaceSafetyPolicy"/> for what that does.</summary>
    EmergencyStopped,
}

/// <summary>What an emergency stop does to one hull.</summary>
/// <remarks>
/// Two genuinely different manoeuvres, and which is available is a fact about the propulsion
/// arrangement rather than a preference.
/// </remarks>
public enum SurfaceEmergencyStopBehaviour
{
    /// <summary>
    /// Stop the propeller. <b>The vessel does not stop moving</b> — it carries its way off and
    /// then drifts with the current and the wind.
    /// </summary>
    AllStop,

    /// <summary>Hold the position the stop was issued at. Only available to a hull that can station-keep.</summary>
    HoldStation,
}

/// <summary>Operating policy for one vessel: what a stop does, and what a lost link does.</summary>
/// <remarks>
/// A <b>policy</b>, deliberately separate from <see cref="SurfaceProfile"/> and from the
/// executor. The profile is the integrator's contract — draft, time constants, turning circle —
/// and neither of the decisions here is physics. Hard-coding them in the executor is worse
/// still: "a lost link means drift" is right for a single-screw workboat and wrong for the
/// thruster-equipped hull that replaces it, and a fleet that berths on link loss must be able to
/// say so rather than patch an asset class.
/// <para>
/// <see cref="For"/> derives the default from the profile, so a vessel that has not been given
/// an explicit policy still gets a documented one rather than an accidental one.
/// </para>
/// </remarks>
/// <param name="EmergencyStop">What an emergency stop does to this hull.</param>
/// <param name="LinkLoss">
/// What this vessel will do if the command link drops, published on
/// <see cref="SurfaceDomainState.LinkLossBehavior"/> so an operator can read it before it
/// happens rather than infer it afterwards.
/// </param>
/// <param name="InhibitPropulsionOnEmergencyStop">
/// True when an emergency stop also refuses later motion commands until it is explicitly
/// released. <see cref="CommandKinds.Stop"/> is exempt in every case — the catalog permits it
/// in every operational state — so a drifting vessel is always two commands from being under
/// way again and can never be latched out of its own recovery.
/// </param>
public readonly record struct SurfaceSafetyPolicy(
    SurfaceEmergencyStopBehaviour EmergencyStop,
    LinkLossBehavior LinkLoss,
    bool InhibitPropulsionOnEmergencyStop)
{
    /// <summary>The default policy for a hull, derived from what its propulsion can actually do.</summary>
    /// <remarks>
    /// A hull that can hold a position stops by holding it and waits out a lost link on station.
    /// A hull that cannot does the only other thing available: it stops the propeller and
    /// drifts, and says so. The asymmetry with the ground domain is the whole point — a rover
    /// that loses its link stops and stays put for free, and no vessel can.
    /// </remarks>
    /// <param name="profile">Hull to derive a policy for.</param>
    /// <returns>The default policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    public static SurfaceSafetyPolicy For(SurfaceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.CanStationKeep
            ? new SurfaceSafetyPolicy(
                SurfaceEmergencyStopBehaviour.HoldStation,
                LinkLossBehavior.HoldPosition,
                InhibitPropulsionOnEmergencyStop: true)
            : new SurfaceSafetyPolicy(
                SurfaceEmergencyStopBehaviour.AllStop,
                LinkLossBehavior.DriftAndAlert,
                InhibitPropulsionOnEmergencyStop: true);
    }
}

/// <summary>What the navigator is told about the world before it produces a setpoint.</summary>
/// <remarks>
/// Two facts about the water, deliberately separated. <paramref name="IsHereNavigable"/> is the
/// water the vessel is <em>in</em> — false once it is aground or ashore — while the look-ahead
/// pair is the water it is about to be in, which is what lets a transit be refused before the
/// hull is on the beach rather than after.
/// <para>
/// The look-ahead pair defaults to "nothing known ahead", so a caller that cannot probe — a test
/// driving the control law from literals — gets permissive behaviour rather than a spurious
/// refusal. Sampling it is the owning asset's job, because the asset is the only party holding
/// an environment sampler.
/// </para>
/// </remarks>
/// <param name="DeltaSeconds">Timestep in seconds; only the docking machine's time budget reads it.</param>
/// <param name="SpeedCeilingMps">Speed ceiling the water permits, in metres per second. See <see cref="SurfaceConditions.SpeedCeilingMps"/>.</param>
/// <param name="Velocities">Resolved heading, course, speeds and drift at the vessel.</param>
/// <param name="PassiveDriftEus">Velocity an unpowered hull would make good here, in metres per second.</param>
/// <param name="WindEus">Wind velocity at the vessel, in metres per second, in the scene frame.</param>
/// <param name="HasPositionFix">False once position quality has been lost.</param>
/// <param name="IsHereNavigable">False when the vessel is already aground or ashore.</param>
/// <param name="IsTargetNavigable">False when the assigned destination has stopped being navigable.</param>
/// <param name="AheadClass">Navigability of the probed point along the direction of travel.</param>
/// <param name="AheadReason">Why that point got its classification. Reported verbatim when it blocks.</param>
public readonly record struct SurfaceGuidanceInput(
    double DeltaSeconds,
    double SpeedCeilingMps,
    SurfaceVelocities Velocities,
    Vector3 PassiveDriftEus,
    Vector3 WindEus,
    bool HasPositionFix = true,
    bool IsHereNavigable = true,
    bool IsTargetNavigable = true,
    WaterNavigability AheadClass = WaterNavigability.Navigable,
    WaterBlockReason AheadReason = WaterBlockReason.None);

/// <summary>The setpoint the navigator produced, and the transitions it made producing it.</summary>
/// <remarks>
/// <paramref name="HasReachedTarget"/> and <paramref name="HasBecomeBlocked"/> are <b>edge</b>
/// flags: each is true on exactly the call that made the transition and false on every call
/// after it. Reporting them as levels is how an event queue fills with one "target reached" per
/// tick for as long as a vessel sits on its waypoint, which is precisely the defect the event
/// discipline here exists to prevent. The docking and station-keeping transitions are not
/// duplicated here: they are read off <see cref="SurfaceNavigator.DockingProgress"/> and
/// <see cref="SurfaceNavigator.StationKeepOutcome"/>, whose phases the owning asset compares
/// against the previous step's.
/// </remarks>
/// <param name="Setpoint">What to ask the motion model for this step.</param>
/// <param name="Mode">Guidance mode after this call.</param>
/// <param name="RemainingDistanceM">Horizontal distance still to run, in metres. Zero without a target.</param>
/// <param name="HasReachedTarget">True only on the call that completed the target.</param>
/// <param name="HasBecomeBlocked">True only on the call that entered <see cref="SurfaceGuidanceMode.Blocked"/>.</param>
/// <param name="BlockingReason">Why the water was refused, or <see cref="WaterBlockReason.None"/>.</param>
public readonly record struct SurfaceGuidanceOutcome(
    SurfaceSetpoint Setpoint,
    SurfaceGuidanceMode Mode,
    double RemainingDistanceM,
    bool HasReachedTarget,
    bool HasBecomeBlocked,
    WaterBlockReason BlockingReason);
