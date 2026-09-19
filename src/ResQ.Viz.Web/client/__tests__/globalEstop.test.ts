// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SelectionStore } from '../editor/selection';
import { mountGlobalEstop } from '../operator/globalEstop';

/**
 * VEHICLE_DASHBOARDS.md §4.7 makes four demands of this control: always mounted,
 * reachable by keyboard from anywhere, guarded by hold-to-confirm, and honest
 * about why it refuses. Each is asserted here, because each was absent before.
 */
function mount(): { button: HTMLButtonElement; selection: SelectionStore } {
  document.body.innerHTML = '<button id="hud-estop" type="button"></button>';
  const button = document.getElementById('hud-estop') as HTMLButtonElement;
  return { button, selection: new SelectionStore() };
}

async function hold(button: HTMLElement, ms: number): Promise<void> {
  button.dispatchEvent(new Event('pointerdown', { bubbles: true }));
  await new Promise((r) => setTimeout(r, ms));
  await new Promise((r) => setTimeout(r, 0));
}

describe('shell-level emergency stop', () => {
  beforeEach(() => { document.body.innerHTML = ''; });

  it('refuses in place, naming the reason, when nothing is selected', async () => {
    const { button, selection } = mount();
    const issue = vi.fn();
    mountGlobalEstop({ button, selection, issue: issue as never, holdMs: 30 });

    // Mounted and visible — refusing, not absent. An operator reaching for it in
    // an emergency must find it where it always is.
    expect(button.isConnected).toBe(true);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.getAttribute('aria-label')).toMatch(/no asset selected/i);

    await hold(button, 80);
    expect(issue).not.toHaveBeenCalled();
  });

  it('commands the selected asset once the press is held', async () => {
    const { button, selection } = mount();
    // Parameters declared so the recorded call is typed and the TARGET can be
    // asserted — "it fired" is not enough for a control that stops a vehicle.
    const issue = vi.fn(async (_assetId: string, _request: { kind: string }) => ({
      accepted: true as const, message: 'Emergency stop accepted.', result: {} as never,
    }));
    mountGlobalEstop({ button, selection, issue: issue as never, holdMs: 30 });

    selection.set('asset', 'fr-ferry-1');
    expect(button.getAttribute('aria-disabled')).toBe('false');

    await hold(button, 90);
    expect(issue).toHaveBeenCalledTimes(1);
    expect(issue.mock.calls[0]?.[0]).toBe('fr-ferry-1');
    expect(issue.mock.calls[0]?.[1]?.kind).toBe('emergencyStop');
  });

  it('does not fire on a single click', async () => {
    const { button, selection } = mount();
    const issue = vi.fn(async () => ({ accepted: true as const, message: 'ok', result: {} as never }));
    mountGlobalEstop({ button, selection, issue: issue as never, holdMs: 30 });
    selection.set('asset', 'fr-ferry-1');

    button.click();
    await new Promise((r) => setTimeout(r, 60));
    expect(issue).not.toHaveBeenCalled();
  });

  it('is focusable from anywhere with Shift+Escape, and the shortcut does not fire it', async () => {
    const { button, selection } = mount();
    const issue = vi.fn(async () => ({ accepted: true as const, message: 'ok', result: {} as never }));
    const other = document.createElement('input');
    document.body.appendChild(other);
    other.focus();

    mountGlobalEstop({ button, selection, issue: issue as never, holdMs: 30 });
    selection.set('asset', 'fr-ferry-1');

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', shiftKey: true, bubbles: true }));
    expect(document.activeElement).toBe(button);
    // Reachability must not become a one-keystroke stop.
    expect(issue).not.toHaveBeenCalled();
  });

  it('reports what the server said rather than claiming the vehicle stopped', async () => {
    const { button, selection } = mount();
    const announce = vi.fn();
    const issue = vi.fn(async () => ({
      accepted: true as const, message: 'Emergency stop accepted.', result: {} as never,
    }));
    mountGlobalEstop({ button, selection, issue: issue as never, announce, holdMs: 30 });
    selection.set('asset', 'fr-ferry-1');

    await hold(button, 90);
    await new Promise((r) => setTimeout(r, 0));
    // "accepted", never "stopped": transport acceptance is not physical completion.
    expect(announce).toHaveBeenCalledWith('Emergency stop accepted.');
    expect(announce.mock.calls.flat().join(' ')).not.toMatch(/\bstopped\b/i);
  });

  it('stops listening once destroyed', async () => {
    const { button, selection } = mount();
    const issue = vi.fn(async () => ({ accepted: true as const, message: 'ok', result: {} as never }));
    const handle = mountGlobalEstop({ button, selection, issue: issue as never, holdMs: 30 });
    selection.set('asset', 'fr-ferry-1');
    handle.destroy();

    await hold(button, 90);
    expect(issue).not.toHaveBeenCalled();
  });
});
