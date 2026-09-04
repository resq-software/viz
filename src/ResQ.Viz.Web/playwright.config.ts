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
  };
}

const runServer = 'dotnet run --project ResQ.Viz.Web.csproj --configuration Debug --no-build';

/**
 * Whether this run is on the shared CI runner rather than a developer machine.
 *
 * This is the one distinction the budgets below turn on, and it is a hardware
 * distinction, not a caution. The `browser` job runs on `ubuntu-latest`, which
 * has no GPU: Chromium reports "No available adapters", ANGLE falls back to
 * software rasterisation, and the whole console — server and client — runs
 * between five and twenty times slower than it does locally.
 */
const onCi = Boolean(process.env['CI']);

/**
 * Per-test budget.
 *
 * 90 s is what this suite costs on a GPU-backed machine: the full four-spec run
 * is 46 s there, and no single spec comes near the budget. The CI figure is not
 * that number with slack added — it is measured, from the traces of run
 * 33851821089, where the *same* application came up correct but slow:
 *
 *   * `page.goto` alone: 8.2 s, 8.4 s, 8.5 s, 11.6 s (locally, well under 1 s).
 *   * Reaching a populated v2 console: 27.6 s on the narrow spec, and longer on
 *     the desktop spec, whose SignalR WebSocket handshake 404'd after a 19.5 s
 *     stall and cost a further ~40 s falling back to server-sent events.
 *   * The DVR bar after that: another 17.6 s.
 *
 * So boot alone spent 53 s of the narrow spec's 90 s, and the spec then failed
 * with 36 s left for forty-odd interactions that each cost seconds on a page
 * rendering at a fraction of a frame per second. Nothing about it was wrong;
 * it ran out of budget. 240 s is four times the observed boot cost, and four
 * specs at that ceiling still fit inside the job's own 30-minute limit.
 */
export const PER_TEST_TIMEOUT_MS = onCi ? 240_000 : 90_000;

/**
 * Budget for a single `expect(locator)` assertion.
 *
 * Every such assertion polls actionability inside the page on
 * `requestAnimationFrame`, so it can never resolve faster than the console
 * paints. Two assertions in that run were the whole failure of their spec:
 * `#legacy-console` visible took 20.3 s against this 15 s line — with 51 s of
 * the test budget still unspent — and `#sidebar` visible took 17.4 s.
 */
export const EXPECT_TIMEOUT_MS = onCi ? 45_000 : 15_000;

/**
 * Ceiling on a wait for a console branch to finish booting.
 *
 * A ceiling, not the timeout actually used: `waitForOperatorConsole` and
 * `waitForLegacyConsole` take the smaller of this and what is left of the
 * per-test budget, so the wait always loses to the budget with room for its own
 * diagnostic. Sized from the desktop spec's measured boot, which needed more
 * than the 49.6 s it was given and had its scenario answered 200 OK twenty
 * seconds after the wait had already given up.
 */
export const CONSOLE_BOOT_TIMEOUT_MS = onCi ? 150_000 : 45_000;

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
        // ANGLE over the host's real GL driver, and this is load-bearing rather
        // than an optimisation.
        //
        // Every Playwright wait — `waitForSelector`, actionability before a
        // click, `expect(locator)` — polls inside the page on
        // `requestAnimationFrame`, so Playwright can never be faster than the
        // application's frame rate. Measured on this console at 1440x900:
        // ANGLE/SwiftShader renders ~0.3 fps, at which a 20-second wait gets
        // about six chances and clicks time out; ANGLE over the host GL driver
        // renders 60 fps and the same waits return in tens of milliseconds.
        // Turning postfx, shadows and SSAO off through persisted settings does
        // not move the SwiftShader number — the cost is the base terrain and
        // water scene, which no test-side switch can remove.
        //
        // So this suite wants a machine with a working GL driver, and says so
        // by asking for one. A runner without one falls back to software
        // rasterisation, which is what `ubuntu-latest` does today.
        //
        // What that fallback costs was measured for real in run 33851821089,
        // and it is slowness rather than breakage: every spec's application
        // booted, connected, ran its scenario and rendered a correct, populated
        // console — the narrow spec's own page snapshot shows the mission
        // Running, "8 assets total: 3 air, 3 ground, 2 surface", and eight
        // roster rows. What failed was the clock, at four different places, so
        // the budgets above are sized from that run rather than from this one.
        // Moving this job onto a GPU-backed runner would make them all
        // unnecessary, and remains the fix that addresses the cause; until then
        // the honest thing is a budget that admits how slow the runner is,
        // never a weaker assertion.
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
