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
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Tracks;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// The surface domain end to end: a vessel reaches the world through both entry points, floats
/// where the water actually is, stays out of every v1 shape, and brings observed contacts with it
/// that nothing can command.
/// </summary>
/// <remarks>
/// Four failures this suite exists to catch, none of which is visible from inside any single
/// component.
/// <list type="number">
///   <item><description>
///     <b>Two registries.</b> A preset and <c>POST /api/v2/sim/assets</c> place assets in the
///     same world, so a class one can build and the other cannot is a contradiction an operator
///     sees as a preset that silently comes up short. That is a bug this repository has already
///     shipped once, which is why both paths are exercised here rather than one.
///   </description></item>
///   <item><description>
///     <b>Vessels staged out of the water.</b> A preset that puts a hull on dry land, or under a
///     terrain preset whose sea level does not cover its draft, produces an asset that serialises
///     perfectly and demonstrates nothing. Every vessel in every maritime preset is checked
///     against the bathymetry the room itself samples.
///   </description></item>
///   <item><description>
///     <b>Leakage into v1.</b> A vessel reaching a v1 <see cref="VizFrame"/> is handed to a
///     client with no geometry for it and no command vocabulary for it.
///   </description></item>
///   <item><description>
///     <b>A commandable contact.</b> An external track has a pose and a classification but no
///     capabilities and no control authority. Asserted structurally — over the controller's own
///     route table — because "we did not add that endpoint" is a property, not a promise.
///   </description></item>
/// </list>
/// <para>
/// Deterministic by construction. Nothing steps the world, nothing sleeps, and every position
/// assertion is made against water the room itself samples rather than a number copied out of it,
/// so retuning the terrain moves the vessels and the expectations together.
/// </para>
/// </remarks>
public sealed partial class SurfaceScenarioTests
{
    /// <summary>Terrain preset the maritime cases run on: the only one whose water is above the datum.</summary>
    private const string CoastalPreset = "coastal";

    /// <summary>Simulation time every built v1 frame is stamped with.</summary>
    private const double FrameSimTime = 12.5;

    /// <summary>Scene-frame spawn point used by the single-vessel cases, in metres.</summary>
    /// <remarks>
    /// In the channel west of the coastal preset's main island, the same water the shipped
    /// maritime presets stage on. Its <c>Y</c> is deliberately zero: the water surface here is at
    /// <see cref="ResQ.Viz.Web.Services.Assets.SeaLevel.CoastalM"/>, so a factory that honoured
    /// the requested height would submerge the hull, and this point is what makes that visible.
    /// </remarks>
    private static readonly Vector3 VesselSpawn = new(-775f, 0f, -100f);

    /// <summary>Maritime presets this build ships. Every vessel in each is checked for water under it.</summary>
    public static TheoryData<string> MaritimePresets => new() { "coastal-search", "coastal-transit" };

    /// <summary>Every preset that shipped before the surface domain existed.</summary>
    public static TheoryData<string> PreSurfacePresets => new()
    {
        "single", "swarm-5", "swarm-20", "sar", "multi-agency-sar", "wildfire-interface",
        "hurricane-melissa", "flood-riverine", "urban-collapse", "alpine-sar", "canyon-sar",
        "mixed-ground", "ground-convoy",
    };

    // ─── A vessel reaches the world through the v2 API ──────────────────────

    /// <summary>A vessel spawns through the v2 endpoint and appears in the asset snapshot.</summary>
    /// <remarks>
    /// The whole of what registering a motion model buys: the request that used to answer
    /// <c>501</c> with <see cref="AssetProblems.MobilityModelUnavailable"/> now answers
    /// <c>201</c>, and the asset is present in the frame rather than merely acknowledged.
    /// </remarks>
    [Fact]
    public void Spawning_A_Vessel_Through_The_V2_Api_Puts_It_In_The_Asset_Snapshot()
    {
        var (ctrl, room) = CreateController();

        var spawned = Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.SurfaceVessel, ScenePose(VesselSpawn), AssetId: "usv-1")));

        spawned.AssetId.Should().Be("usv-1");
        spawned.Descriptor.Domain.Should().Be(AssetDomain.Surface);
        spawned.Descriptor.VehicleClass.Should().Be(VehicleClass.SurfaceVessel);

        var frame = room.CaptureAssetFrame();
        frame.Descriptors.Select(d => d.AssetId).Should().Equal("usv-1");

        var state = frame.Assets.Should().ContainSingle().Which;
        state.AssetId.Should().Be("usv-1");
        state.DomainState.Should().BeOfType<SurfaceDomainState>(
            "the wire model narrows on the domain discriminator, so a vessel must carry one");
    }

    /// <summary>A spawned vessel floats on the water surface, not at the height the request named.</summary>
    /// <remarks>
    /// The request asks for <c>y = 0</c>, and on this preset the water is three metres above that,
    /// so honouring it would submerge the hull. A vessel's height is the water-surface elevation
    /// in force where it floats and is never commanded — this is the assertion that catches a
    /// factory which stopped floating, because a submerged vessel still serialises perfectly.
    /// </remarks>
    [Fact]
    public void A_Spawned_Vessel_Floats_On_The_Water_Surface_Not_The_Requested_Height()
    {
        var (ctrl, room) = CreateController();

        Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.SurfaceVessel, ScenePose(VesselSpawn), AssetId: "usv-1")));

        var state = room.CaptureAssetFrame().Assets.Should().ContainSingle().Which;

        AssertAfloatWithClearance(room, state);
        state.Pose.Position.Y.Should().NotBe(
            VesselSpawn.Y, "the requested height is discarded, not honoured");
    }

    /// <summary>The capability report for a real vessel offers the surface vocabulary and no other.</summary>
    /// <remarks>
    /// A capability report is a promise: a client rendering exactly these affordances must issue
    /// exactly the commands the validator accepts. The withheld entries carry as much weight as
    /// the offered ones — <c>stationKeep</c> is absent because a single-screw displacement hull
    /// loses steerage below its minimum speed and physically cannot pin a spot, so offering it
    /// would put a control on screen whose only honest outcome is a drift nobody asked for.
    /// <para>
    /// The general form of this — advertised equals accepted, for every class this build can
    /// spawn — is pinned by <c>GroundWiringHardeningTests</c>, which now probes the surface
    /// domain too because the surface factory joined its shipped-factory list in the same change
    /// that registered it in the composition root.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Capability_Report_For_A_Vessel_Offers_The_Surface_Command_Set()
    {
        var (ctrl, _) = CreateController();

        Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.SurfaceVessel, ScenePose(VesselSpawn), AssetId: "usv-1")));

        var report = Body<AssetCapabilitiesResponse>(ctrl.GetAssetCapabilities("usv-1"));
        report.Domain.Should().Be(AssetDomain.Surface);

        var kinds = report.Commands.Select(c => c.Kind).ToList();

        kinds.Should().Contain(
        [
            CommandKinds.Stop, CommandKinds.EmergencyStop, CommandKinds.Hold,
            CommandKinds.ResumeAutonomy, CommandKinds.GoTo, CommandKinds.ReturnToBase,
            CommandKinds.SetSpeed, CommandKinds.TransitTo, CommandKinds.SetCourse,
            CommandKinds.Dock, CommandKinds.Undock,
        ]);

        kinds.Should().NotContain(
        [
            CommandKinds.Takeoff, CommandKinds.Land, CommandKinds.SetAltitude,
            CommandKinds.Loiter, CommandKinds.DriveTo, CommandKinds.SetSteering,
            CommandKinds.Reverse, CommandKinds.Park,

            // Withheld rather than missing: this hull has one screw and one rudder, so
            // AssetProfiles withholds AssetCapability.StationKeep and the vessel honestly
            // refuses "wait here" instead of accepting it and drifting off the spot.
            CommandKinds.StationKeep,
        ]);

        report.DataFeatures.Should().Contain("domain.surface");
    }

    // ─── …and through the scenario loader, from the same registry ───────────

    /// <summary>A preset places vessels through the same motion models the endpoint spawns with.</summary>
    /// <remarks>
    /// The divergence this repository has already shipped once: the loader held a second,
    /// hand-maintained copy of the registry, so a newly registered factory was spawnable through
    /// the API and silently skipped by every preset, with nothing but a log line saying so.
    /// <para>
    /// Deliberately run through the loader's <em>fallback</em> factory list — the one it uses when
    /// a caller supplies none — because that is the copy which can lag. The composition root's own
    /// list is pinned separately, against the real host, by
    /// <c>SnapshotIntegrityTests.The_Wired_Scenario_Loader_Spawns_From_The_Registered_Motion_Models</c>.
    /// Between them the two cover both ways the lists can part company.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Preset_Spawns_Vessels_Through_The_Same_Registry_The_Api_Uses()
    {
        var apiRoom = CreateRoom();
        var apiController = ControllerFor(apiRoom);

        Spawned(apiController.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.SurfaceVessel, ScenePose(VesselSpawn), AssetId: "usv-1")));

        var presetRoom = CreateRoom();
        new ScenarioService(AppConfiguration()).TryRun("coastal-transit", presetRoom)
            .Should().BeTrue();

        var viaApi = apiRoom.CaptureAssetFrame().Descriptors
            .Where(d => d.Domain == AssetDomain.Surface).ToList();
        var viaPreset = presetRoom.CaptureAssetFrame().Descriptors
            .Where(d => d.Domain == AssetDomain.Surface).ToList();

        viaApi.Should().ContainSingle();
        viaPreset.Should().HaveCount(
            3, "the preset's vessels must be built, not skipped for a missing motion model");

        viaPreset.Select(d => d.VehicleClass).Should().AllBeEquivalentTo(viaApi[0].VehicleClass);
        viaPreset.Select(d => d.MobilityModel).Should().AllBeEquivalentTo(viaApi[0].MobilityModel);
        viaPreset.Select(d => d.Capabilities).Should().AllBeEquivalentTo(
            viaApi[0].Capabilities,
            "both paths build the descriptor from AssetProfiles, so neither may hand out a "
            + "capability the other withholds");
    }

    // ─── …and stays out of every v1 shape ───────────────────────────────────

    /// <summary>A vessel spawned through v2 is absent from the v1 snapshot and the v1 frame.</summary>
    /// <remarks>
    /// Both surfaces, because they fail differently. The snapshot feeds the drone cap and the v1
    /// command lookup, so a vessel appearing there shadows an identifier; the frame feeds the
    /// client, so a vessel appearing there is an entity no v1 renderer can draw.
    /// </remarks>
    [Fact]
    public void A_Vessel_Spawned_Through_The_V2_Api_Is_Invisible_To_The_V1_Frame()
    {
        var (ctrl, room) = CreateController();
        room.AddDrone("uav-1", new Vector3(-760f, 140f, -240f));

        Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.SurfaceVessel, ScenePose(VesselSpawn), AssetId: "usv-1")));

        room.CaptureAssetFrame().Descriptors.Should().HaveCount(
            2, "the v2 surface sees both domains");

        room.GetSnapshot().Select(d => d.Id).Should().Equal("uav-1");

        new VizFrameBuilder().Build(room.GetSnapshot(), FrameSimTime)
            .Drones.Select(d => d.Id).Should().Equal("uav-1");
    }

    // ─── Contacts reach the wire, and nothing can command one ───────────────

    /// <summary>Injected contacts appear in the v2 snapshot and in the track inventory.</summary>
    /// <remarks>
    /// Routing the store into the room is only half the job: a contact that is held but never
    /// published is a contact no operator can see. Both surfaces are asserted because they carry
    /// different things — the snapshot carries the picture, the inventory carries the ages the
    /// picture has to be read with.
    /// </remarks>
    [Fact]
    public void Injected_Contacts_Reach_The_Snapshot_And_The_Track_Inventory()
    {
        var (ctrl, _) = CreateController();

        Created(ctrl.ReportTrack(Contact("mv-astrid", new Vector3(-900f, 3f, -260f))))
            .TrackId.Should().Be("mv-astrid");
        Created(ctrl.ReportTrack(Contact("mv-borea", new Vector3(-1040f, 3f, -180f))))
            .Created.Should().BeTrue();

        var snapshot = Body<VizSnapshotV2>(ctrl.GetSnapshot());
        snapshot.Tracks.Select(t => t.TrackId).Should()
            .BeEquivalentTo(["mv-astrid", "mv-borea"]);

        var inventory = Body<TrackInventoryResponse>(ctrl.GetTracks());
        inventory.Tracks.Should().HaveCount(2);
        inventory.Tracks.Should().OnlyContain(t => t.AgeSeconds >= 0.0);
        inventory.Capacity.Should().BeGreaterThan(0);
        inventory.RejectedReportCount.Should().Be(0);
    }

    /// <summary>Nothing on the track surface offers, or accepts, a command.</summary>
    /// <remarks>
    /// Asserted structurally over the controller's own route table rather than by trying every
    /// verb, because the property being defended is that the endpoint <em>does not exist</em>. A
    /// behavioural probe only shows that today's routes refuse today's payloads; the route table
    /// shows there is nothing to refuse.
    /// <para>
    /// The behavioural half is asserted too, and it is the one an operator would actually hit: a
    /// command addressed to a track identifier resolves in the asset identifier space, finds
    /// nothing, and is refused — a contact colliding with an asset id is granted nothing by the
    /// collision.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_Route_Commands_A_Track_And_No_Command_Resolves_One()
    {
        // Read through IRouteTemplateProvider rather than any one attribute type, so a command
        // route added with [HttpPost], [HttpPut] or a bare [Route] is caught the same way.
        var trackRoutes = typeof(SimV2Controller)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes(inherit: false).OfType<IRouteTemplateProvider>())
            .Select(a => a.Template ?? string.Empty)
            .Where(t => t.StartsWith("tracks", StringComparison.Ordinal))
            .ToList();

        trackRoutes.Should().BeEquivalentTo(
            ["tracks", "tracks", "tracks/{trackId}"],
            "the track surface is list, fetch and report — a fourth route under 'tracks' is how a "
            + "contact becomes commandable, and it must not appear without this failing");

        var (ctrl, room) = CreateController();
        Created(ctrl.ReportTrack(Contact("mv-astrid", new Vector3(-900f, 3f, -260f))));

        var problem = Problem(
            ctrl.SendCommand("mv-astrid", new AssetCommandRequest(
                CommandKinds.TransitTo,
                "key-track-command",
                CommandId: Guid.NewGuid(),
                Target: new PointCommandTarget(ScenePose(VesselSpawn)))),
            StatusCodes.Status404NotFound);

        problem.Code.Should().Be(CommandRejectionReasons.AssetNotFound);
        room.CaptureAssetFrame().Assets.Should().BeEmpty("a refusal leaves nothing behind");
    }

    /// <summary>A contact and a vessel produce an advisory, and it is labelled as one.</summary>
    /// <remarks>
    /// What the maritime presets and the ingest route exist to make possible. The geometry is
    /// decision support: it reports a range, a closing rate and a closest approach, it carries the
    /// age and confidence of the worse of its two inputs, and it asserts nothing about any
    /// navigation regulation. This case runs the path end to end — spawn, inject, resolve both
    /// samples, compute — because each half is already unit-tested and the seam between them is
    /// not.
    /// </remarks>
    [Fact]
    public void A_Contact_Closing_On_A_Vessel_Produces_An_Advisory()
    {
        var (ctrl, room) = CreateController();

        Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.SurfaceVessel, ScenePose(VesselSpawn), AssetId: "usv-1")));

        // South-west of the vessel and making towards it at a few knots.
        var contactAt = new Vector3(VesselSpawn.X - 140f, 3f, VesselSpawn.Z + 140f);
        Created(ctrl.ReportTrack(Contact(
            "mv-astrid", contactAt, velocity: new Vector3(1.2f, 0f, -1.2f))));

        var frame = room.CaptureAssetFrame();
        var vessel = frame.Assets.Should().ContainSingle().Which;
        var contact = frame.Tracks.Should().ContainSingle().Which;

        ClosestPointOfApproach.TryFromAsset(vessel, ageSeconds: 0.0, confidence: 1.0, out var subject)
            .Should().BeTrue("a vessel publishes a scene-frame pose and twist");
        ClosestPointOfApproach.TryFromTrack(contact, out var other)
            .Should().BeTrue("an injected contact is stored in the frame it was reported in");

        var advisory = ClosestPointOfApproach.Compute(in subject, in other);

        advisory.SubjectId.Should().Be("usv-1");
        advisory.ContactId.Should().Be("mv-astrid");
        advisory.RangeM.Should().BeApproximately(Separation(vessel, contact), 0.5);
        advisory.IsClosing.Should().BeTrue("the contact is making towards the vessel");
        advisory.HasClosestApproach.Should().BeTrue();
        advisory.ClosestApproachDistanceM.Should().BeLessThan(advisory.RangeM);
        advisory.Confidence.Should().BeInRange(0.0, 1.0);

        ClosestPointOfApproach.AdvisoryNotice.Should().Contain(
            "advisory", "the wording an operator sees must say what this is");
    }

    // ─── The maritime presets stage something that actually works ───────────

    /// <summary>The shipped maritime search preset places all three domains in usable states.</summary>
    /// <remarks>
    /// Run against the real <c>appsettings.json</c> rather than a fixture, because the preset is
    /// the deliverable: an entry whose class name is misspelled, or whose declared domain has
    /// drifted out of step with its class, is skipped silently at load and would read as a
    /// spawning bug rather than the configuration typo it is.
    /// <para>
    /// "Usable" means something different in each domain and is asserted per domain: a drone is
    /// present in the v1 projection, a rover is on ground it is not immobilised on and has a
    /// non-zero speed ceiling, and a vessel is afloat with clearance under its keel. A preset that
    /// spawns three aground vessels satisfies a count and demonstrates nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_Coastal_Search_Preset_Spawns_All_Three_Domains_In_Valid_States()
    {
        var room = CreateRoom();

        new ScenarioService(AppConfiguration()).TryRun("coastal-search", room).Should().BeTrue();

        room.GetSnapshot().Select(d => d.Id).Should()
            .Equal("cs-overwatch-1", "cs-overwatch-2", "cs-relay-1");

        var frame = room.CaptureAssetFrame();
        frame.Descriptors.Should().HaveCount(8);

        var ground = frame.Descriptors.Where(d => d.Domain == AssetDomain.Ground).ToList();
        ground.Select(d => d.AssetId).Should().Equal("cs-shore-rover", "cs-shore-scout");
        ground.Select(d => d.VehicleClass).Should().Equal(
            VehicleClass.AckermannRover, VehicleClass.TrackedRover);

        var surface = frame.Descriptors.Where(d => d.Domain == AssetDomain.Surface).ToList();
        surface.Select(d => d.AssetId).Should()
            .Equal("cs-vessel-lead", "cs-vessel-tender", "cs-vessel-sweep");

        foreach (var descriptor in ground)
        {
            AssertDrivable(StateOf(frame, descriptor.AssetId));
        }

        foreach (var descriptor in surface)
        {
            AssertAfloatWithClearance(room, StateOf(frame, descriptor.AssetId));
        }
    }

    /// <summary>Every vessel in every maritime preset spawns in navigable water.</summary>
    /// <remarks>
    /// The check that would have caught a hull staged on a beach, applied to all of them rather
    /// than to the one somebody happened to look at. Asserted against bathymetry the room itself
    /// samples, so retuning the terrain moves the water and the expectation together and only a
    /// preset that has genuinely drifted onto land fails.
    /// </remarks>
    /// <param name="preset">A maritime preset shipped in <c>appsettings.json</c>.</param>
    [Theory]
    [MemberData(nameof(MaritimePresets))]
    public void Every_Vessel_In_A_Maritime_Preset_Spawns_Afloat_With_Under_Keel_Clearance(
        string preset)
    {
        var room = CreateRoom();
        new ScenarioService(AppConfiguration()).TryRun(preset, room).Should().BeTrue();

        var vessels = room.CaptureAssetFrame().Assets
            .Where(s => s.DomainState is SurfaceDomainState)
            .ToList();

        vessels.Should().NotBeEmpty($"'{preset}' is a maritime preset and must place vessels");

        foreach (var vessel in vessels)
        {
            AssertAfloatWithClearance(room, vessel);
        }
    }

    // ─── …without disturbing anything that shipped before it ────────────────

    /// <summary>
    /// A preset written before the surface domain existed still spawns exactly the assets it
    /// always did, in the same order, with nothing afloat.
    /// </summary>
    /// <remarks>
    /// The expectation is read out of the same configuration the loader reads, so this cannot
    /// drift into asserting a stale copy of the presets. What it pins is that adding a factory to
    /// the loader's registry changed no existing preset: an entry naming no class is still an air
    /// multirotor, every row still spawns, and no row has quietly acquired a surface domain.
    /// </remarks>
    /// <param name="preset">A preset that shipped before the surface work.</param>
    [Theory]
    [MemberData(nameof(PreSurfacePresets))]
    public void A_Preset_From_Before_The_Surface_Domain_Still_Spawns_Exactly_What_It_Did(
        string preset)
    {
        var configuration = AppConfiguration();
        var rows = configuration.GetSection($"Scenarios:{preset}").GetChildren().ToList();
        rows.Should().NotBeEmpty($"'{preset}' must still be present in appsettings.json");

        // The default terrain, not the coastal one the rest of this suite runs on. These presets
        // were staged against the environment a fresh room starts in, and "what it did before"
        // means nothing if the ground under them has been moved first.
        var room = CreateDefaultRoom();
        new ScenarioService(configuration).TryRun(preset, room).Should().BeTrue();

        room.GetSnapshot().Select(d => d.Id).Should().Equal(
            rows.Where(IsAirRow).Select(r => r["id"] ?? string.Empty),
            "an entry naming no class is an air multirotor, and always was");

        var frame = room.CaptureAssetFrame();
        frame.Descriptors.Select(d => d.AssetId).Should().Equal(
            rows.Select(r => r["id"] ?? string.Empty),
            "every row spawned before and must still, in preset order");

        frame.Descriptors.Should().NotContain(
            d => d.Domain == AssetDomain.Surface,
            "no preset from before the surface domain may have acquired a vessel");
    }
}
