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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The shipped scenario catalog as a whole: every preset in <c>appsettings.json</c> spawns what
/// it declares, every preset that shipped earlier still spawns exactly what it used to, and no
/// preset stages an asset in a state it cannot get out of.
/// </summary>
/// <remarks>
/// The per-domain suites each ask their question of the presets that domain added.
/// <see cref="GroundScenarioTests"/> checks the ground presets, <see cref="SurfaceScenarioTests"/>
/// the maritime ones, and both check that the presets predating them still spawn. Nothing asked
/// the question of the catalog itself, and that is where the failures this file exists for live.
/// <list type="number">
///   <item><description>
///     <b>A preset that silently comes up short.</b> A malformed row is skipped and logged rather
///     than thrown, which is the right behaviour for a data file read at startup and the wrong
///     one to leave unchecked: a misspelled class name turns a nine-asset scenario into an
///     eight-asset one and nothing in the running system says so. Every preset is therefore held
///     to the row count in the file it was read from, not to a number written here.
///   </description></item>
///   <item><description>
///     <b>An asset staged somewhere it cannot leave.</b> A rover on ground too steep to climb off
///     and a hull with the bed under its keel both serialise perfectly and demonstrate nothing.
///     Both have shipped in this repository. Every asset in every preset is spawned into a real
///     room on the terrain the preset was authored against, stepped, and then checked against
///     the water or ground the room itself samples.
///   </description></item>
///   <item><description>
///     <b>A catalog addition that quietly edits the catalog.</b> Presets are one configuration
///     section, so adding to it is an edit of the whole section. The identifiers every earlier
///     preset spawns, in order, are pinned as literals here — the one assertion in this file
///     deliberately not derived from the file under test, because deriving it would make the
///     pin agree with any edit at all.
///   </description></item>
/// </list>
/// <para>
/// Deterministic by construction: nothing sleeps, nothing reads a wall clock, every room is
/// stepped explicitly through <see cref="SimulationRoom.StepOnce"/>, and every threshold is
/// compared against a quantity the room publishes rather than a number copied out of it, so
/// retuning the terrain moves an asset and its expectation together.
/// </para>
/// </remarks>
public sealed partial class ScenarioCatalogTests
{
    /// <summary>Simulated ticks every preset is advanced before it is judged.</summary>
    /// <remarks>
    /// Two seconds at the 60 Hz step rate. Spawn-time state is not the interesting state: a hull
    /// floated onto the surface and a rover settled onto a slope both look fine in the frame they
    /// were created in, and a placement that is actually wrong shows up once the contact solver
    /// and the water constraints have run over it a few times.
    /// </remarks>
    private const int StepsBeforeJudging = 120;

    /// <summary>Air assets a single session admits, mirroring <c>SimV2Controller</c>'s cap.</summary>
    private const int SessionAirCap = 50;

    /// <summary>Assets of every domain a single session admits, mirroring <c>SimV2Controller</c>'s cap.</summary>
    private const int SessionAssetCap = 200;

    /// <summary>The immutable discovery catalog is derived from every validated configured row.</summary>
    [Fact]
    public void Scenario_Summaries_Match_All_Validated_Configured_Presets()
    {
        var service = new ScenarioService(AppConfiguration());
        var expected = new[]
        {
            Summary("single", 1, 0, 0, (VehicleClass.Multirotor, 1)),
            Summary("swarm-5", 5, 0, 0, (VehicleClass.Multirotor, 5)),
            Summary("swarm-20", 20, 0, 0, (VehicleClass.Multirotor, 20)),
            Summary("sar", 3, 0, 0, (VehicleClass.Multirotor, 3)),
            Summary("multi-agency-sar", 12, 0, 0, (VehicleClass.Multirotor, 12)),
            Summary("wildfire-interface", 5, 0, 0, (VehicleClass.Multirotor, 5)),
            Summary("hurricane-melissa", 6, 0, 0, (VehicleClass.Multirotor, 6)),
            Summary("flood-riverine", 5, 0, 0, (VehicleClass.Multirotor, 5)),
            Summary("urban-collapse", 6, 0, 0, (VehicleClass.Multirotor, 6)),
            Summary("alpine-sar", 4, 0, 0, (VehicleClass.Multirotor, 4)),
            Summary("canyon-sar", 4, 0, 0, (VehicleClass.Multirotor, 4)),
            Summary(
                "mixed-ground", 3, 3, 0,
                (VehicleClass.Multirotor, 3), (VehicleClass.AckermannRover, 1),
                (VehicleClass.DifferentialRover, 1), (VehicleClass.TrackedRover, 1)),
            Summary(
                "ground-convoy", 1, 3, 0,
                (VehicleClass.Multirotor, 1), (VehicleClass.AckermannRover, 1),
                (VehicleClass.DifferentialRover, 1), (VehicleClass.TrackedRover, 1)),
            Summary(
                "coastal-search", 3, 2, 3,
                (VehicleClass.Multirotor, 3), (VehicleClass.AckermannRover, 1),
                (VehicleClass.TrackedRover, 1), (VehicleClass.SurfaceVessel, 3)),
            Summary(
                "coastal-transit", 1, 0, 3,
                (VehicleClass.Multirotor, 1), (VehicleClass.SurfaceVessel, 3)),
            Summary(
                "flood-response", 3, 3, 2,
                (VehicleClass.Multirotor, 3), (VehicleClass.AckermannRover, 1),
                (VehicleClass.DifferentialRover, 1), (VehicleClass.TrackedRover, 1),
                (VehicleClass.SurfaceVessel, 2)),
            Summary(
                "port-incident", 2, 3, 3,
                (VehicleClass.Multirotor, 2), (VehicleClass.AckermannRover, 2),
                (VehicleClass.TrackedRover, 1), (VehicleClass.SurfaceVessel, 3)),
            Summary(
                "link-loss-divergence", 1, 1, 1,
                (VehicleClass.Multirotor, 1), (VehicleClass.AckermannRover, 1),
                (VehicleClass.SurfaceVessel, 1)),
            Summary(
                "mixed-load-150", 50, 50, 50,
                (VehicleClass.Multirotor, 50), (VehicleClass.AckermannRover, 17),
                (VehicleClass.DifferentialRover, 17), (VehicleClass.TrackedRover, 16),
                (VehicleClass.SurfaceVessel, 50)),
        };

        service.ScenarioSummaries.Should().BeEquivalentTo(expected);

        var summaries = service.ScenarioSummaries.Should()
            .BeAssignableTo<IList<ScenarioSummary>>().Subject;
        Action clearSummaries = () => summaries.Clear();
        clearSummaries.Should().Throw<NotSupportedException>();

        var classCounts = service.ScenarioSummaries[0].VehicleClassCounts.Should()
            .BeAssignableTo<IDictionary<string, int>>().Subject;
        Action clearClassCounts = () => classCounts.Clear();
        clearClassCounts.Should().Throw<NotSupportedException>();
    }

    private static ScenarioSummary Summary(
        string name,
        int air,
        int ground,
        int surface,
        params (VehicleClass Class, int Count)[] classes) =>
        new(
            Name: name,
            AssetCount: air + ground + surface,
            DomainCounts: new ScenarioDomainCounts(air, ground, surface),
            VehicleClassCounts: classes.ToDictionary(x => x.Class.ToString(), x => x.Count));

    // ─── Every preset spawns what it declares ───────────────────────────────

    /// <summary>A preset spawns one asset per configured row, in order, in the declared domain.</summary>
    /// <remarks>
    /// Held to the file rather than to a count written here, so a row added to a preset is
    /// covered the moment it is added. What this catches is the silent shortfall: a row whose
    /// identifier repeats one above it, whose coordinate is unparseable, or whose class this
    /// build ships no motion model for is skipped at load with a log line nobody reads, and the
    /// scenario simply comes up one vehicle short.
    /// </remarks>
    /// <param name="preset">Preset name.</param>
    /// <param name="terrain">Terrain preset the scenario was authored against.</param>
    [Theory]
    [MemberData(nameof(CatalogPresets))]
    public void Every_Preset_Spawns_One_Asset_Per_Configured_Row(string preset, string terrain)
    {
        var configuration = AppConfiguration();
        var rows = configuration.GetSection($"Scenarios:{preset}").GetChildren().ToList();
        rows.Should().NotBeEmpty($"'{preset}' must still be present in appsettings.json");

        var room = CreateRoom(terrain);
        new ScenarioService(configuration).TryRun(preset, room).Should().BeTrue();

        var frame = room.CaptureAssetFrame();

        frame.Descriptors.Select(d => d.AssetId).Should().Equal(
            rows.Select(r => r["id"] ?? string.Empty),
            "every configured row must reach the world, in the order the preset lists it — a "
            + "row skipped at load leaves a scenario silently short of a vehicle");

        frame.Descriptors.Select(d => d.Domain).Should().Equal(
            rows.Select(r => AssetProfiles.DomainFor(ClassOf(r))),
            "a row's domain is derived from its class rather than trusted from configuration, so "
            + "an asset filtered as one kind of thing and simulated as another is impossible");

        frame.Assets.Select(s => s.AssetId).Should().Equal(
            frame.Descriptors.Select(d => d.AssetId),
            "descriptors and states are two halves of one frame and must describe one population");
    }

    /// <summary>A preset that shipped earlier spawns exactly the assets it always did.</summary>
    /// <remarks>
    /// The literal pin. See <see cref="PresetsThatShippedEarlier"/> for why this one expectation
    /// is written out instead of derived.
    /// </remarks>
    /// <param name="preset">Preset name.</param>
    /// <param name="expectedIds">Identifiers the preset has always spawned, in order.</param>
    [Theory]
    [MemberData(nameof(PresetsThatShippedEarlier))]
    public void A_Preset_That_Shipped_Earlier_Spawns_Exactly_What_It_Always_Did(
        string preset, string[] expectedIds)
    {
        var room = CreateRoom(TerrainFor(preset));
        new ScenarioService(AppConfiguration()).TryRun(preset, room).Should().BeTrue();

        room.CaptureAssetFrame().Descriptors.Select(d => d.AssetId).Should().Equal(expectedIds);
    }

    /// <summary>Adding presets does not add, remove or rename a preset that already existed.</summary>
    /// <remarks>
    /// The pinned list covers what each earlier preset contains; this covers the section itself,
    /// which the per-preset cases cannot see. A preset deleted outright makes every case that
    /// names it vanish rather than fail, and <c>[Theory]</c> data that no longer matches anything
    /// is a silently shrinking suite.
    /// </remarks>
    [Fact]
    public void The_Catalog_Still_Contains_Every_Preset_It_Used_To()
    {
        var service = new ScenarioService(AppConfiguration());

        foreach (var name in PresetsThatShippedEarlier.Select(row => (string)row[0]))
        {
            service.HasScenario(name).Should().BeTrue(
                $"'{name}' shipped before this catalog pass and clients select it by name");
        }

        service.ScenarioNames.Should().BeEquivalentTo(
            CatalogPresets.Select(row => (string)row[0]),
            "the presets this suite verifies must be the presets the host actually loads — a "
            + "preset present in configuration but absent here is a preset nothing checks");
    }

    // ─── No preset stages an asset it cannot recover ────────────────────────

    /// <summary>Every asset in every preset is in a state it can act and move out of.</summary>
    /// <remarks>
    /// Three domain-specific checks against quantities the room itself publishes, because "valid"
    /// means something different in each medium. An aircraft must be clear of the ground under
    /// it. A rover must be neither latched immobilised nor derated to a standstill, and must
    /// declare the capability that reverses it out of trouble, since a recovery an operator
    /// cannot reach is not a recovery. A hull must be inside the water mask with genuine
    /// clearance under its keel rather than merely a positive number, and its three water
    /// quantities — depth, draft, clearance — must agree, because a clearance that disagrees with
    /// the depth and draft it is drawn from is how a hull gets reported clear of a bed it is
    /// sitting on.
    /// </remarks>
    /// <param name="preset">Preset name.</param>
    /// <param name="terrain">Terrain preset the scenario was authored against.</param>
    [Theory]
    [MemberData(nameof(CatalogPresets))]
    public void No_Preset_Stages_An_Asset_In_An_Unrecoverable_State(string preset, string terrain)
    {
        var room = CreateRoom(terrain);
        new ScenarioService(AppConfiguration()).TryRun(preset, room).Should().BeTrue();

        for (int i = 0; i < StepsBeforeJudging; i++)
        {
            room.StepOnce();
        }

        var frame = room.CaptureAssetFrame();
        var capabilities = frame.Descriptors.ToDictionary(d => d.AssetId, d => d.Capabilities);

        foreach (var state in frame.Assets)
        {
            switch (state.DomainState)
            {
                case AirDomainState air:
                    AssertClearOfTerrain(state.AssetId, air);
                    break;

                case GroundDomainState ground:
                    AssertDrivable(state.AssetId, ground, capabilities[state.AssetId]);
                    break;

                case SurfaceDomainState surface:
                    AssertAfloatWithClearance(state.AssetId, surface);
                    break;

                default:
                    state.DomainState.Should().NotBeNull(
                        $"'{state.AssetId}' publishes no domain state, so nothing about its "
                        + "medium can be checked and the client cannot narrow on it");
                    break;
            }
        }
    }

    /// <summary>Every preset is staged for the terrain a fresh session already has, or says otherwise.</summary>
    /// <remarks>
    /// Two presets are named after terrain presets they are not staged for. <c>alpine-sar</c> is
    /// staged for alpine and happens to agree; <c>canyon-sar</c> is not staged for canyon, whose
    /// floor lies below every altitude it names, and it is verified against the default terrain
    /// like every other preset whose comment names none. Stating that here keeps the pairing an
    /// asserted fact rather than an inference from a preset's name.
    /// </remarks>
    [Fact]
    public void Only_The_Maritime_Presets_Require_The_Operator_To_Change_Terrain()
    {
        var needsCoastal = CatalogPresets
            .Where(row => (string)row[1] == CoastalTerrain)
            .Select(row => (string)row[0]);

        needsCoastal.Should().Equal(
            ["coastal-search", "coastal-transit", "port-incident"],
            "only a preset that stages hulls needs water above the datum, and each of these says "
            + "so in its own comment — a preset that silently needed a terrain switch would look "
            + "like a broken preset instead of a missed operator step");
    }

    // ─── The mixed presets are actually mixed ───────────────────────────────

    /// <summary>A mixed preset reaches every domain it was added to exercise.</summary>
    /// <param name="preset">Preset name.</param>
    /// <param name="air">Air assets the preset must stage.</param>
    /// <param name="ground">Ground assets the preset must stage.</param>
    /// <param name="surface">Surface assets the preset must stage.</param>
    [Theory]
    [MemberData(nameof(MixedDomainPresets))]
    public void A_Mixed_Preset_Reaches_Every_Domain_It_Was_Added_For(
        string preset, int air, int ground, int surface)
    {
        var room = CreateRoom(TerrainFor(preset));
        new ScenarioService(AppConfiguration()).TryRun(preset, room).Should().BeTrue();

        var domains = room.CaptureAssetFrame().Descriptors
            .GroupBy(d => d.Domain)
            .ToDictionary(g => g.Key, g => g.Count());

        domains.GetValueOrDefault(AssetDomain.Air).Should().Be(air);
        domains.GetValueOrDefault(AssetDomain.Ground).Should().Be(ground);
        domains.GetValueOrDefault(AssetDomain.Surface).Should().Be(surface);
    }

    /// <summary>The link-loss preset shows three different failure behaviours side by side.</summary>
    /// <remarks>
    /// The whole reason that preset exists. Nothing in this build simulates a link drop —
    /// <see cref="LinkLossBehavior"/> is a policy each asset declares and republishes every frame
    /// — so the preset's job is to put one asset of each domain where all three declarations can
    /// be read at once. If they ever collapse onto a shared value the preset still runs, still
    /// spawns three assets, and demonstrates nothing.
    /// <para>
    /// The uncertainty growth rate is checked alongside, because it is the quantity the policies
    /// imply: a rover that stops has a last known position that stays true however stale the
    /// report, and a hull that cannot hold station does not.
    /// </para>
    /// <para>
    /// The rover is <b>parked explicitly</b>, and that matters. The ground assertion used to hold
    /// for free, because nothing in the build ever tasked a ground asset and every rover therefore
    /// sat at its spawn — so the test read as "a stopped rover" while actually measuring "a rover
    /// nobody had given anywhere to go". Once <see cref="GroundSurfaceCoordinator"/> began driving
    /// the ground fleet that premise evaporated and this failed, correctly: a rover under way does
    /// accumulate position error. Parking it restores the condition the assertion is about rather
    /// than relaxing the assertion to match whatever the fleet happens to be doing, and it
    /// exercises the operator-override path on the way past — a parked rover that the coordinator
    /// then retasked would fail here too.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Link_Loss_Preset_Publishes_A_Different_Failure_Behaviour_Per_Domain()
    {
        var room = CreateRoom(TerrainFor("link-loss-divergence"));
        new ScenarioService(AppConfiguration()).TryRun("link-loss-divergence", room).Should().BeTrue();

        var rover = room.CaptureAssetFrame().Descriptors
            .Should().ContainSingle(d => d.Domain == AssetDomain.Ground).Which;
        room.SendAssetCommand(new SimulatedAssetCommand(AssetCommandKind.Park, rover.AssetId))
            .IsAccepted.Should().BeTrue("the assertion below is about a rover that is stopped");

        for (int i = 0; i < StepsBeforeJudging; i++)
        {
            room.StepOnce();
        }

        var states = room.CaptureAssetFrame().Assets;

        var air = states.Select(s => s.DomainState).OfType<AirDomainState>().Should().ContainSingle().Which;
        var ground = states.Select(s => s.DomainState).OfType<GroundDomainState>().Should().ContainSingle().Which;
        var surface = states.Select(s => s.DomainState).OfType<SurfaceDomainState>().Should().ContainSingle().Which;

        new[] { air.LinkLossBehavior, ground.LinkLossBehavior, surface.LinkLossBehavior }
            .Should().OnlyHaveUniqueItems(
                "the preset exists to put three different link-loss policies in one view");

        ground.PositionUncertaintyGrowthMps.Should().Be(
            0.0, "a stopped rover's last reported position stays true however stale the report");

        surface.PositionUncertaintyGrowthMps.Should().BeGreaterThan(
            0.0, "a hull that cannot hold station drifts, so its uncertainty never settles");

        air.PositionUncertaintyGrowthMps.Should().BeGreaterThan(
            0.0, "a drone flying its link-loss profile accumulates error across the transit");
    }

    /// <summary>The load preset stages a full mixed fleet inside the caps a session enforces.</summary>
    /// <remarks>
    /// A load gate that exceeded the session caps would be measuring a population no operator can
    /// assemble through the API, so the split is pinned against the same two limits the v2 spawn
    /// endpoint refuses on.
    /// </remarks>
    [Fact]
    public void The_Load_Preset_Stages_A_Full_Mixed_Fleet_Inside_The_Session_Caps()
    {
        var room = CreateRoom(TerrainFor("mixed-load-150"));
        new ScenarioService(AppConfiguration()).TryRun("mixed-load-150", room).Should().BeTrue();

        var descriptors = room.CaptureAssetFrame().Descriptors;

        descriptors.Should().HaveCount(150);
        descriptors.Count(d => d.Domain == AssetDomain.Air).Should().BeLessThanOrEqualTo(SessionAirCap);
        descriptors.Should().HaveCountLessThanOrEqualTo(SessionAssetCap);

        descriptors.Select(d => d.VehicleClass).Distinct().Should().Contain(
            [
                VehicleClass.Multirotor, VehicleClass.AckermannRover,
                VehicleClass.DifferentialRover, VehicleClass.TrackedRover,
                VehicleClass.SurfaceVessel,
            ],
            "the gate is only a mixed-fleet gate if every motion model this build ships carries "
            + "part of the load");
    }
}
