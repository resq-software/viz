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

using ResQ.Viz.Web.Models;

namespace ResQ.Viz.Web.Services.Assets;

/// <summary>Default descriptor for each vehicle class this simulation can spawn.</summary>
/// <remarks>
/// One table, so capabilities, envelope and motion limits for a class are decided in exactly
/// one place. Command validation gates on the capability flags these hand out, which makes the
/// table a safety-relevant contract rather than presentation metadata: adding
/// <see cref="AssetCapability.Takeoff"/> to a rover here would make <c>takeoff</c> a valid
/// command for a rover everywhere at once.
/// <para>
/// Classes with no dynamics yet — fixed-wing, VTOL, legged, sailboat, and the reserved
/// subsurface classes — deliberately have no profile. A missing profile throws rather than
/// falling back to a generic one, because a wrong capability set fails silently at the
/// validator and a wrong motion envelope fails silently at the task allocator.
/// </para>
/// </remarks>
public static class AssetProfiles
{
    /// <summary>Mean power a multirotor draws to hold a hover, in watts.</summary>
    /// <remarks>Holding station is not free for an air asset, which is what this figure records.</remarks>
    private const double MultirotorHoverPowerW = 180.0;

    /// <summary>Whether a default profile exists for <paramref name="vehicleClass"/>.</summary>
    /// <param name="vehicleClass">Class to test.</param>
    /// <returns><see langword="true"/> when the class can be spawned.</returns>
    public static bool IsSupported(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.Multirotor => true,
        VehicleClass.AckermannRover => true,
        VehicleClass.DifferentialRover => true,
        VehicleClass.TrackedRover => true,
        VehicleClass.SurfaceVessel => true,
        _ => false,
    };

    /// <summary>Medium a vehicle class operates in.</summary>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>The class's domain.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The class has no profile; see <see cref="IsSupported"/>.</exception>
    public static AssetDomain DomainFor(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.Multirotor => AssetDomain.Air,
        VehicleClass.AckermannRover or VehicleClass.DifferentialRover
            or VehicleClass.TrackedRover => AssetDomain.Ground,
        VehicleClass.SurfaceVessel => AssetDomain.Surface,
        _ => throw Unsupported(vehicleClass),
    };

    /// <summary>Everything a vehicle class is declared able to do.</summary>
    /// <remarks>
    /// A displacement-hull vessel declares neither <see cref="AssetCapability.Takeoff"/> nor
    /// <see cref="AssetCapability.Land"/> — it has no support surface to leave or return to —
    /// and no <see cref="AssetCapability.StationKeep"/>, because a single-screw hull with a
    /// rudder loses steerage below its minimum speed and physically cannot hold a spot. That
    /// omission is the point: it is what makes "wait here" a rejectable command rather than one
    /// the vessel accepts and then silently drifts away from.
    /// <para>
    /// A rover declares <see cref="AssetCapability.StationKeep"/> because holding a position on
    /// land costs it nothing — it simply stops — and <see cref="AssetCapability.Land"/> because
    /// that is the capability <c>park</c> gates on: securing onto a support surface.
    /// </para>
    /// </remarks>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>The class's declared capabilities.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The class has no profile; see <see cref="IsSupported"/>.</exception>
    public static AssetCapability CapabilitiesFor(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.Multirotor =>
            AssetCapability.Arm | AssetCapability.Navigate2D | AssetCapability.Navigate3D
            | AssetCapability.Takeoff | AssetCapability.Land | AssetCapability.StationKeep
            | AssetCapability.ManualControl | AssetCapability.MeshRelay,

        VehicleClass.AckermannRover =>
            AssetCapability.Arm | AssetCapability.Navigate2D | AssetCapability.Reverse
            | AssetCapability.Land | AssetCapability.StationKeep
            | AssetCapability.ManualControl | AssetCapability.MeshRelay,

        // Skid and tracked platforms add a pivot turn: they can rotate at zero forward speed,
        // which an Ackermann steering geometry cannot.
        VehicleClass.DifferentialRover or VehicleClass.TrackedRover =>
            AssetCapability.Arm | AssetCapability.Navigate2D | AssetCapability.Reverse
            | AssetCapability.PivotTurn | AssetCapability.Land | AssetCapability.StationKeep
            | AssetCapability.ManualControl | AssetCapability.MeshRelay,

        VehicleClass.SurfaceVessel =>
            AssetCapability.Arm | AssetCapability.Navigate2D | AssetCapability.Reverse
            | AssetCapability.Dock | AssetCapability.ManualControl | AssetCapability.MeshRelay,

        _ => throw Unsupported(vehicleClass),
    };

    /// <summary>Physical envelope and mass of a vehicle class.</summary>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>The class's dimensions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The class has no profile; see <see cref="IsSupported"/>.</exception>
    public static PhysicalDimensions DimensionsFor(VehicleClass vehicleClass) => vehicleClass switch
    {
        // Mass matches the SDK's default quadrotor mass, so the descriptor and the flight model
        // do not disagree about the same airframe.
        VehicleClass.Multirotor => new PhysicalDimensions(0.90, 0.90, 0.35, 2.5, 0.65),
        VehicleClass.AckermannRover => new PhysicalDimensions(2.20, 1.40, 1.10, 320.0, 1.30),
        VehicleClass.DifferentialRover => new PhysicalDimensions(1.20, 0.90, 0.70, 85.0, 0.80),
        VehicleClass.TrackedRover => new PhysicalDimensions(1.60, 1.10, 0.90, 240.0, 1.00),
        VehicleClass.SurfaceVessel => new PhysicalDimensions(6.50, 2.30, 2.00, 1450.0, 3.50),
        _ => throw Unsupported(vehicleClass),
    };

    /// <summary>Speed, turn and station-keeping limits of a vehicle class.</summary>
    /// <remarks>
    /// The vessel is the interesting row. Its minimum speed is non-zero because below roughly
    /// half a metre per second the rudder has no authority, and its passive drift is non-zero
    /// because with propulsion lost it does not stop — it moves with the current. Every other
    /// class here can come to a genuine standstill and hold it for free.
    /// </remarks>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>The class's motion constraints.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The class has no profile; see <see cref="IsSupported"/>.</exception>
    public static MotionConstraints MotionFor(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.Multirotor => new MotionConstraints(
            MinSpeedMps: 0.0, MaxSpeedMps: 18.0, MinTurnRadiusM: 0.0,
            CanStationKeep: true, PassiveDriftMps: 0.0,
            StationKeepCostW: MultirotorHoverPowerW),

        VehicleClass.AckermannRover => new MotionConstraints(
            MinSpeedMps: 0.0, MaxSpeedMps: 8.0, MinTurnRadiusM: 3.2,
            CanStationKeep: true, PassiveDriftMps: 0.0, StationKeepCostW: 0.0),

        VehicleClass.DifferentialRover => new MotionConstraints(
            MinSpeedMps: 0.0, MaxSpeedMps: 5.0, MinTurnRadiusM: 0.0,
            CanStationKeep: true, PassiveDriftMps: 0.0, StationKeepCostW: 0.0),

        VehicleClass.TrackedRover => new MotionConstraints(
            MinSpeedMps: 0.0, MaxSpeedMps: 3.5, MinTurnRadiusM: 0.0,
            CanStationKeep: true, PassiveDriftMps: 0.0, StationKeepCostW: 0.0),

        VehicleClass.SurfaceVessel => new MotionConstraints(
            MinSpeedMps: 0.6, MaxSpeedMps: 6.0, MinTurnRadiusM: 12.0,
            CanStationKeep: false, PassiveDriftMps: 0.4, StationKeepCostW: 0.0),

        _ => throw Unsupported(vehicleClass),
    };

    /// <summary>Identifier of the motion model that drives a vehicle class.</summary>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>A stable lower-case model key.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The class has no profile; see <see cref="IsSupported"/>.</exception>
    public static string MobilityModelFor(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.Multirotor => "multirotor",
        VehicleClass.AckermannRover => "ackermann",
        VehicleClass.DifferentialRover => "differential",
        VehicleClass.TrackedRover => "tracked",
        VehicleClass.SurfaceVessel => "displacement-hull",
        _ => throw Unsupported(vehicleClass),
    };

    /// <summary>Key selecting the client's geometry and material set.</summary>
    /// <remarks>Presentation only. Never branch behaviour on it — that is what capabilities are for.</remarks>
    /// <param name="vehicleClass">Class to resolve.</param>
    /// <returns>A stable lower-case visual key.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The class has no profile; see <see cref="IsSupported"/>.</exception>
    public static string VisualProfileFor(VehicleClass vehicleClass) => vehicleClass switch
    {
        VehicleClass.Multirotor => "quadrotor",
        VehicleClass.AckermannRover => "rover-ackermann",
        VehicleClass.DifferentialRover => "rover-differential",
        VehicleClass.TrackedRover => "rover-tracked",
        VehicleClass.SurfaceVessel => "vessel-displacement",
        _ => throw Unsupported(vehicleClass),
    };

    /// <summary>Builds the default descriptor for a newly spawned asset.</summary>
    /// <remarks>
    /// <paramref name="vendor"/> and <paramref name="model"/> are normalised so an empty string
    /// becomes <see langword="null"/>. That preserves the v1 behaviour exactly: spawning with an
    /// empty vendor stored nothing and reported no vendor, and a client can only tell
    /// "unattributed" from "attributed to the empty string" if the two stay distinct.
    /// </remarks>
    /// <param name="assetId">Stable identifier, unique across the session and across domains.</param>
    /// <param name="vehicleClass">Mobility archetype to build a descriptor for.</param>
    /// <param name="displayName">Operator-facing name, or null to use <paramref name="assetId"/>.</param>
    /// <param name="vendor">Equipment maker, or null/empty when unattributed.</param>
    /// <param name="model">Vendor's model designation, or null/empty when unknown.</param>
    /// <param name="agencyId">Owning agency, or null when unattributed.</param>
    /// <param name="fleetId">Fleet or group, or null when ungrouped.</param>
    /// <param name="revision">Monotonic descriptor version. Starts at 1 and increments on any later change.</param>
    /// <returns>A fully populated descriptor.</returns>
    /// <exception cref="ArgumentException"><paramref name="assetId"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The class has no profile; see <see cref="IsSupported"/>.</exception>
    public static AssetDescriptor Create(
        string assetId,
        VehicleClass vehicleClass,
        string? displayName = null,
        string? vendor = null,
        string? model = null,
        string? agencyId = null,
        string? fleetId = null,
        long revision = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);

        return new AssetDescriptor(
            AssetId: assetId,
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? assetId : displayName,
            Domain: DomainFor(vehicleClass),
            VehicleClass: vehicleClass,
            MobilityModel: MobilityModelFor(vehicleClass),
            AgencyId: NullIfEmpty(agencyId),
            FleetId: NullIfEmpty(fleetId),
            Vendor: NullIfEmpty(vendor),
            Model: NullIfEmpty(model),
            Capabilities: CapabilitiesFor(vehicleClass),
            Dimensions: DimensionsFor(vehicleClass),
            Motion: MotionFor(vehicleClass),
            VisualProfile: VisualProfileFor(vehicleClass),
            Revision: revision);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static ArgumentOutOfRangeException Unsupported(VehicleClass vehicleClass) =>
        new(nameof(vehicleClass), vehicleClass,
            $"No asset profile is defined for vehicle class '{vehicleClass}'.");
}
