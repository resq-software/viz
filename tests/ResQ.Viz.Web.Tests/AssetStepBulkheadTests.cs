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
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// One asset's exception must cost that asset and nothing else.
/// </summary>
/// <remarks>
/// Before this bulkhead existed, a throw left <see cref="AssetWorld.Step"/>, left
/// <c>SimulationRoom.Tick</c>, and reached <c>SimulationManager.ExecuteAsync</c>, whose only
/// catch is <see cref="OperationCanceledException"/>. The hosted service faulted, and with no
/// <c>BackgroundServiceExceptionBehavior</c> configured the .NET default of <c>StopHost</c>
/// applies: the process exits and every room on the host goes with it. One vehicle's arithmetic
/// error was an outage for every connected client.
/// <para>
/// It was also silent. Nothing under <c>Services/Assets</c> held an <c>ILogger</c>, so the only
/// record was a host-level stack trace with no asset id and no tick number.
/// </para>
/// </remarks>
public sealed class AssetStepBulkheadTests
{
    private const double Dt = 1.0 / 60.0;

    /// <summary>A throwing asset does not stop the assets beside it.</summary>
    [Fact]
    public void One_Asset_Throwing_Does_Not_Stop_The_Others()
    {
        var world = World();
        var thrower = AddThrower(world, "ground-thrower");
        var healthy = AddRover(world, "ground-healthy", new Vector3(40f, 0f, 0f));

        var act = () => world.Step(Dt);

        act.Should().NotThrow("a bulkhead that rethrows is not a bulkhead");
        thrower.StepCalls.Should().Be(1);
        healthy.Should().NotBeNull("the healthy rover must still be registered");
        world.FaultedAssetIds.Should().ContainSingle().Which.Should().Be("ground-thrower");
    }

    /// <summary>A faulted asset is not asked again.</summary>
    /// <remarks>
    /// Its state is whatever a half-finished step left behind, so retrying asks the same broken
    /// arithmetic the same question sixty times a second and fills the log with it.
    /// </remarks>
    [Fact]
    public void A_Faulted_Asset_Is_Not_Stepped_Again()
    {
        var world = World();
        var thrower = AddThrower(world, "ground-thrower");

        for (var i = 0; i < 20; i++)
        {
            world.Step(Dt);
        }

        thrower.StepCalls.Should().Be(1, "it faulted on the first step and must not be retried");
    }

    /// <summary>It is still published, and published as faulted.</summary>
    /// <remarks>
    /// An asset reported Active while the world has stopped stepping it is worse than one
    /// reported broken: it looks like a vehicle that has merely stopped moving. The asset cannot
    /// report this itself — it threw out of its own step and does not know — so the world says it.
    /// <para>
    /// <see cref="OperationalState.Faulted"/> had no producer anywhere in the server before this.
    /// The client has had a colour for it in the minimap, a <c>crit</c> badge in the panel and an
    /// attention filter that selects it, for a state that could never arrive.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Faulted_Asset_Is_Still_Captured_And_Reads_Faulted()
    {
        var world = World();
        AddThrower(world, "ground-thrower");
        AddRover(world, "ground-healthy", new Vector3(40f, 0f, 0f));

        world.Step(Dt);

        var states = world.States;
        states.Should().HaveCount(2, "a broken vehicle is still a vehicle on the operator's map");

        states.Single(s => s.AssetId == "ground-thrower")
            .OperationalState.Should().Be(OperationalState.Faulted);

        states.Single(s => s.AssetId == "ground-healthy")
            .OperationalState.Should().NotBe(OperationalState.Faulted);
    }

    /// <summary>Its pose stays in the frozen peer buffer, so the others still avoid the wreck.</summary>
    [Fact]
    public void A_Faulted_Asset_Is_Still_An_Obstacle()
    {
        var world = World();
        AddThrower(world, "ground-thrower");

        world.Step(Dt);

        world.Assets.Should().Contain(a => a.AssetId == "ground-thrower",
            "removing it from the registry would take it out of the peer poses as well");
    }

    /// <summary>Removing the asset clears the fault, so a reused id is not born broken.</summary>
    [Fact]
    public void Removing_A_Faulted_Asset_Clears_Its_Fault()
    {
        var world = World();
        AddThrower(world, "ground-thrower");
        world.Step(Dt);
        world.FaultedAssetIds.Should().Contain("ground-thrower");

        world.RemoveAsset("ground-thrower").Should().BeTrue();

        world.FaultedAssetIds.Should().BeEmpty(
            "an id reused by a later spawn would otherwise inherit the fault and never step");
    }

    /// <summary>Cancellation is the host shutting down and belongs to the caller.</summary>
    /// <remarks>
    /// The one exception the bulkhead must not swallow. Catching it would turn a shutdown into a
    /// world full of assets silently marked broken.
    /// </remarks>
    [Fact]
    public void Cancellation_Is_Not_Contained()
    {
        var world = World();
        AddThrower(world, "ground-canceller", new OperationCanceledException());

        var act = () => world.Step(Dt);

        act.Should().Throw<OperationCanceledException>();
        world.FaultedAssetIds.Should().BeEmpty("a shutdown does not fault an asset");
    }

    // ─── Fixtures ───────────────────────────────────────────────────────────

    private static AssetWorld World() => new(
        NewTerrain(),
        new UpdatableWeatherSystem(new WeatherConfig()),
        new AssetWorldOptions(Simulation: new SimulationConfig { DeltaTime = Dt }));

    private static TerrainNoiseService NewTerrain()
    {
        var terrain = new TerrainNoiseService();
        terrain.SetPreset("alpine");
        return terrain;
    }

    private static ThrowingAsset AddThrower(
        AssetWorld world, string id, Exception? thrown = null)
    {
        var asset = new ThrowingAsset(id, thrown ?? new InvalidOperationException("integrator"));
        world.AddAsset(asset);
        return asset;
    }

    private static ISimulatedAsset AddRover(AssetWorld world, string id, Vector3 positionEus)
    {
        var asset = new GroundAssetFactory(world.Environment).Create(new AssetSpawnPlan(
            AssetId: id,
            VehicleClass: VehicleClass.TrackedRover,
            Descriptor: AssetProfiles.Create(id, VehicleClass.TrackedRover),
            PositionEus: positionEus,
            HeadingRad: 0.0));

        world.AddAsset(asset);
        return asset;
    }

    /// <summary>A ground asset whose step throws, and which counts how often it was asked.</summary>
    /// <remarks>
    /// Everything but <c>Step</c> behaves: it has to be capturable after it faults, because the
    /// point of the bulkhead is that a broken vehicle is still reported.
    /// </remarks>
    private sealed class ThrowingAsset(string assetId, Exception thrown) : IStepDrivenAsset
    {
        private readonly Exception _thrown = thrown;

        public int StepCalls { get; private set; }

        public string AssetId { get; } = assetId;

        public AssetDomain Domain => AssetDomain.Ground;

        public Vector3 PositionEus => Vector3.Zero;

        public AssetDescriptor Descriptor { get; } =
            AssetProfiles.Create(assetId, VehicleClass.TrackedRover);

        public void Step(in AssetStepContext context)
        {
            StepCalls++;
            throw _thrown;
        }

        public AssetState Capture(in AssetCaptureContext context) => new(
            AssetId: AssetId,
            SourceTime: context.SourceTime,
            ReceiveTime: context.ReceiveTime,
            SequenceNumber: 0,
            Freshness: DataFreshness.Fresh,
            Pose: new FramedPose(
                CoordinateFrame.LocalEus,
                context.Origin?.OriginId,
                PositionEus,
                Quaternion.Identity),
            Twist: new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),

            // Active on purpose: the world has to override this, and a stub that already
            // reported Faulted would let the override pass by agreeing with it.
            OperationalState: OperationalState.Active,
            Mode: "idle",
            Power: new PowerState([], PercentRemaining: 100.0),
            Health: new HealthState(
                ComponentHealthStatus.Nominal, [], [], "Nominal."),
            Link: new LinkState(LinkTransport.Loopback, IsConnected: true, LastHeardAt: context.ReceiveTime),
            Mission: null,
            DomainState: null);

        public AssetCommandResult Apply(in SimulatedAssetCommand command) =>
            AssetCommandResult.Rejected("command.unsupported");

        public IReadOnlyList<AssetEvent> DrainEvents() => [];
    }
}
