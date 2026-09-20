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
using FluentAssertions;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// What an asset publishes for power before anything has stepped it.
/// </summary>
/// <remarks>
/// The two domains idle at different loads on purpose — a rover at 45 W, a vessel at a 60 W
/// hotel load — and the difference is otherwise invisible. The determinism hashes append only
/// the remaining percentage, not the draw or the endurance, so defaulting the idle load to zero
/// passes every other test in the suite while changing what the first frame of every asset
/// reports. Two mutations proved exactly that before these cases existed.
/// </remarks>
public sealed class IdleDrawTests
{
    /// <summary>A rover idles at its own load, not a default and not a vessel's.</summary>
    [Fact]
    public void A_Rover_Publishes_Its_Own_Idle_Draw_Before_The_First_Step()
    {
        Draw(Rover()).Should().Be(45.0);
    }

    /// <summary>A vessel idles at its hotel load.</summary>
    /// <remarks>
    /// Higher than a rover's, and for a reason that survives the vessel sitting still: a hull
    /// carries navigation lights, a sounder and a radio whether or not it is under way.
    /// </remarks>
    [Fact]
    public void A_Vessel_Publishes_Its_Hotel_Load_Before_The_First_Step()
    {
        Draw(Vessel()).Should().Be(60.0);
    }

    /// <summary>The two differ, which is the whole point of carrying both.</summary>
    [Fact]
    public void The_Two_Domains_Do_Not_Share_An_Idle_Load()
    {
        Draw(Rover()).Should().NotBe(Draw(Vessel()));
    }

    /// <summary>An idling asset reports an endurance, because it is consuming something.</summary>
    [Fact]
    public void An_Idling_Asset_Reports_A_Finite_Endurance()
    {
        Capture(Rover()).Power.RemainingTime.Should().NotBeNull();
        Capture(Vessel()).Power.RemainingTime.Should().NotBeNull();
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────

    private static readonly Flat Ground = new();

    private static double Draw(ISimulatedAsset asset) =>
        Capture(asset).Power.Sources.Should().ContainSingle().Subject.DrawWatts
            ?? throw new InvalidOperationException("A pack must publish a draw.");

    private static AssetState Capture(ISimulatedAsset asset) =>
        asset.Capture(new AssetCaptureContext(
            Environment: Ground,
            SimulationTimeSeconds: 0.0,
            Tick: 0,
            SourceTime: DateTimeOffset.UnixEpoch,
            ReceiveTime: DateTimeOffset.UnixEpoch,
            Origin: null));

    private static ISimulatedAsset Rover() =>
        new GroundAssetFactory(Ground).Create(new AssetSpawnPlan(
            AssetId: "rover-1",
            VehicleClass: VehicleClass.TrackedRover,
            Descriptor: AssetProfiles.Create("rover-1", VehicleClass.TrackedRover),
            PositionEus: Vector3.Zero,
            HeadingRad: 0.0));

    private static ISimulatedAsset Vessel() =>
        new SurfaceAssetFactory(Ground).Create(new AssetSpawnPlan(
            AssetId: "vessel-1",
            VehicleClass: VehicleClass.SurfaceVessel,
            Descriptor: AssetProfiles.Create("vessel-1", VehicleClass.SurfaceVessel),
            PositionEus: Vector3.Zero,
            HeadingRad: 0.0));

    /// <summary>Level ground under a deep, still basin, so neither domain is refused its spawn.</summary>
    private sealed class Flat : IEnvironmentSampler
    {
        public double SeaLevelM => 0.0;

        public IWindField Wind { get; } = new NoWind();

        public double GetElevation(double x, double z) => -20.0;

        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) => new(
            PositionEus: positionEus,
            WindEus: Vector3.Zero,
            Visibility: 1.0,
            Precipitation: 0.0,
            SurfaceCurrentEus: Vector3.Zero,
            TerrainElevationM: -20.0,
            TerrainNormalEus: Vector3.UnitY,
            SurfaceMaterial: SurfaceType.Water,
            WaterSurfaceElevationM: 0.0,
            BathymetricElevationM: -20.0,
            Zones: []);

        private sealed class NoWind : IWindField
        {
            public double Visibility => 1.0;

            public double Precipitation => 0.0;

            public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
        }
    }
}
