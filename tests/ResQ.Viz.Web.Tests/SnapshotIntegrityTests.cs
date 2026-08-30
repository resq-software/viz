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
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Guards the integrity of one v2 frame: that a snapshot is a single reading, that it does not
/// invent comms facts the server never measured, that per-asset events cannot pile up unbounded,
/// and that a domain this build cannot spawn is refused deliberately rather than accidentally.
/// </summary>
/// <remarks>
/// These are the invariants that fail silently. A torn frame still deserialises, a fabricated
/// partition flag still renders, an unbounded event list still answers every request, and a
/// missing factory still returns <em>something</em>. Nothing here asserts a status code and
/// stops: each case asserts the property that makes the response honest.
/// </remarks>
public sealed partial class SnapshotIntegrityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string DroneId = "uav-1";
    private const string RoverId = "ugv-1";

    /// <summary>Detection radius the fixture configures, in metres.</summary>
    /// <remarks>
    /// Far wider than the production default so the drone stays detectable for the whole of a
    /// long transit. A detection that drops out of range mid-run would make the cross-check
    /// vacuous exactly when the world is moving fastest, which is when it needs to bite.
    /// </remarks>
    private const float DetectionRangeM = 20_000f;

    /// <summary>Largest confidence disagreement tolerated between a frame's two halves.</summary>
    /// <remarks>
    /// The two halves are computed from the same <c>float</c> position, so an atomic frame agrees
    /// bit for bit and this is slack, not budget. One world step of drift moves the drone about
    /// 0.3 m, a confidence change near 1.5e-5 at <see cref="DetectionRangeM"/> — four orders of
    /// magnitude above this.
    /// </remarks>
    private const double ConfidenceTolerance = 1e-9;

    /// <summary>
    /// Mirror of <c>SimulationRoom</c>'s private event-buffer cap. Restated rather than imported
    /// so that raising the production cap has to be a deliberate edit here too.
    /// </summary>
    private const int MaxBufferedAssetEvents = 256;

    private static readonly Vector3 SpawnEus = new(0f, 50f, 0f);
    private static readonly Vector3 SurvivorEus = Vector3.Zero;

    /// <summary>A waypoint far enough north that a long run never arrives and stops moving.</summary>
    private static readonly Vector3 FarWaypoint = new(0f, 50f, -15_000f);

    private static readonly DateTimeOffset FixedInstant =
        new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly WebApplicationFactory<Program> _app;

    /// <summary>Binds the fixture that boots the real host, for the dependency-injection case.</summary>
    /// <param name="app">Factory hosting the application exactly as <c>Program.cs</c> wires it.</param>
    public SnapshotIntegrityTests(WebApplicationFactory<Program> app) => _app = app;

    // ─── D1: one snapshot is one reading ────────────────────────────────────

    /// <summary>
    /// The captured frame carries the drone projection its detections are derived from, so the
    /// two halves of a snapshot cannot come from different world steps.
    /// </summary>
    [Fact]
    public void CaptureAssetFrame_Carries_The_Drone_Projection_From_The_Same_Reading()
    {
        var room = CreateRoom();
        room.AddDrone(DroneId, SpawnEus);
        room.SendCommand(DroneId, FlightCommand.GoTo(FarWaypoint));
        Step(room, 600);

        var frame = room.CaptureAssetFrame();
        var capturedTick = frame.Transport.Tick;
        var assetPosition = frame.Assets.Single(a => a.AssetId == DroneId).Pose.Position;

        ScenePosition(frame.Drones.Should().ContainSingle().Which)
            .Should().Be(
                assetPosition,
                "the v1 projection and the v2 state describe the same drone in the same reading");

        // The window a second locked read would open. At the maximum run speed the tick loop
        // advances eight world steps per real tick, so this is the gap a naive snapshot spans.
        Step(room, 8);

        ScenePosition(room.GetSnapshot().Single()).Should().NotBe(
            assetPosition, "the world genuinely advanced inside the window, so a tear was possible");
        ScenePosition(frame.Drones.Single()).Should().Be(
            assetPosition, "the captured frame is a value, unaffected by anything after it");
        frame.Transport.Tick.Should().Be(capturedTick);
        room.TickCount.Should().Be(capturedTick + 8);
    }

    /// <summary>A snapshot's detections are derivable from the asset poses printed beside them.</summary>
    [Fact]
    public void Snapshot_Detections_Are_Derivable_From_Its_Own_Asset_Poses()
    {
        var (ctrl, room) = CreateController(BuilderWithSurvivor());
        room.AddDrone(DroneId, SpawnEus);
        room.SendCommand(DroneId, FlightCommand.GoTo(FarWaypoint));
        Step(room, 600);

        var snapshot = Snapshot(ctrl);
        var asset = snapshot.Assets.Single(a => a.AssetId == DroneId);
        var detection = snapshot.Detections.Should().ContainSingle().Which;

        detection.SourceAssetId.Should().Be(DroneId);
        detection.Confidence.Should().BeApproximately(
            ExpectedConfidence(asset.Pose.Position),
            ConfidenceTolerance,
            "a detection's range falls off from the pose the same frame publishes");
    }

    /// <summary>
    /// The endpoint stays self-consistent while the tick loop advances underneath it — the case
    /// two separate locked reads cannot survive.
    /// </summary>
    /// <remarks>
    /// A single-threaded test cannot distinguish one lock acquisition from two, because nothing
    /// runs in the gap. This one puts the world's own loop in that gap: a background stepper
    /// advances the room while the endpoint is read thousands of times, and every response must
    /// still agree with itself. With the reads split, a response assembled across a step pairs
    /// detections computed from one pose with an asset state carrying another.
    /// </remarks>
    /// <returns>A task that completes when the stepping thread has finished.</returns>
    [Fact]
    public async Task GetSnapshot_Stays_SelfConsistent_While_The_World_Advances()
    {
        const int steps = 12_000;
        const int maxSamples = 5_000;

        var (ctrl, room) = CreateController(BuilderWithSurvivor());
        room.AddDrone(DroneId, SpawnEus);
        room.SendCommand(DroneId, FlightCommand.GoTo(FarWaypoint));

        var startPosition = PositionOf(room);
        var stepper = Task.Run(() =>
        {
            for (var i = 0; i < steps; i++)
            {
                room.StepOnce();
            }
        });

        // Accumulate rather than assert per sample: thousands of FluentAssertions calls would
        // dominate the run, and the worst disagreement is the only one worth reporting.
        var samples = 0;
        var compared = 0;
        var worstError = 0.0;
        long worstTick = 0;

        while (!stepper.IsCompleted && samples < maxSamples)
        {
            var snapshot = Snapshot(ctrl);
            samples++;

            var asset = snapshot.Assets.FirstOrDefault(a => a.AssetId == DroneId);
            var detection = snapshot.Detections.FirstOrDefault(d => d.SourceAssetId == DroneId);
            if (asset is null || detection is null)
            {
                continue;
            }

            compared++;
            var error = Math.Abs(detection.Confidence - ExpectedConfidence(asset.Pose.Position));
            if (error > worstError)
            {
                worstError = error;
                worstTick = snapshot.Tick;
            }
        }

        await stepper;

        compared.Should().BeGreaterThan(0, "the run must actually have compared frames");
        PositionOf(room).Should().NotBe(
            startPosition, "the world advanced during sampling, so a torn frame was possible");
        worstError.Should().BeLessThan(
            ConfidenceTolerance,
            "the frame at tick {0} paired a detection with an asset pose from a different step",
            worstTick);
    }

    // ─── D2: the network fields are not complements ─────────────────────────

    /// <summary>A cut backhaul is reported as a cut backhaul, not as a split mesh.</summary>
    [Fact]
    public void Snapshot_Reports_A_Cut_Backhaul_Without_Claiming_The_Mesh_Split()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(DroneId, SpawnEus);
        room.SetBackhaulKilled(true);

        var network = Snapshot(ctrl).Network;

        network.Should().NotBeNull();
        network!.BackhaulAvailable.Should().BeFalse("the operations-centre uplink is down");
        network.IsPartitioned.Should().BeNull(
            "a backhaul cut says nothing about connectivity between the assets themselves");
        network.Partitions.Should().BeNull("this build computes no connected components");
    }

    /// <summary>Partition state is unknown whether or not the backhaul is up.</summary>
    /// <remarks>
    /// The regression this pins is the pair being exact complements: derive one from the other
    /// and a healthy mesh with a dead uplink is reported as a swarm that has split in two, which
    /// is a different incident with a different response.
    /// </remarks>
    [Fact]
    public void Snapshot_Partition_State_Does_Not_Track_The_Backhaul_Flag()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone(DroneId, SpawnEus);

        var healthy = Snapshot(ctrl).Network!;
        room.SetBackhaulKilled(true);
        var cut = Snapshot(ctrl).Network!;

        healthy.BackhaulAvailable.Should().BeTrue();
        cut.BackhaulAvailable.Should().BeFalse();
        healthy.IsPartitioned.Should().Be(cut.IsPartitioned, "only the backhaul changed");
        healthy.IsPartitioned.Should().BeNull();

        typeof(NetworkState).GetProperty(nameof(NetworkState.IsPartitioned))!
            .PropertyType.Should().Be(
                typeof(bool?),
                "unknown is a third state, and a plain bool can only fabricate one of the other two");
    }

    // ─── D3: asset events cannot pile up ────────────────────────────────────

    /// <summary>
    /// The tick loop drains the assets, so a session nobody is listening to still cannot grow an
    /// unbounded backlog.
    /// </summary>
    [Fact]
    public void Asset_Events_Are_Bounded_When_Nothing_Collects_Them()
    {
        const int rounds = 4 * MaxBufferedAssetEvents;

        var room = CreateRoom();
        var rover = AddChattyRover(room);

        for (var i = 0; i < rounds; i++)
        {
            room.StepOnce();
            room.CaptureAssetFrame();
        }

        rover.RaisedCount.Should().Be(rounds, "one event per capture, so the backlog is real");
        room.PendingAssetEventCount.Should().BeLessThanOrEqualTo(
            MaxBufferedAssetEvents, "the session buffer is capped");
        room.DroppedAssetEventCount.Should().BeGreaterThan(
            0, "the overflow is dropped explicitly and counted, not silently retained");

        var drained = room.DrainAssetEvents();

        drained.Count.Should().BeLessThanOrEqualTo(
            MaxBufferedAssetEvents,
            "an undrained room must not hand back every event it ever raised");
        drained.Should().BeInAscendingOrder(e => e.Tick, "delivery keeps raise order");
        room.DrainAssetEvents().Should().BeEmpty("draining is destructive");
    }

    /// <summary>A consumer that keeps up loses nothing: the cap is an overflow policy, not a filter.</summary>
    [Fact]
    public void Asset_Events_Are_Delivered_Complete_When_Drained_Every_Tick()
    {
        const int rounds = 100;

        var room = CreateRoom();
        AddChattyRover(room);

        var delivered = 0;
        for (var i = 0; i < rounds; i++)
        {
            room.StepOnce();
            room.CaptureAssetFrame();
            delivered += room.DrainAssetEvents().Count;
        }

        delivered.Should().Be(rounds);
        room.DroppedAssetEventCount.Should().Be(0);
        room.PendingAssetEventCount.Should().Be(0);
    }

    /// <summary>An event raised by a REST capture between two ticks is delivered on the next drain.</summary>
    /// <remarks>
    /// Draining sweeps the assets first for this reason. Without that sweep the event would wait
    /// for the following tick and arrive a frame late, which is indistinguishable from a dropped
    /// event to anything counting them.
    /// </remarks>
    [Fact]
    public void Asset_Events_Raised_Between_Ticks_Are_Not_Held_Back()
    {
        var room = CreateRoom();
        AddChattyRover(room);

        room.CaptureAssetFrame();

        room.DrainAssetEvents().Should().ContainSingle()
            .Which.AssetId.Should().Be(RoverId);
    }

    /// <summary>Resetting a session discards its buffered events along with the world they describe.</summary>
    [Fact]
    public void Resetting_A_Session_Clears_The_Event_Buffer()
    {
        var room = CreateRoom();
        AddChattyRover(room);
        room.CaptureAssetFrame();
        room.StepOnce();
        room.PendingAssetEventCount.Should().BeGreaterThan(0);

        room.Reset();

        room.PendingAssetEventCount.Should().Be(0);
        room.DroppedAssetEventCount.Should().Be(0);
        room.DrainAssetEvents().Should().BeEmpty();
    }

    // ─── D4: a class with no registered model is refused deliberately ───────

    /// <summary>
    /// A class with a profile but no motion model available to the controller is refused with a
    /// reason code that names the gap.
    /// </summary>
    /// <remarks>
    /// About the mechanism, not about any one domain: the controller here is built with no
    /// factories at all, so every non-air class takes the same refusal path whatever the wired
    /// host happens to register. That separation is the point — availability is a deployment
    /// fact, asserted against the real host by
    /// <see cref="The_Wired_Application_Registers_A_Ground_Model_And_No_Surface_Model"/>, while
    /// this case pins what happens when a model is genuinely absent. The rover rows stay after
    /// the ground work landed because a deployment that ships no ground model must still refuse
    /// this way rather than throwing.
    /// </remarks>
    /// <param name="vehicleClass">A class no factory on this controller can build.</param>
    [Theory]
    [InlineData(VehicleClass.AckermannRover)]
    [InlineData(VehicleClass.DifferentialRover)]
    [InlineData(VehicleClass.TrackedRover)]
    [InlineData(VehicleClass.SurfaceVessel)]
    public void Spawning_A_Class_With_No_Registered_Model_Is_Refused_With_A_Reason_Code(
        VehicleClass vehicleClass)
    {
        var (ctrl, room) = CreateController();

        var result = ctrl.SpawnAsset(new AssetSpawnRequest(
            vehicleClass,
            new FramedPose(CoordinateFrame.LocalEus, null, new Vector3(5f, 0f, 5f), Quaternion.Identity),
            AssetId: "asset-1"));

        var problem = result.Should().BeOfType<ObjectResult>().Which;
        problem.StatusCode.Should().Be(
            StatusCodes.Status501NotImplemented,
            "a missing motion model is neither the caller's fault nor a server error");

        problem.Value.Should().BeOfType<CommandProblemDetails>()
            .Which.Code.Should().Be(AssetProblems.MobilityModelUnavailable);

        room.CaptureAssetFrame().Assets.Should().BeEmpty("a refusal leaves no half-built asset behind");
    }

    /// <summary>
    /// The wired application can build every ground class and no surface class, which is what
    /// decides which domains the refusal above actually applies to at runtime.
    /// </summary>
    /// <remarks>
    /// Asserted against the real host rather than a hand-built controller, because availability
    /// is a composition-root fact: what <c>Program.cs</c> registers is the whole of what can be
    /// spawned. This case previously required the registration to be empty, which was the honest
    /// contract while no motion model existed; it now pins the deliberate replacement — ground
    /// available, surface not yet — so that enabling surface has to come with an update here
    /// rather than a 501 quietly becoming a 201 nobody noticed.
    /// <para>
    /// <see cref="VehicleClass.LeggedRover"/> is included on purpose. The ground factory answers
    /// for it because it has a motion model, while <c>AssetProfiles</c> has no row for it, so the
    /// API refuses it earlier and for a different reason. Asserting the factory's answer here
    /// keeps those two facts visibly separate.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Wired_Application_Registers_A_Ground_Model_And_No_Surface_Model()
    {
        using var scope = _app.Services.CreateScope();

        var factories = scope.ServiceProvider.GetServices<IAssetFactory>().ToList();

        factories.Should().ContainSingle("this build ships exactly one non-air motion model");

        var factory = factories[0];
        factory.CanCreate(VehicleClass.AckermannRover).Should().BeTrue();
        factory.CanCreate(VehicleClass.DifferentialRover).Should().BeTrue();
        factory.CanCreate(VehicleClass.TrackedRover).Should().BeTrue();
        factory.CanCreate(VehicleClass.LeggedRover).Should().BeTrue();

        factory.CanCreate(VehicleClass.SurfaceVessel).Should().BeFalse(
            "the surface domain lands in later work and must still refuse with "
            + "MobilityModelUnavailable until it does");

        factory.CanCreate(VehicleClass.Multirotor).Should().BeFalse(
            "air assets belong to the flight world, which AddDrone is the only way into");
    }

    /// <summary>
    /// The wired scenario loader spawns from the same motion models the spawn endpoint does,
    /// rather than from a second list of its own.
    /// </summary>
    /// <remarks>
    /// <see cref="ScenarioService"/> accepts its factories and falls back to a built-in default
    /// when handed none, which is what its own unit tests rely on. The composition root must not
    /// take that fallback: a preset and <c>POST /api/v2/sim/assets</c> place assets in the same
    /// world, so a class one can build and the other cannot is a contradiction an operator sees
    /// as a preset that silently comes up short. Pinned by identity rather than by count — the
    /// two must be the <em>same</em> instances, because two equivalent lists today is exactly how
    /// they come to disagree the first time one of them gains a row.
    /// <para>
    /// This is the assertion the earlier wiring would have failed: the loader held its own copy of
    /// the registry, so the first factory registered without a matching edit there would have been
    /// spawnable through the API and skipped by every preset, with nothing but a log line saying
    /// so.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Wired_Scenario_Loader_Spawns_From_The_Registered_Motion_Models()
    {
        using var scope = _app.Services.CreateScope();

        var registered = scope.ServiceProvider.GetServices<IAssetFactory>().ToList();
        var loader = scope.ServiceProvider.GetRequiredService<ScenarioService>();

        loader.AssetFactories.Should().BeEquivalentTo(
            registered,
            options => options.WithStrictOrdering().ComparingByValue<IAssetFactory>(),
            "the scenario loader must spawn through the container's models, not a second list");
    }
}
