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
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Simulation.Engine.Environment;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The safe-action layer's call paths, as opposed to its verdicts.
/// </summary>
/// <remarks>
/// <c>SafeActionPolicyTests</c> already establishes that the policy reaches the right conclusion
/// from a given state, and it does so against literals. Every case here is about the other half —
/// whether the conclusion is ever reached from the running world, and whether anything downstream
/// is still standing when it is. That half is where this layer's defects have lived: a link an
/// operator cut that no capture reported, a sweep whose documented cadence was not the one the
/// code kept, a governor that outlived the asset it was about, a position gate no command path
/// consulted, and a failsafe the swarm coordinator overwrote half a second after it fired.
/// <para>
/// Cases are therefore written against a real <see cref="AssetWorld"/> or a real
/// <see cref="SimulationRoom"/>, never a stub. A stubbed world would have passed all five while
/// the system did none of it, which is precisely how the gap arose.
/// </para>
/// </remarks>
public sealed class SafeActionWiringTests
{
    /// <summary>World steps between safe-action sweeps, mirroring the world's own constant.</summary>
    private const int SweepTicks = 60;

    /// <summary>Timestep every world in this suite integrates at, in seconds.</summary>
    private const double TimestepSeconds = 1.0 / 60.0;

    private const string RoverId = "wiring-rover";
    private const string DroneId = "wiring-drone";

    /// <summary>Where a room's drone is launched from, and therefore where it returns to.</summary>
    private static readonly Vector3 DroneLaunchEus = new(0f, 60f, 0f);

    /// <summary>Somewhere flat and unremarkable for a rover to sit.</summary>
    private static readonly Vector3 RoverSpawnEus = new(-475f, 0f, -375f);

    // ── B1: which clock the sweep keeps ─────────────────────────────────────

    /// <summary>A step the asset pass skipped still owes the sweep it was carrying.</summary>
    /// <remarks>
    /// The world counts a tick for every attempted step but runs the asset pass only for a
    /// positive timestep, so a modulo gate on the tick counter drops the sweep outright whenever
    /// the skipped step is the sixtieth — and then waits a further full second. Counting steps
    /// since the last sweep instead makes the cadence a property of work actually done.
    /// </remarks>
    [Fact]
    public void A_Skipped_Step_Does_Not_Lose_The_Sweep_It_Was_Carrying()
    {
        var world = World();
        AddRover(world);

        Advance(world, SweepTicks - 1);
        world.SafeActionFor(RoverId).Should().BeNull("no sweep is owed before the sixtieth step");

        world.Step(0.0);
        world.TickCount.Should().Be(SweepTicks, "an attempted step has always been counted");
        world.SafeActionFor(RoverId).Should().BeNull("the asset pass did not run on that step");

        Advance(world, 1);

        world.SafeActionFor(RoverId).Should().NotBeNull(
            "the sweep the skipped step was carrying is owed on the next step that runs, not a "
            + "further sixty steps later");
    }

    /// <summary>The sweep keeps simulated time, so speed and pause cannot move it.</summary>
    /// <remarks>
    /// Everything the sweep judges — silence against the link-loss threshold, the accrued
    /// uncertainty integral, the reserve — is a simulated-time quantity, and the governor's ledger
    /// is kept in simulated seconds so a replay produces the same fallbacks at the same instants.
    /// Only a world-step cadence has that property. A sweep driven by the broadcast clock instead
    /// would sample every 0.8 simulated seconds at eight times speed and forever at one simulated
    /// instant while paused.
    /// </remarks>
    [Fact]
    public void The_Sweep_Keeps_Simulated_Time_Across_A_Speed_Change_And_A_Pause()
    {
        var room = Room();
        PlaceRover(room);

        room.SetSpeed(8);
        Step(room, 20);

        room.TickCount.Should().Be(160, "eight world steps run per real tick at eight times speed");

        Observed(room).Should().Be(
            2.0, "the last sweep to land inside 160 world steps is the one at world step 120");

        room.Pause();
        Step(room, 40);

        Observed(room).Should().Be(
            2.0, "a paused world advances no simulated time, so it owes no sweep");

        room.Resume();
        Step(room, 10);

        room.TickCount.Should().Be(240);
        Observed(room).Should().Be(
            4.0,
            "the cadence survived a speed change and a pause because it is counted in world "
            + "steps, which are the only clock the fallbacks are judged against");
    }

    // ── B2: the link flag says what the server actually knows ───────────────

    /// <summary>Cutting a link triggers link loss at once, on the flag rather than the timer.</summary>
    /// <remarks>
    /// The two halves of the policy's link test are meant to catch different failures: the flag
    /// catches a bearer known to be down, the elapsed silence catches one that has merely gone
    /// quiet. While every capture hardcoded a connected link the first half was dead code, and an
    /// operator's cut was noticed only once the five-second silence timer expired — five seconds
    /// during which the layer was reporting that nothing was wrong.
    /// </remarks>
    [Fact]
    public void A_Cut_Link_Triggers_Link_Loss_On_The_Flag_Not_The_Silence_Timer()
    {
        var world = World();
        AddRover(world);
        world.SetLinkAvailable(RoverId, false).Should().BeTrue();

        Advance(world, SweepTicks);

        var verdict = world.SafeActionFor(RoverId);
        verdict.Should().NotBeNull();

        verdict!.Assessment.Trigger.Should().Be(SafeActionTrigger.LinkLoss);

        verdict.Assessment.ElapsedSinceContactSeconds.Should().BeLessThan(
            SafeActionThresholds.Default.LinkLossAfterSeconds,
            "the trigger has to have come from the link flag: no silence threshold had expired "
            + "yet, and if only the timer were live the asset would still be judged nominal");
    }

    /// <summary>A cut link is what the asset publishes, not just what the governor believes.</summary>
    [Fact]
    public void A_Cut_Link_Is_Published_On_The_Asset_State()
    {
        var world = World();
        AddRover(world);

        LinkOf(world, RoverId).IsConnected.Should().BeTrue("nothing has taken the link down");

        world.SetLinkAvailable(RoverId, false);

        LinkOf(world, RoverId).IsConnected.Should().BeFalse(
            "an operator holding this asset's link down must not be shown a connected asset; the "
            + "asset cannot know it is being ignored, so the fact has to reach it from the world");

        world.SetLinkAvailable(RoverId, true);
        LinkOf(world, RoverId).IsConnected.Should().BeTrue("restoring a link restores the report");
    }

    // ── B3: removal takes the governor's memory with it ─────────────────────

    /// <summary>Removing an asset drops the link and the ledger entry held against its id.</summary>
    /// <remarks>
    /// Ids are chosen by the operator and are routinely reused. The sweep's own pruning runs once
    /// a simulated second, so a removal and a respawn under the same id inside that second handed
    /// the replacement a link that was already down and a latch saying its fallback had already
    /// been issued: a brand-new asset, silent, and never to be made safe.
    /// </remarks>
    [Fact]
    public void Removing_An_Asset_Takes_Its_Governor_State_With_It()
    {
        var world = World();
        AddRover(world);
        world.SetLinkAvailable(RoverId, false);

        Advance(world, SweepTicks);
        world.SafeActionFor(RoverId).Should().NotBeNull();

        world.RemoveAsset(RoverId).Should().BeTrue();

        world.SafeActionFor(RoverId).Should().BeNull(
            "nothing may outlive the asset it was about, and the next sweep is a second away");

        world.IsLinkAvailable(RoverId).Should().BeTrue(
            "a held-down link belongs to the asset that was removed, not to the id it used");

        AddRover(world);
        Advance(world, SweepTicks);

        var replacement = world.SafeActionFor(RoverId);
        replacement.Should().NotBeNull();

        replacement!.Assessment.Trigger.Should().Be(
            SafeActionTrigger.None,
            "a replacement spawned under a reused id starts in contact and unacted-on");
    }

    // ── B4: the position gate is on the command path ────────────────────────

    /// <summary>A command that needs a current position is refused once the position is stale.</summary>
    /// <remarks>
    /// The gate the model documents and, until it was given a call path, did not apply: the
    /// validator cannot apply it because it has no view of how long an asset has been silent, and
    /// the executor cannot because a simulated vehicle always knows exactly where it is.
    /// </remarks>
    [Fact]
    public void A_Stale_Position_Refuses_A_Command_That_Needs_One()
    {
        var world = World();
        AddRover(world);

        Advance(world, SweepTicks);
        world.SendCommand(DriveTo()).IsAccepted.Should().BeTrue(
            "an asset in contact is refused nothing by this layer");

        world.SetLinkAvailable(RoverId, false);
        Advance(world, SweepTicks * 4);

        var refused = world.SendCommand(DriveTo());

        refused.IsAccepted.Should().BeFalse();
        refused.Reason.Should().Be(
            SafeActionReasons.PositionStale,
            "driveTo is catalogued as needing a fresh position, and the one on file is three "
            + "simulated seconds old on a bearer that is down");
    }

    /// <summary>The gate refuses only what needs a position, leaving stop reachable.</summary>
    /// <remarks>
    /// Both halves are what stop the gate becoming its own hazard. An asset whose position went
    /// stale must still be stoppable, or the refusal itself strands it; navigation remains
    /// refused until the position assessment is current again.
    /// </remarks>
    [Fact]
    public void The_Position_Gate_Leaves_Nonpositional_Commands_Reachable()
    {
        var world = World();
        AddRover(world);
        world.SetLinkAvailable(RoverId, false);
        Advance(world, SweepTicks * 4);

        world.SendCommand(new SimulatedAssetCommand(AssetCommandKind.Stop, RoverId))
            .IsAccepted.Should().BeTrue("stop needs no position and is never refused here");

        world.AuthorizeCommand(RoverId, AssetCommandKind.DriveTo)
            .IsAllowed.Should().BeFalse();
    }

    // ── B5: a failsafe that is not overwritten ──────────────────────────────

    /// <summary>The layer names the air asset it took off autonomous control, and only that one.</summary>
    /// <remarks>
    /// Air is the only domain anything else steers. Naming a rover here would ask the swarm
    /// coordinator to stand down from a vehicle it has never heard of.
    /// </remarks>
    [Fact]
    public void A_Safe_Action_Names_The_Air_Asset_It_Took_Off_Autonomous_Control()
    {
        var world = World();
        world.AddDrone(DroneId, DroneLaunchEus);
        AddRover(world);

        world.SetLinkAvailable(DroneId, false);
        world.SetLinkAvailable(RoverId, false);

        Advance(world, SweepTicks);

        var detached = world.DrainAutonomyDetachments();

        detached.Should().ContainSingle(
            "the drone's fallback has to survive the coordinator's next pass, and the rover has "
            + "no second writer to stand down").Which.Should().Be(DroneId);

        world.DrainAutonomyDetachments().Should().BeEmpty("a drain takes delivery");
    }

    /// <summary>A return-to-base failsafe survives every later pass of the swarm coordinator.</summary>
    /// <remarks>
    /// The end-to-end case, and the one that was silently broken: the governor issued the return
    /// straight to the asset, the coordinator retasked the same drone within half a simulated
    /// second, and nothing anywhere recorded that the failsafe had been undone. A drone that is
    /// genuinely returning parks on its launch point and stays there; a drone the coordinator is
    /// still flying is somewhere out on its patrol octagon, hundreds of metres away.
    /// </remarks>
    [Fact]
    public void A_Return_To_Base_Failsafe_Survives_The_Coordinators_Next_Pass()
    {
        var room = Room();
        room.AddDrone(DroneId, DroneLaunchEus, vendor: null);

        Step(room, 1200);
        DistanceFromLaunch(room).Should().BeGreaterThan(
            100f, "the coordinator has to have taken the drone somewhere for a return to mean anything");

        room.TrySetAssetLinkAvailable(DroneId, available: false, out var changed).Should().BeTrue();
        changed.Should().BeTrue();

        Step(room, 7200);

        // Sampled repeatedly rather than once. A single reading could catch a still-patrolling
        // drone as it happened to cross its own launch point; staying there for twenty simulated
        // seconds is something only a drone nobody is retasking can do.
        for (var sample = 0; sample < 12; sample++)
        {
            Step(room, 100);

            DistanceFromLaunch(room).Should().BeLessThan(
                5f,
                "the failsafe must still be the thing flying this drone {0} samples after it "
                + "fired, through dozens of coordinator passes",
                sample + 1);
        }
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    /// <summary>A world on the default terrain and calm weather.</summary>
    /// <returns>The world.</returns>
    private static AssetWorld World() =>
        new(new TerrainNoiseService(), new UpdatableWeatherSystem(new WeatherConfig()));

    /// <summary>A session with its own world, coordinator and tick loop.</summary>
    /// <returns>The room.</returns>
    private static SimulationRoom Room() =>
        new(id: "test-room-wiring", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>Registers a real Ackermann rover in a world.</summary>
    /// <param name="world">World to register it in.</param>
    private static void AddRover(AssetWorld world)
    {
        var profile = GroundProfile.ForVehicleClass(VehicleClass.AckermannRover)
            ?? throw new InvalidOperationException("The Ackermann class has no ground profile.");

        world.AddAsset(new GroundAsset(
            AssetProfiles.Create(RoverId, VehicleClass.AckermannRover),
            GroundDynamics.For(profile),
            world.Environment,
            RoverSpawnEus));
    }

    /// <summary>Spawns a rover into a room the way the spawn endpoint does.</summary>
    /// <param name="room">Room to spawn it in.</param>
    private static void PlaceRover(SimulationRoom room)
    {
        var profile = GroundProfile.ForVehicleClass(VehicleClass.AckermannRover)
            ?? throw new InvalidOperationException("The Ackermann class has no ground profile.");

        room.TrySpawnAsset(
                RoverId,
                environment => new GroundAsset(
                    AssetProfiles.Create(RoverId, VehicleClass.AckermannRover),
                    GroundDynamics.For(profile),
                    environment,
                    RoverSpawnEus),
                out var reason)
            .Should().BeTrue("the fixture must place the rover: {0}", reason);
    }

    /// <summary>Advances a world by whole steps at the suite's timestep.</summary>
    /// <param name="world">World to advance.</param>
    /// <param name="steps">Number of steps.</param>
    private static void Advance(AssetWorld world, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            world.Step(TimestepSeconds);
        }
    }

    /// <summary>Advances a room by whole real ticks.</summary>
    /// <param name="room">Room to advance.</param>
    /// <param name="ticks">Number of real ticks.</param>
    private static void Step(SimulationRoom room, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            room.StepOnce();
        }
    }

    /// <summary>Simulation instant the rover was last swept at.</summary>
    /// <param name="room">Room holding the rover.</param>
    /// <returns>The instant, in seconds.</returns>
    private static double Observed(SimulationRoom room)
    {
        var swept = room.UseAssets(w => w.SafeActionFor(RoverId));
        swept.Should().NotBeNull();

        return swept!.ObservedAtSeconds;
    }

    /// <summary>The link an asset currently publishes on its captured state.</summary>
    /// <param name="world">World holding the asset.</param>
    /// <param name="assetId">Asset to read.</param>
    /// <returns>The published link state.</returns>
    private static LinkState LinkOf(AssetWorld world, string assetId) =>
        world.States.Single(state => state.AssetId == assetId).Link;

    /// <summary>Horizontal distance from the room's drone to the point it launched from.</summary>
    /// <param name="room">Room holding the drone.</param>
    /// <returns>Distance in metres.</returns>
    private static float DistanceFromLaunch(SimulationRoom room) =>
        room.UseAssets(w =>
        {
            var model = w.Drones.Single(d => d.Id == DroneId).FlightModel;
            var position = model.State.Position;
            var launch = model.LaunchPosition;

            return new Vector2(position.X - launch.X, position.Z - launch.Z).Length();
        });

    /// <summary>A drive command to a point forty metres north of the rover's spawn.</summary>
    /// <returns>The translated command.</returns>
    private static SimulatedAssetCommand DriveTo() =>
        new(
            Kind: AssetCommandKind.DriveTo,
            AssetId: RoverId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus,
                OriginId: null,
                Position: RoverSpawnEus with { Z = RoverSpawnEus.Z - 40f },
                Orientation: Quaternion.Identity));
}
