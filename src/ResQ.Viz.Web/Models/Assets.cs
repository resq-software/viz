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

namespace ResQ.Viz.Web.Models;

/// <summary>Physical envelope and mass of an asset.</summary>
/// <param name="LengthM">Longitudinal extent along the body X axis, in metres.</param>
/// <param name="WidthM">Lateral extent along the body Y axis, in metres.</param>
/// <param name="HeightM">Vertical extent along the body Z axis, in metres.</param>
/// <param name="MassKg">Gross mass including payload, in kilograms.</param>
/// <param name="FootprintRadiusM">
/// Radius of the circle that bounds the asset's ground or water footprint, in metres.
/// Separate from the box extents because separation, terrain sampling and shoreline checks
/// all want one cheap conservative number, and a rotating asset's box extents are not it.
/// </param>
public record PhysicalDimensions(
    double LengthM,
    double WidthM,
    double HeightM,
    double MassKg,
    double FootprintRadiusM);

/// <summary>What an asset can and cannot physically do when moving.</summary>
/// <remarks>
/// This is what stops a task allocator assigning "wait here" to a displacement-hull vessel
/// that cannot hold station. A multirotor is roughly
/// <c>(0, 18, 0, true, 0, ...)</c>; a displacement hull has a non-zero
/// <paramref name="MinSpeedMps"/> — below it the rudder loses authority — and a non-zero
/// <paramref name="PassiveDriftMps"/>, because unpowered it moves with the current.
/// </remarks>
/// <param name="MinSpeedMps">Lowest speed at which the asset remains controllable, in m/s. Zero if it can stop.</param>
/// <param name="MaxSpeedMps">Highest sustainable commanded speed, in m/s.</param>
/// <param name="MinTurnRadiusM">Tightest achievable turn radius, in metres. Zero if it can turn on the spot.</param>
/// <param name="CanStationKeep">True if the asset can actively hold a position against disturbance.</param>
/// <param name="PassiveDriftMps">Typical speed the asset moves at with no propulsion, in m/s.</param>
/// <param name="StationKeepCostW">Mean power drawn while holding station, in watts. Zero when holding is free.</param>
public record MotionConstraints(
    double MinSpeedMps,
    double MaxSpeedMps,
    double MinTurnRadiusM,
    bool CanStationKeep,
    double PassiveDriftMps,
    double StationKeepCostW);

/// <summary>One energy store or supply aboard an asset.</summary>
/// <param name="SourceId">Stable identifier, unique within the asset (e.g. "pack-a", "tank-1").</param>
/// <param name="Kind">What kind of source this is; determines how the percentage is read.</param>
/// <param name="PercentRemaining">Remaining fraction as 0–100, or null if not measurable.</param>
/// <param name="RemainingEnergyWh">Remaining usable energy in watt-hours; fuel is converted to its energy equivalent.</param>
/// <param name="RemainingTime">Estimated endurance at the current draw. Null for an unbounded source such as a tether.</param>
/// <param name="DrawWatts">Instantaneous power drawn from this source, in watts.</param>
/// <param name="VoltageV">Bus voltage, in volts, where meaningful.</param>
/// <param name="TemperatureC">Source temperature in degrees Celsius; drives derating.</param>
/// <param name="IsCharging">True while this source is being replenished.</param>
public record PowerSource(
    string SourceId,
    PowerSourceKind Kind,
    double? PercentRemaining = null,
    double? RemainingEnergyWh = null,
    TimeSpan? RemainingTime = null,
    double? DrawWatts = null,
    double? VoltageV = null,
    double? TemperatureC = null,
    bool IsCharging = false);

/// <summary>Aggregate energy state of an asset.</summary>
/// <remarks>
/// Aggregate figures sit beside the per-source list rather than replacing it: the UI and
/// the endurance check want one number, but a hybrid asset genuinely has several, and a
/// tethered asset has none that means "percent full". Every aggregate is nullable so
/// "not applicable" never has to be encoded as a misleading 0 or 100.
/// </remarks>
/// <param name="Sources">Every energy store or supply aboard. May be empty for an unmetered asset.</param>
/// <param name="PercentRemaining">Aggregate remaining fraction as 0–100, or null when no source reports one.</param>
/// <param name="RemainingEnergyWh">Total remaining usable energy across all sources, in watt-hours.</param>
/// <param name="RemainingTime">Estimated endurance at the current draw across all sources.</param>
/// <param name="IsExternallyPowered">True when a tether or shore supply makes endurance effectively unbounded.</param>
/// <param name="IsCharging">True when net energy aboard is increasing.</param>
public record PowerState(
    IReadOnlyList<PowerSource> Sources,
    double? PercentRemaining = null,
    double? RemainingEnergyWh = null,
    TimeSpan? RemainingTime = null,
    bool IsExternallyPowered = false,
    bool IsCharging = false);

/// <summary>Health of one named subsystem.</summary>
/// <param name="Component">Subsystem identifier (e.g. "propulsion.motor.1", "gnss", "thruster.port").</param>
/// <param name="Status">Health of that subsystem.</param>
/// <param name="Detail">Optional short human-readable qualifier.</param>
public record ComponentHealth(
    string Component,
    ComponentHealthStatus Status,
    string? Detail = null);

/// <summary>A structured, machine-readable fault.</summary>
/// <remarks>
/// The code is the contract — dashboards, alerting and tests key on it — while
/// <paramref name="Message"/> is free to be rewritten for readability at any time.
/// </remarks>
/// <param name="Code">Stable machine-readable code, e.g. "GNSS_FIX_LOST".</param>
/// <param name="Severity">How serious the fault is.</param>
/// <param name="Subsystem">Subsystem the fault was raised against; matches a <see cref="ComponentHealth.Component"/> where possible.</param>
/// <param name="Message">Operator-facing description of this specific occurrence.</param>
/// <param name="RaisedAt">When the fault was first raised.</param>
/// <param name="IsLatched">True if the fault persists until explicitly cleared, even once the condition passes.</param>
public record FaultCode(
    string Code,
    FaultSeverity Severity,
    string Subsystem,
    string Message,
    DateTimeOffset RaisedAt,
    bool IsLatched = false);

/// <summary>Overall and component-level health of an asset.</summary>
/// <param name="Overall">Rolled-up status, normally the worst component status.</param>
/// <param name="Components">Per-subsystem statuses. Empty when the asset reports only an overall status.</param>
/// <param name="Faults">Active structured faults, most significant first.</param>
/// <param name="Summary">One-line operator-facing summary. Never parsed; render it, do not branch on it.</param>
public record HealthState(
    ComponentHealthStatus Overall,
    IReadOnlyList<ComponentHealth> Components,
    IReadOnlyList<FaultCode> Faults,
    string Summary);

/// <summary>Connectivity between the server and an asset.</summary>
/// <remarks>
/// Every quality metric is optional because partial data is the normal case: a mesh peer
/// may report a hop path but no latency, a satellite bearer latency but no RSSI. Absent
/// must stay distinguishable from zero, since zero loss and zero information are opposites.
/// </remarks>
/// <param name="Transport">Bearer currently carrying traffic for the asset.</param>
/// <param name="IsConnected">True when the bearer is up. Independent of <see cref="DataFreshness"/>.</param>
/// <param name="LatencyMs">Round-trip latency in milliseconds.</param>
/// <param name="PacketLossRatio">Observed loss as a fraction in 0–1.</param>
/// <param name="SignalDbm">Received signal strength in dBm.</param>
/// <param name="SignalQuality">Normalised link quality in 0–1, for bearers that do not expose dBm.</param>
/// <param name="MeshPath">Asset identifiers along the mesh route, source first, target last. Null when not mesh-routed.</param>
/// <param name="LastHeardAt">When traffic was last received from the asset.</param>
public record LinkState(
    LinkTransport Transport,
    bool IsConnected,
    double? LatencyMs = null,
    double? PacketLossRatio = null,
    double? SignalDbm = null,
    double? SignalQuality = null,
    IReadOnlyList<string>? MeshPath = null,
    DateTimeOffset? LastHeardAt = null);

/// <summary>What an asset is currently working on.</summary>
/// <remarks>
/// Vocabulary is deliberately neutral — route, waypoint, task, progress — so the same
/// record serves an air, ground or surface asset without either the server or the client
/// reaching for flight-only terminology.
/// </remarks>
/// <param name="Execution">Where the assigned plan has got to.</param>
/// <param name="RouteId">Identifier of the assigned route, or null when the asset is running a bare task.</param>
/// <param name="RouteName">Operator-facing route name, for display only.</param>
/// <param name="ActiveWaypointIndex">Zero-based index of the waypoint being driven to, or null when none is active.</param>
/// <param name="WaypointCount">Number of waypoints in the assigned route.</param>
/// <param name="TaskId">Identifier of the active task, or null when none is active.</param>
/// <param name="TaskKind">Kind of the active task (e.g. "survey", "relay", "inspect"), for display and filtering.</param>
/// <param name="ProgressFraction">Completion of the assigned plan as a fraction in 0–1.</param>
/// <param name="DistanceRemainingM">Path distance still to travel, in metres.</param>
/// <param name="TimeRemaining">Estimated time to completion at current progress.</param>
public record MissionState(
    MissionExecutionState Execution,
    string? RouteId = null,
    string? RouteName = null,
    int? ActiveWaypointIndex = null,
    int WaypointCount = 0,
    string? TaskId = null,
    string? TaskKind = null,
    double ProgressFraction = 0,
    double? DistanceRemainingM = null,
    TimeSpan? TimeRemaining = null);

/// <summary>Metadata describing what an asset is. Changes rarely.</summary>
/// <remarks>
/// Split from <see cref="AssetState"/> so a delta frame at stream rate never repeats
/// unchanged metadata. Clients cache descriptors by <paramref name="AssetId"/> and refresh
/// only when <paramref name="Revision"/> increases.
/// </remarks>
/// <param name="AssetId">Stable identifier, unique across the session and across domains.</param>
/// <param name="DisplayName">Operator-facing name.</param>
/// <param name="Domain">Medium the asset operates in.</param>
/// <param name="VehicleClass">Mobility archetype. <see cref="VehicleClass.Unspecified"/> for a fixed asset.</param>
/// <param name="MobilityModel">Identifier of the motion model driving the asset (e.g. "multirotor", "ackermann", "displacement-hull").</param>
/// <param name="AgencyId">Owning agency, for multi-agency scenarios. Null when unattributed.</param>
/// <param name="FleetId">Fleet or group the asset belongs to, for bulk selection and tasking.</param>
/// <param name="Vendor">Equipment maker, used for vendor-specific visual treatment.</param>
/// <param name="Model">Vendor's model designation.</param>
/// <param name="Capabilities">Everything the asset is declared able to do. Command validation gates on this.</param>
/// <param name="Dimensions">Physical envelope and mass.</param>
/// <param name="Motion">Speed, turn and station-keeping limits.</param>
/// <param name="VisualProfile">Key selecting the client's geometry and material set. Presentation only; never branch behaviour on it.</param>
/// <param name="Revision">Monotonic version, incremented whenever any other field changes.</param>
public record AssetDescriptor(
    string AssetId,
    string DisplayName,
    AssetDomain Domain,
    VehicleClass VehicleClass,
    string MobilityModel,
    string? AgencyId,
    string? FleetId,
    string? Vendor,
    string? Model,
    AssetCapability Capabilities,
    PhysicalDimensions Dimensions,
    MotionConstraints Motion,
    string VisualProfile,
    long Revision);

/// <summary>Everything about an asset that changes at stream rate.</summary>
/// <remarks>
/// <paramref name="SourceTime"/> and <paramref name="ReceiveTime"/> are both carried because
/// they answer different questions: how old the measurement is, and how long our own
/// pipeline took. Collapsing them hides transport delay. <paramref name="Pose"/> and
/// <paramref name="Twist"/> name their coordinate frame — a bare <c>[x, y, z]</c> is not a
/// valid position in this model.
/// </remarks>
/// <param name="AssetId">Identifier matching an <see cref="AssetDescriptor.AssetId"/>.</param>
/// <param name="SourceTime">When the asset itself observed this state.</param>
/// <param name="ReceiveTime">When the server received it.</param>
/// <param name="SequenceNumber">Monotonic per-asset counter; detects reordering and gaps.</param>
/// <param name="Freshness">How far this report can still be trusted.</param>
/// <param name="Pose">Frame-qualified position and orientation.</param>
/// <param name="Twist">Frame-qualified linear and angular velocity.</param>
/// <param name="OperationalState">Coarse domain-neutral state.</param>
/// <param name="Mode">Domain- or vendor-specific mode string for display (e.g. "loiter", "park", "station-keep").</param>
/// <param name="Power">Energy state.</param>
/// <param name="Health">Overall and component-level health.</param>
/// <param name="Link">Connectivity to the asset.</param>
/// <param name="Mission">Assigned plan and its progress, or null when nothing is assigned.</param>
/// <param name="DomainState">Typed domain extension, or null when the asset reports no domain detail.</param>
public record AssetState(
    string AssetId,
    DateTimeOffset SourceTime,
    DateTimeOffset ReceiveTime,
    ulong SequenceNumber,
    DataFreshness Freshness,
    FramedPose Pose,
    FramedTwist Twist,
    OperationalState OperationalState,
    string Mode,
    PowerState Power,
    HealthState Health,
    LinkState Link,
    MissionState? Mission,
    IAssetDomainState? DomainState);
