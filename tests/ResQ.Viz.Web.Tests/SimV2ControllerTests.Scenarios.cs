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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ResQ.Viz.Web.Controllers;
using ResQ.Viz.Web.Models;
using ResQ.Viz.Web.Services;
using ResQ.Viz.Web.Services.Assets;
using Xunit;

namespace ResQ.Viz.Web.Tests;

/// <summary>Scenario discovery and start contracts on the v2 simulation surface.</summary>
[Collection(ScenarioTelemetryCollection.Name)]
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

    /// <summary>Two replacements serialize their commits and return the state each one committed.</summary>
    [Fact]
    public async Task Concurrent_Starts_Cannot_Interleave_And_Return_Their_Own_Committed_State()
    {
        var scenarios = ScenarioServiceFor(
            ("alpha", "alpha-air", VehicleClass.Multirotor),
            ("bravo", "bravo-air-1", VehicleClass.Multirotor),
            ("bravo", "bravo-air-2", VehicleClass.Multirotor));
        var (ctrl, room) = CreateController(scenarios: scenarios);
        var barrier = new BlockingFirstWorldResetObserver();
        room.AddLifecycleObserver(barrier);
        var authority = new ControlAuthorityRegistry(
            TimeProvider.System, new ControlAuthorityOptions()).For(room);

        var alphaTask = Task.Run(() => Body<ScenarioStartResponse>(ctrl.StartScenario("alpha")));
        barrier.WaitForFirstReset();

        var bravoTask = Task.Run(() => Body<ScenarioStartResponse>(ctrl.StartScenario("bravo")));
        barrier.WaitForSecondReset();
        var bravo = await bravoTask.WaitAsync(TimeSpan.FromSeconds(5));
        authority.Acquire(
            "bravo-air-1", "new-console", ControlRole.Operator, TimeSpan.FromMinutes(1))
            .IsAccepted.Should().BeTrue();

        barrier.ReleaseFirstReset();
        var alpha = await alphaTask.WaitAsync(TimeSpan.FromSeconds(5));
        var final = room.CaptureAssetFrame();

        alpha.Current.Name.Should().Be("alpha");
        bravo.Current.Name.Should().Be("bravo");
        alpha.Current.Revision.Should().BeLessThan(bravo.Current.Revision);
        final.Scenario.Should().Be(bravo.Current);
        final.Descriptors.Select(d => d.AssetId).Should().Equal("bravo-air-1", "bravo-air-2");
        authority.FindLiveLease("bravo-air-1")!.HolderId.Should().Be("new-console",
            "an older replacement notification must not revoke authority acquired after the newer commit");
    }

    /// <summary>A later direct reset cannot rewrite the state returned by an already committed start.</summary>
    [Fact]
    public async Task Response_State_Matches_The_Committed_Transaction_When_A_Reset_Wins_After_Commit()
    {
        var scenarios = ScenarioServiceFor(("alpha", "alpha-air", VehicleClass.Multirotor));
        var (ctrl, room) = CreateController(scenarios: scenarios);
        var barrier = new BlockingFirstWorldResetObserver();
        room.AddLifecycleObserver(barrier);

        var startTask = Task.Run(() => Body<ScenarioStartResponse>(ctrl.StartScenario("alpha")));
        barrier.WaitForFirstReset();

        room.Reset();
        barrier.WaitForSecondReset();
        barrier.ReleaseFirstReset();

        var response = await startTask.WaitAsync(TimeSpan.FromSeconds(5));
        var final = room.CaptureAssetFrame();

        response.Current.Name.Should().Be("alpha");
        final.Scenario.Should().BeNull();
        final.Descriptors.Should().BeEmpty();
    }

    /// <summary>A staging exception preserves the old world and returns a stable typed problem.</summary>
    [Fact]
    public void Throwing_Factory_Preserves_Previous_World_And_Returns_Typed_Failure()
    {
        var factory = new ThrowingScenarioFactory();
        var scenarios = ScenarioServiceFor(
            [factory],
            ("previous", "old-air", VehicleClass.Multirotor),
            ("broken", "broken-ground", VehicleClass.AckermannRover));
        var logger = new RecordingLogger<SimV2Controller>();
        var (ctrl, room) = CreateController(scenarios: scenarios, logger: logger);
        var previous = Body<ScenarioStartResponse>(ctrl.StartScenario("previous"));
        var before = room.CaptureAssetFrame();
        logger.Messages.Clear();

        using var telemetry = new ScenarioTelemetryProbe();
        var problem = Problem(ctrl.StartScenario("broken"), StatusCodes.Status503ServiceUnavailable);
        var after = room.CaptureAssetFrame();

        problem.Code.Should().Be(ScenarioProblems.ReplacementFailed);
        problem.Detail.Should().NotContain(ThrowingScenarioFactory.Secret);
        after.Scenario.Should().Be(previous.Current).And.Be(before.Scenario);
        after.Descriptors.Should().Equal(before.Descriptors);
        after.Assets.Select(a => a.AssetId).Should().Equal(before.Assets.Select(a => a.AssetId));
        after.Assets.Select(a => a.Pose).Should().Equal(before.Assets.Select(a => a.Pose));
        telemetry.ScenarioRuns.Should().Be(0);
        telemetry.ScenarioFailures.Should().Be(1);
        telemetry.Durations.Should().ContainSingle(sample => sample.Status == "failure");
        telemetry.CompletedActivities.Should().ContainSingle().Which.Should().Match<Activity>(activity =>
            activity.Status == ActivityStatusCode.Error
            && Equals(activity.GetTagItem("scenario.name"), "broken")
            && Equals(activity.GetTagItem("error.type"), "population.stage"));
        logger.Messages.Should().ContainSingle(message =>
            message.Contains("population.stage", StringComparison.Ordinal));
    }

    /// <summary>A successful v2 start emits the same activity, metric and structured log as v1.</summary>
    [Fact]
    public void Successful_Start_Emits_Scenario_Observability_Exactly_Once()
    {
        var scenarios = ScenarioServiceFor(("single", "air-1", VehicleClass.Multirotor));
        var logger = new RecordingLogger<SimV2Controller>();
        var (ctrl, room) = CreateController(scenarios: scenarios, logger: logger);
        using var telemetry = new ScenarioTelemetryProbe();

        var response = Body<ScenarioStartResponse>(ctrl.StartScenario("SINGLE"));

        response.Current.Name.Should().Be("single");
        telemetry.ScenarioRuns.Should().Be(1);
        telemetry.ScenarioFailures.Should().Be(0);
        telemetry.Durations.Should().ContainSingle(sample => sample.Status == "success");
        telemetry.CompletedActivities.Should().ContainSingle()
            .Which.GetTagItem("scenario.name").Should().Be("single");
        logger.Messages.Should().ContainSingle()
            .Which.Should().Be($"Scenario 'single' started in room {room.Id}.");
    }

    /// <summary>An observer cannot turn an already committed replacement into an ambiguous failure.</summary>
    [Fact]
    public void Throwing_World_Reset_Observer_Does_Not_Escape_After_Commit()
    {
        var scenarios = ScenarioServiceFor(("single", "air-1", VehicleClass.Multirotor));
        var (ctrl, room) = CreateController(scenarios: scenarios);
        room.AddLifecycleObserver(new ThrowingWorldResetObserver());

        var response = Body<ScenarioStartResponse>(ctrl.StartScenario("single"));

        response.Current.Name.Should().Be("single");
        room.CaptureAssetFrame().Scenario.Should().Be(response.Current);
        room.CaptureAssetFrame().Descriptors.Select(d => d.AssetId).Should().ContainSingle("air-1");
    }

    /// <summary>An authority first created after a newer commit ignores a delayed older callback.</summary>
    [Fact]
    public void Late_Authority_Subscription_Is_Baselined_Against_Stale_Reset_Notifications()
    {
        var room = CreateRoom();
        room.Reset();
        room.Reset();
        room.AddDrone("air-1", new System.Numerics.Vector3(0f, 15f, 0f));
        var authority = new ControlAuthorityRegistry(
            TimeProvider.System, new ControlAuthorityOptions()).For(room);
        authority.Acquire("air-1", "new-console", ControlRole.Operator, TimeSpan.FromMinutes(1))
            .IsAccepted.Should().BeTrue();

        typeof(SimulationRoom)
            .GetMethod("NotifyWorldReset", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(room, [1L]);

        authority.FindLiveLease("air-1")!.HolderId.Should().Be("new-console");
    }

    /// <summary>A lease acquired after commit survives that same world's delayed reset callback.</summary>
    [Fact]
    public async Task Same_Revision_Callback_Preserves_A_Lease_Acquired_After_World_Commit()
    {
        var scenarios = ScenarioServiceFor(
            ("single", "new-air", VehicleClass.Multirotor),
            ("single", "reused-air", VehicleClass.Multirotor));
        var (ctrl, room) = CreateController(scenarios: scenarios);
        room.AddDrone("missing-air", new System.Numerics.Vector3(0f, 15f, 0f));
        room.AddDrone("reused-air", new System.Numerics.Vector3(10f, 15f, 0f));
        var barrier = new BlockingFirstWorldResetObserver();
        room.AddLifecycleObserver(barrier);
        var authority = new ControlAuthorityRegistry(
            TimeProvider.System, new ControlAuthorityOptions()).For(room);
        var missing = authority.Acquire(
            "missing-air", "old-console-a", ControlRole.Operator, TimeSpan.FromMinutes(1)).Lease!;
        var reused = authority.Acquire(
            "reused-air", "old-console-b", ControlRole.Operator, TimeSpan.FromMinutes(1)).Lease!;

        var startTask = Task.Run(() => Body<ScenarioStartResponse>(ctrl.StartScenario("single")));
        barrier.WaitForFirstReset();
        var lease = authority.Acquire(
            "new-air", "new-console", ControlRole.Operator, TimeSpan.FromMinutes(1)).Lease!;

        barrier.ReleaseFirstReset();
        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        authority.FindLiveLease("new-air").Should().Be(lease);
        authority.ReadAudit().Should().NotContain(record =>
            record.LeaseId == lease.LeaseId && record.EndReason != null);
        var oldEndings = authority.ReadAudit().Where(record =>
            record.EndReason != null
            && (record.LeaseId == missing.LeaseId || record.LeaseId == reused.LeaseId)).ToList();
        oldEndings.Should().HaveCount(2);
        oldEndings.Should().OnlyContain(record =>
            record.Kind == ControlAuditKind.Revoked
            && record.EndReason == ControlLeaseEndReason.AuthorityReset);
    }

    /// <summary>A world replacement selectively revokes old instances with reset audit reasons.</summary>
    [Fact]
    public void World_Replacement_Revokes_Missing_And_Reused_Instances_As_Authority_Reset()
    {
        var scenarios = ScenarioServiceFor(("replacement", "reused-air", VehicleClass.Multirotor));
        var (ctrl, room) = CreateController(scenarios: scenarios);
        room.AddDrone("missing-air", new System.Numerics.Vector3(0f, 15f, 0f));
        room.AddDrone("reused-air", new System.Numerics.Vector3(10f, 15f, 0f));
        var authority = new ControlAuthorityRegistry(
            TimeProvider.System, new ControlAuthorityOptions()).For(room);
        var missing = authority.Acquire(
            "missing-air", "console-a", ControlRole.Operator, TimeSpan.FromMinutes(1)).Lease!;
        var reused = authority.Acquire(
            "reused-air", "console-b", ControlRole.Operator, TimeSpan.FromMinutes(1)).Lease!;

        Body<ScenarioStartResponse>(ctrl.StartScenario("replacement"));

        authority.LiveLeases().Should().BeEmpty();
        var endings = authority.ReadAudit().Where(record =>
            record.EndReason != null
            && (record.LeaseId == missing.LeaseId || record.LeaseId == reused.LeaseId)).ToList();
        endings.Should().HaveCount(2);
        endings.Should().OnlyContain(record =>
            record.Kind == ControlAuditKind.Revoked
            && record.EndReason == ControlLeaseEndReason.AuthorityReset);
    }

    private static ScenarioService ScenarioServiceFor(
        params (string Scenario, string AssetId, VehicleClass VehicleClass)[] rows) =>
        ScenarioServiceFor([], rows);

    private static ScenarioService ScenarioServiceFor(
        IReadOnlyList<IAssetFactory> factories,
        params (string Scenario, string AssetId, VehicleClass VehicleClass)[] rows)
    {
        var settings = new Dictionary<string, string?>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var index = indexes.GetValueOrDefault(row.Scenario);
            indexes[row.Scenario] = index + 1;
            var prefix = $"Scenarios:{row.Scenario}:{index}";
            settings[$"{prefix}:id"] = row.AssetId;
            settings[$"{prefix}:pos:0"] = "0";
            settings[$"{prefix}:pos:1"] = row.VehicleClass == VehicleClass.Multirotor ? "15" : "0";
            settings[$"{prefix}:pos:2"] = "0";
            if (row.VehicleClass != VehicleClass.Multirotor)
            {
                settings[$"{prefix}:class"] = row.VehicleClass.ToString();
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ScenarioService(configuration, factories);
    }

    /// <summary>Test-only barrier at the world-reset notification, which runs outside the room lock.</summary>
    private sealed class BlockingFirstWorldResetObserver : IRoomLifecycleObserver
    {
        private readonly ManualResetEventSlim _firstReset = new(false);
        private readonly ManualResetEventSlim _secondReset = new(false);
        private readonly ManualResetEventSlim _releaseFirst = new(false);
        private int _resetCount;

        public void InitializeWorldRevision(long revision) { }

        public void OnAssetRemoved(string assetId) { }

        public void OnWorldReset(long revision)
        {
            var ordinal = Interlocked.Increment(ref _resetCount);
            if (ordinal == 1)
            {
                _firstReset.Set();
                if (!_releaseFirst.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The first reset was never released by the test.");
                }
            }
            else if (ordinal == 2)
            {
                _secondReset.Set();
            }
        }

        public void OnUpkeep() { }

        public void WaitForFirstReset() => Wait(_firstReset, "first reset");

        public void WaitForSecondReset() => Wait(_secondReset, "second reset");

        public void ReleaseFirstReset() => _releaseFirst.Set();

        private static void Wait(ManualResetEventSlim signal, string description)
        {
            if (!signal.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }
        }
    }

    private sealed class ThrowingScenarioFactory : IAssetFactory
    {
        public const string Secret = "sensitive factory detail";

        public bool CanCreate(VehicleClass vehicleClass) =>
            vehicleClass == VehicleClass.AckermannRover;

        public ISimulatedAsset Create(in AssetSpawnPlan plan) =>
            throw new InvalidOperationException(Secret);
    }

    private sealed class ThrowingWorldResetObserver : IRoomLifecycleObserver
    {
        public void InitializeWorldRevision(long revision) { }

        public void OnAssetRemoved(string assetId) { }

        public void OnWorldReset(long revision) => throw new InvalidOperationException("observer failure");

        public void OnUpkeep() { }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class ScenarioTelemetryProbe : IDisposable
    {
        private readonly MeterListener _meter = new();
        private readonly ActivityListener _activity = new();
        private long _scenarioRuns;
        private long _scenarioFailures;

        public ScenarioTelemetryProbe()
        {
            _meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name is "resq.viz.scenarios_run"
                    or "resq.viz.scenario_run_failures"
                    or "resq.viz.scenario_run_duration")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meter.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                if (instrument.Name == "resq.viz.scenarios_run")
                {
                    Interlocked.Add(ref _scenarioRuns, measurement);
                }
                else
                {
                    Interlocked.Add(ref _scenarioFailures, measurement);
                }
            });
            _meter.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
            {
                var status = tags.ToArray().FirstOrDefault(tag => tag.Key == "status").Value?.ToString();
                Durations.Enqueue((measurement, status));
            });
            _meter.Start();

            _activity.ShouldListenTo = source => source.Name == VizTelemetry.ServiceName;
            _activity.Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded;
            _activity.SampleUsingParentId = (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded;
            _activity.ActivityStopped = activity => CompletedActivities.Enqueue(activity);
            ActivitySource.AddActivityListener(_activity);
        }

        public long ScenarioRuns => Interlocked.Read(ref _scenarioRuns);

        public long ScenarioFailures => Interlocked.Read(ref _scenarioFailures);

        public ConcurrentQueue<(double Milliseconds, string? Status)> Durations { get; } = new();

        public ConcurrentQueue<Activity> CompletedActivities { get; } = new();

        public void Dispose()
        {
            _activity.Dispose();
            _meter.Dispose();
        }
    }
}

/// <summary>Serializes tests that observe process-wide scenario telemetry instruments.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScenarioTelemetryCollection
{
    public const string Name = "scenario telemetry";
}
