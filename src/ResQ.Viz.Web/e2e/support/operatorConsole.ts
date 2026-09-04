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
//
//  3. **A wait must lose to the test budget, not race it.** A wait with a fixed
//     timeout is only as good as the smallest budget any spec gives it, and when
//     the budget is the smaller of the two it is the *test* that is torn down —
//     taking the wait's own explanation with it. The waits below therefore
//     derive their timeout from what is left of the running test's budget, and
//     keep a reserve for the diagnostic that explains a failure. See
//     {@link bootWaitTimeoutMs}.

import { test as base, type BrowserContext, type Locator, type Page } from '@playwright/test';

import { CONSOLE_BOOT_TIMEOUT_MS } from '../../playwright.config';

export { FORCED_LEGACY_ORIGIN, NORMAL_ORIGIN } from '../../playwright.config';

/**
 * When the running test's budget started, so the waits below can tell how much
 * of it is left.
 *
 * Playwright publishes a test's *total* budget (`testInfo.timeout`) but not how
 * much of it has been spent, and the difference is exactly what a wait needs in
 * order to stop before the budget does. An auto fixture is the earliest hook
 * inside a test's own clock, so this reads a shade early — which is the
 * conservative direction: it can only under-report the time remaining.
 *
 * Keyed by the `TestInfo` rather than held in a module variable so it stays
 * correct if this suite ever runs more than one worker.
 */
const budgetStartedAt = new WeakMap<object, number>();

/**
 * The suite's `test`, extended with the budget clock the waits below read.
 *
 * Specs import `test` from here rather than from `@playwright/test` for that
 * one reason. `expect` is unaffected and still comes from the package.
 */
export const test = base.extend<{ readonly consoleBudgetClock: void }>({
  consoleBudgetClock: [
    async ({}, use, testInfo): Promise<void> => {
      budgetStartedAt.set(testInfo, Date.now());
      await use();
    },
    { auto: true },
  ],
});

/**
 * Milliseconds of the running test's budget still unspent, or `Infinity` when
 * the test declared no timeout at all.
 */
function remainingBudgetMs(): number {
  const info = test.info();
  if (info.timeout === 0) return Number.POSITIVE_INFINITY;
  const startedAt = budgetStartedAt.get(info);
  if (startedAt === undefined) return info.timeout;
  return info.timeout - (Date.now() - startedAt);
}

/**
 * What a boot wait keeps back so its own failure can be explained.
 *
 * Reading the console's state out of a page this starved is not free: the same
 * diagnostic `page.evaluate` took 2.2 s and 8.2 s in the two CI traces that
 * reached it. Twenty seconds covers that with room for the round trip and for
 * the error to be assembled and thrown.
 */
const DIAGNOSTIC_RESERVE_MS = 20_000;

/** Floor on a boot wait, so a caller that arrives late still gets a real look. */
const MINIMUM_BOOT_WAIT_MS = 5_000;

/**
 * How long a wait for a console branch may run.
 *
 * The smaller of the configured ceiling and what is left of the test's budget
 * after the diagnostic reserve. That subtraction is the whole point: it is what
 * makes the wait — which knows what it was waiting for — the thing that fails,
 * rather than the test timeout, which does not.
 *
 * Before this, `waitForOperatorConsole` used a flat 45 s justified against "the
 * 90-second per-test budget". The DVR heap spec's budget is not 90 s, and on CI
 * that spec died reporting `page.evaluate: Test timeout of 60000ms exceeded` —
 * the diagnostic killed by the failure it existed to describe.
 */
function bootWaitTimeoutMs(): number {
  const affordable = remainingBudgetMs() - DIAGNOSTIC_RESERVE_MS;
  return Math.max(MINIMUM_BOOT_WAIT_MS, Math.min(CONSOLE_BOOT_TIMEOUT_MS, affordable));
}

/** Everything worth knowing about a console that has not reached its branch. */
export interface ConsoleObservation {
  readonly branch: 'v2' | 'legacy' | 'boot';
  readonly bootTitle: string | null;
  readonly bootDetail: string | null;
  readonly connection: string | null;
  readonly mission: string | null;
  readonly assetCount: string | null;
  readonly rosterRows: number;
  readonly simulationTime: string | null;
}

/** Runs in the page. Closes over nothing, so it survives serialization. */
const OBSERVE_CONSOLE = (): ConsoleObservation => {
  const branch = document.getElementById('operator-v2-console')?.hidden === false
    ? 'v2'
    : document.getElementById('legacy-console')?.hidden === false ? 'legacy' : 'boot';
  // The boot section keeps its markup copy after it is hidden, so reading it
  // unconditionally reports "Establishing simulation link…" beside a console
  // that connected long ago — a diagnostic that invents a transport failure.
  // It is only evidence while boot is the branch on screen.
  const booting = branch === 'boot';
  return {
    branch,
    bootTitle: booting
      ? document.getElementById('operator-boot-title')?.textContent ?? null
      : null,
    bootDetail: booting
      ? document.getElementById('operator-boot-detail')?.textContent ?? null
      : null,
    connection: document.getElementById('conn-label')?.textContent ?? null,
    mission: document.querySelector('.operator-mission-title')?.textContent ?? null,
    assetCount: document.getElementById('asset-count')?.textContent ?? null,
    rosterRows: document.querySelectorAll('.ar-row').length,
    simulationTime: document.getElementById('sim-time')?.textContent ?? null,
  };
};

/**
 * Reads the console's state for a failure message, and never throws.
 *
 * A diagnostic that can fail is a diagnostic that replaces the error it was
 * meant to annotate — which is precisely what happened on CI, where the caller
 * saw `page.evaluate: Test timeout of 60000ms exceeded` and learned nothing
 * about the console. So every way this can go wrong (a torn-down page, a budget
 * that expired anyway, a main thread too blocked to answer) degrades to `null`,
 * and the caller reports its own failure with the state marked unreadable.
 */
async function observeConsole(page: Page): Promise<ConsoleObservation | null> {
  let expire: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      page.evaluate(OBSERVE_CONSOLE),
      new Promise<null>((resolve) => {
        expire = setTimeout(() => resolve(null), DIAGNOSTIC_RESERVE_MS);
      }),
    ]);
  } catch {
    return null;
  } finally {
    if (expire !== undefined) clearTimeout(expire);
  }
}

/** The observation rendered for an error message, readable or not. */
function describeObservation(observed: ConsoleObservation | null): string {
  return observed === null
    ? '<unreadable — the page did not answer within the diagnostic reserve>'
    : JSON.stringify(observed);
}

/** Scene frame the console draws in: +X east, +Y up, +Z south. */
const COORDINATE_FRAME_LOCAL_EUS = 2;

/** `TrackSourceKind.OperatorEntered` — what the console's own report form sends. */
const TRACK_SOURCE_OPERATOR_ENTERED = 6;

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
 *
 * The timeout comes from {@link bootWaitTimeoutMs}, not from a constant here:
 * this wait is the first thing every spec does, its cost is dominated by the
 * runner's hardware, and its callers do not all share one budget.
 */
export async function waitForOperatorConsole(page: Page, minimumRows = 1): Promise<void> {
  // Read once. Deriving it again for the message would report what was left of
  // the budget *after* the wait, not what the wait was actually given.
  const timeoutMs = bootWaitTimeoutMs();
  try {
    await page.waitForFunction(
      (rows: number) => {
        const v2 = document.getElementById('operator-v2-console');
        if (v2 === null || v2.hidden) return false;
        return document.querySelectorAll('.ar-row').length >= rows;
      },
      minimumRows,
      { polling: 100, timeout: timeoutMs },
    );
  } catch (cause) {
    // What this failure has meant in practice, from the one CI run that
    // produced it (33851821089), read out of the uploaded traces rather than
    // reasoned about:
    //
    //   * **Not the rate limiter.** An earlier version of this message blamed
    //     the server's process-wide `destructive` fixed window — ten requests a
    //     minute, two spent per fresh room — for a room that came up connected
    //     and empty. The traces refute it: every request on that limiter in the
    //     whole run answered 200, the busiest 60-second window held four of
    //     them, and no 429 was issued to anything. The desktop spec's scenario
    //     start succeeded — twenty seconds *after* this wait had given up.
    //   * **A slow runner.** `ubuntu-latest` has no GPU. Boot took 27.6 s on
    //     one spec and past 50 s on another, whose SignalR WebSocket handshake
    //     404'd after a 19.5 s stall (the negotiated connection id had expired
    //     while the main thread was blocked) and then spent ~40 s falling back
    //     to server-sent events. A `simulationTime` well ahead of an
    //     `assetCount` of 0 is that: the room's clock runs from session
    //     creation, and the fleet simply has not been asked for yet.
    //
    // So read the observation below before concluding anything. A connected
    // room whose boot title still says it is negotiating is a transport that
    // has not finished; a v2 branch with a mission and no rows is a roster that
    // has not rendered; and neither is a rate limit.
    const observed = await observeConsole(page);
    throw new Error(
      `The v2 operator console never reached ${minimumRows} roster row(s) within `
      + `${timeoutMs}ms. Observed: ${describeObservation(observed)}. `
      + `Underlying wait: ${String(cause)}`,
    );
  }
}

/**
 * Waits until the legacy branch owns the rail.
 *
 * The forced-legacy case reaches this branch only after a real SignalR
 * negotiation completes and its v2 opt-in is really refused, which makes "the
 * legacy console is visible" a boot-scale event rather than an ordinary
 * assertion. Asserting it directly gives it the `expect` budget instead: on CI
 * that assertion needed 20.3 s against a 15 s line and failed its spec with 51
 * seconds of the test budget still unspent, while the page was honestly still
 * on "Establishing simulation link…".
 *
 * This adds no claim of its own — the spec still asserts everything it asserted
 * before. It only stops the spec's first assertion from doubling as a wait for
 * the transport.
 */
export async function waitForLegacyConsole(page: Page): Promise<void> {
  const timeoutMs = bootWaitTimeoutMs();
  try {
    await page.waitForFunction(
      () => document.getElementById('legacy-console')?.hidden === false,
      undefined,
      { polling: 100, timeout: timeoutMs },
    );
  } catch (cause) {
    const observed = await observeConsole(page);
    throw new Error(
      `The legacy console branch was never shown within ${timeoutMs}ms. `
      + `Observed: ${describeObservation(observed)}. `
      + 'Reaching this branch needs the hub connection to come up and its v2 '
      + 'subscription to be refused; a boot title still reading "Establishing '
      + 'simulation link…" means the transport never finished, which is a slow '
      + 'or broken negotiation rather than a missing fallback. '
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
 * Waits until the DVR ring holds at least this many frames.
 *
 * The "at least" form exists because the ring is only stable at its cap. Below
 * it the count changes every 100 ms, and a wait for one exact value polling at
 * the same rate can step straight over it; only 180 — where the ring stops
 * growing — can be waited on exactly.
 */
export async function waitForDvrFramesAtLeast(page: Page, atLeast: number): Promise<void> {
  await page.waitForFunction(
    (want: number) => {
      const count = Number.parseInt(
        document.querySelector('.dvr-count')?.textContent ?? '',
        10,
      );
      return Number.isFinite(count) && count >= want;
    },
    atLeast,
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
 * Counts against the server's process-wide `destructive` fixed window: ten
 * requests a minute for the whole server, not partitioned by room or caller
 * (`Program.cs`, `AddFixedWindowLimiter("destructive")`). One full suite run is
 * nowhere near it — the busiest 60-second window on CI held four such requests,
 * and every one of them answered 200. `--repeat-each`, a retry storm, or many
 * local runs against one reused server are what could reach the limit, and a
 * refusal would surface here as a thrown 429 rather than as a silently empty
 * room.
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

/**
 * Waits for the ring to shrink below `ceiling`, and reports where it landed.
 *
 * The DVR ring is emptied whenever the next tick belongs to a different world
 * than the one behind it — a schema change, or a new scenario revision. Only a
 * *drop* witnesses that: the count after a scenario start is small either
 * because the ring was cleared or because it was small all along, and those two
 * are the same number. So a caller reads the count first, starts the scenario,
 * and waits here for the count to fall below what it saw.
 *
 * Returns the first count observed below the ceiling rather than re-reading
 * afterwards, because recording continues and a second read would already have
 * grown past the moment being witnessed.
 */
export async function waitForDvrRingBelow(page: Page, ceiling: number): Promise<number> {
  const handle = await page.waitForFunction(
    (want: number) => {
      const count = Number.parseInt(
        document.querySelector('.dvr-count')?.textContent ?? '',
        10,
      );
      // Wrapped rather than returned bare: a cleared ring reads 0, and a bare 0
      // is falsy, so the wait would sit through the exact event it is watching
      // for.
      return Number.isFinite(count) && count < want ? { count } : null;
    },
    ceiling,
    { polling: 100 },
  );
  const observed = await handle.jsonValue();
  if (observed === null) {
    // Unreachable: `waitForFunction` resolves only on a truthy value, so the
    // null branch of the predicate never lands here. Stated rather than cast
    // away, because the cast that silences the compiler would also silence a
    // genuine null.
    throw new Error('The DVR ring wait resolved without reporting a count.');
  }
  return observed.count;
}

/** One `Runtime.getHeapUsage` reading, in bytes, for the page's whole isolate. */
export interface HeapUsage {
  readonly usedSize: number;
  readonly totalSize: number;
  readonly embedderHeapUsedSize: number;
  readonly backingStorageSize: number;
}

/** A live CDP attachment that can force a collection and read what survived. */
export interface HeapProbe {
  /** Collects garbage, then reports what is still reachable. */
  sample(): Promise<HeapUsage>;
  /** Detaches the CDP session. Safe to call once, from a `finally`. */
  detach(): Promise<void>;
}

/**
 * Opens a CDP heap probe on the page.
 *
 * `performance.memory` is quantised and updated lazily, and `window.gc` needs a
 * launch flag this suite does not set; CDP is the only way from a test to make
 * V8 collect on demand and then report what genuinely survived. Chromium-only,
 * which this suite already is.
 *
 * `sample()` collects twice. The first pass is what makes dropped snapshots
 * unreachable; the second reclaims everything that only *became* unreachable
 * during the first — objects held by weak references and by finalizers, of
 * which a Three.js scene has many. Sampling after one pass reads a heap that is
 * still holding a collection's worth of garbage, and that noise is the same
 * order as the growth being measured.
 *
 * The caller owns the lifetime: open inside `try`, `detach()` in `finally`. A
 * session left attached outlives the test and keeps the target alive.
 */
export async function openHeapProbe(page: Page): Promise<HeapProbe> {
  const session = await page.context().newCDPSession(page);
  let detached = false;
  return {
    async sample(): Promise<HeapUsage> {
      await session.send('HeapProfiler.collectGarbage');
      await session.send('HeapProfiler.collectGarbage');
      return session.send('Runtime.getHeapUsage');
    },
    async detach(): Promise<void> {
      if (detached) return;
      detached = true;
      await session.detach();
    },
  };
}
