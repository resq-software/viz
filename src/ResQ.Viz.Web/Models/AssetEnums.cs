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

namespace ResQ.Viz.Web.Models;

/// <summary>Physical medium an asset primarily operates in.</summary>
/// <remarks>
/// Numeric values are part of the wire contract — the client and any recorded frame log
/// persist the integer — so members may be appended but never renumbered. <c>Subsurface</c>
/// is a reserved value with no implementation this pass; reserving it now keeps adding
/// submersibles additive instead of a breaking renumber.
/// </remarks>
public enum AssetDomain
{
    /// <summary>Domain not reported. Planners must treat the asset as untaskable.</summary>
    Unspecified = 0,

    /// <summary>Operates in the air (rotary, fixed-wing or hybrid).</summary>
    Air = 1,

    /// <summary>Operates on land, constrained to the terrain surface.</summary>
    Ground = 2,

    /// <summary>Operates on the water surface.</summary>
    Surface = 3,

    /// <summary>Reserved for submersibles. Not implemented.</summary>
    Subsurface = 4,

    /// <summary>A stationary asset: a ground station, sensor mast or relay.</summary>
    Fixed = 5,
}

/// <summary>Mobility archetype of a vehicle, used to pick a physics model and a visual.</summary>
/// <remarks>
/// Ranges are grouped by domain (1–9 air, 10–19 ground, 20–29 surface, 30–39 subsurface)
/// with deliberate gaps so a new class slots into its own band without renumbering.
/// <c>Rov</c> and <c>Auv</c> are reserved values only.
/// </remarks>
public enum VehicleClass
{
    /// <summary>Class not reported.</summary>
    Unspecified = 0,

    /// <summary>Rotary-wing aircraft with three or more lift rotors.</summary>
    Multirotor = 1,

    /// <summary>Fixed-wing aircraft; requires forward airspeed to stay aloft.</summary>
    FixedWing = 2,

    /// <summary>Hybrid that takes off vertically and cruises on a wing.</summary>
    Vtol = 3,

    /// <summary>Car-like rover: steered front axle, no pivot turn, finite turn radius.</summary>
    AckermannRover = 10,

    /// <summary>Skid/differential rover: independent left and right drive, pivot capable.</summary>
    DifferentialRover = 11,

    /// <summary>Tracked rover: differential drive on continuous tracks.</summary>
    TrackedRover = 12,

    /// <summary>Legged rover: walking locomotion over discontinuous terrain.</summary>
    LeggedRover = 13,

    /// <summary>Powered surface vessel (displacement or planing hull).</summary>
    SurfaceVessel = 20,

    /// <summary>Wind-propelled surface vessel; motion is constrained by wind angle.</summary>
    Sailboat = 21,

    /// <summary>Reserved for remotely operated underwater vehicles. Not implemented.</summary>
    Rov = 30,

    /// <summary>Reserved for autonomous underwater vehicles. Not implemented.</summary>
    Auv = 31,
}

/// <summary>What an asset is declared able to do.</summary>
/// <remarks>
/// Behaviour is gated on declared capability rather than on a switch over
/// <see cref="VehicleClass"/>. That is what lets a command validator reject
/// <c>takeoff</c> on a rover with a machine-readable reason and no side effects,
/// and what lets a new vehicle class arrive without editing every call site.
/// </remarks>
[Flags]
public enum AssetCapability : ulong
{
    /// <summary>No declared capability. Every gated command is rejected.</summary>
    None = 0,

    /// <summary>Can be armed and disarmed as a distinct safety step.</summary>
    Arm = 1UL << 0,

    /// <summary>Can navigate to a horizontal position; altitude is not commandable.</summary>
    Navigate2D = 1UL << 1,

    /// <summary>Can navigate to a full three-dimensional position.</summary>
    Navigate3D = 1UL << 2,

    /// <summary>Can leave its support surface under its own power.</summary>
    Takeoff = 1UL << 3,

    /// <summary>Can perform a controlled descent onto a support surface.</summary>
    Land = 1UL << 4,

    /// <summary>Can drive backwards along its longitudinal axis.</summary>
    Reverse = 1UL << 5,

    /// <summary>Can rotate about its vertical axis at zero forward speed.</summary>
    PivotTurn = 1UL << 6,

    /// <summary>Can actively hold a position against wind or current.</summary>
    StationKeep = 1UL << 7,

    /// <summary>Can dock to and undock from a fixed or floating station.</summary>
    Dock = 1UL << 8,

    /// <summary>Accepts direct operator control input, bypassing autonomy.</summary>
    ManualControl = 1UL << 9,

    /// <summary>Can forward mesh traffic on behalf of other assets.</summary>
    MeshRelay = 1UL << 10,
}

/// <summary>Coarse operational state, common to every domain.</summary>
/// <remarks>
/// Deliberately domain-neutral: a vessel holding station, a rover parked on a slope and a
/// multirotor in a loiter are all <see cref="Holding"/>. Domain nuance belongs in
/// <see cref="AssetState.Mode"/> and in the domain state, not in this enum.
/// </remarks>
public enum OperationalState
{
    /// <summary>State could not be determined; usually paired with stale or lost data.</summary>
    Unknown,

    /// <summary>Known to the system but not powered or not reachable.</summary>
    Offline,

    /// <summary>Powered and reachable but not yet cleared to move.</summary>
    Standby,

    /// <summary>Cleared and idle; will accept a motion command immediately.</summary>
    Ready,

    /// <summary>Executing a route, task or operator command.</summary>
    Active,

    /// <summary>Holding position or pattern, waiting for the next instruction.</summary>
    Holding,

    /// <summary>Returning to a base, launch point or rally point.</summary>
    Returning,

    /// <summary>Landing, docking, parking or otherwise being recovered.</summary>
    Recovering,

    /// <summary>Executing an emergency behaviour; operator attention required.</summary>
    Emergency,

    /// <summary>A fault has taken the asset out of service until cleared.</summary>
    Faulted,
}

/// <summary>How much the most recent state report can still be trusted.</summary>
/// <remarks>
/// Separated from <see cref="LinkState.IsConnected"/> on purpose: a link can be up while
/// telemetry has stalled, and a link can be down while the last report is still fresh
/// enough to act on. The UI dims stale assets and stops offering commands on lost ones.
/// </remarks>
public enum DataFreshness
{
    /// <summary>Age of the report is unknown, e.g. no usable source timestamp.</summary>
    Unknown,

    /// <summary>Within the expected reporting interval.</summary>
    Fresh,

    /// <summary>Overdue but still usable; position uncertainty is growing.</summary>
    Stale,

    /// <summary>Too old to act on. Treat the position as an estimate only.</summary>
    Lost,
}

/// <summary>Where an asset's energy comes from.</summary>
/// <remarks>
/// Modelled as a kind per source rather than a battery-only assumption so a fuel-burning
/// vessel, a tethered relay and a hybrid rover all describe themselves honestly. Percent
/// remaining means different things per kind, which is why the kind travels with it.
/// </remarks>
public enum PowerSourceKind
{
    /// <summary>Source type not reported.</summary>
    Unknown,

    /// <summary>Rechargeable pack; percent is state of charge.</summary>
    Battery,

    /// <summary>Liquid or gaseous fuel; percent is tank level.</summary>
    Fuel,

    /// <summary>Engine or generator charging an onboard bus.</summary>
    Generator,

    /// <summary>Fuel cell converting stored fuel to electrical power.</summary>
    FuelCell,

    /// <summary>Photovoltaic input; contribution varies with conditions.</summary>
    Solar,

    /// <summary>Powered over a tether or umbilical; endurance is effectively unbounded.</summary>
    Tethered,

    /// <summary>Mains or shore power, typically while docked.</summary>
    External,
}

/// <summary>Health of an asset or of one of its components.</summary>
public enum ComponentHealthStatus
{
    /// <summary>No health report available for this component.</summary>
    Unknown,

    /// <summary>Operating within expected limits.</summary>
    Nominal,

    /// <summary>Working with reduced margin or reduced performance.</summary>
    Degraded,

    /// <summary>Approaching a limit; intervention is advisable.</summary>
    Warning,

    /// <summary>At or past a limit; the mission should be curtailed.</summary>
    Critical,

    /// <summary>Not functioning.</summary>
    Failed,

    /// <summary>Component is not fitted to this asset. Distinct from a failure.</summary>
    NotPresent,
}

/// <summary>Severity of a structured fault code.</summary>
public enum FaultSeverity
{
    /// <summary>Informational; recorded for context, no action implied.</summary>
    Info,

    /// <summary>Worth an operator's attention but not mission-limiting on its own.</summary>
    Warning,

    /// <summary>Mission-limiting; the asset cannot fully perform its task.</summary>
    Error,

    /// <summary>Safety-relevant; the asset should be recovered.</summary>
    Critical,
}

/// <summary>Bearer carrying an asset's telemetry and commands.</summary>
public enum LinkTransport
{
    /// <summary>Bearer not reported.</summary>
    Unknown,

    /// <summary>No bearer is currently carrying traffic for this asset.</summary>
    None,

    /// <summary>Multi-hop mesh radio; see <see cref="LinkState.MeshPath"/>.</summary>
    Mesh,

    /// <summary>Direct point-to-point radio.</summary>
    Radio,

    /// <summary>Cellular data.</summary>
    Cellular,

    /// <summary>Satellite backhaul; expect high latency.</summary>
    Satellite,

    /// <summary>Wireless LAN, typically close range or at base.</summary>
    Wifi,

    /// <summary>Wired tether or umbilical.</summary>
    Tether,

    /// <summary>Simulated in-process link. Present so simulated assets are not "unknown".</summary>
    Loopback,
}

/// <summary>Progress of the plan an asset is currently executing.</summary>
/// <remarks>
/// Names are domain-neutral by design: a route is a route whether it is flown, driven or
/// steamed. No flight-only vocabulary appears here.
/// </remarks>
public enum MissionExecutionState
{
    /// <summary>Nothing assigned.</summary>
    Idle,

    /// <summary>A route or task is assigned but execution has not begun.</summary>
    Planned,

    /// <summary>Actively executing.</summary>
    Executing,

    /// <summary>Paused by an operator; resumable from the same point.</summary>
    Paused,

    /// <summary>Interrupted by the system (fault, safety, link loss); resumable.</summary>
    Suspended,

    /// <summary>Finished successfully.</summary>
    Completed,

    /// <summary>Stopped before completion by operator or policy.</summary>
    Aborted,

    /// <summary>Stopped before completion because execution could not continue.</summary>
    Failed,
}
