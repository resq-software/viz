// ResQ Viz - authoritative streamed scenario runtime
// SPDX-License-Identifier: Apache-2.0

import type { ScenarioSessionState } from '../assets/types';

export type ScenarioInteractionMode = 'live' | 'replay';
export type MissionBaseKind = 'unknown' | 'none' | 'custom' | 'active';

export type MissionView =
  | { readonly kind: 'unknown'; readonly pendingName: null }
  | { readonly kind: 'none'; readonly pendingName: null }
  | { readonly kind: 'custom'; readonly pendingName: null }
  | {
      readonly kind: 'active';
      readonly name: string;
      readonly startedAtSimulationSeconds: number;
      readonly revision: number;
      readonly pendingName: null;
    }
  | {
      readonly kind: 'pending';
      /** Authoritative title retained while the request awaits streamed confirmation. */
      readonly baseKind: MissionBaseKind;
      /** Current active name, or the requested name when no named scenario is active. */
      readonly name: string | null;
      readonly startedAtSimulationSeconds?: number;
      readonly revision?: number;
      readonly pendingName: string | null;
      readonly pendingKind: 'scenario' | 'reset';
    };

type MissionBaseView = Exclude<MissionView, { readonly kind: 'pending' }>;

export interface ScenarioRequestToken {
  readonly generation: number;
  readonly targetName: string | null;
  readonly baselineRevision: number;
  readonly baselineApplySequence: number;
}

export interface ScenarioRuntimeOptions {
  /** Runs presentation effects only after an authoritative named state reaches Live. */
  readonly onPresent: (scenario: ScenarioSessionState) => void;
}

export type MissionListener = (view: MissionView) => void;

interface PendingRequest {
  readonly token: ScenarioRequestToken;
  accepted: boolean;
  expectedRevision: number | null;
  matchingRevision: number | null;
  observedRevision: number | null;
  resetConfirmationSequence: number | null;
}

interface DeferredState {
  readonly scenario: ScenarioSessionState | null | undefined;
  readonly assetCount: number;
  readonly applySequence: number;
}

/**
 * Converts the streamed scenario tri-state into one operator-facing mission.
 *
 * HTTP responses can mark a request pending but never activate it. A full or
 * reconstructed streamed frame is the only authority, and presentation effects
 * are held while replay is driving the scene.
 */
export class ScenarioRuntime {
  private _scenario: ScenarioSessionState | null | undefined = undefined;
  private _assetCount = 0;
  private _highestRevision = -1;
  private _lastPresentedRevision = -1;
  private _applySequence = 0;
  private _requestGeneration = 0;
  private _request: PendingRequest | null = null;
  private _deferred: DeferredState | null = null;
  private _view: MissionView = { kind: 'unknown', pendingName: null };
  private readonly _listeners = new Set<MissionListener>();

  constructor(private readonly _options: ScenarioRuntimeOptions) {}

  get view(): MissionView {
    return this._view;
  }

  /** The active streamed name, excluding a merely requested replacement. */
  get currentName(): string | null {
    return this._scenario?.name ?? null;
  }

  subscribe(listener: MissionListener): () => void {
    this._listeners.add(listener);
    listener(this._view);
    return () => this._listeners.delete(listener);
  }

  apply(
    scenario: ScenarioSessionState | null | undefined,
    assetCount: number,
    mode: ScenarioInteractionMode,
  ): void {
    const applySequence = ++this._applySequence;

    if (scenario !== null && scenario !== undefined) {
      if (scenario.revision < this._highestRevision) return;

      if (scenario.revision === this._highestRevision) {
        const deferredMatches = this._deferred?.scenario !== null
          && this._deferred?.scenario !== undefined
          && this._deferred.scenario.revision === scenario.revision
          && this._deferred.scenario.name === scenario.name;
        const currentMatches = this._scenario !== null
          && this._scenario !== undefined
          && this._scenario.revision === scenario.revision
          && this._scenario.name === scenario.name;

        // Equal identity is an ordinary repeated frame. It is also the bridge
        // from a deferred replay transition back to Live.
        if (mode === 'live' && deferredMatches) {
          this._deferred = null;
          this._commit(scenario, assetCount, applySequence, true);
        } else if (currentMatches) {
          this._commit(scenario, assetCount, applySequence, false);
        }
        return;
      }

      this._highestRevision = scenario.revision;
    }

    if (mode === 'replay') {
      this._deferred = { scenario, assetCount, applySequence };
      this._observeRequest(scenario, assetCount, applySequence);
      return;
    }

    this._deferred = null;
    this._commit(scenario, assetCount, applySequence, true);
  }

  /** Flushes the newest authoritative state observed while replay was active. */
  resumeLive(): void {
    const deferred = this._deferred;
    if (deferred === null) return;
    this._deferred = null;
    this._commit(deferred.scenario, deferred.assetCount, deferred.applySequence, true);
  }

  /** Starts a request generation without presenting it as accepted yet. */
  requested(targetName: string | null): ScenarioRequestToken {
    const token: ScenarioRequestToken = {
      generation: ++this._requestGeneration,
      targetName,
      baselineRevision: this._highestRevision,
      baselineApplySequence: this._applySequence,
    };
    this._request = {
      token,
      accepted: false,
      expectedRevision: null,
      matchingRevision: null,
      observedRevision: null,
      resetConfirmationSequence: null,
    };
    return token;
  }

  requestAccepted(
    request: ScenarioRequestToken | string | null,
    current?: ScenarioSessionState,
  ): void {
    const pending = this._matchingRequest(request);
    if (pending === null) return;

    pending.accepted = true;
    pending.expectedRevision = current?.revision ?? null;

    if (this._isAlreadyResolved(pending)) {
      this._request = null;
    }
    this._refreshView();
  }

  requestFailed(request: ScenarioRequestToken | string | null): void {
    if (this._matchingRequest(request) === null) return;
    this._request = null;
    this._refreshView();
  }

  private _commit(
    scenario: ScenarioSessionState | null | undefined,
    assetCount: number,
    applySequence: number,
    allowPresentation: boolean,
  ): void {
    this._scenario = scenario;
    this._assetCount = assetCount;
    this._observeRequest(scenario, assetCount, applySequence);
    this._refreshView();

    if (allowPresentation
      && scenario !== null
      && scenario !== undefined
      && scenario.revision > this._lastPresentedRevision) {
      this._lastPresentedRevision = scenario.revision;
      this._options.onPresent(scenario);
    }
  }

  private _observeRequest(
    scenario: ScenarioSessionState | null | undefined,
    assetCount: number,
    applySequence: number,
  ): void {
    const pending = this._request;
    if (pending === null || applySequence <= pending.token.baselineApplySequence) return;

    const target = pending.token.targetName;
    if (target === null) {
      if (scenario === null && assetCount === 0) {
        pending.resetConfirmationSequence = applySequence;
      }
    } else if (scenario !== null && scenario !== undefined
      && scenario.revision > pending.token.baselineRevision) {
      pending.observedRevision = Math.max(pending.observedRevision ?? -1, scenario.revision);
      if (scenario.name === target) {
        pending.matchingRevision = Math.max(pending.matchingRevision ?? -1, scenario.revision);
      }
    }

    if (pending.accepted && this._isAlreadyResolved(pending)) {
      this._request = null;
    }
  }

  private _isAlreadyResolved(pending: PendingRequest): boolean {
    const target = pending.token.targetName;
    if (target === null) {
      return pending.resetConfirmationSequence !== null
        && pending.resetConfirmationSequence > pending.token.baselineApplySequence;
    }

    if (pending.expectedRevision !== null) {
      return (pending.matchingRevision ?? -1) >= pending.expectedRevision
        || (pending.observedRevision ?? -1) > pending.expectedRevision;
    }
    return pending.matchingRevision !== null;
  }

  private _matchingRequest(
    request: ScenarioRequestToken | string | null,
  ): PendingRequest | null {
    const pending = this._request;
    if (pending === null) return null;
    if (typeof request === 'object' && request !== null) {
      return request.generation === pending.token.generation ? pending : null;
    }
    return request === pending.token.targetName ? pending : null;
  }

  private _refreshView(): void {
    const base = this._baseView();
    const pending = this._request;
    let next: MissionView = base;
    if (pending?.accepted) {
      const active = base.kind === 'active' ? base : null;
      next = {
        kind: 'pending',
        baseKind: base.kind,
        name: active?.name ?? pending.token.targetName,
        ...(active === null ? {} : {
          startedAtSimulationSeconds: active.startedAtSimulationSeconds,
          revision: active.revision,
        }),
        pendingName: pending.token.targetName,
        pendingKind: pending.token.targetName === null ? 'reset' : 'scenario',
      };
    }

    if (sameView(this._view, next)) return;
    this._view = next;
    for (const listener of this._listeners) listener(next);
  }

  private _baseView(): MissionBaseView {
    const scenario = this._scenario;
    if (scenario === undefined) return { kind: 'unknown', pendingName: null };
    if (scenario === null) {
      return this._assetCount === 0
        ? { kind: 'none', pendingName: null }
        : { kind: 'custom', pendingName: null };
    }
    return {
      kind: 'active',
      name: scenario.name,
      startedAtSimulationSeconds: scenario.startedAtSimulationSeconds,
      revision: scenario.revision,
      pendingName: null,
    };
  }
}

function sameView(left: MissionView, right: MissionView): boolean {
  return left.kind === right.kind
    && ('name' in left ? left.name : null) === ('name' in right ? right.name : null)
    && ('revision' in left ? left.revision : null) === ('revision' in right ? right.revision : null)
    && ('startedAtSimulationSeconds' in left ? left.startedAtSimulationSeconds : null)
      === ('startedAtSimulationSeconds' in right ? right.startedAtSimulationSeconds : null)
    && left.pendingName === right.pendingName
    && ('baseKind' in left ? left.baseKind : null)
      === ('baseKind' in right ? right.baseKind : null)
    && ('pendingKind' in left ? left.pendingKind : null)
      === ('pendingKind' in right ? right.pendingKind : null);
}
