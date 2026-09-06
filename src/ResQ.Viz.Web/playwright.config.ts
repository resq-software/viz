// ResQ Viz - browser reachability suite configuration
// SPDX-License-Identifier: Apache-2.0
//
// Two servers, both this application, differing in exactly one setting.
//
//   * NORMAL (5100/5101) is the console every deployment runs: v2 subscriptions
//     are accepted and the operator console is what loads.
//   * FORCED (5200/5201) sets `BrowserVerification:RejectV2Subscriptions`, which
//     `BrowserVerificationMode` only honours because the environment is exactly
//     `BrowserVerification`. Its v2 opt-in is refused at the hub, so the client
//     takes its legacy branch — the branch a DOM emulator cannot reach, because
//     reaching it requires a real SignalR negotiation that really fails.
//
// Both also set `BrowserVerification:SuspendSceneRendering`, which is what makes
// this suite affordable on a runner with no GPU. THE PRICE IS REAL AND IS PAID
// BY EVERY SPEC HERE: neither server's page draws anything, so nothing below
// verifies a shader, a draw call, the post-processing chain, the onboard
// picture-in-picture, or any regression whose only symptom is a wrong pixel.
// What these four specs claim — reachability, layout, stacking, hit targets,
// focus containment, the legacy transport fallback, DVR retention — is
// unaffected by the setting, and that is the whole reason it is acceptable here.
// `Services/SceneRenderingSuspension.cs` carries the measurement and the full
// list of what goes uncovered.
//
// Both are HTTPS because the room cookie is `Secure`. Both are started with
// `--no-build`: `npm run build` and `dotnet build` are the caller's job, and a
// `dotnet run` that built would race the second server for the same output.
//
// `workers: 1` and `fullyParallel: false` are not caution — each server owns one
// simulation process whose rooms advance on one 60 Hz tick loop, and two
// concurrent specs would be resetting and scrubbing it out from under each
// other.

import { defineConfig, devices } from '@playwright/test';

/** HTTPS origin of the ordinary console server. */
export const NORMAL_ORIGIN = 'https://127.0.0.1:5101';

/** HTTPS origin of the server whose hub refuses every v2 subscription. */
export const FORCED_LEGACY_ORIGIN = 'https://127.0.0.1:5201';

const pfxPath = process.env['RESQ_BROWSER_PFX'] ?? '';
const pfxPassword = process.env['RESQ_BROWSER_PFX_PASSWORD'] ?? '';

if (pfxPath === '' || pfxPassword === '') {
  // Failing here beats failing as "connection refused" two minutes later, with
  // the real Kestrel startup exception buried in a webServer log nobody reads.
  throw new Error(
    'RESQ_BROWSER_PFX and RESQ_BROWSER_PFX_PASSWORD must both be set. '
    + 'Run this suite through `npm run test:browser`, which exports a '
    + 'development certificate into a temporary directory for you.',
  );
}

/**
 * One server's environment.
 *
 * The ports are written as explicit `Kestrel:Endpoints` keys rather than
 * `ASPNETCORE_URLS`, because `appsettings.json` already declares those
 * endpoints and an explicit configuration section outranks `ASPNETCORE_URLS`.
 * A URLS-based override would be read, ignored, and leave both servers fighting
 * over port 5001.
 */
function serverEnvironment(
  httpPort: number,
  httpsPort: number,
  rejectV2: boolean,
): Record<string, string> {
  return {
    ASPNETCORE_ENVIRONMENT: 'BrowserVerification',
    Kestrel__Endpoints__Http__Url: `http://127.0.0.1:${httpPort}`,
    Kestrel__Endpoints__Https__Url: `https://127.0.0.1:${httpsPort}`,
    Kestrel__Certificates__Default__Path: pfxPath,
    Kestrel__Certificates__Default__Password: pfxPassword,
    BrowserVerification__RejectV2Subscriptions: String(rejectV2),
    // The one setting both servers share and no deployment sets. Written here
    // rather than per server so the two cannot drift: a suite where one page
    // draws and the other does not would have one spec paying a cost the others
    // do not, which is how a suite acquires a slow spec nobody can explain.
    BrowserVerification__SuspendSceneRendering: 'true',
    // The destructive limiter is a GLOBAL ten-per-minute window, and every spec
    // in this suite drives the same server process. Booting a console spends
    // permits on the scenario start and the terrain fetch, so the budget runs
    // out: measured, the first two specs leave exactly ONE permit, the third
    // console's scenario start returns 429, and it renders an empty room —
    // "No active mission", zero rows — while the connection stays up. That
    // reads as a console bug and is not one.
    //
    // Raised through configuration rather than a test-only branch in the
    // server, so the suite still drives the shipped code path. Production
    // defaults are untouched. One trusted client doing setup is not the traffic
    // the cap exists to bound.
    RateLimits__DestructivePermitsPerMinute: '500',
    RateLimits__GeneralPermitsPerMinute: '2000',
  };
}

const runServer = 'dotnet run --project ResQ.Viz.Web.csproj --configuration Debug --no-build';

/**
 * Whether this run is on the shared CI runner rather than a developer machine.
 *
 * This is the one distinction the budgets below turn on, and it is now a modest
 * one. It used to carry the whole weight of a GPU-less runner: `ubuntu-latest`
 * has no GPU, ANGLE falls back to software rasterisation, and a console drawing
 * a full-screen terrain scene that way saturates the main thread — which starved
 * not only the application but Playwright's own in-page machinery, and cost two
 * rounds of ever-larger budgets that failed later and later without ever
 * converging. `BrowserVerification:SuspendSceneRendering`, set for both servers
 * above, removes that draw, and with it the reason the CI figures were large.
 * What is left is an ordinary four-vCPU runner doing ordinary work more slowly
 * than a workstation does.
 */
const onCi = Boolean(process.env['CI']);

/**
 * Per-test budget.
 *
 * Measured, not padded. With the scene's draw suspended, the whole four-spec run
 * costs 29.3 s against a Chromium deliberately forced onto SwiftShader
 * (`--use-angle=swiftshader`) — that is, under the CI runner's rasteriser rather
 * than a GPU: 1.6 s, 1.7 s, 3.5 s, and 20.0 s for the DVR heap spec, whose cost
 * is almost entirely the eighteen wall-clock seconds of 10 Hz broadcasts needed
 * to fill the ring, which no hardware compresses.
 *
 * The same desktop spec, same rasteriser, same budgets, with the draw left on,
 * fails exactly as CI does: the test clock expires inside the first roster row's
 * `toBeVisible`. So 4.3 s versus a blown 90 s budget is the measurement this
 * number rests on, and 120 s on CI is six times the slowest spec.
 *
 * Both figures came DOWN. That direction matters: two earlier rounds raised them
 * — 90 s to 240 s, and 300 s for the DVR spec — and both rounds failed later,
 * at a different assertion, because the cost being outrun grew with every extra
 * call the larger budget bought. A budget is a claim about how long correct work
 * takes, and a budget that has to keep growing is evidence the work is not the
 * problem.
 */
export const PER_TEST_TIMEOUT_MS = onCi ? 120_000 : 90_000;

/**
 * Budget for a single `expect(locator)` assertion.
 *
 * Every such assertion polls actionability inside the page on
 * `requestAnimationFrame`, so it can never resolve faster than the page's frames
 * arrive. That sentence is unchanged and is still the reason this constant
 * exists; what changed is the frame rate it describes. A page whose animation
 * frame ends in a software-rasterised full-screen draw delivered roughly 0.3 to
 * 5.7 of them a second, and assertions took tens of seconds. A page that skips
 * the draw runs its loop at the display rate, and the whole desktop spec — some
 * forty of these assertions, plus clicks, hit tests and a scrub through replay —
 * now finishes in 3.5 s on that same rasteriser. So 20 s on CI is headroom for a
 * slow runner, not for a slow page. For scale, the run that motivated the old
 * 45 s figure never got past this spec's ninth Playwright call.
 */
export const EXPECT_TIMEOUT_MS = onCi ? 20_000 : 15_000;

/**
 * Ceiling on a wait for a console branch to finish booting.
 *
 * A ceiling, not the timeout actually used: `waitForOperatorConsole` and
 * `waitForLegacyConsole` take the smaller of this and what is left of the
 * per-test budget, so the wait always loses to the budget with room for its own
 * diagnostic. That arrangement is the reason a failure here reads as "the
 * console did not reach its branch, and here is what it did show" instead of as
 * a bare test timeout, and it is worth keeping whatever the numbers are.
 *
 * The numbers themselves: booting to a populated v2 console is under two seconds
 * of the desktop spec's 3.5 s total on a SwiftShader Chromium. 60 s on CI is
 * dominated by what the runner adds around the browser — a cold Kestrel, JIT,
 * and a SignalR negotiation — rather than by the page.
 */
export const CONSOLE_BOOT_TIMEOUT_MS = onCi ? 60_000 : 45_000;

export default defineConfig({
  testDir: './e2e',
  // A console frame arrives at 10 Hz and a populated scenario takes a while to
  // settle; the per-test budget is raised once here rather than per await, and
  // both budgets are hardware-derived — see the constants above.
  timeout: PER_TEST_TIMEOUT_MS,
  expect: { timeout: EXPECT_TIMEOUT_MS },
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env['CI']),
  retries: 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : [['list']],
  outputDir: './test-results',

  use: {
    // The development certificate is self-signed and issued to `localhost`;
    // nothing in this suite is testing PKI.
    ignoreHTTPSErrors: true,
    baseURL: NORMAL_ORIGIN,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },

  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        // ANGLE over the host's real GL driver.
        //
        // This flag used to be load-bearing and is not any more, which is worth
        // stating because the reasoning it replaced was correct and is still the
        // reason the suite once needed a GPU. Every Playwright wait —
        // `waitForSelector`, actionability before a click, `expect(locator)` —
        // polls inside the page on `requestAnimationFrame`, so Playwright can
        // never be faster than the page's frames. Measured on this console at
        // 1440x900 with the scene drawing: ANGLE/SwiftShader renders ~0.3 fps
        // and clicks time out, ANGLE over host GL renders 60 fps and the same
        // waits return in tens of milliseconds. Nothing test-side moved the
        // SwiftShader number — turning postfx, shadows and SSAO off through
        // persisted settings did not, because the cost was the base terrain and
        // water scene itself.
        //
        // Both servers now suspend that draw
        // (`BrowserVerification:SuspendSceneRendering`), so the frame rate no
        // longer depends on the rasteriser and the suite passes on SwiftShader in
        // 29.3 s. Two reasons to keep the flag anyway. The client still needs a
        // WebGL2 context to construct the scene at all — Mesa llvmpipe produced
        // none and the application never booted — so asking for the best
        // available implementation still buys something. And a developer who
        // clears the setting to look at a rendering question gets the fast path
        // rather than a mysteriously frozen console.
        launchOptions: {
          args: [
            '--use-gl=angle',
            '--use-angle=gl',
            '--disable-dev-shm-usage',
          ],
        },
      },
    },
  ],

  webServer: [
    {
      command: runServer,
      // The SPA fallback, which is anonymous. Every `/api/**` route is behind
      // `[RequireRoom]` and answers 401 without a session cookie, which
      // Playwright would read as "still starting" until the timeout.
      url: `${NORMAL_ORIGIN}/`,
      env: serverEnvironment(5100, 5101, false),
      ignoreHTTPSErrors: true,
      reuseExistingServer: !process.env['CI'],
      stdout: 'ignore',
      stderr: 'pipe',
      timeout: 120_000,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 5_000 },
    },
    {
      command: runServer,
      url: `${FORCED_LEGACY_ORIGIN}/`,
      env: serverEnvironment(5200, 5201, true),
      ignoreHTTPSErrors: true,
      reuseExistingServer: !process.env['CI'],
      stdout: 'ignore',
      stderr: 'pipe',
      timeout: 120_000,
      gracefulShutdown: { signal: 'SIGTERM', timeout: 5_000 },
    },
  ],
});
