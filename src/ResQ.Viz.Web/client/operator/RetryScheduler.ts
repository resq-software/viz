// ResQ Viz - deduplicated connection retry scheduling
// SPDX-License-Identifier: Apache-2.0

const RETRY_DELAY_MS = 5_000;

/** Timer and retry effects kept outside the deterministic scheduler. */
export interface RetrySchedulerDependencies {
  readonly retry: () => void;
  readonly schedule: (callback: () => void, delayMs: number) => number;
  readonly cancel: (id: number) => void;
}

/** Owns the single pending manual retry after connection startup or recovery fails. */
export class RetryScheduler {
  private _timer: number | null = null;
  private _disposed = false;

  constructor(private readonly _deps: RetrySchedulerDependencies) {}

  /** Requests one retry; concurrent failure signals share the same timer. */
  request(): void {
    if (this._disposed || this._timer !== null) return;
    this._timer = this._deps.schedule(() => {
      this._timer = null;
      if (!this._disposed) this._deps.retry();
    }, RETRY_DELAY_MS);
  }

  /** Cancels the pending retry when another start has already begun or succeeded. */
  cancel(): void {
    if (this._timer === null) return;
    this._deps.cancel(this._timer);
    this._timer = null;
  }

  /** Cancels pending work and rejects future requests. */
  dispose(): void {
    if (this._disposed) return;
    this._disposed = true;
    this.cancel();
  }
}
