// ResQ Viz - temporary managed-layer suppression
// SPDX-License-Identifier: Apache-2.0

interface LayerState {
  readonly hidden: HTMLElement['hidden'];
  readonly inert: boolean;
  readonly ariaHidden: string | null;
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
      layer.hidden = state.hidden;
      if (state.inert) layer.setAttribute('inert', '');
      else layer.removeAttribute('inert');
      if (state.ariaHidden === null) layer.removeAttribute('aria-hidden');
      else layer.setAttribute('aria-hidden', state.ariaHidden);
      layer.removeAttribute('data-investor-suppressed');
      this._states.delete(layer);
    }
  }

  private _suppress(layer: HTMLElement): void {
    const active = layer.ownerDocument.activeElement;
    if (active instanceof HTMLElement && layer.contains(active)) active.blur();
    if (!this._states.has(layer)) {
      this._states.set(layer, {
        hidden: layer.hidden,
        inert: layer.hasAttribute('inert'),
        ariaHidden: layer.getAttribute('aria-hidden'),
      });
    }
    layer.hidden = true;
    layer.setAttribute('inert', '');
    layer.setAttribute('aria-hidden', 'true');
    layer.setAttribute('data-investor-suppressed', '');
  }
}
