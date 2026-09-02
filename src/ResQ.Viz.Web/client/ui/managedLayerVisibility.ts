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

  constructor(private readonly _layers: readonly HTMLElement[]) {}

  setSuppressed(suppressed: boolean): void {
    if (suppressed) {
      const active = this._layers[0]?.ownerDocument.activeElement;
      if (active instanceof HTMLElement && this._layers.some((layer) => layer.contains(active))) {
        active.blur();
      }

      for (const layer of this._layers) {
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
}
