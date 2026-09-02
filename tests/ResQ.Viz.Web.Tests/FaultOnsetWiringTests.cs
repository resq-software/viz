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
using ResQ.Simulation.Engine.Core;
using ResQ.Simulation.Engine.Entities;
using ResQ.Simulation.Engine.Environment;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The onset ledger is actually wired into a capturing asset, not merely unit-tested beside one.
/// </summary>
/// <remarks>
/// <see cref="FaultOnsetLedgerTests"/> proves the ledger keeps the right instant; this proves an
/// asset consults it. The two are worth separating because the failure modes are different: the
/// ledger can be perfect and still never be called, which is precisely the state the code was in
/// before — every domain's health builder stamped the capture's own source time and nothing
/// remembered anything.
/// </remarks>
public sealed class FaultOnsetWiringTests
{
    private const double Dt = 1.0 / 60.0;

    private const float CruiseAltitudeM = 60.0f;

    /// <summary>Ceiling on the settle loop so a drain-rate change fails loudly instead of hanging.</summary>
    private const int MaxDrainSteps = 400_000;

    private static readonly Vector3 Calm = Vector3.Zero;

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A drone's low-battery advisory dates from when the battery crossed, not from now.</summary>
    [Fact]
    public void AStandingAirFault_DoesNotRestampItselfOnEveryCapture()
    {
        var drone = new SimulatedDrone(
            "air-1", new Vector3(0f, CruiseAltitudeM, 0f), FlightModelType.Kinematic);
        drone.SendCommand(FlightCommand.GoTo(new Vector3(50_000f, CruiseAltitudeM, 0f)));
        var asset = new AirAsset(drone, AssetProfiles.Create(drone.Id, VehicleClass.Multirotor));
        var environment = new CalmAir();

        var steps = 0;
        while (drone.FlightModel.State.BatteryPercent >= 20.0 && steps < MaxDrainSteps)
        {
            drone.Step(Dt, Calm);
            steps++;
        }

        steps.Should().BeLessThan(
            MaxDrainSteps, "the battery has to reach the advisory for this test to mean anything");

        var first = asset.Capture(Context(environment, steps));

        for (var i = 0; i < 600; i++)
        {
            drone.Step(Dt, Calm);
            steps++;
        }

        var later = asset.Capture(Context(environment, steps));

        first.Health.Faults.Should().ContainSingle().Which.Code.Should().Be("BATTERY_LOW");
        later.Health.Faults.Should().ContainSingle().Which.RaisedAt.Should().Be(
            first.Health.Faults[0].RaisedAt,
            "ten seconds later the same advisory is ten seconds old, not brand new");

        later.SourceTime.Should().BeAfter(
            first.SourceTime, "the capture itself did advance, so this is not a frozen clock");
    }

    private static AssetCaptureContext Context(IEnvironmentSampler environment, int steps)
    {
        var simulationTime = steps * Dt;
        return new AssetCaptureContext(
            Environment: environment,
            SimulationTimeSeconds: simulationTime,
            Tick: steps,
            SourceTime: Epoch.AddSeconds(simulationTime),
            ReceiveTime: Epoch.AddSeconds(simulationTime),
            Origin: null);
    }

    /// <summary>Still air over flat bare ground; the drone's only changing quantity is charge.</summary>
    private sealed class CalmAir : IEnvironmentSampler
    {
        /// <inheritdoc/>
        public double SeaLevelM => -1000.0;

        /// <inheritdoc/>
        public IWindField Wind { get; } = new NoWind();

        /// <inheritdoc/>
        public double GetElevation(double x, double z) => 0.0;

        /// <inheritdoc/>
        public Vector3 GetTerrainNormal(double x, double z, double spacingM) => Vector3.UnitY;

        /// <inheritdoc/>
        public EnvironmentSample Sample(Vector3 positionEus, double normalSpacingM) =>
            new(
                PositionEus: positionEus,
                WindEus: Vector3.Zero,
                Visibility: 1.0,
                Precipitation: 0.0,
                SurfaceCurrentEus: Vector3.Zero,
                TerrainElevationM: 0.0,
                TerrainNormalEus: Vector3.UnitY,
                SurfaceMaterial: SurfaceType.BareGround,
                WaterSurfaceElevationM: null,
                BathymetricElevationM: null,
                Zones: []);
    }

    /// <summary>Dead calm, so nothing but the battery moves between the two captures.</summary>
    private sealed class NoWind : IWindField
    {
        /// <inheritdoc/>
        public double Visibility => 1.0;

        /// <inheritdoc/>
        public double Precipitation => 0.0;

        /// <inheritdoc/>
        public Vector3 GetWind(double x, double y, double z) => Vector3.Zero;
    }
}
