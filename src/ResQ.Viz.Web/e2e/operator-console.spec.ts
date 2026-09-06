// ResQ Viz - operator console reachability in a real browser
// SPDX-License-Identifier: Apache-2.0
//
// The Vitest suite already proves what the console *computes*. This suite exists
// for the three things a DOM emulator structurally cannot answer:
//
//   * **Reachability.** happy-dom has no layout and no compositor, so a rail
//     rendered underneath the WebGL canvas, or a roster with a zero-height
//     scroller, passes every unit test in the repository. `elementFromPoint` is
//     the only witness that a click would land where the operator aimed it.
//   * **Negotiation.** Legacy mode is entered when a real SignalR v2 opt-in is
//     really refused. That refusal comes from a second server process started by
//     `playwright.config.ts`; there is no client-side switch, and adding one
//     would have meant verifying a code path production never runs.
//   * **Focus.** `inert` is a browser behaviour. Whether Tab can walk into a
//     closed rail is a question only a browser answers.
//
// Each `test` gets its own BrowserContext from Playwright, which is what makes
// each case a fresh room: the room is bound to a `Secure` `viz_session` cookie,
// and a new context starts with no cookies.

import { expect } from '@playwright/test';

import {
  assetRow,
  contactRow,
  contextPanel,
  dvrFrameCount,
  enterReplay,
  FORCED_LEGACY_ORIGIN,
  goLive,
  hitTestOwn,
  legacySightings,
  NORMAL_ORIGIN,
  reportTrack,
  tabThrough,
  // `test` comes from the support module, not from `@playwright/test`: it
  // carries the auto fixture that records when the budget started, which is
  // what lets the boot waits stop before the test timeout does.
  test,
  waitForDvr,
  waitForLegacyConsole,
  waitForOperatorConsole,
  waitForSimulationAdvance,
  watchLegacyBranch,
  zIndexOf,
} from './support/operatorConsole';

/** `flood-response` places these three, one per domain, at fixed identifiers. */
const AIR_ASSET = 'fr-mapper-n';
const GROUND_ASSET = 'fr-supply-lead';
const SURFACE_ASSET = 'fr-ferry-1';

/** The contact this suite injects. Present in no configured scenario. */
const INJECTED_TRACK = 'browser-track-1';

test.describe('operator console — desktop', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('is reachable, selectable, and closed to mutation away from Live', async ({ page, context }) => {
    await watchLegacyBranch(context);
    await page.goto(NORMAL_ORIGIN);

    // ── A fresh room reaches v2 and shows its server-held scenario ──────────
    await waitForOperatorConsole(page, 8);
    await waitForDvr(page);
    await expect(page.locator('.operator-mission-title')).toHaveText(/Flood Response/i);

    const air = assetRow(page, AIR_ASSET);
    const ground = assetRow(page, GROUND_ASSET);
    const surface = assetRow(page, SURFACE_ASSET);
    for (const row of [air, ground, surface]) await expect(row).toBeVisible();

    // ── The rail and roster are laid out, painted above the scene, and hit ──
    const rail = page.locator('#sidebar');
    const roster = page.locator('#fleet-roster');
    for (const region of [rail, roster]) {
      const box = await region.boundingBox();
      expect(box, 'rail and roster must have a real box, not merely exist').not.toBeNull();
      expect(box!.width).toBeGreaterThan(0);
      expect(box!.height).toBeGreaterThan(0);
    }

    const railZ = await zIndexOf(rail);
    const sceneZ = await zIndexOf(page.locator('#scene-container'));
    expect(railZ, 'the rail must declare a stacking order, not inherit auto').not.toBeNull();
    expect(railZ!).toBeGreaterThan(sceneZ ?? 0);

    // The claim the z-index is only evidence for: a click at the row's own
    // centre reaches the row rather than the canvas over it.
    for (const row of [air, ground, surface]) {
      expect(await hitTestOwn(row), 'roster row must be the top hit at its centre').toBe(true);
    }

    // ── Selecting each domain opens body-level context for that entity ──────
    const panel = contextPanel(page);
    for (const [row, id, domain] of [
      [air, AIR_ASSET, 'Air'],
      [ground, GROUND_ASSET, 'Ground'],
      [surface, SURFACE_ASSET, 'Surface'],
    ] as const) {
      await row.click();
      await expect(panel).toBeVisible();
      await expect(panel.locator('.ap-domain')).toHaveText(domain);
      await expect(panel.locator('[data-card="identity"]')).toContainText(id);
    }

    // ── Advanced/Safety and Editor are closed until a labelled control asks ─
    const advanced = page.locator('#advanced-safety');
    const editorLayer = page.locator('#operator-editor-layer');
    await expect(advanced).not.toHaveAttribute('open', /.*/);
    await expect(editorLayer).toBeHidden();

    await advanced.locator('summary').click();
    await expect(advanced).toHaveAttribute('open', /.*/);
    // The four panels are a lazily fetched chunk. These three are the controls
    // the replay assertions below are about, so they have to exist first.
    const leaseAcquire = advanced.locator('[data-action="acquire"]');
    const linkCut = advanced.locator('[data-action="cut"]');
    const trackReport = advanced.locator('[data-action="report"]');
    await expect(leaseAcquire).toBeVisible();

    const editorToggle = page.locator('#btn-editor-toggle');
    await expect(editorToggle).toHaveText('Editor');
    await editorToggle.click();
    await expect(editorLayer).toBeVisible();
    // Closed again through the same control, so the rest of this case runs
    // against the layout an operator who never opens Editor actually sees.
    await editorToggle.click();
    await expect(editorLayer).toBeHidden();

    // ── An injected contact reaches Observed contacts ───────────────────────
    await reportTrack(page, INJECTED_TRACK, { x: 120, y: 5, z: -60 });
    const contact = contactRow(page, INJECTED_TRACK);
    await expect(contact).toBeVisible();
    await expect(
      contact.locator('xpath=ancestor::section[contains(@class,"ar-group")]')
        .locator('.ar-group-heading'),
    ).toContainText('Observed contacts');

    // ── Scrubbing away from Live keeps the picture and closes mutation ──────
    // The row above appeared on the very *first* recorded frame that carried the
    // contact, so scrubbing one frame back at this instant would land on the
    // last frame without it and test the wrong thing. Let the stream get a few
    // frames ahead of the ingestion first.
    await waitForSimulationAdvance(page, 0.5);
    await enterReplay(page);
    await expect(page.locator('.dvr-reclabel')).toHaveText('REPLAY');
    await expect(page.locator('.dvr-live')).toHaveText('GO LIVE');

    // Every domain and the contact survive the schema-tagged replay.
    for (const row of [air, ground, surface, contact]) await expect(row).toBeVisible();
    await expect(page.locator('#air-count')).toHaveText('3');
    await expect(page.locator('#ground-count')).toHaveText('3');
    await expect(page.locator('#surface-count')).toHaveText('2');

    // Mutations are closed in the two shapes this console actually uses.
    //
    // Shape one — the control goes inert and says why. Transport reset, the
    // lease, the link drill and the track report carry `disabled`; the selected
    // asset's commands carry `aria-disabled` instead, deliberately, so a
    // keyboard operator keeps their place and can still read the reason.
    await expect(page.locator('.dvr-reset')).toBeDisabled();
    await expect(leaseAcquire).toBeDisabled();
    await expect(linkCut).toBeDisabled();
    await expect(trackReport).toBeDisabled();

    const commandButtons = panel.locator('.ap-cmd button');
    expect(await commandButtons.count()).toBeGreaterThan(0);

    // EVERY command is inert. That is the guarantee, and it admits no exception.
    for (const button of await commandButtons.all()) {
      await expect(button).toHaveAttribute('aria-disabled', 'true');
      // ...and each says why: a control that goes grey with nothing to say reads
      // as broken rather than as closed.
      await expect(button).not.toHaveAttribute('title', '');
    }

    // Replay is the stated reason for the commands whose only blocker IS replay
    // — but not necessarily for all of them, and requiring it everywhere
    // contradicts the panel's own documented precedence. `AssetPanel` ranks what
    // the asset can do above whether the console is live, because an asset-level
    // refusal survives returning to Live: a moving vessel's Undock reads "not
    // available while active" whatever the transport is doing, and that is the
    // more actionable of the two sentences. The stricter assertion passed only
    // while no scenario shipped an asset with a command its own state refuses.
    const titles = await commandButtons.evaluateAll(
      (buttons) => buttons.map((button) => button.getAttribute('title') ?? ''));
    expect(
      titles.some((title) => /unavailable during replay/.test(title)),
      `replay must be the stated reason for the commands it blocks; saw ${JSON.stringify(titles)}`,
    ).toBe(true);

    // Shape two — the control still looks pressable, and the mutation is
    // refused at `OperatorActions`' gate rather than at the button. That is what
    // the shipped build does today: pressing Spawn asset, Environment, Change…
    // or mission Reset while replaying opens nothing and changes nothing. The
    // assertion is therefore on the effect, because the effect is the guarantee.
    // (That these four do not also read as unavailable is a real gap against the
    // design's own rule that a console must not offer a control it will refuse.
    // It is pinned here rather than papered over, so closing the gap will show
    // up as this comment going stale rather than as silence.)
    const modalLayer = page.locator('#operator-modal-layer');
    const missionTitle = await page.locator('.operator-mission-title').textContent();
    for (const refused of [
      page.locator('#btn-spawn-asset'),
      page.locator('#btn-environment'),
      page.locator('.operator-mission-actions [data-action="change"]'),
      page.locator('.operator-mission-actions [data-action="reset"]'),
    ]) {
      await refused.click();
    }
    await expect(modalLayer).toBeEmpty();
    await expect(page.locator('.dvr-reclabel')).toHaveText('REPLAY');
    await expect(page.locator('.operator-mission-title')).toHaveText(missionTitle ?? '');
    for (const row of [air, ground, surface, contact]) await expect(row).toBeVisible();

    // ── Returning to Live reopens them, after the resources are re-read ─────
    await goLive(page);
    await expect(page.locator('.dvr-reclabel')).toHaveText('REC');
    await expect(page.locator('.dvr-reset')).toBeEnabled();
    await expect(leaseAcquire).toBeEnabled();
    await expect(linkCut).toBeEnabled();
    await expect(trackReport).toBeEnabled();
    // The REPLAY refusal must not outlive the replay — that is what returning to
    // Live undoes, and all it undoes. A command its own asset refuses stays
    // refused and says so, which is the behaviour wanted: Undock on a moving
    // vessel is not something going Live can grant.
    await expect(panel.locator('.ap-cmd button[title*="unavailable during replay"]'))
      .toHaveCount(0);

    // Everything blocked only by replay is now open. Asserting that no command
    // is blocked AT ALL would be asserting something about the scenario's assets
    // rather than about the transport.
    const openCount = await commandButtons
      .filter({ has: page.locator(':scope[aria-disabled="false"]') }).count();
    expect(openCount, 'returning to Live must reopen the commands replay had closed')
      .toBeGreaterThan(0);
    await expect(
      advanced.getByText('Replay — this console is not at the live edge'),
    ).toHaveCount(0);

    // Nothing in any of that was the legacy console.
    expect(await legacySightings(page)).toEqual([]);
  });
});

test.describe('operator console — narrow', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('is a drawer and a sheet, and never traps focus in what it closed', async ({ page, context }) => {
    await watchLegacyBranch(context);
    await page.goto(NORMAL_ORIGIN);
    await waitForOperatorConsole(page, 8);
    await waitForDvr(page);

    // ── The rail is a drawer that is actually inside the viewport ───────────
    const rail = page.locator('#sidebar');
    await expect(rail).toBeVisible();
    const railBox = (await rail.boundingBox())!;
    expect(railBox.width).toBeGreaterThan(0);
    expect(railBox.x).toBeGreaterThanOrEqual(0);
    expect(railBox.x + railBox.width).toBeLessThanOrEqual(390);

    // ── Editor is unavailable here, and says so rather than opening nothing ─
    const editorToggle = page.locator('#btn-editor-toggle');
    await expect(editorToggle).toHaveAttribute('aria-disabled', 'true');
    await expect(editorToggle).toHaveAttribute('title', 'Desktop workspace required');
    await expect(editorToggle).toHaveAttribute('aria-describedby', 'editor-unavailable-note');
    await expect(page.locator('#editor-unavailable-note')).toHaveText('Desktop workspace required');

    // ── Selecting closes the rail and opens context as a bottom sheet ───────
    const surface = assetRow(page, SURFACE_ASSET);
    await expect(surface).toBeVisible();
    await surface.click();

    // The layer is a zero-height positioning host for an absolutely-positioned
    // panel, so it is the panel that has to be visible; the layer's own contract
    // is the pair of attributes that keep it out of the tab order when closed.
    const layer = page.locator('#operator-context-layer');
    await expect(layer).not.toHaveAttribute('hidden', /.*/);
    await expect(layer).not.toHaveAttribute('inert', /.*/);
    await expect(rail).toBeHidden();
    await expect(rail).toHaveAttribute('inert', '');

    const panel = contextPanel(page);
    await expect(panel).toBeVisible();
    await expect(panel.locator('.ap-title')).toHaveText(SURFACE_ASSET);
    const panelBox = (await panel.boundingBox())!;
    // A sheet, not a side dock: near-full width, seated against the bottom.
    expect(panelBox.width).toBeGreaterThan(390 * 0.85);
    expect(panelBox.y + panelBox.height).toBeGreaterThan(844 * 0.75);
    expect(panelBox.y + panelBox.height).toBeLessThanOrEqual(844);

    // ── Tab must never walk into the rail it just closed ────────────────────
    const stops = await tabThrough(page, 25);
    const trapped = stops.filter((stop) => stop.insideHidden);
    expect(trapped, `focus entered a hidden or inert subtree: ${JSON.stringify(trapped)}`)
      .toEqual([]);

    // ── Every primary target is at least 44 x 44 CSS pixels ────────────────
    const undersized = await page.evaluate(() => {
      const selector = [
        '#sidebar button', '#sidebar summary', '#sidebar a[href]',
        '.operator-context-layer button', '.operator-context-layer summary',
        '.operator-context-layer a[href]', '.resq-dvr button',
      ].join(',');
      return [...document.querySelectorAll(selector)]
        .map((element) => ({ element, box: element.getBoundingClientRect() }))
        // Zero-area elements are the ones not rendered at all right now — the
        // closed rail's contents, say — and a control nobody can see is not a
        // target that has to meet a target size.
        .filter(({ box }) => box.width > 0 && box.height > 0)
        .filter(({ box }) => box.width < 44 || box.height < 44)
        .map(({ element, box }) => ({
          description: `${element.tagName.toLowerCase()}`
            + `${element.id !== '' ? `#${element.id}` : ''}`
            + `.${String(element.className).trim().split(/\s+/).join('.')}`,
          width: Math.round(box.width),
          height: Math.round(box.height),
        }));
    });
    expect(undersized, 'primary targets below 44x44 CSS pixels').toEqual([]);

    // ── Closing context gives the row its focus back ────────────────────────
    await panel.locator('.ap-close').click();
    await expect(panel).toBeHidden();
    await expect(layer).toHaveAttribute('hidden', '');
    await expect(layer).toHaveAttribute('inert', '');
    await expect(rail).toBeVisible();
    await expect(surface).toBeFocused();

    expect(await legacySightings(page)).toEqual([]);
  });
});

test.describe('operator console — forced legacy', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('falls back to a labelled legacy branch when v2 is refused', async ({ page, context }) => {
    // Its own context, so the `Secure` room cookie this page is issued cannot
    // cross into the normal server's room: Chromium scopes cookies by host and
    // ignores the port, and both servers are 127.0.0.1.
    await watchLegacyBranch(context);
    await page.goto(FORCED_LEGACY_ORIGIN);

    // Reaching this branch takes a full SignalR negotiation whose v2 opt-in is
    // then refused — a boot-scale event, and the only one in this spec. Waiting
    // on it explicitly keeps the assertions below on the `expect` budget they
    // are actually sized for, instead of making the first of them carry the
    // transport as well. Nothing here is asserted; the assertion follows.
    await waitForLegacyConsole(page);

    const legacy = page.locator('#legacy-console');
    await expect(legacy).toBeVisible();
    const notice = page.locator('#legacy-mode-notice');
    await expect(notice).toBeVisible();
    await expect(notice).toHaveText('Legacy mode: v2 unavailable');

    // The v2 branch is not merely styled away — it is out of the accessibility
    // tree and out of the tab order, so nothing offers a v2 control that has no
    // stream behind it.
    const v2 = page.locator('#operator-v2-console');
    await expect(v2).toBeHidden();
    await expect(v2).toHaveAttribute('inert', '');
    await expect(page.locator('#hud-count-v2')).toBeHidden();
    await expect(page.locator('#hud-count-v1')).toBeVisible();

    // Only the v2 *subscription* failed. The hub connection itself is up, and
    // an empty legacy room loads `single` exactly once through the v1 path.
    await expect(page.locator('#conn-label')).toHaveText('Connected');
    await expect(page.locator('#drone-count')).toHaveText('1');
    await expect(page.locator('#drone-select option[value="drone-1"]')).toHaveCount(1);

    // At least one rendered v1 frame, evidenced by the simulation clock moving:
    // a stalled stream would hold whatever the first frame said.
    const firstClock = await page.locator('#sim-time').textContent();
    await expect(page.locator('#sim-time')).not.toHaveText(firstClock ?? '', { timeout: 20_000 });

    // The air controls the legacy branch exists to provide are operable, not
    // merely present.
    for (const control of [
      page.locator('#btn-start'),
      page.locator('#btn-stop'),
      page.locator('#btn-reset'),
      page.locator('.scenario-card[data-scenario="swarm-5"]'),
    ]) {
      await expect(control).toBeEnabled();
      expect(await hitTestOwn(control), 'legacy control must be the top hit at its centre')
        .toBe(true);
    }

    // The connection stayed up throughout: a DVR that never appeared, or a ring
    // stuck at zero, would mean the fallback only looked right.
    await waitForDvr(page);
    expect(await dvrFrameCount(page)).toBeGreaterThan(0);

    const sightings = await legacySightings(page);
    expect(sightings.length, 'the legacy branch must actually have been rendered')
      .toBeGreaterThan(0);
  });
});
