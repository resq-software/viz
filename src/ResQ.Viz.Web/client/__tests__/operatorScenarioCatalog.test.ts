// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { ScenarioCatalog } from '../operator/ScenarioCatalog';
import type { ApiFailure, Result } from '../api';
import type { ScenarioCatalogResponse } from '../operator/types';
import type { ScenarioStartResponse } from '../operator/types';
import { handleOwnedEscape } from '../ui/escapeOwnership';

const scenarios: ScenarioCatalogResponse = {
  scenarios: [
    {
      name: 'single',
      assetCount: 1,
      domainCounts: { air: 1, ground: 0, surface: 0 },
      vehicleClassCounts: { Multirotor: 1 },
    },
    {
      name: 'flood-response',
      assetCount: 8,
      domainCounts: { air: 3, ground: 3, surface: 2 },
      vehicleClassCounts: {},
    },
    {
      name: 'alpine-sar',
      assetCount: 4,
      domainCounts: { air: 4, ground: 0, surface: 0 },
      vehicleClassCounts: { Multirotor: 4 },
    },
    {
      name: 'new-preset',
      assetCount: 2,
      domainCounts: { air: 0, ground: 2, surface: 0 },
      vehicleClassCounts: {},
    },
  ],
};

const presentation = (name: string) => ({
  displayName: name === 'flood-response'
    ? 'Flood Response'
    : name === 'alpine-sar'
      ? 'Alpine SAR'
      : name === 'new-preset'
        ? 'New Preset'
        : 'Single',
  category: name === 'new-preset' ? 'Other' : 'Response',
  purpose: `Purpose for ${name}`,
  environment: name === 'alpine-sar' ? 'Alpine' : null,
});

beforeEach(() => document.body.replaceChildren());

function deferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

function problem(code = 'scenario.replacementFailed'): Result<ScenarioStartResponse, ApiFailure> {
  return {
    success: false,
    error: {
      kind: 'problem',
      problem: {
        status: 409,
        code,
        reasonCode: 'scenario.populationChanged',
        title: 'Scenario not started',
        detail: 'The current session was preserved.',
        traceId: 'trace-1',
        errors: [],
      },
    },
  };
}

function harness(overrides: Partial<ConstructorParameters<typeof ScenarioCatalog>[0]> = {}) {
  const mount = document.createElement('div');
  const trigger = document.createElement('button');
  trigger.textContent = 'Change…';
  const fallbackFocus = document.createElement('h2');
  fallbackFocus.tabIndex = -1;
  document.body.append(trigger, fallbackFocus, mount);
  const session = { assetCount: 0, tick: 0, activeName: 'single' as string | null };
  const startScenario = vi.fn().mockResolvedValue({
    success: true as const,
    value: {
      current: { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 2 },
    },
  });
  const confirmReplace = vi.fn().mockReturnValue(true);
  const onClose = vi.fn();
  const catalog = new ScenarioCatalog({
    mount,
    trigger,
    fallbackFocus,
    scenarios,
    presentation,
    getSession: () => session,
    startScenario,
    confirmReplace,
    onClose,
    ...overrides,
  });
  return {
    catalog, mount, trigger, fallbackFocus, session, startScenario, confirmReplace, onClose,
  };
}

describe('ScenarioCatalog', () => {
  it('owns a lazy, safe-area-aware shared dialog stylesheet', () => {
    const source = readFileSync(resolve(process.cwd(), 'client/operator/ScenarioCatalog.ts'), 'utf8');
    const css = readFileSync(resolve(process.cwd(), 'client/styles/operator-dialogs.css'), 'utf8');

    expect(source).toContain("import '../styles/operator-dialogs.css'");
    expect(css).toMatch(/\.operator-dialog::backdrop[\s\S]*?background:/);
    expect(css).toContain('100dvh');
    expect(css).toContain('safe-area-inset-top');
    expect(css).toContain('safe-area-inset-bottom');
    expect(css).toMatch(/\.operator-dialog :focus-visible/);
    expect(css).toMatch(/@media \(max-width: 759px\)[\s\S]*?min-height:\s*44px/);
    expect(css).toMatch(
      /@media \(max-width: 759px\)[\s\S]*?\.operator-dialog-close\s*\{[\s\S]*?min-width:\s*44px/,
    );
    expect(css).toMatch(/@media \(forced-colors: active\)/);
    for (const shared of [
      '.operator-dialog-form', '.operator-dialog-error', '.operator-dialog-actions',
    ]) expect(css).toContain(shared);
  });

  it('searches the complete server catalog and starts only after destructive confirmation', async () => {
    const mount = document.createElement('div');
    const trigger = document.createElement('button');
    document.body.append(trigger, mount);
    const session = { assetCount: 8, tick: 40, activeName: 'single' as string | null };
    const startScenario = vi.fn().mockResolvedValue({
      success: true as const,
      value: {
        current: { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 2 },
      },
    });
    const confirmReplace = vi.fn().mockReturnValue(false);
    const catalog = new ScenarioCatalog({
      mount,
      trigger,
      scenarios,
      presentation,
      getSession: () => session,
      startScenario,
      confirmReplace,
    });

    catalog.open();
    const search = mount.querySelector<HTMLInputElement>('input[type="search"]')!;
    search.value = 'flood';
    search.dispatchEvent(new Event('input', { bubbles: true }));

    expect(mount.textContent).toContain('Flood Response');
    expect(mount.textContent).toContain('8 assets');
    expect(mount.textContent).toContain('3 Air');
    expect(mount.textContent).toContain('3 Ground');
    expect(mount.textContent).toContain('2 Surface');

    mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();
    expect(confirmReplace).toHaveBeenCalledOnce();
    expect(startScenario).not.toHaveBeenCalled();

    confirmReplace.mockReturnValue(true);
    mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();
    await vi.waitFor(() => expect(startScenario).toHaveBeenCalledWith('flood-response'));
  });

  it('renders unknown scenarios under Other and shows a bound environment', () => {
    const h = harness();
    h.catalog.open();

    expect(h.mount.textContent).toContain('Alpine SAR');
    expect(h.mount.textContent).toContain('Environment · Alpine');
    const unknown = h.mount.querySelector('[data-scenario="new-preset"]')!;
    expect(unknown.textContent).toContain('New Preset');
    expect(unknown.closest('section')?.querySelector('h3')?.textContent).toBe('Other');
  });

  it('uses the stable category order instead of server arrival order', () => {
    const h = harness({
      scenarios: { scenarios: [scenarios.scenarios[3]!, scenarios.scenarios[1]!, scenarios.scenarios[0]!] },
      presentation: name => ({
        ...presentation(name),
        category: name === 'single' ? 'Exercise' : name === 'new-preset' ? 'Other' : 'Response',
      }),
    });

    expect(Array.from(h.mount.querySelectorAll('h3'), heading => heading.textContent))
      .toEqual(['Exercise', 'Response', 'Other']);
  });

  it('re-evaluates raw inventory and tick immediately before confirmation', async () => {
    const h = harness();
    h.catalog.open();
    h.session.assetCount = 0;
    h.session.tick = 0;
    h.mount.querySelector<HTMLButtonElement>('[data-scenario="single"]')!.click();
    expect(h.confirmReplace).not.toHaveBeenCalled();
    await vi.waitFor(() => expect(h.startScenario).toHaveBeenCalledOnce());
    await vi.waitFor(() => expect(h.catalog.isOpen).toBe(false));

    h.catalog.open();
    h.session.tick = 1;
    h.mount.querySelector<HTMLButtonElement>('[data-scenario="single"]')!.click();
    expect(h.confirmReplace).toHaveBeenCalledOnce();
  });

  it('marks the authoritative active scenario without changing it on typed failure', async () => {
    const h = harness({ startScenario: vi.fn().mockResolvedValue(problem()) });
    h.catalog.open();
    const active = h.mount.querySelector<HTMLButtonElement>('[data-scenario="single"]')!;
    const target = h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!;

    expect(active.getAttribute('aria-current')).toBe('true');
    expect(active.textContent).toContain('Current');
    target.click();

    await vi.waitFor(() => {
      expect(h.mount.textContent).toContain('scenario.populationChanged');
      expect(h.mount.textContent).toContain('The current session was preserved.');
    });
    expect(h.catalog.isOpen).toBe(true);
    expect(active.getAttribute('aria-current')).toBe('true');
    expect(target.disabled).toBe(false);
    expect(h.mount.querySelector('dialog')?.getAttribute('aria-busy')).toBe('false');
  });

  it.each([
    [{ kind: 'network' as const, message: 'offline' }, 'offline'],
    [{ kind: 'timeout' as const, message: 'Request timed out' }, 'Request timed out'],
  ])('renders a %s failure and leaves the modal retryable', async (error, expected) => {
    const h = harness({
      startScenario: vi.fn().mockResolvedValue({ success: false, error }),
    });
    h.catalog.open();
    h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();

    await vi.waitFor(() => expect(h.mount.textContent).toContain(expected));
    expect(h.catalog.isOpen).toBe(true);
    expect(h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')?.disabled)
      .toBe(false);
  });

  it('allows only one scenario POST while an action is pending', async () => {
    const result = deferred<Result<ScenarioStartResponse, ApiFailure>>();
    const startScenario = vi.fn(() => result.promise);
    const h = harness({ startScenario });
    h.catalog.open();
    const target = h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!;

    target.click();
    target.click();

    expect(startScenario).toHaveBeenCalledOnce();
    expect(h.mount.querySelector('dialog')?.getAttribute('aria-busy')).toBe('true');
    expect(target.disabled).toBe(true);
    result.resolve(problem());
    await vi.waitFor(() => expect(target.disabled).toBe(false));
  });

  it('keeps search and programmatically rebuilt cards disabled until the request settles', async () => {
    const result = deferred<Result<ScenarioStartResponse, ApiFailure>>();
    const h = harness({ startScenario: vi.fn(() => result.promise) });
    h.catalog.open();
    h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();
    const search = h.mount.querySelector<HTMLInputElement>('input[type="search"]')!;

    expect(search.disabled).toBe(true);
    search.value = 'single';
    search.dispatchEvent(new Event('input', { bubbles: true }));
    const rebuilt = h.mount.querySelector<HTMLButtonElement>('[data-scenario="single"]')!;
    expect(rebuilt.disabled).toBe(true);

    result.resolve(problem());
    await vi.waitFor(() => expect(search.disabled).toBe(false));
    expect(rebuilt.disabled).toBe(false);
  });

  it('focuses search, traps Tab in both directions, consumes Escape, and restores the trigger', () => {
    const h = harness();
    const escapedToWindow = vi.fn();
    window.addEventListener('keydown', escapedToWindow);
    h.catalog.open();
    const dialog = h.mount.querySelector('dialog')!;
    const search = h.mount.querySelector<HTMLInputElement>('input[type="search"]')!;
    const focusable = Array.from(dialog.querySelectorAll<HTMLElement>('button, input'));

    expect(document.activeElement).toBe(search);
    expect(dialog.getAttribute('aria-busy')).toBe('false');
    expect(h.trigger.getAttribute('aria-haspopup')).toBe('dialog');
    expect(h.trigger.getAttribute('aria-controls')).toBe(dialog.id);
    expect(h.trigger.getAttribute('aria-expanded')).toBe('true');
    focusable[focusable.length - 1]!.focus();
    focusable[focusable.length - 1]!.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'Tab', bubbles: true, cancelable: true,
    }));
    expect(document.activeElement).toBe(focusable[0]);

    focusable[0]!.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'Tab', shiftKey: true, bubbles: true, cancelable: true,
    }));
    expect(document.activeElement).toBe(focusable[focusable.length - 1]);

    escapedToWindow.mockClear();
    search.focus();
    search.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'Escape', bubbles: true, cancelable: true,
    }));
    expect(h.catalog.isOpen).toBe(false);
    expect(document.activeElement).toBe(h.trigger);
    expect(h.trigger.getAttribute('aria-expanded')).toBe('false');
    expect(escapedToWindow).not.toHaveBeenCalled();
    window.removeEventListener('keydown', escapedToWindow);
  });

  it.each([
    ['disabled', (trigger: HTMLButtonElement) => { trigger.disabled = true; }],
    ['aria-disabled', (trigger: HTMLButtonElement) => { trigger.setAttribute('aria-disabled', 'true'); }],
    ['aria-hidden', (trigger: HTMLButtonElement) => { trigger.setAttribute('aria-hidden', 'true'); }],
    ['hidden', (trigger: HTMLButtonElement) => { trigger.hidden = true; }],
    ['display-none', (trigger: HTMLButtonElement) => { trigger.style.display = 'none'; }],
    ['visibility-hidden', (trigger: HTMLButtonElement) => { trigger.style.visibility = 'hidden'; }],
  ])('returns focus to the fleet heading when the trigger is %s', (_label, makeUnavailable) => {
    const h = harness();
    h.catalog.open();
    makeUnavailable(h.trigger);

    h.catalog.close();

    expect(document.activeElement).toBe(h.fallbackFocus);
  });

  it('uses fallback focus when successful request state disables Change before close', async () => {
    const h = harness();
    h.startScenario.mockImplementationOnce(async () => {
      h.trigger.disabled = true;
      return {
        success: true,
        value: {
          current: { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 2 },
        },
      };
    });
    h.catalog.open();
    h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();

    await vi.waitFor(() => expect(h.catalog.isOpen).toBe(false));

    expect(document.activeElement).toBe(h.fallbackFocus);
  });

  it.each([
    ['prevented', {}, true],
    ['Ctrl', { ctrlKey: true }, false],
    ['Meta', { metaKey: true }, false],
    ['Alt', { altKey: true }, false],
  ])('leaves %s Escape to existing guards without closing or mutating them', (
    _label,
    init,
    prevented,
  ) => {
    const h = harness();
    const underlyingMutation = vi.fn();
    const globalOwner = (event: KeyboardEvent): void => {
      handleOwnedEscape(
        event,
        true,
        false,
        () => false,
        underlyingMutation,
        vi.fn(),
        vi.fn(),
      );
    };
    window.addEventListener('keydown', globalOwner);
    h.catalog.open();
    const event = new KeyboardEvent('keydown', {
      key: 'Escape', bubbles: true, cancelable: true, ...init,
    });
    if (prevented) event.preventDefault();

    h.mount.querySelector<HTMLInputElement>('input[type="search"]')!.dispatchEvent(event);

    expect(h.catalog.isOpen).toBe(true);
    expect(underlyingMutation).not.toHaveBeenCalled();
    window.removeEventListener('keydown', globalOwner);
  });

  it('prevents native cancel from bypassing guarded Escape ownership', () => {
    const h = harness();
    h.catalog.open();
    const cancel = new Event('cancel', { cancelable: true });

    h.mount.querySelector('dialog')!.dispatchEvent(cancel);

    expect(cancel.defaultPrevented).toBe(true);
    expect(h.catalog.isOpen).toBe(true);
  });

  it('falls back to the fleet heading when its trigger became inert', () => {
    const h = harness();
    const branch = document.createElement('section');
    h.trigger.replaceWith(branch);
    branch.appendChild(h.trigger);
    h.catalog.open();
    branch.setAttribute('inert', '');

    h.catalog.close();

    expect(document.activeElement).toBe(h.fallbackFocus);
  });

  it('invalidates a pending result without a late close or repaint', async () => {
    const result = deferred<Result<ScenarioStartResponse, ApiFailure>>();
    const h = harness({ startScenario: vi.fn(() => result.promise) });
    h.catalog.open();
    h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();
    h.catalog.invalidate();
    expect(h.onClose).toHaveBeenCalledOnce();

    result.resolve(problem('late.failure'));
    await Promise.resolve();
    await Promise.resolve();

    expect(h.catalog.isOpen).toBe(false);
    expect(h.onClose).toHaveBeenCalledOnce();
    expect(h.mount.textContent).not.toContain('late.failure');
  });

  it.each([
    ['failure', problem('late.failure')],
    ['success', {
      success: true as const,
      value: {
        current: { name: 'flood-response', startedAtSimulationSeconds: 0, revision: 2 },
      },
    }],
  ])('keeps a reopened modal busy until an old %s settles without stale UI', async (_label, outcome) => {
    const result = deferred<Result<ScenarioStartResponse, ApiFailure>>();
    const h = harness({ startScenario: vi.fn(() => result.promise) });
    h.catalog.open();
    h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();
    h.catalog.close();
    h.onClose.mockClear();
    h.catalog.open();
    const search = h.mount.querySelector<HTMLInputElement>('input[type="search"]')!;
    const close = h.mount.querySelector<HTMLButtonElement>('.operator-dialog-close')!;

    expect(h.mount.querySelector('dialog')?.getAttribute('aria-busy')).toBe('true');
    expect(h.mount.querySelector<HTMLButtonElement>('[data-scenario="single"]')?.disabled).toBe(true);
    expect(document.activeElement).toBe(close);
    result.resolve(outcome);
    await vi.waitFor(() => {
      expect(h.mount.querySelector('dialog')?.getAttribute('aria-busy')).toBe('false');
    });

    expect(h.catalog.isOpen).toBe(true);
    expect(h.mount.querySelector<HTMLButtonElement>('[data-scenario="single"]')?.disabled).toBe(false);
    expect(h.mount.textContent).not.toContain('late.failure');
    expect(document.activeElement).toBe(close);
    expect(h.onClose).not.toHaveBeenCalled();
  });

  it('unlocks a closed modal after its invalidated request settles before reopening', async () => {
    const result = deferred<Result<ScenarioStartResponse, ApiFailure>>();
    const h = harness({ startScenario: vi.fn(() => result.promise) });
    h.catalog.open();
    h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')!.click();
    h.catalog.close();

    result.resolve(problem('late.failure'));
    await Promise.resolve();
    await Promise.resolve();
    h.catalog.open();

    expect(h.mount.querySelector('dialog')?.getAttribute('aria-busy')).toBe('false');
    expect(h.mount.querySelector<HTMLButtonElement>('[data-scenario="flood-response"]')?.disabled)
      .toBe(false);
    expect(h.mount.textContent).not.toContain('late.failure');
  });

  it('refreshes the active marker after a remote streamed scenario change', () => {
    const h = harness();
    h.catalog.open();
    h.session.activeName = 'new-preset';

    h.catalog.refreshSession();

    expect(h.mount.querySelector('[data-scenario="single"]')?.getAttribute('aria-current')).toBeNull();
    const current = h.mount.querySelector('[data-scenario="new-preset"]')!;
    expect(current.getAttribute('aria-current')).toBe('true');
    expect(current.textContent).toContain('Current');
  });
});
