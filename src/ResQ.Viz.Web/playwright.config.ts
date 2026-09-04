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

export default defineConfig({
  testDir: './e2e',
  // A console frame arrives at 10 Hz and a populated scenario takes a while to
  // settle; the per-test budget is raised once here rather than per await.
  timeout: 90_000,
  expect: { timeout: 15_000 },
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
        // So this suite needs a machine with a working GL driver, and says so
        // by asking for one. A runner without one falls back to SwiftShader and
        // will time out rather than quietly pass a weaker assertion.
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
