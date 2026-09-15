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

using System;

using FluentAssertions;

using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;

using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>A command has to be able to end, and to end only once.</summary>
/// <remarks>
/// Before <see cref="AssetCommandLog.Settle"/> existed, only <c>Requested</c>, <c>Accepted</c> and
/// <c>Rejected</c> were ever assigned anywhere in the codebase. <c>InProgress</c> had no writer at
/// all, and the four terminal states appeared exactly once each — together, inside the
/// <c>IsTerminal</c> predicate that tests for them. That predicate is called, so a live completion
/// check ran over five states of which four could not occur: "has this command finished?" could
/// only ever answer "was it rejected?".
/// </remarks>
public sealed class CommandLifecycleTests
{
    private const string Key = "idem-key-1";

    private static (AssetCommandLog Log, Guid CommandId) Accepted(DateTimeOffset at)
    {
        var log = new AssetCommandLog();
        var id = Guid.NewGuid();
        log.Record(CommandResult.Accepted(id, at));
        return (log, id);
    }

    [Fact]
    public void An_Accepted_Command_Can_Reach_Succeeded()
    {
        var at = DateTimeOffset.UnixEpoch;
        var (log, id) = Accepted(at);

        log.Settle(id, CommandState.Succeeded, at.AddSeconds(30), message: "arrived")
            .Should().BeTrue();

        log.TryGet(id, out var result).Should().BeTrue();
        result!.State.Should().Be(CommandState.Succeeded);
        result.IsTerminal.Should().BeTrue();
        result.ProgressPercent.Should().Be(100,
            "a command that succeeded and reports part-way progress is a contradiction");
        result.AcceptedAt.Should().Be(at, "the acceptance time carries forward");
    }

    [Theory]
    [InlineData(CommandState.Failed)]
    [InlineData(CommandState.Cancelled)]
    [InlineData(CommandState.TimedOut)]
    public void Each_Terminal_State_Is_Reachable_And_Carries_A_Reason(CommandState terminal)
    {
        // Every one of these had zero writers. Driving each separately is what stops a fix that
        // only ever exercises the success path from reading as a working lifecycle.
        var at = DateTimeOffset.UnixEpoch;
        var (log, id) = Accepted(at);

        log.Settle(id, terminal, at.AddSeconds(5)).Should().BeTrue();

        log.TryGet(id, out var result).Should().BeTrue();
        result!.State.Should().Be(terminal);
        result.IsTerminal.Should().BeTrue();
        result.ReasonCode.Should().NotBeNullOrWhiteSpace(
            "a client that cannot see WHY a command ended cannot tell a refusal from a stuck vehicle");
    }

    [Fact]
    public void A_Terminal_Command_Does_Not_Move_Again()
    {
        // The asset that raised a completion may raise it again on a later tick, and a superseded
        // command that then arrives somewhere must stay cancelled rather than turning into a
        // success.
        var at = DateTimeOffset.UnixEpoch;
        var (log, id) = Accepted(at);

        log.Settle(id, CommandState.Cancelled, at.AddSeconds(1),
            CommandTerminalReasons.Superseded, "replaced").Should().BeTrue();
        log.Settle(id, CommandState.Succeeded, at.AddSeconds(2)).Should().BeFalse(
            "a command that already ended cannot end differently");

        log.TryGet(id, out var result).Should().BeTrue();
        result!.State.Should().Be(CommandState.Cancelled);
        result.ReasonCode.Should().Be(CommandTerminalReasons.Superseded);
    }

    [Fact]
    public void An_Unknown_Command_Is_Ignored_Rather_Than_Throwing()
    {
        // Results are evicted after MaxTrackedResults, so an asset finishing an old command is an
        // expected race rather than a fault.
        var log = new AssetCommandLog();
        log.Settle(Guid.NewGuid(), CommandState.Succeeded, DateTimeOffset.UnixEpoch)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(CommandState.Requested)]
    [InlineData(CommandState.Accepted)]
    [InlineData(CommandState.InProgress)]
    public void A_Non_Terminal_State_Is_Refused(CommandState notTerminal)
    {
        // Settle means "move this command to its end". Accepting a non-terminal state here would
        // make that contract false for some calls, and a caller could silently un-finish a command.
        var at = DateTimeOffset.UnixEpoch;
        var (log, id) = Accepted(at);

        log.Settle(id, notTerminal, at.AddSeconds(1)).Should().BeFalse();

        log.TryGet(id, out var result).Should().BeTrue();
        result!.State.Should().Be(CommandState.Accepted, "the command must be left as it was");
    }

    [Fact]
    public void A_Rejected_Command_Cannot_Be_Settled()
    {
        // Rejected is already terminal: the command never started. Letting it be settled would
        // erase the distinction between "refused" and "attempted and failed".
        var log = new AssetCommandLog();
        var id = Guid.NewGuid();
        log.Record(CommandResult.Rejected(id, "payload.kindUnknown", "no such kind"));

        log.Settle(id, CommandState.Succeeded, DateTimeOffset.UnixEpoch).Should().BeFalse();

        log.TryGet(id, out var result).Should().BeTrue();
        result!.State.Should().Be(CommandState.Rejected);
    }

    // ---- the supersession slot, tested against Complete itself ----
    //
    // Both of the guards below were unreachable through the controller when they were written,
    // and each one hid the other: the two rejection call sites pass no asset id, so
    // `!string.IsNullOrEmpty(assetId)` was already false there — and reverting *either* conjunct
    // alone left every test green, because the surviving one still short-circuited the same path.
    // They are not redundant in general (one covers a null asset id on a live result, the other a
    // terminal result carrying an asset id), so the fix is to reach them directly rather than to
    // delete one and call the other sufficient.

    private const string Asset = "ugv-1";

    private static (AssetCommandLog Log, AssetCommandLogSession Session) OpenLog()
    {
        var log = new AssetCommandLog();
        return (log, log.OpenSession());
    }

    [Fact]
    public void A_Terminal_Result_Neither_Supersedes_Nor_Claims_The_In_Flight_Slot()
    {
        var at = DateTimeOffset.UnixEpoch;
        var (log, session) = OpenLog();

        var live = Guid.NewGuid();
        session.Complete(
            CommandResult.Accepted(live, at), "key-live", CommandState.Accepted, at, Asset)
            .Should().BeTrue();

        // A command that was refused never reached the vehicle. Passing its asset id here is the
        // call the controller does not currently make — and the reason Complete may not assume it.
        var refused = Guid.NewGuid();
        session.Complete(
            CommandResult.Rejected(refused, "payload.kindUnknown", "no such kind"),
            "key-refused", CommandState.Rejected, at.AddSeconds(1), Asset)
            .Should().BeTrue();

        log.TryGet(live, out var untouched).Should().BeTrue();
        untouched!.State.Should().Be(
            CommandState.Accepted,
            "a refused command supersedes nothing: it was never in flight");

        // And the slot still belongs to the live command, not to the refused one. Proven by the
        // next real command for this asset ending the live one — if the refusal had claimed the
        // slot, the genuinely live command would leak at Accepted for the rest of the session.
        var next = Guid.NewGuid();
        session.Complete(
            CommandResult.Accepted(next, at.AddSeconds(2)), "key-next",
            CommandState.Accepted, at.AddSeconds(2), Asset)
            .Should().BeTrue();

        log.TryGet(live, out var superseded).Should().BeTrue();
        superseded!.State.Should().Be(CommandState.Cancelled);
        superseded.ReasonCode.Should().Be(CommandTerminalReasons.Superseded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_Live_Result_With_No_Asset_Id_Is_Recorded_Without_Touching_The_Slot(string? assetId)
    {
        // `assetId` defaults to null on the session wrapper, so any caller that completes a
        // command without naming an asset takes this path. A dictionary keyed by a null string
        // throws, so dropping the emptiness check turns an ordinary completion into a 500.
        var at = DateTimeOffset.UnixEpoch;
        var (log, session) = OpenLog();

        var id = Guid.NewGuid();
        var complete = () => session.Complete(
            CommandResult.Accepted(id, at), "key-anon", CommandState.Accepted, at, assetId);

        complete.Should().NotThrow();
        log.TryGet(id, out var result).Should().BeTrue();
        result!.State.Should().Be(CommandState.Accepted);
    }

    // ---- the range the XML docs promise ----

    /// <summary>Every factory keeps progress inside the range it documents.</summary>
    /// <remarks>
    /// <c>Math.Clamp</c> returns NaN unchanged, so clamping alone never enforced this: a NaN
    /// handed to any factory was stored and published as a percentage that is not a number. The
    /// guarantee was written in the docs and checked nowhere — and the terminal factories forward
    /// whatever the previous result held, so one NaN would travel the whole lifecycle.
    /// </remarks>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void No_Factory_Publishes_Progress_Outside_Its_Documented_Range(double given)
    {
        var id = Guid.NewGuid();
        var at = DateTimeOffset.UnixEpoch;

        CommandResult[] results =
        [
            CommandResult.Progress(id, at, given),
            CommandResult.Failed(id, at, "r", "m", given),
            CommandResult.Cancelled(id, at, "r", "m", given),
            CommandResult.TimedOut(id, at, "r", "m", given),
        ];

        foreach (var result in results)
        {
            result.ProgressPercent.Should().BeInRange(
                0, 100, "{0} documents progress as a percentage", result.State);
        }
    }

    /// <summary>A NaN that reaches the log does not travel into the terminal result.</summary>
    /// <remarks>
    /// The factories are the only guard, and <c>Settle</c> forwards the stored figure into each
    /// of them — so this is the path a bad number would actually take.
    /// </remarks>
    [Fact]
    public void A_Nan_Progress_Does_Not_Survive_Into_A_Terminal_Result()
    {
        var at = DateTimeOffset.UnixEpoch;
        var (log, id) = Accepted(at);
        log.Record(CommandResult.Progress(id, at, double.NaN));

        log.Settle(id, CommandState.Failed, at.AddSeconds(1), "r", "m").Should().BeTrue();

        log.TryGet(id, out var settled).Should().BeTrue();
        settled!.ProgressPercent.Should().BeInRange(0, 100);
    }
}
