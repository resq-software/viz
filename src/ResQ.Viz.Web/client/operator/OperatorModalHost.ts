// ResQ Viz - shared lazy operator-modal ownership
// SPDX-License-Identifier: Apache-2.0

/** Surface that can retire its DOM and async paint generation. */
export interface OperatorModalSurface {
  invalidate(): void;
  refresh?(): void;
}

/**
 * Owns one body-level operator modal and rejects stale lazy-import generations.
 * Scenario, spawn, and environment surfaces share this coordinator.
 */
export class OperatorModalHost {
  private _generation = 0;
  private _active: OperatorModalSurface | null = null;

  get active(): OperatorModalSurface | null {
    return this._active;
  }

  /** Whether a lazy import still belongs to the newest requested surface. */
  isCurrent(generation: number): boolean {
    return generation === this._generation;
  }

  /** Begins a new lazy surface request and retires the previous owner. */
  begin(): number {
    this._generation++;
    this._retireActive();
    return this._generation;
  }

  /** Claims the layer only if no later load or invalidation superseded it. */
  activate(generation: number, surface: OperatorModalSurface): boolean {
    if (generation !== this._generation) {
      surface.invalidate();
      return false;
    }
    this._retireActive();
    this._active = surface;
    return true;
  }

  /** Invalidates in-flight imports and closes the active surface. */
  invalidate(): void {
    this._generation++;
    this._retireActive();
  }

  /** Releases ownership only when the closing surface is still current. */
  release(surface: OperatorModalSurface): void {
    if (this._active === surface) this._active = null;
  }

  /** Lets authoritative state patch the currently visible surface in place. */
  refresh(): void {
    this._active?.refresh?.();
  }

  private _retireActive(): void {
    const active = this._active;
    this._active = null;
    active?.invalidate();
  }
}
