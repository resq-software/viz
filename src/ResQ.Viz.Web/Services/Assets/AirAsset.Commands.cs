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
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

// The command half of AirAsset: translating a validated multi-domain command into the SDK's
// flight-command vocabulary, and resolving its target into the scene frame. Split from the
// telemetry half so a change to what a drone reports cannot silently alter what it accepts;
// the type's summary lives on the primary declaration in AirAsset.cs.
public sealed partial class AirAsset
{
    // The waypoint this asset last commanded, and the yaw it was commanded with. Mirrored here
    // because IFlightModel publishes no setpoint to read back, and a standing cruise speed with
    // no waypoint to attach it to would be an acceptance with nowhere to land. Null whenever the
    // airframe is doing something that is not tracking a waypoint — hovering, landing, or
    // returning to launch — so a cruise change is never re-issued as a waypoint the operator
    // cancelled. Command state, which is why it lives in the command half of the type.
    private Vector3? _activeWaypointEus;
    private double? _activeWaypointYaw;

    /// <summary>Standing cruise speed from the last <c>setSpeed</c>, in metres per second.</summary>
    /// <remarks>Null until one is commanded, which is what keeps the flight model's own default in force.</remarks>
    private double? _cruiseSpeedMps;

    /// <inheritdoc />
    /// <remarks>
    /// Translates into the SDK's small flight-command vocabulary. Anything that vocabulary
    /// cannot express, and anything belonging to another domain, is rejected before the drone is
    /// touched, so a rejection never leaves a half-applied setpoint behind.
    /// </remarks>
    public AssetCommandResult Apply(in SimulatedAssetCommand command)
    {
        if (!string.Equals(command.AssetId, AssetId, StringComparison.Ordinal))
        {
            return AssetCommandResult.Rejected("command.assetMismatch");
        }

        // The catalog's own rule rather than a restatement of it: an asset must accept exactly
        // the set its capability report advertises, and a second hand-written table drifts from
        // the first the moment either is edited alone.
        if (!command.IsSatisfiedBy(Descriptor.Capabilities))
        {
            return AssetCommandResult.Rejected("capability.missing");
        }

        var position = _drone.FlightModel.State.Position;

        // The SDK's yaw is a scene yaw about +Y with zero facing +Z, not a compass heading.
        // Converting through the tested helper is what keeps a "fly north" command from
        // pointing the airframe south.
        double? yaw = command.HeadingRad is { } h
            ? CoordinateFrames.SceneYawFromHeading(h)
            : null;

        switch (command.Kind)
        {
            case AssetCommandKind.GoTo:
                {
                    if (ResolveTarget(command.Target, out var target) is { } rejection)
                    {
                        return AssetCommandResult.Rejected(rejection);
                    }

                    return TrackWaypoint(target, EffectiveSpeed(in command), yaw);
                }

            case AssetCommandKind.SetAltitude:
                {
                    if (ResolveAltitude(in command, out var altitude) is { } rejection)
                    {
                        return AssetCommandResult.Rejected(rejection);
                    }

                    return TrackWaypoint(
                        new Vector3(position.X, (float)altitude, position.Z),
                        EffectiveSpeed(in command),
                        yaw);
                }

            case AssetCommandKind.Takeoff:
                {
                    // The vendored kinematic flight model re-arms on any command other than Land, so
                    // a takeoff after a landing does resume stepping. The quadrotor model latches its
                    // landed flag instead, where a takeoff is accepted but has no effect.
                    double climb = position.Y + DefaultTakeoffClimbM;

                    // A takeoff altitude is optional, but one that is supplied is held to exactly
                    // the same datum and range rules as setAltitude's: an unbounded climb target
                    // poisons the position through the same cast, whichever command carried it.
                    if (command.AltitudeM is not null)
                    {
                        if (ResolveAltitude(in command, out var requested) is { } rejection)
                        {
                            return AssetCommandResult.Rejected(rejection);
                        }

                        climb = requested;
                    }

                    return TrackWaypoint(
                        new Vector3(position.X, (float)climb, position.Z),
                        EffectiveSpeed(in command),
                        yaw);
                }

            // Land takes no target: this flight model has one setpoint and cannot sequence "fly
            // there, then descend", and only Land() latches the landed flag. Refusing a target is
            // the honest answer — accepting one and landing in place reports success for a
            // command that was not carried out. The catalog advertises no target for land, so a
            // target here means a caller that bypassed it.
            case AssetCommandKind.Land:
                if (command.Target is not null)
                {
                    return AssetCommandResult.Rejected("command.target.unsupported");
                }

                return Untracked(FlightCommand.Land());

            // Loiter's target IS honoured: with one, the airframe flies to the point and holds
            // over it; without one it holds where it is. There is no orbit primitive in this
            // model, so the pattern is a hold rather than a circle — which is why the catalog
            // asks for no radius.
            case AssetCommandKind.Loiter:
                {
                    if (command.Target is null)
                    {
                        return Untracked(FlightCommand.Hover(yaw));
                    }

                    if (ResolveTarget(command.Target, out var centre) is { } rejection)
                    {
                        return AssetCommandResult.Rejected(rejection);
                    }

                    return TrackWaypoint(centre, EffectiveSpeed(in command), yaw);
                }

            case AssetCommandKind.ReturnToBase:
                return Untracked(FlightCommand.RTL());

            // A multirotor stops by holding position: there is no other way for it to remain
            // aloft, which is exactly the asymmetry the capability model exists to express.
            case AssetCommandKind.Stop:
            case AssetCommandKind.EmergencyStop:
            case AssetCommandKind.Hold:
            case AssetCommandKind.StationKeep:
                return Untracked(FlightCommand.Hover(yaw));

            // A cruise speed is a standing setpoint rather than a manoeuvre, so it governs the
            // waypoint being flown now and every waypoint issued after it — the same reading the
            // ground and surface navigators give their own cruise settings.
            case AssetCommandKind.SetSpeed:
                return ApplySetSpeed(in command);

            // Whether a drone follows the swarm coordinator or an operator is room state, not
            // asset state, so there is nothing here to undo. Accepted rather than rejected so
            // the room's own reattachment is not refused out from under it.
            case AssetCommandKind.ResumeAutonomy:
                return AssetCommandResult.Accepted;

            default:
                return AssetCommandResult.Rejected("command.unsupported");
        }
    }

    /// <summary>Changes the cruise speed without changing the destination.</summary>
    /// <remarks>
    /// The air domain's counterpart of the cruise setting the ground and surface navigators
    /// already keep. It exists because the catalog advertises <c>setSpeed</c> to every mobile
    /// domain and air was the one that fell through to <c>command.unsupported</c>: the capability
    /// report drew a speed control on every drone, and that control could never work.
    /// <para>
    /// The SDK's flight vocabulary carries a speed only as a field of a waypoint command, and the
    /// flight model publishes no setpoint to read back, so this asset mirrors the waypoint it last
    /// issued. With one being tracked the waypoint is re-issued immediately at the new speed, so
    /// the command takes effect now rather than at the next retask; with none — hovering, landing,
    /// returning to launch — the setting is recorded and the next waypoint is flown at it. The
    /// re-issue is not deferred because an acceptance that changes nothing an operator can observe
    /// is worse than a refusal: nothing anywhere would say the speed had not been taken.
    /// </para>
    /// <para>
    /// A value above the airframe's declared ceiling is clamped rather than refused, because that
    /// ceiling is a physical fact and "as fast as you can" is the honest reading of the request.
    /// A non-positive one <em>is</em> refused: a multirotor takes its direction from where its
    /// waypoint is, so a negative speed would make one field carry two meanings. Both refusals
    /// describe the payload rather than the build, so supplying a usable speed makes the same
    /// command land.
    /// </para>
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

        double ceiling = Descriptor.Motion.MaxSpeedMps;
        _cruiseSpeedMps = ceiling > 0.0 ? Math.Min(speed, ceiling) : speed;

        if (_activeWaypointEus is { } waypoint)
        {
            _drone.SendCommand(FlightCommand.GoTo(waypoint, _cruiseSpeedMps, _activeWaypointYaw));
        }

        return AssetCommandResult.Accepted;
    }

    /// <summary>The speed a waypoint command should be flown at.</summary>
    /// <remarks>
    /// A speed carried by the command itself wins; otherwise the standing cruise setting applies;
    /// with neither, null lets the flight model use its own default. That order is what makes
    /// "set the cruise speed, then send waypoints" behave in the air exactly as it does on the
    /// ground and on the water, instead of silently ignoring the setting on every retask.
    /// </remarks>
    /// <param name="command">Command being executed.</param>
    /// <returns>The speed in metres per second, or null to leave the choice to the flight model.</returns>
    private double? EffectiveSpeed(in SimulatedAssetCommand command) =>
        command.SpeedMps ?? _cruiseSpeedMps;

    /// <summary>Flies to a waypoint and remembers it as the setpoint in force.</summary>
    /// <remarks>
    /// Every waypoint this asset commands goes through here, so the mirror cannot fall out of step
    /// with the flight model by someone adding a command arm and forgetting to record it.
    /// </remarks>
    /// <param name="waypointEus">Scene-frame destination.</param>
    /// <param name="speedMps">Speed to fly it at, or null for the flight model's default.</param>
    /// <param name="yaw">Commanded scene yaw, or null to face the direction of travel.</param>
    /// <returns>Acceptance; a waypoint that reaches this method has already been resolved.</returns>
    private AssetCommandResult TrackWaypoint(Vector3 waypointEus, double? speedMps, double? yaw)
    {
        _activeWaypointEus = waypointEus;
        _activeWaypointYaw = yaw;
        _drone.SendCommand(FlightCommand.GoTo(waypointEus, speedMps, yaw));
        return AssetCommandResult.Accepted;
    }

    /// <summary>Issues a command that is not a waypoint, and forgets the one that was.</summary>
    /// <remarks>
    /// Hovering, landing and returning to launch each replace the waypoint the flight model was
    /// tracking, so keeping the mirror would let a later <c>setSpeed</c> re-issue a destination
    /// the operator had already cancelled — an asset flying somewhere nobody asked for, from a
    /// command that only named a number.
    /// </remarks>
    /// <param name="command">Flight command to issue.</param>
    /// <returns>Acceptance.</returns>
    private AssetCommandResult Untracked(FlightCommand command)
    {
        _activeWaypointEus = null;
        _activeWaypointYaw = null;
        _drone.SendCommand(command);
        return AssetCommandResult.Accepted;
    }

    /// <summary>Resolves a commanded altitude onto the scene's vertical axis.</summary>
    /// <remarks>
    /// A last line of defence, not the conversion itself. Converting an above-ground altitude
    /// needs the terrain elevation under the asset, which the API boundary samples and applies
    /// before the command is built; an air asset holds no environment sampler of its own, and
    /// giving it one so it could re-derive the same number would put the datum arithmetic in two
    /// places. What is checked here is that the boundary was actually crossed — the datum is the
    /// scene's own — and that the value is inside the scene's vertical envelope. Without the
    /// second check <c>1e300</c> survives as a finite <see cref="double"/>, becomes
    /// <c>+Infinity</c> on the cast to <see cref="float"/>, and the drone's position goes
    /// <c>NaN</c>, which takes the room's whole frame broadcast down with it.
    /// </remarks>
    /// <param name="command">Command carrying the altitude and the datum it was quoted against.</param>
    /// <param name="sceneAltitudeM">The scene-frame <c>Y</c> to fly to, when the return value is null.</param>
    /// <returns>A machine-readable rejection token, or null when the altitude is usable.</returns>
    private static string? ResolveAltitude(in SimulatedAssetCommand command, out double sceneAltitudeM)
    {
        sceneAltitudeM = 0.0;

        if (command.AltitudeM is not { } altitude || !double.IsFinite(altitude))
        {
            return "command.altitude.missing";
        }

        if (command.AltitudeReference != VerticalReference.MeanSeaLevel)
        {
            return "command.altitude.reference";
        }

        if (altitude < CommandCatalog.MinCommandedAltitudeM
            || altitude > CommandCatalog.MaxCommandedAltitudeM)
        {
            return "command.altitude.outOfRange";
        }

        sceneAltitudeM = altitude;
        return null;
    }

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
            // The validator always supplies a token on failure; the coalesce keeps the
            // nullable analysis honest without suppressing it.
            return error ?? "command.target.invalid";
        }

        if (pose is not { Frame: CoordinateFrame.LocalEus })
        {
            return "command.target.frame";
        }

        target = pose.Position;
        return null;
    }
}
