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
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Assets.Ground;
using ResQ.Viz.Web.Services.Assets.Surface;

namespace ResQ.Viz.Web.Tests;

// What the reference fleet is made of, and how it is put under way. Split from the cases the way
// every other v2 suite here is split: reading what a gate asserts should not mean scrolling past
// how its hundred and fifty vehicles were built. Where each one goes is decided in
// MixedFleetLoadTests.Sites.cs; the instruments pointed at the result live in
// MixedFleetLoadTests.Measurement.cs. The suite's summary lives on the primary declaration in
// MixedFleetLoadTests.cs.
//
// A PARKED FLEET IS NOT A LOAD. Staging is not finished when the assets exist: a fleet that is
// not moving takes the cheap branch of every integrator and publishes a delta of nothing but
// stamps, so a gate measured on one reports a cost and a bandwidth this deployment never sees.
// Every commandable asset leaves this file under way, and every acceptance is asserted rather
// than assumed — a silently refused command is the quietest way a load gate stops measuring load.
public sealed partial class MixedFleetLoadTests
{
    /// <summary>Assets in the reference fleet: the figure this work was designed against.</summary>
    private const int AirCount = 50;

    /// <summary>Rovers in the reference fleet.</summary>
    private const int GroundCount = 50;

    /// <summary>Vessels in the reference fleet.</summary>
    private const int SurfaceCount = 50;

    /// <summary>Total reference fleet size.</summary>
    private const int FleetSize = AirCount + GroundCount + SurfaceCount;

    /// <summary>Assets per domain in the small fleet the scaling comparison is made against.</summary>
    /// <remarks>
    /// A tenth of the reference fleet, so the ratio between the two costs has a known linear
    /// expectation of ten and a known quadratic expectation of a hundred. Anything else would
    /// make the bound a number nobody could reason about.
    /// </remarks>
    private const int SmallFleetPerDomain = 5;

    /// <summary>The only shipped preset whose water surface is above the datum.</summary>
    private const string CoastalPreset = "coastal";

    /// <summary>World steps between broadcast frames: 60 Hz stepping, 10 Hz frames.</summary>
    private const int StepsPerFrame = 6;

    /// <summary>Cruise speed every commanded asset is given, in metres per second.</summary>
    private const double CruiseSpeedMps = 3.0;

    /// <summary>Height above local terrain a drone is launched from, in metres.</summary>
    private const double LaunchHeightM = 45.0;

    /// <summary>Serialises a wire record exactly as the hub would, so a measured size is the size sent.</summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Wall-clock instant every frame in this suite is stamped with.</summary>
    /// <remarks>
    /// Fixed rather than sampled, so the one clock the frame builder reads cannot make two
    /// otherwise identical runs differ. The stamps the <em>capture</em> reads are handled in
    /// <see cref="Digest"/>.
    /// </remarks>
    private static readonly DateTimeOffset ServerTime = new(2024, 5, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The ground classes staged, cycled so no single motion model dominates the load.</summary>
    private static readonly VehicleClass[] GroundClasses =
    [
        VehicleClass.AckermannRover,
        VehicleClass.DifferentialRover,
        VehicleClass.TrackedRover,
    ];

    /// <summary>What one staged fleet is, so a case can command it without re-deriving anything.</summary>
    /// <param name="AirIds">Drone identifiers, in spawn order.</param>
    /// <param name="GroundIds">Rover identifiers, in spawn order.</param>
    /// <param name="GroundTargets">Vetted destination for each rover, in the scene frame.</param>
    /// <param name="SurfaceIds">Vessel identifiers, in spawn order.</param>
    /// <param name="SurfaceCourses">Commanded course for each vessel, radians clockwise from true north.</param>
    private sealed record FleetPlan(
        IReadOnlyList<string> AirIds,
        IReadOnlyList<string> GroundIds,
        IReadOnlyList<Vector3> GroundTargets,
        IReadOnlyList<string> SurfaceIds,
        IReadOnlyList<double> SurfaceCourses);

    /// <summary>One timed frame: what it cost to assemble, what it cost to serialise, and how big it was.</summary>
    /// <param name="BuildMs">Capture plus assembly, in milliseconds.</param>
    /// <param name="SerialiseMs">JSON serialisation, in milliseconds.</param>
    /// <param name="Bytes">Serialised size in UTF-8 bytes.</param>
    private readonly record struct FrameSample(double BuildMs, double SerialiseMs, int Bytes)
    {
        /// <summary>What the broadcast path actually spends per frame.</summary>
        public double TotalMs => BuildMs + SerialiseMs;
    }

    // ─── Room and fleet construction ────────────────────────────────────────

    /// <summary>A fresh room on the coastal preset, which is the only one with water above the datum.</summary>
    /// <remarks>
    /// The identifier is a constant rather than a fresh string per room, so two rooms staged for
    /// the determinism case agree on everything a room id reaches — the environment revision
    /// token and the delta chain's keyframe phase included.
    /// </remarks>
    /// <param name="roomId">Identifier for the room.</param>
    /// <returns>A room with the coastal terrain and its sea level installed, holding no assets.</returns>
    private static SimulationRoom CreateRoom(string roomId = "load-room")
    {
        var room = new SimulationRoom(id: roomId, ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);
        room.SetTerrainPreset(CoastalPreset);
        return room;
    }

    /// <summary>The frame builder holding this deployment's survivor and hazard data.</summary>
    /// <remarks>
    /// Built from the file the host itself loads rather than from the parameterless constructor,
    /// because that one ships no survivors and no hazards: a frame assembled from it skips the
    /// detection pass entirely and carries none of the payload a real frame carries, so both the
    /// timing and the size measured against it would be of something this deployment never sends.
    /// </remarks>
    /// <returns>A builder configured exactly as the host configures its own.</returns>
    private static VizFrameBuilder ShippedFrameBuilder() =>
        new(new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build());

    /// <summary>Stages one mixed fleet into a room and leaves every asset under way.</summary>
    /// <remarks>
    /// <b>The fleet has to be moving for any of this to mean anything.</b> A parked fleet steps
    /// through the cheap branch of every integrator, and its delta carries nothing but stamps —
    /// so a gate measured on one would report a cost and a bandwidth the deployment never sees.
    /// Drones are left to the swarm coordinator, which is how they fly in production; rovers are
    /// sent to a surveyed destination; vessels are put on a course, which is the one surface
    /// command that needs no route validation and so cannot make a staging step depend on where
    /// the coastline happens to run.
    /// </remarks>
    /// <param name="room">Room to stage into; must already be on the coastal preset.</param>
    /// <param name="air">Drones to launch.</param>
    /// <param name="ground">Rovers to place.</param>
    /// <param name="surface">Vessels to place.</param>
    /// <returns>What was staged, so a caller can address it later.</returns>
    private static FleetPlan StageFleet(SimulationRoom room, int air, int ground, int surface)
    {
        var sites = SurveySites(room, air + ground, surface);

        sites.Land.Count.Should().Be(
            air + ground,
            "the coastal preset must offer {0} dry, gently-sloped sites for a fleet of this size",
            air + ground);
        sites.Water.Count.Should().Be(
            surface,
            "the coastal preset must offer {0} navigable sites deeper than {1} m",
            surface,
            MinSiteDepthM);

        var airIds = new List<string>(air);
        for (var i = 0; i < air; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"uav-{i:D3}");
            var site = sites.Land[i];
            room.AddDrone(id, new Vector3(site.X, (float)(site.Y + LaunchHeightM), site.Z), vendor: null);
            airIds.Add(id);
        }

        var groundIds = new List<string>(ground);
        var groundTargets = new List<Vector3>(ground);
        for (var i = 0; i < ground; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"ugv-{i:D3}");
            SpawnGround(room, id, GroundClasses[i % GroundClasses.Length], sites.Land[air + i], i * 0.37);
            groundIds.Add(id);
            groundTargets.Add(NearestSite(sites.Land, sites.Land[air + i]));
        }

        var surfaceIds = new List<string>(surface);
        var surfaceCourses = new List<double>(surface);
        for (var i = 0; i < surface; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"usv-{i:D3}");
            var course = (i * 0.41) % (2.0 * Math.PI);
            SpawnSurface(room, id, sites.Water[i], course);
            surfaceIds.Add(id);
            surfaceCourses.Add(course);
        }

        var fleet = new FleetPlan(airIds, groundIds, groundTargets, surfaceIds, surfaceCourses);
        OrderFleetUnderway(room, fleet);
        return fleet;
    }

    /// <summary>Builds and registers one rover, both inside the room's own lock.</summary>
    /// <param name="room">Room to spawn into.</param>
    /// <param name="assetId">Identifier for the rover.</param>
    /// <param name="vehicleClass">Ground class to build.</param>
    /// <param name="siteEus">Surveyed site, in the scene frame.</param>
    /// <param name="headingRad">Initial heading, radians clockwise from true north.</param>
    private static void SpawnGround(
        SimulationRoom room, string assetId, VehicleClass vehicleClass, Vector3 siteEus, double headingRad)
    {
        var plan = new AssetSpawnPlan(
            AssetId: assetId,
            VehicleClass: vehicleClass,
            Descriptor: AssetProfiles.Create(assetId, vehicleClass),
            PositionEus: siteEus,
            HeadingRad: headingRad);

        room.TrySpawnAsset(assetId, env => new GroundAssetFactory(env).Create(plan), out var reason)
            .Should().BeTrue("'{0}' must spawn; it was refused with '{1}'", assetId, reason);
    }

    /// <summary>Builds and registers one vessel, both inside the room's own lock.</summary>
    /// <param name="room">Room to spawn into.</param>
    /// <param name="assetId">Identifier for the vessel.</param>
    /// <param name="siteEus">Surveyed navigable site, in the scene frame.</param>
    /// <param name="headingRad">Initial heading, radians clockwise from true north.</param>
    private static void SpawnSurface(
        SimulationRoom room, string assetId, Vector3 siteEus, double headingRad)
    {
        var plan = new AssetSpawnPlan(
            AssetId: assetId,
            VehicleClass: VehicleClass.SurfaceVessel,
            Descriptor: AssetProfiles.Create(assetId, VehicleClass.SurfaceVessel),
            PositionEus: siteEus,
            HeadingRad: headingRad);

        room.TrySpawnAsset(assetId, env => new SurfaceAssetFactory(env).Create(plan), out var reason)
            .Should().BeTrue("'{0}' must spawn; it was refused with '{1}'", assetId, reason);
    }

    /// <summary>Puts every commandable asset in a staged fleet under way.</summary>
    /// <remarks>
    /// Acceptance is asserted rather than assumed. A refusal here would leave that asset parked
    /// while every measurement below went on reporting a fleet of the nominal size, which is the
    /// quietest way a load gate can stop measuring load.
    /// </remarks>
    /// <param name="room">Room holding the fleet.</param>
    /// <param name="fleet">What was staged.</param>
    private static void OrderFleetUnderway(SimulationRoom room, FleetPlan fleet)
    {
        for (var i = 0; i < fleet.GroundIds.Count; i++)
        {
            var command = new SimulatedAssetCommand(
                Kind: AssetCommandKind.DriveTo,
                AssetId: fleet.GroundIds[i],
                Target: new FramedPose(
                    CoordinateFrame.LocalEus, OriginId: null,
                    fleet.GroundTargets[i], Quaternion.Identity),
                SpeedMps: CruiseSpeedMps);

            var result = room.SendAssetCommand(command);
            result.IsAccepted.Should().BeTrue(
                "'{0}' must accept driveTo; it was refused with '{1}'", fleet.GroundIds[i], result.Reason);
        }

        for (var i = 0; i < fleet.SurfaceIds.Count; i++)
        {
            var command = new SimulatedAssetCommand(
                Kind: AssetCommandKind.SetCourse,
                AssetId: fleet.SurfaceIds[i],
                SpeedMps: CruiseSpeedMps,
                HeadingRad: fleet.SurfaceCourses[i]);

            var result = room.SendAssetCommand(command);
            result.IsAccepted.Should().BeTrue(
                "'{0}' must accept setCourse; it was refused with '{1}'", fleet.SurfaceIds[i], result.Reason);
        }
    }
}
