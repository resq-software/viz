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

                    _drone.SendCommand(FlightCommand.GoTo(target, command.SpeedMps, yaw));
                    return AssetCommandResult.Accepted;
                }

            case AssetCommandKind.SetAltitude:
                {
                    if (ResolveAltitude(in command, out var altitude) is { } rejection)
                    {
                        return AssetCommandResult.Rejected(rejection);
                    }

                    _drone.SendCommand(FlightCommand.GoTo(
                        new Vector3(position.X, (float)altitude, position.Z), command.SpeedMps, yaw));
                    return AssetCommandResult.Accepted;
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

                    _drone.SendCommand(FlightCommand.GoTo(
                        new Vector3(position.X, (float)climb, position.Z), command.SpeedMps, yaw));
                    return AssetCommandResult.Accepted;
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

                _drone.SendCommand(FlightCommand.Land());
                return AssetCommandResult.Accepted;

            // Loiter's target IS honoured: with one, the airframe flies to the point and holds
            // over it; without one it holds where it is. There is no orbit primitive in this
            // model, so the pattern is a hold rather than a circle — which is why the catalog
            // asks for no radius.
            case AssetCommandKind.Loiter:
                {
                    if (command.Target is null)
                    {
                        _drone.SendCommand(FlightCommand.Hover(yaw));
                        return AssetCommandResult.Accepted;
                    }

                    if (ResolveTarget(command.Target, out var centre) is { } rejection)
                    {
                        return AssetCommandResult.Rejected(rejection);
                    }

                    _drone.SendCommand(FlightCommand.GoTo(centre, command.SpeedMps, yaw));
                    return AssetCommandResult.Accepted;
                }

            case AssetCommandKind.ReturnToBase:
                _drone.SendCommand(FlightCommand.RTL());
                return AssetCommandResult.Accepted;

            // A multirotor stops by holding position: there is no other way for it to remain
            // aloft, which is exactly the asymmetry the capability model exists to express.
            case AssetCommandKind.Stop:
            case AssetCommandKind.EmergencyStop:
            case AssetCommandKind.Hold:
            case AssetCommandKind.StationKeep:
                _drone.SendCommand(FlightCommand.Hover(yaw));
                return AssetCommandResult.Accepted;

            // Whether a drone follows the swarm coordinator or an operator is room state, not
            // asset state, so there is nothing here to undo. Accepted rather than rejected so
            // the room's own reattachment is not refused out from under it.
            case AssetCommandKind.ResumeAutonomy:
                return AssetCommandResult.Accepted;

            default:
                return AssetCommandResult.Rejected("command.unsupported");
        }
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
