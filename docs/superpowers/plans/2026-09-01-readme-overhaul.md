# ResQ Viz README Overhaul Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the stale drone-only README with a source-backed, approximately 8,000-word operating and contributor guide for the merged air, ground, and surface simulator.

**Architecture:** Keep one expanded narrative in `README.md`, reached through Evaluate, Operate, and Build links near the top. Put exhaustive HTTP, SignalR, scenario, control, command, and directory tables in collapsed blocks. Derive claims from the merged source at `4a4abd4`, and label run-specific measurements with that commit and date.

**Tech Stack:** GitHub-flavored Markdown, Mermaid, ASP.NET Core on .NET 10, SignalR, TypeScript 7, Vite 8, Vitest 4, Three.js 0.185, xUnit, shell verification with `rg`, `jq`, `curl`, and Git.

**Required skills:** @writing for every drafting task, @superpowers:verification-before-completion before the final completion claim.

---

## File Structure

- Modify: `README.md` — the single product, operator, architecture, API, and contributor guide requested by the user.
- Reference: `docs/superpowers/specs/2026-09-01-readme-overhaul-design.md` — approved content contract and measured baseline.
- Create: `docs/superpowers/plans/2026-09-01-readme-overhaul.md` — this execution plan.
- Read only: source and test files named in each task — evidence for every route, limit, workflow, and boundary.

The README is intentionally large because the approved design calls for one repository front door. Collapsed reference blocks keep repeated data out of the main reading path without splitting the requested deliverable across files.

The repository ignores `docs/`, so the planning checkpoint force-adds and commits the approved spec plus this plan before execution. Implementation must leave both tracked and must not rewrite them to hide a README discrepancy.

## Chunk 1: Evidence, front door, and first run

### Task 1: Establish the evidence ledger and README frame

**Files:**
- Modify: `README.md`
- Read: `docs/superpowers/specs/2026-09-01-readme-overhaul-design.md`
- Read: `src/ResQ.Viz.Web/ResQ.Viz.Web.csproj`
- Read: `src/ResQ.Viz.Web/package.json`
- Read: `src/ResQ.Viz.Web/package-lock.json`
- Read: `src/ResQ.Viz.Web/appsettings.json`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationManager.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationRoom.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationRoom.DeltaStream.cs`
- Read: `.github/workflows/ci.yml`
- Read: `tests/ResQ.Viz.Web.Tests/MixedFleetLoadTests.cs`
- Read: `tests/ResQ.Viz.Web.Tests/MixedFleetLoadTests.Measurement.cs`

- [ ] **Step 1: Confirm the branch baseline and allowed tracked files**

Run:

```bash
git merge-base --is-ancestor 4a4abd4 HEAD
git diff --name-status 4a4abd4...HEAD
git status --short
```

Expected: the ancestry check exits zero, only approved documentation differs from `4a4abd4`, and the status contains only the plan/spec work in progress.

- [ ] **Step 2: Reconfirm version, rate, limit, and measurement evidence**

Run:

```bash
rg -n 'net10.0|BUNDLE_(JS|CSS)_BUDGET|dotnet-version|node-version' \
  src/ResQ.Viz.Web/ResQ.Viz.Web.csproj \
  .github/workflows/ci.yml

jq -r '.packages as $p | ["three", "typescript", "vite", "vitest", "@microsoft/signalr"][] as $n | "\($n) \($p["node_modules/\($n)"].version)"' src/ResQ.Viz.Web/package-lock.json

rg -n 'MaxDroneCount|MaxAssetCount|MaxCoordinateM|MaxRooms|TickPeriod|1000\.0 / 60\.0|BroadcastEveryNTicks|KeyframeInterval' \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.cs \
  src/ResQ.Viz.Web/Services/SimulationManager.cs \
  src/ResQ.Viz.Web/Services/SimulationRoom.cs \
  src/ResQ.Viz.Web/Services/SimulationRoom.DeltaStream.cs \
  tests/ResQ.Viz.Web.Tests/MixedFleetLoadTests*.cs
```

Expected: .NET 10, Node 22 in CI, resolved Three.js 0.185.1, TypeScript 7.0.2, Vite 8.2.2, Vitest 4.1.11, SignalR 10.0.11, 50 air assets, 200 total assets, ±20 km coordinates, 100 rooms, 60 Hz stepping, 10 Hz broadcast, and a 50-frame keyframe interval.

- [ ] **Step 3: Record the exact measurement provenance**

The dated baseline came from these commands at `4a4abd4`:

```bash
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Release --logger 'console;verbosity=normal'

dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj \
  -c Release \
  --filter 'FullyQualifiedName~ResQ.Viz.Web.Tests.MixedFleetLoadTests' \
  --logger 'console;verbosity=detailed'

(
  cd src/ResQ.Viz.Web
  npm test
  npm run build
  stat -c '%n %s bytes' wwwroot/assets/index-*.js wwwroot/assets/index-*.css
)
```

Expected reference-run records: 1,257 xUnit tests and 661 Vitest tests passed. The 150-asset world-step p95 was 0.798 ms, and the 150-asset frame total p95 including serialization was 4.136 ms. The median step-cost ratio was 10.56× for 150 versus 15 assets. Delta-to-snapshot payload ratios were 80.8% underway and 9.6% holding. Entry JavaScript was 796,176 bytes, and CSS was 37,569 bytes. A reproduction run may differ by host. Keep the reference date and commit unless the full set is regenerated together.

- [ ] **Step 4: Verify the shared banner and repository destinations**

Run:

```bash
curl -fsSLI https://raw.githubusercontent.com/resq-software/.github/main/assets/banner.png
curl -fsSLI https://github.com/resq-software/viz/actions/workflows/ci.yml
curl -fsSLI https://github.com/resq-software/viz/blob/main/LICENSE
curl -fsSLI https://viz.resq.software/
```

Expected: each URL returns a successful response or redirect. Use `curl -fsSL` for a host that refuses `HEAD`.

- [ ] **Step 5: Write the banner, badges, product line, and boundary**

Use `apply_patch` to replace the old opening with:

1. the shared organization banner.
2. a centered `ResQ Viz` heading and factual one-line product description.
3. CI, Apache-2.0, .NET 10, Three.js, and SignalR badges.
4. links for local start and the hosted deployment if the repository declares one.
5. the simulation-only and advisory boundary before the first long section.

Do not add a product screenshot. Do not copy the banner into the repository.

- [ ] **Step 6: Add role routes, contents, and the twelve-section frame**

Add Evaluate, Operate, and Build jump links, then a complete contents list in the approved order. Put an explicit `<a id="..."></a>` anchor before every role destination and every section targeted by those links. Add all twelve section headings with a short factual scope sentence beneath each. These are real introductory sentences, not TODOs or HTML placeholders, and later tasks expand them in place.

- [ ] **Step 7: Write the evaluator scope and compatibility overview**

Write the product scope, the three supported domains, v1 compatibility boundary, room isolation, streaming choices, and command authority. Keep the simulation-only and advisory warnings visible without repeating the same paragraph.

- [ ] **Step 8: Add the dated reference-measurement table**

Use the `2026-09-01 · 4a4abd4` label and these exact rows:

| Measurement | Verified value |
| :--- | ---: |
| xUnit tests | 1,257 passed |
| Vitest tests | 661 passed |
| 150-asset world-step p95 | 0.798 ms |
| 150-asset frame total p95, including serialization | 4.136 ms |
| Median world-step scaling, 150 versus 15 assets | 10.56× |
| Underway delta-to-snapshot payload ratio | 80.8% |
| Holding delta-to-snapshot payload ratio | 9.6% |
| Built entry JavaScript | 796,176 bytes |
| Built entry CSS | 37,569 bytes |

State beside the table that these are one verified run, while CI ceilings and capacity caps are source-enforced contracts.

- [ ] **Step 9: Check heading order, balance, and opening prose**

Run:

```bash
rg -n '^## |simulation-only|advisory|2026-09-01|4a4abd4' README.md
test "$(rg '^<details>$' README.md | wc -l)" -eq "$(rg '^</details>$' README.md | wc -l)"
test -z "$(rg -ni 'TODO|TBD|PLACEHOLDER' README.md)"
git diff --check -- README.md
```

Expected: the twelve approved headings print in order, the operating boundary is visible near the top, the measured table is labeled, details tags balance, no draft marker remains, and Git reports no whitespace errors.

- [ ] **Step 10: Commit the front door**

```bash
git add README.md
git commit -m "docs: rebuild README front door"
```

### Task 2: Write the five-minute run and three-domain model

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/appsettings.json`
- Read: `src/ResQ.Viz.Web/Controllers/SessionController.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimController.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.Assets.cs`
- Read: `src/ResQ.Viz.Web/Models/Assets.cs`
- Read: `src/ResQ.Viz.Web/Models/AssetCommand.cs`
- Read: `src/ResQ.Viz.Web/Models/AssetEnums.cs`
- Read: `src/ResQ.Viz.Web/Models/Geo.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/AssetProfiles.cs`
- Read: `src/ResQ.Viz.Web/Services/ScenarioService.cs`
- Read: `src/ResQ.Viz.Web/Program.cs`

- [ ] **Step 1: Verify the local start path and HTTPS endpoint**

Run:

```bash
jq '.Kestrel.Endpoints' src/ResQ.Viz.Web/appsettings.json
rg -n 'TargetFramework|NpmInstall|ViteBuild|dotnet-version|node-version|TickPeriod|1000\.0 / 60\.0|BroadcastEveryNTicks|60 Hz / 6 = 10 Hz' \
  src/ResQ.Viz.Web/ResQ.Viz.Web.csproj \
  src/ResQ.Viz.Web/Services/SimulationManager.cs \
  src/ResQ.Viz.Web/Services/SimulationRoom.cs \
  .github/workflows/ci.yml
```

Expected: HTTPS is configured on port 5001, the target is .NET 10, and the repository defines the client-install/build behavior documented in the quick start.

- [ ] **Step 2: Draft the browser quick start**

Document these executable actions:

```bash
git clone --recurse-submodules https://github.com/resq-software/viz.git
cd viz
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/ResQ.Viz.Web
```

Direct the reader to `https://localhost:5001`, explain the local development certificate case, and state why Development is explicit: Debug skips the Release client build, while `Program.cs` starts Vite only in Development. Use `flood-response` for the first mixed-fleet view because it runs on the default alpine environment. Keep `coastal-search`, `coastal-transit`, and `port-incident` out of this first run because they require the coastal environment.

- [ ] **Step 3: Draft exact session and scenario commands**

Use this executable block:

```bash
readme_base=https://localhost:5001
readme_cookie=$(mktemp /tmp/resq-viz-readme-cookie.XXXXXX)

curl -ksS -c "$readme_cookie" -X POST \
  "$readme_base/api/sim/session" \
  | jq -e '.roomId and (.expiresIn == 86400)'

curl -ksS -b "$readme_cookie" -X POST \
  "$readme_base/api/sim/scenario/flood-response" \
  | jq -e '.scenario == "flood-response" and .status == "started"'
```

Explain why all later requests need the `viz_session` cookie. Keep command authority and polling examples for their dedicated section.

- [ ] **Step 4: Draft exact snapshot and inventory commands**

Continue the same block:

```bash
curl -ksS -b "$readme_cookie" \
  "$readme_base/api/v2/sim/snapshot" \
  | jq -e '.schemaVersion == "2.0" and (.assets | length == 8)'

curl -ksS -b "$readme_cookie" \
  "$readme_base/api/v2/sim/assets" \
  | jq -e '(.descriptors | length == 8) and (.assets | length == 8)'
```

Explain that the first response is one atomic operating-picture snapshot, while the inventory endpoint returns descriptors and current states with a captured tick.

- [ ] **Step 5: Exercise the four commands against a disposable server**

Run from the repository root after dependencies and the SDK submodule are present:

```bash
set -Eeuo pipefail

readme_log=$(mktemp /tmp/resq-viz-readme-server.XXXXXX)
readme_cookie=$(mktemp /tmp/resq-viz-readme-cookie.XXXXXX)
readme_base=https://localhost:5001

ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/ResQ.Viz.Web >"$readme_log" 2>&1 &
readme_server_pid=$!

readme_cleanup() {
  kill -TERM "$readme_server_pid" 2>/dev/null || true
  wait "$readme_server_pid" 2>/dev/null || true
  rm -f "$readme_log" "$readme_cookie"
}
trap readme_cleanup EXIT

readme_ready=false
for readme_attempt in $(seq 1 120)
do
  if curl -ksSf https://localhost:5001/ >/dev/null
  then
    readme_ready=true
    break
  fi
  kill -0 "$readme_server_pid"
  sleep 0.5
done
test "$readme_ready" = true

curl -ksS -c "$readme_cookie" -X POST \
  "$readme_base/api/sim/session" \
  | jq -e '.roomId and (.expiresIn == 86400)'

curl -ksS -b "$readme_cookie" -X POST \
  "$readme_base/api/sim/scenario/flood-response" \
  | jq -e '.scenario == "flood-response" and .status == "started"'

curl -ksS -b "$readme_cookie" \
  "$readme_base/api/v2/sim/snapshot" \
  | jq -e '.schemaVersion == "2.0" and (.assets | length == 8)'

curl -ksS -b "$readme_cookie" \
  "$readme_base/api/v2/sim/assets" \
  | jq -e '(.descriptors | length == 8) and (.assets | length == 8)'

awk -F '\t' '$6 == "viz_session" { found = 1 } END { exit !found }' "$readme_cookie"
kill -TERM "$readme_server_pid"
wait "$readme_server_pid" || true
rm -f "$readme_log" "$readme_cookie"
trap - EXIT
```

Expected: session creation returns a room id and 86,400-second TTL. `flood-response` starts. The v2 snapshot reports schema `2.0` and eight assets, while the inventory contains eight descriptors and eight states. Inspect the cookie jar and confirm a `viz_session` row was written.

- [ ] **Step 6: Write descriptors, state, and capabilities**

Describe descriptors versus state, air/ground/surface domain values, reserved subsurface values, mobility classes, capabilities, lifecycle state, energy, link state, and typed domain state. Include one compact comparison table for the three domains.

- [ ] **Step 7: Write the coordinate-frame boundary**

State that the canonical Three.js scene frame is `LocalEus`. Explain that `LocalEnu` and `LocalNed` are distinct supported local frames, while `GlobalWgs84` is the non-Cartesian frame carried by `GeoPosition` and `GeoCommandTarget`. Tie point-command targets to their explicit frame and origin, and warn that the default origin is a placeholder.

- [ ] **Step 8: Explain v1 and v2 without implying equivalent behavior**

State that v1 continues for one deprecation cycle, exposes the drone-shaped compatibility surface, and receives `ReceiveFrame`. State that v2 exposes mixed assets, descriptors, domain state, commands, authority, tracks, snapshots, and deltas. Put the v1 lease bypass warning in this section and repeat it only in the authority reference row where an operator could otherwise miss it.

- [ ] **Step 9: Verify commands, scenario spelling, and section budget**

Run:

```bash
jq -e '.Scenarios["flood-response"] | length > 0' src/ResQ.Viz.Web/appsettings.json
rg -n 'api/sim/session|scenario/flood-response|api/v2/sim/snapshot|api/v2/sim/assets|LocalEus|LocalEnu|LocalNed|GlobalWgs84|GeoPosition|GeoCommandTarget' README.md
rg -n 'enum CoordinateFrame|LocalEus|LocalEnu|LocalNed|GlobalWgs84|GeoPosition' src/ResQ.Viz.Web/Models/Geo.cs
rg -n 'GeoCommandTarget' src/ResQ.Viz.Web/Models/AssetCommand.cs
readme_words=$(wc -w < README.md)
test "$readme_words" -ge 1600
test "$readme_words" -le 2600
git diff --check -- README.md
```

Expected: the scenario exists, the live smoke already proved all four routes and cookie propagation, coordinate-frame names match source, the cumulative README is 1,600–2,600 words, and Git reports no whitespace errors.

- [ ] **Step 10: Commit the first-run guide**

```bash
git add README.md
git commit -m "docs: add mixed-fleet quick start"
```

## Chunk 2: Command authority, safe actions, and frame delivery

### Task 3: Document the command contract and control authority

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.Commands.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.Validation.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.Authority.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.Authority.Gate.cs`
- Read: `src/ResQ.Viz.Web/Models/AssetCommand.cs`
- Read: `src/ResQ.Viz.Web/Models/SimCommandV2.cs`
- Read: `src/ResQ.Viz.Web/Models/Geo.cs`
- Read: `src/ResQ.Viz.Web/Models/CommandAudit.cs`
- Read: `src/ResQ.Viz.Web/Models/ControlLease.cs`
- Read: `src/ResQ.Viz.Web/Models/ControlLeaseApi.cs`
- Read: `src/ResQ.Viz.Web/Services/CommandCatalog.cs`
- Read: `src/ResQ.Viz.Web/Services/CommandCatalog.Validation.cs`
- Read: `src/ResQ.Viz.Web/Services/CommandIdempotency.cs`
- Read: `src/ResQ.Viz.Web/Services/AssetCommandDispatch.cs`
- Read: `src/ResQ.Viz.Web/Services/ControlAuthority.cs`
- Read: `src/ResQ.Viz.Web/Services/ControlAuthority.Audit.cs`
- Read: `src/ResQ.Viz.Web/Services/ControlAuthority.Instances.cs`
- Read: `src/ResQ.Viz.Web/Services/ControlAuthority.Leases.cs`
- Read: `src/ResQ.Viz.Web/Services/ControlAuthorityRegistry.cs`
- Read: `tests/ResQ.Viz.Web.Tests/AssetCommandHardeningTests.cs`
- Read: `tests/ResQ.Viz.Web.Tests/AssetCommandValidationTests.cs`
- Read: `tests/ResQ.Viz.Web.Tests/AssetCommandValidationTests.Lifecycle.cs`
- Read: `tests/ResQ.Viz.Web.Tests/CommandAuthorityTests.cs`
- Read: `tests/ResQ.Viz.Web.Tests/CommandAuthorityTests.Leases.cs`
- Read: `tests/ResQ.Viz.Web.Tests/SimV2ControllerTests.Commands.cs`
- Read: `tests/ResQ.Viz.Web.Tests/V1CompatibilityTests.Commands.cs`

- [ ] **Step 1: Extract the registered command catalog**

Run:

```bash
rg -n 'Def\(CommandKinds\.|public const string' src/ResQ.Viz.Web/Services/CommandCatalog.cs
```

Expected registered families: common `stop`, `emergencyStop`, `hold`, `resumeAutonomy`, `goTo`, `returnToBase`, and `setSpeed`. Air commands are `takeoff`, `land`, `setAltitude`, and `loiter`. Ground commands are `driveTo`, `reverse`, and `park`. Surface commands are `transitTo`, `setCourse`, `stationKeep`, `dock`, and `undock`. `followRoute` and `setSteering` are named but unregistered.

- [ ] **Step 2: Extract the controller's command order**

Run:

```bash
rg -n 'TryBuildEnvelope|Idempotency.Classify|ReplayDuplicate|CaptureAssetFrame|CommandCatalog.Validate|PrecedesAuthority|AuthorityRefusal|SafetyRefusal|Idempotency.Claim|TryTranslate|SendAssetCommand|Accepted\(' \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.cs
```

Expected order: payload and frame/target normalization, then idempotency classification plus duplicate/conflict replay. Asset-frame capture and a pure catalog verdict follow. Every `payload.*`, `deadline.*`, and `asset.*` refusal, including target-payload errors, precedes authority. Remaining capability/domain/state/freshness refusals follow authority, then link reachability, idempotency claim, intent translation, world dispatch, the accepted record, decision audit, and HTTP `202`.

- [ ] **Step 3: Write the envelope and idempotency contract**

Describe the case-sensitive kind, mandatory idempotency key, optional caller-supplied command ID, caller-supplied issuer ID with room fallback, optional lease ID, typed target, constraints, deadline, scalar frame, and string parameter bag. Explain that local/geodetic targets normalize to `LocalEus` before payload hashing, then duplicate/conflict classification runs before asset resolution. A refusal before claim leaves the key reusable.

- [ ] **Step 4: Write the split validation and dispatch contract**

Follow the exact order from Step 2. Explain why payload errors, including invalid target shape, plus deadline and unknown-asset failures precede authority. Capability, domain, state, and freshness details follow it. Put link reachability before claim. Put translation after claim, then name world dispatch as a final refusal point that can enforce the last safe-action assessment.

- [ ] **Step 5: State polling behavior without promising physical completion**

State that HTTP `202` means the command passed the current gates and was handed to the simulated asset. `GET /api/v2/sim/commands/{commandId}` returns the latest bounded room record. Envelope-build failures and new duplicate/conflict outcomes do not add a decision-audit record or a pollable result. A true duplicate can replay an existing record. Catalog, authority, and link refusals are decision-audited before claim but are not pollable. Post-claim translation/dispatch rejections and accepted commands are both audited and stored. A `404` can therefore mean never tracked or no longer retained. The production path does not advance an accepted record from physical execution, so describe the endpoint as status retrieval rather than a guaranteed completion tracker.

- [ ] **Step 6: Add one complete request and status-read example**

Use `flood-response` asset `fr-supply-lead` and this body:

```json
{
  "kind": "driveTo",
  "idempotencyKey": "readme-fr-supply-001",
  "issuerId": "readme-operator",
  "target": {
    "type": "point",
    "point": {
      "frame": 2,
      "position": { "x": -400, "y": 0, "z": 25 }
    }
  }
}
```

Show a `curl -o`/`-w '%{http_code}'` request that asserts `202`, extracts `.commandId`, then reads `/api/v2/sim/commands/{commandId}` with the same cookie. Frame value `2` is `LocalEus`. Do not claim the returned accepted record proves arrival at the target.

```bash
readme_command_body=$(mktemp /tmp/resq-viz-readme-command.XXXXXX)
readme_command_status=$(curl -ksS -b "$readme_cookie" -H 'Content-Type: application/json' \
  -X POST \
  -d '{"kind":"driveTo","idempotencyKey":"readme-fr-supply-001","issuerId":"readme-operator","target":{"type":"point","point":{"frame":2,"position":{"x":-400,"y":0,"z":25}}}}' \
  -o "$readme_command_body" -w '%{http_code}' \
  "$readme_base/api/v2/sim/assets/fr-supply-lead/commands")
test "$readme_command_status" = 202
readme_command_id=$(jq -er '.commandId' "$readme_command_body")
curl -ksS -b "$readme_cookie" \
  "$readme_base/api/v2/sim/commands/$readme_command_id" \
  | jq -e '.commandId and .state'
rm -f "$readme_command_body"
```

- [ ] **Step 7: Extract lease endpoints and request rules**

Run:

```bash
rg -n 'Http(Get|Post)|Duration|Holder|Lease|Preempt|Audit|ControlMode' \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.Authority.cs \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.Authority.Gate.cs \
  src/ResQ.Viz.Web/Models/ControlLeaseApi.cs \
  src/ResQ.Viz.Web/Models/ControlLease.cs \
  src/ResQ.Viz.Web/Services/ControlAuthorityRegistry.cs
```

Expected: mode, audit, holder, acquire, renew, release, and preempt routes with their exact request fields, duration bounds, and decision records.

- [ ] **Step 8: Write the authority semantics and identity boundary**

State that an uncontrolled asset accepts a v2 command without a lease. Once held, the issuer must match the live holder. A matching holder may omit the lease ID, while a supplied ID must match the live lease. Each room owns an authority whose leases are keyed by asset. The audit endpoint returns a room-wide bounded window, and each record identifies its asset. Holder/issuer values are assertions supplied by the caller, not authenticated user identities. The room cookie is the only server-established identity in this build. Explain expiry, renew, release, and emergency preemption. State that v1 commands bypass the v2 envelope, authority, idempotency, link gate, and held-position safe-action gate during the compatibility cycle.

- [ ] **Step 9: Add the command-authority Mermaid flow**

Draw the exact Step 2 sequence, including the early classification branch and the split catalog verdict around authority. Put target/frame normalization before classification, all target-payload refusals before authority, translation after claim, and latest-status read plus audit after dispatch. Mark envelope-build and new duplicate/conflict outcomes as neither newly audited nor pollable. Mark catalog/authority/link pre-claim refusals as audited-only. Mark post-claim results as audited and pollable. Add refusal branches without reordering them.

- [ ] **Step 10: Verify command and authority prose**

Run:

```bash
rg -n 'classification|normaliz|uncontrolled|matching holder|caller-supplied|202|latest|does not advance|v1|bypass|translation|claim' README.md
rg -n 'Classify|Claim|AuthorityRefusal|IsHeldBy|lease is null|TryTranslate|GetCommand' \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.cs \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.Authority.Gate.cs
git diff --check -- README.md
```

Expected: prose and diagram follow source order, identity limits are explicit, status retrieval is not called physical completion, and Git reports no whitespace errors.

- [ ] **Step 11: Check the command-section budget and commit**

Keep the full command and authority section, including its request example, near 500–650 words before the collapsed catalog. Then run:

```bash
git add README.md
git commit -m "docs: explain commands and control authority"
```

### Task 4: Document link state, safe actions, and recovery

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/appsettings.json`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.Link.cs`
- Read: `src/ResQ.Viz.Web/Models/AssetLinkApi.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationRoom.Link.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationRoom.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/SafeActionGovernor.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/SafeActionPolicy.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/SafeActionPolicy.Gates.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/SafeActionPolicy.Model.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/AssetWorld.Stepping.cs`
- Read: `tests/ResQ.Viz.Web.Tests/LinkGatingTests.cs`
- Read: `tests/ResQ.Viz.Web.Tests/SafeActionPolicyTests.Enforcement.cs`
- Read: `tests/ResQ.Viz.Web.Tests/SafeActionPolicyTests.Position.cs`
- Read: `tests/ResQ.Viz.Web.Tests/SafeActionWiringTests.cs`

- [ ] **Step 1: Extract link mutation and pre-claim refusal**

Run:

```bash
rg -n 'Http(Get|Post)|link\.unreachable|changed|issuerId|reason|SafetyRefusal|Claim' \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.Link.cs \
  src/ResQ.Viz.Web/Controllers/SimV2Controller.cs \
  src/ResQ.Viz.Web/Services/SimulationRoom.Link.cs
```

Expected: GET/POST per-asset link routes, actor/reason audit fields, and an unreachable refusal before idempotency claim.

- [ ] **Step 2: Extract fallback priority and degradation**

Run:

```bash
rg -n 'linkLost|IsEnergyReserveSpent|SafeActionTrigger|ReserveBehaviour|Resolve|IsPositionFixUsable|IsHeldPositionUsable|ReturnToBase|Land|StopAndHold|DriftAndAlert' \
  src/ResQ.Viz.Web/Services/Assets/SafeActionPolicy.cs \
  src/ResQ.Viz.Web/Services/Assets/SafeActionPolicy.Gates.cs \
  src/ResQ.Viz.Web/Services/Assets/SafeActionPolicy.Model.cs
```

Expected: link loss takes priority over low energy. Onboard action uses the asset's own fix. An unusable air return can degrade to land and then stop. Operator positional commands use the effective held-position freshness/uncertainty gate. A displacement hull drifts and alerts on link loss but maps low energy to stop-and-hold.

- [ ] **Step 3: Write the three domain fallbacks**

Explain air return-to-base with land/stop degradation, ground stop-and-hold, and surface drift-and-alert for link loss. The shipped displacement hull does not advertise `StationKeep`. Explain the separate low-energy mapping, once-per-episode governor, uncertainty growth, simulated-time sweep, and non-latching restoration. The world applies an accepted air fallback first, then detaches that aircraft before the coordinator's next pass.

- [ ] **Step 4: Write the onboard-versus-operator position boundary**

Separate `IsPositionFixUsable` from `IsHeldPositionUsable`. The onboard fallback judges the asset's own fix. An operator positional command can be refused as stale or uncertain against the last safe-action assessment during world dispatch. Stop commands remain non-positional. If a positional command is sent immediately after restoration, tell the reader to wait for the next one-simulated-second safety sweep when the held assessment is still stale.

- [ ] **Step 5: Add the safe-action Mermaid flow**

Draw link and energy observations, priority selection, onboard fix gate, return-to-base degradation, the three domain branches, accepted air fallback followed by coordinator detachment, audit/telemetry, restoration, held-position re-assessment, and operator command recovery. Keep onboard and operator gates visually separate.

Show that the link mutation route itself is not lease-gated. A deployment reporting live control refuses link cuts, while restoration remains permitted.

- [ ] **Step 6: Draft an exact same-key recovery walkthrough**

State first that `link-loss-divergence` only places assets. With `readme_base` and `readme_cookie` from the quick start, use `lld-ugv-1`:

```bash
curl -ksS -b "$readme_cookie" -X POST \
  "$readme_base/api/sim/scenario/link-loss-divergence" \
  | jq -e '.scenario == "link-loss-divergence" and .status == "started"'

curl -ksS -b "$readme_cookie" -H 'Content-Type: application/json' \
  -X POST \
  -d '{"available":false,"issuerId":"readme-operator","reason":"README link-loss exercise"}' \
  "$readme_base/api/v2/sim/assets/lld-ugv-1/link" \
  | jq -e '.isAvailable == false and .changed == true'

readme_link_body=$(mktemp /tmp/resq-viz-readme-link.XXXXXX)
readme_link_status=$(curl -ksS -b "$readme_cookie" -H 'Content-Type: application/json' \
  -X POST \
  -d '{"kind":"stop","idempotencyKey":"readme-link-retry-001","issuerId":"readme-operator"}' \
  -o "$readme_link_body" -w '%{http_code}' \
  "$readme_base/api/v2/sim/assets/lld-ugv-1/commands")
test "$readme_link_status" = 409
jq -e '.code == "link.unreachable"' "$readme_link_body"
```

- [ ] **Step 7: Complete restoration with the identical command**

Continue:

```bash
curl -ksS -b "$readme_cookie" -H 'Content-Type: application/json' \
  -X POST \
  -d '{"available":true,"issuerId":"readme-operator","reason":"README recovery"}' \
  "$readme_base/api/v2/sim/assets/lld-ugv-1/link" \
  | jq -e '.isAvailable == true and .changed == true'

readme_link_status=$(curl -ksS -b "$readme_cookie" -H 'Content-Type: application/json' \
  -X POST \
  -d '{"kind":"stop","idempotencyKey":"readme-link-retry-001","issuerId":"readme-operator"}' \
  -o "$readme_link_body" -w '%{http_code}' \
  "$readme_base/api/v2/sim/assets/lld-ugv-1/commands")
test "$readme_link_status" = 202
jq -e '.commandId' "$readme_link_body"
rm -f "$readme_link_body"
```

Explain that the same key succeeds because the link refusal did not claim it. This short block restores immediately and verifies link gating plus idempotency only. It does not prove that the rover executed its fallback. To observe the fallback, leave the link down through the next one-simulated-second sweep and inspect the v2 snapshot before restoring it. A positional retry has the additional held-position timing described in Step 4.

- [ ] **Step 8: Verify safety wording and section budget**

Run:

```bash
rg -n 'does not cut|not lease-gated|live control|restoration.*permitted|link loss.*priority|low energy|own fix|held position|degrade|same key|next one-simulated-second|StationKeep' README.md
rg -n 'link-loss-divergence|LinkGatingTests|PositionStale|PositionUncertain|ReserveBehaviour|Resolve|IsEnergyReserveSpent' \
  src/ResQ.Viz.Web/appsettings.json \
  tests/ResQ.Viz.Web.Tests/LinkGatingTests.cs \
  src/ResQ.Viz.Web/Services/Assets/SafeActionPolicy.cs \
  src/ResQ.Viz.Web/Services/Assets/SafeActionPolicy.Gates.cs
git diff --check -- README.md
```

Expected: the full link/safe-action section, including its executable block, is about 300–450 words. It distinguishes both position gates and triggers, states mutation permissions, labels the quick recovery block's scope, and has no whitespace errors. Combined with Task 3, the command/control/safety material stays near the approved 950-word allocation.

- [ ] **Step 9: Commit the link and recovery guide**

```bash
git add README.md
git commit -m "docs: document link safety and recovery"
```

### Task 5: Document v1, snapshot, and delta streaming

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/Program.cs`
- Read: `src/ResQ.Viz.Web/Hubs/VizHub.cs`
- Read: `src/ResQ.Viz.Web/Hubs/VizHub.Deltas.cs`
- Read: `src/ResQ.Viz.Web/Models/VizFrame.cs`
- Read: `src/ResQ.Viz.Web/Models/VizFrameV2.cs`
- Read: `src/ResQ.Viz.Web/Models/VizFrameDeltaV2.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationManager.Broadcast.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationRoom.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationRoom.Broadcast.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationRoom.DeltaStream.cs`
- Read: `src/ResQ.Viz.Web/Services/SignalRFrameBroadcaster.cs`
- Read: `src/ResQ.Viz.Web/Services/VizSnapshotDiffer.cs`
- Read: `src/ResQ.Viz.Web/Services/VizSnapshotDiffer.Budget.cs`
- Read: `src/ResQ.Viz.Web/Services/VizSnapshotDiffer.Equality.cs`
- Read: `src/ResQ.Viz.Web/Services/VizSnapshotDiffer.Equality.Observations.cs`
- Read: `src/ResQ.Viz.Web/client/assets/deltaApply.ts`
- Read: `src/ResQ.Viz.Web/client/app.ts`
- Read: `src/ResQ.Viz.Web/client/__tests__/deltaApply.test.ts`
- Read: `tests/ResQ.Viz.Web.Tests/DeltaStreamTests.cs`
- Read: `tests/ResQ.Viz.Web.Tests/DeltaTransportHardeningTests.cs`

- [ ] **Step 1: Extract the SignalR contract**

Run:

```bash
rg -n 'Receive(Frame|Snapshot|Delta)|Subscribe(Snapshots|Deltas)|RequestKeyframe|MapHub' \
  src/ResQ.Viz.Web/Hubs/VizHub.cs \
  src/ResQ.Viz.Web/Hubs/VizHub.Deltas.cs \
  src/ResQ.Viz.Web/Services/SignalRFrameBroadcaster.cs \
  src/ResQ.Viz.Web/Program.cs
```

Expected: hub `/viz`. Events are `ReceiveFrame`, `ReceiveSnapshotV2`, and `ReceiveDeltaV2`. Calls are `SubscribeSnapshots`, `SubscribeDeltas`, and `RequestKeyframe`.

- [ ] **Step 2: Write stream membership and opt-in behavior**

State that every connection remains in the room's v1 group and receives `ReceiveFrame`. `SubscribeSnapshots(true)` adds the full-v2 group. `SubscribeDeltas(true)` moves the connection out of the full-v2 group and into the delta group, but does not remove it from v1. Subscription intent is per connection and must be restored after reconnect. REST snapshot supports cold start and reconciliation.

- [ ] **Step 3: Write server keyframe and delta rules**

Explain the per-room baseline and stream sequence, exact diff, removed IDs, carried stamps, environment/tick/resync/join triggers, and a periodic keyframe every 50 published v2 frames. Call five seconds nominal at an uninterrupted 10 Hz publication cadence. Backpressure skips can extend wall-clock time. A delta already in the transport may reach a joining client first, but it is unusable. The first actionable frame is complete.

- [ ] **Step 4: Write client apply and recovery rules**

State that the client applies a delta only when `delta.baseFrameId` equals the held `frameId`. It does not validate `baseSequence`. Explain duplicate, stale/reordered, and gap outcomes, atomic reconstruction, keeping the last good picture, rate-limited `RequestKeyframe`, periodic recovery, reconnect resubscription, and eventual fallback to full snapshots when recovery fails.

- [ ] **Step 5: Write bounded delivery and observability budgets**

Describe separate v1 and v2 family slots and per-stream drop metrics. The delta chain advances when a frame is handed to the transport, before the awaited send completes. A send failure requests a repairing keyframe. Explain that energy budgets choose changed-versus-carried payload channels while carried stamps preserve exact values. They do not decide whether a frame is sent.

- [ ] **Step 6: Add the frame-streaming Mermaid flow**

Draw 60 Hz host ticks, multiple world steps per host tick at 2×–8× speed, capture every sixth host tick, separate v1/v2 slots, v1 frame delivery to every connection, subscriber-gated v2 assembly, full and delta groups, 50-published-frame/requested keyframes, base-frame gap detection, resync, and REST reconciliation.

- [ ] **Step 7: Verify server and client terminology**

Run:

```bash
rg -n 'ReceiveFrame|ReceiveSnapshotV2|ReceiveDeltaV2|SubscribeSnapshots|SubscribeDeltas|RequestKeyframe|every connection|baseFrameId|does not validate.*baseSequence|50 published|nominal|first actionable|host tick|backpressure|carried' README.md
rg -n 'baseFrameId|baseSequence|gap|duplicate|stale|RequestKeyframe' \
  src/ResQ.Viz.Web/client/assets/deltaApply.ts \
  src/ResQ.Viz.Web/client/app.ts
rg -n 'KeyframeInterval|TryBeginBroadcast|TryBeginLegacyBroadcast|PublishDeltaFrame|HasPendingDeltaJoin' \
  src/ResQ.Viz.Web/Services/SimulationRoom.DeltaStream.cs \
  src/ResQ.Viz.Web/Services/SimulationManager.Broadcast.cs
git diff --check -- README.md
```

Expected: the client rule is based on frame IDs, cadence is not promised in wall-clock time, v1 membership remains explicit, and Git reports no whitespace errors.

- [ ] **Step 8: Check the streaming budget and commit**

Keep the streaming section near 600–800 words, then run:

```bash
git add README.md
git commit -m "docs: explain snapshots and delta streaming"
```

## Chunk 3: Domain models, operator workspace, and architecture

### Task 6: Write the air, ground, surface, and track model

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/Models/AssetDomainState.cs`
- Read: `src/ResQ.Viz.Web/Models/ExternalTracks.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/AirAsset.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/AirAsset.Commands.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/AssetProfiles.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/GroundAsset.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/GroundAsset.Commands.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/GroundAsset.Telemetry.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/GroundNavigator.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/GroundNavigator.Guidance.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/GroundNavigator.Model.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/GroundProfile.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/AckermannDynamics.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/DifferentialDynamics.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/TerrainContact.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Ground/Traversability.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/SurfaceDynamics.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/WaterConstraints.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/UnderKeelClearance.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/Docking.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/StationKeeping.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/SurfaceProfile.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/SurfaceAsset.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/SurfaceAsset.Commands.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/SurfaceAsset.Telemetry.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/SurfaceNavigator.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/SurfaceNavigator.Guidance.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/SurfaceNavigator.Model.cs`
- Read: `src/ResQ.Viz.Web/Services/Assets/Surface/WaveModel.cs`
- Read: `src/ResQ.Viz.Web/Services/Tracks/*.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SimV2Controller.Tracks.cs`

- [ ] **Step 1: Draft the air model from the SDK adapter boundary**

Describe the multirotor adapter, SDK flight physics, pose/twist/energy projection, air commands, launch position, and return-to-base policy. Keep this subsection shorter than ground and surface because the old README already over-weighted air behavior.

- [ ] **Step 2: Draft ground motion, contact, and advisories**

Compare Ackermann, differential, and tracked rover profiles. Explain terrain settling, suspension/contact sampling, steering and speed behavior, profile-specific traversability, slope/cross-slope/step checks, and rollover proximity. Mark traversability and rollover output as advisory, and avoid claiming route planning or certified mobility.

- [ ] **Step 3: Draft surface motion, clearance, and docking**

Explain hull dynamics, thrust/rudder response, current/leeway, bathymetry, water constraints, under-keel-clearance bands, docking phases and aborts, visual wave attitude, and drift. State that the shipped displacement hull does not advertise `StationKeep`, even though generic station-keeping code exists.

- [ ] **Step 4: Draft external tracks and CPA**

Explain report fusion, source ageing, stale/coasting/expired states, store bounds, observed-versus-simulated identity, and closest-point-of-approach output. State that tracks have no capability or command resource and that CPA is advisory. Include the default 5-second, 20-second, and 60-second ageing points only after confirming them in `ExternalTrackStore.Model.cs`.

- [ ] **Step 5: Add one comparison table and operational-limit callouts**

The table must map each domain to shipped class, motion inputs, published domain state, common commands, safe action, and advisory outputs. Add separate callouts for the ±20 km local-coordinate bound, 50-air and 200-total room caps, placeholder geographic origin, and simulation-only control mode.

- [ ] **Step 6: Verify every model statement**

Run:

```bash
rg -n 'advisory|StationKeep|ReturnToBase|StopAndHold|DriftAndAlert|Ackermann|Differential|Tracked|under-keel|docking|closest.point|5 seconds|20 seconds|60 seconds' \
  README.md \
  src/ResQ.Viz.Web/Services/Assets \
  src/ResQ.Viz.Web/Services/Tracks \
  src/ResQ.Viz.Web/Models

git diff --check -- README.md
```

Expected: each domain has a source-backed model, every navigation-related output has an advisory boundary, tracks remain non-commandable, and Git reports no whitespace errors.

- [ ] **Step 7: Commit the domain model**

```bash
git add README.md
git commit -m "docs: describe domain and advisory models"
```

### Task 7: Write the operator workspace and scenario catalog

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/appsettings.json`
- Read: `src/ResQ.Viz.Web/Services/ScenarioService.cs`
- Read: `src/ResQ.Viz.Web/Services/ScenarioService.Entries.cs`
- Read: `src/ResQ.Viz.Web/client/app.ts`
- Read: `src/ResQ.Viz.Web/client/controls.ts`
- Read: `src/ResQ.Viz.Web/client/cameraControl.ts`
- Read: `src/ResQ.Viz.Web/client/cameraMode.ts`
- Read: `src/ResQ.Viz.Web/client/cameraPresets.ts`
- Read: `src/ResQ.Viz.Web/client/assets/AssetManager.ts`
- Read: `src/ResQ.Viz.Web/client/assets/AssetPanel.ts`
- Read: `src/ResQ.Viz.Web/client/assets/AssetFilter.ts`
- Read: `src/ResQ.Viz.Web/client/assets/fleetUi.ts`
- Read: `src/ResQ.Viz.Web/client/assets/overlays/*.ts`
- Read: `src/ResQ.Viz.Web/client/editor/*.ts`
- Read: `src/ResQ.Viz.Web/client/scenarioEnvironments.ts`
- Read: `src/ResQ.Viz.Web/client/sensorStatsOverlay.ts`
- Read: `src/ResQ.Viz.Web/client/sensors/onboardPip.ts`

- [ ] **Step 1: Explain the live operating picture**

Describe domain-neutral asset selection, domain renderers, fleet filters, the capability-driven asset panel, overlays, event log, mini-map, cameras, cockpit/FPV views, editor dock, DVR, recorder, inspector, outliner, gizmo, and scene JSON import/export. Make clear which controls operate simulation state and which affect only the browser view.

- [ ] **Step 2: Build the complete scenario table from configuration**

Generate names plus air/ground/surface counts with:

```bash
jq -r '
  .Scenarios | to_entries[] |
  .key as $name |
  (.value | map(.domain // "Air") | group_by(.) | map({(.[0]): length}) | add) as $counts |
  [$name, ($counts.Air // 0), ($counts.Ground // 0), ($counts.Surface // 0), (.value | length)] |
  @tsv
' \
  src/ResQ.Viz.Web/appsettings.json
```

The collapsed table must contain all 19 names: `single`, `swarm-5`, `swarm-20`, `sar`, `multi-agency-sar`, `wildfire-interface`, `hurricane-melissa`, `flood-riverine`, `urban-collapse`, `alpine-sar`, `canyon-sar`, `mixed-ground`, `ground-convoy`, `coastal-search`, `coastal-transit`, `flood-response`, `port-incident`, `link-loss-divergence`, and `mixed-load-150`. Report air/ground/surface composition rather than only total rows for every mixed scenario.

- [ ] **Step 3: Document environment coupling accurately**

List the six browser-bound disaster environments from `SCENARIO_ENVIRONMENTS`: wildfire, hurricane, riverine flood, urban collapse, alpine SAR, and canyon SAR. The browser always applies a bound atmosphere and camera. A manual terrain override suppresses only the automatic terrain and water switch. State that the backend scenario endpoint only places assets. An unbound scenario retains the browser's current environment rather than selecting a default. Mark `coastal-search`, `coastal-transit`, and `port-incident` as authored for coastal terrain. Mark `mixed-ground`, `ground-convoy`, `flood-response`, `link-loss-divergence`, and `mixed-load-150` as authored for alpine terrain. A fresh room starts alpine, but an operator who changed terrain must restore the matching environment.

- [ ] **Step 4: Build the complete live-control table from handlers**

Document mouse orbit/pan/zoom and RMB free-fly, scenario/reset/sidebar commands, camera presets and chase modes, overlays, cockpit, map, focus/home, playback, PIP, editor dock, manual air piloting, help, and escape behavior. Verify each row against a production-instantiated handler, not the stale help markup.

Call out three live collisions without implying a code fix. Plain `I` toggles both cockpit and sensor stats. `Space` can both toggle DVR playback and move upward while RMB free-fly is active. `Ctrl+Shift+R` reaches both the document-level reset handler and investor-mode handler because the reset handler does not reject modifiers. Do not report a Transport/DVR `.` collision because `editor/transport.ts` is not instantiated by the production app.

- [ ] **Step 5: Verify scenario and shortcut completeness**

Run:

```bash
test "$(jq '.Scenarios | length' src/ResQ.Viz.Web/appsettings.json)" -eq 19

readme_expected_scenarios=$(mktemp /tmp/resq-viz-scenarios-expected.XXXXXX)
readme_actual_scenarios=$(mktemp /tmp/resq-viz-scenarios-actual.XXXXXX)

jq -r '
  .Scenarios | to_entries[] |
  .key as $name |
  (.value | map(.domain // "Air") | group_by(.) | map({(.[0]): length}) | add) as $counts |
  [$name, ($counts.Air // 0), ($counts.Ground // 0), ($counts.Surface // 0)] | @tsv
' src/ResQ.Viz.Web/appsettings.json | sort > "$readme_expected_scenarios"

awk -F '|' '/^\| `[a-z0-9-]+` \| [0-9]+ \| [0-9]+ \| [0-9]+ \|/ {
  for (i = 2; i <= 5; i++) {
    gsub(/^[[:space:]`]+|[[:space:]`]+$/, "", $i)
  }
  print $2 "\t" $3 "\t" $4 "\t" $5
}' README.md | sort > "$readme_actual_scenarios"

if ! diff -u "$readme_expected_scenarios" "$readme_actual_scenarios"
then
  rm -f "$readme_expected_scenarios" "$readme_actual_scenarios"
  exit 1
fi
rm -f "$readme_expected_scenarios" "$readme_actual_scenarios"

rg -n 'keydown|keyup|e\.code|e\.key|addEventListener' \
  src/ResQ.Viz.Web/client/app.ts \
  src/ResQ.Viz.Web/client/controls.ts \
  src/ResQ.Viz.Web/client/cameraControl.ts \
  src/ResQ.Viz.Web/client/editor/dock.ts \
  src/ResQ.Viz.Web/client/editor/dvr.ts \
  src/ResQ.Viz.Web/client/sensorStatsOverlay.ts \
  src/ResQ.Viz.Web/client/sensors/onboardPip.ts

rg -n 'single|mixed-load-150|coastal-search|flood-response|link-loss-divergence|plain `I`|RMB free-fly|Ctrl\+Shift\+R|manual.*override|retains.*current environment' README.md
git diff --check -- README.md
```

Expected: the README scenario names and domain counts match all 19 configuration rows, environment selection rules match browser code, every shortcut has a live handler, all three current collisions are disclosed, and Git reports no whitespace errors.

- [ ] **Step 6: Commit the operator guide**

```bash
git add README.md
git commit -m "docs: add operator workspace and scenarios"
```

### Task 8: Write the current system architecture

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/Program.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationManager.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationManager.Broadcast.cs`
- Read: `src/ResQ.Viz.Web/Services/SimulationRoom*.cs`
- Read: `src/ResQ.Viz.Web/Services/VizFrameBuilder.cs`
- Read: `src/ResQ.Viz.Web/Services/VizSnapshotV2Builder.cs`
- Read: `src/ResQ.Viz.Web/Services/SignalRFrameBroadcaster.cs`
- Read: `src/ResQ.Viz.Web/client/app.ts`
- Read: `src/ResQ.Viz.Web/client/assets/AssetRegistry.ts`
- Read: `src/ResQ.Viz.Web/client/assets/domainRegistration.ts`
- Read: `src/ResQ.Viz.Web/client/assets/sceneFrame.ts`
- Read: `src/ResQ.Viz.Web/client/terrain.ts`
- Read: `src/ResQ.Viz.Web/client/geoCache.ts`
- Read: `src/ResQ.Viz.Web/client/postfx.ts`
- Read: `src/ResQ.Viz.Web/client/postfxDeferred.ts`
- Read: `src/ResQ.Viz.Web/client/webgpu/*.ts`

- [ ] **Step 1: Replace the obsolete single-world architecture**

Explain that `SimulationManager` owns isolated rooms and the 60 Hz host loop, while each `SimulationRoom` owns assets, terrain, weather, tracks, coordinator, command state, and stream state. Explain the `viz_session` room binding, 100-room cap, 60-second idle reap, REST controllers, and SignalR groups. Do not mention the removed `SimulationService` architecture as current.

- [ ] **Step 2: Add the system-context Mermaid diagram**

Draw browser clients, the HTTPS REST surface, `/viz` SignalR hub, room session binding, `SimulationManager`, isolated rooms, frame builders, command/authority services, domain assets, and SDK physics. Show v1 and v2 browser paths separately and route every broadcast through room-scoped groups.

- [ ] **Step 3: Explain the browser composition**

Describe `app.ts` orchestration, `AssetManager`, lazy domain registration, renderer registry/fallback, one scene-frame projection shared by renderers/overlays/cameras, capability panel, editor modules, and analytics startup. Avoid repeating the operator catalog.

- [ ] **Step 4: Keep four compact technical tours**

Retain concise prose on terrain generation and erosion, two-level geometry caching, WebGPU sensor fallback, and deferred post-processing. Correct the cache wording: L1 is in-memory and L2 is per-tab `sessionStorage`. At 500 segments the cached height field has 501×501 float values, or 1,004,004 raw bytes (about 0.96 MiB), before compression and base64 encoding. Do not reuse the stale three-component-buffer estimates in old comments.

- [ ] **Step 5: Verify architecture names and Mermaid count**

Run:

```bash
rg -n 'SimulationService|SimulationManager|SimulationRoom|AssetManager|AssetRegistry|sceneFrame|sessionStorage|1,004,004|0\.96|/viz' README.md
test "$(rg -c '^```mermaid$' README.md)" -eq 4
git diff --check -- README.md
```

Expected: `SimulationService` appears only if explicitly labeled historical, current component names match source, the README has exactly four Mermaid diagrams, the cache calculation uses one height scalar per vertex, and Git reports no whitespace errors.

- [ ] **Step 6: Commit the architecture guide**

```bash
git add README.md
git commit -m "docs: map the current Viz architecture"
```

## Chunk 4: Contributors, deployment facts, references, and final verification

### Task 9: Write the contributor workflow and repository map

**Files:**
- Modify: `README.md`
- Read: `ResQ.Viz.sln`
- Read: `src/ResQ.Viz.Web/ResQ.Viz.Web.csproj`
- Read: `tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj`
- Read: `src/ResQ.Viz.Web/package.json`
- Read: `src/ResQ.Viz.Web/vite.config.ts`
- Read: `.github/workflows/ci.yml`
- Read: `.github/workflows/security.yml`
- Read: `.git-hooks/local-pre-push`
- Read: `AGENTS.md`
- Read: `SECURITY.md`

- [ ] **Step 1: Write exact prerequisites and setup**

List Git with submodule support, .NET 10 SDK, Node.js 22, npm, and a browser with WebGL 2. Explain that WebGPU paths are optional and have browser fallbacks. Use `git submodule update --init --recursive` for an existing checkout and `npm ci --legacy-peer-deps` for a clean client install.

- [ ] **Step 2: Write build, test, and format commands**

Document repository-root commands:

```bash
dotnet restore ResQ.Viz.sln
dotnet build ResQ.Viz.sln -c Debug --no-restore
dotnet build ResQ.Viz.sln -c Release --no-restore
dotnet test tests/ResQ.Viz.Web.Tests/ResQ.Viz.Web.Tests.csproj -c Release --no-build --no-restore
dotnet format ResQ.Viz.sln --no-restore --verify-no-changes
```

Document client commands from `src/ResQ.Viz.Web`:

```bash
npm ci --legacy-peer-deps
npm run typecheck
npm test
npm run build
```

Explain that Release invokes the client build through MSBuild. Debug omits that build, and an explicit Development runtime starts the Vite development server.

- [ ] **Step 3: Explain CI, performance gates, and reproducibility**

Separate permanent gates from the dated baseline. Permanent gates include 150-asset 60 Hz step and 10 Hz frame ceilings, no more than 25× cost for a tenfold fleet, delta payload ratios below 90% underway and 25% holding, entry JavaScript at most 819,200 bytes, and entry CSS at most 53,248 bytes. Show the filtered `ReplayDeterminismTests` and `MixedFleetLoadTests` commands, then show entry-asset byte measurement with `stat` after `npm run build`.

- [ ] **Step 4: Add contribution workflow and hook installation**

Describe branch-local changes, Apache headers on C# files, XML docs on public APIs, xUnit plus FluentAssertions, the central Vitest suite under `src/ResQ.Viz.Web/client/__tests__/`, source-derived API claims, and the canonical hook installer already named in `AGENTS.md`. Link to the organization development guide rather than copying its contract.

- [ ] **Step 5: Build the collapsed repository map**

Use `rg --files` to verify each path, then map:

- host composition, controllers, hub, services, models, and client.
- domain assets and tracks.
- asset manager, renderers, overlays, editor, sensors, WebGPU, and styles.
- backend and client tests.
- SDK submodule, CI, hooks, docs, security policy, license, and Docker/deployment files when present.

Do not list generated `wwwroot/assets` files as source.

- [ ] **Step 6: Verify every contributor command against configuration**

Run:

```bash
set -Eeuo pipefail

rg -n 'dotnet (restore|build|test|format)|npm (ci|run typecheck|test|run build)|819,200|53,248|25×|90%|25%' README.md
rg -n 'dotnet-version|node-version|BUNDLE_JS_BUDGET_BYTES|BUNDLE_CSS_BUDGET_BYTES|MixedFleetLoadTests|ReplayDeterminismTests' \
  .github/workflows/ci.yml \
  tests/ResQ.Viz.Web.Tests/*.cs
git diff --check -- README.md
```

Expected: every documented command maps to a repository script, permanent ceilings match CI/tests, dated values are not framed as guarantees, and Git reports no whitespace errors.

- [ ] **Step 7: Commit the contributor guide**

```bash
git add README.md
git commit -m "docs: add contributor workflow and gates"
```

### Task 10: Write security, privacy, observability, and deployment facts

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/Program.cs`
- Read: `src/ResQ.Viz.Web/appsettings.json`
- Read: `src/ResQ.Viz.Web/SecurityConstants.cs`
- Read: `src/ResQ.Viz.Web/Controllers/SessionController.cs`
- Read: `src/ResQ.Viz.Web/Services/RoomSessionService.cs`
- Read: `src/ResQ.Viz.Web/Services/ControlAuthorityRegistry.cs`
- Read: `src/ResQ.Viz.Web/Services/VizTelemetry.cs`
- Read: `src/ResQ.Viz.Web/client/analytics.ts`
- Read: `src/ResQ.Viz.Web/client/settings.ts`
- Read: `src/ResQ.Viz.Web/client/geoCache.ts`
- Read: `src/ResQ.Viz.Web/client/app.ts`
- Read: `src/ResQ.Viz.Web/client/editor/dock.ts`
- Read: `src/ResQ.Viz.Web/client/assets/AssetFilter.ts`
- Read: `src/ResQ.Viz.Web/client/editor/sceneConfig.ts`
- Read: `SECURITY.md`

- [ ] **Step 1: Write the simulation-only security boundary**

State that startup rejects a control configuration that claims live attachment. Explain room isolation, the 24-hour protected `viz_session`, HttpOnly/Secure/SameSite Strict flags, /24 IPv4 or /64 IPv6 prefix binding, session refresh, bounded idle cleanup, destructive/general rate limits, security headers, HTTPS redirection, and production-only HSTS. Keep this operational and avoid legal compliance conclusions.

- [ ] **Step 2: Write the complete browser and export data flow**

List visual settings, dismissed hints, cockpit state, editor-dock state, and fleet filters in `localStorage`. List compressed terrain geometry under `resq-geo-v1-*` in `sessionStorage`. State that scene and environment choices move through explicit `resq-scene.json` export/import rather than browser persistence.

- [ ] **Step 3: Write analytics and telemetry conditions**

State that PostHog and GA4 initialize only when their build-time variables are present, Cloudflare Web Analytics may be injected by the deployment platform and is permitted by CSP, and OpenTelemetry exports only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Name the useful Viz meter/activity families from `VizTelemetry.cs` without promising a specific collector.

- [ ] **Step 4: Add deployment notes a maintainer can act on**

Cover shared ASP.NET data-protection keys for multi-instance or rolling deployment, forwarded-header trust, secure-cookie dependence on HTTPS, default ports 5000/5001, the placeholder geographic origin, and in-memory room/audit lifetime. Link `SECURITY.md` for vulnerability reporting.

- [ ] **Step 5: Verify each data-flow statement**

Run:

```bash
set -Eeuo pipefail

rg -n 'viz_session|localStorage|sessionStorage|resq-scene\.json|PostHog|GA4|Cloudflare|OTEL_EXPORTER_OTLP_ENDPOINT|data.protection|5000|5001|placeholder' \
  README.md \
  src/ResQ.Viz.Web/Program.cs \
  src/ResQ.Viz.Web/Controllers/SessionController.cs \
  src/ResQ.Viz.Web/Services/RoomSessionService.cs \
  src/ResQ.Viz.Web/client

git diff --check -- README.md

readme_privacy_patterns=(
  'viz_session'
  'localStorage.*visual|visual.*localStorage'
  'localStorage.*hint|hint.*localStorage'
  'localStorage.*cockpit|cockpit.*localStorage'
  'localStorage.*editor|editor.*localStorage'
  'localStorage.*fleet filter|fleet filter.*localStorage'
  'sessionStorage.*geometry|geometry.*sessionStorage'
  'resq-scene\.json'
  'PostHog.*build|build.*PostHog'
  'GA4.*build|build.*GA4'
  'Cloudflare Web Analytics.*inject|inject.*Cloudflare Web Analytics'
  'OTEL_EXPORTER_OTLP_ENDPOINT'
  '5000.*5001|5001.*5000'
  'origin.*placeholder|placeholder.*origin'
)

for readme_pattern in "${readme_privacy_patterns[@]}"
do
  rg -qi "$readme_pattern" README.md
done

if rg -ni 'GDPR|HIPAA|PCI(?:-DSS)?|legally compliant|compliance guarantee' README.md
then
  echo 'Remove legal-compliance conclusions from README.md.' >&2
  exit 1
fi
```

Expected: the README names every persisted category and optional exporter, does not claim zero cookies or analytics, contains no GDPR/HIPAA/PCI conclusion, and Git reports no whitespace errors.

- [ ] **Step 6: Commit the deployment guide**

```bash
git add README.md
git commit -m "docs: state deployment and data flows"
```

### Task 11: Complete the collapsed reference contract

**Files:**
- Modify: `README.md`
- Read: `src/ResQ.Viz.Web/Controllers/*.cs`
- Read: `src/ResQ.Viz.Web/Hubs/*.cs`
- Read: `src/ResQ.Viz.Web/Models/SimCommand.cs`
- Read: `src/ResQ.Viz.Web/Models/SimCommandV2.cs`
- Read: `src/ResQ.Viz.Web/Models/SimCommandTracks.cs`
- Read: `src/ResQ.Viz.Web/Models/ControlLeaseApi.cs`
- Read: `src/ResQ.Viz.Web/Models/AssetLinkApi.cs`
- Read: `src/ResQ.Viz.Web/Models/AssetCommand.cs`
- Read: `src/ResQ.Viz.Web/Services/CommandCatalog.cs`
- Read: `src/ResQ.Viz.Web/Program.cs`
- Read: `src/ResQ.Viz.Web/client/app.ts`

- [ ] **Step 1: Build the full HTTP REST matrix**

Create collapsed tables for:

1. session creation/info/deletion.
2. v1 lifecycle, transport, drone, weather, fault, mesh, state, scenario, preset, heightmap, and eroded-terrain routes.
3. v2 snapshot, assets, capabilities, commands, command status, control mode/audit/leases, per-asset link, and tracks.

Each row needs method, exact path, purpose, main request body or query, success status, and important refusal/status codes. State that the v1 fault route validates and logs a request but does not inject a simulated fault or write the durable v2 command-audit trail in the current build.

Use the exact `<summary>HTTP REST reference (44 actions)</summary>` label. Each data row begins `| METHOD | \`/exact/path\` |` so the coverage gate can compare it with the source manifest.

- [ ] **Step 2: Build the separate SignalR table**

List hub path `/viz`, server events `ReceiveFrame`, `ReceiveSnapshotV2`, and `ReceiveDeltaV2`, and client calls `SubscribeSnapshots(bool)`, `SubscribeDeltas(bool)`, and `RequestKeyframe()`. Include room-cookie binding and opt-in behavior. Both subscription methods return the schema version, while `RequestKeyframe` returns a Boolean. The first delta subscription on a connection receives one free opening keyframe. Later re-subscribe/rejoin and `RequestKeyframe` share the five-requests-per-ten-seconds budget. An exhausted in-place re-subscribe returns the schema without forcing a frame. An exhausted fresh rejoin throws `HubException` and leaves the connection's prior subscription state unchanged. The production client has full-snapshot intent before delta opt-in, so it remains on full snapshots in that case. An exhausted or inapplicable `RequestKeyframe` returns `false`. Reconnect creates new connection state and requires resubscription.

Use `<summary>SignalR contract (6 messages)</summary>`. Each data row begins `| Server event | \`Name\` |` or `| Client call | \`Name\` |`.

- [ ] **Step 3: Build the command and response reference**

Group every registered command by common/air/ground/surface. For each, record allowed domain, target/parameters, capability gate, lifecycle policy, and freshness requirement. Put `followRoute` and `setSteering` in a reserved/unregistered note rather than the callable table. Add a structured-problem glossary and command lifecycle states from the wire models.

Scope the glossary to the machine-readable families a caller can act on: request/payload/deadline; asset/capability/domain/state/freshness; idempotency; control authority; link and held-position safety; execution; capacity and not-found. Explain the shared `code`, optional downstream `reasonCode`, `field`, `traceId`, `assetId`, and `commandId` fields without enumerating private log text.

Use `<summary>Command catalog (19 registered commands)</summary>`. Each callable row begins `| \`wireToken\` |`. Keep the reserved note outside that callable table.

- [ ] **Step 4: Reconcile references already placed in operator sections**

Keep scenario and live-control tables where an operator first needs them, but wrap them in `<details>` and link them from the reference contents. Do not duplicate the 19 rows or shortcut table in a second appendix. Keep the directory map in the contributor section under the same collapsed treatment.

- [ ] **Step 5: Verify all 44 HTTP actions with an exact manifest**

Run:

```bash
set -Eeuo pipefail

test "$(rg '^\s*\[Http(Get|Post|Delete)' src/ResQ.Viz.Web/Controllers/*.cs | wc -l)" -eq 44

readme_expected_http=$(mktemp /tmp/resq-viz-http-expected.XXXXXX)
readme_actual_http=$(mktemp /tmp/resq-viz-http-actual.XXXXXX)

printf '%s\n' \
  'POST /api/sim/session' \
  'GET /api/sim/session/info' \
  'DELETE /api/sim/session' \
  'POST /api/sim/start' \
  'POST /api/sim/stop' \
  'POST /api/sim/reset' \
  'POST /api/sim/pause' \
  'POST /api/sim/resume' \
  'POST /api/sim/step' \
  'POST /api/sim/speed' \
  'GET /api/sim/transport' \
  'POST /api/sim/drone' \
  'POST /api/sim/drone/{id}/cmd' \
  'POST /api/sim/weather' \
  'POST /api/sim/fault' \
  'POST /api/sim/mesh/backhaul' \
  'GET /api/sim/mesh/backhaul' \
  'GET /api/sim/state' \
  'GET /api/sim/scenarios' \
  'POST /api/sim/scenario/{name}' \
  'POST /api/sim/preset/{key}' \
  'POST /api/sim/heightmap' \
  'DELETE /api/sim/heightmap' \
  'GET /api/sim/terrain/eroded' \
  'POST /api/v2/sim/assets/{id}/commands' \
  'GET /api/v2/sim/commands/{commandId}' \
  'GET /api/v2/sim/snapshot' \
  'GET /api/v2/sim/tracks' \
  'GET /api/v2/sim/tracks/{trackId}' \
  'POST /api/v2/sim/tracks' \
  'GET /api/v2/sim/assets/{id}/link' \
  'POST /api/v2/sim/assets/{id}/link' \
  'GET /api/v2/sim/control/mode' \
  'GET /api/v2/sim/control/audit' \
  'GET /api/v2/sim/assets/{id}/control' \
  'POST /api/v2/sim/assets/{id}/control' \
  'POST /api/v2/sim/assets/{id}/control/renew' \
  'POST /api/v2/sim/assets/{id}/control/release' \
  'POST /api/v2/sim/assets/{id}/control/preempt' \
  'GET /api/v2/sim/assets' \
  'POST /api/v2/sim/assets' \
  'GET /api/v2/sim/assets/{id}' \
  'DELETE /api/v2/sim/assets/{id}' \
  'GET /api/v2/sim/assets/{id}/capabilities' \
  | sort > "$readme_expected_http"

awk -F '|' '
  $0 == "<summary>HTTP REST reference (44 actions)</summary>" {
    inside = 1
    next
  }
  inside && /^<\/details>$/ { exit }
  inside && /^\| (GET|POST|DELETE) \| `/ {
    for (i = 2; i <= 3; i++) {
      gsub(/^[[:space:]`]+|[[:space:]`]+$/, "", $i)
    }
    print $2 " " $3
  }
' README.md | sort > "$readme_actual_http"

if ! diff -u "$readme_expected_http" "$readme_actual_http"
then
  rm -f "$readme_expected_http" "$readme_actual_http"
  exit 1
fi
rm -f "$readme_expected_http" "$readme_actual_http"
```

Expected: source still declares 44 actions and the README contains each exact public method/path pair once.

- [ ] **Step 6: Verify all registered commands and SignalR messages**

Run:

```bash
set -Eeuo pipefail

test "$(rg -oP 'Def\(CommandKinds\.\K[A-Za-z]+' src/ResQ.Viz.Web/Services/CommandCatalog.cs | sort -u | wc -l)" -eq 19

readme_expected_commands=$(mktemp /tmp/resq-viz-commands-expected.XXXXXX)
readme_actual_commands=$(mktemp /tmp/resq-viz-commands-actual.XXXXXX)

printf '%s\n' dock driveTo emergencyStop goTo hold land loiter park resumeAutonomy \
  returnToBase reverse setAltitude setCourse setSpeed stationKeep stop takeoff transitTo undock \
  | sort > "$readme_expected_commands"

awk -F '|' '
  $0 == "<summary>Command catalog (19 registered commands)</summary>" {
    inside = 1
    next
  }
  inside && /^<\/details>$/ { exit }
  inside && /^\| `[A-Za-z]+` \|/ {
    gsub(/^[[:space:]`]+|[[:space:]`]+$/, "", $2)
    print $2
  }
' README.md | sort > "$readme_actual_commands"

if ! diff -u "$readme_expected_commands" "$readme_actual_commands"
then
  rm -f "$readme_expected_commands" "$readme_actual_commands"
  exit 1
fi

if rg -n 'followRoute|setSteering' README.md | rg -vi 'reserved|unregistered|not registered'
then
  echo 'Reserved commands appear outside an explicitly reserved context.' >&2
  exit 1
fi

rm -f "$readme_expected_commands" "$readme_actual_commands"

readme_signalr=$(mktemp /tmp/resq-viz-signalr.XXXXXX)
awk -F '|' '
  $0 == "<summary>SignalR contract (6 messages)</summary>" {
    inside = 1
    next
  }
  inside && /^<\/details>$/ { exit }
  inside && /^\| (Server event|Client call) \| `/ {
    for (i = 2; i <= 3; i++) {
      gsub(/^[[:space:]`]+|[[:space:]`]+$/, "", $i)
    }
    print $2 "\t" $3
  }
' README.md | sort > "$readme_signalr"

if ! diff -u <(printf '%s\n' \
    $'Client call\tRequestKeyframe' \
    $'Client call\tSubscribeDeltas' \
    $'Client call\tSubscribeSnapshots' \
    $'Server event\tReceiveDeltaV2' \
    $'Server event\tReceiveFrame' \
    $'Server event\tReceiveSnapshotV2') "$readme_signalr"
then
  rm -f "$readme_signalr"
  exit 1
fi
rm -f "$readme_signalr"

git diff --check -- README.md
```

Expected: the callable table matches all 19 registered wire tokens, reserved names occur only in reserved wording, the SignalR table has the six exact names, and Git reports no whitespace errors.

- [ ] **Step 7: Add license and project links**

End with Apache-2.0, `LICENSE`, `SECURITY.md`, issue tracker, organization profile, SDK submodule repository, and canonical development guide links. Do not add a generic conclusion after these destinations.

- [ ] **Step 8: Commit the reference material**

```bash
git add README.md
git commit -m "docs: complete API and operator references"
```

### Task 12: Run the documentation verification gate

**Files:**
- Modify: `README.md` only when a check finds an issue
- Verify: `README.md`
- Verify: `docs/superpowers/specs/2026-09-01-readme-overhaul-design.md`
- Verify: `docs/superpowers/plans/2026-09-01-readme-overhaul.md`

- [ ] **Step 1: Enforce target length and punctuation limits**

Run:

```bash
set -Eeuo pipefail

words=$(wc -w < README.md)
em_dashes=$( (rg -o '—' README.md || true) | wc -l )
semicolons=$( (rg -o ';' README.md || true) | wc -l )

test "$words" -ge 7200
test "$words" -le 8800
test $((em_dashes * 1000)) -le $((words * 2))
test $((semicolons * 1000)) -le $((words * 3))

printf 'words=%s em_dashes=%s semicolons=%s\n' "$words" "$em_dashes" "$semicolons"
```

Expected: 7,200–8,800 words, at most two em dashes per 1,000 words, and at most three semicolons per 1,000 words.

- [ ] **Step 2: Run the @writing banned-language and structure scans**

Read `/home/wombocombo/.codex/skills/writing/SKILL.md` in full, then run every Pass 1 banned-word grep and structural-pattern grep from it with `FILE=README.md`. Any banned-language match must be rewritten. Inspect every structural flag in context and rewrite formulaic filler, restatement, unsupported comparison, or personification.

The skill references `Reference_Writing.md`, but that file is absent at the planning baseline. Confirm the state and record the fallback:

```bash
test -f /home/wombocombo/.codex/skills/writing/SKILL.md
if test -f /home/wombocombo/.codex/skills/writing/Reference_Writing.md
then
  echo 'Using Reference_Writing.md.'
else
  echo 'Reference_Writing.md is unavailable. Applying the complete SKILL.md rules directly.'
fi

if command -v markdownlint-cli2 >/dev/null 2>&1
then
  markdownlint-cli2 README.md
elif command -v markdownlint >/dev/null 2>&1
then
  markdownlint README.md
else
  echo 'No repository-provided or installed Markdown linter. Using structural and manual checks.'
fi
```

Expected: zero banned-language matches and no unexplained structural flags.

- [ ] **Step 3: Run the @writing rhythm and manual reviews**

Run the skill's checker directly without creating a repository file:

```bash
python3 - README.md <<'PY'
import re
import sys

text = open(sys.argv[1], encoding="utf-8").read()
paragraphs = [
    p.strip() for p in text.split("\n\n")
    if p.strip() and not p.strip().startswith("#")
]
violations = []
for index, paragraph in enumerate(paragraphs, start=1):
    sentences = re.split(r"(?<=[.!?])\s+", paragraph)
    if len(sentences) < 3:
        continue
    lengths = [len(sentence.split()) for sentence in sentences]
    for offset in range(len(lengths) - 2):
        window = lengths[offset:offset + 3]
        if all(words > 5 for words in window) and max(window) - min(window) <= 3:
            violations.append((index, offset + 1, window, paragraph))

if violations:
    print("RHYTHM FLAGS:")
    for paragraph, sentence, window, text in violations:
        print(f"paragraph {paragraph}, sentences {sentence}-{sentence + 2}: {window}")
        print(text)
else:
    print("No rhythm violations found.")
PY
```

Review each flag, ignoring tables and list rows only when their parallel cadence is intentional. Then read the full README from top to bottom and apply the skill's Pass 2 and Pass 3 checklists.

Expected: no unresolved prose-rhythm flag, duplicate conclusion, unsupported number, synonym cycling, audience mismatch, or vague heading.

- [ ] **Step 4: Extract links once and verify local targets plus anchors**

Run:

```bash
set -Eeuo pipefail

test "$(rg '^```mermaid$' README.md | wc -l)" -eq 4
test "$(rg '^<details>$' README.md | wc -l)" -eq "$(rg '^</details>$' README.md | wc -l)"
test "$(jq '.Scenarios | length' src/ResQ.Viz.Web/appsettings.json)" -eq 19

readme_links=$(mktemp /tmp/resq-viz-readme-links.XXXXXX)
perl -ne '
  while (/\[[^]]*\]\(([^)[:space:]]+)(?:[[:space:]]+"[^"]*")?\)/g) { print "$1\n" }
  while (/\b(?:href|src)="([^"]+)"/g) { print "$1\n" }
' README.md | sort -u > "$readme_links"

if rg -n '^\[[^]]+\]:[[:space:]]|\[[^]]+\]\[[^]]*\]|\]\(<[^>]+>\)|<https?://' README.md
then
  echo 'Use inline Markdown links or double-quoted HTML href/src attributes only.' >&2
  exit 1
fi

while IFS= read -r readme_target
do
  if [[ "$readme_target" == https://* ]]
  then
    continue
  elif [[ "$readme_target" == \#* ]]
  then
    readme_anchor=${readme_target#\#}
    rg -Fq "id=\"$readme_anchor\"" README.md
  else
    if [[ "$readme_target" == *#* ]]
    then
      echo "Relative file fragments are not accepted by this checker: $readme_target" >&2
      exit 1
    fi
    readme_path=${readme_target%%#*}
    test -n "$readme_path"
    test -e "$readme_path"
  fi
done < "$readme_links"

rm -f "$readme_links"

git diff --check
```

Expected: four Mermaid diagrams, balanced details tags, 19 source scenarios, every relative file exists, every in-document link names an explicit anchor, and Git reports no whitespace errors.

- [ ] **Step 5: Verify every extracted external URL with bounded requests**

Run:

```bash
set -Eeuo pipefail

readme_urls=$(mktemp /tmp/resq-viz-readme-urls.XXXXXX)
perl -ne '
  while (/\[[^]]*\]\((https:\/\/[^)[:space:]]+)/g) { print "$1\n" }
  while (/\b(?:href|src)="(https:\/\/[^"]+)"/g) { print "$1\n" }
' README.md | sort -u > "$readme_urls"

while IFS= read -r readme_url
do
  if ! curl --connect-timeout 10 --max-time 30 -fsSLI "$readme_url" >/dev/null
  then
    echo "HEAD refused. Checking with GET: $readme_url"
    curl --connect-timeout 10 --max-time 30 -fsSL "$readme_url" -o /dev/null
  fi
done < "$readme_urls"

rg -Fxq 'https://raw.githubusercontent.com/resq-software/.github/main/assets/banner.png' "$readme_urls"
rm -f "$readme_urls"
```

Expected: every external link resolves. Record any host that required the GET fallback.

- [ ] **Step 6: Extract and inspect or render each Mermaid diagram**

Run:

```bash
set -Eeuo pipefail

readme_mermaid=$(mktemp -d /tmp/resq-viz-mermaid.XXXXXX)
awk -v output_dir="$readme_mermaid" '
  /^```mermaid$/ {
    inside = 1
    count++
    output = sprintf("%s/diagram-%d.mmd", output_dir, count)
    next
  }
  inside && /^```$/ {
    inside = 0
    close(output)
    next
  }
  inside { print > output }
' README.md

test "$(find "$readme_mermaid" -maxdepth 1 -name 'diagram-*.mmd' -type f | wc -l)" -eq 4

if command -v mmdc >/dev/null 2>&1
then
  for readme_diagram in "$readme_mermaid"/*.mmd
  do
    mmdc -i "$readme_diagram" -o "${readme_diagram%.mmd}.svg"
  done
else
  echo 'No installed Mermaid renderer. Record manual fence/node/edge/subgraph/label inspection.'
fi

rm -rf "$readme_mermaid"
```

When no renderer exists, inspect every fence, node identifier, edge, subgraph, and quoted label and record the fallback in the final handoff.

Expected: all four diagrams have valid, self-contained syntax and distinct purposes: system context, frame streaming, command authority, and safe-action flow.

- [ ] **Step 7: Rerun factual manifests and boundary checks**

Rerun Task 7's scenario-composition diff, Task 10's persistence/analytics/legal scan, and Task 11's HTTP/command/SignalR manifests. Search README and source side by side for live shortcuts, tool versions, limits, and performance gates. Confirm these boundaries explicitly:

- simulation-only startup guard.
- all navigation-related output is advisory.
- `202` is accepted rather than completed.
- v1 bypasses the v2 gates during its deprecation cycle.
- displacement hull lacks `StationKeep`.
- default origin is a placeholder.
- environment-bound presets need the matching browser environment.
- `link-loss-divergence` does not cut links.
- the v1 fault route does not inject a fault.

Expected: every item appears in the reader-facing section where its omission could cause an operating mistake.

- [ ] **Step 8: Close the correction loop and enforce final scope**

If Steps 1–7 require any README edit, commit the correction and restart Task 12 at Step 1. Do not continue from the failed step. Repeat until one complete pass makes no file change.

The planning checkpoint must already have force-added the ignored spec and plan. Verify that decision, then enforce the allowlist:

```bash
set -Eeuo pipefail

git ls-files --error-unmatch docs/superpowers/specs/2026-09-01-readme-overhaul-design.md
git ls-files --error-unmatch docs/superpowers/plans/2026-09-01-readme-overhaul.md

readme_unexpected=$(git diff --name-only 4a4abd4...HEAD \
  | rg -v '^(README\.md|docs/superpowers/specs/2026-09-01-readme-overhaul-design\.md|docs/superpowers/plans/2026-09-01-readme-overhaul\.md)$' \
  || true)
test -z "$readme_unexpected"

git diff --check 4a4abd4...HEAD
test -z "$(git status --porcelain)"
git log --oneline --decorate 4a4abd4..HEAD
```

Expected: spec and plan are tracked, only the three allowed files differ from baseline, the worktree is clean, and the last full verification pass required no edits. Commit a correction only before restarting the gate:

```bash
git add README.md
git commit -m "docs: verify comprehensive README"
```

If no final correction was needed, do not create an empty commit.
