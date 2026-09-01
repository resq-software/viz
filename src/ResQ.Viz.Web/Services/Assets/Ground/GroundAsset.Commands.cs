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
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets.Ground;

// The command half of GroundAsset: translating a validated multi-domain command into guidance
// state, and refusing everything a rover cannot or must not do. Split from the telemetry half so a
// change to what a rover reports cannot silently alter what it accepts; the type's summary lives
// on the primary declaration in GroundAsset.cs.
public sealed partial class GroundAsset
{
    /// <inheritdoc />
    /// <remarks>
    /// <b>Defence in depth, in this order.</b> The v2 pipeline has already checked the issuer, the
    /// payload, the lease, the capability, the domain and the operational state before a command is
    /// translated — and every one of those checks is repeated or reinforced here, because the v1
    /// compatibility adapter builds a <see cref="SimulatedAssetCommand"/> directly without passing
    /// the v2 gate, and because a check that only ever runs in one place is one refactor away from
    /// not running at all.
    /// <list type="number">
    ///   <item><description>
    ///     <b>Domain first, before capability.</b> Deliberately, and the ordering is load-bearing:
    ///     a rover declares <see cref="AssetCapability.Land"/> — it is what <c>park</c> gates on —
    ///     so a <c>land</c> command <em>passes</em> the capability check, and only the domain gate
    ///     refuses it. The same is true of <c>stationKeep</c>, which a rover has the capability for
    ///     and which is nonetheless a surface command.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Capability</b>, read from the catalog's own any-of/all-of rule rather than restated,
    ///     so the asset accepts exactly the set its capability report advertises.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The emergency-stop latch</b>, which refuses everything except its own release.
    ///   </description></item>
    /// </list>
    /// Every rejection is side-effect free: nothing is written to the navigator until the command
    /// is known to be executable, so a refused <c>driveTo</c> leaves behind neither a target nor a
    /// cleared block.
    /// </remarks>
    public AssetCommandResult Apply(in SimulatedAssetCommand command)
    {
        if (!string.Equals(command.AssetId, AssetId, StringComparison.Ordinal))
        {
            return AssetCommandResult.Rejected("command.assetMismatch");
        }

        if (RejectByDomain(command.Kind) is { } wrongDomain)
        {
            return AssetCommandResult.Rejected(wrongDomain);
        }

        // The catalog's own rule rather than a restatement of it: a second hand-written table
        // drifts from the first the moment either is edited alone.
        if (!command.IsSatisfiedBy(Descriptor.Capabilities))
        {
            return AssetCommandResult.Rejected("capability.missing");
        }

        if (IsEmergencyStopped && !IsEmergencyRelease(command.Kind))
        {
            return AssetCommandResult.Rejected("asset.emergencyStopped");
        }

        switch (command.Kind)
        {
            case AssetCommandKind.EmergencyStop:
                EngageEmergencyStop();
                return AssetCommandResult.Accepted;

            // Stop is one of the two commands the catalog permits in every operational state, which
            // makes it the always-reachable release. Without that the latch would be a trap: an
            // emergency-stopped rover publishes OperationalState.Emergency, which the catalog's
            // Operable policy excludes, so resumeAutonomy would be refused upstream and nothing
            // could ever bring the vehicle back.
            case AssetCommandKind.Stop:
                ReleaseEmergencyStop();
                _navigator.Stop();
                return AssetCommandResult.Accepted;

            case AssetCommandKind.ResumeAutonomy:
                ReleaseEmergencyStop();
                _navigator.Resume();
                return AssetCommandResult.Accepted;

            // Hold means stop making mission progress while staying safe, and a rover satisfies it
            // by stopping and staying stopped — no station-keeping capability required, which is
            // why the catalog leaves hold ungated. The target survives, so resumeAutonomy picks the
            // route up where it was suspended.
            case AssetCommandKind.Hold:
                _navigator.Hold();
                return AssetCommandResult.Accepted;

            case AssetCommandKind.Park:
                _navigator.Park();
                return AssetCommandResult.Accepted;

            // goTo and driveTo are the same manoeuvre for a ground asset: goTo is the
            // domain-neutral spelling and driveTo the ground-domain one, and a rover navigating in
            // two dimensions executes both identically. Diverging them would mean an operator's
            // choice of vocabulary changed the vehicle's behaviour.
            case AssetCommandKind.GoTo:
            case AssetCommandKind.DriveTo:
                return ApplyDriveTo(in command);

            case AssetCommandKind.ReturnToBase:
                return ApplyDriveTo(in command, _basePositionEus);

            case AssetCommandKind.SetSpeed:
                return ApplySetSpeed(in command);

            case AssetCommandKind.SetSteering:
                return ApplySetSteering();

            case AssetCommandKind.Reverse:
                return ApplyReverse(in command);

            default:
                return AssetCommandResult.Rejected("command.unsupported");
        }
    }

    /// <summary>Refuses a command that belongs to another domain, whatever the asset declares.</summary>
    /// <remarks>
    /// Never assume the catalog is the only gate. Its domain lists would already refuse each of
    /// these, but the v1 adapter does not consult the catalog, and a descriptor that wrongly
    /// declared an air or surface capability would sail through the capability check. This is the
    /// gate that still fires.
    /// </remarks>
    /// <param name="kind">Translated command kind.</param>
    /// <returns>A machine-readable rejection token, or null when the kind belongs to this domain.</returns>
    private static string? RejectByDomain(AssetCommandKind kind) => kind switch
    {
        AssetCommandKind.Takeoff or AssetCommandKind.Land or AssetCommandKind.SetAltitude
            or AssetCommandKind.Loiter => "command.domain.air",

        AssetCommandKind.TransitTo or AssetCommandKind.SetCourse or AssetCommandKind.StationKeep
            or AssetCommandKind.Dock or AssetCommandKind.Undock => "command.domain.surface",

        _ => null,
    };

    /// <summary>Whether a command is one of the three that may reach a disarmed rover.</summary>
    /// <remarks>
    /// A repeated emergency stop is included so re-issuing one is never refused. Refusing to stop
    /// something because it is already stopping is exactly backwards, and it is the same reasoning
    /// that makes the stop commands ungated in the catalog.
    /// </remarks>
    /// <param name="kind">Translated command kind.</param>
    /// <returns><see langword="true"/> when the command may execute while the latch is set.</returns>
    private static bool IsEmergencyRelease(AssetCommandKind kind) =>
        kind is AssetCommandKind.Stop or AssetCommandKind.ResumeAutonomy
            or AssetCommandKind.EmergencyStop;

    /// <summary>Resolves a command target into a scene-frame position.</summary>
    /// <remarks>
    /// Only the scene frame is accepted. Converting from NED or ENU needs a shared origin, and
    /// guessing one is how a waypoint ends up mirrored about the map; that conversion belongs in
    /// the translation layer, where the origin is known.
    /// </remarks>
    /// <param name="pose">Target pose from the command, possibly null.</param>
    /// <param name="target">Resolved scene-frame position when the return value is null.</param>
    /// <returns>A machine-readable rejection token, or null when the target is usable.</returns>
    private static string? ResolveTarget(FramedPose? pose, out Vector3 target)
    {
        target = Vector3.Zero;

        if (!CoordinateFrames.TryValidate(pose, out string? error))
        {
            // The validator always supplies a token on failure; the coalesce keeps the nullable
            // analysis honest without suppressing it.
            return error ?? "command.target.invalid";
        }

        if (pose is not { Frame: CoordinateFrame.LocalEus })
        {
            return "command.target.frame";
        }

        target = pose.Position;
        return null;
    }

    /// <summary>Sends the rover to the command's target, if it is one the platform may reach.</summary>
    /// <param name="command">Command carrying the target and an optional cruise speed.</param>
    /// <returns>Acceptance, or a rejection naming why the target was refused.</returns>
    private AssetCommandResult ApplyDriveTo(in SimulatedAssetCommand command)
    {
        if (ResolveTarget(command.Target, out var target) is { } rejection)
        {
            return AssetCommandResult.Rejected(rejection);
        }

        return ApplyDriveTo(in command, target);
    }

    /// <summary>Sends the rover to an already-resolved position, if it is one it may reach.</summary>
    /// <remarks>
    /// The base position goes through exactly the same traversability check as an operator's
    /// target. A launch point is not permanently reachable: a terrain-preset change can raise the
    /// water surface over it, and a <c>returnToBase</c> that skipped the check would dispatch the
    /// rover towards a lake it would then refuse mid-route.
    /// </remarks>
    /// <param name="command">Command carrying an optional cruise speed.</param>
    /// <param name="targetEus">Destination in the scene frame.</param>
    /// <returns>Acceptance, or a rejection naming why the target was refused.</returns>
    private AssetCommandResult ApplyDriveTo(in SimulatedAssetCommand command, Vector3 targetEus)
    {
        if (RejectUntraversable(targetEus) is { } blocked)
        {
            return AssetCommandResult.Rejected(blocked);
        }

        _navigator.DriveTo(targetEus, command.SpeedMps);
        return AssetCommandResult.Accepted;
    }

    /// <summary>Refuses a destination this platform cannot occupy, naming why.</summary>
    /// <remarks>
    /// Evaluated direction-free, along the line of steepest ascent, so a
    /// <see cref="TraversabilityClass.Blocked"/> verdict means <em>no</em> approach heading works —
    /// the only honest answer before a route is chosen. The returned token is
    /// <see cref="Traversability.ReasonCode"/>'s, so the refusal names the cause — water, a
    /// prohibited zone, a grade, a cross-slope, lost traction — rather than saying only that
    /// something was wrong.
    /// <para>
    /// The <em>route</em> to the target is deliberately not swept here. A straight-line sweep would
    /// refuse a perfectly reachable destination merely because a wall sits between the two points,
    /// and finding a way round is a planner's job this simulation does not do. A block discovered
    /// mid-route is caught instead by the per-step look-ahead, which stops the vehicle short of it
    /// and raises <c>ground.blocked</c>.
    /// </para>
    /// <para>
    /// Read-only: it samples the environment and evaluates, and touches no asset state, so a
    /// refusal leaves the rover exactly as it was.
    /// </para>
    /// </remarks>
    /// <param name="targetEus">Destination in the scene frame; the vertical component is ignored.</param>
    /// <returns>A machine-readable rejection token, or null when the destination is usable.</returns>
    private string? RejectUntraversable(Vector3 targetEus)
    {
        var probe = new Vector3(
            targetEus.X,
            (float)_environment.GetElevation(targetEus.X, targetEus.Z),
            targetEus.Z);

        var sample = _environment.Sample(probe, GroundContactGeometry.NormalSpacingM(_profile));
        var verdict = Traversability.Evaluate(_profile, sample);

        return verdict.IsBlocked ? verdict.ReasonCode : null;
    }

    /// <summary>Changes the cruise speed without changing the destination.</summary>
    /// <remarks>
    /// A value above the platform's ceiling is clamped rather than refused, because that ceiling is
    /// a physical fact and "as fast as you can" is the honest reading of the request. A negative
    /// one <em>is</em> refused: direction of travel is chosen by the command — <c>driveTo</c>
    /// forwards, <c>reverse</c> backwards — and letting the sign of a speed reverse a rover would
    /// give one field two meanings.
    /// </remarks>
    /// <param name="command">Command carrying the requested speed.</param>
    /// <returns>Acceptance, or a rejection naming the fault in the requested speed.</returns>
    private AssetCommandResult ApplySetSpeed(in SimulatedAssetCommand command)
    {
        if (command.SpeedMps is not { } speed || !double.IsFinite(speed))
        {
            return AssetCommandResult.Rejected("command.speed.missing");
        }

        if (speed <= 0.0)
        {
            return AssetCommandResult.Rejected("command.speed.outOfRange");
        }

        _navigator.SetCruiseSpeed(speed);
        return AssetCommandResult.Accepted;
    }

    /// <summary>Backs the rover up, when the platform is physically able to.</summary>
    /// <remarks>
    /// Two separate gates, and the distinction matters. <see cref="AssetCapability.Reverse"/> —
    /// already checked in <see cref="Apply"/> — says the platform is <em>permitted</em> to reverse;
    /// <see cref="GroundProfile.CanReverse"/> says it physically <em>can</em>, and it is false
    /// exactly when the profile declares a zero reverse speed. Collapsing the two would let a
    /// declared capability outvote a drivetrain that cannot turn backwards, which is the sort of
    /// disagreement the capability model exists to make impossible.
    /// </remarks>
    /// <param name="command">Command carrying an optional reverse speed.</param>
    /// <returns>Acceptance, or <c>capability.reverse.unsupported</c>.</returns>
    private AssetCommandResult ApplyReverse(in SimulatedAssetCommand command)
    {
        if (!_profile.CanReverse)
        {
            return AssetCommandResult.Rejected("capability.reverse.unsupported");
        }

        _navigator.Reverse(command.SpeedMps);
        return AssetCommandResult.Accepted;
    }

    /// <summary>Commands a road-wheel angle directly.</summary>
    /// <remarks>
    /// Refused in both of its two cases, and the two are different findings rather than one.
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>command.steering.unsupported</c> — the platform has no steering linkage. A skid-steer
    ///     changes direction by driving its sides at different speeds, and
    ///     <see cref="DifferentialDynamics"/> deliberately ignores a steering angle rather than
    ///     reinterpreting it as a yaw rate, because that would give one field two meanings
    ///     depending on which model received it. Refusing is the honest answer; a caller that wants
    ///     a pivot-steered platform to turn asks for a position, not a wheel angle.
    ///   </description></item>
    ///   <item><description>
    ///     <c>command.steering.unavailable</c> — the angle cannot reach here. The validator parses
    ///     and range-checks a steering angle, but <see cref="SimulatedAssetCommand"/> carries no
    ///     steering field, so the translator has nowhere to put it and drops it. The one angular
    ///     field that <em>is</em> carried, <see cref="SimulatedAssetCommand.HeadingRad"/>, is
    ///     documented as a heading or course clockwise from true north — reading a road-wheel angle
    ///     out of it would steer to a heading-shaped number and look plausible while being wrong.
    ///   </description></item>
    /// </list>
    /// The executing path itself is complete and exercised:
    /// <see cref="GroundNavigator.SetManualControl"/> takes the angle, clamps it to the profile's
    /// lock, and drives the bicycle model with it. Carrying a steering angle onto
    /// <see cref="SimulatedAssetCommand"/> is the whole of what this still needs, and this method is
    /// where it lands.
    /// </remarks>
    /// <returns>A rejection naming which of the two cases applies.</returns>
    private AssetCommandResult ApplySetSteering() =>
        _profile.MaxSteeringAngleRad > 0.0
            ? AssetCommandResult.Rejected("command.steering.unavailable")
            : AssetCommandResult.Rejected("command.steering.unsupported");

    /// <summary>Latches the emergency stop and raises the transition event.</summary>
    /// <remarks>
    /// Takes effect within one step. The navigator is snapped into
    /// <see cref="GroundGuidanceMode.EmergencyStopped"/> immediately, and <see cref="Step"/>
    /// additionally overrides the setpoint with <see cref="GroundSetpoint.Stop"/> whenever the
    /// latch is set — so the drivetrain is commanded to zero and the steering centred on the very
    /// next integration, whatever the vehicle was doing and whatever else reached the navigator.
    /// That zero-speed setpoint is then chased at <see cref="GroundProfile.MaxBrakingMps2"/>, the
    /// hardest deceleration the profile declares.
    /// <para>
    /// Whether the drivetrain is also inhibited is <see cref="GroundSafetyPolicy"/>'s call, not
    /// this method's. Raised on the transition only, so re-issuing an emergency stop is accepted
    /// without adding a second event.
    /// </para>
    /// </remarks>
    private void EngageEmergencyStop()
    {
        bool wasEngaged = _navigator.Mode == GroundGuidanceMode.EmergencyStopped;

        _navigator.EmergencyStop();

        if (Safety.DisarmOnEmergencyStop)
        {
            IsEmergencyStopped = true;
        }

        if (wasEngaged)
        {
            return;
        }

        string braking = Safety.HasServiceBrake
            ? "braking to a halt"
            : "coasting to a halt, as the platform declares no service brake";

        string arming = Safety.DisarmOnEmergencyStop
            ? " Drivetrain inhibited until the stop is released."
            : " Drivetrain remains armed.";

        Raise(
            "ground.emergencyStop",
            AssetEventSeverity.Alert,
            $"Emergency stop engaged: motion commanded to zero, steering centred, {braking}.{arming}");
    }

    /// <summary>Clears the emergency-stop latch and raises the transition event.</summary>
    /// <remarks>
    /// Clears only the latch. The navigator is left where it is, so the caller decides what the
    /// rover does next — <c>stop</c> idles it, <c>resumeAutonomy</c> hands control back — and
    /// releasing a stop therefore never sets anything moving by itself. Raised on the transition
    /// only.
    /// </remarks>
    private void ReleaseEmergencyStop()
    {
        if (!IsEmergencyStopped)
        {
            return;
        }

        IsEmergencyStopped = false;

        Raise(
            "ground.emergencyStop.released",
            AssetEventSeverity.Info,
            "Emergency stop released; the drivetrain is armed and the rover is stationary.");
    }
}
