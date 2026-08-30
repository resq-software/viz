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
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>v1 command types map onto v2 kinds, and v1 detections onto their v2 shape.</summary>
/// <remarks>
/// The adapter is asserted in both directions so a v1 caller keeps its exact behaviour while the
/// command actually executed is the v2 one.
/// </remarks>
public partial class V1CompatibilityTests
{
    // ─── v1 command types map onto v2 kinds ─────────────────────────────────

    /// <summary>
    /// Each v1 token maps to the domain-neutral kind it was always shorthand for, and matching
    /// stays case-insensitive because the v1 endpoint has always lower-cased before comparing.
    /// </summary>
    [Theory]
    [InlineData("hover", CommandKinds.Hold)]
    [InlineData("goto", CommandKinds.GoTo)]
    [InlineData("rtl", CommandKinds.ReturnToBase)]
    [InlineData("land", CommandKinds.Land)]
    [InlineData("auto", CommandKinds.ResumeAutonomy)]
    [InlineData("HOVER", CommandKinds.Hold)]
    [InlineData("GoTo", CommandKinds.GoTo)]
    [InlineData("Rtl", CommandKinds.ReturnToBase)]
    [InlineData("LAND", CommandKinds.Land)]
    [InlineData("Auto", CommandKinds.ResumeAutonomy)]
    public void Each_V1_Command_Type_Maps_To_Its_Intended_V2_Kind(string v1Type, string expectedKind)
    {
        AssetProjection.TryToCommandKind(v1Type, out var kind).Should().BeTrue();
        kind.Should().Be(expectedKind);
    }

    /// <summary>
    /// Anything that was not a v1 type stays unrecognised — a v2 token included, since the v1
    /// endpoint has never accepted one and must not start accepting one by accident.
    /// </summary>
    [Theory]
    [InlineData("explode")]
    [InlineData("")]
    [InlineData(CommandKinds.Hold)]
    [InlineData(CommandKinds.Takeoff)]
    public void A_Non_V1_Command_Type_Maps_To_Nothing(string v1Type)
    {
        AssetProjection.TryToCommandKind(v1Type, out var kind).Should().BeFalse();
        kind.Should().BeNull();
    }

    /// <summary>A missing command type is unrecognised rather than a null-reference crash.</summary>
    [Fact]
    public void A_Null_V1_Command_Type_Maps_To_Nothing()
    {
        AssetProjection.TryToCommandKind(null, out var kind).Should().BeFalse();
        kind.Should().BeNull();
    }

    /// <summary>
    /// Every v1 command still clears the v2 gate for a multirotor: the catalog knows the kind, it
    /// applies to the air domain, and the multirotor profile declares what it requires. Tighten a
    /// capability without checking this and a v1 client's commands start being rejected.
    /// </summary>
    [Theory]
    [InlineData("hover")]
    [InlineData("goto")]
    [InlineData("rtl")]
    [InlineData("land")]
    [InlineData("auto")]
    public void Every_V1_Command_Still_Passes_The_V2_Gate_For_A_Multirotor(string v1Type)
    {
        AssetProjection.TryToCommandKind(v1Type, out var kind).Should().BeTrue();
        CommandCatalog.TryGet(kind, out var definition).Should().BeTrue();

        definition!.AppliesTo(AssetDomain.Air).Should().BeTrue();
        definition.IsSatisfiedBy(AssetProfiles.CapabilitiesFor(VehicleClass.Multirotor)).Should().BeTrue();
        AssetCommandTranslator.ToAssetCommandKind(kind).Should().NotBe(AssetCommandKind.Unspecified);
    }

    /// <summary>A v1 spawn is an air multirotor at the same scene position, with its model kept.</summary>
    [Fact]
    public void A_V1_Spawn_Becomes_An_Air_Multirotor_At_The_Same_Scene_Position()
    {
        var request = AssetProjection.ToAssetSpawnRequest(
            new SpawnDroneRequest([10f, 50f, 20f]), assetId: "drone-1");

        request.VehicleClass.Should().Be(VehicleClass.Multirotor);
        AssetProfiles.DomainFor(request.VehicleClass).Should().Be(AssetDomain.Air);
        request.Pose.Frame.Should().Be(CoordinateFrame.LocalEus);
        request.Pose.Position.Should().Be(new Vector3(10f, 50f, 20f));
        request.Pose.Orientation.Should().Be(Quaternion.Identity);
        request.Model.Should().Be("quadrotor");
    }

    /// <summary>A v1 goto target keeps its numbers and gains the frame it always implied.</summary>
    [Fact]
    public void A_V1_Goto_Target_Keeps_Its_Numbers_And_Gains_The_Scene_Frame()
    {
        var target = AssetProjection.ToCommandTarget([100f, 50f, 100f]);

        target!.Point.Frame.Should().Be(CoordinateFrame.LocalEus);
        target.Point.Position.Should().Be(new Vector3(100f, 50f, 100f));
        AssetProjection.ToCommandTarget(null).Should().BeNull();
    }

    // ─── Detections ─────────────────────────────────────────────────────────

    /// <summary>A projected detection carries its reporting asset in v1's only such field.</summary>
    [Fact]
    public void A_Detection_Projected_To_V1_Carries_Its_Reporter_As_DroneId()
    {
        var detection = Detection("survivor-1", "drone-1", new Vector3(12f, 0f, -4f), confidence: 0.75);

        var v1 = AssetProjection.ToDetectionVizState(detection);

        v1.Id.Should().Be("survivor-1");
        v1.Type.Should().Be("survivor");
        v1.Pos.Should().Equal(12f, 0f, -4f);
        v1.DroneId.Should().Be("drone-1");
        v1.Confidence.Should().Be(0.75);
    }

    /// <summary>
    /// A detection reported by a rover still populates <c>DroneId</c> — the honest limit of the
    /// v1 shape. Dropping the sighting would lose a casualty, and a v1 client only uses the field
    /// to draw a line back to a reporter it will simply fail to resolve.
    /// </summary>
    [Fact]
    public void A_Detection_From_A_Non_Air_Reporter_Still_Populates_DroneId()
    {
        var detection = Detection("survivor-2", "rover-1", Vector3.Zero, confidence: 0.5);

        AssetProjection.ToDetectionVizState(detection).DroneId.Should().Be("rover-1");
    }

    /// <summary>The list projection keeps order and reporters, which the client's log depends on.</summary>
    [Fact]
    public void Projected_Detections_Keep_Their_Order_And_Their_Reporters()
    {
        IReadOnlyList<DetectionV2State> detections =
        [
            Detection("survivor-1", "drone-1", new Vector3(1f, 0f, 1f), confidence: 0.9),
            Detection("survivor-2", "drone-2", new Vector3(2f, 0f, 2f), confidence: 0.4),
        ];

        var projected = AssetProjection.ToDetectionVizStates(detections);

        projected.Select(d => d.Id).Should().Equal("survivor-1", "survivor-2");
        projected.Select(d => d.DroneId).Should().Equal("drone-1", "drone-2");
    }
}
