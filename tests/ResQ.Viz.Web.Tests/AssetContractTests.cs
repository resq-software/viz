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

using System.Text.Json;
using FluentAssertions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Contract tests for the multi-domain asset model and the v2 frame.
/// </summary>
/// <remarks>
/// These assert the promises the model makes to anyone outside this assembly — the TypeScript
/// client, a recorded frame log, a future delta encoder — rather than any one implementation.
/// They are the tests that must fail if a later change quietly renumbers an enum, drops a
/// discriminator, replaces asset-id endpoints with list indices, or lets a command be gated on
/// vehicle class instead of declared capability.
/// <para>
/// Everything here is deterministic: fixed timestamps, fixed identifiers, and a fixed seed for
/// the one round-trip test that sweeps a range of values. No wall clock, no unseeded randomness
/// and no sleeps, so a failure is always a contract change and never a flake.
/// </para>
/// </remarks>
public partial class AssetContractTests
{
    // ─── Domain-state union ─────────────────────────────────────────────────

    /// <summary>Each concrete domain state serialises under its own stable discriminator.</summary>
    [Theory]
    [InlineData(AirDomainState.Discriminator)]
    [InlineData(GroundDomainState.Discriminator)]
    [InlineData(SurfaceDomainState.Discriminator)]
    public void DomainState_Serialises_With_Its_Discriminator(string discriminator)
    {
        IAssetDomainState state = DomainStateFor(discriminator);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(state, WireOptions));

        document.RootElement.TryGetProperty("type", out var type).Should().BeTrue(
            "the client narrows the union on a 'type' property it can see on the wire");
        type.ValueKind.Should().Be(JsonValueKind.String);
        type.GetString().Should().Be(discriminator);
    }

    /// <summary>Serialising then deserialising the union narrows back to the same concrete type.</summary>
    [Fact]
    public void DomainState_RoundTrips_And_Narrows_To_The_Air_Type()
    {
        IAssetDomainState original = SampleAir();

        var restored = FromJson<IAssetDomainState>(ToJson(original));

        restored.Should().BeOfType<AirDomainState>().Which.Should().Be(original);
    }

    /// <summary>The ground case, whose uncertainty growth is the one that is near zero when stopped.</summary>
    [Fact]
    public void DomainState_RoundTrips_And_Narrows_To_The_Ground_Type()
    {
        IAssetDomainState original = SampleGround();

        var restored = FromJson<IAssetDomainState>(ToJson(original));

        restored.Should().BeOfType<GroundDomainState>().Which.Should().Be(original);
    }

    /// <summary>The surface case, including its nested station-keeping goal.</summary>
    [Fact]
    public void DomainState_RoundTrips_And_Narrows_To_The_Surface_Type()
    {
        IAssetDomainState original = SampleSurface();

        var restored = FromJson<IAssetDomainState>(ToJson(original));

        var surface = restored.Should().BeOfType<SurfaceDomainState>().Which;
        surface.Should().Be(original);
        surface.StationKeep.Should().NotBeNull();
        surface.StationKeep!.IsDegraded.Should().BeTrue();
        surface.StationKeep.DegradedReason.Should().Be("current-exceeds-thrust");
    }

    /// <summary>
    /// The other direction: JSON minted elsewhere — a client, a replayed frame log — deserialises
    /// into the right concrete type with its fields intact.
    /// </summary>
    [Fact]
    public void DomainState_Deserialises_From_Foreign_Json_And_Narrows()
    {
        const string json = """
            {
              "type": "ground",
              "isMoving": true,
              "headingRad": 1.25,
              "courseOverGroundRad": 1.3,
              "groundSpeedMps": 2.5,
              "steeringAngleRad": 0.18,
              "rollRad": 0.04,
              "pitchRad": -0.09,
              "terrainElevationM": 112.5,
              "slopeRad": 0.21,
              "surfaceType": "vegetation",
              "tractionCoefficient": 0.62,
              "deratedSpeedLimitMps": 3.1,
              "rolloverRisk": 0.28,
              "isImmobilised": false,
              "linkLossBehavior": 2,
              "positionUncertaintyGrowthMps": 0.05,
              "immobilisationReason": null
            }
            """;

        var restored = FromJson<IAssetDomainState>(json);

        var ground = restored.Should().BeOfType<GroundDomainState>().Which;
        ground.Type.Should().Be(GroundDomainState.Discriminator);
        ground.SurfaceType.Should().Be("vegetation");
        ground.LinkLossBehavior.Should().Be(LinkLossBehavior.StopAndHold);
        ground.PositionUncertaintyGrowthMps.Should().Be(0.05);
        ground.ImmobilisationReason.Should().BeNull();
    }

    /// <summary>
    /// A discriminator for a reserved-but-unimplemented domain is refused outright rather than
    /// falling back to one of the three implemented shapes.
    /// </summary>
    [Fact]
    public void DomainState_Rejects_An_Unregistered_Discriminator()
    {
        const string json = """{"type":"subsurface","positionUncertaintyGrowthMps":0.4}""";

        var deserialise = () => FromJson<IAssetDomainState>(json);

        deserialise.Should().Throw<JsonException>(
            "silently narrowing an unknown domain to a known one would plan against the wrong physics");
    }

    /// <summary>The union survives being carried on an asset state, which is how it actually ships.</summary>
    [Fact]
    public void AssetState_Carries_The_Union_Through_A_RoundTrip()
    {
        // Pose and twist are deliberately not asserted here; they have their own test.
        var state = StateFor("vessel-1", domainState: SampleSurface());

        var restored = FromJson<AssetState>(ToJson(state));

        restored.Should().NotBeNull();
        restored!.AssetId.Should().Be("vessel-1");
        restored.SequenceNumber.Should().Be(4_294_967_296UL, "the counter is 64-bit on the wire");
        restored.DomainState.Should().BeOfType<SurfaceDomainState>()
            .Which.UnderKeelClearanceM.Should().Be(1.4);
    }

    /// <summary>Round-tripping is lossless across a swept range of values, not just one sample.</summary>
    [Fact]
    public void DomainState_RoundTrip_Is_Lossless_Across_A_Seeded_Sweep()
    {
        var random = new Random(RandomSeed);

        for (var i = 0; i < SweepIterations; i++)
        {
            IAssetDomainState original = RandomGround(random);

            var restored = FromJson<IAssetDomainState>(ToJson(original));

            restored.Should().BeOfType<GroundDomainState>().Which.Should().Be(original);
        }
    }

    // ─── Reserved enum values ───────────────────────────────────────────────

    /// <summary>
    /// Domain numbers are part of the wire contract, and the reserved subsurface slot exists so a
    /// later addition is additive rather than a renumber that misreads every recorded frame.
    /// </summary>
    [Fact]
    public void AssetDomain_Members_Keep_Their_Declared_Numbers()
    {
        Members<AssetDomain>().Should().Equal(
            "Unspecified=0", "Air=1", "Ground=2", "Surface=3", "Subsurface=4", "Fixed=5");

        ((int)AssetDomain.Subsurface).Should().Be(4, "the slot is reserved, not free");
    }

    /// <summary>
    /// Vehicle classes are banded by domain with gaps, so a new class slots into its own band.
    /// </summary>
    [Fact]
    public void VehicleClass_Members_Keep_Their_Declared_Numbers()
    {
        Members<VehicleClass>().Should().Equal(
            "Unspecified=0", "Multirotor=1", "FixedWing=2", "Vtol=3",
            "AckermannRover=10", "DifferentialRover=11", "TrackedRover=12", "LeggedRover=13",
            "SurfaceVessel=20", "Sailboat=21", "Rov=30", "Auv=31");

        ((int)VehicleClass.Rov).Should().Be(30);
        ((int)VehicleClass.Auv).Should().Be(31);
    }

    /// <summary>
    /// Reserved classes exist as values but cannot be spawned; nothing falls back to a generic
    /// profile.
    /// </summary>
    [Theory]
    [InlineData(VehicleClass.Rov)]
    [InlineData(VehicleClass.Auv)]
    [InlineData(VehicleClass.Sailboat)]
    [InlineData(VehicleClass.LeggedRover)]
    public void Reserved_VehicleClasses_Have_No_Profile(VehicleClass vehicleClass)
    {
        AssetProfiles.IsSupported(vehicleClass).Should().BeFalse();

        var resolve = () => AssetProfiles.CapabilitiesFor(vehicleClass);

        resolve.Should().Throw<ArgumentOutOfRangeException>(
            "a wrong capability set fails silently at the validator, so guessing one is worse than throwing");
    }

    /// <summary>
    /// Every capability is a distinct single bit, so masks stay composable as members are appended.
    /// </summary>
    [Fact]
    public void AssetCapability_Members_Are_Distinct_Single_Bits()
    {
        Members<AssetCapability>().Should().Equal(
            "None=0", "Arm=1", "Navigate2D=2", "Navigate3D=4", "Takeoff=8", "Land=16",
            "Reverse=32", "PivotTurn=64", "StationKeep=128", "Dock=256",
            "ManualControl=512", "MeshRelay=1024");

        ((ulong)AssetCapability.MeshRelay).Should().Be(1UL << 10);

        var bits = Enum.GetValues<AssetCapability>()
            .Where(c => c != AssetCapability.None)
            .Select(c => (ulong)c)
            .ToArray();
        bits.Should().OnlyHaveUniqueItems();
        bits.Should().AllSatisfy(bit => (bit & (bit - 1)).Should().Be(0UL));
    }
}
