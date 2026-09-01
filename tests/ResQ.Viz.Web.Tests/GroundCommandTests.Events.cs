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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The event half of <see cref="GroundCommandTests"/>: a rover announces transitions, never levels.
/// </summary>
/// <remarks>
/// Split from the command assertions because the two ask different questions of the same object. A
/// command test asks what one call returned; these run a condition across dozens of ticks and count
/// what came out, which is the only way to catch the failure that matters here — a level-triggered
/// condition emitting an alert on every tick, sixty a second at the world's rate, burying every
/// other event in the log.
/// <para>
/// The terrain is scripted rather than driven into: flooding the plateau under a stationary rover
/// is what a preset change raising the water surface does, and it is the one way a vehicle that is
/// not moving becomes immobilised. That keeps the rover's own state constant, so the event count is
/// unambiguously about the terrain transition.
/// </para>
/// </remarks>
public partial class GroundCommandTests
{
    /// <summary>Immobilisation is announced once, however many ticks the rover stays stuck.</summary>
    /// <remarks>
    /// The rover is stopped throughout, so nothing but the terrain changes. Drained as an exact
    /// sequence rather than as a count of matching codes, so a spurious second event of any kind
    /// fails this too.
    /// </remarks>
    [Fact]
    public void An_Immobilised_Rover_Raises_One_Event_However_Long_It_Stays_Stuck()
    {
        var rover = CreateRover();
        rover.Step(5);
        rover.Asset.DrainEvents();

        rover.Ground.IsFlooded = true;
        rover.Step(40);

        rover.Asset.DrainEvents().Should().ContainSingle(
            "an event is an edge; forty ticks of the same condition is still one transition")
            .Which.Code.Should().Be("ground.immobilised");

        var state = rover.GroundState();
        state.IsImmobilised.Should().BeTrue();
        state.ImmobilisationReason.Should().Be("ground.blocked.water");
    }

    /// <summary>Recovering mobility is announced once too, on the opposite edge.</summary>
    /// <remarks>
    /// The complement matters as much as the alert: an operator watching only for the alarm never
    /// learns the vehicle is free again, and a recovery that fired every tick would be just as
    /// unreadable as an alarm that did.
    /// </remarks>
    [Fact]
    public void A_Rover_That_Recovers_Its_Mobility_Raises_One_Matching_Event()
    {
        var rover = CreateRover();
        rover.Ground.IsFlooded = true;
        rover.Step(10);
        rover.Asset.DrainEvents();

        rover.Ground.IsFlooded = false;
        rover.Step(20);

        rover.Asset.DrainEvents().Should().ContainSingle().Which.Code.Should().Be("ground.mobile");
        rover.GroundState().IsImmobilised.Should().BeFalse();
    }

    /// <summary>An immobilised rover is not reported as faulted, so recovery stays commandable.</summary>
    /// <remarks>
    /// Bad ground is not a defect of the vehicle. Publishing
    /// <see cref="OperationalState.Faulted"/> would put the asset outside the command catalog's
    /// operable policy and refuse exactly the commands that get it out again.
    /// </remarks>
    [Fact]
    public void An_Immobilised_Rover_Stays_Commandable()
    {
        var rover = CreateRover();
        rover.Ground.IsFlooded = true;
        rover.Step(10);

        rover.Capture().OperationalState.Should().NotBe(OperationalState.Faulted);

        rover.Ground.IsFlooded = false;
        rover.Asset.Apply(Command(AssetCommandKind.DriveTo, new Vector3(0f, 0f, -40f)))
            .Should().Be(AssetCommandResult.Accepted);
    }
}
