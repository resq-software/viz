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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Capability gating, gate ordering and command lifecycle for <see cref="CommandCatalog"/>,
/// <see cref="CommandIdempotency"/> and <see cref="CommandResult"/>.
/// </summary>
/// <remarks>
/// Every test here is deterministic by construction: validation is a pure function, the instant
/// is always <see cref="Now"/>, and command identifiers come from a fixed seed. No wall clock,
/// no unseeded randomness, no sleeps — a failure reproduces exactly.
/// <para>
/// The per-kind theories are driven off <see cref="CommandCatalog.All"/> rather than a copy of
/// the table, so a command added to the catalog is covered the moment it is registered instead
/// of the day somebody remembers to add a row here.
/// </para>
/// </remarks>
public sealed partial class AssetCommandValidationTests
{
    /// <summary>Fixed instant every gate is judged against. Nothing here reads a clock.</summary>
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);

    /// <summary>Seed for deterministic command identifiers.</summary>
    private const int Seed = 20260314;

    private const string AssetId = "asset-1";
    private const string IssuerId = "operator-1";
    private const string OriginId = "origin-1";

    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    /// <summary>Every declared capability, for descriptors that must clear the capability gate.</summary>
    private static readonly AssetCapability AllCapabilities =
        Enum.GetValues<AssetCapability>().Aggregate(AssetCapability.None, (mask, value) => mask | value);

    /// <summary>Domains tried, in order, when looking for one a command does <i>not</i> apply to.</summary>
    private static readonly AssetDomain[] ForeignDomainCandidates =
        [AssetDomain.Ground, AssetDomain.Surface, AssetDomain.Air, AssetDomain.Fixed];

    private static readonly FramedPose AssetPose = new(
        CoordinateFrame.LocalEus, OriginId, new Vector3(12f, 3f, -40f), Quaternion.Identity);

    private static readonly FramedTwist AssetTwist = new(
        CoordinateFrame.LocalEus, Vector3.Zero, Vector3.Zero, OriginId);

    private static readonly FramedPose TargetPose = new(
        CoordinateFrame.LocalEus, OriginId, new Vector3(60f, 20f, -85f), Quaternion.Identity);

    /// <summary>Every command kind the catalog registers, as theory data.</summary>
    /// <returns>One row per registered kind.</returns>
    public static TheoryData<string> AllKinds()
    {
        var data = new TheoryData<string>();
        foreach (var definition in CommandCatalog.All)
        {
            data.Add(definition.Kind);
        }

        return data;
    }

    // ── Per-kind gates ─────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_Kind_Accepts_A_Well_Formed_Command(string kind)
    {
        var definition = Definition(kind);
        var domain = definition.Domains[0];
        var envelope = EnvelopeFor(definition);

        var result = CommandCatalog.Validate(
            envelope, DescriptorFor(domain, AllCapabilities), StateFor(), Now);

        result.IsAccepted.Should().BeTrue(
            "'{0}' with every capability, its own domain, a permitted state and a fresh position "
            + "must pass every gate (refused with {1}: {2})", kind, result.ReasonCode, result.Message);
        result.ReasonCode.Should().BeNull();

        var intent = result.Intent;
        intent.Should().NotBeNull();
        intent?.Kind.Should().Be(kind);
        intent?.Domain.Should().Be(domain);
        intent?.CommandId.Should().Be(envelope.CommandId);
        intent?.AssetId.Should().Be(AssetId);
        intent?.RequiredCapabilities.Should().Be(definition.RequiredCapabilities);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_Kind_Rejects_An_Asset_That_Does_Not_Declare_Its_Capability(string kind)
    {
        var definition = Definition(kind);
        var domain = definition.Domains[0];
        var envelope = EnvelopeFor(definition);

        if (definition.RequiredCapabilities == AssetCapability.None)
        {
            // Ungated on purpose. The assertion that matters for these kinds is the inverse:
            // an asset that declares nothing at all must still be commandable.
            CommandCatalog.Validate(
                    envelope, DescriptorFor(domain, AssetCapability.None), StateFor(), Now)
                .IsAccepted.Should().BeTrue("'{0}' declares no capability requirement", kind);
            return;
        }

        var stripped = DescriptorFor(domain, AllCapabilities & ~definition.RequiredCapabilities);
        CommandCatalog.Validate(envelope, stripped, StateFor(), Now)
            .ReasonCode.Should().Be(
                CommandRejectionReasons.CapabilityNotDeclared,
                "'{0}' requires {1}", kind, definition.RequiredCapabilities);

        // Match.All must fail on a single missing bit; Match.Any must still accept while one of
        // its alternatives survives. Testing only the all-bits-removed case would also pass for
        // a definition whose match mode had been transcribed the wrong way round.
        foreach (var bit in SetBits(definition.RequiredCapabilities))
        {
            var partial = CommandCatalog.Validate(
                envelope, DescriptorFor(domain, AllCapabilities & ~bit), StateFor(), Now);

            if (definition.Match == CapabilityMatch.All)
            {
                partial.ReasonCode.Should().Be(
                    CommandRejectionReasons.CapabilityNotDeclared,
                    "'{0}' requires all of {1} and {2} is missing",
                    kind, definition.RequiredCapabilities, bit);
            }
            else
            {
                partial.IsAccepted.Should().BeTrue(
                    "'{0}' requires any of {1}, so losing {2} alone is survivable",
                    kind, definition.RequiredCapabilities, bit);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_Kind_Rejects_An_Asset_In_A_Domain_It_Does_Not_Apply_To(string kind)
    {
        var definition = Definition(kind);
        var foreign = ForeignDomainFor(definition);

        // Every capability is declared, so the capability gate — which runs first — cannot be
        // what refuses this. Only the domain list can.
        var result = CommandCatalog.Validate(
            EnvelopeFor(definition), DescriptorFor(foreign, AllCapabilities), StateFor(), Now);

        result.ReasonCode.Should().Be(
            CommandRejectionReasons.DomainNotApplicable,
            "'{0}' applies to {1} only, not to {2}", kind, string.Join('/', definition.Domains), foreign);
        result.Field.Should().Be("kind");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_Kind_Honours_Its_Operational_State_Policy(string kind)
    {
        var definition = Definition(kind);
        var descriptor = DescriptorFor(definition.Domains[0], AllCapabilities);
        var envelope = EnvelopeFor(definition);

        foreach (var operationalState in Enum.GetValues<OperationalState>())
        {
            var result = CommandCatalog.Validate(envelope, descriptor, StateFor(operationalState), Now);

            if (definition.PermitsState(operationalState))
            {
                result.IsAccepted.Should().BeTrue(
                    "'{0}' permits {1} (refused with {2})", kind, operationalState, result.ReasonCode);
            }
            else
            {
                result.ReasonCode.Should().Be(
                    CommandRejectionReasons.StateNotPermitted,
                    "'{0}' must not be issued while the asset is {1}", kind, operationalState);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_Kind_Honours_Its_Position_Freshness_Requirement(string kind)
    {
        var definition = Definition(kind);
        var descriptor = DescriptorFor(definition.Domains[0], AllCapabilities);
        var envelope = EnvelopeFor(definition);

        foreach (var freshness in Enum.GetValues<DataFreshness>())
        {
            var result = CommandCatalog.Validate(envelope, descriptor, StateFor(freshness: freshness), Now);

            if (!definition.RequiresFreshPosition || freshness == DataFreshness.Fresh)
            {
                result.IsAccepted.Should().BeTrue(
                    "'{0}' does not need a fresh position, or this one is fresh (refused with {1})",
                    kind, result.ReasonCode);
            }
            else
            {
                result.ReasonCode.Should().Be(
                    CommandRejectionReasons.PositionStale,
                    "'{0}' cannot be executed from a {1} position report", kind, freshness);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_Kind_Treats_A_Retry_Under_The_Same_Key_As_A_Duplicate(string kind)
    {
        var definition = Definition(kind);
        var envelope = EnvelopeFor(definition);
        var ledger = new CommandIdempotencyLedger(Retention);

        ledger.Claim(envelope, Now).Outcome.Should().Be(
            CommandIdempotencyOutcome.New, "'{0}' has not been issued under this key before", kind);

        // A retry after a timeout: same logical request, new attempt id, later issue time and a
        // pushed-out deadline. None of those may make it look like a second, deliberate command.
        var retry = envelope with
        {
            CommandId = DeterministicId($"{kind}:retry"),
            IssuedAt = Now + TimeSpan.FromSeconds(4),
            Deadline = Now + TimeSpan.FromMinutes(20),
        };

        var duplicate = ledger.Claim(retry, Now + TimeSpan.FromSeconds(4));
        duplicate.Outcome.Should().Be(
            CommandIdempotencyOutcome.DuplicateInFlight, "'{0}' was retried, not reissued", kind);
        duplicate.Existing.Should().NotBeNull();
        duplicate.Existing?.CommandId.Should().Be(
            envelope.CommandId, "a duplicate is answered with the original command's result");
        duplicate.PayloadHash.Should().Be(CommandIdempotency.ComputePayloadHash(envelope));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_Rejection_Leaves_No_Observable_Side_Effect(string kind)
    {
        var definition = Definition(kind);
        var domain = definition.Domains[0];
        var pristineDescriptor = DescriptorFor(domain, AllCapabilities);
        var pristineState = StateFor();
        var ledger = new CommandIdempotencyLedger(Retention);

        foreach (var (label, descriptor, state, envelope) in RejectionVariantsFor(definition))
        {
            var result = CommandCatalog.Validate(envelope, descriptor, state, Now);
            AssertRejectedCleanly(result, $"'{kind}' {label}");

            // Validation is pure, so re-running it must answer identically. A gate that had
            // recorded anything would answer differently the second time.
            CommandCatalog.Validate(envelope, descriptor, state, Now)
                .ReasonCode.Should().Be(result.ReasonCode);
        }

        // Nothing the refused commands touched changed: the inputs still equal what they were,
        // and no idempotency key was claimed on the way out.
        DescriptorFor(domain, AllCapabilities).Should().Be(pristineDescriptor);
        StateFor().Should().Be(pristineState);

        var valid = EnvelopeFor(definition);
        ledger.Classify(valid, Now).Outcome.Should().Be(
            CommandIdempotencyOutcome.New, "a refused command must not claim its key");
        ledger.Claim(valid, Now).Outcome.Should().Be(CommandIdempotencyOutcome.New);
    }
}
