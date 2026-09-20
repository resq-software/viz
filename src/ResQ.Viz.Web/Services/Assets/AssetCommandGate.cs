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

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>
/// The checks every asset runs before it looks at what a command asks for, and the argument
/// handling every asset does the same way afterwards.
/// </summary>
/// <remarks>
/// The screening sequence is a <b>safety</b> sequence and its order is load-bearing: the asset
/// must be the addressee before its domain is consulted, the domain before its capabilities, and
/// the emergency latch last so a release still gets through. That argument was written out at
/// length in two files and implemented in three, which meant adding a fifth check, or reordering
/// the four, was a multi-file change no compiler and no test would notice if only one was made.
/// The copies would then disagree about which commands reach a latched asset — precisely the
/// defect the ordering exists to prevent.
/// <para>
/// Static, because none of this is state. Each domain keeps what is genuinely its own: the
/// switch over command kinds, and the table saying which kinds belong to another domain.
/// </para>
/// </remarks>
public static class AssetCommandGate
{
    /// <summary>Screens a command, returning a rejection token or null to let it through.</summary>
    /// <remarks>
    /// Ordered deliberately, and the order is the whole point:
    /// <list type="number">
    ///   <item><description>
    ///     Addressee. A command for another asset is not this one's to judge, so nothing else is
    ///     consulted — its capabilities and its emergency latch are irrelevant to somebody
    ///     else's command.
    ///   </description></item>
    ///   <item><description>
    ///     Domain. Defence in depth: the catalog's domain lists would already refuse these, but
    ///     the v1 adapter does not consult the catalog, and a descriptor that wrongly declared
    ///     another domain's capability would sail through the capability check below.
    ///   </description></item>
    ///   <item><description>
    ///     Capability, read off the descriptor the asset publishes, so what it accepts and what
    ///     it advertises cannot diverge.
    ///   </description></item>
    ///   <item><description>
    ///     The emergency latch, last, and with a release exempted. A stop that cannot be
    ///     released is a bricked asset.
    ///   </description></item>
    /// </list>
    /// </remarks>
    /// <param name="command">Translated command to screen.</param>
    /// <param name="assetId">Identifier of the asset doing the screening.</param>
    /// <param name="capabilities">What that asset publishes it can do.</param>
    /// <param name="isEmergencyStopped">Whether its emergency stop is latched.</param>
    /// <param name="rejectByDomain">The asset's own table of kinds belonging to another domain.</param>
    /// <returns>A machine-readable rejection token, or null when the command may proceed.</returns>
    public static string? Screen(
        in SimulatedAssetCommand command,
        string assetId,
        AssetCapability capabilities,
        bool isEmergencyStopped,
        Func<AssetCommandKind, string?> rejectByDomain)
    {
        ArgumentNullException.ThrowIfNull(rejectByDomain);

        if (!string.Equals(command.AssetId, assetId, StringComparison.Ordinal))
        {
            return "command.assetMismatch";
        }

        if (rejectByDomain(command.Kind) is { } wrongDomain)
        {
            return wrongDomain;
        }

        if (!command.IsSatisfiedBy(capabilities))
        {
            return "capability.missing";
        }

        return isEmergencyStopped && !IsEmergencyRelease(command.Kind)
            ? "asset.emergencyStopped"
            : null;
    }

    /// <summary>Whether a command is one of the three that may reach a latched asset.</summary>
    /// <remarks>
    /// A repeated emergency stop is included so re-issuing one is never refused. Refusing to stop
    /// something because it is already stopping is exactly backwards, and it is the same reasoning
    /// that makes the stop commands ungated in the catalog.
    /// </remarks>
    /// <param name="kind">Translated command kind.</param>
    /// <returns><see langword="true"/> when the command may execute while the latch is set.</returns>
    public static bool IsEmergencyRelease(AssetCommandKind kind) =>
        kind is AssetCommandKind.Stop or AssetCommandKind.ResumeAutonomy
            or AssetCommandKind.EmergencyStop;

    /// <summary>Resolves a command target into a scene-frame position.</summary>
    /// <remarks>
    /// Only the scene frame is accepted. Converting from NED or ENU needs a shared origin, and
    /// guessing one is how a waypoint ends up mirrored about the chart; that conversion belongs
    /// in the translation layer, where the origin is known.
    /// </remarks>
    /// <param name="pose">Target pose from the command, possibly null.</param>
    /// <param name="target">Resolved scene-frame position when the return value is null.</param>
    /// <returns>A machine-readable rejection token, or null when the target is usable.</returns>
    public static string? ResolveTarget(FramedPose? pose, out Vector3 target)
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

    /// <summary>Validates a commanded cruise speed.</summary>
    /// <remarks>
    /// Zero is refused rather than treated as a stop. A speed setpoint is a standing value that
    /// governs every leg after it, so accepting zero would leave a vehicle that answers every
    /// later waypoint by not moving, with nothing in its state explaining why. Stopping is a
    /// command of its own.
    /// </remarks>
    /// <param name="command">Translated command carrying the speed.</param>
    /// <param name="speedMps">The validated speed when the return value is null.</param>
    /// <returns>A machine-readable rejection token, or null when the speed is usable.</returns>
    public static string? ValidateSpeed(in SimulatedAssetCommand command, out double speedMps)
    {
        speedMps = 0.0;

        if (command.SpeedMps is not { } speed || !double.IsFinite(speed))
        {
            return "command.speed.missing";
        }

        if (speed <= 0.0)
        {
            return "command.speed.outOfRange";
        }

        speedMps = speed;
        return null;
    }
}
