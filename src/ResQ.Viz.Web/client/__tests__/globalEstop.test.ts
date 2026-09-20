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

  // holdToConfirm used to arm on any press and leave the aria-disabled check to
  // confirm time. Both callers do re-check there, but availability can flip from
  // blocked to allowed DURING the hold — the asset panel recomputes it on every
  // stream frame — and the confirm check then passes for a hold begun against a
  // control that was visibly refusing.
  it('does not arm at all while it is refusing', async () => {
    const { button, selection } = mount();
    const issue = vi.fn();
    const announced: string[] = [];
    mountGlobalEstop({
      button, selection, issue: issue as never, holdMs: 30,
      announce: (m) => announced.push(m),
    });
    expect(button.getAttribute('aria-disabled')).toBe('true');

    button.dispatchEvent(new Event('pointerdown', { bubbles: true }));

    // Not merely "does not fire" — it never begins. A progress fill and a "hold
    // to confirm" prompt on a control that will refuse is a promise it cannot keep.
    expect(button.hasAttribute('data-holding')).toBe(false);
    expect(announced.join(' ')).not.toMatch(/hold to confirm/i);

    await new Promise((r) => setTimeout(r, 60));
    expect(issue).not.toHaveBeenCalled();
  });

  it('abandons a hold that is already running when the control starts refusing', async () => {
    const { button, selection } = mount();
    const issue = vi.fn();
    const announced: string[] = [];
    selection.set('asset', 'fr-ferry-1');
    mountGlobalEstop({
      button, selection, issue: issue as never, holdMs: 40,
      announce: (m) => announced.push(m),
    });

    button.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    expect(button.hasAttribute('data-holding')).toBe(true);

    // The asset goes away under the operator's finger.
    selection.clear();
    await new Promise((r) => setTimeout(r, 0));
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.hasAttribute('data-holding'), 'the hold died with the target').toBe(false);

    // And it asks for something that can actually be done. "Hold again" would be
    // impossible here: with nothing selected the control refuses to arm at all.
    const said = announced.join(' ');
    expect(said).toMatch(/select an asset/i);
    expect(said, 'does not ask for a hold the guard will refuse').not.toMatch(/hold again to stop/i);

    await new Promise((r) => setTimeout(r, 60));
    expect(issue).not.toHaveBeenCalled();
  });

  // The defect this guards: `target` is mutable and `selection.subscribe` rewrites
  // it from anywhere — a stream frame, a map click, a keyboard shortcut. `fire()`
  // read it at CONFIRM time, so an operator who armed the stop against one asset
  // and looked away for the length of the hold would stop a different one.
  it('refuses when the selection changes while the operator is holding', async () => {
    const { button, selection } = mount();
    const issue = vi.fn(async () => ({ accepted: true, message: 'Emergency stop accepted.' }));
    const announced: string[] = [];
    selection.set('asset', 'fr-ferry-1');
    mountGlobalEstop({
      button, selection, issue: issue as never, holdMs: 40,
      announce: (m) => announced.push(m),
    });

    // Arm against ferry-1, and let the world move under it mid-hold.
    button.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    await new Promise((r) => setTimeout(r, 10));
    expect(button.hasAttribute('data-holding')).toBe(true);

    selection.set('asset', 'fr-mapper-n');
    await new Promise((r) => setTimeout(r, 0));

    // Cancelled AT THE SWAP, not 800ms later at the confirm. The operator finds
    // out while their finger is still down, not after waiting out a hold that was
    // never going to fire.
    expect(button.hasAttribute('data-holding'), 'the hold ended immediately').toBe(false);
    expect(announced.join(' ')).toMatch(/cancelled/i);
    expect(announced.join(' '), 'names what it was armed against').toMatch(/fr-ferry-1/);
    expect(announced.join(' '), 'and what to re-arm against').toMatch(/fr-mapper-n/);

    // And nothing is stopped: not the asset it no longer names, not the one the
    // operator never armed.
    await new Promise((r) => setTimeout(r, 60));
    expect(issue).not.toHaveBeenCalled();
  });

  it('stops the asset it was armed against when the selection holds still', async () => {
    const { button, selection } = mount();
    // Typed with its real parameters: an argless `vi.fn` infers a zero-length
    // call tuple, so reading `calls[0][0]` is a compile error `tsc --noEmit`
    // catches even though vitest runs it happily.
    const issue = vi.fn(async (_assetId: string, _body: unknown) =>
      ({ accepted: true, message: 'Emergency stop accepted.' }));
    selection.set('asset', 'fr-ferry-1');
    mountGlobalEstop({ button, selection, issue: issue as never, holdMs: 40 });

    await hold(button, 80);

    expect(issue).toHaveBeenCalledTimes(1);
    expect(issue.mock.calls[0]?.[0]).toBe('fr-ferry-1');
  });

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
