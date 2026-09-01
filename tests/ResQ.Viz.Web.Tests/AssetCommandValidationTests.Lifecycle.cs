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

using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Command lifecycle: transport acknowledgement is not physical completion.</summary>
/// <remarks>
/// Every case here drives a command from Accepted to a terminal state explicitly, so a shortcut
/// that reported success on acceptance would fail rather than pass quietly.
/// </remarks>
public sealed partial class AssetCommandValidationTests
{
    // ── Lifecycle: acknowledgement is not completion ───────────────────────────

    [Fact]
    public void An_Accepted_Command_Is_Not_A_Completed_One()
    {
        var result = CommandCatalog.Validate(
            EnvelopeFor(Definition(CommandKinds.GoTo)),
            DescriptorFor(AssetDomain.Air, AllCapabilities),
            StateFor(),
            Now);

        result.IsAccepted.Should().BeTrue();

        var accepted = result.ToCommandResult(Now);
        accepted.State.Should().Be(CommandState.Accepted);
        accepted.State.Should().NotBe(CommandState.Succeeded);
        accepted.IsTerminal.Should().BeFalse(
            "acceptance is a transport acknowledgement, not physical completion");
        accepted.ProgressPercent.Should().Be(0);
        accepted.AcceptedAt.Should().Be(Now);
        accepted.ReasonCode.Should().BeNull();

        var executing = CommandResult.Progress(accepted.CommandId, Now, 60);
        executing.State.Should().Be(CommandState.InProgress);
        executing.IsTerminal.Should().BeFalse();
        executing.AcceptedAt.Should().Be(Now, "the accept time survives every later update");

        var completed = accepted with { State = CommandState.Succeeded, ProgressPercent = 100 };
        completed.IsTerminal.Should().BeTrue("only the asset actually doing the thing ends the lifecycle");
    }

    [Fact]
    public void The_Idempotency_Ledger_Distinguishes_Acceptance_From_Completion()
    {
        var envelope = EnvelopeFor(Definition(CommandKinds.TransitTo));
        var ledger = new CommandIdempotencyLedger(Retention);
        var retry = envelope with { CommandId = DeterministicId("transitTo:retry") };

        ledger.Claim(envelope, Now).Outcome.Should().Be(CommandIdempotencyOutcome.New);
        ledger.Update(envelope.IdempotencyKey, CommandState.Accepted, Now);

        ledger.Claim(retry, Now + TimeSpan.FromSeconds(2)).Outcome.Should().Be(
            CommandIdempotencyOutcome.DuplicateInFlight,
            "an accepted command is still under way, so its result cannot simply be replayed");

        ledger.Update(envelope.IdempotencyKey, CommandState.Succeeded, Now + TimeSpan.FromSeconds(30));

        ledger.Claim(retry, Now + TimeSpan.FromSeconds(31)).Outcome.Should().Be(
            CommandIdempotencyOutcome.DuplicateCompleted, "only completion makes the result replayable");
    }

    [Fact]
    public void Reusing_A_Key_For_A_Different_Payload_Conflicts_And_Changes_Nothing()
    {
        var envelope = EnvelopeFor(Definition(CommandKinds.GoTo));
        var ledger = new CommandIdempotencyLedger(Retention);
        ledger.Claim(envelope, Now).Outcome.Should().Be(CommandIdempotencyOutcome.New);

        var different = envelope with
        {
            CommandId = DeterministicId("goTo:different"),
            AssetId = "asset-2",
        };

        var conflict = ledger.Claim(different, Now + TimeSpan.FromSeconds(1));
        conflict.Outcome.Should().Be(CommandIdempotencyOutcome.KeyReuseConflict);
        conflict.Existing?.CommandId.Should().Be(envelope.CommandId);

        // The conflicting claim is refused, and refusing it leaves the ledger as it was.
        var after = ledger.Classify(envelope, Now + TimeSpan.FromSeconds(2));
        after.Outcome.Should().Be(CommandIdempotencyOutcome.DuplicateInFlight);
        after.Existing?.CommandId.Should().Be(envelope.CommandId);
        after.Existing?.State.Should().Be(CommandState.Requested);
    }

    [Fact]
    public void The_Payload_Hash_Ignores_Fields_That_Only_A_Retry_Changes()
    {
        var envelope = EnvelopeFor(Definition(CommandKinds.DriveTo));
        var original = CommandIdempotency.ComputePayloadHash(envelope);

        var retry = envelope with
        {
            CommandId = DeterministicId("driveTo:retry"),
            IssuedAt = Now + TimeSpan.FromSeconds(9),
            Deadline = Now + TimeSpan.FromMinutes(45),
            ControlLeaseId = "lease-2",
        };

        CommandIdempotency.ComputePayloadHash(retry).Should().Be(
            original, "a retry is the same logical request with a new attempt id");

        CommandIdempotency.ComputePayloadHash(envelope with { IssuerId = "operator-2" }).Should().NotBe(
            original, "two operators picking the same key must collide loudly");
    }

    [Fact]
    public void An_Expired_Key_Is_Treated_As_Never_Seen()
    {
        var envelope = EnvelopeFor(Definition(CommandKinds.Hold));
        var record = new CommandIdempotencyRecord(
            envelope.IdempotencyKey,
            CommandIdempotency.ComputePayloadHash(envelope),
            DeterministicId("hold:original"),
            CommandState.Succeeded,
            Now);

        CommandIdempotency.Decide(record, record.PayloadHash, Now + Retention, Retention)
            .Should().Be(
                CommandIdempotencyOutcome.DuplicateCompleted, "the entry is still inside the window");

        CommandIdempotency.Decide(
                record, record.PayloadHash, Now + Retention + TimeSpan.FromSeconds(1), Retention)
            .Should().Be(
                CommandIdempotencyOutcome.New, "retention only has to outlive a client's retry budget");
    }

    [Fact]
    public void An_Unknown_Kind_Is_Refused_Before_Anything_Is_Looked_Up()
    {
        var envelope = EnvelopeFor(Definition(CommandKinds.GoTo)) with { Kind = "TakeOff" };

        var result = CommandCatalog.Validate(envelope, descriptor: null, state: null, Now);

        AssertRejectedCleanly(result, "a mis-cased kind");
        result.ReasonCode.Should().Be(
            CommandRejectionReasons.KindUnknown,
            "kinds are matched ordinally, so a wrong casing is unknown rather than a near miss");
        AssetCommandTranslator.ToAssetCommandKind("TakeOff").Should().Be(AssetCommandKind.Unspecified);
    }
}
