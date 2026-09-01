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

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Fixtures and helpers for <see cref="AssetContractTests"/>.
/// </summary>
/// <remarks>
/// Split out so the assertions file stays readable as a list of contracts. Every value here
/// is a literal: no clock, no unseeded randomness, so two calls a test apart build identical
/// records and a comparison against a pristine copy is a genuine no-side-effects check.
/// </remarks>
public partial class AssetContractTests
{
    /// <summary>Seed for the round-trip sweep. Fixed so a failure reproduces exactly.</summary>
    private const int RandomSeed = 20260830;

    /// <summary>Iterations in the round-trip sweep. Enough to vary every field, cheap to run.</summary>
    private const int SweepIterations = 32;

    private static readonly DateTimeOffset SourceTime = new(2026, 3, 14, 9, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReceiveTime = new(2026, 3, 14, 9, 15, 0, 120, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidationTime = new(2026, 3, 14, 9, 15, 1, TimeSpan.Zero);
    private static readonly Guid CommandId = new("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    /// <summary>
    /// Serializer options mirroring the wire path: SignalR's JSON hub protocol and ASP.NET's
    /// MVC formatters both use web defaults, so camelCase property names and case-insensitive
    /// reads are what the client actually sees.
    /// </summary>
    /// <remarks>
    /// If the frame path ever registers custom converters, register them here too — this is the
    /// single place these tests decide what "on the wire" means.
    /// </remarks>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private static string ToJson<T>(T value) => JsonSerializer.Serialize(value, WireOptions);

    private static T? FromJson<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, WireOptions);

    private static string[] Members<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>()
            .Select(value => $"{value}={Convert.ToUInt64(value, CultureInfo.InvariantCulture)}")
            .ToArray();

    private static string[] PropertyNames<T>() =>
        typeof(T).GetProperties().Select(property => property.Name).ToArray();

    private static IAssetDomainState DomainStateFor(string discriminator) => discriminator switch
    {
        AirDomainState.Discriminator => SampleAir(),
        GroundDomainState.Discriminator => SampleGround(),
        SurfaceDomainState.Discriminator => SampleSurface(),
        _ => throw new ArgumentOutOfRangeException(
            nameof(discriminator), discriminator, "No sample exists for this union case."),
    };

    private static AirDomainState SampleAir() => new(
        IsAirborne: true,
        HeadingRad: 0.5,
        CourseOverGroundRad: 0.62,
        GroundSpeedMps: 9.5,
        ClimbRateMps: -0.4,
        AltitudeAboveGroundM: 42.0,
        AltitudeAboveLaunchM: 45.5,
        AltitudeMslM: 158.25,
        WindSpeedMps: 3.2,
        WindDirectionRad: 2.1,
        LinkLossBehavior: LinkLossBehavior.ReturnToBase,
        PositionUncertaintyGrowthMps: 3.6,
        AirspeedMps: 10.1,
        IsWithinGeofence: true);

    private static GroundDomainState SampleGround() => new(
        IsMoving: false,
        HeadingRad: 1.25,
        CourseOverGroundRad: 1.25,
        GroundSpeedMps: 0.0,
        SteeringAngleRad: 0.0,
        RollRad: 0.03,
        PitchRad: -0.11,
        TerrainElevationM: 112.5,
        SlopeRad: 0.19,
        SurfaceType: "bare-ground",
        TractionCoefficient: 0.71,
        DeratedSpeedLimitMps: 2.8,
        RolloverRisk: 0.22,
        IsImmobilised: false,
        LinkLossBehavior: LinkLossBehavior.StopAndHold,
        PositionUncertaintyGrowthMps: 0.0,
        ImmobilisationReason: null);

    /// <summary>A vessel holding station badly, for the surface arm of the union.</summary>
    /// <remarks>
    /// The station-keeping target is left null on purpose: this fixture exercises the union's own
    /// round-trip, and a nested framed pose belongs to the coordinate test rather than this one.
    /// </remarks>
    private static SurfaceDomainState SampleSurface() => new(
        HeadingRad: 4.2,
        CourseOverGroundRad: 4.35,
        SpeedOverGroundMps: 2.6,
        SpeedThroughWaterMps: 2.9,
        SurgeMps: 2.85,
        SwayMps: 0.31,
        YawRateRadPerSec: -0.05,
        WaterSurfaceElevationM: 0.35,
        WaterDepthM: 3.1,
        DraftM: 1.7,
        UnderKeelClearanceM: 1.4,
        HasUnsafeUnderKeelClearance: false,
        CurrentSpeedMps: 0.8,
        CurrentDirectionRad: 1.9,
        WindSpeedMps: 5.5,
        WindDirectionRad: 2.4,
        IsInsideWaterMask: true,
        LinkLossBehavior: LinkLossBehavior.DriftAndAlert,
        PositionUncertaintyGrowthMps: 1.15,
        StationKeep: new StationKeepState(
            IsEngaged: true,
            Target: null,
            ToleranceRadiusM: 15.0,
            HeadingPolicy: StationKeepHeadingPolicy.IntoCurrent,
            HeadingSetpointRad: null,
            PositionErrorM: 21.5,
            IsDegraded: true,
            DegradedReason: "current-exceeds-thrust"),
        HeaveM: 0.22,
        RollRad: 0.06,
        PitchRad: -0.02);

    private static GroundDomainState RandomGround(Random random)
    {
        string[] surfaces = ["vegetation", "urban", "bare-ground", "water"];
        return new GroundDomainState(
            IsMoving: random.Next(2) == 1,
            HeadingRad: random.NextDouble() * Math.Tau,
            CourseOverGroundRad: random.NextDouble() * Math.Tau,
            GroundSpeedMps: random.NextDouble() * 8.0 - 2.0,
            SteeringAngleRad: random.NextDouble() * 0.8 - 0.4,
            RollRad: random.NextDouble() * 0.4 - 0.2,
            PitchRad: random.NextDouble() * 0.4 - 0.2,
            TerrainElevationM: random.NextDouble() * 400.0,
            SlopeRad: random.NextDouble() * 0.6,
            SurfaceType: surfaces[random.Next(surfaces.Length)],
            TractionCoefficient: random.NextDouble(),
            DeratedSpeedLimitMps: random.NextDouble() * 8.0,
            RolloverRisk: random.NextDouble(),
            IsImmobilised: random.Next(4) == 0,
            LinkLossBehavior: LinkLossBehavior.StopAndHold,
            PositionUncertaintyGrowthMps: random.NextDouble() * 0.2,
            ImmobilisationReason: random.Next(4) == 0 ? "slope-exceeded" : null);
    }

    private static DetectionV2State SampleDetection() => new(
        DetectionId: "det-1",
        Type: "survivor",
        Pose: SamplePose(),
        SourceAssetId: "rover-7",
        Confidence: 0.91,
        DetectedAt: SourceTime,
        SensorId: "eo-1",
        Label: "casualty, roadside");

    private static ExternalTrackState SampleTrack() => new(
        TrackId: "track-1",
        Classification: TrackClassification.Vessel,
        Pose: SamplePose(),
        Twist: SampleTwist(),
        Sources: [new TrackSource("ais-feed", TrackSourceKind.Transponder, SourceTime, Quality: 0.95)],
        Quality: new TrackQuality(Confidence: 0.9, PositionAccuracyM: 12.0, UpdateCount: 7, IsFused: false),
        LastUpdateTime: SourceTime,
        Freshness: DataFreshness.Fresh,
        Label: "MV Example",
        Transponder: new TransponderIdentity(TransponderKind.Ais, "244110352", CallSign: "EXAMPLE"));

    private static FramedPose SamplePose() => new(
        CoordinateFrame.LocalEus,
        OriginId: "origin-1",
        Position: new Vector3(25.0f, 1.5f, -40.0f),
        Orientation: Quaternion.Identity);

    private static FramedTwist SampleTwist() => new(
        CoordinateFrame.LocalEus,
        Linear: new Vector3(1.5f, 0f, -2.5f),
        Angular: Vector3.Zero,
        OriginId: "origin-1");

    private static AssetDescriptor DescriptorFor(
        string assetId,
        AssetDomain domain,
        VehicleClass vehicleClass,
        AssetCapability capabilities) =>
        new(
            AssetId: assetId,
            DisplayName: assetId,
            Domain: domain,
            VehicleClass: vehicleClass,
            MobilityModel: "test-model",
            AgencyId: null,
            FleetId: null,
            Vendor: null,
            Model: null,
            Capabilities: capabilities,
            Dimensions: new PhysicalDimensions(1.0, 1.0, 1.0, 10.0, 0.7),
            Motion: new MotionConstraints(0.0, 15.0, 0.0, true, 0.0, 0.0),
            VisualProfile: "test-visual",
            Revision: 1);

    private static AssetDescriptor ProfileDescriptor(string assetId, VehicleClass vehicleClass) =>
        AssetProfiles.Create(assetId, vehicleClass);

    private static AssetState StateFor(
        string assetId,
        OperationalState operationalState = OperationalState.Ready,
        DataFreshness freshness = DataFreshness.Fresh,
        IAssetDomainState? domainState = null) =>
        new(
            AssetId: assetId,
            SourceTime: SourceTime,
            ReceiveTime: ReceiveTime,
            SequenceNumber: 4_294_967_296UL,
            Freshness: freshness,
            Pose: SamplePose(),
            Twist: SampleTwist(),
            OperationalState: operationalState,
            Mode: "idle",
            Power: new PowerState([new PowerSource("pack-a", PowerSourceKind.Battery, PercentRemaining: 88.0)],
                PercentRemaining: 88.0),
            Health: new HealthState(ComponentHealthStatus.Nominal, [], [], "Nominal."),
            Link: new LinkState(LinkTransport.Loopback, IsConnected: true, LastHeardAt: ReceiveTime),
            Mission: null,
            DomainState: domainState);

    private static AssetCommandEnvelope EnvelopeFor(
        string assetId,
        string kind,
        CommandTarget? target = null,
        IReadOnlyDictionary<string, string>? parameters = null) =>
        new(
            CommandId: CommandId,
            AssetId: assetId,
            Kind: kind,
            IssuedAt: SourceTime,
            Deadline: null,
            IssuerId: "operator-1",
            ControlLeaseId: null,
            IdempotencyKey: "idem-1",
            Frame: CoordinateFrame.LocalEus,
            Target: target,
            Constraints: null,
            Parameters: parameters);

    private static CommandValidationResult Validate(
        AssetDescriptor descriptor,
        AssetCommandEnvelope envelope,
        OperationalState operationalState = OperationalState.Ready,
        DataFreshness freshness = DataFreshness.Fresh) =>
        CommandCatalog.Validate(
            envelope,
            descriptor,
            StateFor(descriptor.AssetId, operationalState, freshness),
            ValidationTime);
}
