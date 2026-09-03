// ResQ Viz - lazy multi-domain asset spawn dialog
// SPDX-License-Identifier: Apache-2.0
//
// Everything this form offers has to be something `POST /api/v2/sim/assets`
// would actually accept, and the two places that is not obvious are both
// deployment facts rather than type-system facts:
//
//  * The spawnable class list is discovered from `GET /api/v2/sim/asset-profiles`,
//    which probes registered factories. A hard-coded list here would offer
//    classes a given host cannot build and hide ones it can.
//  * An air spawn goes through the flight world's `AddDrone`, which carries an
//    id, a position and a vendor tag and nothing else. The host *refuses* a
//    displayName, model, agencyId or fleetId on that path rather than dropping
//    them, so those inputs are withheld for the discovered Air domain instead
//    of being sent and apologised for.
//
// Acceptance is not arrival: a 201 names the asset, and the roster still comes
// from the stream. This dialog therefore has no roster seam at all.

import '../styles/operator-dialogs.css';

import { Matrix4, Quaternion } from 'three';

import { AssetDomain, CoordinateFrame } from '../assets/types';
import type { ApiFailure, Result } from '../api';
import type { WireQuat, WireVec3 } from '../types';
import type { ResourceState } from './ConsoleResources';
import type {
  AssetProfileCatalogResponse,
  AssetSpawnProfile,
  AssetSpawnRequest,
  AssetSpawnResponse,
} from './types';

/** The all-zero quaternion: "no attitude was declared", which is not a rotation. */
const OMITTED_ORIENTATION: WireQuat = Object.freeze({ x: 0, y: 0, z: 0, w: 0 });

/** Descriptor fields the air spawn path refuses outright. */
const AIR_UNSUPPORTED_METADATA = ['displayName', 'model', 'agencyId', 'fleetId'] as const;

/** Free-text descriptor budget enforced by the host, mirrored so the input can cap. */
const MAX_METADATA_LENGTH = 64;

const DOMAIN_LABELS = new Map<number, string>([
  [AssetDomain.Unspecified, 'Unspecified'],
  [AssetDomain.Air, 'Air'],
  [AssetDomain.Ground, 'Ground'],
  [AssetDomain.Surface, 'Surface'],
  [AssetDomain.Subsurface, 'Subsurface'],
  [AssetDomain.Fixed, 'Fixed'],
]);

/**
 * Builds the level EUS-from-FLU attitude for a heading, matching the server's
 * `CoordinateFrames.HeadingToEusOrientation` basis exactly.
 *
 * Not a yaw about `+Y`: the wire's body convention is FLU (`+X` forward, `+Y`
 * left, `+Z` up) and the scene frame is EUS (`+X` east, `+Y` up, `+Z` south),
 * so the change of basis is part of the rotation. A yaw-only quaternion agrees
 * on the forward axis and puts body up on a horizontal axis, which the host
 * then reads back as a different heading.
 *
 * @param headingDegrees Heading clockwise from true north. Non-finite reads as north.
 * @returns A unit quaternion as named wire components, never a Three.js instance.
 */
export function headingToEusQuaternion(headingDegrees: number): WireQuat {
  const heading = Number.isFinite(headingDegrees)
    ? (headingDegrees * Math.PI) / 180
    : 0;
  const sin = Math.sin(heading);
  const cos = Math.cos(heading);
  // Rows, so the columns are the FLU body axes expressed in EUS: forward, left, up.
  const basis = new Matrix4().set(
    sin, -cos, 0, 0,
    0, 0, 1, 0,
    -cos, -sin, 0, 0,
    0, 0, 0, 1,
  );
  const rotation = new Quaternion().setFromRotationMatrix(basis).normalize();
  return { x: rotation.x, y: rotation.y, z: rotation.z, w: rotation.w };
}

export interface SpawnAssetDialogOptions {
  readonly mount: HTMLElement;
  readonly trigger: HTMLButtonElement;
  readonly fallbackFocus?: HTMLElement;
  /** Deployment-discovered profiles; the only source of spawnable classes. */
  readonly profiles: () => ResourceState<AssetProfileCatalogResponse>;
  readonly spawn: (
    request: AssetSpawnRequest,
  ) => Promise<Result<AssetSpawnResponse, ApiFailure>>;
  readonly onRetryProfiles?: () => void;
  /** Reports the accepted id only; streamed state still owns the roster. */
  readonly onAccepted?: (assetId: string) => void;
  readonly onClose?: () => void;
}

interface SpawnElements {
  readonly dialog: HTMLDialogElement;
  readonly close: HTMLButtonElement;
  readonly form: HTMLFormElement;
  readonly select: HTMLSelectElement;
  readonly domain: HTMLElement;
  readonly error: HTMLElement;
  readonly status: HTMLElement;
  readonly submit: HTMLButtonElement;
  readonly rows: ReadonlyMap<string, HTMLElement>;
  readonly inputs: ReadonlyMap<string, HTMLInputElement>;
}

/** Compact spawn form over the typed v2 endpoint and its discovered profiles. */
export class SpawnAssetDialog {
  private readonly _options: SpawnAssetDialogOptions;
  private readonly _elements: SpawnElements;
  private readonly _retry: HTMLButtonElement;
  private _renderedProfiles: AssetProfileCatalogResponse | null = null;
  private _requestInFlight = false;
  private _generation = 0;

  constructor(options: SpawnAssetDialogOptions) {
    this._options = options;
    this._elements = build();
    this._retry = buildRetry();
    options.mount.appendChild(this._elements.dialog);
    if (options.trigger.insertAdjacentElement('afterend', this._retry) === null) {
      options.mount.appendChild(this._retry);
    }

    options.trigger.setAttribute('aria-haspopup', 'dialog');
    options.trigger.setAttribute('aria-controls', this._elements.dialog.id);
    options.trigger.setAttribute('aria-expanded', 'false');
    this._elements.dialog.setAttribute('aria-busy', 'false');

    this._retry.addEventListener('click', () => this._options.onRetryProfiles?.());
    this._elements.close.addEventListener('click', () => this.close());
    this._elements.select.addEventListener('change', () => {
      this._showFailure(null);
      this._setStatus('');
      this._syncFields();
    });
    for (const input of this._elements.inputs.values()) {
      input.addEventListener('input', () => input.removeAttribute('aria-invalid'));
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

    this.refresh();
  }

  get isOpen(): boolean {
    return this._elements.dialog.open;
  }

  open(): void {
    this.refresh();
    if (!this._elements.dialog.open) this._elements.dialog.showModal();
    this._options.trigger.setAttribute('aria-expanded', 'true');
    (this._elements.select.disabled ? this._elements.close : this._elements.select).focus();
  }

  close(): void {
    const wasOpen = this._elements.dialog.open;
    this._generation++;
    if (wasOpen) this._elements.dialog.close();
    if (!wasOpen) return;
    this._options.trigger.setAttribute('aria-expanded', 'false');
    const trigger = this._options.trigger;
    const target = trigger.isConnected && !trigger.disabled ? trigger : this._options.fallbackFocus;
    target?.focus();
    this._options.onClose?.();
  }

  /** Repaints from the current profile resource state. Also the modal-host seam. */
  refresh(): void {
    const state = this._options.profiles();
    const ready = state.status === 'ready';
    if (ready && state.value !== this._renderedProfiles) {
      this._renderedProfiles = state.value;
      this._renderOptions(state.value.profiles);
    }
    if (!ready) {
      this._renderedProfiles = null;
      this._elements.select.replaceChildren();
    }

    this._options.trigger.disabled = !ready;
    this._options.trigger.setAttribute('aria-disabled', String(!ready));
    this._retry.hidden = state.status !== 'error';
    this._elements.select.disabled = !ready;
    this._elements.submit.disabled = !ready || this._requestInFlight;
    if (!ready) this._showUnavailable(state);
    this._syncFields();
  }

  /** Retires this generation so a late response cannot repaint a newer surface. */
  invalidate(): void {
    this.close();
  }

  private _renderOptions(profiles: readonly AssetSpawnProfile[]): void {
    const previous = this._elements.select.value;
    const options = profiles.map(profile => {
      const option = document.createElement('option');
      option.value = String(profile.vehicleClass);
      option.textContent = profile.displayName;
      return option;
    });
    this._elements.select.replaceChildren(...options);
    const keep = options.some(option => option.value === previous);
    this._elements.select.value = keep ? previous : (options[0]?.value ?? '');
  }

  private _selectedProfile(): AssetSpawnProfile | null {
    const value = this._elements.select.value;
    const profiles = this._renderedProfiles?.profiles ?? [];
    return profiles.find(profile => String(profile.vehicleClass) === value) ?? null;
  }

  private _syncFields(): void {
    const profile = this._selectedProfile();
    const domain = profile?.domain ?? AssetDomain.Unspecified;
    this._elements.domain.textContent = profile === null
      ? '—'
      : DOMAIN_LABELS.get(domain) ?? 'Unspecified';

    setHidden(this._elements.rows.get('heading')!, !(profile?.headingApplies ?? false));
    // Withheld by discovered domain, never by a hard-coded class list.
    const air = domain === AssetDomain.Air;
    for (const field of AIR_UNSUPPORTED_METADATA) {
      setHidden(this._elements.rows.get(field)!, air);
    }
  }

  private async _submit(): Promise<void> {
    if (this._requestInFlight) return;
    const profile = this._selectedProfile();
    if (profile === null) return;

    const position = this._readPosition();
    if (position === null) {
      this._setStatus('');
      this._showText('Position needs three finite metre values in the local EUS scene frame.');
      return;
    }
    const heading = profile.headingApplies ? this._readNumber('heading') : 0;
    if (heading === null) {
      this._setStatus('');
      this._showText('Heading needs a finite value in degrees clockwise from true north.');
      return;
    }

    const request = this._buildRequest(profile, position, heading);
    const generation = this._generation;
    this._requestInFlight = true;
    this._showFailure(null);
    this._setStatus('Spawning…');
    this._setBusy(true);

    let result: Result<AssetSpawnResponse, ApiFailure>;
    try {
      result = await this._options.spawn(request);
    } catch (error: unknown) {
      result = {
        success: false,
        error: {
          kind: 'network',
          message: error instanceof Error ? error.message : String(error),
        },
      };
    }

    this._requestInFlight = false;
    if (generation !== this._generation) return;
    this._setBusy(false);
    if (!result.success) {
      this._setStatus('');
      this._showFailure(result.error);
      return;
    }
    // A 201 names the asset. It does not place it on the roster — the stream does.
    this._setStatus(`Spawn accepted as ${result.value.assetId} · Awaiting streamed asset state`);
    this._options.onAccepted?.(result.value.assetId);
  }

  private _buildRequest(
    profile: AssetSpawnProfile,
    position: WireVec3,
    headingDegrees: number,
  ): AssetSpawnRequest {
    const request: Record<string, unknown> = {
      vehicleClass: profile.vehicleClass,
      pose: {
        frame: CoordinateFrame.LocalEus,
        originId: null,
        position,
        orientation: profile.headingApplies
          ? headingToEusQuaternion(headingDegrees)
          : { ...OMITTED_ORIENTATION },
      },
      assetId: this._text('assetId'),
      vendor: this._text('vendor'),
    };
    if (profile.domain !== AssetDomain.Air) {
      request['displayName'] = this._text('displayName');
      request['model'] = this._text('model');
      request['agencyId'] = this._text('agencyId');
      request['fleetId'] = this._text('fleetId');
    }
    return request as unknown as AssetSpawnRequest;
  }

  private _readPosition(): WireVec3 | null {
    const x = this._readNumber('positionX');
    const y = this._readNumber('positionY');
    const z = this._readNumber('positionZ');
    if (x === null || y === null || z === null) return null;
    return { x, y, z };
  }

  private _readNumber(name: string): number | null {
    const raw = this._elements.inputs.get(name)?.value.trim() ?? '';
    if (raw === '') return null;
    const value = Number(raw);
    return Number.isFinite(value) ? value : null;
  }

  private _text(name: string): string | null {
    const raw = this._elements.inputs.get(name)?.value.trim() ?? '';
    return raw === '' ? null : raw;
  }

  private _setBusy(busy: boolean): void {
    this._elements.dialog.setAttribute('aria-busy', String(busy));
    this._elements.select.disabled = busy || this._options.profiles().status !== 'ready';
    this._elements.submit.disabled = busy || this._elements.select.disabled;
    for (const input of this._elements.inputs.values()) input.disabled = busy;
  }

  private _setStatus(text: string): void {
    this._elements.status.textContent = text;
    this._elements.status.hidden = text === '';
  }

  private _showUnavailable(state: ResourceState<AssetProfileCatalogResponse>): void {
    if (state.status === 'error') {
      this._showText(`Asset profiles unavailable · ${failureText(state.failure)}`);
      return;
    }
    this._showText('Loading the spawnable asset profiles for this deployment…');
  }

  private _showFailure(failure: ApiFailure | null): void {
    for (const input of this._elements.inputs.values()) input.removeAttribute('aria-invalid');
    if (failure === null) {
      this._showText(null);
      return;
    }
    if (failure.kind !== 'problem') {
      this._showText(failure.message);
      return;
    }
    const problem = failure.problem;
    const lines = [`${problem.reasonCode ?? problem.code} · ${problem.detail}`];
    for (const error of problem.errors) {
      lines.push(`${error.field}: ${error.message}`);
      this._elements.inputs.get(fieldInputName(error.field))?.setAttribute('aria-invalid', 'true');
    }
    this._showText(lines.join('\n'));
  }

  private _showText(text: string | null): void {
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

/** Maps a server problem field onto the input that carries it, where one exists. */
function fieldInputName(field: string): string {
  if (field === 'pose.position' || field === 'pose') return 'positionX';
  return field;
}

function failureText(failure: ApiFailure): string {
  return failure.kind === 'problem'
    ? `${failure.problem.reasonCode ?? failure.problem.code} · ${failure.problem.detail}`
    : failure.message;
}

function setHidden(element: HTMLElement, hidden: boolean): void {
  if (element.hidden !== hidden) element.hidden = hidden;
}

function buildRetry(): HTMLButtonElement {
  const retry = document.createElement('button');
  retry.type = 'button';
  retry.className = 'btn';
  retry.dataset['action'] = 'retry-profiles';
  retry.textContent = 'Retry asset profiles';
  retry.hidden = true;
  return retry;
}

function build(): SpawnElements {
  const dialog = document.createElement('dialog');
  dialog.id = 'operator-spawn-asset';
  dialog.className = 'operator-dialog spawn-asset';
  const title = document.createElement('h2');
  title.id = 'spawn-asset-title';
  title.textContent = 'Spawn asset';
  dialog.setAttribute('aria-labelledby', title.id);

  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'operator-dialog-close';
  close.setAttribute('aria-label', 'Close spawn asset');
  close.textContent = '×';

  const form = document.createElement('form');
  form.className = 'operator-dialog-form';
  form.noValidate = true;

  const grid = document.createElement('div');
  grid.className = 'operator-dialog-grid';

  const rows = new Map<string, HTMLElement>();
  const inputs = new Map<string, HTMLInputElement>();

  const classRow = document.createElement('label');
  classRow.className = 'operator-dialog-field';
  classRow.dataset['field'] = 'vehicleClass';
  classRow.append(caption('Vehicle class'));
  const select = document.createElement('select');
  select.name = 'vehicleClass';
  classRow.appendChild(select);
  rows.set('vehicleClass', classRow);

  const domainRow = document.createElement('p');
  domainRow.className = 'operator-dialog-field';
  domainRow.dataset['field'] = 'domain';
  domainRow.append(caption('Domain (derived)'));
  const domain = document.createElement('output');
  domain.className = 'operator-dialog-readout';
  domain.setAttribute('name', 'domain');
  domain.textContent = '—';
  domainRow.appendChild(domain);
  rows.set('domain', domainRow);

  const positionRow = document.createElement('fieldset');
  positionRow.className = 'operator-dialog-field operator-dialog-span';
  positionRow.dataset['field'] = 'position';
  const legend = document.createElement('legend');
  legend.textContent = 'Position — metres, local EUS';
  positionRow.appendChild(legend);
  const triple = document.createElement('div');
  triple.className = 'operator-dialog-triple';
  for (const [name, label] of [
    ['positionX', 'X east'],
    ['positionY', 'Y up'],
    ['positionZ', 'Z south'],
  ] as const) {
    const axis = document.createElement('label');
    axis.className = 'operator-dialog-field';
    axis.append(caption(label));
    const input = numberInput(name);
    inputs.set(name, input);
    axis.appendChild(input);
    triple.appendChild(axis);
  }
  positionRow.appendChild(triple);
  rows.set('position', positionRow);

  const headingRow = document.createElement('label');
  headingRow.className = 'operator-dialog-field';
  headingRow.dataset['field'] = 'heading';
  headingRow.append(caption('Heading — degrees true'));
  const heading = numberInput('heading');
  heading.min = '0';
  heading.max = '360';
  inputs.set('heading', heading);
  headingRow.appendChild(heading);
  rows.set('heading', headingRow);

  grid.append(classRow, domainRow, positionRow, headingRow);

  for (const [name, label] of [
    ['assetId', 'Asset id — optional'],
    ['vendor', 'Vendor'],
    ['displayName', 'Display name'],
    ['model', 'Model'],
    ['agencyId', 'Agency id'],
    ['fleetId', 'Fleet id'],
  ] as const) {
    const row = document.createElement('label');
    row.className = 'operator-dialog-field';
    row.dataset['field'] = name;
    row.append(caption(label));
    const input = document.createElement('input');
    input.type = 'text';
    input.name = name;
    input.autocomplete = 'off';
    input.maxLength = MAX_METADATA_LENGTH;
    inputs.set(name, input);
    row.appendChild(input);
    rows.set(name, row);
    grid.appendChild(row);
  }

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
  submit.dataset['action'] = 'spawn';
  submit.textContent = 'Spawn';
  actions.appendChild(submit);

  form.append(grid, error, status, actions);
  dialog.append(title, close, form);
  return { dialog, close, form, select, domain, error, status, submit, rows, inputs };
}

function caption(text: string): HTMLElement {
  const span = document.createElement('span');
  span.textContent = text;
  return span;
}

function numberInput(name: string): HTMLInputElement {
  const input = document.createElement('input');
  input.type = 'number';
  input.name = name;
  input.inputMode = 'decimal';
  input.autocomplete = 'off';
  input.value = '0';
  return input;
}
