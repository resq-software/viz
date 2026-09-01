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
using System.Reflection;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Tests;

// Fixtures for SafeActionPolicyTests: literal descriptors and states, a recording asset, and two
// flat environments. Split from the assertions so the file that says what the policy must do
// stays readable; the suite's summary lives on the primary declaration in SafeActionPolicyTests.cs.
public partial class SafeActionPolicyTests
{
    /// <summary>Identifier every hand-built asset in this suite carries.</summary>
    private const string AssetId = "asset-1";

    /// <summary>Uncertainty growth a drifting hull reports, in metres per second.</summary>
    private const double VesselDriftMps = 0.9;

    /// <summary>Elevation of the dry plateau the rover fixtures stand on, in metres.</summary>
    private const double PlateauElevationM = 40.0;

    /// <summary>Water-surface elevation of the basin the vessel fixtures float on, in metres.</summary>
    private const double BasinSurfaceM = 10.0;

    /// <summary>Bed elevation of that basin, in metres. Deep enough that keel clearance is never the story.</summary>
    private const double BasinBedM = -20.0;

    private static readonly DateTimeOffset Epoch = new(2026, 4, 1, 6, 0, 0, TimeSpan.Zero);

    /// <summary>Every reason token the policy is allowed to return, read off the class itself.</summary>
    /// <remarks>
    /// Reflected rather than restated so a token added to <see cref="SafeActionReasons"/> without
    /// a home in the policy still counts as known, and a token invented at a call site does not.
    /// </remarks>
    private static readonly HashSet<string> KnownReasons =
        typeof(SafeActionReasons)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => f.GetRawConstantValue())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every command kind the executors can be handed.</summary>
    private static IEnumerable<AssetCommandKind> AllCommandKinds =>
        Enum.GetValues<AssetCommandKind>();

    /// <summary>A descriptor for one of the classes this build can spawn.</summary>
    /// <param name="vehicleClass">Class to describe.</param>
    /// <param name="assetId">Identifier to carry, for the tests that need two assets at once.</param>
    /// <returns>The real profile, so capability gating is the shipping one.</returns>
    private static AssetDescriptor Describe(VehicleClass vehicleClass, string? assetId = null) =>
        AssetProfiles.Create(assetId ?? AssetId, vehicleClass);

    /// <summary>An air state with the link-loss behaviour the air executor publishes.</summary>
    /// <param name="behaviour">Behaviour to advertise.</param>
    /// <param name="growthMps">Uncertainty growth rate to advertise, in metres per second.</param>
    /// <returns>The domain half of an air state.</returns>
    private static AirDomainState Air(
        LinkLossBehavior behaviour = LinkLossBehavior.ReturnToBase, double growthMps = 3.0) =>
        new(
            IsAirborne: true,
            HeadingRad: 0.0,
            CourseOverGroundRad: 0.0,
            GroundSpeedMps: 6.0,
            ClimbRateMps: 0.0,
            AltitudeAboveGroundM: 50.0,
            AltitudeAboveLaunchM: 50.0,
            AltitudeMslM: 90.0,
            WindSpeedMps: 3.0,
            WindDirectionRad: 0.0,
            LinkLossBehavior: behaviour,
            PositionUncertaintyGrowthMps: growthMps);

    /// <summary>A ground state that is stopped, so its uncertainty growth is genuinely zero.</summary>
    /// <param name="behaviour">Behaviour to advertise.</param>
    /// <param name="growthMps">Uncertainty growth rate to advertise, in metres per second.</param>
    /// <returns>The domain half of a ground state.</returns>
    private static GroundDomainState Ground(
        LinkLossBehavior behaviour = LinkLossBehavior.StopAndHold, double growthMps = 0.0) =>
        new(
            IsMoving: growthMps > 0.0,
            HeadingRad: 0.0,
            CourseOverGroundRad: 0.0,
            GroundSpeedMps: 0.0,
            SteeringAngleRad: 0.0,
            RollRad: 0.0,
            PitchRad: 0.0,
            TerrainElevationM: PlateauElevationM,
            SlopeRad: 0.0,
            SurfaceType: "bare-ground",
            TractionCoefficient: 0.8,
            DeratedSpeedLimitMps: 8.0,
            RolloverRisk: 0.0,
            IsImmobilised: false,
            LinkLossBehavior: behaviour,
            PositionUncertaintyGrowthMps: growthMps);

    /// <summary>A surface state whose uncertainty grows because a hull cannot stop drifting.</summary>
    /// <param name="behaviour">Behaviour to advertise.</param>
    /// <param name="growthMps">Uncertainty growth rate to advertise, in metres per second.</param>
    /// <returns>The domain half of a surface state.</returns>
    private static SurfaceDomainState Surface(
        LinkLossBehavior behaviour = LinkLossBehavior.DriftAndAlert, double growthMps = VesselDriftMps) =>
        new(
            HeadingRad: 0.0,
            CourseOverGroundRad: 0.0,
            SpeedOverGroundMps: 0.0,
            SpeedThroughWaterMps: 0.0,
            SurgeMps: 0.0,
            SwayMps: 0.0,
            YawRateRadPerSec: 0.0,
            WaterSurfaceElevationM: BasinSurfaceM,
            WaterDepthM: BasinSurfaceM - BasinBedM,
            DraftM: 0.8,
            UnderKeelClearanceM: BasinSurfaceM - BasinBedM - 0.8,
            HasUnsafeUnderKeelClearance: false,
            CurrentSpeedMps: 0.5,
            CurrentDirectionRad: 0.0,
            WindSpeedMps: 4.0,
            WindDirectionRad: 0.0,
            IsInsideWaterMask: true,
            LinkLossBehavior: behaviour,
            PositionUncertaintyGrowthMps: growthMps);

    /// <summary>Builds a published state around a domain extension.</summary>
    /// <param name="domainState">Typed domain half, or null to publish none.</param>
    /// <param name="operationalState">Coarse state; <see cref="OperationalState.Emergency"/> means latched.</param>
    /// <param name="connected">Whether the link reports itself up.</param>
    /// <param name="lowEnergy">Whether to raise the power fault every domain raises at its reserve.</param>
    /// <param name="externallyPowered">Whether the asset has no reserve of its own to spend.</param>
    /// <param name="positionSigmaM">One-sigma horizontal fix error to report, or null for no covariance.</param>
    /// <returns>A state the policy can be handed.</returns>
    private static AssetState State(
        IAssetDomainState? domainState,
        OperationalState operationalState = OperationalState.Active,
        bool connected = true,
        bool lowEnergy = false,
        bool externallyPowered = false,
        double? positionSigmaM = null)
    {
        FaultCode[] faults = lowEnergy
            ?
            [
                new FaultCode(
                    Code: "BATTERY_LOW",
                    Severity: FaultSeverity.Warning,
                    Subsystem: "power.battery",
                    Message: "Reserve spent.",
                    RaisedAt: Epoch),
            ]
            : [];

        return new AssetState(
            AssetId: AssetId,
            SourceTime: Epoch,
            ReceiveTime: Epoch,
            SequenceNumber: 1,
            Freshness: DataFreshness.Fresh,
            Pose: new FramedPose(
                CoordinateFrame.LocalEus,
                OriginId: null,
                Position: Vector3.Zero,
                Orientation: Quaternion.Identity,
                Covariance: Covariance(positionSigmaM)),
            Twist: new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),
            OperationalState: operationalState,
            Mode: "test",
            Power: new PowerState(
                Sources: [],
                PercentRemaining: lowEnergy ? 8.0 : 90.0,
                IsExternallyPowered: externallyPowered),
            Health: new HealthState(
                Overall: lowEnergy ? ComponentHealthStatus.Warning : ComponentHealthStatus.Nominal,
                Components: [],
                Faults: faults,
                Summary: lowEnergy ? "Reserve spent." : "Nominal."),
            Link: new LinkState(LinkTransport.Loopback, IsConnected: connected, LastHeardAt: Epoch),
            Mission: null,
            DomainState: domainState);
    }

    /// <summary>A 6x6 row-major pose covariance carrying one horizontal sigma.</summary>
    /// <param name="sigmaM">One-sigma horizontal error in metres, or null for no covariance at all.</param>
    /// <returns>Thirty-six entries, or null.</returns>
    private static double[]? Covariance(double? sigmaM)
    {
        if (sigmaM is not { } sigma)
        {
            return null;
        }

        var entries = new double[36];
        entries[0] = sigma * sigma;
        entries[14] = sigma * sigma;

        return entries;
    }

    /// <summary>An asset that executes nothing and remembers everything it was asked to do.</summary>
    /// <remarks>
    /// Stands in for a real executor wherever the question is what the <em>governor</em> issued
    /// rather than what an executor made of it.
    /// </remarks>
    private sealed class RecordingAsset : ISimulatedAsset
    {
        private readonly AssetCommandResult _applyResult;
        private readonly AssetState _state;

        /// <summary>Wraps a fixed descriptor, state and executor result.</summary>
        /// <param name="descriptor">What this asset claims to be.</param>
        /// <param name="state">What it publishes, on every capture.</param>
        /// <param name="applyResult">Result returned for every command; accepted when omitted.</param>
        public RecordingAsset(
            AssetDescriptor descriptor,
            AssetState state,
            AssetCommandResult? applyResult = null)
        {
            Descriptor = descriptor;
            _state = state;
            _applyResult = applyResult ?? AssetCommandResult.Accepted;
        }

        /// <summary>Commands handed to this asset, in order.</summary>
        public List<AssetCommandKind> Applied { get; } = [];

        /// <inheritdoc />
        public string AssetId => Descriptor.AssetId;

        /// <inheritdoc />
        public AssetDomain Domain => Descriptor.Domain;

        /// <inheritdoc />
        public Vector3 PositionEus => Vector3.Zero;

        /// <inheritdoc />
        public AssetDescriptor Descriptor { get; }

        /// <inheritdoc />
        public AssetState Capture(in AssetCaptureContext context) => _state;

        /// <inheritdoc />
        public AssetCommandResult Apply(in SimulatedAssetCommand command)
        {
            Applied.Add(command.Kind);

            return _applyResult;
        }

        /// <inheritdoc />
        public IReadOnlyList<AssetEvent> DrainEvents() => [];
    }

    /// <summary>A featureless dry plateau. Nothing here drifts.</summary>
    private sealed class Plateau : IEnvironmentSampler
    {
        /// <inheritdoc />
        public double SeaLevelM => PlateauElevationM - 100.0;

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <inheritdoc />
        public double GetElevation(double x, double z) => PlateauElevationM;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) =>
            new(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: Vector3.Zero,
                TerrainElevationM: PlateauElevationM,
                TerrainNormalEus: Vector3.UnitY,
                SurfaceMaterial: SurfaceType.BareGround,
                WaterSurfaceElevationM: null,
                BathymetricElevationM: null,
                Zones: []);
    }

    /// <summary>Deep, still water with a steady set. Everything floating on it drifts.</summary>
    private sealed class Basin : IEnvironmentSampler
    {
        /// <summary>Surface current in the scene frame, in metres per second.</summary>
        private static readonly Vector3 CurrentEus = new(0.5f, 0f, 0f);

        /// <inheritdoc />
        public double SeaLevelM => BasinSurfaceM;

        /// <inheritdoc />
        public IWindField Wind { get; } = new StillAir();

        /// <inheritdoc />
        public double GetElevation(double x, double z) => BasinBedM;

        /// <inheritdoc />
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        /// <inheritdoc />
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) =>
            new(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: CurrentEus,
                TerrainElevationM: BasinBedM,
                TerrainNormalEus: Vector3.UnitY,
                SurfaceMaterial: SurfaceType.Water,
                WaterSurfaceElevationM: BasinSurfaceM,
                BathymetricElevationM: BasinBedM,
                Zones: []);
    }

    /// <summary>Still, clear air. Wind is not what any of these tests is about.</summary>
    private sealed class StillAir : IWindField
    {
        /// <inheritdoc />
        public double Visibility => 1.0;

        /// <inheritdoc />
        public double Precipitation => 0.0;

        /// <inheritdoc />
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }
}
