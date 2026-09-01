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
using ResQ.Simulation.Engine.Entities;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>An air asset: a view over a drone the SDK's world already owns and integrates.</summary>
/// <remarks>
/// This type deliberately implements <see cref="ISimulatedAsset"/> and <b>not</b>
/// <see cref="IStepDrivenAsset"/>. Air physics belongs to the SDK's <c>SimulationWorld</c>,
/// which steps every non-landed drone once per world step; a step here would either duplicate
/// that integration or sit as a no-op inviting someone to move flight physics into it. Either
/// outcome moves drone trajectories away from the pinned SDK behaviour. Everything derived —
/// operational state, altitudes, freshness, uncertainty growth — is computed in
/// <see cref="Capture"/>, where it is honestly a projection rather than a pretend step.
/// <para>
/// Body-frame conventions differ between the two sides, and that difference is load-bearing.
/// The SDK builds attitude with <c>CreateFromYawPitchRoll</c> about scene <c>+Y</c>, so its
/// body forward axis is <c>+Z</c> — it is <em>not</em> FLU. The wire model documents
/// <see cref="FramedPose.Orientation"/> as an EUS-from-FLU rotation, so the SDK quaternion is
/// composed with a fixed basis change on the way out. Reading heading off the raw quaternion
/// with an FLU helper would give a rotation that looks right in a hover and is wrong the moment
/// the airframe banks.
/// </para>
/// </remarks>
public sealed partial class AirAsset : ISimulatedAsset
{
    /// <summary>Battery percentage below which health is reported as degraded.</summary>
    private const double LowBatteryPercent = 20.0;

    /// <summary>Height gained by a bare takeoff command that carries no altitude, in metres.</summary>
    private const double DefaultTakeoffClimbM = 30.0;

    /// <summary>
    /// Airspeed-tracking error added to wind speed to give a bounded position-uncertainty
    /// growth rate, in metres per second.
    /// </summary>
    /// <remarks>
    /// Bounded is the point. An air asset that loses its link executes a return or a landing, so
    /// its uncertainty grows over that transit and then stops — unlike a vessel, whose drift
    /// never settles.
    /// </remarks>
    private const double AirspeedTrackingErrorMps = 0.5;

    /// <summary>
    /// Rotation taking FLU body axes into the SDK's body axes (forward <c>+Z</c>, left
    /// <c>+X</c>, up <c>+Y</c>).
    /// </summary>
    /// <remarks>
    /// A 120-degree turn about <c>(1, 1, 1)</c>: a proper rotation, not a mirror, so composing
    /// with it preserves handedness. Written as an explicit basis change rather than by swapping
    /// Euler angles, which is the classic way to produce an attitude that is correct in level
    /// flight and mirrored in a bank.
    /// </remarks>
    private static readonly Quaternion SdkBodyFromFlu = new(-0.5f, -0.5f, -0.5f, 0.5f);

    private static readonly FaultCode[] NoFaults = [];
    private static readonly ComponentHealth[] NoComponents = [];
    private static readonly AssetEvent[] NoEvents = [];

    private readonly SimulatedDrone _drone;
    private readonly List<AssetEvent> _events = [];

    // Transition tracking for event raising, guarded by _lastObservedTick so capturing twice
    // within one tick cannot raise an event twice — capture must be idempotent per tick.
    private long _lastObservedTick = -1;
    private bool _wasLanded;
    private bool _lowBatteryLatched;
    private double _lastHeadingRad;

    /// <summary>Wraps a drone the SDK world already owns.</summary>
    /// <param name="drone">Drone instance returned by the SDK world's <c>AddDrone</c>.</param>
    /// <param name="descriptor">Descriptor for this asset; its domain must be <see cref="AssetDomain.Air"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="drone"/> or <paramref name="descriptor"/> is null.</exception>
    /// <exception cref="ArgumentException">The descriptor is not an air descriptor, or its id does not match the drone.</exception>
    public AirAsset(SimulatedDrone drone, AssetDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(drone);
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Domain != AssetDomain.Air)
        {
            throw new ArgumentException(
                $"An air asset needs an air descriptor; got '{descriptor.Domain}'.", nameof(descriptor));
        }

        if (!string.Equals(descriptor.AssetId, drone.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Descriptor id '{descriptor.AssetId}' does not match drone id '{drone.Id}'.",
                nameof(descriptor));
        }

        _drone = drone;
        Descriptor = descriptor;
        _wasLanded = drone.FlightModel.HasLanded;
    }

    /// <inheritdoc />
    public string AssetId => _drone.Id;

    /// <inheritdoc />
    public AssetDomain Domain => AssetDomain.Air;

    /// <inheritdoc />
    public Vector3 PositionEus => _drone.FlightModel.State.Position;

    /// <inheritdoc />
    public AssetDescriptor Descriptor { get; }

    /// <summary>The SDK drone this asset views.</summary>
    /// <remarks>
    /// Exposed because the swarm coordinator and the v1 snapshot projection both address the
    /// SDK's own drone list, and duplicating that state on our side is what would let the two
    /// populations diverge. Read-mostly: the only supported mutation is
    /// <see cref="SimulatedDrone.SendCommand"/>, which is what <see cref="Apply"/> is for.
    /// </remarks>
    public SimulatedDrone Drone => _drone;

    /// <summary>Whether the drone is off the ground.</summary>
    /// <remarks>
    /// The inverse of the flight model's landed flag, which is also the v1 armed flag. The two
    /// have always been the same bit and are deliberately kept that way.
    /// </remarks>
    public bool IsAirborne => !_drone.FlightModel.HasLanded;

    /// <summary>The v1 status string for this drone: <c>"landed"</c> or <c>"flying"</c>.</summary>
    /// <remarks>
    /// Kept here so the compatibility projection and the v2 mode string cannot drift apart.
    /// Preserves the v1 semantics exactly: landed means disarmed, anything else means flying and
    /// armed.
    /// </remarks>
    public string StatusV1 => _drone.FlightModel.HasLanded ? "landed" : "flying";

    /// <inheritdoc />
    public AssetState Capture(in AssetCaptureContext context)
    {
        var flight = _drone.FlightModel;
        var physics = flight.State;
        var position = physics.Position;

        var sample = context.Environment.Sample(position, Descriptor.Dimensions.FootprintRadiusM);

        // Ground and air-relative velocity are different vectors and which one the flight model
        // stores depends on the model. See ResolveVelocities.
        var (groundVelocity, airRelativeVelocity) = ResolveVelocities(flight, sample.WindEus);

        // FLU-referenced attitude, so heading is read the same way in every domain.
        var orientationFlu = Quaternion.Multiply(physics.Orientation, SdkBodyFromFlu);
        double heading = CoordinateFrames.HeadingFromEusOrientation(orientationFlu, _lastHeadingRad);
        _lastHeadingRad = heading;

        double groundSpeed = CoordinateFrames.SpeedOverGround(groundVelocity);
        double windSpeed = CoordinateFrames.SpeedOverGround(sample.WindEus);
        bool landed = flight.HasLanded;

        RaiseTransitionEvents(in context, landed, physics.BatteryPercent);

        var pose = new FramedPose(
            Frame: CoordinateFrame.LocalEus,
            OriginId: context.Origin?.OriginId,
            Position: position,
            Orientation: orientationFlu,
            Covariance: null,
            Geo: context.Origin is { } origin
                ? CoordinateFrames.LocalEusToGeo(position, origin)
                : null);

        // Linear twist is the GROUND velocity, deliberately: a twist is the time derivative of
        // the pose it travels beside, so anything that differentiates position — a dead-reckoned
        // extrapolation between frames, a track fuser blending this with an external contact —
        // must get the same vector back. Publishing an air-relative velocity here would make
        // those readouts disagree with the positions in the very same frame, by the wind.
        var twist = new FramedTwist(
            Frame: CoordinateFrame.LocalEus,
            Linear: groundVelocity,
            Angular: physics.AngularVelocity,
            OriginId: context.Origin?.OriginId);

        var domain = new AirDomainState(
            IsAirborne: !landed,
            HeadingRad: heading,
            CourseOverGroundRad: CoordinateFrames.BearingFromEusVector(groundVelocity, heading),
            GroundSpeedMps: groundSpeed,
            ClimbRateMps: groundVelocity.Y,

            // Three altitudes against three different references, never collapsed into one:
            // above-ground drives obstacle clearance, above-launch drives the return profile,
            // and mean sea level is what a shared air picture needs. The scene's Y datum is
            // treated as mean sea level, which is the datum terrain elevations are quoted
            // against.
            AltitudeAboveGroundM: position.Y - sample.TerrainElevationM,
            AltitudeAboveLaunchM: position.Y - flight.LaunchPosition.Y,
            AltitudeMslM: position.Y,

            WindSpeedMps: windSpeed,
            WindDirectionRad: CoordinateFrames.BearingFromEusVector(sample.WindEus),
            LinkLossBehavior: LinkLossBehavior.ReturnToBase,
            PositionUncertaintyGrowthMps: windSpeed + AirspeedTrackingErrorMps,

            // Derived from truth state rather than read off an air-data sensor the simulated
            // airframe does not carry. Reported because it is the honest speed through the air,
            // and it diverges from ground speed exactly when the wind matters. Horizontal, like
            // GroundSpeedMps, so the two are directly comparable and their difference is the
            // headwind or tailwind component; the vertical rate is reported as ClimbRateMps.
            AirspeedMps: CoordinateFrames.SpeedOverGround(airRelativeVelocity),
            IsWithinGeofence: true);

        return new AssetState(
            AssetId: AssetId,
            SourceTime: context.SourceTime,
            ReceiveTime: context.ReceiveTime,

            // The SDK counts a telemetry tick per integrated step and skips landed drones, so a
            // landed asset's sequence number stops advancing. That is the honest reading: it is
            // not producing new observations.
            SequenceNumber: (ulong)Math.Max(0, _drone.TelemetryCount),

            // Always fresh: the source is in-process, so no transport exists that could make a
            // simulated report stale. Staleness is in the model for external feeds.
            Freshness: DataFreshness.Fresh,
            Pose: pose,
            Twist: twist,
            OperationalState: landed ? OperationalState.Standby : OperationalState.Active,

            // The v1 status string, unchanged, so the compatibility projection and the v2 mode
            // can never disagree about the same drone.
            Mode: StatusV1,
            Power: BuildPower(physics.BatteryPercent),
            Health: BuildHealth(physics.BatteryPercent, context.SourceTime),
            Link: new LinkState(
                Transport: LinkTransport.Loopback,
                IsConnected: true,
                LastHeardAt: context.ReceiveTime),

            // Route state lives in the swarm coordinator, which assigns patrol legs the asset
            // itself never sees. Reporting a mission from here would be a guess.
            Mission: null,
            DomainState: domain);
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
    /// <summary>
    /// Splits a flight model's single stored velocity into the two distinct quantities the wire
    /// carries: velocity over the ground, and velocity relative to the air mass.
    /// </summary>
    /// <remarks>
    /// Ground speed and airspeed differ by the wind, and confusing them makes every wind-affected
    /// readout wrong in the same way a heading/course-over-ground mix-up does for a vessel.
    /// <see cref="DronePhysicsState.Velocity"/> is a single field, and <b>which of the two it
    /// holds depends on the flight model</b> — the SDK's <c>IFlightModel</c> has no way to
    /// declare that, so the convention is pinned here rather than assumed:
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="KinematicFlightModel"/> integrates <c>position += velocity*dt + wind*dt</c>
    ///     and stores only the commanded velocity. Wind moves the airframe without ever entering
    ///     the stored value, so the field is the <em>air-relative</em> velocity and the ground
    ///     velocity is that plus the wind. This is the configured default
    ///     (<c>SimulationConfig.FlightModel</c>), so getting it backwards was wrong everywhere.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="QuadrotorFlightModel"/> applies wind as a body force that accelerates the
    ///     airframe, then integrates <c>position += velocity*dt</c>. The stored value is
    ///     therefore already the <em>ground</em> velocity, and airspeed is that minus the wind.
    ///   </description></item>
    /// </list>
    /// Any future model is treated as ground-referenced, which is the convention a model that
    /// integrates forces will follow.
    /// <para>
    /// The kinematic identity holds exactly except on the tick a descent is clamped at
    /// <c>y = 0</c>, where the integrator discards part of the step; the vertical component is
    /// then a commanded rate rather than an achieved one, for that tick only.
    /// </para>
    /// </remarks>
    /// <param name="flight">Flight model to read; its concrete type selects the convention.</param>
    /// <param name="windEus">Wind velocity at the asset, in metres per second, in the scene frame.</param>
    /// <returns>The ground velocity and the air-relative velocity, both in the scene frame.</returns>
    private static (Vector3 Ground, Vector3 AirRelative) ResolveVelocities(
        IFlightModel flight, Vector3 windEus)
    {
        var velocity = flight.State.Velocity;

        return flight is KinematicFlightModel
            ? (velocity + windEus, velocity)
            : (velocity, velocity - windEus);
    }

    private static PowerState BuildPower(double batteryPercent) =>
        new(
            Sources: [new PowerSource("battery", PowerSourceKind.Battery, PercentRemaining: batteryPercent)],
            PercentRemaining: batteryPercent);

    private static HealthState BuildHealth(double batteryPercent, DateTimeOffset raisedAt)
    {
        if (batteryPercent >= LowBatteryPercent)
        {
            return new HealthState(
                ComponentHealthStatus.Nominal, NoComponents, NoFaults, "Nominal.");
        }

        return new HealthState(
            Overall: ComponentHealthStatus.Warning,
            Components: [new ComponentHealth("power.battery", ComponentHealthStatus.Warning)],
            Faults:
            [
                new FaultCode(
                    Code: "BATTERY_LOW",
                    Severity: FaultSeverity.Warning,
                    Subsystem: "power.battery",
                    Message: "Battery below the return-to-base reserve.",
                    RaisedAt: raisedAt),
            ],
            Summary: "Battery low.");
    }

    /// <summary>Raises events for state transitions observed since the previous tick.</summary>
    /// <remarks>
    /// Guarded on the tick, so a second capture within the same tick observes the same
    /// transitions and raises nothing new. That is what keeps <see cref="Capture"/> idempotent
    /// per tick, which matters because a broadcast frame and a REST read can both capture the
    /// same tick.
    /// </remarks>
    /// <param name="context">Capture context supplying the tick and simulation time.</param>
    /// <param name="landed">Whether the flight model currently reports a landed drone.</param>
    /// <param name="batteryPercent">Remaining battery charge as a percentage.</param>
    private void RaiseTransitionEvents(in AssetCaptureContext context, bool landed, double batteryPercent)
    {
        if (context.Tick == _lastObservedTick)
        {
            return;
        }

        _lastObservedTick = context.Tick;

        if (landed != _wasLanded)
        {
            _events.Add(new AssetEvent(
                AssetId,
                landed ? "air.landed" : "air.airborne",
                AssetEventSeverity.Info,
                landed ? "Drone has landed." : "Drone has left the ground.",
                context.SimulationTimeSeconds,
                context.Tick));
            _wasLanded = landed;
        }

        // Latched rather than level-triggered: a battery sitting on the threshold would
        // otherwise emit an event every tick and bury everything else in the log.
        if (batteryPercent < LowBatteryPercent && !_lowBatteryLatched)
        {
            _lowBatteryLatched = true;
            _events.Add(new AssetEvent(
                AssetId,
                "air.batteryLow",
                AssetEventSeverity.Warning,
                "Battery below the return-to-base reserve.",
                context.SimulationTimeSeconds,
                context.Tick));
        }
        else if (batteryPercent >= LowBatteryPercent)
        {
            _lowBatteryLatched = false;
        }
    }
}
