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
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// What a rover does with a command, and — more importantly — what it refuses to do with one.
/// </summary>
/// <remarks>
/// These go straight at <see cref="GroundAsset.Apply"/>, deliberately bypassing the
/// <c>CommandCatalog</c> validator. That is not a shortcut: the v1 compatibility adapter builds a
/// <see cref="SimulatedAssetCommand"/> without ever consulting the catalog, so every gate that
/// matters has to hold at the asset itself. A suite that only ever drove the validator would pass
/// while a rover happily accepted <c>takeoff</c> from the other entry point.
/// <para>
/// Two properties are asserted everywhere and are worth stating once. A rejection is
/// <b>side-effect free</b> — compared as the whole published state, not just the pose, because a
/// refusal that quietly dropped a target or cleared a block leaves the position identical. And an
/// event is an <b>edge</b>: a condition that persists for a hundred ticks is one event, not a
/// hundred, or the log buries everything worth reading.
/// </para>
/// <para>
/// Deterministic by construction: a fixed seed, a fixed timestep, timestamps derived from
/// simulation time, and terrain that is a pure function of position. Nothing sleeps and nothing
/// reads a clock.
/// </para>
/// </remarks>
public partial class GroundCommandTests
{
    // ─── The ground vocabulary on a capable platform ────────────────────────

    /// <summary>A capable rover takes a drive target and closes on it.</summary>
    /// <remarks>
    /// Asserted as displacement toward the target rather than as arrival, so the test says
    /// "the command reached the guidance law" without also pinning the approach profile.
    /// </remarks>
    [Fact]
    public void DriveTo_Is_Accepted_And_Moves_The_Rover_Toward_The_Target()
    {
        var rover = CreateRover();

        rover.Asset.Apply(Command(AssetCommandKind.DriveTo, new Vector3(0f, 0f, -40f)))
            .Should().Be(AssetCommandResult.Accepted);

        rover.Capture().Mode.Should().Be("drive");
        rover.Step(30);

        var state = rover.GroundState();
        state.GroundSpeedMps.Should().BePositive("the rover drives forward toward the target");
        state.IsMoving.Should().BeTrue();

        rover.Capture().Pose.Position.Z.Should().BeLessThan(
            -3.0f, "north is -Z, so closing on a target 40 m north moves the rover that way");
    }

    /// <summary>A capable rover backs up under <c>reverse</c>, with no target.</summary>
    [Fact]
    public void Reverse_Is_Accepted_And_Backs_A_Capable_Rover_Up()
    {
        var rover = CreateRover();

        rover.Asset.Apply(Command(AssetCommandKind.Reverse))
            .Should().Be(AssetCommandResult.Accepted);

        rover.Capture().Mode.Should().Be("reverse");
        rover.Step(20);

        rover.GroundState().GroundSpeedMps.Should().BeNegative(
            "reverse is a negative longitudinal speed, not a heading change");

        rover.Capture().Pose.Position.Z.Should().BeGreaterThan(
            1.0f, "backing up from a northward heading moves the rover south, toward +Z");
    }

    /// <summary>A parked rover stops, is secured, and stays exactly where it stopped.</summary>
    /// <remarks>
    /// Both halves matter. Reaching zero says the setpoint took effect; staying put over further
    /// steps says the integrator adds an exact zero rather than a small residual, which is what
    /// stops a parked rover wandering across a long idle.
    /// </remarks>
    [Fact]
    public void Park_Brings_A_Moving_Rover_To_A_Standstill_And_Secures_It()
    {
        var rover = MovingRover();

        rover.Asset.Apply(Command(AssetCommandKind.Park))
            .Should().Be(AssetCommandResult.Accepted);

        var parked = rover.Capture();
        parked.Mode.Should().Be("park");
        parked.OperationalState.Should().Be(OperationalState.Standby);

        rover.Step(30);

        rover.GroundState().GroundSpeedMps.Should().Be(0.0);
        var settled = rover.Capture().Pose.Position;

        rover.Step(10);
        rover.Capture().Pose.Position.Should().Be(
            settled, "a secured rover holds its pose exactly, not approximately");
    }

    /// <summary><c>setSteering</c> is refused, and is no longer offered to anyone either.</summary>
    /// <remarks>
    /// The gap this used to document is now closed from the advertising side. The catalog offered
    /// <c>setSteering</c> to any ground asset declaring <see cref="AssetCapability.ManualControl"/>
    /// — which is all three rovers — while <see cref="SimulatedAssetCommand"/> carries no steering
    /// field for the angle to travel in, so the capability report put a control on screen whose
    /// only possible outcome was a rejection. The command is therefore no longer registered, and
    /// the refusal here is the catalog's own: an unregistered kind satisfies no descriptor.
    /// <para>
    /// <see cref="GroundAsset"/>'s two-cause distinction is still written down where it belongs,
    /// in <c>ApplySetSteering</c>'s remarks, and still matters for the commit that lands the
    /// field: a steered platform will accept the angle, a pivot-steered one must go on refusing
    /// it. Register the command in that same commit, and this test becomes an acceptance test.
    /// Do not re-register it before then to make this assertion prettier.
    /// </para>
    /// </remarks>
    /// <param name="vehicleClass">Platform to command.</param>
    [Theory]
    [InlineData(VehicleClass.AckermannRover)]
    [InlineData(VehicleClass.DifferentialRover)]
    [InlineData(VehicleClass.TrackedRover)]
    public void SetSteering_Is_Refused_Without_Side_Effects_And_Advertised_To_Nobody(
        VehicleClass vehicleClass)
    {
        var rover = CreateRover(vehicleClass);

        rover.Asset.Descriptor.Capabilities.Should().HaveFlag(
            AssetCapability.ManualControl,
            "the refusal under test must come from the command being unregistered, not from a "
            + "capability the platform never claimed");

        CommandCatalog.TryGet(CommandKinds.SetSteering, out _).Should().BeFalse(
            "a command no asset can execute must not be registered for one to be offered");

        RefusedWithoutSideEffects(
            rover, Command(AssetCommandKind.SetSteering), "capability.missing");
    }

    // ─── Reverse on a platform that must not reverse ────────────────────────

    /// <summary>A drivetrain that cannot go backwards refuses <c>reverse</c>, and moves nothing.</summary>
    /// <remarks>
    /// The descriptor still declares <see cref="AssetCapability.Reverse"/>, so the capability gate
    /// passes and only the profile refuses. That separation is the point: a declared capability
    /// must never outvote a drivetrain that physically cannot turn backwards.
    /// </remarks>
    [Fact]
    public void Reverse_Is_Refused_Without_Side_Effects_When_The_Drivetrain_Cannot_Reverse()
    {
        var rover = MovingRover(GroundProfile.AckermannRover with { MaxReverseSpeedMps = 0.0 });

        rover.Asset.Descriptor.Capabilities.Should().HaveFlag(
            AssetCapability.Reverse, "the refusal under test must come from the profile, not the mask");

        RefusedWithoutSideEffects(
            rover, Command(AssetCommandKind.Reverse), "capability.reverse.unsupported");
    }

    /// <summary>A rover that does not declare reverse is refused by the capability gate.</summary>
    [Fact]
    public void Reverse_Is_Refused_Without_Side_Effects_When_The_Capability_Is_Not_Declared()
    {
        var rover = CreateRover(withoutCapabilities: AssetCapability.Reverse);

        rover.Asset.Apply(Command(AssetCommandKind.DriveTo, new Vector3(0f, 0f, -60f)))
            .IsAccepted.Should().BeTrue();
        rover.Step(20);

        RefusedWithoutSideEffects(rover, Command(AssetCommandKind.Reverse), "capability.missing");
    }

    // ─── Targets the platform may not occupy ────────────────────────────────

    /// <summary>A drive target on ground the platform cannot occupy is refused, naming the cause.</summary>
    /// <remarks>
    /// The token is the planner's own <see cref="Traversability.ReasonCode"/>, so the refusal says
    /// <em>why</em> — water, a prohibited zone — rather than only that something was wrong. Both
    /// spellings of the manoeuvre are exercised: <c>goTo</c> and <c>driveTo</c> are the same
    /// command for a rover, and an operator's choice of vocabulary must not change the answer.
    /// </remarks>
    /// <param name="kind">Which spelling of the drive command to issue.</param>
    /// <param name="isWater">True to flood the target, false to restrict it with a no-entry zone.</param>
    /// <param name="reason">Token the refusal must carry.</param>
    [Theory]
    [InlineData(AssetCommandKind.DriveTo, true, "traversability.blocked.water")]
    [InlineData(AssetCommandKind.GoTo, true, "traversability.blocked.water")]
    [InlineData(AssetCommandKind.DriveTo, false, "traversability.blocked.zone")]
    [InlineData(AssetCommandKind.GoTo, false, "traversability.blocked.zone")]
    public void DriveTo_A_Blocked_Target_Is_Refused_Naming_Why_And_Changes_Nothing(
        AssetCommandKind kind, bool isWater, string reason)
    {
        var rover = MovingRover();

        if (isWater)
        {
            rover.Ground.WaterEastFromM = HazardEastFromM;
        }
        else
        {
            rover.Ground.ProhibitedEastFromM = HazardEastFromM;
        }

        RefusedWithoutSideEffects(
            rover,
            Command(kind, new Vector3((float)HazardEastFromM + 50f, 0f, 0f)),
            reason);
    }

    // ─── Emergency stop ─────────────────────────────────────────────────────

    /// <summary>An emergency stop zeroes the drivetrain and centres the steering in one step.</summary>
    /// <remarks>
    /// Zero exactly, not merely smaller. The limiter clamps the change toward the setpoint rather
    /// than scaling toward it, so a step carrying more authority than the rover has speed lands on
    /// an exact zero — and a model that only ever trended down would fail here. The step is one
    /// second because that is what the profile's declared 4.5 m/s² of braking and 0.70 rad/s of
    /// steering rate need to cover the speed and lock angle the rover has picked up; asserting a
    /// stop the platform could not physically make would be asserting a fiction.
    /// </remarks>
    [Fact]
    public void EmergencyStop_Zeroes_The_Speed_And_Centres_The_Steering_Within_One_Step()
    {
        var rover = CreateRover();

        // A target abeam forces the steering onto its lock, so "centres the steering" is a real
        // assertion rather than a restatement of the initial condition.
        rover.Asset.Apply(Command(AssetCommandKind.DriveTo, new Vector3(60f, 0f, 0f)))
            .IsAccepted.Should().BeTrue();
        rover.Step(10);

        var moving = rover.GroundState();
        moving.GroundSpeedMps.Should().BePositive();
        Math.Abs(moving.SteeringAngleRad).Should().BeGreaterThan(0.1);

        rover.Asset.Apply(Command(AssetCommandKind.EmergencyStop))
            .Should().Be(AssetCommandResult.Accepted);

        rover.Step(count: 1, deltaSeconds: 1.0);

        var stopped = rover.GroundState();
        stopped.GroundSpeedMps.Should().Be(0.0, "the drivetrain is commanded to zero, not slowed");
        stopped.SteeringAngleRad.Should().Be(0.0, "the steering is centred, not left on lock");
        stopped.IsMoving.Should().BeFalse();

        var state = rover.Capture();
        state.Mode.Should().Be("emergency-stop");
        state.OperationalState.Should().Be(OperationalState.Emergency);
    }

    /// <summary>A latched emergency stop refuses everything but its own release.</summary>
    [Fact]
    public void An_Emergency_Stopped_Rover_Refuses_Motion_Until_The_Stop_Is_Released()
    {
        var rover = MovingRover();

        rover.Asset.Apply(Command(AssetCommandKind.EmergencyStop)).IsAccepted.Should().BeTrue();
        rover.Step(count: 1, deltaSeconds: 1.0);

        RefusedWithoutSideEffects(
            rover,
            Command(AssetCommandKind.DriveTo, new Vector3(0f, 0f, -80f)),
            "asset.emergencyStopped");

        rover.Asset.Apply(Command(AssetCommandKind.Stop))
            .Should().Be(AssetCommandResult.Accepted, "stop is the always-reachable release");

        rover.Asset.IsEmergencyStopped.Should().BeFalse();
        rover.Capture().OperationalState.Should().Be(OperationalState.Ready);
    }

    /// <summary>Re-issuing an emergency stop is accepted, and raises no second event.</summary>
    [Fact]
    public void Repeating_An_Emergency_Stop_Raises_Exactly_One_Event()
    {
        var rover = MovingRover();
        rover.Asset.DrainEvents();

        for (int i = 0; i < 3; i++)
        {
            rover.Asset.Apply(Command(AssetCommandKind.EmergencyStop)).Should().Be(
                AssetCommandResult.Accepted,
                "refusing to stop something because it is already stopping is exactly backwards");
        }

        rover.Asset.DrainEvents().Should().ContainSingle(
            "the event is raised on the transition, not on every issue of the command")
            .Which.Code.Should().Be("ground.emergencyStop");
    }

    // ─── Hold is not station keeping ────────────────────────────────────────

    /// <summary>A rover holds by stopping, and needs no station-keeping capability to do it.</summary>
    /// <remarks>
    /// The asymmetry this pins is why <c>hold</c> is ungated in the command catalog: holding a spot
    /// on land costs a rover nothing, while a displacement hull cannot do it at all. The retained
    /// target is the other half of the contract — <c>hold</c> suspends mission progress rather than
    /// abandoning it, so autonomy resumes the route instead of idling.
    /// </remarks>
    [Fact]
    public void Hold_Stops_A_Rover_That_Declares_No_Station_Keeping_And_Keeps_Its_Target()
    {
        var rover = CreateRover(withoutCapabilities: AssetCapability.StationKeep);
        rover.Asset.Descriptor.Capabilities.Should().NotHaveFlag(AssetCapability.StationKeep);

        rover.Asset.Apply(Command(AssetCommandKind.DriveTo, new Vector3(0f, 0f, -80f)))
            .IsAccepted.Should().BeTrue();
        rover.Step(20);
        rover.GroundState().GroundSpeedMps.Should().BePositive();

        rover.Asset.Apply(Command(AssetCommandKind.Hold))
            .Should().Be(AssetCommandResult.Accepted, "a rover satisfies hold by stopping");

        var holding = rover.Capture();
        holding.Mode.Should().Be("hold");
        holding.OperationalState.Should().Be(OperationalState.Holding);

        rover.Step(30);
        rover.GroundState().GroundSpeedMps.Should().Be(0.0);

        var held = rover.Capture().Pose.Position;
        rover.Step(10);
        rover.Capture().Pose.Position.Should().Be(held, "a held rover stays exactly where it stopped");

        rover.Asset.Apply(Command(AssetCommandKind.ResumeAutonomy))
            .Should().Be(AssetCommandResult.Accepted);
        rover.Capture().Mode.Should().Be("drive", "hold keeps the target so autonomy can resume it");

        rover.Step(20);
        rover.GroundState().GroundSpeedMps.Should().BePositive();
    }

    // ─── Commands from another domain ───────────────────────────────────────

    /// <summary>Air-domain commands are refused at the asset, whatever the rover declares.</summary>
    /// <remarks>
    /// Handed straight to <see cref="GroundAsset.Apply"/> with no catalog in the path, because the
    /// v1 adapter reaches the asset that way. <c>land</c> is the case that proves the gate is the
    /// domain and not the capability: a rover declares <see cref="AssetCapability.Land"/> — it is
    /// what <c>park</c> is gated on — so the capability check passes and only the domain refuses.
    /// </remarks>
    /// <param name="kind">Air-domain command to refuse.</param>
    [Theory]
    [InlineData(AssetCommandKind.Takeoff)]
    [InlineData(AssetCommandKind.Land)]
    [InlineData(AssetCommandKind.SetAltitude)]
    [InlineData(AssetCommandKind.Loiter)]
    public void An_Air_Command_Is_Refused_At_The_Asset_With_No_State_Change(AssetCommandKind kind)
    {
        var rover = MovingRover();

        rover.Asset.Descriptor.Capabilities.Should().HaveFlag(
            AssetCapability.Land, "otherwise 'land' would be caught by the capability gate instead");

        RefusedWithoutSideEffects(rover, Command(kind), "command.domain.air");
    }

    /// <summary>Surface-domain commands are refused the same way.</summary>
    /// <remarks>
    /// <c>stationKeep</c> is the mirror of <c>land</c>: a rover declares
    /// <see cref="AssetCapability.StationKeep"/> and is still refused, because holding a point on
    /// land and holding one against a current are not the same manoeuvre.
    /// </remarks>
    /// <param name="kind">Surface-domain command to refuse.</param>
    [Theory]
    [InlineData(AssetCommandKind.TransitTo)]
    [InlineData(AssetCommandKind.SetCourse)]
    [InlineData(AssetCommandKind.StationKeep)]
    [InlineData(AssetCommandKind.Dock)]
    [InlineData(AssetCommandKind.Undock)]
    public void A_Surface_Command_Is_Refused_At_The_Asset_With_No_State_Change(AssetCommandKind kind)
    {
        var rover = MovingRover();

        RefusedWithoutSideEffects(rover, Command(kind), "command.domain.surface");
    }

    /// <summary>A command addressed to another asset is refused before anything else is read.</summary>
    [Fact]
    public void A_Command_Addressed_To_Another_Asset_Is_Refused_With_No_State_Change()
    {
        var rover = MovingRover();

        RefusedWithoutSideEffects(
            rover,
            Command(AssetCommandKind.Stop) with { AssetId = "ugv-2" },
            "command.assetMismatch");
    }

    // ─── Shared setup ───────────────────────────────────────────────────────

    /// <summary>A rover already under way, so a refusal has real state to fail to disturb.</summary>
    /// <remarks>
    /// A rejection test against a rover sitting at its spawn point proves almost nothing: most
    /// fields are still at their initial values, so a mutation would have to be very unlucky to
    /// show up. Driving first gives the comparison a non-trivial pose, speed, steering angle and
    /// target to protect.
    /// </remarks>
    /// <param name="profile">Physical envelope to integrate with, or null for the class's own.</param>
    /// <returns>A harness whose rover is moving.</returns>
    private static RoverHarness MovingRover(GroundProfile? profile = null)
    {
        var rover = CreateRover(profile: profile);

        rover.Asset.Apply(Command(AssetCommandKind.DriveTo, new Vector3(0f, 0f, -60f)))
            .IsAccepted.Should().BeTrue();

        rover.Step(20);
        rover.GroundState().GroundSpeedMps.Should().BePositive(
            "the fixture is only useful if the rover really is under way");

        return rover;
    }
}
