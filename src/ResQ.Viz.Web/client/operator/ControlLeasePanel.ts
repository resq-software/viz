// ResQ Viz - who holds the selected asset, and this console's lease over it
// SPDX-License-Identifier: Apache-2.0
//
// A view over `ControlAuthorityStore`, and deliberately nothing more: it keeps
// no copy of the selected asset's authority, because a second copy is a second
// answer waiting to disagree with the one the command path consults.
//
// Four mutations, and they are not equivalent. Acquire, renew and release are
// this console acting on its own claim. **Preemption ends somebody else's**, on
// emergency authority, and the server refuses it without a justification — so
// this panel refuses to offer it without one either, and asks for an explicit
// confirmation on top. Advertised must equal accepted: a button that posts a
// request the server would reject teaches an operator to distrust the console.
//
// Nothing here implies an authenticated person or a certified chain of custody.
// A holder id is a browser tab in a simulation; this console's own id is shown
// as `This console` and every other one verbatim.

import type { ResourceState } from './ConsoleResources';
import type { AuthorityState } from './controlAuthorityStore';
import type { ControlModeStatus } from './types';
import { actionButton, panelCard, readout, setDisabled, setHidden, setText, textField } from './panelDom';

/** Everything the panel renders, recomputed by the workspace on every change. */
export interface LeasePanelState {
  readonly selectedId: string | null;
  readonly authority: AuthorityState;
  readonly mode: ResourceState<ControlModeStatus>;
  /** How a holder id reads to an operator, from the store that owns the answer. */
  readonly describeHolder: (holderId: string) => string;
  /** False away from the live edge. The reason is rendered beside the controls. */
  readonly mutationsEnabled: boolean;
  readonly blockedReason: string | null;
}

export interface LeasePanelOptions {
  readonly mount: HTMLElement;
  readonly onAcquire: () => void;
  readonly onRenew: () => void;
  readonly onRelease: () => void;
  readonly onPreempt: (justification: string) => void;
}

/** Control authority for the selected asset: mode, holder, expiry, and the four
 *  lease operations the server publishes. */
export class ControlLeasePanel {
  private readonly _mode: HTMLElement;
  private readonly _holder: HTMLElement;
  private readonly _grant: HTMLElement;
  private readonly _status: HTMLElement;
  private readonly _justification: HTMLInputElement;
  private readonly _confirm: HTMLElement;
  private readonly _acquire: HTMLButtonElement;
  private readonly _renew: HTMLButtonElement;
  private readonly _release: HTMLButtonElement;
  private readonly _preempt: HTMLButtonElement;
  private readonly _preemptConfirm: HTMLButtonElement;
  private readonly _preemptCancel: HTMLButtonElement;

  private _state: LeasePanelState = {
    selectedId: null,
    authority: { status: 'idle' },
    mode: { status: 'idle' },
    describeHolder: (id) => id,
    mutationsEnabled: true,
    blockedReason: null,
  };
  private _busy = false;
  private _confirming = false;
  /** The last outcome this panel reported, or '' for none. Held separately from
   *  the blocked reason so `_render` can decide between them every time rather
   *  than leaving whichever was written last on screen. */
  private _message = '';
  private _isError = false;

  constructor(options: LeasePanelOptions) {
    const card = panelCard(
      options.mount,
      'lease',
      'Control authority',
      'A lease says which console may command this asset. It confers no real-world '
      + 'authority and identifies no person.',
    );

    const list = document.createElement('dl');
    list.className = 'advanced-readout';
    const mode = readout('lease-mode', 'Control mode');
    const holder = readout('lease-holder', 'Current holder');
    const grant = readout('lease-grant', 'Last grant');
    list.append(mode.row, holder.row, grant.row);
    this._mode = mode.value;
    this._holder = holder.value;
    this._grant = grant.value;
    this._status = card.status;

    const justification = textField(
      'justification', 'Justification (required to preempt)',
    );
    this._justification = justification.input;
    this._justification.addEventListener('input', () => this._render());

    this._acquire = actionButton('acquire', 'Take control');
    this._renew = actionButton('renew', 'Extend');
    this._release = actionButton('release', 'Hand back');
    this._preempt = actionButton('preempt', 'Preempt holder…');
    this._preempt.classList.add('btn-danger');

    const actions = document.createElement('div');
    actions.className = 'advanced-actions';
    actions.append(this._acquire, this._renew, this._release, this._preempt);

    this._preemptConfirm = actionButton('preempt-confirm', 'Preempt now');
    this._preemptConfirm.classList.add('btn-danger');
    this._preemptCancel = actionButton('preempt-cancel', 'Cancel');
    this._confirm = document.createElement('div');
    this._confirm.className = 'advanced-confirm';
    this._confirm.hidden = true;
    const warning = document.createElement('p');
    warning.className = 'advanced-warning';
    warning.textContent =
      'This ends another console’s lease and records the justification on the '
      + 'session’s audit trail.';
    this._confirm.append(warning, this._preemptConfirm, this._preemptCancel);

    card.body.append(list, justification.wrapper, actions, this._confirm);

    this._acquire.addEventListener('click', () => options.onAcquire());
    this._renew.addEventListener('click', () => options.onRenew());
    this._release.addEventListener('click', () => options.onRelease());
    this._preempt.addEventListener('click', () => {
      this._confirming = true;
      this._render();
    });
    this._preemptCancel.addEventListener('click', () => {
      this._confirming = false;
      this._render();
    });
    this._preemptConfirm.addEventListener('click', () => {
      this._confirming = false;
      this._render();
      options.onPreempt(this._justification.value.trim());
    });
    this._render();
  }

  render(state: LeasePanelState): void {
    if (state.selectedId !== this._state.selectedId) {
      // A grant and a confirmation both belong to the asset they were taken
      // against. Carrying either across a selection change would attach one
      // asset's authority to another's name.
      this._confirming = false;
      setText(this._grant, '—');
      this._message = '';
      this._isError = false;
    }
    this._state = state;
    this._render();
  }

  /** Blocks the controls while a lease POST is outstanding. Always paired with
   *  a `setBusy(false)`, including on the refusal path — a surface left busy by
   *  a failure is a surface the operator can never use again. */
  setBusy(busy: boolean): void {
    this._busy = busy;
    this._render();
  }

  /** Reports the outcome of the last lease operation, or clears it. */
  setStatus(message: string | null, isError = false): void {
    this._message = message ?? '';
    this._isError = isError;
    this._render();
  }

  /** States what policy actually granted, never what was requested: a console
   *  that believed its own request stops renewing exactly when the lease lapsed. */
  setGrant(requestedSeconds: number, grantedSeconds: number, clamped: boolean): void {
    setText(this._grant, clamped
      ? `${grantedSeconds}s granted (${requestedSeconds}s requested — clamped by policy)`
      : `${grantedSeconds}s granted`);
  }

  private _render(): void {
    const { authority, mode, selectedId } = this._state;
    setText(this._mode, describeMode(mode));
    setText(this._holder, this._describeAuthority());

    const live = this._state.mutationsEnabled && !this._busy && selectedId !== null;
    const heldByConsole = authority.status === 'heldByConsole'
      && authority.assetId === selectedId;
    const heldByOther = authority.status === 'heldByOther' && authority.assetId === selectedId;
    const uncontrolled = authority.status === 'uncontrolled' && authority.assetId === selectedId;
    const justified = this._justification.value.trim().length > 0;

    setDisabled(this._acquire, !live || !uncontrolled);
    setDisabled(this._renew, !live || !heldByConsole);
    setDisabled(this._release, !live || !heldByConsole);
    setDisabled(this._preempt, !live || !heldByOther || !justified || this._confirming);
    setHidden(this._confirm, !this._confirming);
    setDisabled(this._preemptConfirm, !live || !heldByOther || !justified);
    this._justification.disabled = !this._state.mutationsEnabled;

    // Derived on every render, never written once and left: a reason that
    // outlives the refusal it explains sits beside a control the operator can
    // now use and tells them they cannot.
    const message = this._message !== ''
      ? this._message
      : this._busy ? '' : this._state.blockedReason ?? '';
    setHidden(this._status, message === '');
    setText(this._status, message);
    const isError = this._isError && this._message !== '';
    this._status.setAttribute('role', isError ? 'alert' : 'status');
    this._status.classList.toggle('is-error', isError);
  }

  private _describeAuthority(): string {
    const { authority, selectedId, describeHolder } = this._state;
    if (selectedId === null) return 'Select an asset';
    switch (authority.status) {
      case 'idle':
        return 'Not read yet';
      case 'loading':
        return 'Checking who holds control…';
      case 'uncontrolled':
        return authority.assetId === selectedId ? 'Uncontrolled' : 'Checking…';
      case 'heldByConsole':
      case 'heldByOther':
        return authority.assetId === selectedId
          ? `${describeHolder(authority.lease.holderId)} until ${expiry(authority.lease.expiresAt)}`
          : 'Checking…';
      case 'error':
        return authority.assetId === selectedId
          ? `Unavailable (${authority.failure.kind === 'problem'
            ? authority.failure.problem.code : authority.failure.message})`
          : 'Checking…';
    }
  }
}

function describeMode(mode: ResourceState<ControlModeStatus>): string {
  if (mode.status === 'ready') return `${mode.value.mode} — ${mode.value.detail}`;
  if (mode.status === 'error') {
    return `Unavailable (${mode.failure.kind === 'problem'
      ? mode.failure.problem.code : mode.failure.message})`;
  }
  return mode.status === 'loading' ? 'Reading…' : 'Not read yet';
}

/** A wall-clock time of day. A lease lasts minutes; the date would be noise. */
function expiry(expiresAt: string): string {
  const at = new Date(expiresAt);
  return Number.isNaN(at.getTime()) ? 'an unknown time' : at.toLocaleTimeString();
}
