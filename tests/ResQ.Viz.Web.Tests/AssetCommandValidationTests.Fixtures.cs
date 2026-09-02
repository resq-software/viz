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

namespace ResQ.Viz.Web.Tests;

/// <summary>Fixtures and helpers for <see cref="AssetCommandValidationTests"/>.</summary>
/// <remarks>
/// Every value is a literal — no clock, no unseeded randomness — so two calls a test apart build
/// identical records and a comparison against a pristine copy is a genuine no-side-effects check.
/// </remarks>
public sealed partial class AssetCommandValidationTests
{
    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static CommandDefinition Definition(string kind) =>
        CommandCatalog.TryGet(kind, out var definition)
            ? definition
            : throw new InvalidOperationException($"Command kind '{kind}' is not registered.");

    private static AssetDescriptor DescriptorFor(AssetDomain domain, AssetCapability capabilities) =>
        new(
            AssetId: AssetId,
            DisplayName: "Test asset",
            Domain: domain,
            VehicleClass: VehicleClassFor(domain),
            MobilityModel: "test",
            AgencyId: null,
            FleetId: null,
            Vendor: null,
            Model: null,
            Capabilities: capabilities,
            Dimensions: new PhysicalDimensions(1.0, 1.0, 1.0, 10.0, 0.5),
            // Deliberately generous, so a parameter range check never masks the gate under test.
            Motion: new MotionConstraints(0.0, 20.0, 0.0, CanStationKeep: true, 0.0, 0.0),
            VisualProfile: "test",
            Revision: 1);

    private static VehicleClass VehicleClassFor(AssetDomain domain) => domain switch
    {
        AssetDomain.Air => VehicleClass.Multirotor,
        AssetDomain.Ground => VehicleClass.AckermannRover,
        AssetDomain.Surface => VehicleClass.SurfaceVessel,
        _ => VehicleClass.Unspecified,
    };

    private static AssetState StateFor(
        OperationalState operationalState = OperationalState.Ready,
        DataFreshness freshness = DataFreshness.Fresh) =>
        new(
            AssetId: AssetId,
            SourceTime: Now - TimeSpan.FromMilliseconds(200),
            ReceiveTime: Now - TimeSpan.FromMilliseconds(100),
            SequenceNumber: 7,
            Freshness: freshness,
            Pose: AssetPose,
            Twist: AssetTwist,
            OperationalState: operationalState,
            Mode: "auto",
            Power: new PowerState([], PercentRemaining: 82.0),
            Health: new HealthState(ComponentHealthStatus.Nominal, [], [], "Nominal."),
            Link: new LinkState(LinkTransport.Loopback, IsConnected: true),
            Mission: null,
            DomainState: null);

    private static AssetCommandEnvelope EnvelopeFor(CommandDefinition definition) =>
        new(
            CommandId: DeterministicId($"{definition.Kind}:command"),
            AssetId: AssetId,
            Kind: definition.Kind,
            IssuedAt: Now,
            Deadline: Now + TimeSpan.FromMinutes(5),
            IssuerId: IssuerId,
            ControlLeaseId: null,
            IdempotencyKey: $"idem-{definition.Kind}",
            Frame: CoordinateFrame.LocalEus,
            Target: TargetFor(definition),
            Constraints: null,
            Parameters: ParametersFor(definition));

    private static CommandTarget? TargetFor(CommandDefinition definition)
    {
        var allowed = definition.AllowedTargets;

        if ((allowed & CommandTargetKinds.Point) != CommandTargetKinds.None)
        {
            return new PointCommandTarget(TargetPose, AcceptanceRadiusM: 2.0);
        }

        if ((allowed & CommandTargetKinds.Route) != CommandTargetKinds.None)
        {
            return new RouteCommandTarget("route-alpha", StartWaypointIndex: 0);
        }

        if ((allowed & CommandTargetKinds.Geo) != CommandTargetKinds.None)
        {
            return new GeoCommandTarget(
                new GeoPosition(40.7128, -74.0060, 12.0, VerticalReference.MeanSeaLevel),
                AcceptanceRadiusM: 2.0);
        }

        return (allowed & CommandTargetKinds.Asset) != CommandTargetKinds.None
            ? new AssetCommandTarget("station-1", StandoffM: 5.0)
            : null;
    }

    private static IReadOnlyDictionary<string, string>? ParametersFor(CommandDefinition definition)
    {
        if (definition.RequiredParameters.Count == 0)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in definition.RequiredParameters)
        {
            parameters[key] = ParameterValue(key);
        }

        return parameters;
    }

    // Invariant-culture decimal strings, inside every modelled profile's envelope: the slowest
    // platform tops out at 3.5 m/s and the fastest hull needs 0.6 m/s to keep steerage.
    private static string ParameterValue(string key) => key switch
    {
        CommandParameters.Speed => "3.0",
        CommandParameters.Altitude => "25.0",
        CommandParameters.Course => "1.5",
        CommandParameters.Steering => "0.25",
        CommandParameters.Radius => "40.0",
        _ => "1.0",
    };

    private static AssetDomain ForeignDomainFor(CommandDefinition definition)
    {
        foreach (var candidate in ForeignDomainCandidates)
        {
            if (!definition.AppliesTo(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"'{definition.Kind}' applies to every candidate domain, so it cannot be domain-gated.");
    }

    private static IEnumerable<AssetCapability> SetBits(AssetCapability mask)
    {
        foreach (var value in Enum.GetValues<AssetCapability>())
        {
            if (value != AssetCapability.None && (mask & value) == value)
            {
                yield return value;
            }
        }
    }

    /// <summary>One rejection per gate, so the no-side-effect contract is checked across all of them.</summary>
    private static IEnumerable<RejectionVariant> RejectionVariantsFor(CommandDefinition definition)
    {
        var domain = definition.Domains[0];
        var descriptor = DescriptorFor(domain, AllCapabilities);
        var envelope = EnvelopeFor(definition);

        yield return new RejectionVariant("with no descriptor", null, null, envelope);
        yield return new RejectionVariant("with no reported state", descriptor, null, envelope);
        yield return new RejectionVariant(
            "in a foreign domain",
            DescriptorFor(ForeignDomainFor(definition), AllCapabilities), StateFor(), envelope);
        yield return new RejectionVariant(
            "past its deadline", descriptor, StateFor(),
            envelope with { Deadline = Now - TimeSpan.FromSeconds(1) });
        yield return new RejectionVariant(
            "with no idempotency key", descriptor, StateFor(),
            envelope with { IdempotencyKey = "  " });
        yield return new RejectionVariant(
            "with no issuer", descriptor, StateFor(), envelope with { IssuerId = string.Empty });

        if (definition.RequiredCapabilities != AssetCapability.None)
        {
            yield return new RejectionVariant(
                "without its capability",
                DescriptorFor(domain, AllCapabilities & ~definition.RequiredCapabilities),
                StateFor(), envelope);
        }

        foreach (var operationalState in Enum.GetValues<OperationalState>())
        {
            if (!definition.PermitsState(operationalState))
            {
                yield return new RejectionVariant(
                    $"while {operationalState}", descriptor, StateFor(operationalState), envelope);
                break;
            }
        }

        if (definition.RequiresFreshPosition)
        {
            yield return new RejectionVariant(
                "from a stale position", descriptor, StateFor(freshness: DataFreshness.Stale), envelope);
        }
    }

    private static void AssertRejectedCleanly(CommandValidationResult result, string label)
    {
        result.IsAccepted.Should().BeFalse("{0} must be refused", label);
        result.Intent.Should().BeNull(
            "{0} must not produce an intent anything downstream could act on", label);
        result.ReasonCode.Should().NotBeNullOrWhiteSpace(
            "{0} must carry a machine-readable reason", label);
        result.Message.Should().NotBeNullOrWhiteSpace("{0} must carry operator-facing prose", label);

        var reported = result.ToCommandResult(Now);
        reported.State.Should().Be(CommandState.Rejected, "{0}", label);
        reported.AcceptedAt.Should().BeNull("{0} was never accepted, so it has no accept time", label);
        reported.ProgressPercent.Should().Be(0, "{0} never started", label);
        reported.IsTerminal.Should().BeTrue("{0} is refused for good", label);
        reported.ReasonCode.Should().Be(result.ReasonCode);

        var problem = result.ToProblem("trace-1");
        problem.Code.Should().Be(result.ReasonCode);
        problem.CommandId.Should().Be(result.CommandId);
        problem.TraceId.Should().Be("trace-1");
    }

    /// <summary>
    /// A command identifier that is a pure function of its discriminator and <see cref="Seed"/>,
    /// so a failing run reproduces with exactly the same identifiers.
    /// </summary>
    private static Guid DeterministicId(string discriminator)
    {
        var bytes = new byte[16];
        new Random(unchecked(Seed ^ (int)StableHash(discriminator))).NextBytes(bytes);
        return new Guid(bytes);
    }

    // Not string.GetHashCode: that is randomised per process, which would make the identifiers
    // differ between runs and defeat the point of seeding them at all.
    private static uint StableHash(string value)
    {
        var hash = 2166136261u;
        unchecked
        {
            foreach (var character in value)
            {
                hash = (hash ^ (uint)character) * 16777619u;
            }
        }

        return hash;
    }

    /// <summary>One way of getting a command refused, and the inputs that do it.</summary>
    /// <param name="Label">Human-readable description, used in assertion messages.</param>
    /// <param name="Descriptor">Descriptor to validate against, or null to simulate an unknown asset.</param>
    /// <param name="State">State to validate against, or null to simulate an asset that has reported none.</param>
    /// <param name="Envelope">The command as issued.</param>
    private sealed record RejectionVariant(
        string Label,
        AssetDescriptor? Descriptor,
        AssetState? State,
        AssetCommandEnvelope Envelope);
}
