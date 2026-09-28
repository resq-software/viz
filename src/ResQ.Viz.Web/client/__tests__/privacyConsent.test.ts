// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Analytics is opt-in. These cases pin the one property the consent flow
// exists for — no tracker loads before the visitor accepts — and the ways a
// visitor makes, keeps and changes that choice. They drive the real index.html
// markup and the real bootstrap; only the analytics package's network-facing
// entry points are replaced, so nothing here can reach a provider.

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const providers = vi.hoisted(() => ({
  posthogImported: 0,
  posthog: {
    opt_out_capturing: vi.fn(),
    opt_in_capturing: vi.fn(),
  },
  initAnalytics: vi.fn(() => Promise.resolve()),
}));

vi.mock('posthog-js', () => {
  providers.posthogImported += 1;
  return { default: {} };
});

vi.mock('@resq-systems/analytics', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@resq-systems/analytics')>();
  return {
    ...actual,
    initAnalytics: providers.initAnalytics,
    analytics: { get posthog() { return providers.posthog; } },
  };
});

const KEYS = { VITE_POSTHOG_KEY: 'phc_test', VITE_GA4_ID: 'G-TEST1234' } as const;
const NO_KEYS = {} as const;
const STORAGE_KEY = 'resq-analytics-consent';
/** Substrings that identify an analytics provider's script URL. */
const TRACKER_MARKERS = ['googletagmanager', 'google-analytics', 'posthog'];

const html = readFileSync(resolve(__dirname, '../index.html'), 'utf8');
const body = html.slice(html.indexOf('<body>') + '<body>'.length, html.indexOf('<script type="module"'));

/** Fresh module graph per case: the bootstrap keeps page-lifetime state. */
async function boot(env: Record<string, string>): Promise<void> {
  vi.resetModules();
  const { bootstrapAnalytics } = await import('../analytics');
  bootstrapAnalytics(document, env);
  await vi.dynamicImportSettled();
}

function banner(): HTMLElement {
  return document.getElementById('consent-banner') as HTMLElement;
}

function click(id: string): void {
  (document.getElementById(id) as HTMLElement).click();
}

/** Every observable sign that an analytics provider was loaded. */
function trackerActivity() {
  return {
    initAnalytics: providers.initAnalytics.mock.calls.length,
    posthogImported: providers.posthogImported,
    trackerScripts: [...document.querySelectorAll('script[src]')]
      .map((s) => s.getAttribute('src') ?? '')
      .filter((src) => TRACKER_MARKERS.some((marker) => src.includes(marker))),
    dataLayer: (window as unknown as { dataLayer?: unknown[] }).dataLayer?.length ?? 0,
  };
}

const NOTHING_LOADED = { initAnalytics: 0, posthogImported: 0, trackerScripts: [], dataLayer: 0 };

beforeEach(() => {
  document.body.innerHTML = body;
  localStorage.clear();
  document.cookie.split(';').forEach((c) => {
    const name = c.split('=')[0]?.trim();
    if (name) document.cookie = `${name}=; Max-Age=0; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`;
  });
  providers.posthogImported = 0;
  vi.clearAllMocks();
});

afterEach(() => {
  delete (window as unknown as Record<string, unknown>)['ga-disable-G-TEST1234'];
});

describe('analytics consent', () => {
  it('loads no tracker before the visitor accepts, and asks with equal options', async () => {
    await boot(KEYS);

    expect(trackerActivity()).toEqual(NOTHING_LOADED);
    expect(banner().hidden).toBe(false);
    const accept = document.getElementById('consent-accept') as HTMLButtonElement;
    const decline = document.getElementById('consent-decline') as HTMLButtonElement;
    expect(accept.tagName).toBe(decline.tagName);
    expect(accept.className).toBe(decline.className);
  });

  it('keeps everything unloaded after Decline, on this visit and the next', async () => {
    await boot(KEYS);
    click('consent-decline');

    expect(localStorage.getItem(STORAGE_KEY)).toBe('denied');
    expect(banner().hidden).toBe(true);
    expect(trackerActivity()).toEqual(NOTHING_LOADED);

    document.body.innerHTML = body;
    await boot(KEYS);
    expect(banner().hidden).toBe(true);
    expect(trackerActivity()).toEqual(NOTHING_LOADED);
  });

  it('loads analytics only once the visitor accepts', async () => {
    await boot(KEYS);
    click('consent-accept');

    expect(localStorage.getItem(STORAGE_KEY)).toBe('granted');
    expect(banner().hidden).toBe(true);
    expect(providers.initAnalytics).toHaveBeenCalledTimes(1);
    expect(providers.initAnalytics).toHaveBeenCalledWith(
      expect.objectContaining({
        posthog: expect.objectContaining({ key: 'phc_test' }),
        ga4: expect.objectContaining({ measurementId: 'G-TEST1234' }),
      }),
    );
  });

  it('boots straight away when an earlier acceptance is stored', async () => {
    localStorage.setItem(STORAGE_KEY, 'granted');
    await boot(KEYS);

    expect(banner().hidden).toBe(true);
    expect(providers.initAnalytics).toHaveBeenCalledTimes(1);
  });

  it('shows no banner and loads nothing when the build has no analytics keys', async () => {
    await boot(NO_KEYS);

    expect(banner().hidden).toBe(true);
    expect(trackerActivity()).toEqual(NOTHING_LOADED);

    click('privacy-settings-open');
    await vi.dynamicImportSettled();
    const dialog = document.getElementById('privacy-dialog') as HTMLDialogElement;
    expect(dialog.open).toBe(true);
    expect((document.getElementById('privacy-unconfigured') as HTMLElement).hidden).toBe(false);
  });

  it('lets the visitor withdraw later, switching loaded providers off', async () => {
    localStorage.setItem(STORAGE_KEY, 'granted');
    document.cookie = '_ga=GA1.1.123; path=/';
    document.cookie = '_ga_TEST1234=GS1.1.456; path=/';
    document.cookie = 'theme=dark; path=/';
    await boot(KEYS);

    click('privacy-settings-open');
    await vi.dynamicImportSettled();
    const dialog = document.getElementById('privacy-dialog') as HTMLDialogElement;
    expect(dialog.open).toBe(true);
    expect(document.getElementById('privacy-choice')?.textContent).toMatch(/accepted/);

    click('privacy-decline');

    expect(localStorage.getItem(STORAGE_KEY)).toBe('denied');
    expect(dialog.open).toBe(false);
    expect(providers.posthog.opt_out_capturing).toHaveBeenCalledTimes(1);
    expect((window as unknown as Record<string, unknown>)['ga-disable-G-TEST1234']).toBe(true);
    expect(document.cookie).not.toMatch(/_ga/);
    expect(document.cookie).toMatch(/theme=dark/);
  });

  it('opens the privacy notice from the banner without making a choice', async () => {
    await boot(KEYS);
    click('consent-notice-open');

    const dialog = document.getElementById('privacy-dialog') as HTMLDialogElement;
    expect(dialog.open).toBe(true);
    expect(document.getElementById('privacy-choice')?.textContent).toMatch(/none yet/);
    expect(localStorage.getItem(STORAGE_KEY)).toBeNull();
    expect(trackerActivity()).toEqual(NOTHING_LOADED);
  });
});

describe('privacy notice', () => {
  const notice = (): string =>
    (document.getElementById('privacy-dialog') as HTMLElement).textContent?.replace(/\s+/g, ' ') ?? '';

  it('names the processors, purpose, legal basis, retention, withdrawal and a contact', () => {
    const text = notice();
    for (const required of [
      'Google Analytics 4',
      'PostHog',
      'To understand how the console is used',
      'The legal basis is your consent',
      'as configured with each provider',
      'Choose Decline below, or later under Settings → Privacy settings',
      'contact@resq.software',
    ]) {
      expect(text).toContain(required);
    }
  });

  it('makes no compliance claim', () => {
    expect(notice()).not.toMatch(/GDPR|compliant|certified/i);
  });
});
