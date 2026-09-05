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
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The contract of the coordinator that gives the ground and surface fleets somewhere to go.
/// </summary>
/// <remarks>
/// The defect these exist against was invisible for one reason: no test ever asserted that a rover
/// <i>moves</i>. Every ground suite checked spawn, capture, refusal and telemetry — all of which
/// pass perfectly for a vehicle parked at its spawn point for an entire session, which is what
/// every rover in every scenario did while the aircraft flew overhead. The first assertion below
/// is the load-bearing one; the rest guard the two ways a coordinator can be worse than none:
/// sending a platform where it cannot go, and overriding an operator.
/// </remarks>
public sealed class GroundSurfaceCoordinatorTests
{
    /// <summary>Drivable ground on the default preset — the ground-convoy column's own site.</summary>
    private static readonly Vector3 RoverSpawn = new(640f, 0f, 320f);

    /// <summary>Navigable water on the default preset — a flood-response ferry's own station.</summary>
    private static readonly Vector3 VesselSpawn = new(-100f, 0f, 0f);

    /// <summary>A ground asset is told to go somewhere, which nothing previously did.</summary>
    [Fact]
    public void A_Rover_Is_Tasked_Rather_Than_Left_At_Its_Spawn()
    {
        var (coordinator, room, log) = Fixture(rover: true, vessel: false);

        Pass(coordinator, room, log, simTime: 0.0);

        log.Should().ContainSingle("the one rover in the world should have been given a waypoint")
            .Which.Kind.Should().Be(
                AssetCommandKind.DriveTo, "a rover is tasked in its own domain's vocabulary");
    }

    /// <summary>A surface asset is tasked with the vessel verb, not the rover one.</summary>
    [Fact]
    public void A_Vessel_Is_Tasked_With_TransitTo()
    {
        var (coordinator, room, log) = Fixture(rover: false, vessel: true);

        Pass(coordinator, room, log, simTime: 0.0);

        log.Should().ContainSingle().Which.Kind.Should().Be(AssetCommandKind.TransitTo);
    }

    /// <summary>Air assets belong to the swarm, and this coordinator must not touch them.</summary>
    /// <remarks>
    /// Two coordinators tasking one aircraft would overwrite each other at 2 Hz, and it would fly
    /// to whichever waypoint happened to land second.
    /// </remarks>
    [Fact]
    public void An_Aircraft_Is_Left_To_The_Swarm_Coordinator()
    {
        var (coordinator, room, log) = Fixture(rover: false, vessel: false);
        room.AddDrone("air-1", new Vector3(0f, 60f, 0f));

        Pass(coordinator, room, log, simTime: 0.0);

        log.Should().BeEmpty("the air fleet has its own coordinator");
    }

    /// <summary>Every waypoint a rover is sent to is reachable from the one before it.</summary>
    /// <remarks>
    /// The property that separates this from a coordinator that merely emits coordinates. It is
    /// re-derived here from the public sweep rather than trusting the coordinator's own call, so a
    /// regression that stopped validating legs — or validated them from the wrong anchor — fails
    /// here even though every command would still be <i>accepted</i>: the asset-side gate probes
    /// only the destination, so a leg crossing a ravine is accepted and then never completed.
    /// </remarks>
    [Fact]
    public void Every_Leg_A_Rover_Is_Sent_Along_Is_Traversable_End_To_End()
    {
        var (coordinator, room, log) = Fixture(rover: true, vessel: false);
        var rover = Assets(room).OfType<GroundAsset>().Should().ContainSingle().Which;
        var sampler = room.UseAssets(w => w.Environment);

        // Expire each leg rather than driving it, so the whole circuit is observed without
        // depending on how far the platform gets in a test's worth of simulated time.
        var targets = new List<Vector3> { rover.PositionEus };
        for (int leg = 0; leg < 8; leg++)
        {
            Pass(
                coordinator, room, log,
                simTime: leg * (GroundSurfaceCoordinator.WaypointTimeoutSeconds + 1.0));
            if (log.Count > 0)
            {
                targets.Add(log[^1].Target!.Position);
                log.Clear();
            }
        }

        targets.Should().HaveCountGreaterThan(
            2, "a patrol is a circuit, not a single out-and-back leg");

        for (int i = 1; i < targets.Count; i++)
        {
            Traversability.CheckRoute(rover.Profile, targets[i - 1], targets[i], sampler)
                .IsTraversable.Should().BeTrue(
                    "leg {0} must be drivable end to end, not merely finish somewhere drivable", i);
        }
    }

    /// <summary>An operator who takes a rover over is not argued with two ticks later.</summary>
    [Fact]
    public void A_Manually_Held_Rover_Receives_Nothing()
    {
        var (coordinator, room, log) = Fixture(rover: true, vessel: false);
        var rover = Assets(room).Should().ContainSingle().Which;

        coordinator.DetachManual(rover.AssetId);
        Pass(coordinator, room, log, simTime: 0.0);

        log.Should().BeEmpty("a detached asset is the operator's, not the coordinator's");
    }

    /// <summary>Handing a rover back returns it to autonomous tasking.</summary>
    [Fact]
    public void Reattaching_A_Rover_Puts_It_Back_On_Patrol()
    {
        var (coordinator, room, log) = Fixture(rover: true, vessel: false);
        var rover = Assets(room).Should().ContainSingle().Which;

        coordinator.DetachManual(rover.AssetId);
        Pass(coordinator, room, log, simTime: 0.0);
        log.Should().BeEmpty();

        coordinator.AttachAuto(rover.AssetId);
        Pass(coordinator, room, log, simTime: 1.0);

        log.Should().ContainSingle("resuming autonomy re-tasks the asset");
    }

    /// <summary>A standing waypoint is not re-sent on every pass.</summary>
    /// <remarks>
    /// <c>DriveTo</c> hands the navigator a persistent goal. Re-issuing it at 2 Hz would fill the
    /// command audit with entries describing no new intent, and bury a genuine operator command
    /// among them.
    /// </remarks>
    [Fact]
    public void An_Unchanged_Waypoint_Is_Not_Reissued_Every_Pass()
    {
        var (coordinator, room, log) = Fixture(rover: true, vessel: false);

        Pass(coordinator, room, log, simTime: 0.0);
        int afterFirst = log.Count;
        Pass(coordinator, room, log, simTime: 0.5);
        Pass(coordinator, room, log, simTime: 1.0);

        log.Should().HaveCount(
            afterFirst, "the goal persists, so only a change of target is worth a command");
    }

    /// <summary>A contact diverts the nearest platform that can actually reach it.</summary>
    [Fact]
    public void A_Contact_Diverts_The_Nearest_Reachable_Asset()
    {
        var (coordinator, room, log) = Fixture(rover: true, vessel: false);
        var rover = Assets(room).Should().ContainSingle().Which;
        var sampler = room.UseAssets(w => w.Environment);

        // Just off the rover's own position, so reachability is a property of the fixture rather
        // than a guess about the terrain.
        var nearby = rover.PositionEus + new Vector3(25f, 0f, 0f);

        coordinator.DivertNearest(nearby, Assets(room), sampler).Should().Be(rover.AssetId);

        Pass(coordinator, room, log, simTime: 0.0);

        var target = log.Should().ContainSingle().Which.Target!.Position;
        Horizontal(target, nearby).Should().BeLessThan(
            1.0, "the diversion outranks the standing patrol until it is reached");
    }

    /// <summary>One contact does not drag the whole fleet onto the same point.</summary>
    [Fact]
    public void An_Already_Diverted_Asset_Is_Not_Retasked_By_A_Second_Contact()
    {
        var (coordinator, room, _) = Fixture(rover: true, vessel: false);
        var rover = Assets(room).Should().ContainSingle().Which;
        var sampler = room.UseAssets(w => w.Environment);

        coordinator.DivertNearest(rover.PositionEus + new Vector3(25f, 0f, 0f), Assets(room), sampler)
            .Should().Be(rover.AssetId);

        coordinator.DivertNearest(rover.PositionEus + new Vector3(-25f, 0f, 0f), Assets(room), sampler)
            .Should().BeNull("the only candidate is already answering a contact");
    }

    /// <summary>An unreachable contact diverts nobody rather than sending someone at a wall.</summary>
    [Fact]
    public void A_Contact_Nothing_Can_Reach_Diverts_Nobody()
    {
        var (coordinator, room, _) = Fixture(rover: true, vessel: false);
        var sampler = room.UseAssets(w => w.Environment);

        // Far outside the terrain the rover stands on, so no sweep can clear the route.
        var unreachable = new Vector3(90_000f, 0f, 90_000f);

        coordinator.DivertNearest(unreachable, Assets(room), sampler).Should().BeNull();
    }

    // ─── Fixture ────────────────────────────────────────────────────────────

    /// <summary>A room holding the requested platforms, plus a coordinator and a command log.</summary>
    /// <param name="rover">Whether to spawn a rover on drivable ground.</param>
    /// <param name="vessel">Whether to spawn a vessel on navigable water.</param>
    /// <returns>The coordinator under test, the room it drives, and the commands it issued.</returns>
    private static (GroundSurfaceCoordinator Coordinator, SimulationRoom Room,
        List<SimulatedAssetCommand> Log) Fixture(bool rover, bool vessel)
    {
        var room = new SimulationRoom(
            id: "coordinator-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        if (rover)
        {
            Spawn(room, "ugv-1", VehicleClass.AckermannRover, RoverSpawn,
                (env, plan) => new GroundAssetFactory(env).Create(plan));
        }

        if (vessel)
        {
            Spawn(room, "usv-1", VehicleClass.SurfaceVessel, VesselSpawn,
                (env, plan) => new SurfaceAssetFactory(env).Create(plan));
        }

        return (new GroundSurfaceCoordinator(), room, []);
    }

    /// <summary>Builds and registers one platform, both inside the room's own lock.</summary>
    private static void Spawn(
        SimulationRoom room,
        string assetId,
        VehicleClass vehicleClass,
        Vector3 siteEus,
        Func<IEnvironmentSampler, AssetSpawnPlan, ISimulatedAsset> build)
    {
        var plan = new AssetSpawnPlan(
            AssetId: assetId,
            VehicleClass: vehicleClass,
            Descriptor: AssetProfiles.Create(assetId, vehicleClass),
            PositionEus: siteEus,
            HeadingRad: 0.0);

        room.TrySpawnAsset(assetId, env => build(env, plan), out var reason)
            .Should().BeTrue("'{0}' must spawn; it was refused with '{1}'", assetId, reason);
    }

    /// <summary>Runs one coordination pass, recording every command it issues.</summary>
    private static void Pass(
        GroundSurfaceCoordinator coordinator,
        SimulationRoom room,
        List<SimulatedAssetCommand> log,
        double simTime) =>
        coordinator.Tick(
            simTime,
            Assets(room),
            room.UseAssets(w => w.Environment),
            command =>
            {
                log.Add(command);
                return room.UseAssets(w => w.SendCommand(in command));
            });

    private static IReadOnlyList<ISimulatedAsset> Assets(SimulationRoom room) =>
        room.UseAssets(world => world.Assets);

    private static double Horizontal(Vector3 a, Vector3 b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));
}
