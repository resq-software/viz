// ResQ Viz - the lazy Advanced/Safety workspace
// SPDX-License-Identifier: Apache-2.0
//
// Four panels behind one disclosure, loaded the first time an operator opens it:
// control authority, the link drill, simulation-only external reports, and the
// session's authority trail. Everything on this surface is safety-relevant and
// none of it is regulated equipment — it drives a simulation, and every panel
// says so in its own words rather than relying on a banner somewhere else.
//
// The workspace owns three things and no more:
//
//   * **the boundary.** Every mutation goes through the live/replay gate here,
//     immediately before its request, and the panels' `disabled` attributes
//     mirror that decision rather than being it. A refusal is rendered where the
//     control is, and it always releases whatever busy state it claimed —
//     a surface that cannot recover from its own refusal is a surface that is
//     gone for the rest of the session.
//   * **staleness.** Every asset-scoped response is matched against the asset
//     and the selection generation it was asked for. This holds no second copy
//     of the selection and no second copy of the authority state: it is handed
//     both, and a panel that kept its own would eventually answer differently
//     from the command path.
//   * **composition.** The panels themselves are dumb views; the fetching, the
//     gate and the store live here.
//
// Its stylesheet is imported here so it rides this chunk. The entry sheet has
// no room for four panels, and a section most sessions never open should not
// cost them anything.

import '../styles/advancedSafety.css';

import type { ApiFailure, Result } from '../api';
import type { AssetState } from '../assets/types';
import { getLogger } from '../log';
import type { ResourceState } from './ConsoleResources';
import type { AuthorityState } from './controlAuthorityStore';
import type { InteractionMode } from './interactionMode';
import { failureCode, failureText } from './panelDom';
import { ControlLeasePanel } from './ControlLeasePanel';
import { LinkDrillPanel, LINK_CUT_REASON, LINK_RESTORE_REASON } from './LinkDrillPanel';
import { TrackReportPanel } from './TrackReportPanel';
import { AuditPanel } from './AuditPanel';
import {
  ControlRole,
  type AssetLinkResponse,
  type CommandAuditResponse,
  type ControlHolderResponse,
  type ControlLeaseResponse,
  type ControlModeStatus,
  type TrackReportRequest,
  type TrackReportResponse,
} from './types';

export { LINK_CUT_REASON, LINK_RESTORE_REASON } from './LinkDrillPanel';
export { TRACK_CONFIDENCE, TRACK_SOURCE_ID } from './TrackReportPanel';


const log = getLogger('advanced-safety');

/**
 * Lease length this console asks for, in seconds.
 *
 * The server clamps it to its own maximum and reports both numbers back, and
 * the panel states what was *granted*. Asking for more than policy allows is
 * not a mistake — it is how a console finds out what policy is — but believing
 * the request would leave it renewing a lease that lapsed minutes ago.
 */
export const LEASE_DURATION_SECONDS = 300;

/** What every v2 render tells the workspace. The selection arrives here rather
 *  than being subscribed to separately, so nothing on this surface holds a
 *  second copy of "what is selected". */
export interface AdvancedFrameInput {
  readonly selectedId: string | null;
  readonly selectionGeneration: number;
  readonly selectedState: AssetState | null;
  readonly simulationTimeSeconds: number;
}

/** The wire calls this workspace makes. Injected so every one of them can be
 *  driven without a server, and so the module carries no route knowledge. */
export interface AdvancedSafetyApi {
  acquire(assetId: string, request: {
    readonly holderId: string;
    readonly role: ControlRole;
    readonly durationSeconds: number;
  }): Promise<Result<ControlLeaseResponse, ApiFailure>>;
  renew(assetId: string, request: {
    readonly holderId: string;
    readonly leaseId: string;
    readonly durationSeconds: number;
  }): Promise<Result<ControlLeaseResponse, ApiFailure>>;
  release(assetId: string, request: {
    readonly holderId: string;
    readonly leaseId: string;
  }): Promise<Result<ControlHolderResponse, ApiFailure>>;
  preempt(assetId: string, request: {
    readonly holderId: string;
    readonly role: ControlRole;
    readonly justification: string;
    readonly durationSeconds: number;
  }): Promise<Result<ControlLeaseResponse, ApiFailure>>;
  getLink(assetId: string): Promise<Result<AssetLinkResponse, ApiFailure>>;
  setLink(assetId: string, request: {
    readonly available: boolean;
    readonly issuerId: string;
    readonly reason: string;
  }): Promise<Result<AssetLinkResponse, ApiFailure>>;
  reportTrack(request: TrackReportRequest): Promise<Result<TrackReportResponse, ApiFailure>>;
  getAudit(): Promise<Result<CommandAuditResponse, ApiFailure>>;
}

/**
 * The authority facts this workspace reads and writes back into.
 *
 * Declared structurally rather than as `ControlAuthorityStore` so the workspace
 * depends on the questions and not on the timers, and so its generic timer
 * parameter does not leak through four panels.
 */
export interface LeaseAuthority {
  readonly holderId: string;
  readonly mode: ResourceState<ControlModeStatus>;
  readonly state: AuthorityState;
  subscribe(listener: (state: AuthorityState) => void): () => void;
  describeHolder(holderId: string): string;
  applyLeaseResponse(response: ControlLeaseResponse): void;
  applyHolderResponse(response: ControlHolderResponse): void;
  invalidateFromFailure(code: string): boolean;
}

export interface AdvancedSafetyOptions {
  readonly mount: HTMLElement;
  readonly authority: LeaseAuthority;
  readonly interaction: InteractionMode;
  readonly api: AdvancedSafetyApi;
}

/** Why a control is refused while the console is off the live edge. */
const REPLAY_REASON =
  'Replay — this console is not at the live edge, so nothing here may change the '
  + 'simulation. Return to Live to command.';

/** The four Advanced/Safety panels, their gate, and their staleness guards. */
export class AdvancedSafetyWorkspace {
  private readonly _options: AdvancedSafetyOptions;
  private readonly _lease: ControlLeasePanel;
  private readonly _link: LinkDrillPanel;
  private readonly _track: TrackReportPanel;
  private readonly _audit: AuditPanel;
  private readonly _unsubscribe: readonly (() => void)[];

  private _frame: AdvancedFrameInput = {
    selectedId: null,
    selectionGeneration: 0,
    selectedState: null,
    simulationTimeSeconds: 0,
  };
  /** The (asset, generation) the link read was last issued for, so the same
   *  selection is not re-read on every 10 Hz frame. */
  private _linkRead: string | null = null;
  private _leaseBusy = false;
  private _linkBusy = false;
  private _trackBusy = false;
  private _auditBusy = false;
  private _disposed = false;

  constructor(options: AdvancedSafetyOptions) {
    this._options = options;
    this._lease = new ControlLeasePanel({
      mount: options.mount,
      onAcquire: () => { void this._acquire(); },
      onRenew: () => { void this._renew(); },
      onRelease: () => { void this._release(); },
      onPreempt: (justification) => { void this._preempt(justification); },
    });
    this._link = new LinkDrillPanel({
      mount: options.mount,
      onCut: () => { void this._setLink(false, LINK_CUT_REASON); },
      onRestore: () => { void this._setLink(true, LINK_RESTORE_REASON); },
    });
    this._track = new TrackReportPanel({
      mount: options.mount,
      onReport: (request) => { void this._report(request); },
    });
    this._audit = new AuditPanel({
      mount: options.mount,
      onLoad: () => { void this._loadAudit(); },
    });

    this._unsubscribe = [
      options.authority.subscribe(() => this._render()),
      options.interaction.subscribe(() => this._render()),
    ];
    this._render();
  }

  /** Called from every v2 render, and immediately on a selection change so a
   *  stale asset's link state is never on screen for a frame interval. */
  updateFrame(input: AdvancedFrameInput): void {
    if (this._disposed) return;
    const moved = input.selectedId !== this._frame.selectedId
      || input.selectionGeneration !== this._frame.selectionGeneration;
    this._frame = input;
    this._render();
    if (moved) this._readLink();
  }

  /** Drops subscriptions. The panels' DOM is owned by the mount. */
  dispose(): void {
    if (this._disposed) return;
    this._disposed = true;
    for (const off of this._unsubscribe) off();
  }

  // ── Rendering ─────────────────────────────────────────────────────────────

  private _render(): void {
    const { authority, interaction } = this._options;
    const live = !interaction.isReplay;
    const blockedReason = live ? null : REPLAY_REASON;
    const mode = authority.mode;

    this._lease.render({
      selectedId: this._frame.selectedId,
      authority: authority.state,
      mode,
      describeHolder: (id) => authority.describeHolder(id),
      mutationsEnabled: live,
      blockedReason,
    });
    this._link.render({
      selectedId: this._frame.selectedId,
      selectionGeneration: this._frame.selectionGeneration,
      streamedConnected: this._streamedLink(),
      mutationsEnabled: live,
      // Mirrors the server's own gate rather than waiting for its 403: a build
      // reporting a live control path refuses a cut and never a restore.
      cutPermitted: mode.status !== 'ready' || !mode.value.liveControlAvailable,
      blockedReason,
    });
    this._track.render({
      simulationTimeSeconds: this._frame.simulationTimeSeconds,
      mutationsEnabled: live,
      blockedReason,
    });
  }

  /** Published link state for the *selected* asset only. A state carried for
   *  anything else is somebody else's fact. */
  private _streamedLink(): boolean | null {
    const state = this._frame.selectedState;
    if (state === null || this._frame.selectedId === null) return null;
    return state.assetId === this._frame.selectedId ? state.link.isConnected : null;
  }

  // ── Requests ──────────────────────────────────────────────────────────────

  private _readLink(): void {
    const assetId = this._frame.selectedId;
    const generation = this._frame.selectionGeneration;
    const key = `${generation}:${assetId ?? ''}`;
    if (assetId === null || this._linkRead === key) return;
    this._linkRead = key;
    void this._options.api.getLink(assetId)
      .then((result) => {
        if (this._disposed) return;
        if (result.success) this._link.applyLinkRead(assetId, generation, result.value.isAvailable);
        // A failed read is not reported as a fault: the stream carries the same
        // fact a beat later, and an error line for a value that is about to
        // arrive anyway is noise on a safety surface.
      })
      .catch((error: unknown) => {
        log.info('link read failed; the asset stream still carries link state', { error });
      });
  }

  /**
   * Whether an answer is still about the asset that was asked about.
   *
   * Every asset-scoped request captures the id and the selection generation it
   * was issued for. A refusal or a grant that lands after the operator has moved
   * on is dropped rather than painted onto whatever is selected now — the
   * *release* of the busy state that goes with it is never dropped, because a
   * surface left busy by a response nobody wanted is a surface gone for good.
   */
  private _current(assetId: string, generation: number): boolean {
    return !this._disposed
      && this._frame.selectedId === assetId
      && this._frame.selectionGeneration === generation;
  }

  /** The gate, asked immediately before a request and never cached. Returns the
   *  selected asset id when the action may proceed, and renders the refusal on
   *  the given panel when it may not. */
  private _permit(
    action: string,
    panel: { setStatus(message: string | null, isError?: boolean): void },
  ): boolean {
    const allowed = this._options.interaction.guard(action);
    if (allowed.success) return true;
    panel.setStatus(`${allowed.error.code} · ${REPLAY_REASON}`, true);
    return false;
  }

  private async _acquire(): Promise<void> {
    const assetId = this._frame.selectedId;
    const generation = this._frame.selectionGeneration;
    if (assetId === null || this._leaseBusy) return;
    if (!this._permit('control.acquire', this._lease)) return;
    await this._runLease(
      assetId, generation,
      () => this._options.api.acquire(assetId, {
        holderId: this._options.authority.holderId,
        role: ControlRole.Operator,
        durationSeconds: LEASE_DURATION_SECONDS,
      }),
      (value) => this._applyGrant(value, 'Control taken.'),
    );
  }

  private async _renew(): Promise<void> {
    const assetId = this._frame.selectedId;
    const generation = this._frame.selectionGeneration;
    const leaseId = this._heldLeaseId();
    if (assetId === null || leaseId === null || this._leaseBusy) return;
    if (!this._permit('control.renew', this._lease)) return;
    await this._runLease(
      assetId, generation,
      () => this._options.api.renew(assetId, {
        holderId: this._options.authority.holderId,
        leaseId,
        durationSeconds: LEASE_DURATION_SECONDS,
      }),
      (value) => this._applyGrant(value, 'Lease extended.'),
    );
  }

  private async _release(): Promise<void> {
    const assetId = this._frame.selectedId;
    const generation = this._frame.selectionGeneration;
    const leaseId = this._heldLeaseId();
    if (assetId === null || leaseId === null || this._leaseBusy) return;
    if (!this._permit('control.release', this._lease)) return;
    await this._runLease(
      assetId, generation,
      () => this._options.api.release(assetId, {
        holderId: this._options.authority.holderId,
        leaseId,
      }),
      (value) => {
        this._options.authority.applyHolderResponse(value);
        this._lease.setStatus('Control handed back.');
      },
    );
  }

  private async _preempt(justification: string): Promise<void> {
    const assetId = this._frame.selectedId;
    const generation = this._frame.selectionGeneration;
    if (assetId === null || justification === '' || this._leaseBusy) return;
    if (!this._permit('control.preempt', this._lease)) return;
    await this._runLease(
      assetId, generation,
      () => this._options.api.preempt(assetId, {
        holderId: this._options.authority.holderId,
        role: ControlRole.Emergency,
        justification,
        durationSeconds: LEASE_DURATION_SECONDS,
      }),
      (value) => this._applyGrant(value, 'Control preempted, and recorded on the trail.'),
    );
  }

  /**
   * One lifecycle for all four lease mutations.
   *
   * The busy state is released *before* the outcome is rendered and on every
   * path, refusal and thrown error included. A generation check may discard a
   * response; it must never discard the re-enable that goes with it.
   */
  private async _runLease<T>(
    assetId: string,
    generation: number,
    send: () => Promise<Result<T, ApiFailure>>,
    apply: (value: T) => void,
  ): Promise<void> {
    this._leaseBusy = true;
    this._lease.setBusy(true);
    this._lease.setStatus(null);
    let result: Result<T, ApiFailure>;
    try {
      result = await send();
    } catch (error: unknown) {
      result = {
        success: false,
        error: { kind: 'network', message: error instanceof Error ? error.message : String(error) },
      };
    }
    this._leaseBusy = false;
    this._lease.setBusy(false);
    if (!this._current(assetId, generation)) return;
    if (result.success) {
      apply(result.value);
      return;
    }
    // `control.*` means this console's picture of who holds the asset is stale;
    // the store re-reads the holder rather than leaving a refused operation
    // looking like a transient error.
    this._options.authority.invalidateFromFailure(failureCode(result.error));
    this._lease.setStatus(failureText(result.error), true);
  }

  private _applyGrant(response: ControlLeaseResponse, message: string): void {
    this._options.authority.applyLeaseResponse(response);
    this._lease.setGrant(
      response.requestedDurationSeconds,
      response.grantedDurationSeconds,
      response.durationClamped,
    );
    this._lease.setStatus(message);
  }

  private _heldLeaseId(): string | null {
    const state = this._options.authority.state;
    return state.status === 'heldByConsole' && state.assetId === this._frame.selectedId
      ? state.lease.leaseId
      : null;
  }

  private async _setLink(available: boolean, reason: string): Promise<void> {
    const assetId = this._frame.selectedId;
    const generation = this._frame.selectionGeneration;
    if (assetId === null || this._linkBusy) return;
    if (!this._permit(available ? 'link.restore' : 'link.cut', this._link)) return;
    this._linkBusy = true;
    this._link.setBusy(true);
    this._link.setStatus(null);
    let result: Result<AssetLinkResponse, ApiFailure>;
    try {
      result = await this._options.api.setLink(assetId, {
        available,
        issuerId: this._options.authority.holderId,
        reason,
      });
    } catch (error: unknown) {
      result = {
        success: false,
        error: { kind: 'network', message: error instanceof Error ? error.message : String(error) },
      };
    }
    this._linkBusy = false;
    this._link.setBusy(false);
    if (!this._current(assetId, generation)) return;
    if (result.success) {
      // The POST is a request. The asset's published state is the fact, so the
      // indicator does not move until the stream says it has.
      this._link.awaitPublished(assetId, generation, available);
      return;
    }
    this._link.setStatus(failureText(result.error), true);
  }

  private async _report(request: TrackReportRequest): Promise<void> {
    if (this._trackBusy) return;
    if (!this._permit('track.report', this._track)) return;
    this._trackBusy = true;
    this._track.setBusy(true);
    this._track.setStatus(null);
    let result: Result<TrackReportResponse, ApiFailure>;
    try {
      result = await this._options.api.reportTrack(request);
    } catch (error: unknown) {
      result = {
        success: false,
        error: { kind: 'network', message: error instanceof Error ? error.message : String(error) },
      };
    }
    this._trackBusy = false;
    this._track.setBusy(false);
    this._track.setStatus(
      result.success
        ? `Report accepted for ${request.trackId}. The contact appears once the session `
          + 'publishes it.'
        : failureText(result.error),
      !result.success,
    );
  }

  /** Read-only, and deliberately not gated: reading what already happened is
   *  not a mutation, and a recording is when the trail matters most. */
  private async _loadAudit(): Promise<void> {
    if (this._auditBusy) return;
    this._auditBusy = true;
    this._audit.setBusy(true);
    this._audit.setStatus(null);
    let result: Result<CommandAuditResponse, ApiFailure>;
    try {
      result = await this._options.api.getAudit();
    } catch (error: unknown) {
      result = {
        success: false,
        error: { kind: 'network', message: error instanceof Error ? error.message : String(error) },
      };
    }
    this._auditBusy = false;
    this._audit.setBusy(false);
    if (result.success) this._audit.render(result.value);
    else this._audit.setStatus(failureText(result.error), true);
  }
}

/** Everything `app.ts` needs to build the workspace without importing four panel
 *  modules to do it. Called from the shell's one-shot first-expansion callback;
 *  a chunk that never arrived is re-requested through `retryAdvancedSafety`,
 *  so a failed fetch does not cost the session the whole section. */
export function mountAdvancedSafety(options: AdvancedSafetyOptions): AdvancedSafetyWorkspace {
  return new AdvancedSafetyWorkspace(options);
}
