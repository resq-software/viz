// ResQ Viz - automated accessibility check (axe-core) in a real browser
// SPDX-License-Identifier: Apache-2.0
//
// happy-dom has no layout, no computed colour and no top layer, so contrast,
// focusability and dialog semantics can only be judged here. This runs axe-core
// against the connected console and against the privacy dialog, for every
// WCAG 2.0 / 2.1 / 2.2 A and AA rule axe-core implements.
//
// A rule id in KNOWN_ISSUES is excluded from the assertion but still reported
// in the test output. Every entry must also appear under "Known issues" in
// ACCESSIBILITY.md; fixing one means deleting it from both places.
//
// The same console load also guards the consent contract at the network layer:
// no analytics host is contacted before the visitor accepts. CI builds carry no
// analytics keys, so this is a regression guard rather than the proof — the
// Vitest suite (client/__tests__/privacyConsent.test.ts) is where consent is
// exercised with keys set.

import AxeBuilder from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';

import { NORMAL_ORIGIN, test, waitForOperatorConsole } from './support/operatorConsole';

const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa'];

/** Rule ids documented as known issues in ACCESSIBILITY.md. The goal is empty. */
const KNOWN_ISSUES: readonly string[] = [];

const TRACKER_HOSTS = /googletagmanager\.com|google-analytics\.com|analytics\.google\.com|posthog\.com/;

interface Finding {
  readonly id: string;
  readonly impact: string | null | undefined;
  readonly targets: readonly string[];
}

async function axeFindings(page: Page, include?: string): Promise<{ blocking: Finding[]; known: Finding[] }> {
  let builder = new AxeBuilder({ page }).withTags(WCAG_TAGS);
  if (include) builder = builder.include(include);
  const { violations } = await builder.analyze();
  const findings = violations.map((v) => ({
    id: v.id,
    impact: v.impact,
    targets: v.nodes.slice(0, 5).map((n) => n.target.join(' ')),
  }));
  return {
    blocking: findings.filter((f) => !KNOWN_ISSUES.includes(f.id)),
    known: findings.filter((f) => KNOWN_ISSUES.includes(f.id)),
  };
}

test.describe('accessibility — desktop', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('the connected console has no axe-detectable WCAG A/AA violations', async ({ page }) => {
    const trackerRequests: string[] = [];
    page.on('request', (request) => {
      if (TRACKER_HOSTS.test(new URL(request.url()).host)) trackerRequests.push(request.url());
    });

    await page.goto(NORMAL_ORIGIN);
    await waitForOperatorConsole(page, 8);

    const { blocking, known } = await axeFindings(page);
    if (known.length > 0) console.log('known accessibility issues:', JSON.stringify(known, null, 2));
    expect(blocking).toEqual([]);

    // Nothing was accepted in this fresh context, so nothing may have loaded.
    expect(trackerRequests).toEqual([]);
  });

  test('the privacy dialog opens from Settings, passes axe, and closes with Escape', async ({ page }) => {
    await page.goto(NORMAL_ORIGIN);
    await waitForOperatorConsole(page, 1);

    await page.locator('#hud-settings-toggle').click();
    await page.locator('#privacy-settings-open').click();
    const dialog = page.locator('#privacy-dialog');
    await expect(dialog).toBeVisible();
    await expect(page.locator('#privacy-accept')).toBeVisible();
    await expect(page.locator('#privacy-decline')).toBeVisible();

    const { blocking } = await axeFindings(page, '#privacy-dialog');
    expect(blocking).toEqual([]);

    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
  });
});
