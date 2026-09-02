// ResQ Viz - startup stream negotiation and one-time default ownership
// SPDX-License-Identifier: Apache-2.0

import type { ScenarioSessionState } from '../assets/types';
import type { OperatorBootStatus, OperatorMode } from './types';

const FALLBACK_DELAY_MS = 5_000;
const LEGACY_DEFAULT = 'single';
const V2_DEFAULT = 'flood-response';

/** The startup facts carried by one readable v2 snapshot. */
export interface StartupSnapshot {
  readonly assetCount: number;
  /** Undefined is an older/unknown payload; null is an authoritative empty state. */
  readonly scenario: ScenarioSessionState | null | undefined;
}

/** Minimum result shape needed from a typed v2 mutation. */
export interface StartupMutationResult {
  readonly success: boolean;
}

/** All effects owned outside the deterministic startup state machine. */
export interface StartupCoordinatorDependencies {
  readonly setMode: (mode: OperatorMode) => void;
  readonly setBootStatus: (status: OperatorBootStatus) => void;
  readonly startLegacyScenario: (name: string) => Promise<boolean>;
  readonly startV2Scenario: (name: string) => Promise<StartupMutationResult>;
  readonly schedule: (callback: () => void, delayMs: number) => number;
  readonly cancel: (id: number) => void;
}

/**
 * Negotiates the readable stream and owns the room session's one startup default.
 *
 * A single decision latch spans both compatibility paths. Once either a populated
 * inventory decides that no default is needed or one path claims its mutation,
 * a late frame from the other path cannot reset the room a second time.
 */
export class StartupCoordinator {
  private _mode: OperatorMode = 'booting';
  private _bootStatus: OperatorBootStatus | null = null;
  private _v1AssetCount: number | null = null;
  private _v2Readable = false;
  private _v2Rejected = false;
  private _fallbackElapsed = false;
  private _fallbackTimer: number | null = null;
  private _defaultDecided = false;
  private _disposed = false;

  constructor(private readonly _deps: StartupCoordinatorDependencies) {}

  /** Starts the five-second accepted-but-silent v2 fallback window. */
  startNegotiation(): void {
    if (this._disposed
      || this._fallbackTimer !== null
      || this._v2Readable
      || this._fallbackElapsed) return;
    this._setBootStatus('connecting');
    this._fallbackElapsed = false;
    this._fallbackTimer = this._deps.schedule(() => {
      this._fallbackTimer = null;
      if (this._disposed || this._v2Readable) return;
      this._fallbackElapsed = true;
      if (!this._tryEnterLegacy()) {
        this._setMode('booting');
        this._setBootStatus('error');
      }
    }, FALLBACK_DELAY_MS);
  }

  /** Records proof that the legacy stream works, before any rendering gate. */
  onV1Frame(assetCount: number): void {
    if (this._disposed) return;
    this._v1AssetCount = assetCount;
    if (!this._v2Readable && (this._v2Rejected || this._fallbackElapsed)) {
      this._tryEnterLegacy();
    }
  }

  /** Records an unavailable or unreadable v2 stream. Legacy still needs proof. */
  onV2Rejected(): void {
    if (this._disposed) return;
    this._v2Readable = false;
    this._v2Rejected = true;
    if (this._v1AssetCount === null) {
      if (this._mode === 'v2') this._setMode('booting');
      return;
    }
    this._cancelFallback();
    this._tryEnterLegacy();
  }

  /** Promotes any fallback in place and evaluates the v2 default exactly once. */
  async onV2Snapshot(snapshot: StartupSnapshot): Promise<void> {
    if (this._disposed) return;
    this._v2Readable = true;
    this._v2Rejected = false;
    this._fallbackElapsed = false;
    this._cancelFallback();
    this._setMode('v2');

    if (this._defaultDecided) return;
    if (snapshot.assetCount > 0) {
      this._defaultDecided = true;
      return;
    }
    if (snapshot.scenario === undefined) return;

    // Both an active named scenario and an authoritative empty room resolve the
    // room-session startup decision. Claim before awaiting so a second frame or
    // a concurrent legacy fallback cannot issue another destructive mutation.
    this._defaultDecided = true;
    if (snapshot.scenario !== null) return;
    await this._deps.startV2Scenario(V2_DEFAULT);
  }

  /** Cancels an unresolved attempt after the connection itself fails. */
  onConnectionFailed(): void {
    if (this._disposed) return;
    this._cancelFallback();
    this._setBootStatus('error');
    // Viability belongs to one SignalR connection. The room-session default
    // decision deliberately does not: reconnecting must not make either preset
    // eligible again, but it must prove the two streams again from fresh frames.
    this._v1AssetCount = null;
    this._v2Readable = false;
    this._v2Rejected = false;
    this._fallbackElapsed = false;
  }

  /** Releases the only timer this state machine owns. */
  dispose(): void {
    if (this._disposed) return;
    this._disposed = true;
    this._cancelFallback();
  }

  private _tryEnterLegacy(): boolean {
    const assetCount = this._v1AssetCount;
    if (assetCount === null || this._v2Readable || this._disposed) return false;
    this._setMode('legacy');

    if (this._defaultDecided) return true;
    this._defaultDecided = true;
    if (assetCount === 0) void this._deps.startLegacyScenario(LEGACY_DEFAULT);
    return true;
  }

  private _setMode(mode: OperatorMode): void {
    if (this._mode === mode) return;
    this._mode = mode;
    this._deps.setMode(mode);
  }

  private _setBootStatus(status: OperatorBootStatus): void {
    if (this._bootStatus === status) return;
    this._bootStatus = status;
    this._deps.setBootStatus(status);
  }

  private _cancelFallback(): void {
    if (this._fallbackTimer === null) return;
    this._deps.cancel(this._fallbackTimer);
    this._fallbackTimer = null;
  }
}
