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

using System.Globalization;
using System.Numerics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Three defect classes that have each now shipped in more than one domain, pinned once for
/// every domain at a time instead of once per instance.
/// </summary>
/// <remarks>
/// Every suite beside this one asks its question of one domain. That is exactly why these three
/// kept coming back: a fix written for the air domain teaches nobody anything about the ground
/// domain, and the ground domain's fix shipped again as a surface bug a fortnight later. The
/// tests here iterate the domains rather than naming them, and each carries a coverage guard
/// that <em>fails</em> when a domain appears that the invariant has no case for — so a fourth
/// domain cannot be added while quietly skipping all three.
/// <list type="number">
///   <item><description>
///     <b>Advertised is accepted, target shapes included.</b>
///     <see cref="GroundWiringHardeningTests"/> already holds the advertised command
///     <em>kinds</em> equal to the accepted ones. It did not catch <c>dock</c> advertising an
///     <see cref="CommandTargetKinds.Asset"/> target that no path resolves, because it probes a
///     kind and never a shape. This one probes the cross product, through the real REST
///     boundary, so the resolution a shape needs is actually attempted.
///   </description></item>
///   <item><description>
///     <b>Events are edges, not levels.</b> Shipped three times: air low battery, ground
///     immobilisation, surface shoreline contact. Each raised an event from a predicate that a
///     persisting cause keeps true, so a condition's <em>duration</em> decided how many entries
///     reached the log — sixty a second for exactly the assets most in need of a readable one.
///   </description></item>
///   <item><description>
///     <b>No state an asset cannot leave.</b> Shipped twice: a bogged rover and a vessel pinned
///     against a shoal each refused the commands that recover them. A recovery an operator
///     cannot discover is not a recovery either, so the command has to be in the capability
///     report as well as accepted by the asset.
///   </description></item>
/// </list>
/// <para>
/// Nothing here reads a wall clock, sleeps, or depends on a background loop: every world is
/// stepped explicitly, the terrain under the ground and surface cases is an installed height
/// field with a closed-form gradient rather than procedural noise, and every loop is bounded by
/// a stated tick budget so a case that never reaches its condition fails on an expectation
/// instead of hanging.
/// </para>
/// </remarks>
public sealed class CrossDomainInvariantTests
{
    // ─── Shared vocabulary ───────────────────────────────────────────────────

    /// <summary>Identifier every probe asset in this suite is spawned with.</summary>
    private const string ProbeId = "probe-1";

    /// <summary>Issuer stamped on every command, so the room's own fallback identity is not used.</summary>
    private const string IssuerId = "cross-domain-invariants";

    /// <summary>Marker for "this command was probed with no target at all".</summary>
    /// <remarks>
    /// Deliberately lower-case, so it can never collide with a
    /// <see cref="CommandTargetKinds"/> member name — which is what the advertised shapes are
    /// spelled as on the wire.
    /// </remarks>
    private const string NoTarget = "none";

    /// <summary>Scene-frame spawn point for ground and surface probes in the wiring invariant.</summary>
    /// <remarks>Ground the shipped alpine preset leaves dry, which the ground wiring suite already relies on.</remarks>
    private static readonly Vector3 GroundSpawn = new(640f, 0f, 300f);

    /// <summary>Scene-frame spawn point for the air probe, above that same hillside.</summary>
    private static readonly Vector3 AirSpawn = new(640f, 130f, 300f);

    /// <summary>How far from the asset a probed positional target is placed, in metres.</summary>
    /// <remarks>
    /// Far enough to be a real destination rather than an arrival, close enough that the terrain
    /// under it is the terrain under the asset — the invariant asks whether a shape can be
    /// resolved at all, not whether a particular hillside is drivable.
    /// </remarks>
    private const float TargetOffsetM = 25f;

    /// <summary>The origin the anchored deployment ties its scene to.</summary>
    /// <remarks>
    /// Present because the capability report withholds <see cref="CommandTargetKinds.Geo"/> from
    /// an unanchored deployment, and a shape that is never advertised is a shape this invariant
    /// would never probe. Anchoring the fixture is what puts the geodetic path under test.
    /// </remarks>
    private static readonly LocalOrigin Origin =
        new("cross-domain-origin", 46.5, 8.0, 0.0, VerticalReference.MeanSeaLevel);

    // ─── INVARIANT 1: advertised equals accepted, including target shapes ────

    /// <summary>
    /// Every command a capability report advertises, in every target shape it advertises for it,
    /// is one the asset accepts through the path a real request takes.
    /// </summary>
    /// <remarks>
    /// Driven from the real profile table and the real catalog: the classes come from
    /// <see cref="AssetProfiles.IsSupported"/>, the commands and the shapes come from the
    /// deployment's own <c>GET /assets/{id}/capabilities</c> response, and the request goes
    /// through <see cref="SimV2Controller.SendCommand"/> — so target normalisation, geodetic
    /// resolution, catalog validation, translation and the asset's own gate all run exactly as
    /// they do in production. A class or a command added later is covered without touching this
    /// test.
    /// <para>
    /// A refusal counts against the invariant only when it is <em>structural</em>: see
    /// <see cref="IsStructuralRefusal"/>. A vessel refusing to transit onto a beach is a fact
    /// about this moment; a vessel refusing a target shape nothing in the build can resolve is a
    /// promise that cannot be kept.
    /// </para>
    /// <para>
    /// <b>There is no quarantine list, and there must not be one.</b> A list of known divergences
    /// turns this from an invariant into documentation: the four it once held were four instances
    /// of the one defect it exists to catch, and suppressing them left the fifth free to ship. The
    /// two ways to close a divergence are the two honest ones — implement the command, or stop
    /// advertising it — and both were taken. <c>setSpeed</c> now has a case in the air executor,
    /// which mirrors the waypoint in force so a cruise change takes effect on it. <c>followRoute</c>
    /// is withdrawn from <see cref="CommandCatalog"/> entirely: its only shape is a
    /// <see cref="CommandTargetKinds.Route"/> target naming a stored route, this build has no route
    /// store for the identifier to name, so <see cref="AssetCommandTranslator"/> refused every one
    /// of them in every domain. Re-advertising it without that store fails here again, which is
    /// the point.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_Advertised_Command_And_Target_Shape_Is_One_The_Asset_Accepts()
    {
        var divergences = new SortedSet<string>(StringComparer.Ordinal);
        var domainsProbed = new SortedSet<AssetDomain>();
        var domainsWithAnAcceptance = new SortedSet<AssetDomain>();
        int probed = 0;

        foreach (var vehicleClass in SupportedClasses())
        {
            var domain = AssetProfiles.DomainFor(vehicleClass);
            var descriptor = AssetProfiles.Create(ProbeId, vehicleClass);

            foreach (var command in AdvertisedCommands(vehicleClass))
            {
                foreach (var shape in ShapesToProbe(command))
                {
                    // A fresh session per probe: an accepted emergencyStop latches on the ground
                    // and surface executors, and every command issued after it would then be
                    // refused for a reason that says nothing about whether it was executable.
                    var (controller, room) = AnchoredController();
                    PrepareWorld(room, domain);
                    Spawned(controller.SpawnAsset(SpawnRequest(vehicleClass, domain)));

                    domainsProbed.Add(domain);
                    probed++;

                    var problem = Issue(controller, command, shape, domain, descriptor.Motion);

                    if (problem is null)
                    {
                        domainsWithAnAcceptance.Add(domain);
                        continue;
                    }

                    if (IsStructuralRefusal(problem))
                    {
                        divergences.Add($"{domain}:{command.Kind}:{shape}");
                    }
                }
            }
        }

        probed.Should().BeGreaterThan(0, "the invariant is vacuous if nothing was actually probed");

        domainsProbed.Should().BeEquivalentTo(
            SupportedDomains(),
            "every domain the profile table can spawn has a capability report, and a domain this "
            + "invariant never reaches is a domain it never checks");

        domainsWithAnAcceptance.Should().BeEquivalentTo(
            SupportedDomains(),
            "a domain in which every probe was refused proves nothing about what is accepted; "
            + "the ungated stop commands alone should land in each");

        divergences.Should().BeEmpty(
            "a capability report is a promise: every command it lists, in every target shape it "
            + "lists for that command, must be one the asset can execute. Close a divergence by "
            + "implementing the command or by withdrawing it from the catalog — never by "
            + "excusing it here, because a list of excused divergences is what let this defect "
            + "ship five times");
    }

    // ─── INVARIANT 2: events are edge-triggered, in every domain ─────────────

    /// <summary>
    /// A sustained adverse condition raises a bounded, small number of events in every domain —
    /// a leading edge and at most a clearing one, never a count that scales with the tick count.
    /// </summary>
    /// <remarks>
    /// The general form of a defect that has now shipped three times. Each domain is driven into
    /// a condition that stays true — a flat battery, ground that will not carry the vehicle,
    /// water the hull may not enter — and then stepped for
    /// <see cref="ObserveTicks"/> ticks with a frame captured on every one of them, because the
    /// air domain raises its transitions during capture rather than during a step.
    /// <para>
    /// Two things are asserted, and both are needed. That the condition was actually reached is
    /// read off the published <see cref="AssetState"/> rather than inferred from the events, so a
    /// scenario that quietly failed to become adverse cannot pass by raising nothing. That the
    /// event count is bounded is then the invariant itself — counted as the events delivered
    /// <em>plus</em> the ones the session had to drop, because a level-triggered raise overruns
    /// the room's bounded buffer and a drain alone would report a plausible-looking 256 while the
    /// earlier transitions that explain how the asset got there had already been thrown away.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_Sustained_Adverse_Condition_Raises_A_Bounded_Number_Of_Events_In_Every_Domain()
    {
        var covered = new SortedSet<AssetDomain>();

        foreach (var domain in SupportedDomains())
        {
            var (raised, state) = RunSustainedAdverseCondition(domain);
            covered.Add(domain);

            IsInAdverseCondition(domain, state).Should().BeTrue(
                $"the {domain} probe must actually reach the condition it claims to sustain; a "
                + "case that never became adverse would pass this invariant by raising nothing");

            raised.Should().BeLessThanOrEqualTo(
                MaxEdgeEvents,
                $"a condition that persists is a level, not an event: over {ObserveTicks} ticks "
                + $"the {domain} asset may raise a leading edge, a clearing edge and the handful "
                + "of other transitions its entry into the condition causes — never a count that "
                + "grows with how long the condition lasts");
        }

        covered.Should().BeEquivalentTo(
            SupportedDomains(),
            "the domain list is derived from the profile table and the registered factories, so "
            + "a new domain is covered the moment it can be spawned");
    }

    // ─── INVARIANT 3: no asset can enter a state it cannot leave ─────────────

    /// <summary>
    /// From every adverse, terminal-looking state a domain can reach there is a command the
    /// asset accepts, that changes its situation, and that the capability report advertises.
    /// </summary>
    /// <remarks>
    /// Stated as behaviour rather than as classification, because the operator's question is the
    /// only one that matters: <em>is there anything I can send that gets this asset out of
    /// this?</em> Acceptance alone is not enough — a command that is taken and then does nothing
    /// leaves the asset exactly as stranded — so each case also observes the situation change:
    /// the vehicle moves, the airframe leaves the ground, or a command that the state was
    /// refusing starts landing.
    /// <para>
    /// Advertisement is asserted through <c>GET /assets/{id}/capabilities</c>, not through the
    /// catalog, because that response is what a client renders its controls from. A recovery
    /// nobody can find on screen is not a recovery.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_Adverse_State_Has_An_Advertised_Command_That_Leaves_It()
    {
        var outcomes = AdverseStateCases().Select(run => run()).ToList();

        outcomes.Should().NotBeEmpty();

        foreach (var outcome in outcomes)
        {
            outcome.ReachedState.Should().BeTrue(
                $"the {outcome.Domain} '{outcome.StateName}' case must actually put the asset "
                + "into the state it is about to try to recover from");

            outcome.RecoveryAccepted.Should().BeTrue(
                $"a {outcome.Domain} asset in '{outcome.StateName}' refused "
                + $"'{outcome.RecoveryKind}', the command that recovers it: "
                + $"{outcome.RecoveryReason ?? "no reason given"}. A state with no accepted way "
                + "out is a dead asset, not a safe one");

            outcome.SituationChanged.Should().BeTrue(
                $"'{outcome.RecoveryKind}' was accepted by the {outcome.Domain} asset in "
                + $"'{outcome.StateName}' and then changed nothing; an acknowledgement that "
                + "leaves the asset exactly as stranded is worse than a refusal, because "
                + "nothing anywhere says the recovery did not happen");

            outcome.RecoveryAdvertised.Should().BeTrue(
                $"'{outcome.RecoveryKind}' is the way out of '{outcome.StateName}' for a "
                + $"{outcome.Domain} asset, so the capability report has to offer it: a recovery "
                + "the operator cannot discover is not a recovery");
        }

        outcomes.Select(o => o.Domain).Distinct().Should().BeEquivalentTo(
            SupportedDomains(),
            "every domain must contribute at least one terminal-looking state; a domain with no "
            + "case here is a domain whose traps are unpinned");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Fixtures
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Ticks each adverse condition is held and observed for.</summary>
    /// <remarks>
    /// Twenty seconds at 60 Hz. The defect this bounds raised one event per tick, so the
    /// difference between the right answer and the broken one here is a handful against twelve
    /// hundred — and the broken run also overruns the room's 256-entry buffer, which is why the
    /// dropped-event counter is added to the drained count rather than ignored.
    /// </remarks>
    private const int ObserveTicks = 1200;

    /// <summary>Ticks an asset is left to settle into its condition before observation begins.</summary>
    private const int SettleTicks = 120;

    /// <summary>Most events one asset may raise across a whole sustained-condition run.</summary>
    /// <remarks>
    /// Generous on purpose: the exact number of transitions entering a condition causes is a
    /// domain's own business — a rover meeting an unclimbable grade also crosses a rollover
    /// threshold and refuses a route — and pinning it here would make this test a change detector
    /// for each domain rather than a guard on the one property it is about. What it may not be is
    /// a function of <see cref="ObserveTicks"/>.
    /// </remarks>
    private const int MaxEdgeEvents = 12;

    /// <summary>Ticks the air probe is run, unobserved, to drain its pack below the reserve.</summary>
    /// <remarks>
    /// The kinematic flight model drains 0.1 percentage points per simulated second and the
    /// warning latches under 20%, so the pack needs 800 simulated seconds. At the room's maximum
    /// eight world steps per tick that is 6 000 ticks; 6 600 clears the threshold with margin and
    /// leaves the whole observation window on the far side of it.
    /// </remarks>
    private const int AirDrainTicks = 6_600;

    /// <summary>Ticks a recovery command is given to visibly change the asset's situation.</summary>
    /// <remarks>Thirty seconds at 60 Hz — long enough that even a heavily derated ceiling covers metres.</remarks>
    private const int RecoveryTicks = 1_800;

    /// <summary>Ticks the aground vessel is given to work itself off the beach.</summary>
    /// <remarks>
    /// Two and a half minutes, and the surface case needs every one of them because its recovery
    /// is dominated by a turn rather than by a passage. The hull is put ashore facing up the
    /// beach and its way out is seaward, so it has to swing a full half-circle before the water
    /// mask will admit a single metre of the move — and it has to do that swing at the aground
    /// crawl, whose rudder authority is
    /// <c>speed / <see cref="SurfaceProfile.MinTurnRadiusM"/></c> and therefore a crawl too.
    /// Measured against this build the bow comes round at about tick 4 500 and the vessel is
    /// afloat again well before the budget runs out; <see cref="RecoveryTicks"/> would expire
    /// while it was still turning, which says nothing about whether it can recover.
    /// </remarks>
    private const int AgroundRecoveryTicks = 9_000;

    /// <summary>Planar distance that counts as an asset having actually moved, in metres.</summary>
    private const double MovedM = 0.5;

    /// <summary>Heading due east, in radians clockwise from true north.</summary>
    private const double East = Math.PI / 2.0;

    /// <summary>Side length of every height field this suite installs, in metres.</summary>
    private const double DemExtentM = 400.0;

    /// <summary>Columns and rows in every height field this suite installs.</summary>
    /// <remarks>
    /// Five, so the spacing is a round hundred metres and the gradient between two columns is the
    /// gradient of the whole ramp. The scene origin maps to the grid's centre, so column two sits
    /// at <c>x = 0</c> and the probe positions below are read straight off that.
    /// </remarks>
    private const int DemCells = 5;

    /// <summary>Distance between two height-field columns, in metres.</summary>
    private const double DemSpacingM = DemExtentM / (DemCells - 1);

    /// <summary>
    /// The motion models the composition root registers, wired the way it wires them.
    /// </summary>
    /// <remarks>
    /// Resolving the sampler from <see cref="SimulationRoom.SpawningEnvironment"/> rather than
    /// capturing one is the production contract: a factory that held a sampler would settle every
    /// session's vehicles onto the first session's terrain. Using the same expression here means
    /// these tests fail if that contract changes under them.
    /// <para>
    /// <b>There is deliberately no air factory, and there cannot be one.</b> An air asset's
    /// lifetime belongs to the SDK's flight world, which <see cref="AssetWorld.AddDrone"/> is the
    /// only correct way into, so the domain list below is derived from the profile table rather
    /// than from this array — and placement branches on domain exactly as the spawn endpoint
    /// does.
    /// </para>
    /// </remarks>
    /// <returns>One factory per registered motion model.</returns>
    private static IAssetFactory[] ShippedFactories() =>
    [
        new GroundAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A ground asset may only be built from inside SimulationRoom.TrySpawnAsset.")),

        new SurfaceAssetFactory(() =>
            SimulationRoom.SpawningEnvironment
            ?? throw new InvalidOperationException(
                "A surface asset may only be built from inside SimulationRoom.TrySpawnAsset.")),
    ];

    /// <summary>Every vehicle class the real profile table describes, in catalog order.</summary>
    /// <returns>The supported classes.</returns>
    private static VehicleClass[] SupportedClasses() =>
        Enum.GetValues<VehicleClass>().Where(AssetProfiles.IsSupported).ToArray();

    /// <summary>Every domain those classes cover, in the order they first appear.</summary>
    /// <remarks>
    /// The list every invariant here iterates. Derived rather than written down, so a class added
    /// to <see cref="AssetProfiles"/> in a new domain makes each coverage guard fail until that
    /// domain has a case — which is the whole point of the guards.
    /// </remarks>
    /// <returns>The supported domains.</returns>
    private static AssetDomain[] SupportedDomains() =>
        SupportedClasses().Select(AssetProfiles.DomainFor).Distinct().ToArray();

    /// <summary>The class this suite uses to stand for a domain.</summary>
    /// <remarks>
    /// The first supported class in the domain, so the choice follows the profile table rather
    /// than a hard-coded name. Cases that depend on a platform limit read that limit off the
    /// chosen class's own profile instead of assuming which class was picked.
    /// </remarks>
    /// <param name="domain">Domain to represent.</param>
    /// <returns>A class in that domain.</returns>
    private static VehicleClass RepresentativeClass(AssetDomain domain) =>
        SupportedClasses().First(c => AssetProfiles.DomainFor(c) == domain);

    /// <summary>A room with no tick loop attached, so the only stepping is the test's own.</summary>
    /// <returns>A fresh room.</returns>
    private static SimulationRoom CreateRoom() =>
        new(id: "cross-domain-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>A v2 controller bound to <paramref name="room"/>, with the shipped factories.</summary>
    /// <param name="room">Room the controller's actions operate on.</param>
    /// <param name="configuration">Configuration the controller reads its local origin from, or null.</param>
    /// <returns>The controller.</returns>
    private static SimV2Controller ControllerFor(SimulationRoom room, IConfiguration? configuration)
    {
        var controller = new SimV2Controller(
            new VizFrameBuilder(), ShippedFactories(), NullLogger<SimV2Controller>.Instance);

        // The same shortcut every other v2 suite uses: stash the resolved room where
        // RequireRoomAttribute would have put it, so these stay unit tests.
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;

        if (configuration is not null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            http.RequestServices = services.BuildServiceProvider();
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>A room and a controller on a deployment whose scene is anchored to the globe.</summary>
    /// <returns>The controller and the room it operates on.</returns>
    private static (SimV2Controller Controller, SimulationRoom Room) AnchoredController()
    {
        var room = CreateRoom();
        return (ControllerFor(room, AnchoredConfiguration()), room);
    }

    /// <summary>Configuration naming <see cref="Origin"/> as the scene's local origin.</summary>
    /// <returns>Configuration the controller's geodesy reads.</returns>
    private static IConfiguration AnchoredConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Simulation:LocalOrigin:OriginId"] = Origin.OriginId,
                ["Simulation:LocalOrigin:LatitudeDeg"] =
                    Origin.LatitudeDeg.ToString(CultureInfo.InvariantCulture),
                ["Simulation:LocalOrigin:LongitudeDeg"] =
                    Origin.LongitudeDeg.ToString(CultureInfo.InvariantCulture),
                ["Simulation:LocalOrigin:VerticalMeters"] = "0",
                ["Simulation:LocalOrigin:VerticalReference"] =
                    nameof(VerticalReference.MeanSeaLevel),
                ["Simulation:LocalOrigin:YawRad"] = "0",
            })
            .Build();

    /// <summary>A scene-frame pose with no rotation, which is all a spawn or a target needs.</summary>
    /// <param name="positionEus">Position in the scene frame.</param>
    /// <returns>The framed pose.</returns>
    private static FramedPose ScenePose(Vector3 positionEus) =>
        new(CoordinateFrame.LocalEus, OriginId: null, positionEus, Quaternion.Identity);

    /// <summary>Where the wiring invariant places an asset of <paramref name="domain"/>.</summary>
    /// <param name="domain">Domain being probed.</param>
    /// <returns>A scene-frame spawn point.</returns>
    private static Vector3 SpawnPointFor(AssetDomain domain) =>
        domain == AssetDomain.Air ? AirSpawn : GroundSpawn;

    /// <summary>The spawn request the wiring invariant issues for one class.</summary>
    /// <param name="vehicleClass">Class to place.</param>
    /// <param name="domain">That class's domain.</param>
    /// <returns>The request.</returns>
    private static AssetSpawnRequest SpawnRequest(VehicleClass vehicleClass, AssetDomain domain) =>
        new(vehicleClass, ScenePose(SpawnPointFor(domain)), AssetId: ProbeId);

    /// <summary>Gives a room the environment the domain under test needs.</summary>
    /// <remarks>
    /// Air and ground keep the shipped preset, whose hillside at <see cref="GroundSpawn"/> is dry
    /// and drivable. A surface asset needs water, and the preset's water is wherever the noise
    /// field happens to put it, so the surface case installs a height field that floods the whole
    /// scene: navigability then follows from the sea level the preset already established rather
    /// than from where a procedural valley landed.
    /// </remarks>
    /// <param name="room">Room to prepare. Must have no assets in it yet.</param>
    /// <param name="domain">Domain about to be placed.</param>
    private static void PrepareWorld(SimulationRoom room, AssetDomain domain)
    {
        if (domain == AssetDomain.Surface)
        {
            room.SetHeightmap(UniformGrid(-50f), DemExtentM * 10.0, DemExtentM * 10.0);
        }
    }

    /// <summary>A height field at one elevation everywhere.</summary>
    /// <param name="elevationM">Elevation every cell carries, in metres.</param>
    /// <returns>The grid.</returns>
    private static float[,] UniformGrid(float elevationM)
    {
        var grid = new float[DemCells, DemCells];

        for (int row = 0; row < DemCells; row++)
        {
            for (int col = 0; col < DemCells; col++)
            {
                grid[row, col] = elevationM;
            }
        }

        return grid;
    }

    /// <summary>A height field rising uniformly towards the east.</summary>
    /// <remarks>
    /// Constant along the north–south axis and linear along the east–west one, so the slope the
    /// terrain sampler derives from central differences is exactly
    /// <paramref name="gradient"/> everywhere and can be reasoned about on paper.
    /// </remarks>
    /// <param name="baseElevationM">Elevation of the westernmost column, in metres.</param>
    /// <param name="gradient">Rise in metres per metre of easting.</param>
    /// <returns>The grid.</returns>
    private static float[,] RampGrid(double baseElevationM, double gradient)
    {
        var grid = new float[DemCells, DemCells];

        for (int row = 0; row < DemCells; row++)
        {
            for (int col = 0; col < DemCells; col++)
            {
                grid[row, col] = (float)(baseElevationM + (col * DemSpacingM * gradient));
            }
        }

        return grid;
    }

    /// <summary>Places one asset of <paramref name="vehicleClass"/> into a room.</summary>
    /// <remarks>
    /// Air goes through the room's drone entry point and everything else through
    /// <see cref="SimulationRoom.TrySpawnAsset"/> — the split the production spawn endpoint makes,
    /// and for the same reason: the SDK's flight world owns air lifetimes.
    /// </remarks>
    /// <param name="room">Room to place the asset in.</param>
    /// <param name="vehicleClass">Class to place.</param>
    /// <param name="positionEus">Scene-frame spawn position.</param>
    /// <param name="headingRad">Initial heading, radians clockwise from true north.</param>
    /// <returns><see langword="true"/> when the asset was placed.</returns>
    private static bool TryPlace(
        SimulationRoom room, VehicleClass vehicleClass, Vector3 positionEus, double headingRad)
    {
        if (AssetProfiles.DomainFor(vehicleClass) == AssetDomain.Air)
        {
            room.AddDrone(ProbeId, positionEus, vendor: null);
            return true;
        }

        var factory = ShippedFactories().FirstOrDefault(f => f.CanCreate(vehicleClass));
        if (factory is null)
        {
            return false;
        }

        var plan = new AssetSpawnPlan(
            ProbeId,
            vehicleClass,
            AssetProfiles.Create(ProbeId, vehicleClass),
            positionEus,
            headingRad);

        return room.TrySpawnAsset(ProbeId, _ => factory.Create(plan), out _);
    }

    /// <summary>Asserts a spawn succeeded and returns what it minted.</summary>
    /// <param name="result">Action result from the spawn endpoint.</param>
    /// <returns>The spawn response.</returns>
    private static AssetSpawnResponse Spawned(IActionResult result)
    {
        var created = result.Should().BeOfType<CreatedResult>().Which;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        return created.Value.Should().BeOfType<AssetSpawnResponse>().Which;
    }

    /// <summary>Unwraps an <c>Ok</c> body of the expected shape.</summary>
    /// <typeparam name="T">Expected body type.</typeparam>
    /// <param name="result">Action result to unwrap.</param>
    /// <returns>The body.</returns>
    private static T Body<T>(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<T>().Which;

    /// <summary>The probe asset's latest published state.</summary>
    /// <param name="room">Room holding it.</param>
    /// <returns>The state.</returns>
    private static AssetState StateOf(SimulationRoom room) =>
        room.CaptureAssetFrame().Assets.Single(s => s.AssetId == ProbeId);

    /// <summary>The probe asset's scene-frame position.</summary>
    /// <param name="room">Room holding it.</param>
    /// <returns>Position in metres.</returns>
    private static Vector3 PositionOf(SimulationRoom room) => StateOf(room).Pose.Position;

    /// <summary>Horizontal distance between two scene-frame points, in metres.</summary>
    /// <param name="from">First point.</param>
    /// <param name="to">Second point.</param>
    /// <returns>Planar distance.</returns>
    private static double PlanarDistance(Vector3 from, Vector3 to) =>
        Math.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Z - from.Z) * (to.Z - from.Z)));

    // ─── Invariant 1 helpers ────────────────────────────────────────────────

    /// <summary>What a deployment advertises for one vehicle class, read off the real endpoint.</summary>
    /// <remarks>
    /// Read through <c>GET /assets/{id}/capabilities</c> on an anchored deployment rather than
    /// derived from the catalog here, because that response — including the shapes it withholds
    /// from an unanchored scene — is what a client renders its controls from, and it is the
    /// promise this invariant holds the build to.
    /// </remarks>
    /// <param name="vehicleClass">Class to interrogate.</param>
    /// <returns>The advertised commands, in catalog order.</returns>
    private static IReadOnlyList<AssetCommandCapability> AdvertisedCommands(VehicleClass vehicleClass)
    {
        var domain = AssetProfiles.DomainFor(vehicleClass);
        var (controller, room) = AnchoredController();

        PrepareWorld(room, domain);
        Spawned(controller.SpawnAsset(SpawnRequest(vehicleClass, domain)));

        return Body<AssetCapabilitiesResponse>(controller.GetAssetCapabilities(ProbeId)).Commands;
    }

    /// <summary>Every target shape one advertised command has to be probed in.</summary>
    /// <remarks>
    /// The advertised shapes, plus the no-target form whenever the command does not require one.
    /// A command that accepts no target at all is probed exactly once, with nothing.
    /// </remarks>
    /// <param name="command">Advertised command.</param>
    /// <returns>Shape tokens, each either a <see cref="CommandTargetKinds"/> name or <see cref="NoTarget"/>.</returns>
    private static IEnumerable<string> ShapesToProbe(AssetCommandCapability command)
    {
        foreach (var shape in command.AllowedTargetKinds)
        {
            yield return shape;
        }

        if (!command.RequiresTarget)
        {
            yield return NoTarget;
        }
    }

    /// <summary>Builds the target for one advertised shape.</summary>
    /// <remarks>
    /// A shape with no arm here throws rather than being skipped: a shape the catalog starts
    /// advertising and this test does not know how to build is exactly the gap that let
    /// <c>dock</c> offer an asset-referenced berth nothing could resolve.
    /// </remarks>
    /// <param name="shape">Shape token from <see cref="ShapesToProbe"/>.</param>
    /// <param name="aimEus">Scene-frame point the command is aimed at.</param>
    /// <returns>The target, or null for <see cref="NoTarget"/>.</returns>
    private static CommandTarget? TargetFor(string shape, Vector3 aimEus)
    {
        if (string.Equals(shape, NoTarget, StringComparison.Ordinal))
        {
            return null;
        }

        return shape switch
        {
            nameof(CommandTargetKinds.Point) => new PointCommandTarget(ScenePose(aimEus)),
            nameof(CommandTargetKinds.Geo) =>
                new GeoCommandTarget(CoordinateFrames.LocalEusToGeo(aimEus, Origin)),
            nameof(CommandTargetKinds.Asset) => new AssetCommandTarget("berth-1"),
            nameof(CommandTargetKinds.Route) => new RouteCommandTarget("route-1"),
            _ => throw new InvalidOperationException(
                $"Target shape '{shape}' is advertised but this invariant does not know how to "
                + "build one; add an arm here in the same change that advertises it."),
        };
    }

    /// <summary>Fills in every parameter an advertised command declares as required.</summary>
    /// <remarks>
    /// Deliberately generous and derived: the speed is the midpoint of the asset's own declared
    /// envelope rather than a literal, so a hull with a non-zero minimum speed is not probed with
    /// a number it would rightly refuse. A required key with no arm here throws, for the same
    /// reason an unknown target shape does.
    /// </remarks>
    /// <param name="command">Advertised command.</param>
    /// <param name="motion">The asset's declared motion envelope.</param>
    /// <param name="sceneAltitudeM">Altitude to command, on the scene's own datum.</param>
    /// <returns>The parameter bag, or null when the command needs none.</returns>
    private static IReadOnlyDictionary<string, string>? ParametersFor(
        AssetCommandCapability command, MotionConstraints motion, double sceneAltitudeM)
    {
        if (command.RequiredParameters.Count == 0)
        {
            return null;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in command.RequiredParameters)
        {
            switch (key)
            {
                case CommandParameters.Speed:
                    parameters[key] = ((motion.MinSpeedMps + motion.MaxSpeedMps) / 2.0)
                        .ToString(CultureInfo.InvariantCulture);
                    break;

                case CommandParameters.Altitude:
                    parameters[key] = sceneAltitudeM.ToString(CultureInfo.InvariantCulture);

                    // Mandatory whenever an altitude is present: the boundary refuses a bare one
                    // rather than guessing which of three datums an operator meant.
                    parameters[CommandParameters.VerticalReference] =
                        nameof(VerticalReference.MeanSeaLevel);
                    break;

                case CommandParameters.Course:
                    parameters[key] = "1.0";
                    break;

                case CommandParameters.Steering:
                    parameters[key] = "0.1";
                    break;

                case CommandParameters.Radius:
                    parameters[key] = "25";
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Command '{command.Kind}' requires parameter '{key}', which this "
                        + "invariant has no probe value for.");
            }
        }

        return parameters;
    }

    /// <summary>Issues one advertised command in one advertised shape, through the real endpoint.</summary>
    /// <param name="controller">Controller bound to the room the asset is in.</param>
    /// <param name="command">Advertised command being probed.</param>
    /// <param name="shape">Target shape being probed.</param>
    /// <param name="domain">Domain of the asset.</param>
    /// <param name="motion">The asset's declared motion envelope.</param>
    /// <returns>The problem the endpoint answered with, or null when the command was accepted.</returns>
    private static CommandProblemDetails? Issue(
        SimV2Controller controller,
        AssetCommandCapability command,
        string shape,
        AssetDomain domain,
        MotionConstraints motion)
    {
        var here = SpawnPointFor(domain);
        var aim = here + new Vector3(TargetOffsetM, 0f, 0f);

        var request = new AssetCommandRequest(
            Kind: command.Kind,
            IdempotencyKey: $"{domain}-{command.Kind}-{shape}",
            IssuerId: IssuerId,
            Target: TargetFor(shape, aim),
            Parameters: ParametersFor(command, motion, here.Y + 20.0));

        var result = controller.SendCommand(ProbeId, request);

        return result is ObjectResult { Value: CommandProblemDetails problem } ? problem : null;
    }

    /// <summary>Problem codes that mean "this build can never execute this, however it is asked".</summary>
    private static readonly string[] StructuralProblemCodes =
    [
        CommandRejectionReasons.TargetKindUnsupported,
        CommandRejectionReasons.CapabilityNotDeclared,
        CommandRejectionReasons.DomainNotApplicable,
        CommandContractReasons.TargetNotResolvable,
        CommandContractReasons.KindNotExecutable,
        CommandContractReasons.LocalOriginNotConfigured,
    ];

    /// <summary>Whether a refusal means "this build cannot execute this command at all".</summary>
    /// <remarks>
    /// The distinction the invariant turns on. A refusal because the water ahead is too shallow,
    /// the ground is not traversable, the asset is emergency-stopped or its position report is
    /// stale is a fact about this moment and no contract problem: issue the command differently,
    /// or later, and it lands. A refusal naming a missing capability, an inapplicable domain, a
    /// target shape nothing resolves, or a kind with no executor is a fact about the <em>build</em>
    /// — no payload and no state will ever make it succeed, so advertising it is a promise that
    /// cannot be kept.
    /// </remarks>
    /// <param name="problem">Problem body the endpoint answered with.</param>
    /// <returns><see langword="true"/> when the refusal is structural.</returns>
    private static bool IsStructuralRefusal(CommandProblemDetails problem) =>
        StructuralProblemCodes.Contains(problem.Code, StringComparer.Ordinal)
        || (string.Equals(problem.Code, AssetProblems.CommandNotExecutable, StringComparison.Ordinal)
            && IsStructuralAssetReason(AssetRefusalReason(problem.Detail)));

    /// <summary>Whether an executor's own refusal token is structural.</summary>
    /// <param name="reason">Machine-readable token the asset refused with.</param>
    /// <returns><see langword="true"/> when no payload could ever satisfy the command.</returns>
    private static bool IsStructuralAssetReason(string? reason) =>
        reason is not null
        && (reason.StartsWith("capability.", StringComparison.Ordinal)
            || reason.EndsWith(".unsupported", StringComparison.Ordinal)
            || reason.EndsWith(".unavailable", StringComparison.Ordinal));

    /// <summary>Recovers the executor's refusal token from the endpoint's prose.</summary>
    /// <remarks>
    /// The v2 command endpoint answers an executor refusal with
    /// <see cref="AssetProblems.CommandNotExecutable"/> and puts the asset's own token at the end
    /// of the detail, because the code names the <em>class</em> of failure and the token names
    /// the instance. Reading it back is the only way to tell "the ground ahead is water" from
    /// "no executor in this build has a case for this command", and the two are the whole
    /// difference between a momentary refusal and a broken promise. A change to that message
    /// format makes this return something that is not a token, which fails loudly here rather
    /// than quietly reclassifying every refusal.
    /// </remarks>
    /// <param name="detail">Operator-facing detail from the problem body.</param>
    /// <returns>The trailing token, or null when the detail carries none.</returns>
    private static string? AssetRefusalReason(string detail)
    {
        int marker = detail.LastIndexOf(": ", StringComparison.Ordinal);
        return marker < 0 ? null : detail[(marker + 2)..].TrimEnd('.');
    }

    // ─── Invariant 2 helpers ────────────────────────────────────────────────

    /// <summary>Drives one domain into its sustained adverse condition and counts what it says.</summary>
    /// <remarks>
    /// The observation window steps <em>and</em> captures on every tick. Capturing matters for
    /// the air domain and only for it: an air asset is integrated by the SDK's world and has no
    /// step of its own, so it observes its own transitions during
    /// <see cref="ISimulatedAsset.Capture"/>. Stepping without capturing would leave the air case
    /// silent whether the code were edge- or level-triggered, which is a test that cannot fail.
    /// </remarks>
    /// <param name="domain">Domain to exercise.</param>
    /// <returns>How many events the session recorded, and the asset's state at the end.</returns>
    private static (long Raised, AssetState State) RunSustainedAdverseCondition(AssetDomain domain)
    {
        var room = CreateRoom();
        var vehicleClass = RepresentativeClass(domain);

        EnterAdverseCondition(room, domain, vehicleClass);

        for (int tick = 0; tick < ObserveTicks; tick++)
        {
            room.StepOnce();
            room.CaptureAssetFrame();
        }

        var state = StateOf(room);

        // Both halves of the count. The buffer drops from the head once it is full, so a
        // level-triggered raise shows up as a saturated drain beside a non-zero drop count
        // rather than as a very large number.
        long raised = room.DrainAssetEvents().Count + room.DroppedAssetEventCount;

        return (raised, state);
    }

    /// <summary>Places an asset and puts it into a condition that will not clear on its own.</summary>
    /// <remarks>
    /// One arm per domain, and the arms are genuinely different because the conditions are: a
    /// flat pack is reached by waiting, unclimbable ground by installing it, and a refused
    /// passage by commanding one. The switch is exhaustive over the domains the profile table
    /// describes and throws for any other, which is what makes the coverage guard in the test
    /// above meaningful rather than decorative.
    /// </remarks>
    /// <param name="room">Room to set up.</param>
    /// <param name="domain">Domain being exercised.</param>
    /// <param name="vehicleClass">Class standing for that domain.</param>
    private static void EnterAdverseCondition(
        SimulationRoom room, AssetDomain domain, VehicleClass vehicleClass)
    {
        switch (domain)
        {
            case AssetDomain.Air:
                TryPlace(room, vehicleClass, AirSpawn, headingRad: 0.0).Should().BeTrue();

                // Hold detaches the drone from the swarm coordinator's 2 Hz pass, so it hovers
                // where it was put instead of being flown a patrol leg — and its pack drains on a
                // trajectory this test chose rather than one the coordinator did.
                Commanded(room, AssetCommandKind.Hold);

                // Unobserved: the air domain raises during capture, so a drain that costs nothing
                // is a drain that raises nothing, and the whole observation window then sits on
                // the far side of the threshold.
                room.SetSpeed(8);
                for (int tick = 0; tick < AirDrainTicks; tick++)
                {
                    room.StepOnce();
                }

                break;

            case AssetDomain.Ground:
                // A grade past the platform's own declared climb limit, read off the profile
                // rather than assumed, so this stays true if the representative class changes.
                room.SetHeightmap(
                    RampGrid(baseElevationM: 0.0, gradient: Math.Tan(UnclimbableGradeRad(vehicleClass))),
                    DemExtentM,
                    DemExtentM);

                TryPlace(room, vehicleClass, Vector3.Zero, East).Should().BeTrue();
                Settle(room);
                break;

            case AssetDomain.Surface:
                // A beach: deep water to the west, dry land to the east, with the vessel put
                // ashore on the dry half. It is then aground for every step of the window — the
                // bed is above the water surface and nothing moves it — so the grounding, the
                // under-keel band and the water mask are all read from a predicate that stays
                // true, which is exactly the shape the shoreline contact used to be raised from.
                //
                // The course further inshore is what makes the first steps attempt a move the
                // mask refuses, so the contact and the refusal are exercised as well as the
                // grounding. It is honest to say the vessel then stops attempting: the navigator
                // latches the block, which is itself an edge, and the condition it latched on
                // persists for the rest of the run.
                room.SetHeightmap(BeachGrid(), DemExtentM, DemExtentM);

                TryPlace(room, vehicleClass, AshorePosition, East).Should().BeTrue();
                Commanded(room, AssetCommandKind.SetCourse, speedMps: 2.0, headingRad: East);
                Settle(room);
                break;

            default:
                throw new InvalidOperationException(
                    $"Domain '{domain}' can be spawned but this invariant has no sustained "
                    + "adverse condition for it; add one in the same change that adds the domain.");
        }
    }

    /// <summary>Whether the asset's published state says it is in the domain's adverse condition.</summary>
    /// <remarks>
    /// Read from the wire state rather than from the events, deliberately. Inferring the
    /// condition from the events would make the whole invariant circular: a scenario that never
    /// became adverse raises nothing, and "raised nothing" is what this test is otherwise looking
    /// for.
    /// </remarks>
    /// <param name="domain">Domain being exercised.</param>
    /// <param name="state">The asset's latest published state.</param>
    /// <returns><see langword="true"/> when the condition really is in force.</returns>
    private static bool IsInAdverseCondition(AssetDomain domain, AssetState state) => domain switch
    {
        AssetDomain.Air => state.Power.PercentRemaining is { } percent && percent < 20.0,
        AssetDomain.Ground => state.DomainState is GroundDomainState { IsImmobilised: true },
        AssetDomain.Surface => state.DomainState is SurfaceDomainState { IsInsideWaterMask: false },
        _ => throw new InvalidOperationException(
            $"Domain '{domain}' has no adverse-condition predicate; add one in the same change "
            + "that adds the domain."),
    };

    /// <summary>A slope no platform of this class can climb, in radians.</summary>
    /// <param name="vehicleClass">Ground class the slope has to defeat.</param>
    /// <returns>A gradient past the profile's declared limit.</returns>
    /// <exception cref="InvalidOperationException">The class has no ground motion model.</exception>
    private static double UnclimbableGradeRad(VehicleClass vehicleClass)
    {
        var profile = GroundProfile.ForVehicleClass(vehicleClass)
            ?? throw new InvalidOperationException(
                $"'{vehicleClass}' stands for the ground domain but has no ground profile.");

        // A fifth of a radian past the limit: unambiguously beyond it, and still a slope a
        // reversing vehicle can back down rather than a cliff it cannot address at all.
        return profile.MaxClimbableGradeRad + 0.2;
    }

    /// <summary>Scene-frame point on the dry half of the beach, above the water line.</summary>
    /// <remarks>
    /// One height-field column east of the origin, where <see cref="BeachGrid"/> puts the bed at
    /// twenty-five metres — comfortably above the shipped preset's water surface, so the vessel
    /// is aground by construction rather than by arithmetic that could drift.
    /// </remarks>
    private static readonly Vector3 AshorePosition = new((float)DemSpacingM, 0f, 0f);

    /// <summary>Scene-frame point in the deep half of the beach, west of the water line.</summary>
    private static readonly Vector3 AfloatPosition = new((float)(-1.5 * DemSpacingM), 0f, 0f);

    /// <summary>A bed rising from fifty metres below the datum to fifty above it, west to east.</summary>
    /// <remarks>
    /// The shipped preset's water surface sits a few metres below the datum, so the water line
    /// lands near the scene origin and both halves of the beach are a whole column wide. A real
    /// gradient rather than a step, because a refused move is deflected along the bed contour and
    /// a vertical bed has no contour to deflect along.
    /// </remarks>
    /// <returns>The grid.</returns>
    private static float[,] BeachGrid() => RampGrid(baseElevationM: -50.0, gradient: 0.25);

    /// <summary>Steps a room far enough for a freshly placed asset to settle into its condition.</summary>
    /// <param name="room">Room to step.</param>
    private static void Settle(SimulationRoom room)
    {
        for (int tick = 0; tick < SettleTicks; tick++)
        {
            room.StepOnce();
        }
    }

    /// <summary>Sends one already-translated command to the probe asset and requires acceptance.</summary>
    /// <param name="room">Room holding the asset.</param>
    /// <param name="kind">Command to send.</param>
    /// <param name="target">Scene-frame destination, when the command takes one.</param>
    /// <param name="speedMps">Commanded speed, when the command takes one.</param>
    /// <param name="headingRad">Commanded heading or course, when the command takes one.</param>
    private static void Commanded(
        SimulationRoom room,
        AssetCommandKind kind,
        Vector3? target = null,
        double? speedMps = null,
        double? headingRad = null)
    {
        var result = Send(room, kind, target, speedMps, headingRad);

        result.IsAccepted.Should().BeTrue(
            $"the fixture's own '{kind}' has to land for the case to mean anything; it was "
            + $"refused with '{result.Reason}'");
    }

    /// <summary>Sends one already-translated command to the probe asset.</summary>
    /// <param name="room">Room holding the asset.</param>
    /// <param name="kind">Command to send.</param>
    /// <param name="target">Scene-frame destination, when the command takes one.</param>
    /// <param name="speedMps">Commanded speed, when the command takes one.</param>
    /// <param name="headingRad">Commanded heading or course, when the command takes one.</param>
    /// <returns>The asset's answer.</returns>
    private static AssetCommandResult Send(
        SimulationRoom room,
        AssetCommandKind kind,
        Vector3? target = null,
        double? speedMps = null,
        double? headingRad = null) =>
        room.SendAssetCommand(new SimulatedAssetCommand(
            Kind: kind,
            AssetId: ProbeId,
            Target: target is { } position ? ScenePose(position) : null,
            SpeedMps: speedMps,
            HeadingRad: headingRad));

    // ─── Invariant 3 helpers ────────────────────────────────────────────────

    /// <summary>What one adverse-state case observed.</summary>
    /// <param name="Domain">Domain the case exercised.</param>
    /// <param name="StateName">Human-readable name of the state, for the failure message.</param>
    /// <param name="RecoveryKind">Catalog token of the command that is supposed to recover it.</param>
    /// <param name="ReachedState">Whether the asset actually entered the state.</param>
    /// <param name="RecoveryAccepted">Whether the asset accepted the recovery command.</param>
    /// <param name="RecoveryReason">The refusal token, when it did not.</param>
    /// <param name="SituationChanged">Whether accepting the command visibly changed anything.</param>
    /// <param name="RecoveryAdvertised">Whether the capability report offers the recovery command.</param>
    private sealed record RecoveryOutcome(
        AssetDomain Domain,
        string StateName,
        string RecoveryKind,
        bool ReachedState,
        bool RecoveryAccepted,
        string? RecoveryReason,
        bool SituationChanged,
        bool RecoveryAdvertised);

    /// <summary>Every adverse, terminal-looking state this build's domains can reach.</summary>
    /// <remarks>
    /// One entry per (domain, state), and the coverage guard in the test requires every domain to
    /// appear. The list is short because the states are: a latched emergency stop, ground that
    /// will not carry a vehicle, water a hull may not float in, and an airframe on the ground
    /// with its integration switched off.
    /// <para>
    /// <b>The air domain has no latched-refusal case, and that is a fact rather than a gap.</b>
    /// <see cref="AirAsset"/> holds no emergency-stop latch — a multirotor stops by holding
    /// position, so its stop command is an ordinary hover and refuses nothing afterwards. The one
    /// state that genuinely freezes an air asset is a completed landing, because the SDK's world
    /// skips a drone reporting <c>HasLanded</c>, and that is the case listed here.
    /// </para>
    /// </remarks>
    /// <returns>One thunk per case; each builds its own session and runs it.</returns>
    private static Func<RecoveryOutcome>[] AdverseStateCases() =>
    [
        LandedAirAssetTakesOffAgain,
        ImmobilisedRoverBacksOut,
        () => LatchedAssetIsReleased(AssetDomain.Ground),
        AgroundVesselWorksItselfOff,
        () => LatchedAssetIsReleased(AssetDomain.Surface),
    ];

    /// <summary>A landed drone is not a frozen one: any command re-arms it and it flies again.</summary>
    /// <returns>What the case observed.</returns>
    private static RecoveryOutcome LandedAirAssetTakesOffAgain()
    {
        const string state = "landed";
        var room = CreateRoom();
        var vehicleClass = RepresentativeClass(AssetDomain.Air);

        // Low enough that the descent finishes inside the settle budget; the model's landed
        // threshold is half a metre above the scene floor, which is where a landing ends whatever
        // the terrain does.
        TryPlace(room, vehicleClass, new Vector3(0f, 3f, 0f), headingRad: 0.0).Should().BeTrue();
        Commanded(room, AssetCommandKind.Land);

        for (int tick = 0; tick < RecoveryTicks && Airborne(room); tick++)
        {
            room.StepOnce();
            room.CaptureAssetFrame();
        }

        bool reached = !Airborne(room);

        var recovery = Send(room, AssetCommandKind.Takeoff);

        for (int tick = 0; tick < SettleTicks; tick++)
        {
            room.StepOnce();
            room.CaptureAssetFrame();
        }

        return new RecoveryOutcome(
            AssetDomain.Air,
            state,
            CommandKinds.Takeoff,
            ReachedState: reached,
            RecoveryAccepted: recovery.IsAccepted,
            RecoveryReason: recovery.Reason,
            SituationChanged: Airborne(room),
            RecoveryAdvertised: IsAdvertised(room, CommandKinds.Takeoff));
    }

    /// <summary>Whether the probe drone is off the ground, read from its published state.</summary>
    /// <param name="room">Room holding it.</param>
    /// <returns><see langword="true"/> when airborne.</returns>
    private static bool Airborne(SimulationRoom room) =>
        StateOf(room).DomainState is AirDomainState { IsAirborne: true };

    /// <summary>A rover stuck on ground it cannot climb still backs out the way it came in.</summary>
    /// <returns>What the case observed.</returns>
    private static RecoveryOutcome ImmobilisedRoverBacksOut()
    {
        const string state = "immobilised";
        var room = CreateRoom();
        var vehicleClass = RepresentativeClass(AssetDomain.Ground);

        EnterAdverseCondition(room, AssetDomain.Ground, vehicleClass);

        bool reached = IsInAdverseCondition(AssetDomain.Ground, StateOf(room));
        var start = PositionOf(room);

        // Backing out is the one manoeuvre the terrain has already proved possible: the vehicle
        // is facing up the slope, so reverse takes it back down the way it drove in.
        var recovery = Send(room, AssetCommandKind.Reverse, speedMps: 1.5);

        for (int tick = 0; tick < RecoveryTicks; tick++)
        {
            room.StepOnce();
        }

        return new RecoveryOutcome(
            AssetDomain.Ground,
            state,
            CommandKinds.Reverse,
            ReachedState: reached,
            RecoveryAccepted: recovery.IsAccepted,
            RecoveryReason: recovery.Reason,
            SituationChanged: PlanarDistance(start, PositionOf(room)) > MovedM,
            RecoveryAdvertised: IsAdvertised(room, CommandKinds.Reverse));
    }

    /// <summary>A vessel put ashore drives itself back into water it is entitled to be in.</summary>
    /// <returns>What the case observed.</returns>
    private static RecoveryOutcome AgroundVesselWorksItselfOff()
    {
        const string state = "aground";
        var room = CreateRoom();
        var vehicleClass = RepresentativeClass(AssetDomain.Surface);

        room.SetHeightmap(BeachGrid(), DemExtentM, DemExtentM);
        TryPlace(room, vehicleClass, AshorePosition, East).Should().BeTrue();
        Settle(room);

        bool reached = IsInAdverseCondition(AssetDomain.Surface, StateOf(room));
        var start = PositionOf(room);

        // Seaward, down the bed. A route off a beach begins on the beach, so the hull is exempt
        // from the passage sweep and only the destination is vetted — which is deep water.
        var recovery = Send(room, AssetCommandKind.TransitTo, target: AfloatPosition);

        for (int tick = 0; tick < AgroundRecoveryTicks; tick++)
        {
            room.StepOnce();
        }

        return new RecoveryOutcome(
            AssetDomain.Surface,
            state,
            CommandKinds.TransitTo,
            ReachedState: reached,
            RecoveryAccepted: recovery.IsAccepted,
            RecoveryReason: recovery.Reason,
            SituationChanged: PlanarDistance(start, PositionOf(room)) > MovedM,
            RecoveryAdvertised: IsAdvertised(room, CommandKinds.TransitTo));
    }

    /// <summary>An emergency-stopped asset is released by the one command that is never gated.</summary>
    /// <remarks>
    /// The latch is a trap unless something can always reach through it. <c>stop</c> is one of the
    /// two commands the catalog permits in every operational state, which is what makes it the
    /// release: an emergency-stopped asset publishes
    /// <see cref="OperationalState.Emergency"/>, which the ordinary operable policy excludes, so
    /// a release gated on that policy could never be issued.
    /// <para>
    /// The situation change is observed with <c>hold</c> rather than with movement, so the case
    /// says nothing about the terrain underneath and can be run identically in any domain that
    /// latches. <c>hold</c> is refused while the latch is set and accepted once it is not, which
    /// is precisely "the state changed".
    /// </para>
    /// </remarks>
    /// <param name="domain">Domain whose executor latches an emergency stop.</param>
    /// <returns>What the case observed.</returns>
    private static RecoveryOutcome LatchedAssetIsReleased(AssetDomain domain)
    {
        const string state = "emergency-stopped";
        var room = CreateRoom();
        var vehicleClass = RepresentativeClass(domain);

        PrepareWorld(room, domain);
        TryPlace(room, vehicleClass, SpawnPointFor(domain), headingRad: 0.0).Should().BeTrue();

        Commanded(room, AssetCommandKind.EmergencyStop);

        // The state is only terminal-looking if it actually refuses something; a latch that
        // refuses nothing is not a trap and this case would be vacuous without the probe.
        bool reached = !Send(room, AssetCommandKind.Hold).IsAccepted;

        var recovery = Send(room, AssetCommandKind.Stop);

        return new RecoveryOutcome(
            domain,
            state,
            CommandKinds.Stop,
            ReachedState: reached,
            RecoveryAccepted: recovery.IsAccepted,
            RecoveryReason: recovery.Reason,
            SituationChanged: Send(room, AssetCommandKind.Hold).IsAccepted,
            RecoveryAdvertised: IsAdvertised(room, CommandKinds.Stop));
    }

    /// <summary>Whether the capability endpoint offers a command kind for the probe asset.</summary>
    /// <param name="room">Room holding the asset.</param>
    /// <param name="kind">Catalog token to look for.</param>
    /// <returns><see langword="true"/> when the report lists it.</returns>
    private static bool IsAdvertised(SimulationRoom room, string kind)
    {
        var controller = ControllerFor(room, configuration: null);
        var report = Body<AssetCapabilitiesResponse>(controller.GetAssetCapabilities(ProbeId));

        return report.Commands.Any(c => string.Equals(c.Kind, kind, StringComparison.Ordinal));
    }
}
