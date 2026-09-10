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

using ResQ.Simulation.Engine.Physics;

using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Asserts the flight behaviours this product depends on are present in the pinned SDK.
/// </summary>
/// <remarks>
/// <para>
/// These duplicate tests the SDK already has, on purpose. They are a contract test: viz asserting
/// that the submodule it is pinned to still behaves the way the client and the physics assume.
/// </para>
/// <para>
/// The reason is specific. The submodule is pinned to <c>a3f8b89</c> on <c>release/0.6.x</c>,
/// three commits past the <c>v0.6.0</c> tag, and those three commits are what added drone
/// attitude, the explicit yaw command, and landing recovery. No tag contains them.
/// </para>
/// <para>
/// Moving the pin is not what these tests guard, because both ways of doing it already fail
/// loudly. <c>v0.6.0</c> is a compile error — viz calls <c>Hover(yaw)</c> and
/// <c>GoTo(…, yaw:)</c>, which that tag has no overloads for. <c>main</c> cannot resolve its
/// project references at all, because <c>ResQ.Simulation.Engine</c> and all three MAVLink
/// projects were removed there. That second divergence is a standing decision for a human, not
/// something a test can hold.
/// </para>
/// <para>
/// What these guard is the part no compiler can see: behaviour that changed without changing a
/// signature. The landing re-arm lives inside <c>ApplyCommand</c> and the roll/pitch term inside
/// <c>IntegrateAttitude</c>, so reverting either builds green. Verified by applying each revert
/// in the submodule — the build stayed green and exactly one test here went red. Those are the
/// failures that would surface as "the client looks wrong", a long way from the cause.
/// </para>
/// </remarks>
public sealed class SdkFlightContractTests
{
    private static KinematicFlightModel Airborne(out Vector3 start)
    {
        start = new Vector3(0f, 50f, 0f);
        return new KinematicFlightModel(start);
    }

    [Fact]
    public void ExplicitYawCommandSteersHeading()
    {
        // FlightCommand.DesiredYaw is the whole reason the client can point a drone. Without it
        // the model free-runs its heading from velocity and the yaw argument is silently ignored.
        var model = Airborne(out var start);
        model.ApplyCommand(FlightCommand.Hover(Math.PI / 2));

        for (int i = 0; i < 120; i++)
            model.Step(1.0 / 60.0, Vector3.Zero);

        // Yaw about +Y extracted from the orientation quaternion.
        Quaternion q = model.State.Orientation;
        double yaw = Math.Atan2(
            2.0 * ((q.W * q.Y) + (q.X * q.Z)),
            1.0 - (2.0 * ((q.Y * q.Y) + (q.X * q.X))));

        yaw.Should().BeApproximately(Math.PI / 2, 0.05,
            "FlightCommand.DesiredYaw must steer heading — without it the client cannot aim a drone");
        model.State.Position.Should().BeEquivalentTo(start,
            "a Hover with a yaw turns on the spot rather than translating");
    }

    [Fact]
    public void ForwardFlightProducesNonTrivialAttitude()
    {
        // Roll banking into turns and pitch tipping with speed. If the model reverts to a
        // heading-only orientation the drone slides around flat, which reads as broken rotation.
        var model = Airborne(out _);
        model.ApplyCommand(FlightCommand.GoTo(new Vector3(500f, 50f, 0f)));

        for (int i = 0; i < 120; i++)
            model.Step(1.0 / 60.0, Vector3.Zero);

        Quaternion q = model.State.Orientation;
        double pitch = Math.Asin(Math.Clamp(2.0 * ((q.W * q.X) - (q.Z * q.Y)), -1.0, 1.0));

        Math.Abs(pitch).Should().BeGreaterThan(0.01,
            "a drone under way must pitch — a flat orientation means attitude integration is gone");
        model.State.Velocity.Length().Should().BeGreaterThan(1f, "it should actually be moving");
    }

    /// <summary>Every command type that is not <c>Land</c>, so the name's "any" is honest.</summary>
    public static TheoryData<string> NonLandCommands() => new() { "GoTo", "Hover", "RTL" };

    [Theory]
    [MemberData(nameof(NonLandCommands))]
    public void ALandedDroneReArmsOnAnyNonLandCommand(string command)
    {
        // HasLanded latches, and Step() returns early while it is set. Without the re-arm, a
        // drone that has landed once ignores every later command and sits frozen for the rest of
        // the session — the bug that made takeoff appear to do nothing.
        //
        // Parameterised because the re-arm is keyed on command.Type, so covering only GoTo would
        // pass a regression that cleared HasLanded for GoTo and not for Hover or RTL. The
        // assertion says "any non-Land command"; this makes the test say it too.
        var model = new KinematicFlightModel(new Vector3(0f, 20f, 0f));
        model.ApplyCommand(FlightCommand.Land());

        for (int i = 0; i < 3000 && !model.HasLanded; i++)
            model.Step(1.0 / 60.0, Vector3.Zero);

        model.HasLanded.Should().BeTrue("the drone should reach the ground and latch as landed");

        model.ApplyCommand(command switch
        {
            "GoTo" => FlightCommand.GoTo(new Vector3(0f, 60f, 0f)),
            "Hover" => FlightCommand.Hover(),
            "RTL" => FlightCommand.RTL(),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "unmapped command"),
        });

        model.HasLanded.Should().BeFalse(
            $"{command} is not Land, so it must re-arm a landed drone");

        // Only GoTo and RTL have somewhere to go: RTL rewrites to GoTo(LaunchPosition), which is
        // 20 m up from where this drone came to rest. Hover holds station, so asserting a climb
        // for it would assert the wrong contract — the re-armed flag is what matters there.
        float before = model.State.Position.Y;
        for (int i = 0; i < 120; i++)
            model.Step(1.0 / 60.0, Vector3.Zero);

        if (command == "Hover")
        {
            model.HasLanded.Should().BeFalse("a hovering drone stays re-armed rather than re-latching");
        }
        else
        {
            model.State.Position.Y.Should().BeGreaterThan(before,
                "a re-armed drone must actually climb, not merely clear the flag");
        }
    }

    [Fact]
    public void LandStillLatchesSoTheDroneStaysDown()
    {
        // The mirror of the test above: re-arming must not have made HasLanded meaningless.
        var model = new KinematicFlightModel(new Vector3(0f, 20f, 0f));
        model.ApplyCommand(FlightCommand.Land());

        for (int i = 0; i < 3000 && !model.HasLanded; i++)
            model.Step(1.0 / 60.0, Vector3.Zero);

        float resting = model.State.Position.Y;
        for (int i = 0; i < 600; i++)
            model.Step(1.0 / 60.0, Vector3.Zero);

        model.HasLanded.Should().BeTrue("Land must keep the drone down");
        model.State.Position.Y.Should().Be(resting, "a landed drone does not drift");
    }
}
