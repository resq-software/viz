// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { MissionChrome } from '../missionChrome';
import { MissionPanel } from '../operator/MissionPanel';
import { scenarioPresentation } from '../operator/scenarioPresentation';
import type { ApiFailure } from '../api';

const unavailable: ApiFailure = {
  kind: 'problem',
  problem: {
    status: 503,
    code: 'catalog.unavailable',
    reasonCode: null,
    title: 'Unavailable',
    detail: 'Retry later',
    traceId: null,
    errors: [],
  },
};

function harness() {
  const mount = document.createElement('section');
  document.body.appendChild(mount);
  const onTogglePause = vi.fn();
  const onReset = vi.fn();
  const onChange = vi.fn();
  const onRetryCatalog = vi.fn();
  const panel = new MissionPanel({
    mount,
    onTogglePause,
    onReset,
    onChange,
    onRetryCatalog,
  });
  return { mount, panel, onTogglePause, onReset, onChange, onRetryCatalog };
}

const readyCatalog = { status: 'ready' as const, value: { scenarios: [] } };

beforeEach(() => {
  document.body.replaceChildren();
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('scenario presentation', () => {
  it('humanizes unknown scenarios under Other and resolves bound environments', () => {
    expect(scenarioPresentation('new-preset')).toEqual({
      displayName: 'New Preset',
      category: 'Other',
      purpose: 'Configured scenario',
      environment: null,
    });
    expect(scenarioPresentation('alpine-sar')).toMatchObject({
      displayName: 'Alpine SAR',
      environment: 'Alpine',
    });
  });
});

describe('MissionPanel', () => {
  it('ships compact mission metadata, stable actions, and visible typed errors', () => {
    const css = readFileSync(resolve(process.cwd(), 'client/styles/operator.css'), 'utf8');
    expect(css).toMatch(/\.operator-mission-meta\s*\{[\s\S]*?font-variant-numeric:\s*tabular-nums/);
    expect(css).toMatch(/\.operator-mission-actions\s*\{[\s\S]*?grid-template-columns/);
    expect(css).toMatch(/\.operator-resource-error\s*\{[\s\S]*?color:\s*var\(--danger\)/);
  });

  it('renders scenario-relative elapsed time, running state, and speed', () => {
    const h = harness();

    h.panel.render({
      mission: {
        kind: 'active',
        name: 'flood-response',
        startedAtSimulationSeconds: 10,
        revision: 2,
        pendingName: null,
      },
      transport: { paused: false, speed: 2, simulationTimeSeconds: 18.2 },
      catalog: readyCatalog,
    });

    expect(h.mount.textContent).toContain('Flood Response');
    expect(h.mount.textContent).toContain('Running');
    expect(h.mount.textContent).toContain('8.2s');
    expect(h.mount.textContent).toContain('2×');
  });

  it('keeps the Change button node stable while patching state', () => {
    const h = harness();
    h.panel.render({
      mission: { kind: 'none', pendingName: null },
      transport: { paused: false, speed: 1, simulationTimeSeconds: 0 },
      catalog: readyCatalog,
    });
    const change = h.mount.querySelector<HTMLButtonElement>('[data-action="change"]')!;
    change.focus();

    h.panel.render({
      mission: {
        kind: 'active', name: 'single', revision: 2,
        startedAtSimulationSeconds: 0, pendingName: null,
      },
      transport: { paused: true, speed: 4, simulationTimeSeconds: 3 },
      catalog: readyCatalog,
    });

    expect(h.mount.querySelector('[data-action="change"]')).toBe(change);
    expect(document.activeElement).toBe(change);
  });

  it.each([
    [{ kind: 'unknown' as const, pendingName: null }, 'Waiting for authoritative scenario state'],
    [{ kind: 'none' as const, pendingName: null }, 'No active mission'],
    [{ kind: 'custom' as const, pendingName: null }, 'Custom session'],
  ])('renders the exact %s mission copy', (mission, expected) => {
    const h = harness();
    h.panel.render({
      mission,
      transport: { paused: false, speed: 1, simulationTimeSeconds: 3 },
      catalog: readyCatalog,
    });
    expect(h.mount.textContent).toContain(expected);
  });

  it('renders accepted scenario and reset requests without replacing the active name', () => {
    const h = harness();
    h.panel.render({
      mission: {
        kind: 'pending', name: 'flood-response', pendingName: 'coastal-search',
        pendingKind: 'scenario', startedAtSimulationSeconds: 0, revision: 2,
      },
      transport: { paused: true, speed: 1, simulationTimeSeconds: 19 },
      catalog: readyCatalog,
    });
    expect(h.mount.textContent).toContain('Flood Response');
    expect(h.mount.textContent).toContain('Starting Coastal Search');
    expect(h.mount.querySelector<HTMLButtonElement>('[data-action="change"]')!.disabled).toBe(true);
    expect(h.mount.querySelector<HTMLButtonElement>('[data-action="reset"]')!.disabled).toBe(true);
    expect(h.mount.querySelector<HTMLButtonElement>('[data-action="pause"]')!.disabled).toBe(false);

    h.panel.render({
      mission: {
        kind: 'pending', name: 'flood-response', pendingName: null,
        pendingKind: 'reset', startedAtSimulationSeconds: 0, revision: 2,
      },
      transport: { paused: true, speed: 1, simulationTimeSeconds: 19 },
      catalog: readyCatalog,
    });
    expect(h.mount.textContent).toContain('Resetting mission');
  });

  it.each([
    {
      baseKind: 'custom' as const,
      pendingKind: 'scenario' as const,
      name: 'flood-response',
      pendingName: 'flood-response',
      title: 'Custom session',
      pending: 'Starting Flood Response',
    },
    {
      baseKind: 'none' as const,
      pendingKind: 'scenario' as const,
      name: 'flood-response',
      pendingName: 'flood-response',
      title: 'No active mission',
      pending: 'Starting Flood Response',
    },
    {
      baseKind: 'custom' as const,
      pendingKind: 'reset' as const,
      name: null,
      pendingName: null,
      title: 'Custom session',
      pending: 'Resetting mission',
    },
  ])('keeps the $baseKind title separate from $pendingKind pending copy', input => {
    const h = harness();
    h.panel.render({
      mission: {
        kind: 'pending',
        baseKind: input.baseKind,
        pendingKind: input.pendingKind,
        name: input.name,
        pendingName: input.pendingName,
      },
      transport: { paused: false, speed: 1, simulationTimeSeconds: 19 },
      catalog: readyCatalog,
    });

    expect(h.mount.querySelector('.operator-mission-title')?.textContent).toBe(input.title);
    expect(h.mount.querySelector('.operator-mission-pending')?.textContent).toContain(input.pending);
  });

  it('performs no DOM writes when a 10 Hz render repeats identical state', async () => {
    const h = harness();
    const state = {
      mission: {
        kind: 'active' as const,
        name: 'flood-response',
        startedAtSimulationSeconds: 10,
        revision: 2,
        pendingName: null,
      },
      transport: { paused: false, speed: 2, simulationTimeSeconds: 18.2 },
      catalog: readyCatalog,
    };
    h.panel.render(state);
    const mutations: MutationRecord[] = [];
    const observer = new MutationObserver(records => mutations.push(...records));
    observer.observe(h.mount, {
      attributes: true,
      childList: true,
      characterData: true,
      subtree: true,
    });

    h.panel.render(state);
    await Promise.resolve();
    observer.disconnect();

    expect(mutations).toEqual([]);
  });

  it('keeps transport usable, disables Change, and exposes typed catalog recovery', () => {
    const h = harness();
    h.panel.render({
      mission: { kind: 'custom', pendingName: null },
      transport: { paused: false, speed: 1, simulationTimeSeconds: 19 },
      catalog: { status: 'error', failure: unavailable },
    });

    const change = h.mount.querySelector<HTMLButtonElement>('[data-action="change"]')!;
    expect(change.disabled).toBe(true);
    expect(h.mount.textContent).toContain('catalog.unavailable');
    expect(h.mount.textContent).toContain('Retry later');
    h.mount.querySelector<HTMLButtonElement>('[data-action="retry-catalog"]')!.click();
    expect(h.onRetryCatalog).toHaveBeenCalledOnce();

    h.mount.querySelector<HTMLButtonElement>('[data-action="pause"]')!.click();
    h.mount.querySelector<HTMLButtonElement>('[data-action="reset"]')!.click();
    expect(h.onTogglePause).toHaveBeenCalledWith(true);
    expect(h.onReset).toHaveBeenCalledOnce();
  });

  it('passes the desired resume state and invokes the stable Change callback', () => {
    const h = harness();
    h.panel.render({
      mission: { kind: 'none', pendingName: null },
      transport: { paused: true, speed: 1, simulationTimeSeconds: 0 },
      catalog: readyCatalog,
    });

    h.mount.querySelector<HTMLButtonElement>('[data-action="pause"]')!.click();
    h.mount.querySelector<HTMLButtonElement>('[data-action="change"]')!.click();
    expect(h.onTogglePause).toHaveBeenCalledWith(false);
    expect(h.onChange).toHaveBeenCalledOnce();
  });
});

describe('legacy MissionChrome', () => {
  it('clears stale state and ignores scenario events while disabled', () => {
    const chrome = new MissionChrome();
    document.dispatchEvent(new CustomEvent('resq:scenario-start', {
      detail: { name: 'single' },
    }));
    chrome.update(4);
    expect(document.querySelector('.mission-chrome')?.textContent).toContain('SINGLE');

    chrome.setEnabled(false);
    document.dispatchEvent(new CustomEvent('resq:scenario-start', {
      detail: { name: 'flood-response' },
    }));
    chrome.setEnabled(true);
    chrome.update(8);

    const element = document.querySelector<HTMLElement>('.mission-chrome')!;
    expect(element.classList.contains('hidden')).toBe(true);
    expect(element.getAttribute('aria-hidden')).toBe('true');
    expect(element.textContent).not.toContain('FLOOD-RESPONSE');
  });
});
