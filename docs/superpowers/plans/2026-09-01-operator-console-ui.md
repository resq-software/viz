# Mixed-domain operator console implementation plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the default drone-only browser shell with a v2 mixed-domain operator console that exposes scenarios, fleet controls, command authority, safety tools, and Editor access while retaining automatic v1 fallback.

**Architecture:** Keep `app.ts` authoritative for connection, frame, projection, and selection state. Add streamed scenario state and discovery routes to the host, then build a small `OperatorShell` with explicit mounts around the existing v2 render pipeline. Lazy operator modules consume shared selection, authority, and live/replay stores instead of creating parallel state.

**Tech Stack:** .NET 10, ASP.NET Core, SignalR, TypeScript 7, Vite 8, Three.js 0.185, Vitest 4, xUnit, Playwright Chromium, HTML, and CSS.

**Required skills:** @superpowers:test-driven-development for every behavior change, @ui-ux-pro-max for operator-shell styling, @superpowers:verification-before-completion before any completion claim, @superpowers:requesting-code-review before branch integration, and @superpowers:finishing-a-development-branch after all gates pass.

**Approved spec:** `docs/superpowers/specs/2026-09-01-operator-console-ui-design.md`

---

## File structure

### Host and wire contracts

- Create `src/ResQ.Viz.Web/Models/ScenarioApi.cs` for scenario session state, catalog DTOs, and stable scenario problem codes.
- Create `src/ResQ.Viz.Web/Models/AssetProfileApi.cs` for spawn-profile discovery DTOs.
- Create `src/ResQ.Viz.Web/Services/SimulationRoom.Scenario.cs` for locked scenario state and revision ownership.
- Modify `src/ResQ.Viz.Web/Services/SimulationRoom.Environment.cs` to move `NotifyScenario` into the scenario partial.
- Modify `src/ResQ.Viz.Web/Services/SimulationRoom.cs` to clear current scenario during reset without resetting its revision counter.
- Modify `src/ResQ.Viz.Web/Services/SimulationRoom.Assets.cs` to include scenario state in the atomic room capture.
- Modify `src/ResQ.Viz.Web/Models/VizFrameV2.cs`, `VizFrameDeltaV2.cs`, `Services/VizSnapshotV2Builder.cs`, and `Services/VizSnapshotDiffer.cs` for full, replacement, and clear semantics.
- Create `src/ResQ.Viz.Web/Controllers/SimV2Controller.Scenarios.cs` for v2 catalog and start routes.
- Create `src/ResQ.Viz.Web/Controllers/SimV2Controller.AssetProfiles.cs` for deployment-derived spawn discovery.
- Modify `src/ResQ.Viz.Web/Controllers/SimV2Controller.cs` only for injected services shared by the new partial controllers.

### Client operator shell

- Create `src/ResQ.Viz.Web/client/operator/OperatorShell.ts` for mode, mount, rail, responsive, and Editor visibility ownership.
- Create `operator/StartupCoordinator.ts` for boot, v2, legacy, and one-time default transitions.
- Create `operator/ScenarioRuntime.ts`, `scenarioPresentation.ts`, and `ConsoleResources.ts` for authoritative mission state and independent resources.
- Create `operator/MissionPanel.ts`, `ScenarioCatalog.ts`, `SpawnAssetDialog.ts`, and `EnvironmentDialog.ts` for mission and setup actions.
- Create `operator/AssetRoster.ts` for keyed asset/contact rows and roster-only search.
- Create `operator/controlAuthorityStore.ts` and `interactionMode.ts` for authority and live/replay state.
- Create `operator/advancedSafety.ts` for authority, link, track, and audit composition.
- Create `client/editor/workspace.ts` for the single Editor visibility/focus owner.
- Create `client/styles/operator.css`, `operator-dialogs.css`, and `advancedSafety.css` for the new surfaces.

### Existing client integration

- Modify `client/index.html` to add boot/v2/legacy branches and explicit mounts.
- Modify `client/app.ts` only as the integration root. Keep DOM construction and form logic in focused modules.
- Modify `client/api.ts` for typed v2 problem decoding and JSON mutation responses.
- Modify `client/assets/types.ts`, `deltaApply.ts`, and `sceneFrame.ts` for streamed scenario state.
- Modify `client/assets/fleetUi.ts`, `AssetFilter.ts`, `AssetPanel.ts`, and `panelCommands.ts` for explicit mounts, roster composition, and authority-aware commands.
- Modify `client/ui/hud.ts` for total and per-domain v2 counts while retaining the v1 drone path.
- Modify `client/controls.ts` so the legacy adapter is root-scoped and no longer captures Tab.
- Modify `client/editor/recorder.ts`, `dvr.ts`, `dock.ts`, `gizmo.ts`, `sceneConfig.ts`, and `transport.ts` for mixed-domain replay, mutation gating, and Editor ownership.
- Modify `client/styles/tokens.css`, `main.css`, `assets.css`, and `editor.css` for the approved layer and breakpoint contracts.

### Verification and CI

- Add focused xUnit and Vitest files beside the responsibilities they test.
- Create `src/ResQ.Viz.Web/Services/BrowserVerificationMode.cs` for an environment-gated forced-legacy seam.
- Create `src/ResQ.Viz.Web/playwright.config.ts`, `e2e/support/operatorConsole.ts`, `e2e/operator-console.spec.ts`, and `e2e/dvr-heap.spec.ts`.
- Create `src/ResQ.Viz.Web/scripts/check-bundle-size.mjs` and modify `package.json`, lockfile, TypeScript/tool entry configuration, `.gitignore`, and `.github/workflows/ci.yml`.

## Chunk 1: Host contracts and typed client transport

### Task 1: Publish scenario state through full snapshots and deltas

**Files:**
- Create: `src/ResQ.Viz.Web/Models/ScenarioApi.cs`
- Create: `src/ResQ.Viz.Web/Services/SimulationRoom.Scenario.cs`
- Modify: `src/ResQ.Viz.Web/Services/SimulationRoom.Environment.cs`
- Modify: `src/ResQ.Viz.Web/Services/SimulationRoom.cs`
- Modify: `src/ResQ.Viz.Web/Services/SimulationRoom.Assets.cs`
- Modify: `src/ResQ.Viz.Web/Models/VizFrameV2.cs`
- Modify: `src/ResQ.Viz.Web/Models/VizFrameDeltaV2.cs`
- Modify: `src/ResQ.Viz.Web/Services/VizSnapshotV2Builder.cs`
- Modify: `src/ResQ.Viz.Web/Services/VizSnapshotDiffer.cs`
- Create: `tests/ResQ.Viz.Web.Tests/ScenarioSessionStateTests.cs`
- Modify: `tests/ResQ.Viz.Web.Tests/SnapshotDifferTests.Collections.cs`
- Modify: `tests/ResQ.Viz.Web.Tests/SnapshotDifferTests.Fixtures.cs`
- Modify: `tests/ResQ.Viz.Web.Tests/SnapshotBroadcastTests.cs`
- Modify: `tests/ResQ.Viz.Web.Tests/DeltaStreamTests.cs`

- [ ] **Step 1: Write failing room-lifecycle tests**

Create `ScenarioSessionStateTests.cs` with four facts:

```csharp
private static SimulationRoom CreateRoom() =>
    new("scenario-state", "127.0.0.0/24", NullLogger.Instance);

[Fact]
public void A_New_Room_Has_No_Current_Scenario() =>
    CreateRoom().CaptureAssetFrame().Scenario.Should().BeNull();

[Fact]
public void NotifyScenario_Publishes_Name_Time_And_First_Revision()
{
    var room = CreateRoom();
    room.NotifyScenario("flood-response");
    room.CaptureAssetFrame().Scenario.Should().Be(
        new ScenarioSessionState("flood-response", 0.0, 1));
}

[Fact]
public void Reset_Clears_The_Current_Scenario()
{
    var room = CreateRoom();
    room.NotifyScenario("flood-response");
    room.Reset();

    room.CaptureAssetFrame().Scenario.Should().BeNull();
}

[Fact]
public void Revision_Remains_Monotonic_After_Reset()
{
    var room = CreateRoom();
    room.NotifyScenario("flood-response");
    room.Reset();
    room.NotifyScenario("coastal-search");

    room.CaptureAssetFrame().Scenario!.Revision.Should().Be(3);
}
```

Use the existing room fixture patterns from `ScenarioCatalogTests.Fixtures.cs`. A reset counts as a scenario publication change, so the next named scenario revision is greater than the pre-reset revision.

- [ ] **Step 2: Run the lifecycle tests and confirm they fail**

Run:

```bash
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~ScenarioSessionStateTests'
```

Expected: FAIL because `ScenarioSessionState` and `RoomAssetFrame.Scenario` do not exist.

- [ ] **Step 3: Implement locked room scenario state**

Add the public wire record to `ScenarioApi.cs`:

```csharp
public sealed record ScenarioSessionState(
    string Name,
    double StartedAtSimulationSeconds,
    long Revision);
```

In `SimulationRoom.Scenario.cs`, own `_scenario`, `_scenarioRevision`, and the lock-only helpers, with `NotifyScenario` setting swarm and publication state in one `_lock` acquisition. `Reset()` clears current state and increments the revision under its existing lock. Never reset `_scenarioRevision` when replacing `_assets`.

Add `ScenarioSessionState? Scenario` to `RoomAssetFrame` and capture it alongside transport and assets. Preserve canonical scenario names supplied by `ScenarioService`.

- [ ] **Step 4: Run the lifecycle tests and confirm they pass**

Run the Step 2 command.

Expected: PASS, 4 tests.

- [ ] **Step 5: Write failing scenario delta round-trip tests**

Extend `SnapshotDifferTests.Collections.cs` with:

```csharp
[Fact]
public void Unchanged_Scenario_Is_Elided()
{
    var asset = Seeded(AirId, AssetDomain.Air, Vector3.Zero);
    var held = new ScenarioSessionState("flood-response", 0, 1);
    var previous = Room(FrameA, 0, [asset], scenario: held);
    var next = Room(
        FrameB, SecondFrameTick,
        [asset with { Sequence = asset.Sequence + 1 }],
        scenario: held);

    var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

    delta.Scenario.Should().BeNull();
    delta.ScenarioCleared.Should().BeFalse();
}

[Fact]
public void Scenario_Replacement_Is_Carried()
{
    var asset = Seeded(AirId, AssetDomain.Air, Vector3.Zero);
    var previousState = new ScenarioSessionState("single", 0, 1);
    var previous = Room(FrameA, 0, [asset], scenario: previousState);
    var replacement = new ScenarioSessionState("flood-response", 0, 2);
    var next = Room(
        FrameB, SecondFrameTick,
        [asset with { Sequence = asset.Sequence + 1 }],
        scenario: replacement);

    var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

    delta.Scenario.Should().Be(replacement);
    delta.ScenarioCleared.Should().BeFalse();
    delta.HasStateChanges.Should().BeTrue();
    VizSnapshotDiffer.Apply(previous, delta).Scenario.Should().Be(replacement);
}

[Fact]
public void Scenario_Clear_Is_Explicit_And_Round_Trips()
{
    var asset = Seeded(AirId, AssetDomain.Air, Vector3.Zero);
    var previous = Room(
        FrameA, 0, [asset],
        scenario: new ScenarioSessionState("flood-response", 0, 1));
    var next = Room(
        FrameB, SecondFrameTick,
        [asset with { Sequence = asset.Sequence + 1 }],
        scenario: null);

    var delta = VizSnapshotDiffer.Diff(previous, next, 1, 2);

    delta.Scenario.Should().BeNull();
    delta.ScenarioCleared.Should().BeTrue();
    delta.HasStateChanges.Should().BeTrue();
    VizSnapshotDiffer.Apply(previous, delta).Scenario.Should().BeNull();
}
```

Extend the fixture builder with an optional `ScenarioSessionState? scenario` argument. Add this broadcast case:

```csharp
[Fact]
public async Task Full_Snapshot_Carries_The_Room_Scenario()
{
    var room = CreatePopulatedRoom();
    room.NotifyScenario("flood-response");
    room.IncrementSnapshotSubscribers();
    var broadcaster = new RecordingBroadcaster();

    await CreateManager(broadcaster).BroadcastRoomAsync(room, CancellationToken.None);

    broadcaster.Snapshots.Single().Snapshot.Scenario.Should().Be(
        new ScenarioSessionState("flood-response", 0.0, 1));
}
```

Extend the existing `Reset_Produces_A_Snapshot_Across_The_Discontinuity` case by calling `room.NotifyScenario("flood-response")` before its first broadcast and adding:

```csharp
restart.Scenario.Should().BeNull(
    "the reset keyframe explicitly reports that no preset remains active");
```

Reset already forces a full keyframe, so do not add a delta integration assertion for it. Exercise `ScenarioCleared` only through the pure differ/apply tests above.

- [ ] **Step 6: Run the scenario stream tests and confirm they fail**

Run:

```bash
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~SnapshotDifferTests|FullyQualifiedName~SnapshotBroadcastTests|FullyQualifiedName~DeltaStreamTests'
```

Expected: FAIL because snapshot and delta records have no scenario fields.

- [ ] **Step 7: Implement full, replacement, and clear semantics**

Add trailing nullable `Scenario = null` to `VizSnapshotV2`. Add trailing nullable replacement `Scenario = null` and `bool ScenarioCleared = false` to `VizDeltaV2`, and include both in `HasStateChanges`. Trailing defaults preserve existing hand-built fixtures. Update `SnapshotDifferTests.Fixtures.cs` only where the new scenario cases need a non-null value.

`VizSnapshotV2Builder.Build` copies `capture.Scenario`. `VizSnapshotDiffer.Diff` follows this truth table:

| Previous | Next | Delta replacement | Clear |
| :--- | :--- | :--- | :--- |
| null | null | null | false |
| null | state | state | false |
| state A | state A | null | false |
| state A | state B | state B | false |
| state | null | null | true |

`Apply` uses `delta.ScenarioCleared ? null : delta.Scenario ?? baseline.Scenario`.

- [ ] **Step 8: Run the focused backend tests**

Run the Step 6 command and the lifecycle command from Step 2.

Expected: all selected tests pass with zero failures.

- [ ] **Step 9: Write failing client delta and projection tests**

Modify `client/__tests__/deltaApply.test.ts` with runnable cases using its existing `snapshot` and `delta` helpers:

```ts
const heldScenario = {
  name: 'flood-response',
  startedAtSimulationSeconds: 100,
  revision: 1,
};

it('preserves an unknown or unchanged scenario', () => {
  expect(mergeSnapshot(snapshot('f1', 100), delta('f2', 'f1', 101, 2)).scenario)
    .toBeUndefined();
  expect(mergeSnapshot(
    snapshot('f1', 100, { scenario: heldScenario }),
    delta('f2', 'f1', 101, 2),
  ).scenario).toEqual(heldScenario);
});

it('applies a replacement and an explicit clear', () => {
  const replacement = { ...heldScenario, name: 'coastal-search', revision: 2 };
  expect(mergeSnapshot(
    snapshot('f1', 100, { scenario: heldScenario }),
    delta('f2', 'f1', 101, 2, { scenario: replacement }),
  ).scenario).toEqual(replacement);
  expect(mergeSnapshot(
    snapshot('f1', 100, { scenario: heldScenario }),
    delta('f2', 'f1', 101, 2, { scenarioCleared: true }),
  ).scenario).toBeNull();
});
```

Modify `sceneFrameProjection.test.ts` using its existing `snapshot(overrides)` helper:

```ts
const scenario = { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 1 };

it('preserves present, unknown, and explicitly empty scenario state', () => {
  expect(projectSnapshot(
    snapshot({ scenario }), ABSURD_WALL_MS, new DescriptorCache(),
  ).scenario).toEqual(scenario);
  expect(projectSnapshot(
    snapshot(), ABSURD_WALL_MS, new DescriptorCache(),
  ).scenario).toBeUndefined();
  expect(projectSnapshot(
    snapshot({ scenario: null }), ABSURD_WALL_MS, new DescriptorCache(),
  ).scenario).toBeNull();
});
```

Run:

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/deltaApply.test.ts \
  client/__tests__/sceneFrameProjection.test.ts
```

Expected: FAIL because the TypeScript wire and scene types do not carry scenario state.

- [ ] **Step 10: Implement the client wire and projection fields**

Add `ScenarioSessionState` and optional-nullable snapshot/delta fields to `client/assets/types.ts`. Preserve the semantic distinction:

```ts
readonly scenario?: ScenarioSessionState | null; // undefined: older/unknown, null: explicit clear
readonly scenarioCleared?: boolean;
```

Update `deltaApply.ts` with the same truth table as the server. Add `scenario: ScenarioSessionState | null | undefined` to `SceneSnapshot` and copy it in `projectSnapshot`.

- [ ] **Step 11: Run focused client tests and typecheck**

Run:

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/deltaApply.test.ts \
  client/__tests__/sceneFrameProjection.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
```

Expected: both test files and TypeScript pass.

- [ ] **Step 12: Commit scenario streaming**

```bash
git add \
  src/ResQ.Viz.Web/Models/ScenarioApi.cs \
  src/ResQ.Viz.Web/Models/VizFrameV2.cs \
  src/ResQ.Viz.Web/Models/VizFrameDeltaV2.cs \
  src/ResQ.Viz.Web/Services/SimulationRoom.Scenario.cs \
  src/ResQ.Viz.Web/Services/SimulationRoom.Environment.cs \
  src/ResQ.Viz.Web/Services/SimulationRoom.cs \
  src/ResQ.Viz.Web/Services/SimulationRoom.Assets.cs \
  src/ResQ.Viz.Web/Services/VizSnapshotV2Builder.cs \
  src/ResQ.Viz.Web/Services/VizSnapshotDiffer.cs \
  src/ResQ.Viz.Web/client/assets/types.ts \
  src/ResQ.Viz.Web/client/assets/deltaApply.ts \
  src/ResQ.Viz.Web/client/assets/sceneFrame.ts \
  tests/ResQ.Viz.Web.Tests/ScenarioSessionStateTests.cs \
  tests/ResQ.Viz.Web.Tests/SnapshotDifferTests.Collections.cs \
  tests/ResQ.Viz.Web.Tests/SnapshotDifferTests.Fixtures.cs \
  tests/ResQ.Viz.Web.Tests/SnapshotBroadcastTests.cs \
  tests/ResQ.Viz.Web.Tests/DeltaStreamTests.cs \
  src/ResQ.Viz.Web/client/__tests__/deltaApply.test.ts \
  src/ResQ.Viz.Web/client/__tests__/sceneFrameProjection.test.ts
git commit -m "feat(stream): publish scenario session state"
```

### Task 2: Add v2 scenario catalog and start routes

**Files:**
- Modify: `src/ResQ.Viz.Web/Models/ScenarioApi.cs`
- Modify: `src/ResQ.Viz.Web/Services/ScenarioService.cs`
- Create: `src/ResQ.Viz.Web/Controllers/SimV2Controller.Scenarios.cs`
- Modify: `src/ResQ.Viz.Web/Controllers/SimV2Controller.cs`
- Create: `tests/ResQ.Viz.Web.Tests/SimV2ControllerTests.Scenarios.cs`
- Modify: `tests/ResQ.Viz.Web.Tests/SimV2ControllerTests.cs`
- Modify: `tests/ResQ.Viz.Web.Tests/ScenarioCatalogTests.cs`

- [ ] **Step 1: Add an executable scenario-controller fixture and failing tests**

Extend the existing test helper without breaking named `factory:` or `frames:` calls:

```csharp
private static IConfiguration ScenarioConfiguration() =>
    new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

private static (SimV2Controller ctrl, SimulationRoom room) CreateController(
    IAssetFactory? factory = null,
    VizFrameBuilder? frames = null,
    ScenarioService? scenarios = null)
{
    var room = CreateRoom();
    IAssetFactory[] factories = factory is null ? [] : [factory];
    var ctrl = new SimV2Controller(
        frames ?? new VizFrameBuilder(), factories,
        NullLogger<SimV2Controller>.Instance,
        authority: null,
        scenarios: scenarios);
    var http = new DefaultHttpContext();
    http.Items[RequireRoomAttribute.RoomItemKey] = room;
    ctrl.ControllerContext = new ControllerContext { HttpContext = http };
    return (ctrl, room);
}
```

Create `SimV2ControllerTests.Scenarios.cs` with these complete assertions:

Add `using System.Reflection` and `using Microsoft.AspNetCore.RateLimiting` for the route-policy assertion.

```csharp
[Fact]
public void Catalog_Uses_Stable_Lowercase_Domain_Keys_Including_Zeroes()
{
    var scenarios = new ScenarioService(ScenarioConfiguration());
    var (ctrl, _) = CreateController(scenarios: scenarios);

    var catalog = Body<ScenarioCatalogResponse>(ctrl.GetScenarioCatalog());
    var flood = catalog.Scenarios.Single(s => s.Name == "flood-response");
    flood.AssetCount.Should().Be(8);
    flood.DomainCounts.Should().Be(new ScenarioDomainCounts(Air: 3, Ground: 3, Surface: 2));
    catalog.Scenarios.Single(s => s.Name == "single").DomainCounts
        .Should().Be(new ScenarioDomainCounts(Air: 1, Ground: 0, Surface: 0));
}

[Fact]
public void Unknown_Scenario_Returns_A_Typed_404_Problem()
{
    var (ctrl, _) = CreateController(scenarios: new ScenarioService(ScenarioConfiguration()));
    Problem(ctrl.StartScenario("missing"), StatusCodes.Status404NotFound).Code
        .Should().Be(ScenarioProblems.NotFound);
}

[Fact]
public void Start_Resets_Runs_Notifies_And_Returns_Canonical_State()
{
    var (ctrl, room) = CreateController(scenarios: new ScenarioService(ScenarioConfiguration()));
    room.AddDrone("old-air", Vector3.Zero, "test");

    var body = Body<ScenarioStartResponse>(ctrl.StartScenario("FLOOD-RESPONSE"));

    body.Current.Name.Should().Be("flood-response");
    room.CaptureAssetFrame().Scenario.Should().Be(body.Current);
    room.CaptureAssetFrame().Descriptors.Should().HaveCount(8);
    room.CaptureAssetFrame().Descriptors.Should().NotContain(d => d.AssetId == "old-air");
}

[Fact]
public void Start_Uses_The_Destructive_Rate_Limit()
{
    var method = typeof(SimV2Controller).GetMethod(nameof(SimV2Controller.StartScenario));
    method!.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName
        .Should().Be("destructive");
}
```

Extend `ScenarioCatalogTests` to compare service summaries with the parsed catalog count for all 19 presets.

- [ ] **Step 2: Run scenario endpoint tests and confirm they fail**

```bash
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~ScenarioCatalogTests|FullyQualifiedName~SimV2ControllerTests'
```

Expected: FAIL on missing DTOs, constructor argument, and actions.

- [ ] **Step 3: Implement exact scenario DTOs and compatible injection**

Add:

```csharp
public sealed record ScenarioDomainCounts(int Air, int Ground, int Surface);

public sealed record ScenarioSummary(
    string Name,
    int AssetCount,
    ScenarioDomainCounts DomainCounts,
    IReadOnlyDictionary<string, int> VehicleClassCounts);

public sealed record ScenarioCatalogResponse(IReadOnlyList<ScenarioSummary> Scenarios);
public sealed record ScenarioStartResponse(ScenarioSessionState Current);

public static class ScenarioProblems
{
    public const string NotFound = "scenario.notFound";
    public const string CatalogUnavailable = "scenario.catalogUnavailable";
}
```

`ScenarioService` exposes immutable summaries derived from validated entries. `ScenarioDomainCounts` always emits all three properties. ASP.NET web JSON serializes them as lowercase `air`, `ground`, and `surface`.

Preserve all direct controller construction with this trailing signature:

```csharp
public SimV2Controller(
    VizFrameBuilder frames,
    IEnumerable<IAssetFactory> factories,
    ILogger<SimV2Controller> logger,
    ControlAuthorityRegistry? authority = null,
    ScenarioService? scenarios = null)
```

Store the nullable service so existing endpoints remain usable when it is absent. Only scenario actions return a typed 501 `CatalogUnavailable`, while production dependency injection supplies the registered service.

Implement `GetScenarioCatalog` and `[EnableRateLimiting("destructive")] StartScenario`. The POST order is validate canonical name, reset, run, notify, and return the captured state. Reuse `Failure(...)`. Keep the v1 route unchanged.

- [ ] **Step 4: Run scenario tests and confirm they pass**

Run the Step 2 command.

Expected: all existing `SimV2ControllerTests`, the four new scenario cases, and all scenario-catalog cases pass.

- [ ] **Step 5: Commit scenario discovery and start**

```bash
git add \
  src/ResQ.Viz.Web/Models/ScenarioApi.cs \
  src/ResQ.Viz.Web/Services/ScenarioService.cs \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.Scenarios.cs \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.cs \
  tests/ResQ.Viz.Web.Tests/SimV2ControllerTests.Scenarios.cs \
  tests/ResQ.Viz.Web.Tests/SimV2ControllerTests.cs \
  tests/ResQ.Viz.Web.Tests/ScenarioCatalogTests.cs
git commit -m "feat(api): expose scenario catalog and start"
```

### Task 3: Add deployment-derived spawn-profile discovery

**Files:**
- Create: `src/ResQ.Viz.Web/Models/AssetProfileApi.cs`
- Create: `src/ResQ.Viz.Web/Controllers/SimV2Controller.AssetProfiles.cs`
- Create: `tests/ResQ.Viz.Web.Tests/SimV2ControllerTests.AssetProfiles.cs`

- [ ] **Step 1: Write executable failing profile tests**

Add a test-only factory and controller helper in the new partial test file:

```csharp
private sealed class ClassOnlyFactory(VehicleClass supported) : IAssetFactory
{
    public bool CanCreate(VehicleClass vehicleClass) => vehicleClass == supported;
    public ISimulatedAsset Create(in AssetSpawnPlan plan) =>
        throw new NotSupportedException("Discovery must not instantiate a profile.");
}

private static SimV2Controller ProfileController(params IAssetFactory[] factories)
{
    var ctrl = new SimV2Controller(
        new VizFrameBuilder(), factories, NullLogger<SimV2Controller>.Instance);
    var http = new DefaultHttpContext();
    http.Items[RequireRoomAttribute.RoomItemKey] = CreateRoom();
    ctrl.ControllerContext = new ControllerContext { HttpContext = http };
    return ctrl;
}
```

Add:

```csharp
[Fact]
public void Multirotor_Is_Always_Discoverable()
{
    var profiles = Body<AssetProfileCatalogResponse>(ProfileController().GetAssetProfiles()).Profiles;
    profiles.Should().ContainSingle(p =>
        p.VehicleClass == VehicleClass.Multirotor && p.Domain == AssetDomain.Air);
}

[Fact]
public void Registered_Ground_And_Surface_Classes_Are_Discoverable()
{
    var ctrl = ProfileController(
        new ClassOnlyFactory(VehicleClass.AckermannRover),
        new ClassOnlyFactory(VehicleClass.SurfaceVessel));
    var profiles = Body<AssetProfileCatalogResponse>(ctrl.GetAssetProfiles()).Profiles;

    profiles.Select(p => p.VehicleClass).Should().BeEquivalentTo(
        [VehicleClass.Multirotor, VehicleClass.AckermannRover, VehicleClass.SurfaceVessel]);
}

[Fact]
public void Reserved_And_Unregistered_Classes_Are_Absent()
{
    var profiles = Body<AssetProfileCatalogResponse>(
        ProfileController(new ClassOnlyFactory(VehicleClass.Rov)).GetAssetProfiles()).Profiles;
    profiles.Should().NotContain(p => p.VehicleClass is
        VehicleClass.Unspecified or VehicleClass.Rov or VehicleClass.Auv or VehicleClass.TrackedRover);
}

[Fact]
public void Heading_Applies_Only_To_Non_Air_Profiles()
{
    var ctrl = ProfileController(new ClassOnlyFactory(VehicleClass.SurfaceVessel));
    var profiles = Body<AssetProfileCatalogResponse>(ctrl.GetAssetProfiles()).Profiles;
    profiles.Single(p => p.VehicleClass == VehicleClass.Multirotor).HeadingApplies.Should().BeFalse();
    profiles.Single(p => p.VehicleClass == VehicleClass.SurfaceVessel).HeadingApplies.Should().BeTrue();
}
```

- [ ] **Step 2: Run profile tests and confirm they fail**

```bash
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~SimV2ControllerTests'
```

Expected: FAIL because the profile DTO and action do not exist.

- [ ] **Step 3: Implement profile discovery without instantiation**

Create:

```csharp
public sealed record AssetSpawnProfile(
    VehicleClass VehicleClass,
    AssetDomain Domain,
    string DisplayName,
    bool HeadingApplies);

public sealed record AssetProfileCatalogResponse(IReadOnlyList<AssetSpawnProfile> Profiles);
```

`GetAssetProfiles` always includes multirotor. It includes another class only when `AssetProfiles.IsSupported` is true and one registered `_factories` member answers `CanCreate`. Domain comes from `AssetProfiles.DomainFor`. Heading applies to non-air classes. Never call `Create` during discovery.

- [ ] **Step 4: Run profile tests and confirm they pass**

Run the Step 2 command.

Expected: all `SimV2ControllerTests`, including the four new profile cases, pass.

- [ ] **Step 5: Commit profile discovery**

```bash
git add \
  src/ResQ.Viz.Web/Models/AssetProfileApi.cs \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.AssetProfiles.cs \
  tests/ResQ.Viz.Web.Tests/SimV2ControllerTests.AssetProfiles.cs
git commit -m "feat(api): expose spawnable asset profiles"
```

### Task 4: Preserve typed v2 problem bodies in the client API

**Files:**
- Modify: `src/ResQ.Viz.Web/client/api.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/api.test.ts`

- [ ] **Step 1: Write executable failing problem-decoder tests**

Use one stubbed fetch for both typed GET and POST helpers:

```ts
const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => vi.stubGlobal('fetch', fetchMock));
afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

it('retains a typed GET problem with HTTP status', async () => {
  fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({
    code: 'authority.notHolder',
    title: 'Request conflicts with current state',
    detail: 'Another console holds the asset.',
    traceId: 'trace-1',
    assetId: 'uav-1',
    errors: [],
  }), { status: 409, statusText: 'Conflict', headers: { 'Content-Type': 'application/json' } }));

  const result = await apiGetJson<unknown>('/api/v2/sim/assets/uav-1/control', { retries: 0 });

  expect(result).toEqual({ success: false, error: {
    kind: 'problem',
    problem: expect.objectContaining({ status: 409, code: 'authority.notHolder', traceId: 'trace-1' }),
  }});
});

it.each(['', '<html>bad gateway</html>'])('falls back for a non-problem body', async body => {
  fetchMock.mockResolvedValueOnce(new Response(body, { status: 502, statusText: 'Bad Gateway' }));
  const result = await apiPostJson<unknown>('/api/v2/sim/assets', {});
  expect(result).toEqual({
    success: false,
    error: {
      kind: 'problem',
      problem: {
      status: 502, code: 'http.error', reasonCode: null,
      title: 'Bad Gateway', detail: 'Request failed', traceId: null, errors: [],
      },
    },
  });
});

it('keeps a network rejection distinct', async () => {
  fetchMock.mockRejectedValueOnce(new TypeError('offline'));
  await expect(apiGetJson('/api/v2/sim/scenarios', { retries: 0 }))
    .resolves.toMatchObject({ success: false, error: { kind: 'network' } });
});

it('keeps an abort distinct as a timeout', async () => {
  fetchMock.mockRejectedValueOnce(new DOMException('aborted', 'AbortError'));
  await expect(apiPostJson('/api/v2/sim/scenarios/flood-response/start'))
    .resolves.toMatchObject({ success: false, error: { kind: 'timeout' } });
});

it('parses a successful JSON mutation', async () => {
  fetchMock.mockResolvedValueOnce(new Response('{"scenario":"flood-response"}', {
    status: 200, headers: { 'Content-Type': 'application/json' },
  }));
  await expect(apiPostJson<{ scenario: string }>('/start'))
    .resolves.toEqual({ success: true, value: { scenario: 'flood-response' } });
});

it('keeps the timeout active while the JSON body is being read', async () => {
  vi.useFakeTimers();
  fetchMock.mockImplementationOnce(async (_path, init) => ({
    ok: true,
    status: 200,
    json: () => new Promise((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () =>
        reject(new DOMException('aborted', 'AbortError')));
    }),
  }) as Response);

  const pending = apiGetJson('/slow', { timeoutMs: 10, retries: 0 });
  await vi.advanceTimersByTimeAsync(11);

  await expect(pending).resolves.toMatchObject({
    success: false,
    error: { kind: 'timeout' },
  });
});
```

Assert the fallback is:

```ts
{
  kind: 'problem',
  problem: {
    status: 502,
    code: 'http.error',
    reasonCode: null,
    title: 'Bad Gateway',
    detail: 'Request failed',
    traceId: null,
    errors: [],
  },
}
```

- [ ] **Step 2: Run API tests and confirm they fail**

Run:

```bash
npm --prefix src/ResQ.Viz.Web test -- client/__tests__/api.test.ts
```

Expected: FAIL because `ApiProblem`, `ApiFailure`, `apiGetJson`, and `apiPostJson` do not exist.

- [ ] **Step 3: Implement one decoder and typed JSON helpers**

Add:

```ts
export interface ApiProblem {
  readonly status: number;
  readonly code: string;
  readonly reasonCode: string | null;
  readonly title: string;
  readonly detail: string;
  readonly traceId: string | null;
  readonly errors: readonly { field: string; code: string; message: string }[];
}

export type ApiFailure =
  | { readonly kind: 'problem'; readonly problem: ApiProblem }
  | { readonly kind: 'network'; readonly message: string }
  | { readonly kind: 'timeout'; readonly message: string };
```

`ApiHttpError` gains `problem`. Keep the abort timer alive through body parsing while both typed JSON helpers share one decoder and GET's network-only retries. Existing `apiGet` and `apiPost` remain unchanged for v1 callers.

Malformed, empty, and non-JSON bodies use `http.error`. A valid problem body gets status from the `Response`, normalizes nullable fields, and never parses `detail` for behavior.

- [ ] **Step 4: Run API tests and all existing API consumers through typecheck**

Run:

```bash
npm --prefix src/ResQ.Viz.Web test -- client/__tests__/api.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
```

Expected: API tests and TypeScript pass.

- [ ] **Step 5: Commit typed API failures**

```bash
git add src/ResQ.Viz.Web/client/api.ts src/ResQ.Viz.Web/client/__tests__/api.test.ts
git commit -m "feat(client): preserve typed API failures"
```

### Task 5: Run the Chunk 1 contract gate

**Files:**
- Verify only: all Chunk 1 files

- [ ] **Step 1: Run all focused backend contract tests**

```bash
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~ScenarioSessionStateTests|FullyQualifiedName~ScenarioCatalogTests|FullyQualifiedName~SimV2ControllerTests|FullyQualifiedName~SnapshotDifferTests|FullyQualifiedName~SnapshotBroadcastTests|FullyQualifiedName~DeltaStreamTests'
```

Expected: zero failures.

- [ ] **Step 2: Run all focused client contract tests**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/api.test.ts \
  client/__tests__/deltaApply.test.ts \
  client/__tests__/sceneFrameProjection.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
```

Expected: all selected files and TypeScript pass.

- [ ] **Step 3: Verify formatting and the chunk diff**

```bash
dotnet format ResQ.Viz.sln --no-restore --verify-no-changes
git diff --check HEAD~4..HEAD
git status --short
```

Expected: format and whitespace checks pass. `git status --short` is empty because the implementation-plan document remains ignored until the planning checkpoint commit.

## Chunk 2: Operator shell, mission, fleet, and setup surfaces

Every new DOM-oriented Vitest file in this chunk starts with `// @vitest-environment happy-dom`. The repository default is Node.

### Task 6: Establish shell branches, mounts, and legacy isolation

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/OperatorShell.ts`
- Create: `src/ResQ.Viz.Web/client/operator/types.ts`
- Create: `src/ResQ.Viz.Web/client/styles/operator.css`
- Modify: `src/ResQ.Viz.Web/client/index.html`
- Modify: `src/ResQ.Viz.Web/client/styles/tokens.css`
- Modify: `src/ResQ.Viz.Web/client/styles/main.css`
- Modify: `src/ResQ.Viz.Web/client/controls.ts`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/operatorShell.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/controls.test.ts`

- [ ] **Step 1: Write failing shell-state tests**

Use this complete fixture in `operatorShell.test.ts`:

```ts
function mountShell(): OperatorShell {
  document.body.innerHTML = `
    <button id="btn-sidebar-toggle" aria-controls="sidebar"></button>
    <button id="btn-editor-toggle" aria-controls="operator-editor-layer"></button>
    <aside id="sidebar">
      <section id="operator-boot"></section>
      <section id="operator-v2-console">
        <h2 id="fleet-heading" tabindex="-1">Fleet</h2>
        <div id="operator-mission"></div>
        <div id="fleet-filter"></div><div id="fleet-roster"></div>
        <button id="btn-spawn-asset"></button><button id="btn-environment"></button>
        <div id="advanced-safety"></div>
      </section>
      <section id="legacy-console"></section>
    </aside>
    <div id="operator-context-layer"></div>
    <div id="operator-modal-layer"></div>
    <div id="operator-editor-layer"></div>`;
  return new OperatorShell(document);
}

it('starts with only boot active and no legacy flash', () => {
  const shell = mountShell();
  expect(shell.mode).toBe('booting');
  expect(document.querySelector('#operator-boot')!.hasAttribute('hidden')).toBe(false);
  for (const id of ['operator-v2-console', 'legacy-console']) {
    const el = document.getElementById(id)!;
    expect(el.hidden).toBe(true);
    expect(el.inert).toBe(true);
    expect(el.getAttribute('aria-hidden')).toBe('true');
  }
});

it.each(['v2', 'legacy'] as const)('activates exactly the %s branch', mode => {
  const shell = mountShell();
  shell.setMode(mode);
  expect(document.getElementById(mode === 'v2' ? 'operator-v2-console' : 'legacy-console')!.inert)
    .toBe(false);
  expect(document.querySelectorAll('#sidebar > section:not([hidden])')).toHaveLength(1);
});

it('exposes context outside the translated rail and synchronizes controls', () => {
  const shell = mountShell();
  expect(shell.mounts.context.closest('#sidebar')).toBeNull();
  shell.setRailOpen(false);
  expect(document.getElementById('btn-sidebar-toggle')!.getAttribute('aria-expanded')).toBe('false');
  expect(document.getElementById('sidebar')!.inert).toBe(true);
  shell.setEditorOpen(true);
  expect(document.getElementById('btn-editor-toggle')!.getAttribute('aria-expanded')).toBe('true');
});
```

Update `controls.test.ts` with a real `<section id="legacy-console">` fixture and dispatch a cancelable Tab key event. Assert `defaultPrevented === false`.

- [ ] **Step 2: Run shell tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/operatorShell.test.ts \
  client/__tests__/controls.test.ts
```

Expected: FAIL because the shell and root-scoped legacy adapter do not exist.

- [ ] **Step 3: Add stable HTML ownership and layer tokens**

Keep `#sidebar` as the layout anchor and move its current markup, with control IDs unchanged, into `#legacy-console`. Add the boot/v2 branches and every fixture mount, followed by context, modal, and Editor layers outside the sidebar. Finish with the labeled Editor button and asset-based empty-state copy.

Add exact layer variables `0, 100, 150, 180, 200, 240, 300, 400` from the spec. Replace relevant literal z-indexes. `operator.css` owns desktop rail, medium drawer/bottom-sheet, `[hidden]`, `inert`, `dvh`, and safe-area rules. Do not style dialog contents in this eager sheet.

- [ ] **Step 4: Implement OperatorShell and root-scope ControlPanel**

Export:

```ts
export type OperatorMode = 'booting' | 'v2' | 'legacy';

export interface OperatorMounts {
  readonly mission: HTMLElement;
  readonly filter: HTMLElement;
  readonly roster: HTMLElement;
  readonly advancedSafety: HTMLElement;
  readonly context: HTMLElement;
  readonly modal: HTMLElement;
  readonly editor: HTMLElement;
}
```

`OperatorShell` resolves required elements once, throws a named setup error for a missing mount, and owns only mode, rail, Editor visibility, and ARIA/inert state. Add `focusFleetHeading()`.

Change `ControlPanel` to require `legacyRoot: HTMLElement` and query all v1 controls within it. Remove its global Tab shortcut. Any remaining global keyboard handler must return when the legacy root is hidden or inert.

In the same task, update `app.ts` to construct `OperatorShell` first and pass `document.getElementById('legacy-console')!` into `ControlPanel`. Do not leave a broken zero-argument constructor for Task 7.

- [ ] **Step 5: Run shell tests, typecheck, and commit**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/operatorShell.test.ts client/__tests__/controls.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/OperatorShell.ts \
  src/ResQ.Viz.Web/client/operator/types.ts \
  src/ResQ.Viz.Web/client/styles/operator.css \
  src/ResQ.Viz.Web/client/index.html \
  src/ResQ.Viz.Web/client/styles/tokens.css \
  src/ResQ.Viz.Web/client/styles/main.css \
  src/ResQ.Viz.Web/client/controls.ts \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/operatorShell.test.ts \
  src/ResQ.Viz.Web/client/__tests__/controls.test.ts
git commit -m "feat(client): establish operator shell"
```

Expected: focused tests and typecheck pass. The commit contains shell ownership only.

### Task 7: Replace drone-count startup with an explicit negotiation state machine

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/StartupCoordinator.ts`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/startupCoordinator.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts`

- [ ] **Step 1: Write failing deterministic startup tests**

Use a harness with injected actions:

```ts
function harness() {
  const modes: OperatorMode[] = [];
  const v1Starts: string[] = [];
  const v2Starts: string[] = [];
  const coordinator = new StartupCoordinator({
    setMode: mode => modes.push(mode),
    startLegacyScenario: async name => { v1Starts.push(name); return true; },
    startV2Scenario: async name => {
      v2Starts.push(name);
      return { success: true, value: { current: { name, startedAtSimulationSeconds: 0, revision: 1 } } };
    },
    schedule: (callback, ms) => window.setTimeout(callback, ms),
    cancel: id => window.clearTimeout(id),
  });
  return { coordinator, modes, v1Starts, v2Starts };
}

it('starts Flood Response once for an empty hydrated v2 room', async () => {
  const h = harness();
  await h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });
  await h.coordinator.onV2Snapshot({ assetCount: 0, scenario: null });
  expect(h.modes).toContain('v2');
  expect(h.v2Starts).toEqual(['flood-response']);
  expect(h.v1Starts).toEqual([]);
});

it.each([
  { assetCount: 0, scenario: undefined },
  { assetCount: 1, scenario: null },
  { assetCount: 3, scenario: { name: 'custom', startedAtSimulationSeconds: 0, revision: 1 } },
])('does not replace an unknown or populated v2 room', async input => {
  const h = harness();
  await h.coordinator.onV2Snapshot(input);
  expect(h.v2Starts).toEqual([]);
});

it('enters viable legacy after five seconds and starts Single once', async () => {
  vi.useFakeTimers();
  const h = harness();
  h.coordinator.startNegotiation();
  h.coordinator.onV1Frame(0);
  await vi.advanceTimersByTimeAsync(5_000);
  expect(h.modes.at(-1)).toBe('legacy');
  expect(h.v1Starts).toEqual(['single']);
  expect(h.v2Starts).toEqual([]);
});

it('does not claim legacy without a v1 frame', async () => {
  vi.useFakeTimers();
  const h = harness();
  h.coordinator.startNegotiation();
  await vi.advanceTimersByTimeAsync(5_000);
  expect(h.modes).toEqual([]);
});

it('promotes legacy to a populated v2 room without a default start', async () => {
  const h = harness();
  h.coordinator.onV1Frame(0);
  h.coordinator.onV2Rejected();
  await h.coordinator.onV2Snapshot({ assetCount: 3, scenario: null });
  expect(h.modes.at(-1)).toBe('v2');
  expect(h.v1Starts).toEqual(['single']);
  expect(h.v2Starts).toEqual([]);
});

it('cancels fallback when v2 arrives before the timer', async () => {
  vi.useFakeTimers();
  const h = harness();
  h.coordinator.startNegotiation();
  h.coordinator.onV1Frame(0);
  await h.coordinator.onV2Snapshot({ assetCount: 2, scenario: null });
  await vi.advanceTimersByTimeAsync(5_000);
  expect(h.modes.at(-1)).toBe('v2');
  expect(h.v1Starts).toEqual([]);
});

it('enters legacy without starting Single for a populated v1 room', () => {
  const h = harness();
  h.coordinator.onV1Frame(2);
  h.coordinator.onV2Rejected();
  expect(h.modes.at(-1)).toBe('legacy');
  expect(h.v1Starts).toEqual([]);
});

it('waits for a v1 frame when rejection arrives first', () => {
  const h = harness();
  h.coordinator.onV2Rejected();
  expect(h.modes).toEqual([]);
  h.coordinator.onV1Frame(0);
  expect(h.modes.at(-1)).toBe('legacy');
  expect(h.v1Starts).toEqual(['single']);
});
```

Restore real timers in `afterEach`. Add a ground-only and surface-only object to the populated table by using `assetCount: 1`. Startup does not inspect drone count or domain.

- [ ] **Step 2: Run startup tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- client/__tests__/startupCoordinator.test.ts
```

Expected: FAIL because the coordinator does not exist.

- [ ] **Step 3: Implement StartupCoordinator as the sole default-scenario owner**

Track v1 viability, v2 rejection, readable v2 arrival, fallback timing, and each attempted default, claiming an attempt before awaiting its POST. Cancel the timer on the first readable v2 snapshot and in `dispose`. Rejection enters legacy only after a v1 frame proves that path works; later v2 always wins, while populated inventory never triggers a default. Route `single` through `startLegacyScenario` and `flood-response` through `startV2Scenario`.

Expose `startNegotiation`, `onV1Frame(assetCount)`, `onV2Rejected`, `onV2Snapshot`, `onConnectionFailed`, and `dispose`. Unknown scenario is `undefined`, while empty is `null`.

- [ ] **Step 4: Wire startup before connection and remove `_autoSpawnIfEmpty`**

Instantiate `OperatorShell` and `StartupCoordinator` before SignalR, feeding boot-time v1 counts and v2 snapshots before auxiliary GETs. Subscription rejection goes to the coordinator, while a no-stream failure remains a boot connection error. Delete `_autoSpawnIfEmpty` and its drone-only `/api/sim/state` test.

Wire the actions immediately, before `consoleApi.ts` exists:

```ts
startLegacyScenario: async () =>
  (await apiPost('/api/sim/scenario/single')).success,
startV2Scenario: async () =>
  apiPostJson<{ current: ScenarioSessionState }>(
    '/api/v2/sim/scenarios/flood-response/start'),
```

Task 9 later moves the v2 route into `consoleApi.ts`. Behavior and tests remain unchanged.

Update `multiDomainWiring.test.ts` source assertions to require coordinator calls and reject `scenario/single` from the v2 startup branch.

- [ ] **Step 5: Run tests and commit startup negotiation**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/startupCoordinator.test.ts \
  client/__tests__/multiDomainWiring.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/StartupCoordinator.ts \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/startupCoordinator.test.ts \
  src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts
git commit -m "feat(client): negotiate operator and legacy startup"
```

### Task 8: Make mission state and auxiliary resources authoritative

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/ScenarioRuntime.ts`
- Create: `src/ResQ.Viz.Web/client/operator/scenarioPresentation.ts`
- Create: `src/ResQ.Viz.Web/client/operator/ConsoleResources.ts`
- Create: `src/ResQ.Viz.Web/client/operator/MissionPanel.ts`
- Modify: `src/ResQ.Viz.Web/client/styles/operator.css`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Modify: `src/ResQ.Viz.Web/client/missionChrome.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/scenarioRuntime.test.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/consoleResources.test.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/missionPanel.test.ts`

- [ ] **Step 1: Write failing scenario-runtime tests**

Use callbacks that record presentation effects:

```ts
it('publishes one Live transition per increasing revision', () => {
  const starts: string[] = [];
  const runtime = new ScenarioRuntime({ onPresent: s => starts.push(s.name) });
  const flood = { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 1 };
  runtime.apply(flood, 8, 'live');
  runtime.apply(flood, 8, 'live');
  expect(runtime.view).toMatchObject({ kind: 'active', name: 'flood-response' });
  expect(starts).toEqual(['flood-response']);
});

it('distinguishes unknown, none, and custom', () => {
  const runtime = new ScenarioRuntime({ onPresent: () => undefined });
  runtime.apply(undefined, 0, 'live');
  expect(runtime.view.kind).toBe('unknown');
  runtime.apply(null, 0, 'live');
  expect(runtime.view.kind).toBe('none');
  runtime.apply(null, 2, 'live');
  expect(runtime.view.kind).toBe('custom');
});

it('defers replay presentation until Live resumes', () => {
  const starts: string[] = [];
  const runtime = new ScenarioRuntime({ onPresent: s => starts.push(s.name) });
  runtime.apply({ name: 'coastal-search', startedAtSimulationSeconds: 4, revision: 2 }, 4, 'replay');
  expect(starts).toEqual([]);
  runtime.resumeLive();
  expect(starts).toEqual(['coastal-search']);
});

it('keeps a successful request pending until the stream confirms it', () => {
  const runtime = new ScenarioRuntime({ onPresent: () => undefined });
  runtime.requested('flood-response');
  runtime.requestAccepted('flood-response');
  expect(runtime.view).toMatchObject({ kind: 'pending', name: 'flood-response' });
});
```

- [ ] **Step 2: Write failing independent-resource and mission-panel tests**

Use this resource harness:

```ts
const unavailable: ApiFailure = {
  kind: 'problem',
  problem: {
    status: 503, code: 'catalog.unavailable', reasonCode: null,
    title: 'Unavailable', detail: 'Retry later', traceId: null, errors: [],
  },
};
const loadCatalog = vi.fn()
  .mockResolvedValue({ success: true, value: { scenarios: [] } });
const loadProfiles = vi.fn()
  .mockResolvedValueOnce({ success: false, error: unavailable })
  .mockResolvedValueOnce({ success: true, value: { profiles: [] } });
const resources = new ConsoleResources({ loadCatalog, loadProfiles });

await resources.loadMissing();
expect(resources.catalog.status).toBe('ready');
expect(resources.profiles).toEqual({ status: 'error', failure: unavailable });
await resources.retry('profiles');
expect(resources.profiles.status).toBe('ready');
expect(loadCatalog).toHaveBeenCalledTimes(1);
expect(loadProfiles).toHaveBeenCalledTimes(2);
await resources.onVisibilityReturn();
expect(loadCatalog).toHaveBeenCalledTimes(1);
expect(loadProfiles).toHaveBeenCalledTimes(2);
```

Use this MissionPanel boundary:

```ts
const mount = document.createElement('section');
const onTogglePause = vi.fn();
const onReset = vi.fn();
const onChange = vi.fn();
const onRetryCatalog = vi.fn();
const panel = new MissionPanel({ mount, onTogglePause, onReset, onChange, onRetryCatalog });

panel.render({
  mission: { kind: 'active', name: 'flood-response', pendingName: null },
  transport: { paused: false, speed: 2, simulationTimeSeconds: 18.2 },
  catalog: { status: 'ready', value: { scenarios: [] } },
});
expect(mount.textContent).toContain('Flood Response');
expect(mount.textContent).toContain('18.2s');
expect(mount.textContent).toContain('2×');

panel.render({
  mission: { kind: 'pending', name: 'flood-response', pendingName: 'coastal-search' },
  transport: { paused: true, speed: 1, simulationTimeSeconds: 19 },
  catalog: { status: 'error', failure: unavailable },
});
expect(mount.textContent).toContain('Flood Response');
expect(mount.textContent).toContain('Starting Coastal Search');
expect(mount.querySelector<HTMLButtonElement>('[data-action="change"]')!.disabled).toBe(true);
mount.querySelector<HTMLButtonElement>('[data-action="retry-catalog"]')!.click();
expect(onRetryCatalog).toHaveBeenCalledOnce();
```

Add separate renders for `{ kind: 'unknown' }`, `{ kind: 'none' }`, and `{ kind: 'custom' }` and assert their exact labels.

- [ ] **Step 3: Run mission/resource tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/scenarioRuntime.test.ts \
  client/__tests__/consoleResources.test.ts \
  client/__tests__/missionPanel.test.ts
```

Expected: FAIL because the three units do not exist.

- [ ] **Step 4: Implement focused mission units**

`ScenarioRuntime` owns highest applied revision, view state, pending request, and deferred Live presentation. `scenarioPresentation.ts` owns display name, category, purpose, and optional environment lookup, with a humanized `Other` fallback.

`ConsoleResources` holds separate `idle | loading | ready | error` states for scenario catalog and asset profiles and exposes `loadMissing`, `retry(kind)`, `onReconnect`, and `onVisibilityReturn`. It uses Chunk 1 `apiGetJson` helpers.

`MissionPanel` consumes state and injected callbacks only. It does not fetch or dispatch `resq:scenario-start`. Make `missionChrome` a legacy-only adapter by adding `setEnabled(boolean)`: it hides and ignores scenario events while v2 is active. `MissionPanel` is the sole v2 mission presenter. Add a mission-panel test that a disabled legacy chrome does not update on `resq:scenario-start`.

- [ ] **Step 5: Wire authoritative stream transitions and commit**

In `app.ts`, replace `_currentScenario` as truth. Feed `projected.scenario` and complete asset count to `ScenarioRuntime`. Its Live presentation callback clears selection, clears the DVR ring, dispatches one `resq:scenario-start`, and fits after the matching asset frame. Replay never calls that callback.

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/scenarioRuntime.test.ts \
  client/__tests__/consoleResources.test.ts \
  client/__tests__/missionPanel.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/ScenarioRuntime.ts \
  src/ResQ.Viz.Web/client/operator/scenarioPresentation.ts \
  src/ResQ.Viz.Web/client/operator/ConsoleResources.ts \
  src/ResQ.Viz.Web/client/operator/MissionPanel.ts \
  src/ResQ.Viz.Web/client/styles/operator.css \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/missionChrome.ts \
  src/ResQ.Viz.Web/client/__tests__/scenarioRuntime.test.ts \
  src/ResQ.Viz.Web/client/__tests__/consoleResources.test.ts \
  src/ResQ.Viz.Web/client/__tests__/missionPanel.test.ts
git commit -m "feat(client): render authoritative mission state"
```

### Task 9: Add the lazy scenario catalog

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/ScenarioCatalog.ts`
- Create: `src/ResQ.Viz.Web/client/operator/consoleApi.ts`
- Create: `src/ResQ.Viz.Web/client/styles/operator-dialogs.css`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/sceneConfig.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/scenarioCatalog.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/sceneConfig.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts`

- [ ] **Step 1: Write failing catalog interaction tests**

Use a catalog fixture containing `single`, `flood-response`, and unknown `new-preset`:

```ts
const scenarioFixture: ScenarioCatalogResponse = {
  scenarios: [
    { name: 'single', assetCount: 1, domainCounts: { air: 1, ground: 0, surface: 0 }, vehicleClassCounts: { Multirotor: 1 } },
    { name: 'flood-response', assetCount: 8, domainCounts: { air: 3, ground: 3, surface: 2 }, vehicleClassCounts: {} },
    { name: 'alpine-sar', assetCount: 4, domainCounts: { air: 4, ground: 0, surface: 0 }, vehicleClassCounts: { Multirotor: 4 } },
    { name: 'new-preset', assetCount: 2, domainCounts: { air: 0, ground: 2, surface: 0 }, vehicleClassCounts: {} },
  ],
};
const scenarioPresentation = (name: string) => ({
  displayName: name === 'flood-response' ? 'Flood Response' : humanise(name),
  category: name === 'new-preset' ? 'Other' : 'Response',
  purpose: name,
  environment: name === 'alpine-sar' ? 'Alpine' : null,
});
const mount = document.createElement('div');
const trigger = document.createElement('button');
document.body.append(trigger, mount);
const startScenario = vi.fn().mockResolvedValue({
  success: true,
  value: { current: { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 2 } },
});
const confirmReplace = vi.fn().mockReturnValue(false);
const requested = vi.fn();
const catalog = new ScenarioCatalog({
  mount, trigger, scenarios: scenarioFixture,
  presentation: scenarioPresentation,
  startScenario, confirmReplace,
  onRequested: requested,
});

catalog.open({ assetCount: 8, tick: 40, activeName: 'single' });
mount.querySelector<HTMLInputElement>('input[type="search"]')!.value = 'flood';
mount.querySelector<HTMLInputElement>('input[type="search"]')!
  .dispatchEvent(new Event('input', { bubbles: true }));
expect(mount.textContent).toContain('3 Air');
expect(mount.textContent).toContain('3 Ground');
expect(mount.textContent).toContain('2 Surface');
expect(scenarioPresentation('flood-response').environment).toBeNull();
expect(scenarioPresentation('alpine-sar').environment).toBe('Alpine');
mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();
expect(confirmReplace).toHaveBeenCalledOnce();
expect(startScenario).not.toHaveBeenCalled();

confirmReplace.mockReturnValue(true);
mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();
await vi.waitFor(() => expect(startScenario).toHaveBeenCalledWith('flood-response'));
expect(requested).toHaveBeenCalledWith('flood-response');
```

Add a second test whose POST returns a typed 409. Assert `requested` is not called, the previous active name remains in the dialog, and both stable code and detail are visible. Search `new` and assert the unknown preset renders under `Other` with a humanized title. Close and assert `document.activeElement === trigger`.

- [ ] **Step 2: Run catalog tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/scenarioCatalog.test.ts client/__tests__/sceneConfig.test.ts
```

Expected: FAIL because the catalog and v2 scene-config delegation do not exist.

- [ ] **Step 3: Implement the dynamic catalog and v2 route client**

`ScenarioCatalog` directly imports `../styles/operator-dialogs.css`, requires explicit modal mount, catalog data, current inventory/tick, presentation resolver, `start(name)` callback, and close callback. It uses native dialog controls, traps focus, confirms when `assetCount > 0 || tick > 0`, and never activates a mission from HTTP response alone.

Create `operator/consoleApi.ts` for typed scenario catalog/start, asset-profile, and spawn route functions over Chunk 1 `apiGetJson`/`apiPostJson`. Views receive these as callbacks and never construct URLs. In v2 mode, `sceneConfig.applyScenario` validates against loaded server names and calls v2 start without optimistic event dispatch. Legacy mode retains the current v1 path.

- [ ] **Step 4: Run tests, verify lazy import, and commit**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/scenarioCatalog.test.ts \
  client/__tests__/sceneConfig.test.ts \
  client/__tests__/multiDomainWiring.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/ScenarioCatalog.ts \
  src/ResQ.Viz.Web/client/operator/consoleApi.ts \
  src/ResQ.Viz.Web/client/operator/types.ts \
  src/ResQ.Viz.Web/client/styles/operator-dialogs.css \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/editor/sceneConfig.ts \
  src/ResQ.Viz.Web/client/__tests__/scenarioCatalog.test.ts \
  src/ResQ.Viz.Web/client/__tests__/sceneConfig.test.ts \
  src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts
git commit -m "feat(client): add scenario catalog"
```

### Task 10: Add a keyed fleet roster and explicit FleetUi mounts

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/AssetRoster.ts`
- Modify: `src/ResQ.Viz.Web/client/assets/fleetUi.ts`
- Modify: `src/ResQ.Viz.Web/client/assets/AssetFilter.ts`
- Modify: `src/ResQ.Viz.Web/client/assets/AssetPanel.ts`
- Modify: `src/ResQ.Viz.Web/client/styles/assets.css`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/assetRoster.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/fleetUi.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/assetFilter.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/assetPanel.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/panelVisibility.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/appSelectionLifecycle.test.ts`

- [ ] **Step 1: Write failing roster and filter contract tests**

Create fixtures for one asset per domain and one external contact. Assert:

```ts
function rosterAsset(
  id: string,
  domain: AssetDomain,
  over: { agencyId?: string | null } = {},
): SceneAsset {
  return {
    view: {
      id, displayName: id, domain,
      vehicleClass: domain === AssetDomain.Air ? VehicleClass.Multirotor
        : domain === AssetDomain.Ground ? VehicleClass.AckermannRover
        : VehicleClass.SurfaceVessel,
      operationalState: OperationalState.Active,
      freshness: DataFreshness.Fresh,
      position: [0, 0, 0],
    },
    descriptor: { assetId: id, agencyId: over.agencyId ?? null, fleetId: null, vendor: null },
    state: { operationalState: OperationalState.Active, freshness: DataFreshness.Fresh },
  } as unknown as SceneAsset;
}

function rosterTrack(id: string, over: { sourceId: string; classification: TrackClassification }): ExternalTrackState {
  return {
    trackId: id,
    label: id,
    classification: over.classification,
    sources: [{
      sourceId: over.sourceId,
      kind: TrackSourceKind.Radar,
      observedAt: '2026-09-01T00:00:00Z',
      quality: 1,
    }],
  } as unknown as ExternalTrackState;
}

function moveRosterAsset(asset: SceneAsset, position: [number, number, number]): SceneAsset {
  return { ...asset, view: { ...asset.view, position } };
}

const mount = document.createElement('div');
const assets = [
  rosterAsset('air-1', AssetDomain.Air, { agencyId: 'agency-1' }),
  rosterAsset('ground-1', AssetDomain.Ground),
  rosterAsset('surface-1', AssetDomain.Surface),
];
const input: RosterInput = {
  assets,
  contacts: [rosterTrack('track-1', { sourceId: 'radar-1', classification: TrackClassification.Vessel })],
  assetFilter: emptySelection(),
  query: '',
  selected: null,
};
const selectAsset = vi.fn();
const selectTrack = vi.fn();
const roster = new AssetRoster({ mount, selectAsset, selectTrack });
roster.update(input);
const firstAirRow = roster.rowFor('asset', 'air-1');
firstAirRow.focus();
roster.update({ ...input, assets: assets.map(a =>
  a.view.id === 'air-1' ? moveRosterAsset(a, [5, 20, 3]) : a) });
expect(roster.rowFor('asset', 'air-1')).toBe(firstAirRow);
expect(document.activeElement).toBe(firstAirRow);
expect(roster.counts).toEqual({ assetsMatching: 3, contactsMatching: 1 });
```

Search `agency-1` and assert nonmatching asset and contact rows are hidden in the roster while FleetUi's scene-visible asset IDs and the track overlay input remain unchanged. Clear search, apply a ground-only asset facet, and assert the contact row remains because asset facets never filter contacts. Select a filtered/search-hidden asset and assert it is pinned first with `Outside filters`. Select a track row and assert only the track callback fires.

Add AssetFilter tests: All clears domain, Air/Ground/Surface set one token, multi-domain persisted selection displays Custom, and zero-count tabs remain visible. Update FleetUi tests to require and verify `filterMount`, `rosterMount`, and `panelMount` parentage.

- [ ] **Step 2: Run fleet tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/assetRoster.test.ts \
  client/__tests__/fleetUi.test.ts \
  client/__tests__/assetFilter.test.ts \
  client/__tests__/assetPanel.test.ts \
  client/__tests__/panelVisibility.test.ts
```

Expected: FAIL on the missing roster, tabs, and required mounts.

- [ ] **Step 3: Implement the exact RosterInput boundary**

Use the spec's immutable `RosterInput`. Key asset rows by `asset:${id}` and contacts by `track:${id}`. Patch text/classes on existing buttons and remove only missing keys. Preserve scroll and focus. Search fields are exactly the spec fields and never affect scene visibility.

Require all three FleetUi mounts and remove body fallback from production AssetFilter/AssetPanel constructors. AssetFilter remains authoritative across scene, mini-map, and outliner, with domain tabs synchronized to its facet. FleetUi accepts complete assets, contacts, shared selection, and query while exposing both counts and restoring roster focus after close.

- [ ] **Step 4: Wire shared selection and disappeared-entity reconciliation**

Pass shell mounts into dynamic FleetUi and feed complete assets, tracks, and selection on every v2 render. Row clicks use the existing asset/track selection functions. Complete snapshots clear vanished selection through `_deselectAll`, while close restores focus to the origin row or fleet heading.

- [ ] **Step 5: Run fleet/selection tests and commit**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/assetRoster.test.ts \
  client/__tests__/fleetUi.test.ts \
  client/__tests__/assetFilter.test.ts \
  client/__tests__/assetPanel.test.ts \
  client/__tests__/panelVisibility.test.ts \
  client/__tests__/appSelectionLifecycle.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/AssetRoster.ts \
  src/ResQ.Viz.Web/client/assets/fleetUi.ts \
  src/ResQ.Viz.Web/client/assets/AssetFilter.ts \
  src/ResQ.Viz.Web/client/assets/AssetPanel.ts \
  src/ResQ.Viz.Web/client/styles/assets.css \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/assetRoster.test.ts \
  src/ResQ.Viz.Web/client/__tests__/fleetUi.test.ts \
  src/ResQ.Viz.Web/client/__tests__/assetFilter.test.ts \
  src/ResQ.Viz.Web/client/__tests__/assetPanel.test.ts \
  src/ResQ.Viz.Web/client/__tests__/panelVisibility.test.ts \
  src/ResQ.Viz.Web/client/__tests__/appSelectionLifecycle.test.ts
git commit -m "feat(client): add mixed-fleet roster"
```

### Task 11: Replace the drone HUD with mixed-domain counts in v2

**Files:**
- Modify: `src/ResQ.Viz.Web/client/ui/hud.ts`
- Modify: `src/ResQ.Viz.Web/client/index.html`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/hud.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/appSelectionLifecycle.test.ts`

- [ ] **Step 1: Write failing complete-inventory HUD tests**

Mount total, air, ground, surface, selected, and battery elements using their final IDs. Build six minimal `SceneAsset` fixtures with descriptors whose domains are 2 Air, 3 Ground, and 1 Surface. Give the air states 80% and 100% power and non-air states 5% values that must be ignored.

```ts
function hudAsset(id: string, domain: AssetDomain, percentRemaining: number): SceneAsset {
  return {
    view: { id, displayName: id, domain },
    descriptor: { assetId: id, domain },
    state: { power: { percentRemaining } },
  } as unknown as SceneAsset;
}

const assets = [
  hudAsset('air-1', AssetDomain.Air, 80),
  hudAsset('air-2', AssetDomain.Air, 100),
  hudAsset('ground-1', AssetDomain.Ground, 5),
  hudAsset('ground-2', AssetDomain.Ground, 5),
  hudAsset('ground-3', AssetDomain.Ground, 5),
  hudAsset('surface-1', AssetDomain.Surface, 5),
];
const hud = new Hud(document);
hud.updateAssets(assets);
expect(document.getElementById('asset-count')!.textContent).toBe('6');
expect(document.getElementById('air-count')!.textContent).toBe('2');
expect(document.getElementById('ground-count')!.textContent).toBe('3');
expect(document.getElementById('surface-count')!.textContent).toBe('1');
expect(document.getElementById('battery-pct')!.textContent).toBe('90%');
hud.selectAsset('ground-1');
expect(document.getElementById('hud-selected-asset')!.textContent).toContain('ground-1');
```

Call `updateDrones` against the legacy count element and assert the DRN path still works. The test passes the complete inventory directly. No filtered subset parameter exists.

- [ ] **Step 2: Run HUD tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- client/__tests__/hud.test.ts
```

Expected: FAIL because `updateAssets` and domain count elements do not exist.

- [ ] **Step 3: Implement dual v2/v1 HUD paths and commit**

Let `Hud` accept an optional `Document` dependency defaulting to global `document`, so tests and the app resolve the same IDs. Add stable IDs for total and per-domain values. `updateAssets(allAssets)` derives counts from the unfiltered inventory and computes battery only over air assets that report a percentage. Selected copy says Asset in v2. Keep `updateDrones` and legacy `DRN` markup for fallback. Feed v2 before filter narrowing.

Keep live-region ownership in `app.ts`: update its existing throttled announcement from the complete projected inventory and remove drone-only wording. `Hud` does not write the live region, which prevents duplicate speech.

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/hud.test.ts client/__tests__/appSelectionLifecycle.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/ui/hud.ts \
  src/ResQ.Viz.Web/client/index.html \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/hud.test.ts \
  src/ResQ.Viz.Web/client/__tests__/appSelectionLifecycle.test.ts
git commit -m "feat(client): show mixed-domain HUD counts"
```

### Task 12: Add the lazy Spawn Asset dialog

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/SpawnAssetDialog.ts`
- Modify: `src/ResQ.Viz.Web/client/operator/types.ts`
- Modify: `src/ResQ.Viz.Web/client/operator/consoleApi.ts`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/spawnAssetDialog.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts`

- [ ] **Step 1: Write failing profile, heading, and payload tests**

Export and test `headingToEusQuaternion(headingDegrees)`, matching the server's full EUS-from-FLU basis rather than a pure Y-axis yaw. Build a Three.js `Matrix4` whose rows are:

```ts
[
  sinHeading, -cosHeading, 0,
  0,           0,          1,
 -cosHeading, -sinHeading, 0,
]
```

Convert it with `Quaternion.setFromRotationMatrix()` and normalize. Import the existing `WireQuat` from `client/types.ts` and return a plain object of that type, never the Three.js instance. Do not redeclare the wire interface in the operator module.

Test cardinal headings by applying a reconstructed Three.js quaternion to FLU body forward `Vector3(1, 0, 0)` and expecting north `[0, 0, -1]`, east `[1, 0, 0]`, south `[0, 0, 1]`, and west `[-1, 0, 0]`. In every case, apply it to FLU body up `Vector3(0, 0, 1)` and expect EUS up `[0, 1, 0]`. Use `toBeCloseTo` per component because quaternion signs and floating-point roundoff are not string contracts. Also assert `JSON.parse(JSON.stringify(headingToEusQuaternion(270)))` equals `{ x, y, z, w }` and is not an array, matching `QuaternionJsonConverter`.

Supply discovered Multirotor, AckermannRover, and SurfaceVessel profiles. Assert no reserved class appears, domain is read-only, and heading is hidden for Multirotor. For air, hide `displayName`, `model`, `agencyId`, and `fleetId`. Permit only optional ID and vendor because the server rejects the other metadata on air assets.

For SurfaceVessel, show all metadata and heading. Define:

```ts
const expectedHeadingQuaternion = headingToEusQuaternion(270);
```

Submit and assert exact payload:

```ts
expect(spawn).toHaveBeenCalledWith({
  vehicleClass: VehicleClass.SurfaceVessel,
  pose: {
    frame: CoordinateFrame.LocalEus,
    originId: null,
    position: { x: 10, y: -3, z: 20 },
    orientation: expectedHeadingQuaternion,
  },
  assetId: 'usv-new',
  displayName: 'Relief Ferry',
  vendor: null,
  model: null,
  agencyId: 'agency-1',
  fleetId: 'relief',
});
```

Switch to Multirotor, submit, and assert those four unsupported metadata properties are absent and orientation is the all-zero omitted quaternion. Return 201 and assert the dialog calls `onAccepted('usv-new')` but never mutates the roster. Return a typed field problem and assert code/detail. Put profiles in error state and assert trigger disabled plus Retry.

- [ ] **Step 2: Run spawn tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- client/__tests__/spawnAssetDialog.test.ts
```

Expected: FAIL because the dialog and heading helper do not exist.

- [ ] **Step 3: Implement the lazy spawn surface**

`SpawnAssetDialog` directly imports `../styles/operator-dialogs.css`. Spawn uses only discovered profiles and `consoleApi.spawnAsset`. It waits for streamed state after 201. Field visibility and payload construction branch on the discovered domain, not hard-coded class lists. `onAccepted` may show `Awaiting streamed asset state`. It cannot insert into FleetUi.

Import the module only inside the v2 Spawn trigger. Keep legacy Spawn Drone scoped to `ControlPanel`. Update the source-boundary test to require a dynamic import, forbid a static import, and assert the lazy module itself imports the shared dialog stylesheet.

- [ ] **Step 4: Run tests and commit Spawn Asset**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/spawnAssetDialog.test.ts client/__tests__/multiDomainWiring.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/SpawnAssetDialog.ts \
  src/ResQ.Viz.Web/client/operator/types.ts \
  src/ResQ.Viz.Web/client/operator/consoleApi.ts \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/spawnAssetDialog.test.ts \
  src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts
git commit -m "feat(client): add asset spawn dialog"
```

### Task 13: Add the lazy Environment dialog

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/EnvironmentDialog.ts`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/environmentDialog.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts`

- [ ] **Step 1: Write failing callback, breakpoint, and focus tests**

Use this explicit dependency surface:

```ts
const mount = document.createElement('div');
const trigger = document.createElement('button');
document.body.append(trigger, mount);
const applyTerrain = vi.fn().mockResolvedValue({ success: true, value: undefined });
const applyWeather = vi.fn().mockResolvedValue({ success: true, value: undefined });
const viewportWidth = vi.fn().mockReturnValue(759);
const dialog = new EnvironmentDialog({
  mount, trigger, applyTerrain, applyWeather, viewportWidth,
});
```

Open, choose coastal, set steady/8/270, and apply. Assert:

```ts
expect(applyTerrain).toHaveBeenCalledWith('coastal');
expect(applyWeather).toHaveBeenCalledWith({
  mode: 'steady', windSpeed: 8, windDirection: 270,
});
expect(mount.classList.contains('operator-sheet')).toBe(true);
```

Set width to 760, reopen, and assert `operator-modal` replaces `operator-sheet`. Close and assert focus returns to `trigger`. Reject weather with an API failure and assert the modal stays open with failure detail.

- [ ] **Step 2: Run Environment tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- client/__tests__/environmentDialog.test.ts
```

Expected: FAIL because the dialog does not exist.

- [ ] **Step 3: Implement over existing terrain/weather callbacks**

`EnvironmentDialog` directly imports `../styles/operator-dialogs.css` and owns form state only. Refactor app terrain handling into three functions:

```ts
function _applyPresetLocally(key: PresetKey, waterLevelOverride?: number): void;
async function _postPreset(key: PresetKey): Promise<Result<unknown, ApiFailure>>;
async function _switchPresetFromOperator(key: PresetKey): Promise<Result<unknown, ApiFailure>>;
```

The existing scenario path `_switchPreset` calls `_applyPresetLocally` and invokes `_postPreset` once without awaiting, preserving current scenario behavior. The dialog calls `_switchPresetFromOperator`, which awaits `_postPreset`, applies locally only after success, and then calls `_markOperatorOverride()` exactly once. It never wraps `_switchPreset` with a second POST. The app callback owns the override. `EnvironmentDialog` has no override callback.

Factor weather POST into one awaited app callback that sends the exact wire keys `mode`, `windSpeed`, and `windDirection` and calls `_markOperatorOverride()` exactly once after success. `EnvironmentDialog` receives that callback and never marks the override itself. Add a source-wiring assertion that each operator callback contains one `_markOperatorOverride()` call. Use `viewportWidth()` for the approved `<760` sheet boundary.

Import only inside the v2 Environment trigger. Legacy Weather remains in `ControlPanel`. Add dynamic/static import assertions and assert the lazy module imports the shared dialog stylesheet.

- [ ] **Step 4: Run tests and commit Environment**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/environmentDialog.test.ts client/__tests__/multiDomainWiring.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/EnvironmentDialog.ts \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/environmentDialog.test.ts \
  src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts
git commit -m "feat(client): add environment dialog"
```

### Task 14: Run the Chunk 2 operator-surface gate

**Files:**
- Verify only: all Chunk 2 files

- [ ] **Step 1: Run every Chunk 2 focused test**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/operatorShell.test.ts \
  client/__tests__/startupCoordinator.test.ts \
  client/__tests__/scenarioRuntime.test.ts \
  client/__tests__/consoleResources.test.ts \
  client/__tests__/missionPanel.test.ts \
  client/__tests__/scenarioCatalog.test.ts \
  client/__tests__/assetRoster.test.ts \
  client/__tests__/fleetUi.test.ts \
  client/__tests__/assetFilter.test.ts \
  client/__tests__/assetPanel.test.ts \
  client/__tests__/hud.test.ts \
  client/__tests__/spawnAssetDialog.test.ts \
  client/__tests__/environmentDialog.test.ts \
  client/__tests__/controls.test.ts \
  client/__tests__/sceneConfig.test.ts \
  client/__tests__/appSelectionLifecycle.test.ts \
  client/__tests__/multiDomainWiring.test.ts
```

Expected: all named files pass.

- [ ] **Step 2: Run the complete client and production bundle gates**

```bash
set -e
npm --prefix src/ResQ.Viz.Web run typecheck
npm --prefix src/ResQ.Viz.Web test
npm --prefix src/ResQ.Viz.Web run build
entry_js=$(stat -c%s src/ResQ.Viz.Web/wwwroot/assets/index-*.js)
entry_css=$(stat -c%s src/ResQ.Viz.Web/wwwroot/assets/index-*.css)
test "$entry_js" -le 819200
test "$entry_css" -le 53248
```

Expected: all client tests pass, Vite produces one entry JS/CSS pair, JavaScript is at most 819,200 bytes, and CSS is at most 53,248 bytes.

- [ ] **Step 3: Verify chunk hygiene**

```bash
git diff --check HEAD~8..HEAD
git status --short
```

Expected: no whitespace errors and an empty status. The ignored plan remains outside status.

## Chunk 3: Authority, safety, replay, and Editor ownership

Every new DOM-oriented Vitest file in this chunk starts with `// @vitest-environment happy-dom`.

### Task 15: Add one live/replay mutation gate

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/interactionMode.ts`
- Create: `src/ResQ.Viz.Web/client/operator/operatorActions.ts`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Modify: `src/ResQ.Viz.Web/client/controls.ts`
- Modify: `src/ResQ.Viz.Web/client/assets/panelCommands.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/transport.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/gizmo.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/sceneConfig.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/interactionMode.test.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/operatorActionWiring.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/controls.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/transport.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/gizmo.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/sceneConfig.test.ts`

- [ ] **Step 1: Write failing store and controller-boundary tests**

Test the store directly:

```ts
const mode = new InteractionMode();
const states: string[] = [];
mode.subscribe(value => states.push(value));
expect(mode.guard('reset')).toEqual({ success: true, value: undefined });
mode.enterReplay();
expect(mode.guard('reset')).toEqual({
  success: false,
  error: { kind: 'replay', code: 'interaction.replay', action: 'reset' },
});
mode.goLive();
expect(states).toEqual(['live', 'replay', 'live']);
```

For each existing mutation boundary, inject a replay gate, invoke the event or method, and assert its POST/apply callback was not called. Cover simulation start/pause/reset/step/speed, scenario start, legacy command/spawn/fault/weather, mesh-backhaul change, panel command, gizmo/nudge, scene import, terrain/heightmap, and Editor transport reset. Assert camera, layer toggle, filter, selection, and scene export still work in replay.

Create `OperatorActions` with injected effects for pause/resume/reset/step/speed, scenario start, spawn/remove, terrain, weather, heightmap, mesh backhaul, nudge, and DVR server callbacks. Its methods call `InteractionMode.guard` immediately before the injected effect. `operatorActionWiring.test.ts` reads `app.ts`, extracts the production Dvr callbacks plus pointer/keyboard mutator handler bodies, and asserts those bodies call `operatorActions` rather than `apiPost` directly. The raw `/api/sim/mesh/backhaul` URL may appear once in the injected effect construction outside those handlers. Assert no handler body contains it. This source test covers the integration root that cannot be imported under Vitest.

- [ ] **Step 2: Run focused mutation tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/interactionMode.test.ts \
  client/__tests__/operatorActionWiring.test.ts \
  client/__tests__/controls.test.ts \
  client/__tests__/transport.test.ts \
  client/__tests__/gizmo.test.ts \
  client/__tests__/sceneConfig.test.ts
```

Expected: FAIL because no shared mutation gate exists.

- [ ] **Step 3: Implement and inject the gate at controller boundaries**

`InteractionMode` owns `live | replay`, immediate subscriptions, `guard(action)`, `enterReplay`, and `goLive`. Replay rejection is a local typed failure, separate from `ApiFailure`, because no network request occurred. `OperatorActions` is the testable production boundary for `app.ts`. `editor/transport.ts` is compatibility code, not evidence that the active Dvr bar is guarded.

Every mutator checks the injected gate immediately before its effect. Disabled buttons mirror the gate but are not the security boundary. Centralize app callbacks for terrain, weather, heightmap, scenario, spawn/remove, and simulation transport so keyboard and pointer paths call the same guarded function.

- [ ] **Step 4: Run tests and commit the mutation gate**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/interactionMode.test.ts \
  client/__tests__/operatorActionWiring.test.ts \
  client/__tests__/controls.test.ts \
  client/__tests__/transport.test.ts \
  client/__tests__/gizmo.test.ts \
  client/__tests__/sceneConfig.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/interactionMode.ts \
  src/ResQ.Viz.Web/client/operator/operatorActions.ts \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/controls.ts \
  src/ResQ.Viz.Web/client/assets/panelCommands.ts \
  src/ResQ.Viz.Web/client/editor/transport.ts \
  src/ResQ.Viz.Web/client/editor/gizmo.ts \
  src/ResQ.Viz.Web/client/editor/sceneConfig.ts \
  src/ResQ.Viz.Web/client/__tests__/interactionMode.test.ts \
  src/ResQ.Viz.Web/client/__tests__/operatorActionWiring.test.ts \
  src/ResQ.Viz.Web/client/__tests__/controls.test.ts \
  src/ResQ.Viz.Web/client/__tests__/transport.test.ts \
  src/ResQ.Viz.Web/client/__tests__/gizmo.test.ts \
  src/ResQ.Viz.Web/client/__tests__/sceneConfig.test.ts
git commit -m "feat(client): gate mutations during replay"
```

### Task 16: Add the control-authority store and authority-aware commands

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/controlAuthorityStore.ts`
- Modify: `src/ResQ.Viz.Web/client/operator/types.ts`
- Modify: `src/ResQ.Viz.Web/client/operator/consoleApi.ts`
- Modify: `src/ResQ.Viz.Web/client/assets/panelCommands.ts`
- Modify: `src/ResQ.Viz.Web/client/assets/AssetPanel.ts`
- Modify: `src/ResQ.Viz.Web/client/assets/fleetUi.ts`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/controlAuthorityStore.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/panelCommands.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/assetPanel.test.ts`

- [ ] **Step 1: Write failing authority-store tests with controlled time**

Use injected `now`, holder loader, and scheduler:

```ts
let now = Date.parse('2026-09-01T12:00:00Z');
const loadHolder = vi.fn();
const scheduled: Array<{ callback: () => void; delayMs: number; cancelled: boolean }> = [];
const store = new ControlAuthorityStore({
  holderId: createConsoleIdentity('room-1', () => 'tab-7'),
  now: () => now,
  loadMode: vi.fn().mockResolvedValue(simulationOnlyMode),
  loadHolder,
  schedule: (callback, delayMs) => {
    const timer = { callback, delayMs, cancelled: false };
    scheduled.push(timer);
    return timer;
  },
  cancel: timer => { timer.cancelled = true; },
});
```

Cover these exact transitions:

- `select('uav-1')` plus uncontrolled response yields `uncontrolled` and command context `{ issuerId: 'room-1:tab-7', controlLeaseId: null }`.
- A lease for this holder yields `heldByConsole` and passes its ID.
- A lease for another holder yields `heldByOther` and disables commands with holder/expiry reason.
- Start request A, select another asset, resolve A, and assert A cannot repaint the second selection.
- Return a self-held lease expiring in 5,000 ms. Assert one timer with `delayMs === 5000`. Advance `now`, invoke that timer callback twice, and assert commands disable plus exactly one holder reload. Selecting another asset or `dispose()` must cancel the old timer.
- Call `invalidateFromFailure('authority.leasePreempted')`, assert loading plus one reload.
- `authority.*` and `control.*` invalidate, while unrelated capability problems do not.
- Network and timeout failures produce distinct `AuthorityState.error.failure` variants.
- `createConsoleIdentity('room-1', () => 'tab-a')` differs from a second call returning `tab-b` and neither value is read from localStorage.
- A requested 300-second lease whose response is clamped uses the returned lease expiry for its timer, never `durationSeconds` from the request.

- [ ] **Step 2: Write failing request-envelope and panel tests**

Extend `panelCommands.test.ts` to issue a command with this authority context and assert exact request fields:

```ts
expect(posted).toMatchObject({
  kind: 'goTo',
  issuerId: 'room-1:tab-7',
  controlLeaseId: 'lease-7',
});
```

Define the transport result as:

```ts
export type CommandOutcome =
  | { readonly accepted: true; readonly message: string; readonly result: CommandResult }
  | { readonly accepted: false; readonly message: string; readonly failure: ApiFailure };
```

Assert uncontrolled sends issuer with `controlLeaseId: null`, held-by-other sends nothing, and replay sends nothing. Return the real authority problem shape with top-level `code: 'authority.notHolder'` and `reasonCode: null`. Assert the outcome retains it, renders `reasonCode ?? code`, and invalidates the store before the panel re-enables. Add a separate synthetic problem with `code: 'command.rejected'` and `reasonCode: 'link.unreachable'` to prove reason-code precedence. Keep capability/state/freshness gates independent.

- [ ] **Step 3: Run authority tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/controlAuthorityStore.test.ts \
  client/__tests__/panelCommands.test.ts \
  client/__tests__/assetPanel.test.ts
```

Expected: FAIL because authority state and request fields are not integrated.

- [ ] **Step 4: Implement store, route clients, and panel composition**

Generate a per-page opaque ID with `createConsoleIdentity(roomId, uuid = crypto.randomUUID)`, for example `room-1:tab-7`. Two tabs in one room receive different IDs. Keep it only in memory for that page session and label it `This console`. Never imply a human login. `consoleApi.ts` adds mode, holder, acquire, renew, release, and preempt calls.

Transcribe the missing wire types into `operator/types.ts`:

```ts
export const ControlRole = { Unspecified: 0, Operator: 1, Emergency: 2 } as const;
export type ControlRole = (typeof ControlRole)[keyof typeof ControlRole];
export const ControlLeaseEndReason = {
  Unspecified: 0, Released: 1, Expired: 2, Preempted: 3,
  AssetRemoved: 4, AuthorityReset: 5,
} as const;
export type ControlLeaseEndReason =
  (typeof ControlLeaseEndReason)[keyof typeof ControlLeaseEndReason];

export interface ControlLease {
  readonly leaseId: string;
  readonly assetId: string;
  readonly assetInstanceId: string;
  readonly holderId: string;
  readonly role: ControlRole;
  readonly issuedAt: string;
  readonly expiresAt: string;
  readonly lastRenewedAt: string | null;
  readonly endedAt: string | null;
  readonly endReason: ControlLeaseEndReason | null;
}

export interface ControlHolderResponse {
  readonly assetId: string;
  readonly isControlled: boolean;
  readonly lease: ControlLease | null;
}

export interface ControlLeaseResponse {
  readonly lease: ControlLease;
  readonly requestedDurationSeconds: number;
  readonly grantedDurationSeconds: number;
  readonly durationClamped: boolean;
}

export interface ControlModeStatus {
  readonly mode: string;
  readonly liveControlAvailable: boolean;
  readonly detail: string;
}

export const CommandState = {
  Requested: 0, Accepted: 1, Rejected: 2, InProgress: 3,
  Succeeded: 4, Failed: 5, Cancelled: 6, TimedOut: 7,
} as const;
export type CommandState = (typeof CommandState)[keyof typeof CommandState];

export interface CommandResult {
  readonly commandId: string;
  readonly state: CommandState;
  readonly acceptedAt: string | null;
  readonly progressPercent: number;
  readonly message: string | null;
  readonly reasonCode: string | null;
}

export interface CommandAuditRecord {
  readonly sequence: number;
  readonly decision: number;
  readonly at: string;
  readonly correlationId: string;
  readonly assetId: string;
  readonly commandId: string | null;
  readonly kind: string | null;
  readonly issuerId: string;
  readonly leaseId: string | null;
  readonly reasonCode: string | null;
  readonly detail: string | null;
}

export interface ControlAuditRecord {
  readonly sequence: number;
  readonly kind: number;
  readonly at: string;
  readonly observedAt: string;
  readonly assetId: string;
  readonly leaseId: string | null;
  readonly holderId: string | null;
  readonly actorId: string | null;
  readonly endReason: ControlLeaseEndReason | null;
  readonly denialCode: string | null;
  readonly justification: string | null;
}

export interface CommandAuditResponse {
  readonly decisions: readonly CommandAuditRecord[];
  readonly leases: readonly ControlAuditRecord[];
  readonly droppedDecisionCount: number;
  readonly droppedLeaseCount: number;
}
```

The store loads mode after v2 activation, then reloads holder state on selection, reconnect, visibility return, expiry, and mutations. Every response is guarded by selected ID and generation. Successful lease bodies update immediately before GET confirmation; `authority.*` and `control.*` failures invalidate and refresh.

Add `issuerId` and `controlLeaseId` to `AssetCommandRequestBody`. Change `postAssetCommand` to `apiPostJson<CommandResult>` and return the discriminated `CommandOutcome`, preserving its `ApiFailure`. AssetPanel combines capability/state gates with authority and interaction gates and renders the first blocking reason. Only when `failure.kind === 'problem'` does it extract `failure.problem.reasonCode ?? failure.problem.code` for authority invalidation. Network and timeout failures never enter prefix matching.

- [ ] **Step 5: Run authority tests, typecheck, and commit**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/controlAuthorityStore.test.ts \
  client/__tests__/panelCommands.test.ts \
  client/__tests__/assetPanel.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/controlAuthorityStore.ts \
  src/ResQ.Viz.Web/client/operator/types.ts \
  src/ResQ.Viz.Web/client/operator/consoleApi.ts \
  src/ResQ.Viz.Web/client/assets/panelCommands.ts \
  src/ResQ.Viz.Web/client/assets/AssetPanel.ts \
  src/ResQ.Viz.Web/client/assets/fleetUi.ts \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/controlAuthorityStore.test.ts \
  src/ResQ.Viz.Web/client/__tests__/panelCommands.test.ts \
  src/ResQ.Viz.Web/client/__tests__/assetPanel.test.ts
git commit -m "feat(client): add control authority state"
```

### Task 17: Add the lazy Advanced/Safety workspace

**Files:**
- Create: `src/ResQ.Viz.Web/client/operator/advancedSafety.ts`
- Create: `src/ResQ.Viz.Web/client/operator/ControlLeasePanel.ts`
- Create: `src/ResQ.Viz.Web/client/operator/LinkDrillPanel.ts`
- Create: `src/ResQ.Viz.Web/client/operator/TrackReportPanel.ts`
- Create: `src/ResQ.Viz.Web/client/operator/AuditPanel.ts`
- Create: `src/ResQ.Viz.Web/client/styles/advancedSafety.css`
- Modify: `src/ResQ.Viz.Web/client/operator/OperatorShell.ts`
- Modify: `src/ResQ.Viz.Web/client/index.html`
- Modify: `src/ResQ.Viz.Web/client/operator/consoleApi.ts`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/advancedSafety.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts`

- [ ] **Step 1: Write failing composition and selected-generation tests**

Mount the lazy workspace with a shared `SelectionStore`, `ControlAuthorityStore`, `InteractionMode`, and mocked API. Assert initial disclosure is collapsed. Expand on `uav-1` and verify holder/link GETs use that ID. Select `ugv-1`, resolve delayed UAV responses, and assert no UAV text appears.

Authority acquire, renew, and release payloads use this-console identity, while preemption also requires Emergency role, justification, and confirmation. Cancelling a link cut sends nothing. An accepted cut displays `Request accepted. Awaiting published asset state` until the selected asset streams its update; restore needs no confirmation.

Use exact payload assertions:

```ts
expect(api.acquire).toHaveBeenCalledWith('uav-1', {
  holderId: consoleId, role: ControlRole.Operator, durationSeconds: 300,
});
expect(api.renew).toHaveBeenCalledWith('uav-1', {
  holderId: consoleId, leaseId: 'lease-1', durationSeconds: 300,
});
expect(api.release).toHaveBeenCalledWith('uav-1', {
  holderId: consoleId, leaseId: 'lease-1',
});
expect(api.preempt).toHaveBeenCalledWith('uav-1', {
  holderId: consoleId,
  role: ControlRole.Emergency,
  justification: 'Immediate safety recovery',
  durationSeconds: 300,
});
expect(api.setLink).toHaveBeenNthCalledWith(1, 'uav-1', {
  available: false, issuerId: consoleId, reason: 'Loss-of-link drill',
});
expect(api.setLink).toHaveBeenNthCalledWith(2, 'uav-1', {
  available: true, issuerId: consoleId, reason: 'Restore after drill',
});
```

Submit an external track and assert `Simulation-only external report` plus this exact body:

```ts
expect(api.reportTrack).toHaveBeenCalledWith({
  trackId: 'browser-track-1',
  pose: {
    frame: CoordinateFrame.LocalEus,
    originId: null,
    position: { x: 150, y: -3, z: 120 },
    orientation: { x: 0, y: 0, z: 0, w: 0 },
  },
  twist: null,
  classification: TrackClassification.Vessel,
  sourceId: 'operator-console',
  sourceKind: TrackSourceKind.OperatorEntered,
  sourceQuality: 0.9,
  confidence: 0.9,
  observedAtSimulationTimeSeconds: 42.5,
  positionAccuracyM: null,
  velocityAccuracyMps: null,
  label: 'Browser contact',
  transponder: null,
});
```

Load audit and assert decisions, leases, `droppedDecisionCount`, and `droppedLeaseCount`. Enter replay and assert every mutation button disables while audit remains readable.

- [ ] **Step 2: Run Advanced/Safety tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- client/__tests__/advancedSafety.test.ts
```

Expected: FAIL because the lazy workspace and panels do not exist.

- [ ] **Step 3: Implement four focused panels behind one lazy entry**

Add the collapsed summary/button to `index.html`, with `OperatorShell` owning its expanded state and first-import callback because the trigger must predate its lazy module. First expansion loads `advancedSafety.ts` and its CSS into the designated mount. The composed panels receive selection and generation without retaining another selected ID. Define the streamed update port:

```ts
interface AdvancedFrameInput {
  readonly selectedId: string | null;
  readonly selectionGeneration: number;
  readonly selectedState: AssetState | null;
  readonly simulationTimeSeconds: number;
}
```

Call `updateFrame(input)` from every v2 render. LinkDrillPanel accepts link state only for its current ID/generation and clears on selection change. The remaining panels consume shared authority, stamp visible simulation time, or render read-only audit data with truncation counts.

Extend `consoleApi.ts` with link, track, and audit calls. Import the workspace only on first disclosure. Add a source-boundary assertion for dynamic import and direct CSS import.

- [ ] **Step 4: Run tests and commit Advanced/Safety**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/advancedSafety.test.ts client/__tests__/multiDomainWiring.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/operator/advancedSafety.ts \
  src/ResQ.Viz.Web/client/operator/ControlLeasePanel.ts \
  src/ResQ.Viz.Web/client/operator/LinkDrillPanel.ts \
  src/ResQ.Viz.Web/client/operator/TrackReportPanel.ts \
  src/ResQ.Viz.Web/client/operator/AuditPanel.ts \
  src/ResQ.Viz.Web/client/styles/advancedSafety.css \
  src/ResQ.Viz.Web/client/operator/OperatorShell.ts \
  src/ResQ.Viz.Web/client/index.html \
  src/ResQ.Viz.Web/client/operator/consoleApi.ts \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/advancedSafety.test.ts \
  src/ResQ.Viz.Web/client/__tests__/multiDomainWiring.test.ts
git commit -m "feat(client): expose advanced safety controls"
```

### Task 18: Record and replay complete mixed-domain snapshots

**Files:**
- Modify: `src/ResQ.Viz.Web/client/editor/recorder.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/dvr.ts`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/recorder.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/dvr.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/editorSuiteWiring.test.ts`

- [ ] **Step 1: Write failing tagged-recorder tests**

Use `v1(frame)` and `v2(snapshot)` builders. Assert:

Define `sceneSnapshot(index: number, over: Partial<SceneSnapshot> = {}): SceneSnapshot` in the test and merge `over` last, so scenario replacement and explicit null are representable.

```ts
const recorder = new FrameRecorder();
const scenario1 = { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 1 };
for (let i = 0; i < 181; i++) {
  recorder.capture(v2(sceneSnapshot(i, { scenario: scenario1 })));
}
expect(recorder.length).toBe(180);
expect(recorder.frameAt(0)?.kind).toBe('v2');
recorder.capture(v2(sceneSnapshot(182, {
  scenario: { name: 'coastal-search', startedAtSimulationSeconds: 18.2, revision: 2 },
})));
expect(recorder.length).toBe(1);
recorder.capture(v2(sceneSnapshot(183, { scenario: null })));
expect(recorder.length).toBe(1);
expect((recorder.frameAt(0) as { kind: 'v2'; snapshot: SceneSnapshot }).snapshot.scenario)
  .toBeNull();
recorder.capture(v1(vizFrame()));
expect(recorder.length).toBe(1);
expect(recorder.frameAt(0)?.kind).toBe('v1');
```

Assert v1 capacity remains 3,000. In DVR tests, scrub a v2 frame containing ground, surface, and track entries and assert `onApply` receives that complete snapshot. Enter replay and assert interaction mode changes plus Reset disables.

Inject `getLatestLiveFrame: () => RecordedFrame | null` into Dvr. Freeze the recorder at frame 2, make the provider return live frame 9, click Go Live, and assert `onApply` receives frame 9 before `onRefreshLiveResources` runs. This provider reads `app.ts`'s newest held v1 frame or `_lastSnapshot`, which continue updating while recording is frozen.

- [ ] **Step 2: Run recorder/DVR tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/recorder.test.ts client/__tests__/dvr.test.ts
```

Expected: FAIL because the recorder accepts only v1 frames.

- [ ] **Step 3: Implement mode-tagged capacity and replay application**

Define:

```ts
type RecordedFrame =
  | { readonly kind: 'v1'; readonly frame: VizFrame }
  | { readonly kind: 'v2'; readonly snapshot: SceneSnapshot };
```

Recorder clears on kind change, active-scenario revision replacement, and transition from a named scenario to explicit `null`. It uses 3,000 v1 or 180 v2 slots and exposes actual oldest/newest time. DVR publishes interaction changes, applies the tagged union, disables server Reset in replay, and uses `getLatestLiveFrame` rather than the frozen ring when returning Live.

After the v2 latch, stop recording v1 and capture projected snapshots only after full/delta reconstruction. Replay the tagged record through the matching render path with `snap=true`, excluding scenario, environment, and intro effects. Go Live applies the newest held state before refreshing mission resources and authority.

- [ ] **Step 4: Run DVR tests, wiring tests, and commit**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/recorder.test.ts \
  client/__tests__/dvr.test.ts \
  client/__tests__/editorSuiteWiring.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/editor/recorder.ts \
  src/ResQ.Viz.Web/client/editor/dvr.ts \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/recorder.test.ts \
  src/ResQ.Viz.Web/client/__tests__/dvr.test.ts \
  src/ResQ.Viz.Web/client/__tests__/editorSuiteWiring.test.ts
git commit -m "feat(client): replay mixed-domain snapshots"
```

### Task 19: Put all authoring surfaces behind the Editor workspace

**Files:**
- Create: `src/ResQ.Viz.Web/client/editor/workspace.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/dock.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/gizmo.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/sceneConfig.ts`
- Modify: `src/ResQ.Viz.Web/client/editor/dvr.ts`
- Modify: `src/ResQ.Viz.Web/client/cameraMode.ts`
- Modify: `src/ResQ.Viz.Web/client/sensors/fpvOsd.ts`
- Modify: `src/ResQ.Viz.Web/client/sensors/onboardPip.ts`
- Create: `src/ResQ.Viz.Web/client/styles/operator-overlays.css`
- Modify: `src/ResQ.Viz.Web/client/styles/editor.css`
- Modify: `src/ResQ.Viz.Web/client/app.ts`
- Create: `src/ResQ.Viz.Web/client/__tests__/editorWorkspace.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/editorSuiteWiring.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/gizmo.test.ts`
- Modify: `src/ResQ.Viz.Web/client/__tests__/sceneConfig.test.ts`

- [ ] **Step 1: Write failing visibility, breakpoint, inert, and focus tests**

Build shell mounts with a toggle, rail, context, and editor layer. Inject `viewportWidth`. At 1,200 px assert Editor starts hidden, opens as dock, preserves shared selection, and closes back to toggle focus. At 900 px assert opening closes and inerts rail/context, focuses Editor close, uses full-screen class, then restores prior rail state and toggle focus. At 759 px assert toggle disabled with `Desktop workspace required`.

Close/reopen and assert page-session inspector/outliner state remains. Enter replay and assert gizmo/nudge/import disable while scene export and inspector remain available. Assert no whole-workspace localStorage read/write.

Add a source-boundary case that `editor/workspace.ts` has no static import of `editor.css`, dock, outliner, inspector, gizmo, or sceneConfig. Assert DVR, camera mode, FPV OSD, and onboard PiP import `operator-overlays.css` rather than `editor.css`.

- [ ] **Step 2: Run Editor tests and confirm they fail**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/editorWorkspace.test.ts \
  client/__tests__/editorSuiteWiring.test.ts \
  client/__tests__/gizmo.test.ts \
  client/__tests__/sceneConfig.test.ts
```

Expected: FAIL because the workspace owner does not exist and dock defaults open.

- [ ] **Step 3: Implement one workspace owner and lazy authoring initialization**

`EditorWorkspace` consumes shell toggle/mount/rail/context ports, shared selection, interaction mode, and viewport width. `OperatorShell` remains the sole visibility state owner. EditorWorkspace calls `shell.setEditorOpen(...)` and reads `shell.editorOpen`, storing no second open flag. It orchestrates default-off responsive focus/inert behavior. Remove the whole-dock hamburger and persisted dock-collapse state. Section disclosures may remain.

Keep recorder/DVR, camera mode, FPV OSD, and onboard PiP initialization after paint and independent of Editor. Split their CSS rules into `operator-overlays.css` and update those modules to import it. Load only dock, outliner, inspector, gizmo, scene import/export, and authoring-only `editor.css` on first Editor open, then hide rather than destroy them on close. Dock accepts explicit workspace mount. Gizmo and import use the mutation gate. Export is local and available in replay.

- [ ] **Step 4: Run Editor tests, typecheck, and commit**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/editorWorkspace.test.ts \
  client/__tests__/editorSuiteWiring.test.ts \
  client/__tests__/gizmo.test.ts \
  client/__tests__/sceneConfig.test.ts
npm --prefix src/ResQ.Viz.Web run typecheck
git add \
  src/ResQ.Viz.Web/client/editor/workspace.ts \
  src/ResQ.Viz.Web/client/editor/dock.ts \
  src/ResQ.Viz.Web/client/editor/gizmo.ts \
  src/ResQ.Viz.Web/client/editor/sceneConfig.ts \
  src/ResQ.Viz.Web/client/editor/dvr.ts \
  src/ResQ.Viz.Web/client/cameraMode.ts \
  src/ResQ.Viz.Web/client/sensors/fpvOsd.ts \
  src/ResQ.Viz.Web/client/sensors/onboardPip.ts \
  src/ResQ.Viz.Web/client/styles/operator-overlays.css \
  src/ResQ.Viz.Web/client/styles/editor.css \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/__tests__/editorWorkspace.test.ts \
  src/ResQ.Viz.Web/client/__tests__/editorSuiteWiring.test.ts \
  src/ResQ.Viz.Web/client/__tests__/gizmo.test.ts \
  src/ResQ.Viz.Web/client/__tests__/sceneConfig.test.ts
git commit -m "feat(client): gate authoring behind Editor"
```

### Task 20: Run the Chunk 3 operational-state gate

**Files:**
- Verify only: all Chunk 3 files

- [ ] **Step 1: Run all focused authority, safety, replay, and Editor tests**

```bash
npm --prefix src/ResQ.Viz.Web test -- \
  client/__tests__/interactionMode.test.ts \
  client/__tests__/operatorActionWiring.test.ts \
  client/__tests__/controlAuthorityStore.test.ts \
  client/__tests__/advancedSafety.test.ts \
  client/__tests__/panelCommands.test.ts \
  client/__tests__/assetPanel.test.ts \
  client/__tests__/recorder.test.ts \
  client/__tests__/dvr.test.ts \
  client/__tests__/editorWorkspace.test.ts \
  client/__tests__/controls.test.ts \
  client/__tests__/transport.test.ts \
  client/__tests__/gizmo.test.ts \
  client/__tests__/sceneConfig.test.ts \
  client/__tests__/editorSuiteWiring.test.ts
```

Expected: all named files pass.

- [ ] **Step 2: Run complete client and bundle gates**

```bash
set -e
npm --prefix src/ResQ.Viz.Web run typecheck
npm --prefix src/ResQ.Viz.Web test
npm --prefix src/ResQ.Viz.Web run build
entry_js=$(stat -c%s src/ResQ.Viz.Web/wwwroot/assets/index-*.js)
entry_css=$(stat -c%s src/ResQ.Viz.Web/wwwroot/assets/index-*.css)
test "$entry_js" -le 819200
test "$entry_css" -le 53248
```

Expected: full client suite passes and entry budgets remain unchanged.

- [ ] **Step 3: Verify chunk hygiene**

```bash
git diff --check HEAD~5..HEAD
git status --short
```

Expected: no whitespace errors and empty status.

## Chunk 4: Real-browser reachability, memory, CI, and final verification

### Task 21: Add an environment-gated forced-legacy browser seam

**Files:**
- Create: `src/ResQ.Viz.Web/Services/BrowserVerificationMode.cs`
- Modify: `src/ResQ.Viz.Web/Program.cs`
- Modify: `src/ResQ.Viz.Web/Hubs/VizHub.cs`
- Modify: `src/ResQ.Viz.Web/Hubs/VizHub.Deltas.cs`
- Create: `tests/ResQ.Viz.Web.Tests/BrowserVerificationModeTests.cs`
- Modify: `tests/ResQ.Viz.Web.Tests/VizHubTests.cs`

- [ ] **Step 1: Write failing policy and hub tests**

Test the policy matrix:

```csharp
[Theory]
[InlineData("Production", true, false)]
[InlineData("Development", true, false)]
[InlineData("BrowserVerification", false, false)]
[InlineData("BrowserVerification", true, true)]
public void RejectV2_Requires_Both_Environment_And_Flag(
    string environment, bool configured, bool expected)
{
    BrowserVerificationMode.Resolve(environment, configured).RejectV2Subscriptions
        .Should().Be(expected);
}
```

Bind a hub to a room with enabled mode. Assert `SubscribeSnapshots(true)` and `SubscribeDeltas(true)` throw `HubException`, calls with `false` do not throw, room v1 group membership remains, and v2 subscriber counts stay zero. Repeat with disabled mode and assert positive subscriptions still work.

- [ ] **Step 2: Run policy/hub tests and confirm they fail**

```bash
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~BrowserVerificationModeTests|FullyQualifiedName~VizHubTests'
```

Expected: FAIL because the policy and hub gate do not exist.

- [ ] **Step 3: Implement a non-production-switchable policy**

`BrowserVerificationMode` is enabled only when:

```csharp
environment.IsEnvironment("BrowserVerification")
&& configuration.GetValue<bool>("BrowserVerification:RejectV2Subscriptions")
```

Register it as a singleton. Inject it as an optional final `VizHub` constructor parameter defaulting disabled, preserving direct test constructions. Throw `HubException` before group/subscriber changes for positive v2 snapshot/delta opt-ins. Negative unsubscribe calls remain allowed. No query string, cookie, or Production/Development configuration can enable the seam.

- [ ] **Step 4: Run focused tests and commit**

```bash
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~BrowserVerificationModeTests|FullyQualifiedName~VizHubTests'
git add \
  src/ResQ.Viz.Web/Services/BrowserVerificationMode.cs \
  src/ResQ.Viz.Web/Program.cs \
  src/ResQ.Viz.Web/Hubs/VizHub.cs \
  src/ResQ.Viz.Web/Hubs/VizHub.Deltas.cs \
  tests/ResQ.Viz.Web.Tests/BrowserVerificationModeTests.cs \
  tests/ResQ.Viz.Web.Tests/VizHubTests.cs
git commit -m "test(server): add forced legacy verification mode"
```

### Task 22: Add Playwright operator-console reachability tests

**Files:**
- Create: `src/ResQ.Viz.Web/playwright.config.ts`
- Create: `src/ResQ.Viz.Web/e2e/support/operatorConsole.ts`
- Create: `src/ResQ.Viz.Web/e2e/operator-console.spec.ts`
- Create: `src/ResQ.Viz.Web/scripts/run-browser-tests.sh`
- Modify: `src/ResQ.Viz.Web/package.json`
- Modify: `src/ResQ.Viz.Web/package-lock.json`
- Modify: `src/ResQ.Viz.Web/tsconfig.json`
- Modify: `src/ResQ.Viz.Web/knip.json`
- Modify: `src/ResQ.Viz.Web/.fallowrc.jsonc`
- Modify: `.gitignore`

- [ ] **Step 1: Install and register Playwright**

Run:

```bash
cd src/ResQ.Viz.Web
npm install --save-dev @playwright/test@latest
npx playwright install chromium
```

Add scripts:

```json
"test:browser": "bash scripts/run-browser-tests.sh"
```

`run-browser-tests.sh` starts with `set -Eeuo pipefail`. If `RESQ_BROWSER_PFX` names an existing file and the password is set, it reuses them. Otherwise it creates a `mktemp -d` directory, exports a development PFX and password, installs an EXIT trap that removes only that temporary directory, runs `npx playwright test "$@"`, and cleans up through the trap. Environment changes remain inside the npm child process.

Include the E2E files and Playwright config in TypeScript, register them as tool entry points, and ignore all generated report/result directories.

- [ ] **Step 2: Configure two real HTTPS app servers**

Use Chromium only, `workers: 1`, `fullyParallel: false`, `forbidOnly` in CI, failure-only traces/screenshots/video, `ignoreHTTPSErrors: true`, and two `webServer` entries. Both run:

```bash
dotnet run --project ResQ.Viz.Web.csproj --configuration Debug --no-build
```

Set these exact environment keys on the normal server:

```text
ASPNETCORE_ENVIRONMENT=BrowserVerification
Kestrel__Endpoints__Http__Url=http://127.0.0.1:5100
Kestrel__Endpoints__Https__Url=https://127.0.0.1:5101
Kestrel__Certificates__Default__Path=<RESQ_BROWSER_PFX>
Kestrel__Certificates__Default__Password=<RESQ_BROWSER_PFX_PASSWORD>
BrowserVerification__RejectV2Subscriptions=false
```

`playwright.config.ts` reads the certificate values from `process.env.RESQ_BROWSER_PFX` and `process.env.RESQ_BROWSER_PFX_PASSWORD` and maps them to the Kestrel keys above. The forced server uses 5200/5201 and sets the reject flag to `true`. Use SIGTERM graceful shutdown after 5 seconds. Do not use `ASPNETCORE_URLS`. Explicit `Kestrel:Endpoints` in appsettings take precedence.

- [ ] **Step 3: Write the desktop reachability test**

Before navigation, install a `MutationObserver` through `page.addInitScript` that records any moment `#legacy-console` becomes visible. On the normal server assert it never did.

At 1440×900, wait for v2 and Flood Response. Assert stable rows `fr-mapper-n`, `fr-supply-lead`, and `fr-ferry-1`. For rail and roster, assert nonzero bounding boxes, rail z-index above scene, and `document.elementFromPoint` at row center resolves inside that row. Use a fresh browser context for this case.

Click each domain row and assert body-level context shows matching ID/domain. Assert Advanced/Safety and Editor start closed, then open only through their labeled controls. Inject `browser-track-1` through the browser context request API with the page's room cookie and assert it appears under Observed contacts.

Scrub away from Live. Assert `REPLAY`, all domains/contact remain, and Reset, scenario, spawn, Environment, selected command, lease, link, and track-report mutations are disabled. Go Live and assert they recover after resource refresh.

- [ ] **Step 4: Write narrow and forced-legacy tests**

At 390×844, assert rail is an in-viewport drawer. Select `fr-ferry-1`. Assert rail closes/inerts and context opens as bottom sheet. Repeated Tab presses must never focus an inert/hidden subtree. Closing context restores row focus. Editor is disabled with `Desktop workspace required`. Primary targets measure at least 44×44 CSS pixels.

On the forced server, use a separate browser context so secure localhost cookies do not cross ports. Assert visible `Legacy mode: v2 unavailable`, operable air controls, hidden/inert v2 branch, `single` startup, and at least one rendered v1 frame. The connection must stay up. Only v2 subscriptions fail.

- [ ] **Step 5: Build, run browser tests, and commit**

```bash
cd src/ResQ.Viz.Web
npm run typecheck
npm test
npm run build
npm run test:browser -- --grep-invert 'DVR retained heap'
cd ../..
git add \
  src/ResQ.Viz.Web/playwright.config.ts \
  src/ResQ.Viz.Web/e2e/support/operatorConsole.ts \
  src/ResQ.Viz.Web/e2e/operator-console.spec.ts \
  src/ResQ.Viz.Web/scripts/run-browser-tests.sh \
  src/ResQ.Viz.Web/package.json \
  src/ResQ.Viz.Web/package-lock.json \
  src/ResQ.Viz.Web/tsconfig.json \
  src/ResQ.Viz.Web/knip.json \
  src/ResQ.Viz.Web/.fallowrc.jsonc \
  .gitignore
git commit -m "test(browser): cover operator console reachability"
```

Expected: client suite/build and desktop, narrow, and forced-legacy browser cases pass.

### Task 23: Enforce mixed-domain DVR retained heap

**Files:**
- Create: `src/ResQ.Viz.Web/e2e/dvr-heap.spec.ts`
- Modify: `src/ResQ.Viz.Web/e2e/support/operatorConsole.ts`
- Modify: `src/ResQ.Viz.Web/package.json`

- [ ] **Step 1: Write the 150-asset heap test**

Set a 60-second timeout. Load a fresh normal room, wait for v2/DVR, and start `mixed-load-150` through the v2 scenario route. Wait for 150 assets and 50/50/50 counts. Inject `browser-track-1`. Assert the scenario revision cleared the previous ring.

Observe the DVR count through the UI during the scenario-clear transition. Once 150 rows and the injected contact are visible, enter replay immediately, read `baselineCount`, and assert `baselineCount > 0 && baselineCount < 180`. This freezes a known baseline even if several broadcasts arrived while the DOM settled.

Create a CDP session, call `HeapProfiler.collectGarbage`, and record `Runtime.getHeapUsage`. Return Live and wait until DVR count is exactly 180. Collect garbage again and read usage.

Attach this JSON to the test result:

```ts
{
  assetCount: 150,
  baselineCount,
  retainedFrames: 180,
  beforeUsedSize: before.usedSize,
  afterUsedSize: after.usedSize,
  delta: after.usedSize - before.usedSize,
  backingStorageSize: after.backingStorageSize,
  limit: 128 * 1024 * 1024,
}
```

Assert a retained-heap delta below 128 MiB and include `baselineCount` in the failure message. Keep CDP creation inside `try` with detachment in `finally`. Backing storage remains diagnostic only; a failure requires compacting `RecordedFrame`, not raising the limit.

- [ ] **Step 2: Run the heap test twice for stability**

Add the script in the same commit as the file:

```json
"test:browser:heap": "bash scripts/run-browser-tests.sh e2e/dvr-heap.spec.ts"
```

```bash
cd src/ResQ.Viz.Web
npm run test:browser:heap -- --repeat-each=2
```

Expected: both Chromium samples pass under 128 MiB.

- [ ] **Step 3: Commit the heap gate**

```bash
git add \
  src/ResQ.Viz.Web/e2e/dvr-heap.spec.ts \
  src/ResQ.Viz.Web/e2e/support/operatorConsole.ts \
  src/ResQ.Viz.Web/package.json
git commit -m "test(browser): bound mixed-fleet DVR heap"
```

### Task 24: Centralize bundle reporting and add the browser CI job

**Files:**
- Create: `src/ResQ.Viz.Web/scripts/check-bundle-size.mjs`
- Modify: `.github/workflows/ci.yml`
- Modify: `src/ResQ.Viz.Web/package.json`

- [ ] **Step 1: Write the deterministic bundle checker**

The script requires exactly one `wwwroot/assets/index-*.js` and one CSS entry, reads `BUNDLE_JS_BUDGET_BYTES`/`BUNDLE_CSS_BUDGET_BYTES` with unchanged defaults 819,200 and 53,248, and exits nonzero for missing/ambiguous/oversize entries. Print a descending Markdown table for every JS/CSS file, mark entry versus lazy chunks, and print total sizes as non-gating context.

Add the package script in this task, alongside its target file:

```json
"bundle:check": "node scripts/check-bundle-size.mjs"
```

Test manually with a temporary small budget:

```bash
cd src/ResQ.Viz.Web
npm run build
npm run bundle:check
if BUNDLE_JS_BUDGET_BYTES=1 npm run bundle:check; then
  echo "bundle checker accepted an impossible one-byte budget" >&2
  exit 1
fi
```

Expected: normal limits pass and one-byte JS limit fails.

- [ ] **Step 2: Replace inline CI budget logic with the script**

In `client-budget`, run:

```bash
npm run bundle:check | tee -a "$GITHUB_STEP_SUMMARY"
```

Keep current environment budgets and built artifact upload.

- [ ] **Step 3: Add a serialized browser job**

Add `browser` after `gates` and `client-budget`, 30-minute timeout, read-only contents, and `src/ResQ.Viz.Web` working directory. Use existing pinned runner-hardening/checkout/setup actions, submodules recursive, Node 22, and .NET 10. Install npm dependencies and Chromium with dependencies. Download the exact built `wwwroot` artifact, build the host Debug without Vite, create a temporary HTTPS PFX, export path/password, then run `npm run test:browser`. Upload reports when not cancelled.

Update the required aggregator to need `browser`, expose its result, and pass it through the existing `ok()` check.

- [ ] **Step 4: Verify workflow syntax, local bundle check, and commit**

```bash
npm --prefix src/ResQ.Viz.Web run build
npm --prefix src/ResQ.Viz.Web run bundle:check
go run github.com/rhysd/actionlint/cmd/actionlint@v1.7.7 .github/workflows/ci.yml
git diff --check -- .github/workflows/ci.yml src/ResQ.Viz.Web/scripts/check-bundle-size.mjs
git add \
  .github/workflows/ci.yml \
  src/ResQ.Viz.Web/scripts/check-bundle-size.mjs \
  src/ResQ.Viz.Web/package.json
git commit -m "ci: verify operator console in Chromium"
```

### Task 25: Run final repository verification

**Files:**
- Verify: complete feature branch

- [ ] **Step 1: Prepare dependencies with serialized MSBuild restore**

```bash
git submodule update --init --recursive
npm --prefix src/ResQ.Viz.Web ci --legacy-peer-deps
dotnet restore ResQ.Viz.sln --disable-parallel -m:1
(cd src/ResQ.Viz.Web && npx playwright install chromium)
```

Expected: dependency setup exits zero. A local NU1900 caused only by blocked vulnerability metadata must be recorded. CI/networked verification must not have it.

- [ ] **Step 2: Build the Debug host required by Playwright `--no-build` servers**

```bash
dotnet build ResQ.Viz.sln -c Debug --no-restore --no-incremental -m:1 -warnAsError
```

Expected: Debug build passes with zero warnings.

- [ ] **Step 3: Run client, bundle, and browser gates**

```bash
set -e
npm --prefix src/ResQ.Viz.Web run typecheck
npm --prefix src/ResQ.Viz.Web test
npm --prefix src/ResQ.Viz.Web run build
npm --prefix src/ResQ.Viz.Web run bundle:check
npm --prefix src/ResQ.Viz.Web run test:browser
```

Expected: all Vitest and Playwright cases pass. Entry byte ceilings pass.

- [ ] **Step 4: Run the clean Release build and backend suites**

```bash
dotnet build ResQ.Viz.sln -c Release --no-restore --no-incremental -m:1 -warnAsError
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Release --no-build --no-restore -m:1
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Release --no-build --no-restore -m:1 \
  --filter 'FullyQualifiedName~ReplayDeterminismTests'
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Release --no-build --no-restore -m:1 \
  --filter 'FullyQualifiedName~CrossDomainInvariantTests'
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Release --no-build --no-restore -m:1 \
  --filter 'FullyQualifiedName~MixedFleetLoadTests' \
  --logger 'console;verbosity=detailed'
```

Expected: Release build passes with zero warnings. Full suite has zero failures, determinism passes 7, cross-domain invariants pass 3, and mixed-load passes 7 within existing thresholds.

- [ ] **Step 5: Run format, diff, and branch-scope checks**

```bash
dotnet format ResQ.Viz.sln --no-restore --verify-no-changes
git diff --check origin/main...HEAD
git status --short
git diff --name-status origin/main...HEAD
```

Expected: format and whitespace checks pass, status is clean, and the branch contains only the approved spec/plan and operator-console implementation files.

- [ ] **Step 6: Request final code review and finish the branch**

Run @superpowers:verification-before-completion followed by @superpowers:requesting-code-review, resolve every blocker, and rerun affected gates. After approval, @superpowers:finishing-a-development-branch presents integration options. Never merge or push without the user's request.
