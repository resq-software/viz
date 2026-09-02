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

namespace ResQ.Viz.Web.Services.Assets.Surface;

// The command half of SurfaceAsset: translating a validated multi-domain command into guidance
// state, and refusing everything a vessel cannot or must not do. Split from the telemetry half so
// a change to what a vessel reports cannot silently alter what it accepts; the type's summary
// lives on the primary declaration in SurfaceAsset.cs.
public sealed partial class SurfaceAsset
{
    /// <summary>Distance a vessel stands off a berth when undocking, in hull lengths.</summary>
    /// <remarks>
    /// Four lengths: far enough to be clear of whatever the berth is attached to and to have room
    /// to turn, close enough that the manoeuvre is over in under a minute at the stand-off speed.
    /// </remarks>
    private const double UndockStandoffLengths = 4.0;

    /// <summary>Share of the hull's top speed used to leave a berth.</summary>
    /// <remarks>
    /// A limit on the departure leg and nothing more.
    /// <see cref="SurfaceNavigator.BeginUndocking"/> scopes it to that leg and hands the standing
    /// cruise setting back when the vessel reaches its stand-off position, so this fraction never
    /// becomes the speed the vessel makes on later passages that name no speed of their own.
    /// </remarks>
    private const double UndockSpeedFraction = 0.15;

    /// <inheritdoc />
    /// <remarks>
    /// <b>Defence in depth, in this order.</b> The v2 pipeline has already checked the issuer,
    /// the payload, the lease, the capability, the domain and the operational state before a
    /// command is translated — and every one of those checks is repeated or reinforced here,
    /// because the v1 compatibility adapter builds a <see cref="SimulatedAssetCommand"/> directly
    /// without passing the v2 gate, and because a check that only ever runs in one place is one
    /// refactor away from not running at all.
    /// <list type="number">
    ///   <item><description>
    ///     <b>Domain first, before capability.</b> Deliberately, and the ordering is load-bearing:
    ///     a vessel declares <see cref="AssetCapability.Reverse"/>, which is what a rover's
    ///     <c>reverse</c> gates on, so that command would <em>pass</em> the capability check and
    ///     only the domain gate refuses it. <c>takeoff</c>, <c>land</c>, <c>setAltitude</c>,
    ///     <c>loiter</c>, <c>driveTo</c>, <c>setSteering</c> and <c>park</c> are refused here even
    ///     when handed straight to this method.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Capability</b>, read from the catalog's own any-of/all-of rule rather than restated,
    ///     so this asset accepts exactly the set its capability report advertises — no more and
    ///     no less. <c>stationKeep</c> is refused here for a displacement hull, and the catalog
    ///     never offered it, which is the pair of facts that has to stay true together.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The emergency-stop latch</b>, which refuses everything except its own release.
    ///   </description></item>
    /// </list>
    /// Every rejection is side-effect free: nothing is written to the navigator until the command
    /// is known to be executable, so a refused <c>transitTo</c> leaves behind neither a target nor
    /// a cleared block.
    /// <para>
    /// <b>Nothing here can leave the vessel uncommandable.</b> Aground, drifting, saturated,
    /// blocked, out of position quality or latched by an emergency stop, every state this asset
    /// can reach still accepts <c>stop</c> — which the catalog permits in every operational
    /// state — and <c>stop</c> releases the latch. A rover that refused every command including
    /// the ones that recovered it is the defect this rule exists to keep off the water, where it
    /// would be worse: a bricked vessel does not stay where it was bricked.
    /// </para>
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

            // Stop is one of the two commands the catalog permits in every operational state,
            // which makes it the always-reachable release. Without that the latch would be a
            // trap: an emergency-stopped vessel publishes OperationalState.Emergency, which the
            // Operable policy excludes, so resumeAutonomy would be refused upstream and nothing
            // could bring a drifting hull back under command.
            case AssetCommandKind.Stop:
                ReleaseEmergencyStop();
                _navigator.Stop();
                return AssetCommandResult.Accepted;

            case AssetCommandKind.ResumeAutonomy:
                ReleaseEmergencyStop();
                _navigator.Resume();
                return AssetCommandResult.Accepted;

            // Hold is ungated on purpose and this asset agrees with the catalog about that. See
            // SurfaceNavigator.Hold for what a hull without a station-keeping capability actually
            // does to satisfy it, and why the published state must not call the result "holding"
            // without also saying the vessel is drifting.
            case AssetCommandKind.Hold:
                _navigator.Hold(_positionEus);
                return AssetCommandResult.Accepted;

            // goTo and transitTo are the same manoeuvre for a vessel: goTo is the domain-neutral
            // spelling and transitTo the surface one, and a hull navigating in two dimensions
            // executes both identically. Diverging them would mean an operator's choice of
            // vocabulary changed what the vessel did.
            case AssetCommandKind.GoTo:
            case AssetCommandKind.TransitTo:
                return ApplyTransitTo(in command);

            // Withdrawn from the catalog and refused here: a route needs a waypoint list that no
            // translated command carries. See ApplyFollowRoute for why running its first leg was
            // worse than refusing it.
            case AssetCommandKind.FollowRoute:
                return ApplyFollowRoute();

            case AssetCommandKind.ReturnToBase:
                return ApplyTransitTo(in command, _basePositionEus);

            case AssetCommandKind.SetSpeed:
                return ApplySetSpeed(in command);

            case AssetCommandKind.SetCourse:
                return ApplySetCourse(in command);

            case AssetCommandKind.StationKeep:
                return ApplyStationKeep(in command);

            case AssetCommandKind.Dock:
                return ApplyDock(in command);

            case AssetCommandKind.Undock:
                return ApplyUndock();

            default:
                return AssetCommandResult.Rejected("command.unsupported");
        }
    }

    /// <summary>Refuses a command that belongs to another domain, whatever the asset declares.</summary>
    /// <remarks>
    /// Never assume the catalog is the only gate. Its domain lists would already refuse each of
    /// these, but the v1 adapter does not consult the catalog, and a descriptor that wrongly
    /// declared an air or ground capability would sail through the capability check. This is the
    /// gate that still fires.
    /// </remarks>
    /// <param name="kind">Translated command kind.</param>
    /// <returns>A machine-readable rejection token, or null when the kind belongs to this domain.</returns>
    private static string? RejectByDomain(AssetCommandKind kind) => kind switch
    {
        AssetCommandKind.Takeoff or AssetCommandKind.Land or AssetCommandKind.SetAltitude
            or AssetCommandKind.Loiter => "command.domain.air",

        AssetCommandKind.DriveTo or AssetCommandKind.SetSteering or AssetCommandKind.Reverse
            or AssetCommandKind.Park => "command.domain.ground",

        _ => null,
    };

    /// <summary>Whether a command is one of the three that may reach a latched vessel.</summary>
    /// <remarks>
    /// A repeated emergency stop is included so re-issuing one is never refused. Refusing to stop
    /// something because it is already stopping is exactly backwards, and it is the same
    /// reasoning that makes the stop commands ungated in the catalog.
    /// </remarks>
    /// <param name="kind">Translated command kind.</param>
    /// <returns><see langword="true"/> when the command may execute while the latch is set.</returns>
    private static bool IsEmergencyRelease(AssetCommandKind kind) =>
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

    /// <summary>Sends the vessel to the command's position, if it is one it may reach.</summary>
    /// <param name="command">Command carrying the target and an optional cruise speed.</param>
    /// <returns>Acceptance, or a rejection naming why the passage was refused.</returns>
    private AssetCommandResult ApplyTransitTo(in SimulatedAssetCommand command)
    {
        if (ResolveTarget(command.Target, out var target) is { } rejection)
        {
            return AssetCommandResult.Rejected(rejection);
        }

        return ApplyTransitTo(in command, target);
    }

    /// <summary>Sends the vessel to an already-resolved position, if it is one it may reach.</summary>
    /// <remarks>
    /// The base position goes through exactly the same check as an operator's target. A launch
    /// point is not permanently reachable: a terrain-preset change moves the water level as well
    /// as the bed, and a <c>returnToBase</c> that skipped the check would dispatch a vessel
    /// towards a beach that used to be a bay.
    /// </remarks>
    /// <param name="command">Command carrying an optional cruise speed.</param>
    /// <param name="targetEus">Destination in the scene frame.</param>
    /// <returns>Acceptance, or a rejection naming why the passage was refused.</returns>
    private AssetCommandResult ApplyTransitTo(in SimulatedAssetCommand command, Vector3 targetEus)
    {
        if (RejectUnnavigable(targetEus) is { } blocked)
        {
            return AssetCommandResult.Rejected(blocked);
        }

        _navigator.TransitTo(targetEus, command.SpeedMps);
        return AssetCommandResult.Accepted;
    }

    /// <summary>Refuses a route, because nothing in this build can carry one this far.</summary>
    /// <remarks>
    /// <b>A route with one leg is not a route, and one leg is all that could ever reach this
    /// method.</b> <see cref="SimulatedAssetCommand"/> carries a single <see cref="FramedPose"/>
    /// and no waypoint list, and the identifier a <c>CommandTargetKinds.Route</c> target names
    /// has no store to be resolved against. This method used to run that single leg as a transit,
    /// which reported a route as executed after sailing one waypoint of it — the same defect as
    /// an air asset accepting "land at this point" and landing where it stood, and worse than a
    /// refusal because nothing anywhere said the rest of the route was discarded.
    /// <para>
    /// So the refusal is <c>command.route.unavailable</c>: a fact about the <em>build</em> rather
    /// than about the payload, which is what the token's suffix tells a caller. The catalog no
    /// longer registers <c>followRoute</c> at all — see <see cref="CommandCatalog"/> — so this
    /// arm is now only reachable by a caller that bypassed it, and the capability re-check in
    /// <see cref="Apply"/> refuses an unregistered kind before it even gets here. It stays as the
    /// backstop and as the marker for where a real route executor lands: give the translated
    /// command a resolved waypoint list and this becomes a loop over <c>ApplyTransitTo</c>,
    /// sequenced by the navigator's own arrival test.
    /// </para>
    /// </remarks>
    /// <returns>A rejection naming the missing capability of this build.</returns>
    private static AssetCommandResult ApplyFollowRoute() =>
        AssetCommandResult.Rejected("command.route.unavailable");

    /// <summary>Changes the speed without changing the destination.</summary>
    /// <remarks>
    /// A value above the hull's ceiling is clamped rather than refused, because that ceiling is a
    /// physical fact and "as fast as you can" is the honest reading of the request. A negative
    /// one <em>is</em> refused: going astern is a manoeuvre the docking and station-keeping laws
    /// command as part of their own control, not a direction an operator selects by the sign of a
    /// speed, and letting one field carry two meanings is how a vessel ends up backing out of a
    /// berth because somebody typed a minus.
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

    /// <summary>Steers a commanded course over ground.</summary>
    /// <remarks>
    /// The course arrives on <see cref="SimulatedAssetCommand.HeadingRad"/>, which is documented
    /// as a heading <em>or course</em> clockwise from true north — and for this command it is
    /// unambiguously the course, because <see cref="SurfaceNavigator.SetCourse"/> closes the
    /// error against the track the vessel is making good rather than against its bow. A command
    /// that arrives without one is refused for the missing parameter, not for being unsupported:
    /// supply a course and the same command lands.
    /// </remarks>
    /// <param name="command">Command carrying the course and an optional speed.</param>
    /// <returns>Acceptance, or <c>command.course.missing</c>.</returns>
    private AssetCommandResult ApplySetCourse(in SimulatedAssetCommand command)
    {
        if (command.HeadingRad is not { } course || !double.IsFinite(course))
        {
            return AssetCommandResult.Rejected("command.course.missing");
        }

        _navigator.SetCourse(course, command.SpeedMps);
        return AssetCommandResult.Accepted;
    }

    /// <summary>Holds a position actively, when the propulsion arrangement can.</summary>
    /// <remarks>
    /// Refused outright by a hull that cannot hold a station, with
    /// <see cref="StationKeeping.UnsupportedReason"/>. That refusal is the point rather than a
    /// limitation: <see cref="AssetProfiles.CapabilitiesFor"/> withholds
    /// <see cref="AssetCapability.StationKeep"/> from a single-screw displacement hull, so the
    /// command is never advertised to one and the capability gate in <see cref="Apply"/> has
    /// already refused it before this method is reached. This is the second gate that still fires
    /// if a descriptor is ever built declaring a capability the hull does not have — a vessel
    /// that accepted "wait here" and then drifted away from it would be worse than one that said
    /// no.
    /// <para>
    /// The target is optional: without one the vessel holds where it is, which is what
    /// <c>stationKeep</c> with no payload means. The station is vetted for navigability like any
    /// other destination, because a hold on a point the hull cannot float at is not a hold.
    /// </para>
    /// </remarks>
    /// <param name="command">Command carrying an optional station and an optional heading to hold.</param>
    /// <returns>Acceptance, or a rejection naming why the hold was refused.</returns>
    private AssetCommandResult ApplyStationKeep(in SimulatedAssetCommand command)
    {
        if (!StationKeeping.IsSupportedBy(_profile))
        {
            return AssetCommandResult.Rejected(StationKeeping.UnsupportedReason);
        }

        var station = _positionEus;

        if (command.Target is not null)
        {
            if (ResolveTarget(command.Target, out station) is { } rejection)
            {
                return AssetCommandResult.Rejected(rejection);
            }

            if (RejectUnnavigable(station) is { } blocked)
            {
                return AssetCommandResult.Rejected(blocked);
            }
        }

        // A commanded heading turns the hold into a fixed-heading one, because an operator who
        // states a heading is asking for the bow to point somewhere — at a sensor bearing, at a
        // casualty — rather than into whatever the weather happens to be doing.
        var policy = command.HeadingRad is { } heading && double.IsFinite(heading)
            ? StationKeepHeadingPolicy.FixedHeading
            : StationKeepHeadingPolicy.MinimumPower;

        _navigator.EngageStationKeep(StationKeepGoal.For(
            _profile,
            station,
            headingPolicy: policy,
            fixedHeadingRad: command.HeadingRad));

        return AssetCommandResult.Accepted;
    }

    /// <summary>Begins a structured berthing approach on the command's target.</summary>
    /// <remarks>
    /// The plan is built from the hull's own dimensions at the moment the command is accepted —
    /// see <see cref="DockingPlan.For"/> — so the corridor, the staged limits and the time budget
    /// all scale with the vessel rather than being fixed metres. A commanded heading becomes the
    /// terminal heading; without one the vessel arrives bow-on along the bearing it approached
    /// down.
    /// </remarks>
    /// <param name="command">Command carrying the berth and an optional terminal heading.</param>
    /// <returns>Acceptance, or a rejection naming why the approach was refused.</returns>
    private AssetCommandResult ApplyDock(in SimulatedAssetCommand command)
    {
        if (!Docking.IsSupportedBy(_profile))
        {
            return AssetCommandResult.Rejected(Docking.UnsupportedReason);
        }

        if (ResolveTarget(command.Target, out var berth) is { } rejection)
        {
            return AssetCommandResult.Rejected(rejection);
        }

        if (RejectUnnavigable(berth) is { } blocked)
        {
            return AssetCommandResult.Rejected(blocked);
        }

        _navigator.BeginDocking(DockingPlan.For(_profile, _positionEus, berth, command.HeadingRad));
        return AssetCommandResult.Accepted;
    }

    /// <summary>Releases from a berth and stands off.</summary>
    /// <remarks>
    /// Refused when the vessel is not secured anywhere, with <see cref="Docking.NotDockedReason"/>
    /// — a fact about this moment rather than about the build, so docking the vessel makes the
    /// same command land.
    /// <para>
    /// The stand-off point is <see cref="UndockStandoffLengths"/> hull lengths back along the
    /// reciprocal of the current heading, which the vessel then transits to under its ordinary
    /// law — so it turns in its own length and leaves under its own bow rather than backing out.
    /// That is a simplification of a real departure and is stated as one. If the stand-off point
    /// is not navigable the command is refused rather than executed into a bank; the vessel is
    /// not stranded by that, because an ordinary <c>transitTo</c> to anywhere else is still
    /// accepted.
    /// </para>
    /// <para>
    /// The departure speed is a limit on this one leg — see <see cref="UndockSpeedFraction"/> —
    /// and the vessel goes back to whatever cruise speed it had before it berthed as soon as it
    /// is clear.
    /// </para>
    /// </remarks>
    /// <returns>Acceptance, or a rejection naming why the departure was refused.</returns>
    private AssetCommandResult ApplyUndock()
    {
        if (!Docking.IsSupportedBy(_profile))
        {
            return AssetCommandResult.Rejected(Docking.UnsupportedReason);
        }

        if (!_navigator.IsDocked)
        {
            return AssetCommandResult.Rejected(Docking.NotDockedReason);
        }

        var astern = CoordinateFrames.BearingToEusVector(
            CoordinateFrames.NormalizeAngle(_motion.HeadingRad + Math.PI),
            UndockStandoffLengths * _profile.LengthM);

        var standoff = new Vector3(
            _positionEus.X + astern.X, _positionEus.Y, _positionEus.Z + astern.Z);

        if (RejectUnnavigable(standoff) is { } blocked)
        {
            return AssetCommandResult.Rejected(blocked);
        }

        _navigator.BeginUndocking(standoff, _profile.MaxSpeedMps * UndockSpeedFraction);
        return AssetCommandResult.Accepted;
    }

    /// <summary>Refuses a destination this hull cannot reach, naming why.</summary>
    /// <remarks>
    /// <b>The whole straight line is swept, not just the destination</b>, which is where this
    /// departs from the ground domain deliberately. A rover refused only impassable destinations
    /// because a planner could in principle route it round a wall; a vessel has no planner here
    /// and cannot cross a headland, so a destination check alone would accept passages that stop
    /// halfway. <see cref="WaterConstraints.CheckRoute"/> exists for exactly this question, takes
    /// a sample count fixed by geometry rather than by what it finds, and reports the first
    /// blocking sample in the same vocabulary the mask uses everywhere else.
    /// <para>
    /// <b>A vessel that is already aground is exempt from the sweep.</b> Every route off a beach
    /// starts on the beach, so sweeping one would refuse precisely the commands that recover the
    /// hull — the trap that once left a bogged rover with nothing it would accept, and which is
    /// worse afloat because a stranded vessel does not stay stranded, it lifts and goes
    /// somewhere. Only the destination is vetted in that case;
    /// <see cref="WaterConstraints.ResolveMotion"/> then permits movement only towards deeper
    /// water, so the exemption cannot be used to drive further up the beach.
    /// </para>
    /// <para>
    /// Read-only: it samples and evaluates, and touches no asset state, so a refusal leaves the
    /// vessel exactly as it was. Every sample is taken here, inside a call the room makes under
    /// its own lock.
    /// </para>
    /// <para>
    /// <b>Advisory.</b> A navigable verdict means nothing sampled contradicts this hull's
    /// envelope over a procedural bed; it is not an assurance that the passage is safe and makes
    /// no claim about any navigation regulation.
    /// </para>
    /// </remarks>
    /// <param name="targetEus">Destination in the scene frame; the vertical component is ignored.</param>
    /// <returns>A machine-readable rejection token, or null when the destination is usable.</returns>
    private string? RejectUnnavigable(Vector3 targetEus)
    {
        if (!_water.IsNavigable)
        {
            var destination = EvaluateAt(targetEus);
            return destination.IsNavigable ? null : destination.ReasonCode;
        }

        var check = WaterConstraints.CheckRoute(_waterProfile, _positionEus, targetEus, _environment);
        return check.IsNavigable ? null : check.BlockingReasonCode;
    }

    /// <summary>Latches the emergency stop and raises the transition event.</summary>
    /// <remarks>
    /// <b>An all-stop does not stop a displacement hull.</b> It stops the propeller. The vessel
    /// then carries its way off over a surge time constant and moves with the current and the
    /// wind for as long as nobody intervenes — so the raised event says so in words, the mode
    /// token says <c>emergency-stop</c> rather than "stopped", and the published speed over
    /// ground goes on reporting whatever the vessel is actually doing. An operator who reads
    /// "stopped" and watches the hull go two hundred metres downstream has been lied to, and this
    /// is the one place that lie would be told.
    /// <para>
    /// A hull that can hold a position takes the other branch and pins the spot it stopped at;
    /// which of the two happens is <see cref="SurfaceSafetyPolicy"/>'s decision and not this
    /// method's. Raised on the transition only, so re-issuing an emergency stop is accepted
    /// without adding a second event.
    /// </para>
    /// </remarks>
    private void EngageEmergencyStop()
    {
        bool wasEngaged = _navigator.Mode == SurfaceGuidanceMode.EmergencyStopped;

        _navigator.EmergencyStop(_positionEus);

        if (Safety.InhibitPropulsionOnEmergencyStop)
        {
            IsEmergencyStopped = true;
        }

        if (wasEngaged)
        {
            return;
        }

        string behaviour = Safety.EmergencyStop == SurfaceEmergencyStopBehaviour.HoldStation
            ? "holding the position it was issued at"
            : "propeller stopped — the vessel will carry its way off and then drift with the "
                + "current and the wind, because a displacement hull has no way of stopping";

        string arming = Safety.InhibitPropulsionOnEmergencyStop
            ? " Propulsion commands are refused until the stop is released; 'stop' releases it and "
                + "is accepted in every state."
            : " Propulsion remains commandable.";

        Raise(
            "surface.emergencyStop",
            AssetEventSeverity.Alert,
            $"Emergency stop engaged: {behaviour}.{arming}");
    }

    /// <summary>Clears the emergency-stop latch and raises the transition event.</summary>
    /// <remarks>
    /// Clears only the latch. The navigator is left where it is, so the caller decides what the
    /// vessel does next — <c>stop</c> idles it, <c>resumeAutonomy</c> hands control back — and
    /// releasing a stop therefore never sets anything moving by itself. It also never makes the
    /// vessel stationary: it was drifting before the release and it is drifting after it, which
    /// the event says rather than leaving to be inferred. Raised on the transition only.
    /// </remarks>
    private void ReleaseEmergencyStop()
    {
        if (!IsEmergencyStopped)
        {
            return;
        }

        IsEmergencyStopped = false;

        Raise(
            "surface.emergencyStop.released",
            AssetEventSeverity.Info,
            "Emergency stop released; propulsion is commandable again. The vessel is not "
            + "stationary — it continues to move with the current and the wind until it is given "
            + "something to do.");
    }
}
