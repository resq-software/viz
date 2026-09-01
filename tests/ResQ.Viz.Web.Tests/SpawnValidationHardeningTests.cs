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

using System.Numerics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>
/// Regressions for the three ways untrusted input used to reach retained state unchecked: an
/// unbounded descriptor string, a coordinate that arrives by being absent, and an orientation
/// large enough that normalising it produces one nobody sent.
/// </summary>
/// <remarks>
/// Grouped by the property they defend rather than by the endpoint they poke, because all three
/// are the same mistake at different layers — trusting a value the caller never had to justify.
/// Every refusal case also asserts the session is unchanged afterwards, so "rejected" keeps
/// meaning "nothing happened" rather than merely "an error was also returned".
/// </remarks>
public sealed class SpawnValidationHardeningTests
{
    /// <summary>
    /// Serializer options mirroring the wire path: MVC's formatters and SignalR's JSON hub
    /// protocol both use web defaults, so camelCase names and numeric enums are what a caller
    /// actually sends.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private const int EusFrame = (int)CoordinateFrame.LocalEus;

    // ─── Fixture ────────────────────────────────────────────────────────────

    private static (SimV2Controller Ctrl, SimulationRoom Room) CreateController()
    {
        var room = new SimulationRoom(
            id: "test-room-hardening", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);
        var ctrl = new SimV2Controller(
            new VizFrameBuilder(), [], NullLogger<SimV2Controller>.Instance);

        // Same shortcut the other controller tests use: stash the resolved room where
        // RequireRoomAttribute would have put it, so these stay unit tests.
        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctrl, room);
    }

    private static FramedPose Pose(float x, float y, float z, Quaternion orientation = default) =>
        new(CoordinateFrame.LocalEus, OriginId: null, new Vector3(x, y, z), orientation);

    private static CommandProblemDetails Problem(IActionResult result, int expectedStatus)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(expectedStatus);
        return objectResult.Value.Should().BeOfType<CommandProblemDetails>().Which;
    }

    private static AssetSpawnResponse Spawned(IActionResult result) =>
        result.Should().BeOfType<CreatedResult>().Which
            .Value.Should().BeOfType<AssetSpawnResponse>().Which;

    // ─── B1: free-text descriptor fields are bounded ────────────────────────

    /// <summary>
    /// A caller-supplied vendor is retained on the descriptor and re-serialised into every frame
    /// broadcast to every client, so an unbounded one is retained storage rather than a
    /// transient payload. It is refused outright, and nothing survives the refusal.
    /// </summary>
    [Fact]
    public void SpawnAsset_Rejects_An_Unbounded_Vendor_Without_Retaining_It()
    {
        var (ctrl, room) = CreateController();

        var problem = Problem(
            ctrl.SpawnAsset(new AssetSpawnRequest(
                VehicleClass.Multirotor,
                Pose(10f, 50f, 20f, Quaternion.Identity),
                AssetId: "uav-1",
                Vendor: new string('a', 20_000))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(AssetProblems.RequestInvalid);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("vendor");
        room.GetSnapshot().Should().BeEmpty("a refused spawn must retain nothing at all");
    }

    /// <summary>
    /// The charset is an allow-list, so a vendor cannot smuggle a line break into the log record
    /// the spawn writes, nor markup into a client that renders the value.
    /// </summary>
    /// <param name="vendor">Vendor string carrying a character outside the allow-list.</param>
    [Theory]
    [InlineData("Acme\r\n2024-01-01 INFO forged log line")]
    [InlineData("Acme\tInc")]
    [InlineData("<script>alert(1)</script>")]
    public void SpawnAsset_Rejects_A_Vendor_Outside_The_Allow_List(string vendor)
    {
        var (ctrl, room) = CreateController();

        Problem(
            ctrl.SpawnAsset(new AssetSpawnRequest(
                VehicleClass.Multirotor,
                Pose(10f, 50f, 20f, Quaternion.Identity),
                AssetId: "uav-1",
                Vendor: vendor)),
            StatusCodes.Status400BadRequest)
            .Errors.Should().ContainSingle().Which.Field.Should().Be("vendor");
        room.GetSnapshot().Should().BeEmpty();
    }

    /// <summary>A name an operator would really type still spawns, and is stored verbatim.</summary>
    /// <remarks>
    /// The counterweight to the refusals above: a limit nobody can work within is a limit that
    /// gets deleted, so the accepted case is pinned as tightly as the rejected ones.
    /// </remarks>
    [Fact]
    public void SpawnAsset_Accepts_And_Retains_A_Realistic_Vendor_Name()
    {
        var (ctrl, _) = CreateController();

        var response = Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.Multirotor,
            Pose(10f, 50f, 20f, Quaternion.Identity),
            AssetId: "uav-1",
            Vendor: "Blue Robotics, Inc.")));

        response.Descriptor.Vendor.Should().Be("Blue Robotics, Inc.");
    }

    /// <summary>The length limit is a boundary, not a suggestion: 64 in, 65 out.</summary>
    [Fact]
    public void SpawnAsset_Bounds_A_Vendor_At_SixtyFour_Characters()
    {
        var (ctrl, _) = CreateController();

        Spawned(ctrl.SpawnAsset(new AssetSpawnRequest(
            VehicleClass.Multirotor,
            Pose(0f, 50f, 0f, Quaternion.Identity),
            AssetId: "uav-1",
            Vendor: new string('v', 64))))
            .Descriptor.Vendor.Should().HaveLength(64);

        Problem(
            ctrl.SpawnAsset(new AssetSpawnRequest(
                VehicleClass.Multirotor,
                Pose(0f, 50f, 0f, Quaternion.Identity),
                AssetId: "uav-2",
                Vendor: new string('v', 65))),
            StatusCodes.Status400BadRequest)
            .Errors.Should().ContainSingle().Which.Field.Should().Be("vendor");
    }

    /// <summary>
    /// Every free-text descriptor field is bounded, not just the vendor — and the payload is
    /// judged before this build's motion-model coverage is, so an over-long name is reported as
    /// the caller's mistake instead of hiding behind an unrelated 501.
    /// </summary>
    /// <param name="field">Name of the descriptor field carrying the over-long value.</param>
    [Theory]
    [InlineData("displayName")]
    [InlineData("model")]
    [InlineData("agencyId")]
    [InlineData("fleetId")]
    public void SpawnAsset_Bounds_Every_Free_Text_Field_Before_Reporting_A_Missing_Model(string field)
    {
        var (ctrl, room) = CreateController();
        var overlong = new string('x', 512);

        var request = new AssetSpawnRequest(
            VehicleClass.AckermannRover,
            Pose(5f, 0f, 5f),
            AssetId: "ugv-1",
            DisplayName: field == "displayName" ? overlong : null,
            Model: field == "model" ? overlong : null,
            AgencyId: field == "agencyId" ? overlong : null,
            FleetId: field == "fleetId" ? overlong : null);

        var problem = Problem(ctrl.SpawnAsset(request), StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(AssetProblems.RequestInvalid);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be(field);
        room.CaptureAssetFrame().Assets.Should().BeEmpty();
    }

    /// <summary>
    /// The guard is not a blanket refusal: with acceptable metadata and no registered motion
    /// model, the spawn still reports the gap it always did.
    /// </summary>
    [Fact]
    public void SpawnAsset_With_Acceptable_Metadata_Still_Reports_A_Missing_Motion_Model()
    {
        var (ctrl, _) = CreateController();

        Problem(
            ctrl.SpawnAsset(new AssetSpawnRequest(
                VehicleClass.AckermannRover,
                Pose(5f, 0f, 5f),
                AssetId: "ugv-1",
                DisplayName: "Rover One",
                FleetId: "fleet-a")),
            StatusCodes.Status501NotImplemented)
            .Code.Should().Be(AssetProblems.MobilityModelUnavailable);
    }

    // ─── B2: an absent coordinate is absent, not the origin ─────────────────

    /// <summary>
    /// An omitted position used to bind (0, 0, 0) — the scene origin — which is a perfectly good
    /// position no consumer could tell from one a caller meant. It is now a deserialisation
    /// failure, so nothing can reach the map centre by saying nothing.
    /// </summary>
    [Fact]
    public void FramedPose_Without_A_Position_Is_Refused_Rather_Than_Bound_To_The_Origin()
    {
        var json = $$$"""{"frame":{{{EusFrame}}},"orientation":{"x":0,"y":0,"z":0,"w":1}}""";

        var read = () => JsonSerializer.Deserialize<FramedPose>(json, WireOptions);

        read.Should().Throw<JsonException>();
    }

    /// <summary>
    /// The same rule reached through the shape a command actually carries: a point target whose
    /// pose omits its position is refused at the wire, before any validation gate sees it.
    /// </summary>
    [Fact]
    public void PointCommandTarget_Without_A_Position_Is_Refused_At_The_Wire()
    {
        var json = $$$"""{"type":"point","point":{"frame":{{{EusFrame}}}}}""";

        var read = () => JsonSerializer.Deserialize<CommandTarget>(json, WireOptions);

        read.Should().Throw<JsonException>();
    }

    /// <summary>
    /// A twist's linear and angular parts get the same treatment: "stationary" is a claim, and
    /// an omitted velocity must not be able to make it.
    /// </summary>
    /// <param name="json">A twist payload missing one of its two velocity vectors.</param>
    [Theory]
    [InlineData("""{"frame":2,"angular":{"x":0,"y":0,"z":1}}""")]
    [InlineData("""{"frame":2,"linear":{"x":1,"y":0,"z":0}}""")]
    public void FramedTwist_Without_Both_Velocities_Is_Refused(string json)
    {
        var read = () => JsonSerializer.Deserialize<FramedTwist>(json, WireOptions);

        read.Should().Throw<JsonException>();
    }

    /// <summary>
    /// The audit's other half. An absent orientation stays legal, because unlike a position its
    /// default is not a value a caller could have meant: the all-zero quaternion is not a
    /// rotation, so every boundary can still tell "undeclared" from "declared".
    /// </summary>
    [Fact]
    public void FramedPose_Without_An_Orientation_Binds_A_Rotation_No_Consumer_Mistakes_For_One()
    {
        var json = $$$"""{"frame":{{{EusFrame}}},"position":{"x":1,"y":2,"z":3}}""";

        var pose = JsonSerializer.Deserialize<FramedPose>(json, WireOptions);

        pose.Should().NotBeNull();
        pose!.Position.Should().Be(new Vector3(1f, 2f, 3f));
        pose.Orientation.Should().Be(default(Quaternion));
        CoordinateFrames.TryValidate(pose, out var error).Should().BeFalse(
            "an undeclared attitude is refused wherever a rotation is actually needed");
        error.Should().Be("pose.orientation.degenerate");
    }

    /// <summary>A wholly-specified pose is unaffected: the fix bounds absence, not content.</summary>
    [Fact]
    public void FramedPose_With_Every_Component_Still_Round_Trips()
    {
        var pose = new FramedPose(
            CoordinateFrame.LocalEus, "origin-1", new Vector3(12.5f, 34.25f, -8.75f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.75f));

        var restored = JsonSerializer.Deserialize<FramedPose>(
            JsonSerializer.Serialize(pose, WireOptions), WireOptions);

        restored.Should().Be(pose);
    }

    // ─── B3: an orientation too large to normalise ──────────────────────────

    /// <summary>
    /// Documents the failure the upper bound exists for, so the number chosen in
    /// <c>CoordinateFrames</c> can be checked rather than trusted.
    /// </summary>
    /// <remarks>
    /// <see cref="Quaternion.Normalize"/> accumulates the squared length in
    /// <see langword="float"/>. One component past <c>sqrt(float.MaxValue)</c> — 1.844674e19 —
    /// squares to infinity, and four components of 1e19 each square finitely but total 4e38,
    /// past the same ceiling. Both reciprocal square roots are zero, so both normalise to the
    /// all-zero quaternion.
    /// </remarks>
    [Fact]
    public void Quaternion_Normalize_Overflows_To_The_Zero_Rotation_As_The_Bound_Assumes()
    {
        Quaternion.Normalize(new Quaternion(1e20f, 0f, 0f, 0f))
            .Should().Be(default(Quaternion));
        Quaternion.Normalize(new Quaternion(1e19f, 1e19f, 1e19f, 1e19f))
            .Should().Be(default(Quaternion));

        // Just under the single-component edge normalisation is still well behaved, which is why
        // this needs an upper bound rather than a finiteness test.
        Quaternion.Normalize(new Quaternion(1e19f, 0f, 0f, 0f))
            .Should().NotBe(default(Quaternion));
    }

    /// <summary>
    /// Pose validation refuses an orientation that would normalise to nothing, in both the
    /// single-component and the summed-overflow cases.
    /// </summary>
    /// <param name="x">Quaternion X component.</param>
    /// <param name="y">Quaternion Y component.</param>
    /// <param name="z">Quaternion Z component.</param>
    /// <param name="w">Quaternion W component.</param>
    [Theory]
    [InlineData(1e20f, 0f, 0f, 0f)]
    [InlineData(1e19f, 1e19f, 1e19f, 1e19f)]
    [InlineData(3.4e38f, 0f, 0f, 1f)]
    public void TryValidate_Refuses_An_Orientation_Too_Large_To_Normalise(
        float x, float y, float z, float w)
    {
        var pose = new FramedPose(
            CoordinateFrame.LocalEus, "origin-a", Vector3.Zero, new Quaternion(x, y, z, w));

        CoordinateFrames.TryValidate(pose, out var error).Should().BeFalse();
        error.Should().Be("pose.orientation.degenerate");
    }

    /// <summary>
    /// The bound is stated as a squared length of 1e12 — a magnitude of 1e6 — so that is where
    /// the behaviour changes, and a test rather than a comment says so.
    /// </summary>
    [Fact]
    public void TryValidate_Accepts_Up_To_The_Documented_Magnitude_And_No_Further()
    {
        var atBound = new FramedPose(
            CoordinateFrame.LocalEus, null, Vector3.Zero, new Quaternion(1e6f, 0f, 0f, 0f));
        CoordinateFrames.TryValidate(atBound, out _).Should().BeTrue();

        var pastBound = new FramedPose(
            CoordinateFrame.LocalEus, null, Vector3.Zero, new Quaternion(2e6f, 0f, 0f, 0f));
        CoordinateFrames.TryValidate(pastBound, out var error).Should().BeFalse();
        error.Should().Be("pose.orientation.degenerate");

        // An ordinary unnormalised attitude — the case the lower bound exists to allow — is
        // untouched by the upper one.
        var unnormalised = new FramedPose(
            CoordinateFrame.LocalEus, null, Vector3.Zero,
            Quaternion.Multiply(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f), 1000f));
        CoordinateFrames.TryValidate(unnormalised, out _).Should().BeTrue();
    }

    /// <summary>
    /// Rotating with an over-large orientation throws instead of quietly returning a vector
    /// nobody asked for: the zero quaternion maps every input to the origin, so a body-frame
    /// velocity would have become no motion at all.
    /// </summary>
    [Fact]
    public void RotateBodyToReference_Throws_Rather_Than_Annihilating_The_Vector()
    {
        var rotate = () => CoordinateFrames.RotateBodyToReference(
            Vector3.UnitX, new Quaternion(1e20f, 0f, 0f, 0f));

        rotate.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// End to end: a spawn carrying such an orientation used to be accepted and placed on
    /// heading zero — true north — because the bearing of an annihilated forward axis falls back
    /// to zero. It is now refused, and nothing is created.
    /// </summary>
    [Fact]
    public void SpawnAsset_Refuses_An_Over_Large_Orientation_Instead_Of_Facing_North()
    {
        var (ctrl, room) = CreateController();

        var problem = Problem(
            ctrl.SpawnAsset(new AssetSpawnRequest(
                VehicleClass.Multirotor,
                Pose(10f, 50f, 20f, new Quaternion(1e20f, 0f, 0f, 0f)),
                AssetId: "uav-1")),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(AssetProblems.PoseInvalid);
        room.GetSnapshot().Should().BeEmpty();
    }
}
