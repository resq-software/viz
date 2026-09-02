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

namespace ResQ.Viz.Web.Services.Assets.Ground;

/// <summary>What the navigator is currently trying to do.</summary>
/// <remarks>
/// A guidance mode, not an <see cref="Models.OperationalState"/>. The two are related but not
/// the same: this says which control law is running, while the operational state says how a
/// command validator should treat the asset. <see cref="GroundAsset"/> maps one onto the other,
/// and that mapping is where the interesting decision lives — being stopped by bad ground is not
/// a fault of the vehicle, so it does not become one on the wire.
/// </remarks>
public enum GroundGuidanceMode
{
    /// <summary>No target and no manual input. The vehicle sits where it is.</summary>
    Idle,

    /// <summary>Closing on a target under the autonomous guidance law.</summary>
    Driving,

    /// <summary>Backing up under direct operator command, with no target.</summary>
    Reversing,

    /// <summary>Following a directly commanded speed and steering angle.</summary>
    Manual,

    /// <summary>Mission progress suspended but resumable; the target is retained.</summary>
    Holding,

    /// <summary>Stopped and secured until explicitly released. The target is discarded.</summary>
    Parked,

    /// <summary>Refusing to continue because the ground ahead is not traversable.</summary>
    Blocked,

    /// <summary>Latched by an emergency stop. Only an explicit release leaves this state.</summary>
    EmergencyStopped,
}

/// <summary>What the navigator is told about the world before it produces a setpoint.</summary>
/// <remarks>
/// Two facts, deliberately separated. <paramref name="Contact"/> is the ground the vehicle is
/// <em>on</em> — it sets the speed ceiling and says whether the vehicle can move at all — while
/// the look-ahead pair is the ground the vehicle is about to be on, which is what lets the
/// navigator refuse a route before driving into it rather than after.
/// <para>
/// The contact is required; only the look-ahead pair is optional, and omitting it reads as
/// "nothing known ahead", so a caller that cannot probe — a test driving the control law from
/// literals — gets permissive behaviour rather than a spurious refusal. Sampling the look-ahead is
/// the owning asset's job, because it is the only party holding an environment sampler.
/// </para>
/// </remarks>
/// <param name="Contact">Terrain contact resolved at the vehicle's current position.</param>
/// <param name="AheadClass">Traversability of the probed point ahead, along the direction of travel.</param>
/// <param name="AheadReason">Why that point got its classification. Reported verbatim when it blocks.</param>
public readonly record struct GroundGuidanceInput(
    TerrainContactState Contact,
    TraversabilityClass AheadClass = TraversabilityClass.Traversable,
    TraversabilityReason AheadReason = TraversabilityReason.None);

/// <summary>The setpoint the navigator produced, and the transitions it made producing it.</summary>
/// <remarks>
/// <paramref name="HasReachedTarget"/> and <paramref name="HasBecomeBlocked"/> are <b>edge</b>
/// flags: each is true on exactly the one call that made the transition and false on every call
/// after it. Reporting them as levels is how an event queue fills with one "target reached" per
/// tick for as long as the vehicle sits on its waypoint, which is precisely the defect the event
/// discipline here exists to prevent.
/// </remarks>
/// <param name="Setpoint">What to ask the motion model for this step.</param>
/// <param name="Mode">Guidance mode after this call.</param>
/// <param name="RemainingDistanceM">Horizontal distance still to run to the target, in metres. Zero without one.</param>
/// <param name="HasReachedTarget">True only on the call that completed the target.</param>
/// <param name="HasBecomeBlocked">True only on the call that entered <see cref="GroundGuidanceMode.Blocked"/>.</param>
/// <param name="BlockingReason">Why the route was refused, or <see cref="TraversabilityReason.None"/>.</param>
public readonly record struct GroundGuidanceOutcome(
    GroundSetpoint Setpoint,
    GroundGuidanceMode Mode,
    double RemainingDistanceM,
    bool HasReachedTarget,
    bool HasBecomeBlocked,
    TraversabilityReason BlockingReason);
