// ResQ Viz - temporary managed-layer suppression
// SPDX-License-Identifier: Apache-2.0

interface LayerState {
  readonly inert: boolean;
  readonly investorSuppressed: boolean;
}

/** Temporarily suppresses shell-owned layers and restores their prior state. */
export class ManagedLayerVisibility {
  private readonly _states = new Map<HTMLElement, LayerState>();
  private readonly _layers = new Set<HTMLElement>();
  private _suppressed = false;

  constructor(layers: readonly HTMLElement[]) {
    this.addLayers(layers);
  }

  /** Adds surfaces that were mounted after suppression began. */
  addLayers(layers: Iterable<HTMLElement>): void {
    for (const layer of layers) {
      if (this._layers.has(layer)) continue;
      this._layers.add(layer);
      if (this._suppressed) this._suppress(layer);
    }
  }

  setSuppressed(suppressed: boolean): void {
    this._suppressed = suppressed;
    if (suppressed) {
      for (const layer of this._layers) {
        this._suppress(layer);
      }
      return;
    }

    for (const layer of this._layers) {
      const state = this._states.get(layer);
      if (state === undefined) continue;
      if (state.inert) layer.setAttribute('inert', '');
      else layer.removeAttribute('inert');
      if (state.investorSuppressed) layer.setAttribute('data-investor-suppressed', '');
      else layer.removeAttribute('data-investor-suppressed');
      this._states.delete(layer);
    }
  }

  private _suppress(layer: HTMLElement): void {
    const active = layer.ownerDocument.activeElement;
    if (active instanceof HTMLElement && layer.contains(active)) active.blur();
    if (!this._states.has(layer)) {
      this._states.set(layer, {
        inert: layer.hasAttribute('inert'),
        investorSuppressed: layer.hasAttribute('data-investor-suppressed'),
      });
    }
    layer.setAttribute('inert', '');
    layer.setAttribute('data-investor-suppressed', '');
  }
}
