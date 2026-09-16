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

using Microsoft.Extensions.Logging.Abstractions;

using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;

using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>A scenario that raises the water must raise it for the simulation, not just the view.</summary>
/// <remarks>
/// The client carried its own per-scenario water level and the server never heard about it.
/// `hurricane-melissa` drew water at 6 m over a server that thought 3 m, and `flood-riverine` drew
/// it at 18 m over a server that thought −3 m — so a vessel spawned in either sat 3 m or 21 m below
/// the surface it appeared to be floating on.
/// <para>
/// <see cref="AssetWorld.SetSeaLevel"/> already existed for exactly this, documented "for a
/// scenario override", with no production caller at all.
/// </para>
/// </remarks>
public sealed class ScenarioSeaLevelTests
{
    [Theory]
    [InlineData("hurricane-melissa", "coastal", 6.0)]
    [InlineData("flood-riverine", "alpine", 18.0)]
    public void A_Scenario_That_Raises_The_Water_Reports_The_Raised_Level(
        string scenario, string preset, double expected)
    {
        SeaLevel.ForScenario(scenario, preset).Should().Be(
            expected, "the simulation must use the level the operator is shown");
    }

    [Theory]
    [InlineData("wildfire-interface", "ridgeline", -15.0)]
    [InlineData("urban-collapse", "canyon", -60.0)]
    [InlineData("alpine-sar", "alpine", -3.0)]
    [InlineData("canyon-sar", "canyon", -60.0)]
    public void A_Scenario_That_Does_Not_Raise_The_Water_Follows_Its_Preset(
        string scenario, string preset, double expected)
    {
        // These four restated their preset's level in the client's own table. Restating a
        // constant is how the two tables drift, so the server derives them rather than listing
        // them — and this pins that deriving gives the same answer.
        SeaLevel.ForScenario(scenario, preset).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-scenario")]
    public void An_Unknown_Scenario_Falls_Back_To_The_Preset(string? scenario)
    {
        SeaLevel.ForScenario(scenario, "coastal").Should().Be(
            SeaLevel.CoastalM, "an unrecognised scenario must not move the water");
    }

    [Fact]
    public void An_Override_Beats_The_Preset_It_Runs_On()
    {
        // The control. Returning the preset unconditionally passes every test above except the
        // first pair, and those are the only two scenarios where this matters at all.
        SeaLevel.ForScenario("flood-riverine", "alpine").Should().NotBe(SeaLevel.AlpineM);
        SeaLevel.ForPreset("alpine").Should().Be(SeaLevel.AlpineM);
    }

    // ---- through a real room, which is what the client actually reads ----

    private static SimulationRoom CreateRoom() =>
        new(id: "sea-level-room", ipBucket: "127.0.0.0/24", logger: NullLogger.Instance);

    /// <summary>A scenario's water reaches the frame the client draws from.</summary>
    /// <remarks>
    /// The static lookup above proves the number is right. This proves something reads it: the
    /// mechanism it calls — <see cref="AssetWorld.SetSeaLevel"/> — existed all along, documented
    /// "for a scenario override", and had no production caller at all.
    /// </remarks>
    [Fact]
    public void Starting_A_Flood_Scenario_Raises_The_Water_On_The_Frame()
    {
        var room = CreateRoom();
        room.StepOnce();

        var before = room.CaptureAssetFrame().SeaLevelM;
        before.Should().Be(SeaLevel.AlpineM, "the default room is alpine");

        room.NotifyScenario("flood-riverine");

        room.CaptureAssetFrame().SeaLevelM.Should().Be(
            18.0, "the client draws its water plane from this, and the vessel floats on it");
    }

    /// <summary>A preset switch under a running scenario keeps the scenario's water.</summary>
    /// <remarks>
    /// The preset path used to set the level straight from the preset, so switching terrain while
    /// a flood scenario ran would have drained it.
    /// </remarks>
    [Fact]
    public void Switching_Preset_Under_A_Flood_Scenario_Keeps_The_Flood()
    {
        var room = CreateRoom();
        room.NotifyScenario("flood-riverine");

        room.SetTerrainPreset("coastal");

        room.CaptureAssetFrame().SeaLevelM.Should().Be(
            18.0, "the scenario's water outranks the preset it is drawn on");
    }

    /// <summary>A plain preset switch, with no scenario, follows the preset.</summary>
    [Fact]
    public void Switching_Preset_With_No_Scenario_Follows_The_Preset()
    {
        // The control. Returning the scenario level unconditionally would pass the test above
        // while freezing the water for every operator who never starts a scenario.
        var room = CreateRoom();

        room.SetTerrainPreset("coastal");

        room.CaptureAssetFrame().SeaLevelM.Should().Be(SeaLevel.CoastalM);
    }
}
