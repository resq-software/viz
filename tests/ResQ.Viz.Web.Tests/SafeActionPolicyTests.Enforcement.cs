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
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

// The enforcement half of SafeActionPolicyTests: the governor's ledger and issue-once discipline,
// and the two places the policy's answers are held against a real executor's. Split from the pure
// halves because these tests drive assets rather than literals; the suite's summary lives on the
// primary declaration in SafeActionPolicyTests.cs.
public partial class SafeActionPolicyTests
{
    /// <summary>Rejection an executor returns for a command a latched asset will not take.</summary>
    private const string EmergencyStopped = "asset.emergencyStopped";

    /// <summary>Rejections both executors raise before they ever look at the latch.</summary>
    private static readonly string[] GatesBeforeTheLatch =
    [
        "command.assetMismatch",
        "command.domain.air",
        "command.domain.ground",
        "command.domain.surface",
        "capability.missing",
    ];

    /// <summary>A fallback is issued once for an outage, not once per sweep.</summary>
    /// <remarks>
    /// Re-commanding a returning drone to return sixty times a minute is the level-triggered
    /// mistake that has filled this repository's event log twice before, in a new place.
    /// </remarks>
    [Fact]
    public void The_Fallback_Is_Issued_Once_Per_Outage()
    {
        var asset = new RecordingAsset(Describe(VehicleClass.AckermannRover), State(Ground()));
        var governor = new SafeActionGovernor();

        governor.SetLinkAvailable(asset.AssetId, false).Should().BeTrue();

        foreach (double simTime in new[] { 0.0, 10.0, 20.0, 30.0 })
        {
            governor.Observe(asset, asset.Capture(default), environment: null, simTime);
        }

        asset.Applied.Should().Equal(AssetCommandKind.Stop);
    }

    /// <summary>An unexplained executor rejection is never reported as a successful fallback.</summary>
    [Fact]
    public void A_Rejection_Without_A_Reason_Is_Not_Recorded_As_Nominal()
    {
        var asset = new RecordingAsset(
            Describe(VehicleClass.AckermannRover),
            State(Ground()),
            new AssetCommandResult(IsAccepted: false, Reason: null));
        var governor = new SafeActionGovernor();

        governor.SetLinkAvailable(asset.AssetId, false);
        governor.Observe(asset, asset.Capture(default), environment: null, 0.0);

        var record = governor.Observe(
            asset, asset.Capture(default), environment: null, 10.0);

        record.AppliedCommand.Should().Be(AssetCommandKind.Stop);
        record.AppliedResult.Should().Be(
            SafeActionReasons.ExecutorRefused,
            "a rejected fallback without a reason needs the stable fallback token");
    }

    /// <summary>A link that drops twice produces two fallbacks, and restoring one produces none.</summary>
    [Fact]
    public void The_Fallback_Re_Arms_When_The_Link_Returns_And_Drops_Again()
    {
        var asset = new RecordingAsset(Describe(VehicleClass.AckermannRover), State(Ground()));
        var governor = new SafeActionGovernor();

        governor.SetLinkAvailable(asset.AssetId, false);
        governor.Observe(asset, asset.Capture(default), environment: null, 0.0);
        governor.Observe(asset, asset.Capture(default), environment: null, 10.0);

        asset.Applied.Should().HaveCount(1);

        governor.SetLinkAvailable(asset.AssetId, true);
        governor.Observe(asset, asset.Capture(default), environment: null, 20.0);

        asset.Applied.Should().HaveCount(
            1,
            "a link coming back is not an instruction: the system cannot know what the operator "
            + "now wants, and moving the asset on a guess would be worse than leaving it");

        governor.SetLinkAvailable(asset.AssetId, false);
        governor.Observe(asset, asset.Capture(default), environment: null, 30.0);

        asset.Applied.Should().Equal(AssetCommandKind.Stop, AssetCommandKind.Stop);
    }

    /// <summary>The accrued search radius grows for a hull and stays at zero for a stopped rover.</summary>
    /// <remarks>
    /// The accrued figure is the integral across observations rather than the current rate
    /// extrapolated, so it is the one an advisory search radius should be drawn from.
    /// </remarks>
    [Fact]
    public void The_Accrued_Radius_Grows_For_A_Vessel_And_Not_For_A_Rover()
    {
        var vessel = new RecordingAsset(
            Describe(VehicleClass.SurfaceVessel, "usv-1"), State(Surface()));

        var rover = new RecordingAsset(
            Describe(VehicleClass.AckermannRover, "ugv-1"), State(Ground()));
        var governor = new SafeActionGovernor();

        governor.SetLinkAvailable(vessel.AssetId, false);
        governor.SetLinkAvailable(rover.AssetId, false);

        foreach (double simTime in new[] { 0.0, 10.0 })
        {
            governor.Observe(vessel, vessel.Capture(default), environment: null, simTime);
            governor.Observe(rover, rover.Capture(default), environment: null, simTime);
        }

        var afloat = governor.Observe(vessel, vessel.Capture(default), environment: null, 20.0);
        var ashore = governor.Observe(rover, rover.Capture(default), environment: null, 20.0);

        afloat.AccruedPositionUncertaintyM.Should().BeApproximately(VesselDriftMps * 20.0, 1e-9);

        ashore.AccruedPositionUncertaintyM.Should().Be(
            0.0, "a rover that stopped where it lost the link is still exactly there");
    }

    /// <summary>Nothing the governor remembers outlives the asset it was remembered about.</summary>
    [Fact]
    public void Assets_That_Leave_The_World_Are_Forgotten()
    {
        var asset = new RecordingAsset(Describe(VehicleClass.AckermannRover), State(Ground()));
        var governor = new SafeActionGovernor();

        governor.SetLinkAvailable(asset.AssetId, false);
        governor.Observe(asset, asset.Capture(default), environment: null, 10.0);
        governor.RecordFor(asset.AssetId).Should().NotBeNull();

        governor.Retain([]);

        governor.RecordFor(asset.AssetId).Should().BeNull();

        governor.IsLinkAvailable(asset.AssetId).Should().BeTrue(
            "an identifier that comes back belongs to a different asset and starts clean");
    }

    /// <summary>A real rover carries out its declared behaviour and is still commandable after.</summary>
    /// <remarks>
    /// The end-to-end version of the suite's central claim, and the recoverability guarantee in
    /// the same test: the fallback is executed by the rover's own executor, and the very next
    /// operator command goes through as though nothing had happened.
    /// </remarks>
    [Fact]
    public void A_Real_Rover_Executes_Its_Declared_Behaviour_And_Stays_Commandable()
    {
        var ground = new Plateau();
        var rover = BuildRover(ground);
        var governor = new SafeActionGovernor();

        rover.Apply(DriveTo(rover.AssetId)).IsAccepted.Should().BeTrue();

        governor.SetLinkAvailable(rover.AssetId, false);
        governor.Observe(rover, Snapshot(rover, ground, 0.0), ground.Sample(rover.PositionEus, 1.0), 0.0);

        var record = governor.Observe(
            rover, Snapshot(rover, ground, 10.0), ground.Sample(rover.PositionEus, 1.0), 10.0);

        record.Assessment.DeclaredBehaviour.Should().Be(
            LinkLossBehavior.StopAndHold, "which is what the rover itself publishes");

        record.AppliedCommand.Should().Be(AssetCommandKind.Stop);

        record.AppliedResult.Should().Be(
            SafeActionReasons.Nominal, "the executor must accept what the policy resolved for it");

        rover.Apply(DriveTo(rover.AssetId)).IsAccepted.Should().BeTrue(
            "the safe-action layer latches nothing, so the next operator command is unaffected");
    }

    /// <summary>The policy's release set is exactly what each latching executor accepts.</summary>
    /// <remarks>
    /// The policy keeps its own list because it must answer before an executor is reached, and a
    /// second list is how the capability tables drifted apart once already. This holds the two in
    /// step over the whole command vocabulary and both latching domains, so a change to either
    /// fails here rather than stranding an asset in the field.
    /// </remarks>
    [Fact]
    public void The_Emergency_Release_Set_Matches_What_The_Executors_Accept()
    {
        foreach (var kind in AllCommandKinds)
        {
            AssertReleaseAgreement(kind, () => BuildRover(new Plateau()));
            AssertReleaseAgreement(kind, () => BuildVessel(new Basin()));
        }
    }

    /// <summary>Holds one command kind's latched behaviour against the policy's answer.</summary>
    /// <param name="kind">Command to probe.</param>
    /// <param name="build">Builds a fresh asset, because probing one changes it.</param>
    private static void AssertReleaseAgreement(AssetCommandKind kind, Func<ISimulatedAsset> build)
    {
        var unlatched = build();
        var baseline = unlatched.Apply(new SimulatedAssetCommand(kind, unlatched.AssetId));

        var latched = build();
        latched.Apply(new SimulatedAssetCommand(AssetCommandKind.EmergencyStop, latched.AssetId))
            .IsAccepted.Should().BeTrue("an emergency stop is never refused");

        var probed = latched.Apply(new SimulatedAssetCommand(kind, latched.AssetId));

        if (baseline.Reason is { } gate && GatesBeforeTheLatch.Contains(gate))
        {
            probed.Reason.Should().Be(
                gate, "'{0}' is refused before the latch is ever consulted", kind);
            return;
        }

        if (SafeActionPolicy.IsEmergencyRelease(kind))
        {
            probed.Reason.Should().NotBe(
                EmergencyStopped,
                "the policy says '{0}' releases the latch, so the executor must let it through — "
                + "a disagreement here is an asset that cannot be brought back",
                kind);
            return;
        }

        probed.IsAccepted.Should().BeFalse();

        probed.Reason.Should().Be(
            EmergencyStopped,
            "the policy says '{0}' is not a release, so the executor must refuse it while latched",
            kind);
    }

    /// <summary>A real Ackermann rover on flat dry ground.</summary>
    /// <param name="ground">Terrain it samples.</param>
    /// <returns>The rover.</returns>
    private static GroundAsset BuildRover(IEnvironmentSampler ground)
    {
        var profile = GroundProfile.ForVehicleClass(VehicleClass.AckermannRover)
            ?? throw new InvalidOperationException("The Ackermann class has no ground profile.");

        return new GroundAsset(
            Describe(VehicleClass.AckermannRover),
            GroundDynamics.For(profile),
            ground,
            Vector3.Zero);
    }

    /// <summary>A real displacement hull on deep still water.</summary>
    /// <param name="water">Basin it floats on.</param>
    /// <returns>The vessel.</returns>
    private static SurfaceAsset BuildVessel(IEnvironmentSampler water)
    {
        var profile = SurfaceProfile.ForVehicleClass(VehicleClass.SurfaceVessel)
            ?? throw new InvalidOperationException("The vessel class has no surface profile.");

        return new SurfaceAsset(
            Describe(VehicleClass.SurfaceVessel),
            SurfaceDynamics.For(profile),
            water,
            Vector3.Zero);
    }

    /// <summary>Projects a real asset onto the wire at a chosen simulation instant.</summary>
    /// <param name="asset">Asset to capture.</param>
    /// <param name="environment">Sampler it reads.</param>
    /// <param name="simulationTimeSeconds">Instant to stamp, in seconds.</param>
    /// <returns>The published state.</returns>
    private static AssetState Snapshot(
        ISimulatedAsset asset, IEnvironmentSampler environment, double simulationTimeSeconds) =>
        asset.Capture(new AssetCaptureContext(
            Environment: environment,
            SimulationTimeSeconds: simulationTimeSeconds,
            Tick: (long)simulationTimeSeconds,
            SourceTime: Epoch.AddSeconds(simulationTimeSeconds),
            ReceiveTime: Epoch.AddSeconds(simulationTimeSeconds),
            Origin: null));

    /// <summary>A drive command to a point forty metres north.</summary>
    /// <param name="assetId">Asset to address.</param>
    /// <returns>The translated command.</returns>
    private static SimulatedAssetCommand DriveTo(string assetId) =>
        new(
            Kind: AssetCommandKind.DriveTo,
            AssetId: assetId,
            Target: new FramedPose(
                CoordinateFrame.LocalEus,
                OriginId: null,
                Position: new Vector3(0f, 0f, -40f),
                Orientation: Quaternion.Identity));
}
