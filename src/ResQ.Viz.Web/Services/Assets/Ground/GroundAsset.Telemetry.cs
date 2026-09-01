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

// The projection half of GroundAsset: turning integrated state into the records the wire carries.
// Split from the physics half so a change to what a rover reports cannot silently alter how it
// drives, and from the event half because a capture must be repeatable within a tick and raise
// nothing; the type's summary lives on the primary declaration in GroundAsset.cs.
public sealed partial class GroundAsset
{
    /// <inheritdoc />
    /// <remarks>
    /// A projection of state <see cref="Step"/> already computed, and deliberately not a second look
    /// at the world: the stored contact and environment sample describe the same instant as the pose
    /// they travel beside, whereas re-sampling here would publish a terrain elevation from one
    /// instant against a position from another. Nothing is mutated and no event is raised, so
    /// calling this twice within a tick yields the same state both times.
    /// </remarks>
    public AssetState Capture(in AssetCaptureContext context)
    {
        var pose = new FramedPose(
            Frame: CoordinateFrame.LocalEus,
            OriginId: context.Origin?.OriginId,
            Position: _positionEus,
            Orientation: _contact.OrientationEusFromFlu,
            Covariance: null,
            Geo: context.Origin is { } origin
                ? CoordinateFrames.LocalEusToGeo(_positionEus, origin)
                : null);

        // Linear twist is the GROUND velocity, taken as the per-tick position delta over the
        // timestep rather than rebuilt from heading and forward speed. Those two disagree by the
        // vertical component terrain following contributed and by whatever the contact solver
        // clamped, so anything that differentiates the published position — a dead-reckoned
        // extrapolation between frames, a track fuser blending this with an external contact —
        // would get back a vector that does not match the positions in the very same frame.
        var twist = new FramedTwist(
            Frame: CoordinateFrame.LocalEus,
            Linear: _groundVelocityEus,

            // Only the yaw component is modelled. Heading increases clockwise from north while
            // scene yaw about +Y increases anticlockwise from it, so the sign flips; roll and pitch
            // follow the terrain rather than being integrated, and publishing their numerical
            // derivative would report height-field noise as a body rate.
            Angular: new Vector3(0f, (float)-_motion.YawRateRadPerSec, 0f),
            OriginId: context.Origin?.OriginId);

        double percent = EnergyPercent;

        return new AssetState(
            AssetId: AssetId,
            SourceTime: context.SourceTime,
            ReceiveTime: context.ReceiveTime,

            // One observation per integrated step, including the steps where the vehicle stood
            // still: a parked rover is still reporting, which is exactly what distinguishes it from
            // one that has stopped talking. This is the opposite of the air case, where the SDK
            // stops counting telemetry for a landed drone because it stops integrating it.
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
            DomainState: BuildDomainState());
    }

    /// <inheritdoc />
    public IReadOnlyList<AssetEvent> DrainEvents()
    {
        if (_events.Count == 0)
        {
            return NoEvents;
        }

        var drained = _events.ToArray();
        _events.Clear();
        return drained;
    }

    /// <summary>Coarse domain-neutral state a command validator gates on.</summary>
    /// <remarks>
    /// The one judgement call worth spelling out: <b>being immobilised by terrain is not reported
    /// as a fault.</b> The catalog's <c>Operable</c> policy excludes
    /// <see cref="OperationalState.Faulted"/>, so publishing that for a bogged rover would refuse
    /// exactly the commands that get it out — reversing, or driving somewhere else — while the
    /// vehicle itself is in perfect health and the ground is the problem. The immobilisation
    /// travels on <see cref="GroundDomainState.IsImmobilised"/>, in the health summary, and as an
    /// event, all of which reach an operator without disarming the recovery.
    /// <para>
    /// A latched emergency stop <em>is</em> <see cref="OperationalState.Emergency"/>, because there
    /// the refusal is the point: nothing but an explicit release should move the vehicle again.
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
            GroundGuidanceMode.Driving or GroundGuidanceMode.Reversing
                or GroundGuidanceMode.Manual => OperationalState.Active,

            // A blocked rover has stopped and is waiting to be retasked, which is what Holding
            // means — and Holding is inside the Operable policy, so retasking is still permitted.
            GroundGuidanceMode.Holding or GroundGuidanceMode.Blocked => OperationalState.Holding,
            GroundGuidanceMode.Parked => OperationalState.Standby,
            _ => OperationalState.Ready,
        };
    }

    /// <summary>Energy state of the pack.</summary>
    /// <remarks>
    /// Reported as a battery rather than through the generic aggregate alone, because
    /// <see cref="PowerState"/> deliberately models fuel, tether and hybrid sources too and a
    /// consumer that wants endurance needs to know which kind it is looking at.
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
    /// Three independent conditions, rolled up to the worst. Rollover proximity outranks the
    /// others because a vehicle on a steep cross-slope is the one an operator has to act on now,
    /// and it stays a <b>warning-grade advisory</b> in its wording: the underlying figure is
    /// quasi-static, ignores suspension travel and load shift, and is inferred from an operational
    /// limit rather than from a mass distribution.
    /// </remarks>
    /// <param name="percentRemaining">Remaining charge as a percentage.</param>
    /// <param name="raisedAt">Instant to stamp any fault with.</param>
    /// <returns>The health state to publish.</returns>
    private HealthState BuildHealth(double percentRemaining, DateTimeOffset raisedAt)
    {
        bool rollover = _contact.HasRolloverRisk;
        bool immobilised = _contact.IsImmobilised;
        bool lowEnergy = percentRemaining < LowEnergyPercent;

        if (!rollover && !immobilised && !lowEnergy)
        {
            return new HealthState(
                ComponentHealthStatus.Nominal, NoComponents, NoFaults, "Nominal.");
        }

        var components = new List<ComponentHealth>(3);
        var faults = new List<FaultCode>(3);

        if (rollover)
        {
            components.Add(new ComponentHealth("mobility.stability", ComponentHealthStatus.Critical));
            faults.Add(new FaultCode(
                Code: "ROLLOVER_RISK",
                Severity: FaultSeverity.Critical,
                Subsystem: "mobility.stability",
                Message: "Advisory: cross-slope is past the platform's operational limit.",
                RaisedAt: raisedAt));
        }

        if (immobilised)
        {
            components.Add(new ComponentHealth("mobility.drivetrain", ComponentHealthStatus.Warning));
            faults.Add(new FaultCode(
                Code: "MOBILITY_IMMOBILISED",
                Severity: FaultSeverity.Error,
                Subsystem: "mobility.drivetrain",
                Message: $"Advisory: the ground here will not carry the vehicle ({_contact.LimitReason}).",
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
            Overall: rollover ? ComponentHealthStatus.Critical : ComponentHealthStatus.Warning,
            Components: components,
            Faults: faults,
            Summary: rollover ? "Rollover risk." : immobilised ? "Immobilised." : "Battery low.");
    }

    /// <summary>What the rover is currently working on, or null when nothing is assigned.</summary>
    /// <remarks>
    /// Reported, where <see cref="AirAsset"/> reports null, because the difference is real rather
    /// than stylistic: a drone's route lives in the swarm coordinator, which the drone never sees,
    /// whereas a rover's target lives in its own navigator. Publishing it here is a readout of
    /// state this asset actually holds, not a guess about somebody else's plan.
    /// </remarks>
    /// <returns>The mission state to publish.</returns>
    private MissionState? BuildMission() =>
        _navigator.TargetEus is null
            ? null
            : new MissionState(
                Execution: _navigator.Mode == GroundGuidanceMode.Holding
                    ? MissionExecutionState.Paused
                    : MissionExecutionState.Executing,
                TaskKind: "drive",
                DistanceRemainingM: _navigator.RemainingDistanceM);

    /// <summary>The typed ground extension published beside the domain-neutral state.</summary>
    /// <remarks>
    /// Every field of <see cref="GroundDomainState"/> is populated. Four quantities worth naming
    /// explicitly, because each is present under a name that does not obviously match:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Drive type</b> is not repeated here. It is descriptor data, not state — it never
    ///     changes — and it already travels as <see cref="AssetDescriptor.VehicleClass"/> and
    ///     <see cref="AssetDescriptor.MobilityModel"/>, which the client caches by revision.
    ///     Restating it at stream rate is exactly what splitting descriptor from state avoids.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Grade and cross-slope</b> are <see cref="GroundDomainState.PitchRad"/> and
    ///     <see cref="GroundDomainState.RollRad"/>. They are the same two angles the contact solver
    ///     resolves, published under the attitude names an operator display uses, and they remain
    ///     separate quantities rather than one slope magnitude — grade decides whether the vehicle
    ///     climbs, cross-slope decides whether it rolls over.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Per-track speeds</b> are exactly recoverable from
    ///     <see cref="GroundDomainState.GroundSpeedMps"/> and the published yaw rate through
    ///     <see cref="DifferentialDynamics.TrackSpeedsFor"/>, which is why
    ///     <see cref="GroundMotionState"/> does not store them either. A second copy of a derived
    ///     quantity is how the two eventually disagree.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Wheel slip is deliberately not reported.</b> Neither ground model integrates it —
    ///     the skid-steer takes its yaw rate as the kinematic ideal — so any number published here
    ///     would be invented rather than measured. What the surface actually does to the vehicle
    ///     travels as <see cref="GroundDomainState.TractionCoefficient"/> and
    ///     <see cref="GroundDomainState.DeratedSpeedLimitMps"/>, both of which are real outputs of
    ///     the contact solver.
    ///   </description></item>
    /// </list>
    /// The traversability verdict for the ground under the vehicle is carried by
    /// <see cref="GroundDomainState.IsImmobilised"/> together with
    /// <see cref="GroundDomainState.ImmobilisationReason"/>, whose token is the same
    /// <see cref="Traversability.ReasonCode"/> vocabulary a route preview reports — so a refused
    /// target and a stopped rover explain themselves in one language.
    /// </remarks>
    /// <returns>The ground domain state to publish.</returns>
    private GroundDomainState BuildDomainState() => new(
        IsMoving: _motion.IsMoving,
        HeadingRad: _motion.HeadingRad,

        // Course over ground comes from the published velocity, so it diverges from heading
        // exactly when it should: by pi while reversing, and by whatever the terrain contributed
        // when the vehicle was pushed off its commanded line. At a standstill the velocity is
        // degenerate and the heading is the honest fallback.
        CourseOverGroundRad: CoordinateFrames.BearingFromEusVector(
            _groundVelocityEus, _motion.HeadingRad),
        GroundSpeedMps: _motion.ForwardSpeedMps,

        // Always zero for a pivot-steered platform: it has no steering linkage, and the skid-steer
        // model never writes this field. That is the convention the wire model already documents.
        SteeringAngleRad: _motion.SteeringAngleRad,
        RollRad: _contact.CrossSlopeRad,
        PitchRad: _contact.GradeRad,
        TerrainElevationM: _sample.TerrainElevationM,
        SlopeRad: _contact.SlopeRad,
        SurfaceType: _sample.SurfaceMaterialName,
        TractionCoefficient: _contact.TractionCoefficient,
        DeratedSpeedLimitMps: _contact.SafeSpeedMps,
        RolloverRisk: _contact.RolloverRiskFraction,
        IsImmobilised: _contact.IsImmobilised,

        // A rover that loses its link stops and stays put, indefinitely and for free. It is the
        // only one of the three domains that can: an air asset must come down, and a vessel cannot
        // stop at all.
        LinkLossBehavior: LinkLossBehavior.StopAndHold,
        PositionUncertaintyGrowthMps: PositionUncertaintyGrowthMps,
        ImmobilisationReason: ImmobilisationReason);

    /// <summary>Rate the one-sigma horizontal position uncertainty grows at, in metres per second.</summary>
    /// <remarks>
    /// <b>Exactly zero at a standstill</b>, and that is the whole point of the field being a rate
    /// rather than a constant. A ground asset that loses its link stops and stays where it is, so
    /// dead reckoning it over an hour of silence must add nothing: a partitioned rover's last known
    /// position is still its position, however stale the report. The three domains diverge here and
    /// the divergence is load-bearing:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Air</b> is bounded but never zero. A drone executing its link-loss behaviour flies a
    ///     return or a landing, so uncertainty grows across that transit — roughly the wind speed
    ///     plus its airspeed-tracking error — and then stops once it is down.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Ground</b> is zero when stopped and small when moving. In motion it is odometry
    ///     drift: a fraction of distance travelled, inflated by lost traction because slipping
    ///     wheels turn without carrying the vehicle with them. It is bounded by the commanded
    ///     speed and it settles the instant the vehicle does.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Surface</b> never settles. A vessel with propulsion lost drifts at the vector sum of
    ///     current and wind-driven leeway, so its uncertainty grows for as long as the link is out
    ///     — which is why an advisory search radius hours after a loss is a completely different
    ///     number in each of the three domains.
    ///   </description></item>
    /// </list>
    /// Advisory search-radius guidance. Not a navigation guarantee.
    /// </remarks>
    private double PositionUncertaintyGrowthMps =>
        Math.Abs(_motion.ForwardSpeedMps) * OdometryDriftFraction
        / Math.Max(_contact.TractionCoefficient, GroundConditions.MinTractionCoefficient);

    /// <summary>Machine-readable reason the rover is not making progress, or null when it is.</summary>
    /// <remarks>
    /// Carries a guidance refusal as well as a physical immobilisation, even though
    /// <see cref="GroundDomainState.IsImmobilised"/> stays false for the former. The two are
    /// genuinely different facts — the ground will not carry the vehicle, versus the vehicle is
    /// declining to drive onto ground it has judged impassable — and the wire keeps them apart in
    /// the flag. But the operator's question in both cases is the same one, "why is it not moving",
    /// so both answers are published, in the same
    /// <see cref="Traversability.ReasonCode"/> vocabulary a route preview uses.
    /// </remarks>
    private string? ImmobilisationReason
    {
        get
        {
            if (_contact.IsImmobilised)
            {
                return _contact.LimitReason;
            }

            return _navigator.Mode == GroundGuidanceMode.Blocked
                ? Traversability.ReasonCode(_navigator.BlockingReason)
                : null;
        }
    }
}
