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
using ResQ.Simulation.Engine.Physics;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Tests for <see cref="SimulationRoom"/> (per-room state holder).</summary>
public class SimulationServiceTests
{
    private static SimulationRoom CreateRoom() =>
        new(id: "test-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    [Fact]
    public void SimulationRoom_Creates_World()
    {
        var act = () => CreateRoom();
        act.Should().NotThrow();
    }

    [Fact]
    public void GetSnapshot_Returns_Empty_When_No_Drones()
    {
        var room = CreateRoom();
        room.GetSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void AddDrone_Adds_Drone_To_World()
    {
        var room = CreateRoom();
        room.AddDrone("drone-1", new Vector3(10f, 0f, 20f));

        var snapshot = room.GetSnapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Id.Should().Be("drone-1");
        snapshot[0].Position.Should().HaveCount(3);
        snapshot[0].Position[0].Should().BeApproximately(10f, 0.001f);
        snapshot[0].Position[2].Should().BeApproximately(20f, 0.001f);
    }

    [Fact]
    public void Step_Advances_Simulation()
    {
        var room = CreateRoom();
        room.AddDrone("drone-2", new Vector3(0f, 100f, 0f));

        var act = () => room.StepOnce();
        act.Should().NotThrow();

        room.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void SetWeather_Changes_Wind_Mode()
    {
        var room = CreateRoom();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
        room.StepOnce();
        room.SetWeather("steady", 20.0, 90.0);
        for (var i = 0; i < 10; i++) room.StepOnce();
        var after = room.GetSnapshot()[0];
        after.Should().NotBeNull();
    }

    [Fact]
    public void Reset_ClearsAllDrones()
    {
        var room = CreateRoom();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
        room.AddDrone("d2", new Vector3(20f, 50f, 0f));
        room.GetSnapshot().Should().HaveCount(2);
        room.Reset();
        room.GetSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void SendCommand_ValidDrone_DoesNotThrow()
    {
        var room = CreateRoom();
        room.AddDrone("drone-cmd", new Vector3(0f, 50f, 0f));

        var act = () => room.SendCommand("drone-cmd", FlightCommand.Hover());
        act.Should().NotThrow();
    }

    [Fact]
    public void SendCommand_UnknownDrone_DoesNotThrow()
    {
        var room = CreateRoom();
        var act = () => room.SendCommand("ghost", FlightCommand.Hover());
        act.Should().NotThrow();
    }

    [Fact]
    public void Tick_Returns_Broadcast_Flag_Every_Sixth_Step()
    {
        var room = CreateRoom();
        var broadcasts = 0;
        for (var i = 0; i < 12; i++)
        {
            var (broadcast, _) = room.Tick();
            if (broadcast) broadcasts++;
        }
        broadcasts.Should().Be(2, "every 6th tick of 12 should broadcast");
    }

    [Fact]
    public void Reset_Resets_After_Weather_Change()
    {
        var room = CreateRoom();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
        room.SetWeather("turbulent", 30.0, 45.0);
        room.Reset();

        room.GetSnapshot().Should().BeEmpty();
        var act = () => room.AddDrone("d2", new Vector3(0f, 50f, 0f));
        act.Should().NotThrow();
        room.GetSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public void Multiple_Drones_Snapshot_Has_Correct_Ids()
    {
        var room = CreateRoom();
        room.AddDrone("alpha", new Vector3(0f, 50f, 0f));
        room.AddDrone("beta", new Vector3(10f, 50f, 0f));
        room.AddDrone("gamma", new Vector3(20f, 50f, 0f));

        var ids = room.GetSnapshot().Select(d => d.Id).ToList();
        ids.Should().BeEquivalentTo(["alpha", "beta", "gamma"]);
    }

    [Fact]
    public void GetSnapshot_Rotation_Has_Four_Elements_For_Quaternion()
    {
        var room = CreateRoom();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
        room.GetSnapshot()[0].Rotation.Should().HaveCount(4);
    }

    [Fact]
    public void GetSnapshot_Rotation_Is_Unit_Quaternion()
    {
        var room = CreateRoom();
        room.AddDrone("d1", new Vector3(0f, 50f, 0f));
        room.StepOnce();
        var rot = room.GetSnapshot()[0].Rotation;
        var mag = Math.Sqrt(rot[0] * rot[0] + rot[1] * rot[1] + rot[2] * rot[2] + rot[3] * rot[3]);
        mag.Should().BeApproximately(1.0, 0.001, "quaternion must be unit-length");
    }

    [Fact]
    public void IncrementConnections_Tracks_LiveCount()
    {
        var room = CreateRoom();
        room.ConnectionCount.Should().Be(0);
        room.IncrementConnections();
        room.IncrementConnections();
        room.ConnectionCount.Should().Be(2);
        room.DecrementConnections();
        room.ConnectionCount.Should().Be(1);
    }

    [Fact]
    public void DecrementConnections_Floors_AtZero()
    {
        var room = CreateRoom();
        room.DecrementConnections();
        room.DecrementConnections();
        room.ConnectionCount.Should().Be(0, "floor at zero — cookie replays must not drive the counter negative");
    }

    // ─── Transport (pause / speed / step) ─────────────────────────────────────

    [Fact]
    /// <summary>Pausing halts world advancement: ticking a paused room leaves simulation time unchanged.</summary>
    public void Pause_Stops_World_Advancement()
    {
        var room = CreateRoom();
        for (var i = 0; i < 6; i++) room.Tick();
        room.Pause();
        var before = room.TickCount;
        for (var i = 0; i < 10; i++) room.Tick();
        room.TickCount.Should().Be(before, "a paused world must not advance");
        room.IsPaused.Should().BeTrue();
    }

    [Fact]
    /// <summary>Resuming after a pause returns the room to advancing simulation time on each tick.</summary>
    public void Resume_Continues_Advancement()
    {
        var room = CreateRoom();
        room.Pause();
        room.Tick();
        var paused = room.TickCount;
        room.Resume();
        room.Tick();
        room.TickCount.Should().BeGreaterThan(paused);
        room.IsPaused.Should().BeFalse();
    }

    [Fact]
    /// <summary>The speed multiplier scales how many fixed steps a single tick advances.</summary>
    public void SetSpeed_Multiplies_Steps_Per_Tick()
    {
        var room = CreateRoom();
        room.SetSpeed(4);
        room.Speed.Should().Be(4);
        room.Tick();
        room.TickCount.Should().Be(4, "speed 4 advances four world steps per real tick");
    }

    [Fact]
    /// <summary>Out-of-range speeds clamp to the supported bounds instead of being applied verbatim.</summary>
    public void SetSpeed_Clamps_To_Valid_Range()
    {
        var room = CreateRoom();
        room.SetSpeed(999);
        room.Speed.Should().Be(8, "speed is clamped to the max multiplier");
        room.SetSpeed(0);
        room.Speed.Should().Be(1, "speed is clamped to the min multiplier");
    }

    [Fact]
    /// <summary>Single-stepping a paused room advances exactly the requested number of frames and no more.</summary>
    public void StepFrames_Advances_Exactly_While_Paused()
    {
        var room = CreateRoom();
        room.Pause();
        room.StepFrames(3);
        // Each Tick consumes one queued step even though the world is paused.
        room.Tick();
        room.Tick();
        room.Tick();
        room.TickCount.Should().Be(3);
        // Queue exhausted → the paused world holds.
        room.Tick();
        room.TickCount.Should().Be(3, "queued steps exhausted; paused world holds");
    }

    [Fact]
    /// <summary>Broadcasts continue at 10 Hz while paused, so a paused client still receives frames and does not appear disconnected.</summary>
    public void Tick_Broadcasts_At_10Hz_Even_While_Paused()
    {
        var room = CreateRoom();
        room.Pause();
        var broadcasts = 0;
        for (var i = 0; i < 12; i++)
            if (room.Tick().ShouldBroadcast) broadcasts++;
        broadcasts.Should().Be(2, "broadcast cadence is driven by real ticks, not sim steps");
        room.TickCount.Should().Be(0, "paused world advanced no steps");
    }

    [Fact]
    /// <summary>Reset returns pause and speed to their defaults along with the world itself.</summary>
    public void Reset_Clears_Transport_State()
    {
        var room = CreateRoom();
        room.Pause();
        room.SetSpeed(4);
        room.Reset();
        room.IsPaused.Should().BeFalse();
        room.Speed.Should().Be(1);
        room.TickCount.Should().Be(0);
    }
}
