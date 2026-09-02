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

// The projection half of SurfaceAsset: turning integrated state into the records the wire
// carries. Split from the physics half so a change to what a vessel reports cannot silently
// alter how it is driven, and from the event half because a capture must be repeatable within a
// tick and raise nothing; the type's summary lives on the primary declaration in SurfaceAsset.cs.
public sealed partial class SurfaceAsset
{
    /// <inheritdoc />
    /// <remarks>
    /// A projection of state <see cref="Step"/> already computed, and deliberately not a second
    /// look at the world: the stored sample and water classification describe the same instant as
    /// the pose they travel beside, whereas re-sampling here would publish a depth from one
    /// instant against a position from another. Nothing is mutated and no event is raised, so
    /// calling this twice within a tick yields the same state both times.
    /// </remarks>
    public AssetState Capture(in AssetCaptureContext context)
    {
        var pose = new FramedPose(
            Frame: CoordinateFrame.LocalEus,
            OriginId: context.Origin?.OriginId,
            Position: _positionEus,
            Orientation: HullOrientation(),
            Covariance: null,
            Geo: context.Origin is { } origin
                ? CoordinateFrames.LocalEusToGeo(_positionEus, origin)
                : null);

        // Linear twist is the GROUND velocity — the realised per-tick position delta, the same
        // vector speed and course over ground are published from. Anything that differentiates
        // the published position between frames therefore gets back a vector that matches those
        // positions, which the analytic velocity would not while the water mask is holding the
        // hull at a shoreline.
        //
        // The vertical component is zero even though the hull heaves: the heave is decoration
        // (see BuildDomainState) and is deliberately absent from the pose as well, so there is
        // no vertical motion in the navigation solution to report.
        var twist = new FramedTwist(
            Frame: CoordinateFrame.LocalEus,
            Linear: _groundVelocityEus,

            // Only the yaw component is modelled. Heading increases clockwise from north while
            // scene yaw about +Y increases anticlockwise from it, so the sign flips; wave-driven
            // roll and pitch are visual, and publishing their derivative would report a
            // decoration as a body rate.
            Angular: new Vector3(0f, (float)-_motion.YawRateRadPerSec, 0f),
            OriginId: context.Origin?.OriginId);

        double percent = EnergyPercent;

        return new AssetState(
            AssetId: AssetId,
            SourceTime: context.SourceTime,
            ReceiveTime: context.ReceiveTime,

            // One observation per integrated step, including the steps where the vessel was only
            // drifting: a vessel with the propeller stopped is still reporting, which is exactly
            // what distinguishes it from one that has stopped talking.
            SequenceNumber: _sequence,

            // Always fresh: the source is in-process, so no transport exists that could make a
            // simulated report stale. Staleness is in the model for external feeds.
            Freshness: DataFreshness.Fresh,
            Pose: pose,
            Twist: twist,
            OperationalState: ResolveOperationalState(),
            Mode: ModeToken,
            Power: BuildPower(percent),
            Health: _faultOnsets.Stamp(BuildHealth(percent, context.SourceTime), context.SourceTime),
            Link: new LinkState(
                Transport: LinkTransport.Loopback,
                // What the server knows, not what the asset wishes were true: an in-process
                // asset is always producing telemetry and has no way to notice that the far end
                // has stopped listening, so this has to come from the link ledger the operator's
                // cut actually reaches. Null means nobody is tracking links, which is honest for
                // a fixture and is exactly the previous behaviour.
                IsConnected: context.Link?.IsLinkConnected(AssetId) ?? true,
                LastHeardAt: context.ReceiveTime),
            Mission: BuildMission(),
            DomainState: BuildDomainState(context));
    }

    /// <summary>Coarse domain-neutral state a command validator gates on.</summary>
    /// <remarks>
    /// The judgement call worth spelling out: <b>being aground is not reported as a fault.</b>
    /// The catalog's <c>Operable</c> policy excludes <see cref="OperationalState.Faulted"/>, so
    /// publishing that for a stranded hull would refuse exactly the commands that get it off —
    /// transiting to deeper water, or going astern — while the vessel itself is in perfect health
    /// and the water is the problem. And unlike a bogged rover, a stranded vessel does not stay
    /// where it is: it lifts on the next of the tide and goes somewhere nobody chose. The
    /// grounding travels on <see cref="SurfaceDomainState.IsInsideWaterMask"/>, on
    /// <see cref="SurfaceDomainState.HasUnsafeUnderKeelClearance"/>, in the health summary and as
    /// an event, all of which reach an operator without disarming the recovery.
    /// <para>
    /// A latched emergency stop <em>is</em> <see cref="OperationalState.Emergency"/>, because
    /// there the refusal is the point. It is not a trap: <c>stop</c> is permitted in every
    /// operational state, so a drifting vessel is always one command from being commandable and
    /// two from being under way.
    /// </para>
    /// </remarks>
    /// <returns>The state to publish.</returns>
    private OperationalState ResolveOperationalState()
    {
        if (IsEmergencyStopped)
        {
            return OperationalState.Emergency;
        }

        return _navigator.Mode switch
        {
            SurfaceGuidanceMode.Transiting or SurfaceGuidanceMode.Steering
                or SurfaceGuidanceMode.Docking or SurfaceGuidanceMode.Undocking
                => OperationalState.Active,

            // Holding and station keeping are both "not making mission progress but under
            // control", and a blocked vessel has given up its passage and is waiting to be
            // retasked. All three are inside the Operable policy, so retasking is permitted.
            SurfaceGuidanceMode.Holding or SurfaceGuidanceMode.StationKeeping
                or SurfaceGuidanceMode.Blocked => OperationalState.Holding,

            // Reachable only under a policy that stops the vessel without inhibiting propulsion,
            // where the latch above is never set. The state is still Emergency, because what the
            // operational state reports is the situation and not the latch.
            SurfaceGuidanceMode.EmergencyStopped => OperationalState.Emergency,

            // Secured at a berth is the vessel's equivalent of a parked rover, and Stationary is
            // the state policy `undock` is gated on — so a moored vessel is exactly the one that
            // can be told to leave.
            _ => _navigator.IsDocked ? OperationalState.Standby : OperationalState.Ready,
        };
    }

    /// <summary>Orientation of the hull in the scene frame, as a rotation from body FLU axes.</summary>
    /// <remarks>
    /// Heading sets the bow direction; the wave-driven roll and pitch are added on top so the
    /// rendered hull is not sitting on a mirror. <b>That contribution is decoration.</b> It is
    /// absent from the pose's position, from the twist, and from every quantity under-keel
    /// clearance is measured against, and nothing may feed it back into the navigation solution.
    /// <para>
    /// The triad is built explicitly rather than by composing Euler quaternions, because
    /// <see cref="Matrix4x4"/> uses the row-vector convention — <c>r = v * M</c>, so each
    /// <b>row</b> holds one body axis expressed in the scene frame — which is the transpose of
    /// the column-vector convention used inside <see cref="CoordinateFrames"/>. Silently mixing
    /// the two is the transposed-rotation bug that yields an attitude correct on flat water and
    /// mirrored in a swell.
    /// </para>
    /// </remarks>
    /// <returns>The unit quaternion mapping body FLU axes into the scene frame.</returns>
    private Quaternion HullOrientation()
    {
        // The level triad at this heading: bow along the heading, left to port, up vertical.
        var forwardLevel = CoordinateFrames.BearingToEusVector(_motion.HeadingRad, 1.0);
        var upLevel = Vector3.UnitY;
        var leftLevel = Vector3.Cross(upLevel, forwardLevel);

        double cosPitch = Math.Cos(_wave.PitchRad);
        double sinPitch = Math.Sin(_wave.PitchRad);
        double cosRoll = Math.Cos(_wave.RollRad);
        double sinRoll = Math.Sin(_wave.RollRad);

        // Body axes in the level triad's own coordinates. Pitch is bow-up positive, so it lifts
        // the forward axis towards the vertical; roll is starboard-rail-down positive, so it
        // swings the left axis up and the mast to starboard.
        var forward = Combine(forwardLevel, leftLevel, upLevel, cosPitch, 0.0, sinPitch);
        var left = Combine(
            forwardLevel, leftLevel, upLevel, -sinPitch * sinRoll, cosRoll, cosPitch * sinRoll);

        forward = Normalise(forward, forwardLevel);
        left = Normalise(left, leftLevel);
        var up = Normalise(Vector3.Cross(forward, left), upLevel);

        var basis = Matrix4x4.Identity;
        basis.M11 = forward.X;
        basis.M12 = forward.Y;
        basis.M13 = forward.Z;
        basis.M21 = left.X;
        basis.M22 = left.Y;
        basis.M23 = left.Z;
        basis.M31 = up.X;
        basis.M32 = up.Y;
        basis.M33 = up.Z;

        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
    }

    /// <summary>Rebuilds a vector from components expressed in an orthonormal triad.</summary>
    /// <param name="forward">Bow axis of the triad.</param>
    /// <param name="left">Port axis of the triad.</param>
    /// <param name="up">Vertical axis of the triad.</param>
    /// <param name="alongForward">Component along the bow axis.</param>
    /// <param name="alongLeft">Component along the port axis.</param>
    /// <param name="alongUp">Component along the vertical axis.</param>
    /// <returns>The vector in the scene frame.</returns>
    private static Vector3 Combine(
        Vector3 forward, Vector3 left, Vector3 up,
        double alongForward, double alongLeft, double alongUp) =>
        (forward * (float)alongForward) + (left * (float)alongLeft) + (up * (float)alongUp);

    /// <summary>Normalises a vector, falling back when it is degenerate.</summary>
    /// <param name="vector">Vector to normalise.</param>
    /// <param name="fallback">Already-unit vector to use when normalisation is not possible.</param>
    /// <returns>A unit vector.</returns>
    private static Vector3 Normalise(Vector3 vector, Vector3 fallback)
    {
        float length = vector.Length();
        return length > 1e-6f ? vector / length : fallback;
    }

    /// <summary>Energy state of the pack.</summary>
    /// <remarks>
    /// Reported as a battery rather than through the generic aggregate alone, because
    /// <see cref="PowerState"/> deliberately models fuel, shore supply and hybrid sources too and
    /// a consumer that wants endurance needs to know which kind it is looking at.
    /// </remarks>
    /// <param name="percentRemaining">Remaining charge as a percentage.</param>
    /// <returns>The power state to publish.</returns>
    private PowerState BuildPower(double percentRemaining)
    {
        TimeSpan? endurance = _drawWatts > 0.0
            ? TimeSpan.FromHours(_energyWh / _drawWatts)
            : null;

        return new PowerState(
            Sources:
            [
                new PowerSource(
                    SourceId: "pack-a",
                    Kind: PowerSourceKind.Battery,
                    PercentRemaining: percentRemaining,
                    RemainingEnergyWh: _energyWh,
                    RemainingTime: endurance,
                    DrawWatts: _drawWatts),
            ],
            PercentRemaining: percentRemaining,
            RemainingEnergyWh: _energyWh,
            RemainingTime: endurance);
    }

    /// <summary>Overall and component-level health.</summary>
    /// <remarks>
    /// Four independent conditions, rolled up to the worst. Being aground outranks the rest
    /// because it is the one an operator has to act on before the tide does, and it stays a
    /// <b>warning-grade advisory</b> in its wording: the bed is a procedural height field, not a
    /// survey.
    /// <para>
    /// <b>Grounding and shallow water are two conditions here, not one.</b> Both are read off
    /// <see cref="WaterConstraints.ContactAt"/> — the single place the claim "the vessel is
    /// aground" originates — rather than off <see cref="WaterSample.IsNavigable"/>. The mask is
    /// cut at draft plus the advisory margin, so it refuses water a hull is floating in quite
    /// happily; wording a report from it publishes <c>HULL_AGROUND</c> for a vessel that is under
    /// way, answering the helm and merely inside its own margin, and publishes it again for one
    /// turned back at the edge of a no-go zone in any depth at all. An operator acts differently
    /// on "you are on the ground" than on "you have less water than you want", so the two carry
    /// different codes, different severities and different summaries. The margin stays advisory:
    /// inside it the vessel is derated by <see cref="UnderKeelClearance.SpeedFactorFor"/> and
    /// warned about, not declared grounded.
    /// </para>
    /// <para>
    /// A hull the mask holds while it is afloat and clear of its margin — a prohibited zone, in
    /// practice — is deliberately <em>not</em> a health condition. Nothing is wrong with the
    /// vessel; it has been told where it may not go, and that travels on
    /// <see cref="SurfaceDomainState.IsInsideWaterMask"/> and as an event.
    /// </para>
    /// <para>
    /// The station-keeping entry is where the remaining control authority is published. The wire
    /// model's <see cref="StationKeepState"/> carries a degraded flag and a machine-readable
    /// reason but no field for the authority figure, so it travels here in
    /// <see cref="ComponentHealth.Detail"/> — which exists for exactly this, a short qualifier a
    /// display renders beside a status — alongside the drift it is losing to. A dedicated field
    /// would be better and this is where it should land when the wire model gains one.
    /// </para>
    /// </remarks>
    /// <param name="percentRemaining">Remaining charge as a percentage.</param>
    /// <param name="raisedAt">Instant to stamp any fault with.</param>
    /// <returns>The health state to publish.</returns>
    private HealthState BuildHealth(double percentRemaining, DateTimeOffset raisedAt)
    {
        var clearance = _water.Clearance;
        var contact = WaterConstraints.ContactAt(_water);
        bool aground = contact == HullContactState.OnTheBed;
        bool unsafeClearance = contact == HullContactState.InsideSafetyMargin;
        bool lowEnergy = percentRemaining < LowEnergyPercent;
        var station = _navigator.StationKeepOutcome;
        bool holdFailing = station.IsDegraded;

        if (!aground && !unsafeClearance && !lowEnergy && !holdFailing)
        {
            return new HealthState(
                ComponentHealthStatus.Nominal, NoComponents, NoFaults, "Nominal.");
        }

        var components = new List<ComponentHealth>(4);
        var faults = new List<FaultCode>(4);

        if (aground)
        {
            components.Add(new ComponentHealth(
                "mobility.hull",
                ComponentHealthStatus.Critical,
                $"On the bed: {clearance.ClearanceM:0.00} m under the keel."));

            faults.Add(new FaultCode(
                Code: "HULL_AGROUND",
                Severity: FaultSeverity.Critical,
                Subsystem: "mobility.hull",

                // Worded off the clearance, not off the mask's refusal code, so this sentence is
                // only ever printed about a hull that is genuinely on the ground.
                Message: $"Advisory: the hull is resting on the bed ({clearance.ReasonCode}).",
                RaisedAt: raisedAt));
        }

        if (unsafeClearance)
        {
            components.Add(new ComponentHealth(
                "mobility.underKeel",
                ComponentHealthStatus.Warning,
                $"{clearance.ClearanceM:0.00} m under the keel."));

            faults.Add(new FaultCode(
                Code: "UNDER_KEEL_CLEARANCE_LOW",
                Severity: FaultSeverity.Error,
                Subsystem: "mobility.underKeel",

                // Says what it is and what it is not. The vessel is afloat, under way and
                // derated; a reader who takes this for a grounding sends the wrong response.
                Message: "Advisory: the vessel is afloat with under-keel clearance inside the "
                    + "hull's safe margin, and its speed is derated. It is not aground.",
                RaisedAt: raisedAt));
        }

        if (holdFailing)
        {
            components.Add(new ComponentHealth(
                "propulsion.stationKeeping",
                ComponentHealthStatus.Warning,
                $"{station.RemainingAuthorityFraction:0.00} of the hold's effort left against "
                + $"{station.DriftSpeedMps:0.00} m/s of drift."));

            faults.Add(new FaultCode(
                Code: "STATION_KEEP_DEGRADED",
                Severity: FaultSeverity.Warning,
                Subsystem: "propulsion.stationKeeping",
                Message: $"Advisory: the hold is not being maintained ({station.DegradedReason}).",
                RaisedAt: raisedAt));
        }

        if (lowEnergy)
        {
            components.Add(new ComponentHealth("power.battery", ComponentHealthStatus.Warning));
            faults.Add(new FaultCode(
                Code: "BATTERY_LOW",
                Severity: FaultSeverity.Warning,
                Subsystem: "power.battery",
                Message: "Battery below the return-to-base reserve.",
                RaisedAt: raisedAt));
        }

        return new HealthState(
            Overall: aground ? ComponentHealthStatus.Critical : ComponentHealthStatus.Warning,
            Components: components,
            Faults: faults,
            Summary: aground ? "Aground."
                : unsafeClearance ? "Shallow water."
                : holdFailing ? "Station keeping degraded."
                : "Battery low.");
    }

    /// <summary>What the vessel is currently working on, or null when nothing is assigned.</summary>
    /// <returns>The mission state to publish.</returns>
    private MissionState? BuildMission() => _navigator.Mode switch
    {
        SurfaceGuidanceMode.Docking => new MissionState(
            Execution: MissionExecutionState.Executing,
            TaskKind: "dock",
            DistanceRemainingM: _navigator.RemainingDistanceM),

        SurfaceGuidanceMode.StationKeeping => new MissionState(
            Execution: MissionExecutionState.Executing,
            TaskKind: "station-keep",
            DistanceRemainingM: _navigator.StationKeepOutcome.PositionErrorM),

        SurfaceGuidanceMode.Steering => new MissionState(
            Execution: MissionExecutionState.Executing,
            TaskKind: "course"),

        // A holding vessel keeps its passage, so it is paused rather than finished; anything else
        // without a target is not working on anything worth reporting.
        _ => _navigator.TargetEus is null
            ? null
            : new MissionState(
                Execution: _navigator.Mode == SurfaceGuidanceMode.Holding
                    ? MissionExecutionState.Paused
                    : MissionExecutionState.Executing,
                TaskKind: _navigator.Mode == SurfaceGuidanceMode.Undocking ? "undock" : "transit",
                DistanceRemainingM: _navigator.RemainingDistanceM),
    };

    /// <summary>The typed surface extension published beside the domain-neutral state.</summary>
    /// <remarks>
    /// Every field of <see cref="SurfaceDomainState"/> is populated, and the ones that are easy
    /// to collapse into each other are the ones worth naming:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Heading, course over ground, speed over ground and speed through water are four
    ///     quantities.</b> The bow direction comes from the integrated pose, the course and the
    ///     ground speed from the realised track, and the speed through the water from the body
    ///     velocities. A vessel stemming a foul tide has a healthy log reading and almost no
    ///     ground speed; the air domain shipped with airspeed and ground speed inverted, and this
    ///     is the same class of error waiting on the water.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Depth, draft and under-keel clearance are three quantities</b>, published
    ///     separately along with the subtraction, so no client has to redo it and get the sign
    ///     wrong. The unsafe flag comes off the clearance band rather than from a second
    ///     comparison, so it cannot disagree with the number beside it.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Current and wind are published as the environment, not as the drift.</b> These are
    ///     the sampled fields at the vessel; what the hull actually makes of them — the coupled
    ///     current plus the leeway — is the resultant published as
    ///     <see cref="SurfaceDomainState.PositionUncertaintyGrowthMps"/>, and the two are
    ///     deliberately not the same number.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Heave, roll and pitch are visual only.</b> They come from <see cref="WaveModel"/>,
    ///     they are not in the pose, not in the twist, and not in anything under-keel clearance
    ///     is measured against. They exist so the rendered hull moves; nothing should plan
    ///     against them.
    ///   </description></item>
    /// </list>
    /// </remarks>
    /// <param name="context">Capture context, read only for the local origin the station target is framed against.</param>
    /// <returns>The surface domain state to publish.</returns>
    private SurfaceDomainState BuildDomainState(in AssetCaptureContext context)
    {
        var clearance = _water.Clearance;

        return new SurfaceDomainState(
            HeadingRad: _motion.HeadingRad,

            // From the realised track, so it diverges from the heading exactly when it should:
            // by the crab angle in a cross-set, and by pi when the vessel is going astern. With
            // no ground speed there is no course, and the bow direction is the honest fallback.
            CourseOverGroundRad: CoordinateFrames.BearingFromEusVector(
                _groundVelocityEus, _motion.HeadingRad),
            SpeedOverGroundMps: CoordinateFrames.SpeedOverGround(_groundVelocityEus),
            SpeedThroughWaterMps: _motion.SpeedThroughWaterMps,
            SurgeMps: _motion.SurgeMps,
            SwayMps: _motion.SwayMps,
            YawRateRadPerSec: _motion.YawRateRadPerSec,

            // The mean surface the hull floats on, never the wave-displaced one. On dry land it
            // is the ground the vessel is stranded on, which is why it is not nullable here.
            WaterSurfaceElevationM: _waterSurfaceElevationM,
            WaterDepthM: clearance.WaterDepthM,
            DraftM: clearance.DraftM,
            UnderKeelClearanceM: clearance.ClearanceM,
            HasUnsafeUnderKeelClearance: clearance.IsUnsafe,
            CurrentSpeedMps: CoordinateFrames.SpeedOverGround(_sample.SurfaceCurrentEus),
            CurrentDirectionRad: CoordinateFrames.BearingFromEusVector(
                _sample.SurfaceCurrentEus, _motion.HeadingRad),
            WindSpeedMps: CoordinateFrames.SpeedOverGround(_sample.WindEus),
            WindDirectionRad: CoordinateFrames.BearingFromEusVector(
                _sample.WindEus, _motion.HeadingRad),
            IsInsideWaterMask: _water.IsNavigable,

            // Policy, never an assumption. See SurfaceSafetyPolicy for why this is not a constant.
            LinkLossBehavior: Safety.LinkLoss,
            PositionUncertaintyGrowthMps: PositionUncertaintyGrowthMps,
            StationKeep: BuildStationKeep(in context),
            HeaveM: _wave.HeaveM,
            RollRad: _wave.RollRad,
            PitchRad: _wave.PitchRad);
    }

    /// <summary>Rate the one-sigma horizontal position uncertainty grows at, in metres per second.</summary>
    /// <remarks>
    /// <b>Never zero, and never settling</b>, which is the whole reason this field is a rate
    /// rather than a constant. It is the speed an unpowered hull makes good over the ground — the
    /// coupled current plus the wind-driven leeway — so dead reckoning a silent vessel over an
    /// hour adds a kilometre of uncertainty even with the propeller stopped. The three domains
    /// diverge here and the divergence is load-bearing:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Air</b> is bounded but never zero. A drone executing its link-loss behaviour flies a
    ///     return or a landing, so uncertainty grows across that transit — roughly the wind speed
    ///     plus its airspeed-tracking error — and then stops once it is down.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Ground</b> is exactly zero at a standstill and small when moving. A rover that loses
    ///     its link stops and stays where it is, so its last known position is still its position
    ///     however stale the report; in motion it is odometry drift, a fraction of distance
    ///     travelled inflated by lost traction.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Surface</b> never settles, and does not even need the vessel to have been moving.
    ///     A hull with propulsion lost drifts at the vector sum of current and leeway for as long
    ///     as the link is out — which is why an advisory search radius an hour after a loss is a
    ///     completely different number in each of the three domains, and why a vessel is the one
    ///     that has to be looked for downstream rather than where it was last seen.
    ///   </description></item>
    /// </list>
    /// Advisory search-radius guidance. Not a navigation guarantee.
    /// </remarks>
    private double PositionUncertaintyGrowthMps =>
        CoordinateFrames.SpeedOverGround(_passiveDriftEus);

    /// <summary>The station-keeping goal and how well it is being met, or null when none is engaged.</summary>
    /// <remarks>
    /// <see cref="StationKeepState.HeadingSetpointRad"/> is published under every heading policy,
    /// not only a fixed one: it is the heading the law is actually steering to, which is what an
    /// operator watching a hull bow into a set needs to see. That is strictly more than the field
    /// promises, never less.
    /// </remarks>
    /// <param name="context">Capture context, read for the local origin the target pose is framed against.</param>
    /// <returns>The station-keep state to publish, or null.</returns>
    private StationKeepState? BuildStationKeep(in AssetCaptureContext context)
    {
        if (_navigator.StationKeep is not { } goal)
        {
            return null;
        }

        var outcome = _navigator.StationKeepOutcome;

        return new StationKeepState(
            IsEngaged: outcome.Phase != StationKeepPhase.Disengaged,
            Target: new FramedPose(
                Frame: CoordinateFrame.LocalEus,
                OriginId: context.Origin?.OriginId,
                Position: goal.TargetEus,
                Orientation: Quaternion.Identity,
                Covariance: null,
                Geo: context.Origin is { } origin
                    ? CoordinateFrames.LocalEusToGeo(goal.TargetEus, origin)
                    : null),
            ToleranceRadiusM: goal.ToleranceRadiusM,
            HeadingPolicy: goal.HeadingPolicy,
            HeadingSetpointRad: outcome.HeadingSetpointRad,
            PositionErrorM: outcome.PositionErrorM,
            IsDegraded: outcome.IsDegraded,
            DegradedReason: outcome.DegradedReason);
    }
}
