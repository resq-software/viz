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

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Scenario discovery and start contracts on the v2 simulation surface.</summary>
public partial class SimV2ControllerTests
{
    /// <summary>The catalog carries all domain keys, including zero-count domains.</summary>
    [Fact]
    public void Catalog_Uses_Stable_Lowercase_Domain_Keys_Including_Zeroes()
    {
        var scenarios = new ScenarioService(ScenarioConfiguration());
        var (ctrl, _) = CreateController(scenarios: scenarios);

        var catalog = Body<ScenarioCatalogResponse>(ctrl.GetScenarioCatalog());

        var flood = catalog.Scenarios.Single(s => s.Name == "flood-response");
        flood.AssetCount.Should().Be(8);
        flood.DomainCounts.Should().Be(new ScenarioDomainCounts(Air: 3, Ground: 3, Surface: 2));

        var single = catalog.Scenarios.Single(s => s.Name == "single");
        single.DomainCounts.Should().Be(new ScenarioDomainCounts(Air: 1, Ground: 0, Surface: 0));

        var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var scenariosElement = document.RootElement.GetProperty("scenarios");
        var singleElement = scenariosElement.EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "single");
        var counts = singleElement.GetProperty("domainCounts");

        counts.EnumerateObject().Select(p => p.Name).Should().Equal("air", "ground", "surface");
        counts.GetProperty("air").GetInt32().Should().Be(1);
        counts.GetProperty("ground").GetInt32().Should().Be(0);
        counts.GetProperty("surface").GetInt32().Should().Be(0);
    }

    /// <summary>An unknown name is a typed not-found response and does not reset the room.</summary>
    [Fact]
    public void Unknown_Scenario_Returns_Typed_NotFound_Problem()
    {
        var scenarios = new ScenarioService(ScenarioConfiguration());
        var (ctrl, room) = CreateController(scenarios: scenarios);
        room.AddDrone("old-air", new System.Numerics.Vector3(0f, 10f, 0f));

        var problem = Problem(ctrl.StartScenario("not-a-scenario"), StatusCodes.Status404NotFound);

        problem.Code.Should().Be(ScenarioProblems.NotFound);
        room.CaptureAssetFrame().Descriptors.Select(d => d.AssetId).Should().ContainSingle("old-air");
    }

    /// <summary>Starting resolves route casing, replaces the world, and publishes the canonical name.</summary>
    [Fact]
    public void Start_Uses_Canonical_Name_And_Replaces_The_Previous_World()
    {
        var scenarios = new ScenarioService(ScenarioConfiguration());
        var (ctrl, room) = CreateController(scenarios: scenarios);
        room.AddDrone("old-air", new System.Numerics.Vector3(0f, 10f, 0f));

        var response = Body<ScenarioStartResponse>(ctrl.StartScenario("FLOOD-RESPONSE"));
        var capture = room.CaptureAssetFrame();

        response.Current.Name.Should().Be("flood-response");
        capture.Scenario.Should().Be(response.Current);
        capture.Descriptors.Should().HaveCount(8);
        capture.Descriptors.Select(d => d.AssetId).Should().NotContain("old-air");
    }

    /// <summary>Scenario replacement is destructive and carries the destructive limiter.</summary>
    [Fact]
    public void StartScenario_Uses_The_Destructive_Rate_Limit()
    {
        typeof(SimV2Controller).GetMethod(nameof(SimV2Controller.StartScenario))!
            .GetCustomAttribute<EnableRateLimitingAttribute>()!
            .PolicyName.Should().Be("destructive");
    }

    /// <summary>Directly constructed controllers keep non-scenario endpoints usable.</summary>
    [Fact]
    public void Scenario_Actions_Without_A_Catalog_Return_Typed_NotImplemented_Problems()
    {
        var (ctrl, _) = CreateController();

        Problem(ctrl.GetScenarioCatalog(), StatusCodes.Status501NotImplemented)
            .Code.Should().Be(ScenarioProblems.CatalogUnavailable);
        Problem(ctrl.StartScenario("single"), StatusCodes.Status501NotImplemented)
            .Code.Should().Be(ScenarioProblems.CatalogUnavailable);

        Body<AssetInventoryResponse>(ctrl.GetAssets()).Assets.Should().BeEmpty();
    }
}
