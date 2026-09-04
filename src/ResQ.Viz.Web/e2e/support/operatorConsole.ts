// ResQ Viz - shared vocabulary for the browser reachability suite
// SPDX-License-Identifier: Apache-2.0
//
// Everything here is a *reader* or a *driver* of the running console. There are
// deliberately no assertions in this file: a helper that asserted would let a
// spec pass without saying what it checked, and the whole point of this suite is
// that each spec states its own claim.
//
// Two rules the helpers exist to enforce:
//
//  1. **Wait on state, not on time.** Frames arrive at 10 Hz over SignalR and the
//     scene renders on `requestAnimationFrame`; every `waitForTimeout` in a suite
//     like this is a flake with a delay fuse. The waits below poll on a timer
//     (`polling: 100`) rather than Playwright's default `raf`, so a slow frame
//     stretches the render but never the state check.
//
//  2. **Reach the server the way the page does.** Room state is bound to the
//     `viz_session` cookie, which is `HttpOnly` and `Secure`. `page.context()
//     .request` shares that cookie jar, so an injected track or a scenario start
//     lands in the same room the page is watching — and a second browser context
//     is a genuinely different operator, which is what the forced-legacy case
//     needs so its cookie cannot cross ports into the normal server's room.

import type { BrowserContext, Locator, Page } from '@playwright/test';

export { FORCED_LEGACY_ORIGIN, NORMAL_ORIGIN } from '../../playwright.config';

/** Scene frame the console draws in: +X east, +Y up, +Z south. */
const COORDINATE_FRAME_LOCAL_EUS = 2;

/** `TrackSourceKind.OperatorEntered` — what the console's own report form sends. */
const TRACK_SOURCE_OPERATOR_ENTERED = 6;

/** Asset counts as the HUD publishes them. */
export interface DomainCounts {
  readonly air: number;
  readonly ground: number;
  readonly surface: number;
  readonly total: number;
}

/** One moment `#legacy-console` was rendered, as the page recorded it. */
export interface LegacySighting {
  readonly atMs: number;
  readonly width: number;
  readonly height: number;
}

declare global {
  interface Window {
    /** Written by {@link watchLegacyBranch}; read by {@link legacySightings}. */
    __resqLegacySightings?: LegacySighting[];
  }
}

/**
 * Records every moment the legacy branch is rendered, from before the app boots.
 *
 * Installed as an init script so it is running before `app.ts` evaluates: the
 * question "did the operator ever see legacy chrome?" cannot be answered by
 * looking at the DOM afterwards, because the shell would have swapped the branch
 * back and left no trace. A `MutationObserver` over the whole document catches
 * the `hidden` flip whichever code path makes it.
 *
 * Call before the first navigation, on the context rather than the page, so it
 * survives a reload.
 */
export async function watchLegacyBranch(context: BrowserContext): Promise<void> {
  await context.addInitScript(() => {
    const sightings: LegacySighting[] = [];
    window.__resqLegacySightings = sightings;

    const sample = (): void => {
      const element = document.getElementById('legacy-console');
      if (element === null || element.hidden) return;
      const style = getComputedStyle(element);
      if (style.display === 'none' || style.visibility === 'hidden') return;
      const box = element.getBoundingClientRect();
      if (box.width <= 0 || box.height <= 0) return;
      sightings.push({ atMs: Math.round(performance.now()), width: box.width, height: box.height });
    };

    const start = (): void => {
      sample();
      new MutationObserver(sample).observe(document.documentElement, {
        subtree: true,
        childList: true,
        attributes: true,
        attributeFilter: ['hidden', 'style', 'class', 'aria-hidden', 'inert'],
      });
    };

    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', start, { once: true });
    } else {
      start();
    }
  });
}

/** Every recorded legacy-branch sighting, oldest first. Empty means never shown. */
export async function legacySightings(page: Page): Promise<LegacySighting[]> {
  return page.evaluate(() => window.__resqLegacySightings ?? []);
}

/**
 * Waits until the v2 operator console owns the rail and its roster has rendered.
 *
 * `#operator-boot` being hidden is not enough on its own — the shell hides boot
 * the moment a stream is chosen, and the legacy branch is also "not booting".
 * This waits on the v2 branch specifically, and on at least one roster row,
 * because a branch with an empty roster is a console nobody can act through.
 */
export async function waitForOperatorConsole(page: Page, minimumRows = 1): Promise<void> {
  try {
    await page.waitForFunction(
      (rows: number) => {
        const v2 = document.getElementById('operator-v2-console');
        if (v2 === null || v2.hidden) return false;
        return document.querySelectorAll('.ar-row').length >= rows;
      },
      minimumRows,
      // Deliberately shorter than the 90-second per-test budget. Letting the
      // test timeout be what fires would tear the page down before the `catch`
      // below could read anything off it, and the diagnostic would report
      // "Target page, context or browser has been closed" instead of the state
      // that explains the failure. A wait that outlives its own error message
      // is not a diagnostic.
      { polling: 100, timeout: 45_000 },
    );
  } catch (cause) {
    // The one failure this wait has that is not about the console at all, and
    // it is worth naming rather than leaving as a 90-second mystery.
    //
    // A fresh room bootstraps itself with `POST /api/sim/session` and then a
    // default scenario start. Both routes sit on the server's `destructive`
    // rate limiter: one fixed window per *process*, ten requests per minute,
    // shared by every console connected to it and not partitioned by room or by
    // caller (`Program.cs`, `AddFixedWindowLimiter("destructive")`). Two calls
    // per fresh room means five rooms a minute; past that the session is still
    // issued but the scenario start is refused with 429, and the console comes
    // up connected, on the live edge, and empty. Measured directly: request ten
    // succeeds, request eleven is 429.
    //
    // A single suite run makes six such calls and is comfortably inside the
    // budget. `--repeat-each`, a retry storm, or repeated local runs against a
    // reused server are what exhaust it.
    const observed = await page.evaluate(() => ({
      branch: document.getElementById('operator-v2-console')?.hidden === false
        ? 'v2'
        : document.getElementById('legacy-console')?.hidden === false ? 'legacy' : 'boot',
      connection: document.getElementById('conn-label')?.textContent ?? null,
      mission: document.querySelector('.operator-mission-title')?.textContent ?? null,
      assetCount: document.getElementById('asset-count')?.textContent ?? null,
      rosterRows: document.querySelectorAll('.ar-row').length,
      simulationTime: document.getElementById('sim-time')?.textContent ?? null,
    }));
    throw new Error(
      `The v2 operator console never reached ${minimumRows} roster row(s). `
      + `Observed: ${JSON.stringify(observed)}. `
      + 'A connected, empty room with no mission means the room was created but its '
      + 'scenario start was refused: the server\'s "destructive" limiter is one '
      + 'process-wide fixed window of 10 requests per minute, and each fresh room '
      + 'spends two of them. Give the window a minute, or run fewer rooms per minute. '
      + `Underlying wait: ${String(cause)}`,
    );
  }
}

/** Waits for the deferred DVR bar, which is fetched after the first scene paint. */
export async function waitForDvr(page: Page): Promise<void> {
  await page.waitForFunction(
    () => document.querySelector('.resq-dvr') !== null,
    undefined,
    { polling: 100 },
  );
}

/** The roster row for one simulated asset. */
export function assetRow(page: Page, assetId: string): Locator {
  return page.locator(`.ar-row[data-roster-key="asset:${assetId}"]`);
}

/** The roster row for one observed contact. */
export function contactRow(page: Page, trackId: string): Locator {
  return page.locator(`.ar-row[data-roster-key="track:${trackId}"]`);
}

/** The body-level selection context panel. */
export function contextPanel(page: Page): Locator {
  return page.locator('#operator-context-layer .asset-panel');
}

/** Reads the HUD's published per-domain asset counts. */
export async function domainCounts(page: Page): Promise<DomainCounts> {
  return page.evaluate(() => {
    const read = (id: string): number => {
      const value = Number.parseInt(document.getElementById(id)?.textContent ?? '', 10);
      return Number.isFinite(value) ? value : -1;
    };
    return {
      air: read('air-count'),
      ground: read('ground-count'),
      surface: read('surface-count'),
      total: read('asset-count'),
    };
  });
}

/** Waits until the HUD publishes exactly these per-domain counts. */
export async function waitForDomainCounts(
  page: Page,
  expected: { readonly air: number; readonly ground: number; readonly surface: number },
): Promise<void> {
  await page.waitForFunction(
    (want: { air: number; ground: number; surface: number }) => {
      const read = (id: string): number =>
        Number.parseInt(document.getElementById(id)?.textContent ?? '', 10);
      return read('air-count') === want.air
        && read('ground-count') === want.ground
        && read('surface-count') === want.surface;
    },
    expected,
    { polling: 100 },
  );
}

/** Waits until the roster shows at least this many visible asset rows. */
export async function waitForAssetRows(page: Page, atLeast: number): Promise<void> {
  await page.waitForFunction(
    (want: number) =>
      document.querySelectorAll('.ar-row[data-roster-kind="asset"]:not([hidden])').length >= want,
    atLeast,
    { polling: 100 },
  );
}

/** How many frames the DVR ring currently holds, as the bar reports it. */
export async function dvrFrameCount(page: Page): Promise<number> {
  return page.evaluate(() => {
    const value = Number.parseInt(document.querySelector('.dvr-count')?.textContent ?? '', 10);
    return Number.isFinite(value) ? value : -1;
  });
}

/** Whether the DVR is at the live edge. `REC` records; `REPLAY` does not. */
export async function isLive(page: Page): Promise<boolean> {
  return page.evaluate(() => document.querySelector('.dvr-reclabel')?.textContent === 'REC');
}

/** The HUD's simulation clock, in seconds, or NaN before the first frame. */
export async function simulationSeconds(page: Page): Promise<number> {
  return page.evaluate(
    () => Number.parseFloat(document.getElementById('sim-time')?.textContent ?? ''),
  );
}

/**
 * Waits until the simulation clock has advanced by `seconds`.
 *
 * The honest way to say "let a few more broadcasts land". Counting DVR frames
 * would look equivalent and is not: the v2 ring caps at 180, after which the
 * count stops changing while frames keep arriving, and a wait on it would hang
 * in exactly the long-running cases that need it most. The clock keeps moving.
 *
 * Only meaningful at the live edge — away from it the clock shows the replayed
 * frame's own time and does not advance.
 */
export async function waitForSimulationAdvance(page: Page, seconds: number): Promise<void> {
  const from = await simulationSeconds(page);
  await page.waitForFunction(
    (target: number) => {
      const now = Number.parseFloat(document.getElementById('sim-time')?.textContent ?? '');
      return Number.isFinite(now) && now >= target;
    },
    (Number.isFinite(from) ? from : 0) + seconds,
    { polling: 100 },
  );
}

/** Waits until the DVR ring holds exactly this many frames. */
export async function waitForDvrFrames(page: Page, exactly: number): Promise<void> {
  await page.waitForFunction(
    (want: number) =>
      Number.parseInt(document.querySelector('.dvr-count')?.textContent ?? '', 10) === want,
    exactly,
    { polling: 100 },
  );
}

/**
 * Leaves the live edge by scrubbing back `framesBack` frames.
 *
 * The DVR is the only writer of the console's interaction mode, so this is also
 * what closes every live mutation — there is deliberately no test-only way in.
 *
 * Scrubbing rather than jumping to the start, and by one frame by default,
 * because those are different questions. A caller that wants "is this console
 * off the live edge" gets the newest recorded picture, which still contains
 * everything that was on screen a moment ago; jumping to the start would land on
 * a frame recorded before the room was populated and turn every "the contact is
 * still there" assertion into a test of the ring's oldest slot instead.
 *
 * ArrowLeft on the focused range input is the operator's own gesture, and fires
 * the same `input` event a drag does.
 */
export async function enterReplay(page: Page, framesBack = 1): Promise<void> {
  const scrub = page.locator('.dvr-scrub');
  await scrub.focus();
  for (let step = 0; step < framesBack; step += 1) {
    await page.keyboard.press('ArrowLeft');
  }
  await page.waitForFunction(
    () => document.querySelector('.dvr-reclabel')?.textContent === 'REPLAY',
    undefined,
    { polling: 100 },
  );
}

/**
 * Returns to the live edge and waits for the console to finish recovering.
 *
 * Recovery is two things, not one: the mode flips back, and then the held
 * snapshot is re-applied and the authority read re-issued. Waiting only for
 * `REC` would race the second half, so this also waits for the ring to grow —
 * the first thing that can only happen once recording has actually resumed.
 */
export async function goLive(page: Page): Promise<void> {
  const before = await dvrFrameCount(page);
  await page.locator('.dvr-live').click();
  await page.waitForFunction(
    (baseline: number) => {
      if (document.querySelector('.dvr-reclabel')?.textContent !== 'REC') return false;
      const count = Number.parseInt(document.querySelector('.dvr-count')?.textContent ?? '', 10);
      return Number.isFinite(count) && count > baseline;
    },
    before,
    { polling: 100 },
  );
}

/**
 * Starts a configured scenario through the v2 route, in the page's own room.
 *
 * Uses the context's request client rather than the console's Change… dialog so
 * a spec can arrange a population without also exercising — and depending on —
 * the catalog UI.
 *
 * Counts against the same process-wide `destructive` budget described on
 * {@link waitForOperatorConsole}: ten per minute for the whole server, and a
 * fresh room has already spent two of them before this is called. A spec that
 * starts a scenario is therefore a third call, which is why repeating such a
 * spec several times a minute is what runs the budget out.
 */
export async function startScenario(page: Page, name: string): Promise<void> {
  const origin = new URL(page.url()).origin;
  const response = await page.context().request.post(
    `${origin}/api/v2/sim/scenarios/${name}/start`,
    { headers: { 'content-type': 'application/json' }, data: {} },
  );
  if (!response.ok()) {
    throw new Error(
      `Starting scenario '${name}' failed with ${response.status()}: ${await response.text()}`,
    );
  }
}

/**
 * Injects one external track report into the page's room.
 *
 * The same body the console's own Advanced/Safety form posts, including the
 * all-zero quaternion that means "no attitude was declared". Contacts are
 * observations, never commandable, so this adds something to the picture without
 * adding anything to the command surface.
 */
export async function reportTrack(
  page: Page,
  trackId: string,
  position: { readonly x: number; readonly y: number; readonly z: number },
  label = 'Browser reachability contact',
): Promise<void> {
  const origin = new URL(page.url()).origin;
  const response = await page.context().request.post(`${origin}/api/v2/sim/tracks`, {
    headers: { 'content-type': 'application/json' },
    data: {
      trackId,
      pose: {
        frame: COORDINATE_FRAME_LOCAL_EUS,
        originId: null,
        position,
        orientation: { x: 0, y: 0, z: 0, w: 0 },
      },
      twist: null,
      classification: 0,
      sourceId: 'browser-verification',
      sourceKind: TRACK_SOURCE_OPERATOR_ENTERED,
      sourceQuality: 0.8,
      confidence: 0.8,
      observedAtSimulationTimeSeconds: null,
      positionAccuracyM: null,
      velocityAccuracyMps: null,
      label,
      transponder: null,
    },
  });
  if (!response.ok()) {
    throw new Error(
      `Reporting track '${trackId}' failed with ${response.status()}: ${await response.text()}`,
    );
  }
}

/**
 * Whether an element is at the top of the hit-test stack at its own centre.
 *
 * This is the reachability question a DOM emulator cannot answer: a control can
 * have a perfectly good bounding box and still sit under the WebGL canvas, and
 * every unit test in the suite would go on passing. `elementFromPoint` asks the
 * compositor what a click would actually land on.
 */
export async function hitTestOwn(target: Locator): Promise<boolean> {
  return target.evaluate((element: Element) => {
    const box = element.getBoundingClientRect();
    if (box.width <= 0 || box.height <= 0) return false;
    const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
    return hit !== null && (hit === element || element.contains(hit));
  });
}

/** Numeric `z-index` of an element's own stacking context, or null when auto. */
export async function zIndexOf(target: Locator): Promise<number | null> {
  return target.evaluate((element: Element) => {
    const value = Number.parseInt(getComputedStyle(element).zIndex, 10);
    return Number.isFinite(value) ? value : null;
  });
}

/** Where one Tab press landed, and whether that place is one nobody can see. */
export interface FocusStop {
  readonly description: string;
  readonly insideHidden: boolean;
}

/**
 * Walks focus forward with Tab and reports where it landed each time.
 *
 * Returns a description per stop rather than an element handle, because the
 * property under test is "focus never entered a subtree nobody can see" — which
 * is answered by the ancestor chain of whatever received focus, not by its
 * identity.
 */
export async function tabThrough(page: Page, presses: number): Promise<readonly FocusStop[]> {
  const stops: FocusStop[] = [];
  for (let index = 0; index < presses; index += 1) {
    await page.keyboard.press('Tab');
    stops.push(await page.evaluate((): FocusStop => {
      const active = document.activeElement;
      if (active === null || active === document.body) {
        return { description: 'body', insideHidden: false };
      }
      let insideHidden = false;
      for (let node: Element | null = active; node !== null; node = node.parentElement) {
        if (!(node instanceof HTMLElement)) continue;
        if (node.hidden || node.hasAttribute('inert')) insideHidden = true;
        const style = getComputedStyle(node);
        if (style.display === 'none' || style.visibility === 'hidden') insideHidden = true;
      }
      const id = active.id !== '' ? `#${active.id}` : '';
      const cls = typeof active.className === 'string' && active.className.trim() !== ''
        ? `.${active.className.trim().split(/\s+/).join('.')}`
        : '';
      return { description: `${active.tagName.toLowerCase()}${id}${cls}`, insideHidden };
    }));
  }
  return stops;
}
