// ResQ Viz - the injected command-link fault, and the way back from it
// SPDX-License-Identifier: Apache-2.0
//
// This is the one control in the console that can make an asset unreachable.
// Three things follow from that and none of them are optional:
//
//   * it is named as what it is — a **simulated fault injected for a drill**,
//     never a modelled radio and never a claim about real equipment;
//   * the restore lives on the same panel, is offered whenever an asset is
//     selected, and is never withheld by a safety gate. A lever that can silence
//     an asset and cannot be reached to un-silence it is worse than no lever;
//   * the deployment's own gate is mirrored here rather than discovered by a
//     403: a build reporting a live control path refuses the cut, so the cut is
//     disabled with that reason and the restore is not.
//
// The panel changes no indicator on its own. A POST is a request; the asset's
// published state is the fact, so after either direction it says
// `Request accepted. Awaiting published asset state` until the stream agrees.
// Every response is matched to the asset and the selection generation it was
// asked for, so an answer that lands after the operator moved on is dropped.

import { actionButton, panelCard, readout, setDisabled, setHidden, setText } from './panelDom';

/** The reason recorded on the session's trail for a drill cut. */
export const LINK_CUT_REASON = 'Loss-of-link drill';
/** The reason recorded when the drill is ended. */
export const LINK_RESTORE_REASON = 'Restore after drill';

/** What the panel is told on every frame and every state change. */
export interface LinkPanelState {
  readonly selectedId: string | null;
  readonly selectionGeneration: number;
  /** Published link state from the asset stream, or null when unknown. */
  readonly streamedConnected: boolean | null;
  readonly mutationsEnabled: boolean;
  /** False when this deployment reports a live control path: the server refuses
   *  a cut there, so the console must not offer one. */
  readonly cutPermitted: boolean;
  readonly blockedReason: string | null;
}

export interface LinkPanelOptions {
  readonly mount: HTMLElement;
  readonly onCut: () => void;
  readonly onRestore: () => void;
}

/** A change the server accepted, and the published state it is waiting for. */
interface PendingLinkChange {
  readonly assetId: string;
  readonly generation: number;
  readonly target: boolean;
}

const AWAITING = 'Request accepted. Awaiting published asset state';

/** Published link state for the selected asset, plus the drill lever both ways. */
export class LinkDrillPanel {
  private readonly _asset: HTMLElement;
  private readonly _link: HTMLElement;
  private readonly _status: HTMLElement;
  private readonly _cut: HTMLButtonElement;
  private readonly _restore: HTMLButtonElement;
  private readonly _confirm: HTMLElement;
  private readonly _cutConfirm: HTMLButtonElement;
  private readonly _cutCancel: HTMLButtonElement;

  private _state: LinkPanelState = {
    selectedId: null,
    selectionGeneration: 0,
    streamedConnected: null,
    mutationsEnabled: true,
    cutPermitted: true,
    blockedReason: null,
  };
  /** Link state the server reported when the asset was selected, until the
   *  stream supersedes it. Held only for the asset and generation it was read
   *  for. */
  private _fetched: { readonly assetId: string; readonly generation: number; readonly available: boolean } | null = null;
  private _awaiting: PendingLinkChange | null = null;
  private _busy = false;
  private _confirming = false;
  private _status_ = '';
  private _isError = false;

  constructor(options: LinkPanelOptions) {
    const card = panelCard(
      options.mount,
      'link',
      'Link drill',
      'Injects a simulated command-link fault so the asset stops hearing commands '
      + 'and acts on its own declared link-loss behaviour. Simulation only; '
      + 'reversible from this panel at any time.',
    );

    const list = document.createElement('dl');
    list.className = 'advanced-readout';
    const asset = readout('link-asset', 'Asset');
    const link = readout('link-state', 'Command link');
    list.append(asset.row, link.row);
    this._asset = asset.value;
    this._link = link.value;
    this._status = card.status;

    this._cut = actionButton('cut', 'Cut command link…');
    this._cut.classList.add('btn-danger');
    this._restore = actionButton('restore', 'Restore command link');
    const actions = document.createElement('div');
    actions.className = 'advanced-actions';
    actions.append(this._cut, this._restore);

    this._cutConfirm = actionButton('cut-confirm', 'Cut link now');
    this._cutConfirm.classList.add('btn-danger');
    this._cutCancel = actionButton('cut-cancel', 'Cancel');
    this._confirm = document.createElement('div');
    this._confirm.className = 'advanced-confirm';
    this._confirm.hidden = true;
    const warning = document.createElement('p');
    warning.className = 'advanced-warning';
    warning.textContent =
      `Recorded on the session’s audit trail as “${LINK_CUT_REASON}”. The asset `
      + 'will refuse every command, including stop, until the link is restored.';
    this._confirm.append(warning, this._cutConfirm, this._cutCancel);

    card.body.append(list, actions, this._confirm);

    this._cut.addEventListener('click', () => {
      this._confirming = true;
      this._render();
    });
    this._cutCancel.addEventListener('click', () => {
      this._confirming = false;
      this._render();
    });
    this._cutConfirm.addEventListener('click', () => {
      this._confirming = false;
      this._render();
      options.onCut();
    });
    this._restore.addEventListener('click', () => options.onRestore());
    this._render();
  }

  render(state: LinkPanelState): void {
    const moved = state.selectedId !== this._state.selectedId
      || state.selectionGeneration !== this._state.selectionGeneration;
    this._state = state;
    if (moved) {
      // Everything held here describes the previous asset. None of it survives.
      this._confirming = false;
      this._awaiting = null;
      this._fetched = null;
      this._status_ = '';
      this._isError = false;
    }
    this._settleAwait();
    this._render();
  }

  /** Accepts a link read, but only for the asset and generation it was asked
   *  for. A late answer about an asset the operator has left is discarded. */
  applyLinkRead(assetId: string, generation: number, available: boolean): void {
    if (assetId !== this._state.selectedId || generation !== this._state.selectionGeneration) {
      return;
    }
    this._fetched = { assetId, generation, available };
    this._render();
  }

  /** Records that a change was accepted, and starts waiting for the stream. */
  awaitPublished(assetId: string, generation: number, target: boolean): void {
    if (assetId !== this._state.selectedId || generation !== this._state.selectionGeneration) {
      return;
    }
    this._awaiting = { assetId, generation, target };
    // A request is not a fact: the read the panel was seeded with no longer
    // describes what is about to be true, and the stream is the only thing that
    // may say so.
    this._fetched = null;
    this._status_ = '';
    this._isError = false;
    this._render();
  }

  setBusy(busy: boolean): void {
    this._busy = busy;
    this._render();
  }

  setStatus(message: string | null, isError = false): void {
    this._status_ = message ?? '';
    this._isError = isError;
    this._render();
  }

  private _settleAwait(): void {
    const awaiting = this._awaiting;
    if (awaiting === null) return;
    if (awaiting.assetId !== this._state.selectedId
      || awaiting.generation !== this._state.selectionGeneration) {
      this._awaiting = null;
      return;
    }
    if (this._state.streamedConnected === awaiting.target) this._awaiting = null;
  }

  private _connected(): boolean | null {
    if (this._state.selectedId === null) return null;
    if (this._state.streamedConnected !== null) return this._state.streamedConnected;
    const fetched = this._fetched;
    return fetched !== null
      && fetched.assetId === this._state.selectedId
      && fetched.generation === this._state.selectionGeneration
      ? fetched.available
      : null;
  }

  private _render(): void {
    const { selectedId, mutationsEnabled, cutPermitted } = this._state;
    setText(this._asset, selectedId ?? 'Select an asset');

    const connected = this._connected();
    setText(this._link, this._awaiting !== null
      ? AWAITING
      : selectedId === null
        ? '—'
        : connected === null
          ? 'Unknown'
          : connected ? 'Up' : 'Held down (drill)');

    const selected = selectedId !== null;
    setDisabled(this._cut, !selected || !mutationsEnabled || this._busy
      || !cutPermitted || connected === false);
    // Never gated on anything but the mode, the selection and an outstanding
    // request. Recovery paths do not get safety gates.
    setDisabled(this._restore, !selected || !mutationsEnabled || this._busy);
    setHidden(this._confirm, !this._confirming);
    setDisabled(this._cutConfirm, !selected || !mutationsEnabled || !cutPermitted);

    const message = this._status_ !== ''
      ? this._status_
      : !mutationsEnabled && this._state.blockedReason !== null
        ? this._state.blockedReason
        : selected && !cutPermitted
          ? 'This deployment reports a live control path, so a link may not be cut '
            + 'through it. Restoring a link is always permitted.'
          : '';
    setHidden(this._status, message === '');
    setText(this._status, message);
    this._status.setAttribute('role', this._isError ? 'alert' : 'status');
    this._status.classList.toggle('is-error', this._isError && this._status_ !== '');
  }
}
