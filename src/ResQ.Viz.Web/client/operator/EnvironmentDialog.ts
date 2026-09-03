// ResQ Viz - lazy operator environment dialog
// SPDX-License-Identifier: Apache-2.0
//
// Terrain and weather are two independent authoritative writes behind one
// Apply, and the ordering of each is the whole design:
//
//  * **Terrain: server first.** The legacy sidebar rebuilds the mesh and then
//    fires the POST off unwatched, so a refused preset leaves the browser
//    rendering terrain the physics engine never adopted. The operator path
//    awaits acceptance before the scene follows, which is why `applyTerrain`
//    returns a `Result` rather than `void`. This module never touches the
//    scene, the preset cache or the override flag itself — the injected
//    callback owns all three.
//  * **Weather: never send what the host refuses.** `POST /api/sim/weather`
//    rejects a wind speed outside 0-100 m/s and a non-finite direction, and
//    only switches on `calm`, `steady` and `turbulent`. All three bounds are
//    mirrored here so Apply cannot offer a rejected action.
//
// Layer mode is a decision, not a media query: below 760 px the surface is a
// full-height bottom sheet and at 760 px and above it is a modal. The padding
// that distinguishes them belongs to the layer the dialog is mounted into, so
// the class goes on the mount and is cleared again on dismissal.

import '../styles/operator-dialogs.css';

import { PRESETS, type PresetKey } from '../terrainPresets';
import type { ApiFailure, Result } from '../api';

/** The exact presets `SimController.SetTerrainPreset` will accept. */
const TERRAIN_KEYS: readonly PresetKey[] = ['alpine', 'ridgeline', 'coastal', 'canyon', 'dunes'];

/** The exact modes `SimulationRoom.SetWeather` switches on; anything else is Calm. */
const WEATHER_MODES: ReadonlyArray<readonly [string, string]> = [
  ['calm', 'Calm'],
  ['steady', 'Steady wind'],
  ['turbulent', 'Turbulent'],
];

/** Host-enforced wind-speed bounds, mirrored so Apply refuses before the POST. */
const MIN_WIND_SPEED = 0;
const MAX_WIND_SPEED = 100;

/** Below this width the surface is a full-height sheet rather than a modal. */
const SHEET_MAX_WIDTH = 760;

/** The exact wire keys `POST /api/sim/weather` binds. */
export interface WeatherCommand {
  readonly mode: string;
  readonly windSpeed: number;
  readonly windDirection: number;
}

export interface EnvironmentDialogOptions {
  readonly mount: HTMLElement;
  readonly trigger: HTMLButtonElement;
  readonly fallbackFocus?: HTMLElement;
  /** Posts the preset and, only once the host accepts, applies it locally. */
  readonly applyTerrain: (key: PresetKey) => Promise<Result<unknown, ApiFailure>>;
  readonly applyWeather: (command: WeatherCommand) => Promise<Result<unknown, ApiFailure>>;
  /** Viewport width in CSS pixels; injected so the boundary is testable. */
  readonly viewportWidth: () => number;
  /** Authoritative preset, re-read on every open so the form is never stale. */
  readonly currentTerrain?: () => PresetKey;
  readonly onClose?: () => void;
}

interface EnvironmentElements {
  readonly dialog: HTMLDialogElement;
  readonly close: HTMLButtonElement;
  readonly form: HTMLFormElement;
  readonly terrain: HTMLSelectElement;
  readonly mode: HTMLSelectElement;
  readonly windSpeed: HTMLInputElement;
  readonly windDirection: HTMLInputElement;
  readonly error: HTMLElement;
  readonly status: HTMLElement;
  readonly submit: HTMLButtonElement;
}

/** Compact terrain + weather form over the two authoritative environment routes. */
export class EnvironmentDialog {
  private readonly _options: EnvironmentDialogOptions;
  private readonly _elements: EnvironmentElements;
  /** The preset the host is believed to hold; re-seeded from truth on open. */
  private _appliedTerrain: PresetKey;
  private _requestInFlight = false;
  private _generation = 0;

  constructor(options: EnvironmentDialogOptions) {
    this._options = options;
    this._elements = build();
    this._appliedTerrain = options.currentTerrain?.() ?? TERRAIN_KEYS[0]!;
    options.mount.appendChild(this._elements.dialog);

    options.trigger.setAttribute('aria-haspopup', 'dialog');
    options.trigger.setAttribute('aria-controls', this._elements.dialog.id);
    options.trigger.setAttribute('aria-expanded', 'false');
    this._elements.dialog.setAttribute('aria-busy', 'false');

    this._elements.close.addEventListener('click', () => this.close());
    for (const control of this._controls()) {
      control.addEventListener(control instanceof HTMLSelectElement ? 'change' : 'input', () => {
        control.removeAttribute('aria-invalid');
        this._showText(null);
        this._setStatus('');
      });
    }
    this._elements.form.addEventListener('submit', event => {
      event.preventDefault();
      void this._submit();
    });
    this._elements.dialog.addEventListener('keydown', event => this._onKeyDown(event));
    this._elements.dialog.addEventListener('cancel', event => {
      // Owned by the keydown handler so ordinary shortcuts never also fire.
      event.preventDefault();
    });
  }

  get isOpen(): boolean {
    return this._elements.dialog.open;
  }

  open(): void {
    // Truth first: a scenario load may have moved the preset since last time.
    this._appliedTerrain = this._options.currentTerrain?.() ?? this._appliedTerrain;
    this._elements.terrain.value = this._appliedTerrain;
    this._showText(null);
    this._setStatus('');
    this._syncLayer();
    if (!this._elements.dialog.open) this._elements.dialog.showModal();
    this._options.trigger.setAttribute('aria-expanded', 'true');
    this._elements.terrain.focus();
  }

  close(): void {
    const wasOpen = this._elements.dialog.open;
    this._generation++;
    this._requestInFlight = false;
    if (wasOpen) this._elements.dialog.close();
    this._clearLayer();
    if (!wasOpen) return;
    this._options.trigger.setAttribute('aria-expanded', 'false');
    const trigger = this._options.trigger;
    const target = trigger.isConnected && !trigger.disabled ? trigger : this._options.fallbackFocus;
    target?.focus();
    this._options.onClose?.();
  }

  /** Retires this generation so a late response cannot repaint a dead surface. */
  invalidate(): void {
    this.close();
  }

  private _controls(): readonly (HTMLInputElement | HTMLSelectElement)[] {
    const { terrain, mode, windSpeed, windDirection } = this._elements;
    return [terrain, mode, windSpeed, windDirection];
  }

  /** Sheet below 760 px, modal at and above it; the padding lives on the layer. */
  private _syncLayer(): void {
    const sheet = this._options.viewportWidth() < SHEET_MAX_WIDTH;
    this._options.mount.classList.toggle('operator-sheet', sheet);
    this._options.mount.classList.toggle('operator-modal', !sheet);
  }

  private _clearLayer(): void {
    this._options.mount.classList.remove('operator-sheet', 'operator-modal');
  }

  private async _submit(): Promise<void> {
    if (this._requestInFlight) return;

    const windSpeed = this._readNumber(this._elements.windSpeed);
    if (windSpeed === null || windSpeed < MIN_WIND_SPEED || windSpeed > MAX_WIND_SPEED) {
      this._reject(
        this._elements.windSpeed,
        `Wind speed must be a value between ${MIN_WIND_SPEED} and ${MAX_WIND_SPEED} m/s.`,
      );
      return;
    }
    const windDirection = this._readNumber(this._elements.windDirection);
    if (windDirection === null) {
      this._reject(
        this._elements.windDirection,
        'Wind direction needs a finite value in degrees clockwise from true north.',
      );
      return;
    }

    const terrain = this._elements.terrain.value as PresetKey;
    const mode = this._elements.mode.value;
    const generation = this._generation;
    this._requestInFlight = true;
    this._showText(null);
    this._setStatus('Applying environment…');
    this._setBusy(true);

    // Terrain first and awaited: the host decides, then the scene follows. A
    // refusal stops here rather than half-applying an environment.
    if (terrain !== this._appliedTerrain) {
      const applied = await this._run(() => this._options.applyTerrain(terrain));
      if (generation !== this._generation) return;
      if (!applied.success) {
        this._settle(applied.error);
        return;
      }
      this._appliedTerrain = terrain;
    }

    const weather = await this._run(
      () => this._options.applyWeather({ mode, windSpeed, windDirection }),
    );
    if (generation !== this._generation) return;
    if (!weather.success) {
      this._settle(weather.error);
      return;
    }

    this._requestInFlight = false;
    this._setBusy(false);
    this._setStatus(
      `Environment applied · ${PRESETS[terrain].name} · ${modeLabel(mode)}`
      + ` ${windSpeed} m/s from ${windDirection}°`,
    );
  }

  /** Normalises a thrown callback into the same typed failure a refusal uses. */
  private async _run(
    operation: () => Promise<Result<unknown, ApiFailure>>,
  ): Promise<Result<unknown, ApiFailure>> {
    try {
      return await operation();
    } catch (error: unknown) {
      return {
        success: false,
        error: {
          kind: 'network',
          message: error instanceof Error ? error.message : String(error),
        },
      };
    }
  }

  private _settle(failure: ApiFailure): void {
    this._requestInFlight = false;
    this._setBusy(false);
    this._setStatus('');
    this._showText(failureText(failure));
  }

  private _reject(control: HTMLElement, message: string): void {
    control.setAttribute('aria-invalid', 'true');
    this._setStatus('');
    this._elements.error.textContent = message;
    this._elements.error.hidden = false;
    control.focus();
  }

  private _readNumber(input: HTMLInputElement): number | null {
    const raw = input.value.trim();
    if (raw === '') return null;
    const value = Number(raw);
    return Number.isFinite(value) ? value : null;
  }

  private _setBusy(busy: boolean): void {
    this._elements.dialog.setAttribute('aria-busy', String(busy));
    this._elements.submit.disabled = busy;
    for (const control of this._controls()) control.disabled = busy;
  }

  private _setStatus(text: string): void {
    this._elements.status.textContent = text;
    this._elements.status.hidden = text === '';
  }

  private _showText(text: string | null): void {
    if (text === null) {
      for (const control of this._controls()) control.removeAttribute('aria-invalid');
    }
    this._elements.error.textContent = text ?? '';
    this._elements.error.hidden = text === null;
  }

  private _onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return;
      event.preventDefault();
      event.stopPropagation();
      this.close();
      return;
    }
    if (event.key !== 'Tab' || event.ctrlKey || event.metaKey || event.altKey) return;
    const focusable = Array.from(this._elements.dialog.querySelectorAll<HTMLElement>(
      'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), '
      + 'a[href], [tabindex]:not([tabindex="-1"])',
    )).filter(element => !element.hidden && element.closest('[hidden], [inert]') === null);
    if (focusable.length === 0) {
      event.preventDefault();
      this._elements.dialog.focus();
      return;
    }
    const first = focusable[0]!;
    const last = focusable[focusable.length - 1]!;
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }
}

function modeLabel(mode: string): string {
  return WEATHER_MODES.find(([value]) => value === mode)?.[1] ?? mode;
}

function failureText(failure: ApiFailure): string {
  return failure.kind === 'problem'
    ? `${failure.problem.reasonCode ?? failure.problem.code} · ${failure.problem.detail}`
    : failure.message;
}

function build(): EnvironmentElements {
  const dialog = document.createElement('dialog');
  dialog.id = 'operator-environment';
  dialog.className = 'operator-dialog environment';
  const title = document.createElement('h2');
  title.id = 'environment-title';
  title.textContent = 'Environment';
  dialog.setAttribute('aria-labelledby', title.id);

  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'operator-dialog-close';
  close.setAttribute('aria-label', 'Close environment');
  close.textContent = '×';

  const form = document.createElement('form');
  form.className = 'operator-dialog-form';
  form.noValidate = true;

  const grid = document.createElement('div');
  grid.className = 'operator-dialog-grid';

  const terrain = document.createElement('select');
  terrain.name = 'terrain';
  terrain.append(...TERRAIN_KEYS.map(key => option(key, PRESETS[key].name)));
  grid.appendChild(field('terrain', 'Terrain preset', terrain));

  const mode = document.createElement('select');
  mode.name = 'mode';
  mode.append(...WEATHER_MODES.map(([value, label]) => option(value, label)));
  grid.appendChild(field('mode', 'Weather mode', mode));

  const windSpeed = numberInput('windSpeed', '5');
  windSpeed.min = String(MIN_WIND_SPEED);
  windSpeed.max = String(MAX_WIND_SPEED);
  grid.appendChild(field('windSpeed', 'Wind speed — m/s', windSpeed));

  const windDirection = numberInput('windDirection', '0');
  windDirection.min = '0';
  windDirection.max = '359';
  grid.appendChild(field('windDirection', 'Wind direction — degrees true', windDirection));

  const error = document.createElement('p');
  error.className = 'operator-dialog-error';
  error.setAttribute('role', 'alert');
  error.hidden = true;

  const status = document.createElement('p');
  status.className = 'operator-dialog-status';
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');
  status.hidden = true;

  const actions = document.createElement('div');
  actions.className = 'operator-dialog-actions';
  const submit = document.createElement('button');
  submit.type = 'submit';
  submit.className = 'btn btn-primary';
  submit.name = 'apply';
  submit.dataset['action'] = 'apply-environment';
  submit.textContent = 'Apply';
  actions.appendChild(submit);

  form.append(grid, error, status, actions);
  dialog.append(title, close, form);
  return { dialog, close, form, terrain, mode, windSpeed, windDirection, error, status, submit };
}

function field(name: string, label: string, control: HTMLElement): HTMLElement {
  const row = document.createElement('label');
  row.className = 'operator-dialog-field';
  row.dataset['field'] = name;
  const caption = document.createElement('span');
  caption.textContent = label;
  row.append(caption, control);
  return row;
}

function option(value: string, label: string): HTMLOptionElement {
  const element = document.createElement('option');
  element.value = value;
  element.textContent = label;
  return element;
}

function numberInput(name: string, initial: string): HTMLInputElement {
  const input = document.createElement('input');
  input.type = 'number';
  input.name = name;
  input.inputMode = 'decimal';
  input.autocomplete = 'off';
  input.value = initial;
  return input;
}
