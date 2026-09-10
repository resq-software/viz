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
/// attitude, the explicit yaw command, and landing recovery. No tag contains them. So moving the
/// pin to <c>v0.6.0</c> — which is exactly what <c>CLAUDE.md</c> used to invite by describing the
/// submodule as "pinned to a release tag" — still builds, still passes every other test, and
/// silently reverts takeoff and rotation. That failure would surface as the client looking wrong,
/// a long way from its cause.
/// </para>
/// <para>
/// Moving the pin to <c>main</c> is loud rather than silent: <c>ResQ.Simulation.Engine</c> and all
/// three MAVLink projects were removed there, so the build cannot resolve its project references
/// at all. That divergence is a standing decision for a human, not something a test can hold.
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

    [Fact]
    public void ALandedDroneReArmsOnAnyNonLandCommand()
    {
        // HasLanded latches, and Step() returns early while it is set. Without the re-arm, a
        // drone that has landed once ignores every later command and sits frozen for the rest of
        // the session — the bug that made takeoff appear to do nothing.
        var model = new KinematicFlightModel(new Vector3(0f, 20f, 0f));
        model.ApplyCommand(FlightCommand.Land());

        for (int i = 0; i < 3000 && !model.HasLanded; i++)
            model.Step(1.0 / 60.0, Vector3.Zero);

        model.HasLanded.Should().BeTrue("the drone should reach the ground and latch as landed");

        model.ApplyCommand(FlightCommand.GoTo(new Vector3(0f, 60f, 0f)));
        model.HasLanded.Should().BeFalse("any command other than Land must re-arm a landed drone");

        float before = model.State.Position.Y;
        for (int i = 0; i < 120; i++)
            model.Step(1.0 / 60.0, Vector3.Zero);

        model.State.Position.Y.Should().BeGreaterThan(before,
            "a re-armed drone must actually climb, not merely clear the flag");
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
