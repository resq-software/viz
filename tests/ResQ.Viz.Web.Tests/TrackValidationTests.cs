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
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Filters;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using ResQ.Viz.Web.Services.Tracks;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Enums bound straight from JSON on the v2 request surface.</summary>
/// <remarks>
/// <c>System.Text.Json</c> binds a numeric enum without checking that the number names a member,
/// so an enum-typed field is only validated if something validates it. Every field on a request
/// that reaches the simulation or the wire therefore needs the same treatment the numbers and
/// strings beside it already get, and this suite is the standing proof that each one has it.
/// <para>
/// The cases assert on the <b>code</b> and the <b>field path</b>, never on prose: the code is the
/// contract and the wording beside it may be rewritten at any time.
/// </para>
/// <para>
/// Everything here is deterministic. No wall clock is read, no sleep is taken, and the room's
/// simulation clock stands still because no tick loop is attached — so an assertion about what a
/// session holds cannot be moved by timing.
/// </para>
/// </remarks>
public sealed class TrackValidationTests
{
    /// <summary>Identifier the single-contact cases report under.</summary>
    private const string ContactId = "contact-1";

    /// <summary>Values outside every enum this surface binds, including a negative one.</summary>
    /// <remarks>
    /// A negative is carried alongside the large positive because the two fail differently in a
    /// consumer: a large value falls out of a bounds check, while a negative one indexes
    /// backwards out of any table keyed on the enum.
    /// </remarks>
    public static TheoryData<int> UndeclaredValues => new() { 9999, -3, int.MaxValue };

    // ─── Track reports: the enums that reach the fusion store ────────────────

    /// <summary>An undeclared classification is refused with the standard error contract.</summary>
    [Theory]
    [MemberData(nameof(UndeclaredValues))]
    public void An_Undeclared_Classification_Is_Refused(int value)
    {
        var (api, room) = Api();

        var problem = Problem(
            api.ReportTrack(ReportRequest(classification: (TrackClassification)value)),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(TrackProblems.RequestInvalid);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("classification");
        problem.AssetId.Should().BeNull("a contact is never filed under an asset identifier");

        room.CaptureTrackFrame().Tracks.Should().BeEmpty(
            "a refused report leaves the session exactly as it found it");
    }

    /// <summary>An undeclared source kind is refused with the standard error contract.</summary>
    [Theory]
    [MemberData(nameof(UndeclaredValues))]
    public void An_Undeclared_SourceKind_Is_Refused(int value)
    {
        var (api, room) = Api();

        var problem = Problem(
            api.ReportTrack(ReportRequest(sourceKind: (TrackSourceKind)value)),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(TrackProblems.RequestInvalid);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("sourceKind");
        room.CaptureTrackFrame().Tracks.Should().BeEmpty();
    }

    /// <summary>An undeclared transponder kind is refused, on the same contract.</summary>
    /// <remarks>
    /// The cooperative identity is echoed on to the wire untouched, so its kind is the same hole
    /// the classification was: nothing between the request body and the snapshot reads it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(UndeclaredValues))]
    public void An_Undeclared_Transponder_Kind_Is_Refused(int value)
    {
        var (api, _) = Api();

        var problem = Problem(
            api.ReportTrack(ReportRequest(
                transponder: new TransponderIdentity((TransponderKind)value, "abc-123"))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(TrackProblems.RequestInvalid);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("transponder.kind");
    }

    /// <summary>An undeclared vertical datum on the pose's geodetic echo is refused.</summary>
    /// <remarks>
    /// The contact's position travels in the local Cartesian frame, so this datum is never read
    /// by the store — it is copied to the wire as the source reported it, which is exactly why it
    /// has to be a declared value on the way in.
    /// </remarks>
    [Fact]
    public void An_Undeclared_Vertical_Datum_On_The_Pose_Is_Refused()
    {
        var (api, _) = Api();

        var problem = Problem(
            api.ReportTrack(ReportRequest(
                geo: new GeoPosition(40.7128, -74.0060, 12.0, (VerticalReference)77))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(TrackProblems.RequestInvalid);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("pose.geo.verticalReference");
    }

    /// <summary>Every declared member of every enum on the request is accepted.</summary>
    /// <remarks>
    /// The other half of the contract, and the half that keeps the gate from being tightened into
    /// a refusal of values the model does have. Enumerated from the enums themselves so a member
    /// added later is covered without this file being touched.
    /// </remarks>
    [Fact]
    public void Every_Declared_Enum_Member_Is_Accepted()
    {
        var (api, room) = Api();
        int ordinal = 0;

        foreach (var classification in Enum.GetValues<TrackClassification>())
        {
            AcceptedTrack(api, ReportRequest($"contact-c{ordinal++}", classification: classification));
        }

        foreach (var sourceKind in Enum.GetValues<TrackSourceKind>())
        {
            AcceptedTrack(api, ReportRequest($"contact-s{ordinal++}", sourceKind: sourceKind));
        }

        foreach (var transponderKind in Enum.GetValues<TransponderKind>())
        {
            AcceptedTrack(
                api,
                ReportRequest(
                    $"contact-t{ordinal++}",
                    transponder: new TransponderIdentity(transponderKind, "abc-123")));
        }

        foreach (var datum in Enum.GetValues<VerticalReference>())
        {
            AcceptedTrack(
                api,
                ReportRequest($"contact-v{ordinal++}", geo: new GeoPosition(40.0, -74.0, 12.0, datum)));
        }

        room.CaptureTrackFrame().Tracks.Should().HaveCount(ordinal);
    }

    /// <summary>No undeclared enum value survives into the published snapshot.</summary>
    /// <remarks>
    /// The property the field-level cases exist to protect, asserted where it actually matters:
    /// a client narrowing on a discriminator it has never seen either drops the contact or draws
    /// it as whatever its switch falls through to, and both are wrong answers about something a
    /// sensor reported. Every refused body is sent first, so the assertion runs against a session
    /// that was given every chance to hold a bad value.
    /// </remarks>
    [Fact]
    public void No_Undeclared_Enum_Value_Reaches_The_Snapshot()
    {
        var (api, room) = Api();

        TrackReportRequest[] rejected =
        [
            ReportRequest("bad-1", classification: (TrackClassification)9999),
            ReportRequest("bad-2", sourceKind: (TrackSourceKind)(-3)),
            ReportRequest("bad-3", transponder: new TransponderIdentity((TransponderKind)42, "abc-123")),
            ReportRequest("bad-4", geo: new GeoPosition(40.0, -74.0, 12.0, (VerticalReference)77)),
        ];

        foreach (var request in rejected)
        {
            Problem(api.ReportTrack(request), StatusCodes.Status400BadRequest);
        }

        AcceptedTrack(api, ReportRequest("good-1"));

        var snapshot = Body<VizSnapshotV2>(api.GetSnapshot());
        snapshot.Tracks.Should().ContainSingle().Which.TrackId.Should().Be("good-1");

        foreach (var track in snapshot.Tracks)
        {
            AssertEveryEnumIsDeclared(track);
        }

        foreach (var held in room.CaptureTrackFrame().Tracks)
        {
            AssertEveryEnumIsDeclared(held.Track);
        }
    }

    /// <summary>The refusal happens in the pure validator, before anything is touched.</summary>
    /// <remarks>
    /// Asserted directly on <see cref="TrackReport.TryCreate"/> rather than only through the
    /// endpoint, because "a rejection has no side effects" is a property of that function: it
    /// produces no report at all, so there is nothing half-applied for the store to inherit.
    /// </remarks>
    [Fact]
    public void TryCreate_Refuses_An_Undeclared_Enum_And_Produces_No_Report()
    {
        var accepted = TrackReport.TryCreate(
            ReportRequest(classification: (TrackClassification)9999), 30.0, out var report, out var rejection);

        accepted.Should().BeFalse();
        report.Should().BeNull();

        var refusal = rejection.Should().BeOfType<TrackReportRejection>().Which;
        refusal.ReasonCode.Should().Be(TrackProblems.RequestInvalid);
        refusal.Field.Should().Be("classification");
    }

    // ─── The rest of the v2 request surface that binds an enum ───────────────

    /// <summary>A spawn naming an undeclared vehicle class is refused.</summary>
    [Theory]
    [MemberData(nameof(UndeclaredValues))]
    public void An_Undeclared_VehicleClass_Is_Refused(int value)
    {
        var (api, room) = Api();

        var problem = Problem(
            api.SpawnAsset(new AssetSpawnRequest((VehicleClass)value, SpawnPose())),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(AssetProblems.VehicleClassUnsupported);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("vehicleClass");
        room.CaptureAssetFrame().Descriptors.Should().BeEmpty();
    }

    /// <summary>A spawn pose naming an undeclared coordinate frame is refused.</summary>
    [Theory]
    [MemberData(nameof(UndeclaredValues))]
    public void An_Undeclared_Spawn_Frame_Is_Refused(int value)
    {
        var (api, room) = Api();

        var problem = Problem(
            api.SpawnAsset(new AssetSpawnRequest(
                VehicleClass.Multirotor,
                new FramedPose((CoordinateFrame)value, null, new Vector3(10f, 20f, 30f), Quaternion.Identity))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(AssetProblems.PoseFrameUnspecified);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("pose.frame");
        room.CaptureAssetFrame().Descriptors.Should().BeEmpty();
    }

    /// <summary>An inventory filtered by an undeclared domain is refused, not silently empty.</summary>
    /// <remarks>
    /// An empty list would be the dangerous answer: it reads as "this session holds nothing of
    /// that kind" rather than "that is not a kind".
    /// </remarks>
    [Theory]
    [MemberData(nameof(UndeclaredValues))]
    public void An_Undeclared_Domain_Filter_Is_Refused(int value)
    {
        var (api, _) = Api();

        var problem = Problem(api.GetAssets((AssetDomain)value), StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(AssetProblems.RequestInvalid);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("domain");
    }

    /// <summary>A command declaring an undeclared coordinate frame is refused.</summary>
    [Theory]
    [MemberData(nameof(UndeclaredValues))]
    public void An_Undeclared_Command_Frame_Is_Refused(int value)
    {
        var (api, room) = Api();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var problem = Problem(
            api.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.Hold, $"key-frame-{value}", CommandId: Guid.NewGuid(),
                Frame: (CoordinateFrame)value)),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(CommandRejectionReasons.FrameUnspecified);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("frame");
    }

    /// <summary>A command target whose pose names an undeclared frame is refused.</summary>
    [Theory]
    [MemberData(nameof(UndeclaredValues))]
    public void An_Undeclared_Target_Frame_Is_Refused(int value)
    {
        var (api, room) = Api();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var problem = Problem(
            api.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.GoTo, $"key-target-{value}", CommandId: Guid.NewGuid(),
                Target: new PointCommandTarget(new FramedPose(
                    (CoordinateFrame)value, null, new Vector3(10f, 20f, 30f), Quaternion.Identity)))),
            StatusCodes.Status400BadRequest);

        problem.Code.Should().Be(CommandRejectionReasons.FrameUnspecified);
        problem.Errors.Should().ContainSingle().Which.Field.Should().Be("target.point.frame");
    }

    /// <summary>An undeclared datum on a geodetic target never reaches the simulation.</summary>
    /// <remarks>
    /// The one enum on this surface whose refusal is currently a <em>consequence</em> rather than
    /// a check. <c>CommandCatalog</c> tests the datum only against
    /// <see cref="VerticalReference.Unknown"/>, so an undeclared value passes that gate; it is
    /// then stopped by the geodetic resolution itself — an unanchored deployment has no origin to
    /// project against, and an anchored one refuses a datum that does not match the origin's.
    /// This case pins the property that matters, that no such value can be executed, and is the
    /// regression test for anyone who later gives the translator a geodetic resolver: an
    /// <c>Enum.IsDefined</c> check has to land in the catalog's geodetic gate in the same commit.
    /// </remarks>
    [Fact]
    public void An_Undeclared_Datum_On_A_Geodetic_Target_Is_Never_Executed()
    {
        var (api, room) = Api();
        room.AddDrone("uav-1", new Vector3(0f, 50f, 0f));

        var target = new GeoCommandTarget(new GeoPosition(40.7128, -74.0060, 12.0, (VerticalReference)77));

        api.SendCommand("uav-1", new AssetCommandRequest(
                CommandKinds.GoTo, "key-geo-datum", CommandId: Guid.NewGuid(), Target: target))
            .Should().BeAssignableTo<ObjectResult>()
            .Which.StatusCode.Should().NotBe(StatusCodes.Status202Accepted);

        var intent = new CommandIntent(
            Guid.NewGuid(), "uav-1", AssetDomain.Air, CommandKinds.GoTo,
            AssetCapability.Navigate3D, target, CoordinateFrame.LocalEus, null, null);

        AssetCommandTranslator.TryTranslate(intent, out _, out var reasonCode, out _)
            .Should().BeFalse();
        reasonCode.Should().Be(CommandContractReasons.TargetNotResolvable);
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────

    /// <summary>A v2 controller bound to a fresh room whose simulation clock stands at zero.</summary>
    /// <remarks>
    /// The room is stashed where <see cref="RequireRoomAttribute"/> would have put it, which is
    /// the shortcut every other v2 suite uses to stay a unit test. No factories are registered
    /// because nothing here spawns anything but a drone.
    /// </remarks>
    /// <returns>The controller and the room it operates on.</returns>
    private static (SimV2Controller Controller, SimulationRoom Room) Api()
    {
        var room = new SimulationRoom(
            id: "enum-validation-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

        IAssetFactory[] factories = [];
        var controller = new SimV2Controller(
            new VizFrameBuilder(), factories, NullLogger<SimV2Controller>.Instance);

        var http = new DefaultHttpContext();
        http.Items[RequireRoomAttribute.RoomItemKey] = room;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, room);
    }

    /// <summary>A well-formed report, with one field at a time swapped for the case under test.</summary>
    /// <param name="trackId">Contact the report names.</param>
    /// <param name="classification">What the source believes the contact is.</param>
    /// <param name="sourceKind">How the source observes.</param>
    /// <param name="transponder">Cooperative identity, or null for a non-cooperative contact.</param>
    /// <param name="geo">Geodetic echo carried on the pose, or null when the source reported none.</param>
    /// <returns>The request.</returns>
    private static TrackReportRequest ReportRequest(
        string trackId = ContactId,
        TrackClassification classification = TrackClassification.Vessel,
        TrackSourceKind sourceKind = TrackSourceKind.Radar,
        TransponderIdentity? transponder = null,
        GeoPosition? geo = null) =>
        new(
            TrackId: trackId,
            Pose: new FramedPose(
                CoordinateFrame.LocalEus,
                OriginId: null,
                Position: new Vector3(120f, 0f, -80f),
                Orientation: Quaternion.Identity,
                Covariance: null,
                Geo: geo),
            Twist: new FramedTwist(
                CoordinateFrame.LocalEus, new Vector3(0f, 0f, -3f), Vector3.Zero),
            Classification: classification,
            SourceId: "radar-1",
            SourceKind: sourceKind,
            Confidence: 0.9,
            Transponder: transponder);

    /// <summary>A scene-frame spawn pose, so a spawn case fails only on the field under test.</summary>
    /// <returns>The pose.</returns>
    private static FramedPose SpawnPose() =>
        new(CoordinateFrame.LocalEus, null, new Vector3(10f, 20f, 30f), Quaternion.Identity);

    /// <summary>Reports one contact and asserts the endpoint created it.</summary>
    /// <param name="controller">Controller to report through.</param>
    /// <param name="request">Report to send.</param>
    private static void AcceptedTrack(SimV2Controller controller, TrackReportRequest request)
    {
        var created = controller.ReportTrack(request).Should().BeOfType<CreatedResult>(
            "'{0}' names only declared enum members", request.TrackId).Which;

        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.Value.Should().BeOfType<TrackReportResponse>().Which.Created.Should().BeTrue();
    }

    /// <summary>Every enum a published contact carries names a declared member.</summary>
    /// <param name="track">Contact as it appears on the wire.</param>
    private static void AssertEveryEnumIsDeclared(ExternalTrackState track)
    {
        Enum.IsDefined(track.Classification).Should().BeTrue();
        Enum.IsDefined(track.Freshness).Should().BeTrue();
        Enum.IsDefined(track.Pose.Frame).Should().BeTrue();
        Enum.IsDefined(track.Twist.Frame).Should().BeTrue();

        foreach (var source in track.Sources)
        {
            Enum.IsDefined(source.Kind).Should().BeTrue();
        }

        if (track.Pose.Geo is { } geo)
        {
            Enum.IsDefined(geo.VerticalReference).Should().BeTrue();
        }

        if (track.Transponder is { } transponder)
        {
            Enum.IsDefined(transponder.Kind).Should().BeTrue();
        }
    }

    /// <summary>Unwraps a problem response and checks the status it was answered with.</summary>
    /// <param name="result">Response returned by the endpoint.</param>
    /// <param name="expectedStatus">Status the refusal must carry.</param>
    /// <returns>The problem body.</returns>
    private static CommandProblemDetails Problem(IActionResult result, int expectedStatus)
    {
        var objectResult = result.Should().BeAssignableTo<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(expectedStatus);
        return objectResult.Value.Should().BeOfType<CommandProblemDetails>().Which;
    }

    /// <summary>Unwraps a 200 response body.</summary>
    /// <typeparam name="T">Expected body type.</typeparam>
    /// <param name="result">Response returned by the endpoint.</param>
    /// <returns>The body.</returns>
    private static T Body<T>(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<T>().Which;
}
