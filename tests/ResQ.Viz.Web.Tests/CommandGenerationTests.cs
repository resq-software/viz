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
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Commands stay bound to the world and command-log generation that admitted them.</summary>
public sealed class CommandGenerationTests
{
    private static readonly DateTimeOffset Now =
        new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Old authorization and identity cannot dispatch onto a same-id replacement.</summary>
    [Fact]
    public void Final_Dispatch_Rejects_A_Same_Id_Replacement_After_Initial_Authorization()
    {
        var room = CreateRoom();
        var oldAsset = new RecordingAsset("rover-1");
        room.TryAddAsset(oldAsset, out _).Should().BeTrue();
        var registry = new ControlAuthorityRegistry(
            TimeProvider.System, new ControlAuthorityOptions());
        var authority = registry.For(room);
        var oldLease = authority.Acquire(
            "rover-1", "old-holder", ControlRole.Operator, TimeSpan.FromMinutes(1)).Lease!;
        var candidate = room.CaptureCommandCandidate("rover-1");
        candidate.Should().NotBeNull();
        authority.IsHeldBy("rover-1", "old-holder", oldLease.LeaseId).Should().BeTrue();

        room.TryRemoveAsset("rover-1", out _).Should().BeTrue();
        var replacement = new RecordingAsset("rover-1");
        room.TryAddAsset(replacement, out _).Should().BeTrue();
        authority.Acquire(
            "rover-1", "new-holder", ControlRole.Operator, TimeSpan.FromMinutes(1))
            .IsAccepted.Should().BeTrue();

        var result = authority.DispatchCommand(
            "rover-1",
            "old-holder",
            oldLease.LeaseId,
            () => room.DispatchCommand(
                candidate!, room.Commands.OpenSession(),
                Envelope("old-holder-key", commandOrdinal: 4) with
                {
                    IssuerId = "old-holder",
                    ControlLeaseId = oldLease.LeaseId,
                },
                Now,
                Command("rover-1")));

        result.ReasonCode.Should().Be(CommandAuthorityReasons.NotHolder);
        oldAsset.Applied.Should().Be(0);
        replacement.Applied.Should().Be(0);
    }

    /// <summary>An uncontrolled command is still bound to the instance captured during resolution.</summary>
    [Fact]
    public void Uncontrolled_Dispatch_Rejects_A_Changed_Instance()
    {
        var room = CreateRoom();
        room.TryAddAsset(new RecordingAsset("rover-1"), out _).Should().BeTrue();
        var authority = new ControlAuthorityRegistry(
            TimeProvider.System, new ControlAuthorityOptions()).For(room);
        var candidate = room.CaptureCommandCandidate("rover-1");
        candidate.Should().NotBeNull();

        room.TryRemoveAsset("rover-1", out _).Should().BeTrue();
        var replacement = new RecordingAsset("rover-1");
        room.TryAddAsset(replacement, out _).Should().BeTrue();

        var result = authority.DispatchCommand(
            "rover-1", "console", leaseId: null,
            () => room.DispatchCommand(
                candidate!, room.Commands.OpenSession(),
                Envelope("uncontrolled-key", commandOrdinal: 5),
                Now,
                Command("rover-1")));

        result.ReasonCode.Should().Be(CommandAuthorityReasons.AssetInstanceChanged);
        replacement.Applied.Should().Be(0);
    }

    /// <summary>A preemption in the approval-to-dispatch gap stays a preemption and claims nothing.</summary>
    [Fact]
    public void Final_Authority_Refusal_Preserves_Preemption_And_Leaves_The_Key_Retryable()
    {
        var room = CreateRoom();
        var asset = new RecordingAsset("rover-1");
        room.TryAddAsset(asset, out _).Should().BeTrue();
        var authority = new ControlAuthorityRegistry(
            TimeProvider.System, new ControlAuthorityOptions()).For(room);
        var oldLease = authority.Acquire(
            "rover-1", "old-holder", ControlRole.Operator, TimeSpan.FromMinutes(1)).Lease!;
        var candidate = room.CaptureCommandCandidate("rover-1");
        var envelope = Envelope("preempted-key", commandOrdinal: 3) with
        {
            IssuerId = "old-holder",
            ControlLeaseId = oldLease.LeaseId,
        };
        var session = room.Commands.OpenSession();
        authority.IsHeldBy("rover-1", "old-holder", oldLease.LeaseId).Should().BeTrue();

        authority.Preempt(
            "rover-1", "incident-command", ControlRole.Emergency,
            TimeSpan.FromMinutes(1), "Immediate safety response.")
            .IsAccepted.Should().BeTrue();

        var result = authority.DispatchCommand(
            "rover-1",
            "old-holder",
            oldLease.LeaseId,
            () => room.DispatchCommand(
                candidate!, session, envelope, Now, Command("rover-1")));

        result.ReasonCode.Should().Be(CommandAuthorityReasons.LeasePreempted);
        asset.Applied.Should().Be(0);
        room.Commands.OpenSession().Classify(envelope, Now)
            .Outcome.Should().Be(CommandIdempotencyOutcome.New);
    }

    /// <summary>A stale log session cannot repopulate results or claim keys after clear.</summary>
    [Fact]
    public async Task Old_Log_Session_Cannot_Write_After_A_New_Generation_Is_Committed()
    {
        var log = new AssetCommandLog();
        var oldSession = log.OpenSession();
        var envelope = Envelope("same-key", commandOrdinal: 1);
        oldSession.Claim(envelope, Now).Outcome.Should().Be(CommandIdempotencyOutcome.New);
        var oldRetry = oldSession.Classify(Envelope("same-key", commandOrdinal: 2), Now);
        oldRetry.Outcome.Should().Be(CommandIdempotencyOutcome.DuplicateInFlight);
        var oldAsset = new RecordingAsset("rover-1");
        var dispatched = new ManualResetEventSlim(false);
        var finish = new ManualResetEventSlim(false);

        var lateWrite = Task.Run(() =>
        {
            oldAsset.Apply(Command("rover-1")).IsAccepted.Should().BeTrue();
            dispatched.Set();
            if (!finish.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The stale command write was never released.");
            }

            oldSession.Record(CommandResult.Accepted(envelope.CommandId, Now));
            oldSession.Update(envelope.IdempotencyKey, CommandState.Accepted, Now);
        });
        dispatched.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        log.RecordDecision(
            CommandDecision.Accepted, Now, "trace-old", "rover-1", "console",
            commandId: envelope.CommandId, kind: envelope.Kind);
        log.Clear();
        oldSession.ResolveReplay(oldRetry, Now).IsCurrent.Should().BeFalse();
        finish.Set();
        await lateWrite.WaitAsync(TimeSpan.FromSeconds(5));

        log.TryGet(envelope.CommandId, out _).Should().BeFalse();
        oldAsset.Applied.Should().Be(1, "the old asset executed before its result became stale");
        var newSession = log.OpenSession();
        newSession.Classify(Envelope("same-key", commandOrdinal: 2), Now)
            .Outcome.Should().Be(CommandIdempotencyOutcome.New);
        log.ReadDecisions().Should().ContainSingle(record => record.CorrelationId == "trace-old");
    }

    private static SimulationRoom CreateRoom() =>
        new("command-generation-room", "127.0.0.0/24", NullLogger.Instance);

    private static SimulatedAssetCommand Command(string assetId) =>
        new(AssetCommandKind.Hold, assetId);

    private static AssetCommandEnvelope Envelope(string key, int commandOrdinal) =>
        new(
            CommandId: new Guid(commandOrdinal, 0, 0, new byte[8]),
            AssetId: "rover-1",
            Kind: "hold",
            IssuedAt: Now,
            Deadline: Now.AddMinutes(1),
            IssuerId: "console",
            ControlLeaseId: null,
            IdempotencyKey: key,
            Frame: null,
            Target: null,
            Constraints: null,
            Parameters: null);

    private sealed class RecordingAsset(string id) : ISimulatedAsset
    {
        public string AssetId => id;

        public AssetDomain Domain => AssetDomain.Ground;

        public Vector3 PositionEus => Vector3.Zero;

        public AssetDescriptor Descriptor { get; } = AssetProfiles.Create(id, VehicleClass.AckermannRover);

        public int Applied { get; private set; }

        public AssetState Capture(in AssetCaptureContext context) => new(
            AssetId,
            context.SourceTime,
            context.ReceiveTime,
            (ulong)Math.Max(0, context.Tick),
            DataFreshness.Fresh,
            new FramedPose(CoordinateFrame.LocalEus, null, PositionEus, Quaternion.Identity),
            new FramedTwist(CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero),
            OperationalState.Ready,
            "idle",
            new PowerState([], PercentRemaining: 100),
            new HealthState(ComponentHealthStatus.Nominal, [], [], "Nominal."),
            new LinkState(LinkTransport.Loopback, true),
            Mission: null,
            DomainState: null);

        public AssetCommandResult Apply(in SimulatedAssetCommand command)
        {
            Applied++;
            return AssetCommandResult.Accepted;
        }

        public IReadOnlyList<AssetEvent> DrainEvents() => [];
    }
}
