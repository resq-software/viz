// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Environment is the one operator surface that writes to two unrelated
// authoritative systems from a single button, and every failure mode it has is
// a failure of *ordering* rather than of markup:
//
//  * The terrain POST is authoritative. The legacy sidebar path rebuilds the
//    mesh first and posts afterwards, so a refused preset leaves the browser
//    showing terrain the physics engine never adopted. The operator path must
//    invert that — server first, scene second — which is only observable in
//    the callback contract asserted here.
//  * `POST /api/sim/weather` refuses a wind speed outside 0-100 and any
//    non-finite direction. A form that lets those leave the browser is a
//    control offering an action the server would reject.
//  * The surface is a modal at 760 px and a bottom sheet below it. That is a
//    layer-class decision taken at open time, not a CSS media query, because
//    the padding belongs to the layer the dialog is mounted into.
//
// Focus return on dismissal is asserted for both exits: a dialog that strands
// focus on a closed surface is unusable with a keyboard.

import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EnvironmentDialog } from '../operator/EnvironmentDialog';
import { PRESETS, type PresetKey } from '../terrainPresets';
import type { ApiFailure, ApiProblem, Result } from '../api';

/** Exactly the presets `POST /api/sim/preset/{key}` will accept. */
const SERVER_PRESETS: readonly PresetKey[] = ['alpine', 'ridgeline', 'coastal', 'canyon', 'dunes'];

/** Exactly the modes `SimulationRoom.SetWeather` switches on. */
const SERVER_MODES = ['calm', 'steady', 'turbulent'] as const;

beforeEach(() => document.body.replaceChildren());

function ok(): Result<unknown, ApiFailure> {
  return { success: true, value: undefined };
}

function refused(detail: string): Result<unknown, ApiFailure> {
  const problem: ApiProblem = {
    status: 400,
    code: 'http.error',
    reasonCode: 'weather.windSpeedOutOfRange',
    title: 'Bad Request',
    detail,
    traceId: null,
    errors: [],
  };
  return { success: false, error: { kind: 'problem', problem } };
}

function harness(
  overrides: Partial<ConstructorParameters<typeof EnvironmentDialog>[0]> = {},
) {
  const mount = document.createElement('div');
  mount.className = 'operator-modal-layer';
  const trigger = document.createElement('button');
  trigger.textContent = 'Environment';
  const fallbackFocus = document.createElement('h2');
  fallbackFocus.tabIndex = -1;
  document.body.append(trigger, fallbackFocus, mount);

  const applyTerrain = vi.fn().mockResolvedValue(ok());
  const applyWeather = vi.fn().mockResolvedValue(ok());
  let width = 759;
  const viewportWidth = vi.fn(() => width);

  const dialog = new EnvironmentDialog({
    mount,
    trigger,
    fallbackFocus,
    applyTerrain,
    applyWeather,
    viewportWidth,
    ...overrides,
  });

  return {
    dialog,
    mount,
    trigger,
    fallbackFocus,
    applyTerrain,
    applyWeather,
    viewportWidth,
    setWidth(next: number) { width = next; },
    element: mount.querySelector('dialog')!,
  };
}

function field<T extends HTMLElement>(root: ParentNode, name: string): T {
  return root.querySelector<T>(`[name="${name}"]`)!;
}

function fill(root: ParentNode, values: Record<string, string>): void {
  for (const [name, value] of Object.entries(values)) {
    const input = field<HTMLInputElement | HTMLSelectElement>(root, name);
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input', {
      bubbles: true,
    }));
  }
}

function apply(root: ParentNode): void {
  root.querySelector<HTMLFormElement>('form')!
    .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
}

function errorText(root: ParentNode): string {
  const error = root.querySelector<HTMLElement>('.operator-dialog-error')!;
  return error.hidden ? '' : error.textContent ?? '';
}

describe('EnvironmentDialog', () => {
  it('applies the chosen terrain and the exact weather wire keys', async () => {
    const bench = harness();
    bench.dialog.open();

    fill(bench.element, {
      terrain: 'coastal',
      mode: 'steady',
      windSpeed: '8',
      windDirection: '270',
    });
    apply(bench.element);
    await vi.waitFor(() => expect(bench.applyWeather).toHaveBeenCalled());

    expect(bench.applyTerrain).toHaveBeenCalledWith('coastal');
    expect(bench.applyWeather).toHaveBeenCalledWith({
      mode: 'steady',
      windSpeed: 8,
      windDirection: 270,
    });
    // Below 760 the surface is a full-height sheet, decided at open time.
    expect(bench.mount.classList.contains('operator-sheet')).toBe(true);
    expect(bench.mount.classList.contains('operator-modal')).toBe(false);
  });

  it('switches the layer to a modal at 760 and drops the sheet class', () => {
    const bench = harness();
    bench.dialog.open();
    expect(bench.mount.classList.contains('operator-sheet')).toBe(true);

    bench.dialog.close();
    bench.setWidth(760);
    bench.dialog.open();

    expect(bench.mount.classList.contains('operator-modal')).toBe(true);
    expect(bench.mount.classList.contains('operator-sheet')).toBe(false);
  });

  it('leaves the layer unstyled once the surface is dismissed', () => {
    const bench = harness();
    bench.dialog.open();
    bench.dialog.close();

    expect(bench.mount.classList.contains('operator-sheet')).toBe(false);
    expect(bench.mount.classList.contains('operator-modal')).toBe(false);
  });

  it('returns focus to the trigger from the close button and from Escape', () => {
    const bench = harness();

    bench.dialog.open();
    bench.element.querySelector<HTMLButtonElement>('.operator-dialog-close')!.click();
    expect(bench.dialog.isOpen).toBe(false);
    expect(document.activeElement).toBe(bench.trigger);

    bench.dialog.open();
    bench.element.dispatchEvent(new KeyboardEvent('keydown', {
      key: 'Escape', bubbles: true, cancelable: true,
    }));
    expect(bench.dialog.isOpen).toBe(false);
    expect(document.activeElement).toBe(bench.trigger);
  });

  it('falls back to the named element when the trigger is gone', () => {
    const bench = harness();
    bench.dialog.open();
    bench.trigger.remove();
    bench.dialog.close();

    expect(document.activeElement).toBe(bench.fallbackFocus);
  });

  it('keeps the surface open and shows the refusal when weather is rejected', async () => {
    const bench = harness();
    bench.applyWeather.mockResolvedValue(refused('WindSpeed must be between 0 and 100.'));
    bench.dialog.open();

    fill(bench.element, { mode: 'turbulent', windSpeed: '12', windDirection: '15' });
    apply(bench.element);
    await vi.waitFor(() => expect(errorText(bench.element)).not.toBe(''));

    expect(bench.dialog.isOpen).toBe(true);
    expect(errorText(bench.element)).toContain('WindSpeed must be between 0 and 100.');
    expect(errorText(bench.element)).toContain('weather.windSpeedOutOfRange');
  });

  it('reports a rejected terrain and never sends the weather behind it', async () => {
    const bench = harness();
    bench.applyTerrain.mockResolvedValue(refused("Unknown preset 'coastal'."));
    bench.dialog.open();

    fill(bench.element, { terrain: 'coastal', mode: 'steady', windSpeed: '4' });
    apply(bench.element);
    await vi.waitFor(() => expect(errorText(bench.element)).not.toBe(''));

    expect(bench.applyTerrain).toHaveBeenCalledWith('coastal');
    expect(bench.applyWeather).not.toHaveBeenCalled();
    expect(bench.dialog.isOpen).toBe(true);
  });

  it('surfaces a thrown callback as a failure instead of leaving Apply stuck', async () => {
    const bench = harness();
    bench.applyWeather.mockRejectedValue(new Error('offline'));
    bench.dialog.open();

    apply(bench.element);
    await vi.waitFor(() => expect(errorText(bench.element)).toContain('offline'));

    expect(bench.dialog.isOpen).toBe(true);
    expect(field<HTMLButtonElement>(bench.element, 'apply').disabled).toBe(false);
  });

  it('does not re-post a preset that is already the authoritative one', async () => {
    const bench = harness({ currentTerrain: () => 'canyon' });
    bench.dialog.open();

    expect(field<HTMLSelectElement>(bench.element, 'terrain').value).toBe('canyon');
    apply(bench.element);
    await vi.waitFor(() => expect(bench.applyWeather).toHaveBeenCalled());

    expect(bench.applyTerrain).not.toHaveBeenCalled();
  });

  it('re-seeds the terrain from authoritative state on every open', () => {
    let current: PresetKey = 'alpine';
    const bench = harness({ currentTerrain: () => current });
    bench.dialog.open();
    fill(bench.element, { terrain: 'dunes' });
    bench.dialog.close();

    current = 'ridgeline';
    bench.dialog.open();
    expect(field<HTMLSelectElement>(bench.element, 'terrain').value).toBe('ridgeline');
  });

  it('refuses a wind speed the server would reject rather than posting it', async () => {
    const bench = harness();
    bench.dialog.open();

    fill(bench.element, { windSpeed: '140' });
    apply(bench.element);
    await vi.waitFor(() => expect(errorText(bench.element)).not.toBe(''));

    expect(bench.applyWeather).not.toHaveBeenCalled();
    expect(bench.applyTerrain).not.toHaveBeenCalled();
    expect(errorText(bench.element)).toContain('0 and 100');
  });

  it('refuses a non-finite wind direction rather than posting it', async () => {
    const bench = harness();
    bench.dialog.open();

    fill(bench.element, { windDirection: '' });
    apply(bench.element);
    await vi.waitFor(() => expect(errorText(bench.element)).not.toBe(''));

    expect(bench.applyWeather).not.toHaveBeenCalled();
  });

  it('offers only the presets and modes the server accepts', () => {
    const bench = harness();
    bench.dialog.open();

    const presets = Array.from(
      field<HTMLSelectElement>(bench.element, 'terrain').options,
      option => option.value,
    );
    expect(presets).toEqual([...SERVER_PRESETS]);
    // Every offered preset is also a real client preset, so the local rebuild
    // after the server accepts cannot fail on a missing definition.
    for (const key of presets) expect(PRESETS).toHaveProperty(key);

    const modes = Array.from(
      field<HTMLSelectElement>(bench.element, 'mode').options,
      option => option.value,
    );
    expect(modes).toEqual([...SERVER_MODES]);
  });

  it('rejects a second Apply while the first is still in flight', async () => {
    const bench = harness();
    let release = (): void => {};
    bench.applyWeather.mockImplementation(
      () => new Promise<Result<unknown, ApiFailure>>(resolve => {
        release = () => resolve(ok());
      }),
    );
    bench.dialog.open();

    apply(bench.element);
    await vi.waitFor(() => expect(bench.applyWeather).toHaveBeenCalledTimes(1));
    apply(bench.element);
    expect(bench.applyWeather).toHaveBeenCalledTimes(1);

    release();
    await vi.waitFor(
      () => expect(field<HTMLButtonElement>(bench.element, 'apply').disabled).toBe(false),
    );
  });

  it('discards a response that lands after the surface was retired', async () => {
    const bench = harness();
    let release = (): void => {};
    bench.applyWeather.mockImplementation(
      () => new Promise<Result<unknown, ApiFailure>>(resolve => {
        release = () => resolve(refused('too late'));
      }),
    );
    bench.dialog.open();
    apply(bench.element);
    await vi.waitFor(() => expect(bench.applyWeather).toHaveBeenCalled());

    bench.dialog.invalidate();
    release();
    await Promise.resolve();
    await Promise.resolve();

    expect(bench.dialog.isOpen).toBe(false);
    expect(errorText(bench.element)).toBe('');
  });

  it('announces the applied environment so the operator sees it landed', async () => {
    const bench = harness();
    bench.dialog.open();
    fill(bench.element, { terrain: 'dunes', mode: 'steady', windSpeed: '3', windDirection: '90' });
    apply(bench.element);

    const status = bench.element.querySelector<HTMLElement>('.operator-dialog-status')!;
    await vi.waitFor(() => expect(status.textContent).toContain('Environment applied'));
    expect(status.hidden).toBe(false);
    expect(status.textContent).toContain('Dunes');
    expect(status.textContent).toContain('3 m/s from 90°');
    expect(errorText(bench.element)).toBe('');
    expect(bench.dialog.isOpen).toBe(true);
  });

  // Dismissing mid-flight is the one exit that leaves the surface in its busy
  // state: `close` retires the generation, so the awaited `_setBusy(false)`
  // after the POST never runs. Without an explicit reset the reopened dialog
  // has every control disabled and `aria-busy="true"` forever — dismissible
  // once, then permanently unable to apply an environment.
  it('reopens interactive after being dismissed mid-apply', async () => {
    let release!: () => void;
    const gate = new Promise<void>(resolve => { release = () => resolve(); });
    const applyWeather = vi.fn(async () => { await gate; return ok(); });
    const bench = harness({ applyWeather });

    bench.dialog.open();
    fill(bench.element, { mode: 'steady', windSpeed: '4', windDirection: '10' });
    apply(bench.element);
    await vi.waitFor(() => expect(applyWeather).toHaveBeenCalled());
    expect(field<HTMLSelectElement>(bench.element, 'terrain').disabled).toBe(true);

    bench.dialog.close();
    release();
    await Promise.resolve();
    await Promise.resolve();

    bench.dialog.open();
    expect(bench.element.getAttribute('aria-busy')).toBe('false');
    for (const name of ['terrain', 'mode', 'windSpeed', 'windDirection']) {
      expect(field<HTMLInputElement | HTMLSelectElement>(bench.element, name).disabled).toBe(false);
    }
    expect(bench.element.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled)
      .toBe(false);
  });

  // A second apply after that dismissal has to actually reach the host: a
  // `_requestInFlight` left true would swallow the submit silently.
  it('accepts a fresh apply after a mid-apply dismissal', async () => {
    let release!: () => void;
    const gate = new Promise<void>(resolve => { release = () => resolve(); });
    const applyWeather = vi.fn(async () => { await gate; return ok(); });
    const bench = harness({ applyWeather });

    bench.dialog.open();
    fill(bench.element, { mode: 'steady', windSpeed: '4', windDirection: '10' });
    apply(bench.element);
    await vi.waitFor(() => expect(applyWeather).toHaveBeenCalledTimes(1));
    bench.dialog.close();
    release();
    await Promise.resolve();

    bench.dialog.open();
    fill(bench.element, { mode: 'turbulent', windSpeed: '9', windDirection: '200' });
    apply(bench.element);

    await vi.waitFor(() => expect(applyWeather).toHaveBeenCalledTimes(2));
    expect(applyWeather).toHaveBeenLastCalledWith({
      mode: 'turbulent',
      windSpeed: 9,
      windDirection: 200,
    });
  });
});
