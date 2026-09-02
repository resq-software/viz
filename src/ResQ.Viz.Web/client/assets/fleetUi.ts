// ResQ Viz - the mixed-fleet operator surface, in its own chunk
// SPDX-License-Identifier: Apache-2.0
//
// Composes the two operator-facing pieces of the asset layer — `AssetPanel` (the
// capability-driven detail panel that replaces `../ui/dronePanel.ts`) and
// `AssetFilter` (the faceted fleet narrowing) — plus the mixed-fleet sentence the
// a11y live region announces.
//
// It exists as one module for one reason: **so that all three, and the stylesheet
// they share, land in a chunk the entry bundle does not pull.** The panel, the
// filter and `assets.css` are together the largest addition this work makes to
// the client, and a session that never receives a v2 snapshot — an older server,
// a client that fell back to the v1 stream — must not pay for a fleet UI it will
// never show. `app.ts` therefore `import()`s this module on the first supported
// snapshot and not before, the same deferral idiom as `../postfxDeferred.ts`.
//
// Keeping `AssetFilter`'s pure helpers (`applyFilter`, `fleetSummaryText`) behind
// this boundary too is deliberate. Importing one of them statically from `app.ts`
// would drag the whole module — control, chips, persistence and all — back into
// the entry chunk, because rollup splits by module and not by export.

import type { PanelSubject } from './AssetPanel';
import { AssetPanel } from './AssetPanel';
import type { FilterableAsset, FilterSelection, SelectionStorage } from './AssetFilter';
import { AssetFilter, filterableFromV2, fleetSummaryText } from './AssetFilter';
import type {
  AssetCapabilitiesReport,
  CommandIssuer,
  TargetPicker,
} from './panelCommands';
import type { SceneAsset } from './sceneFrame';

/** Construction options. Every collaborator is injectable so the surface can be
 *  driven headlessly in a test. */
export interface FleetUiOptions {
  /** Element the panel attaches to. Defaults to `document.body`; both widgets
   *  position themselves, so a host normally lets them. */
  readonly panelMount?: HTMLElement;
  readonly filterMount?: HTMLElement;
  /** Where the facet selection is remembered. Defaults to `localStorage`; pass
   *  `null` to keep it in memory only, which is what a test wants and what a
   *  kiosk that should not remember one operator's filter for the next wants
   *  too. */
  readonly filterStorage?: SelectionStorage | null;
  /** Resolves a scene-frame destination for a command that needs one. Absent
   *  means target-taking commands are disabled *with that reason* rather than
   *  hidden — the asset accepts them, this client just cannot aim them. */
  readonly pickTarget?: TargetPicker | null;
  /** Where the panel reads an asset's declared capabilities from. Defaults to
   *  `GET /api/v2/sim/assets/{id}/capabilities`; injectable so the surface can be
   *  driven with no server. */
  readonly loadCapabilities?: (assetId: string) => Promise<AssetCapabilitiesReport | null>;
  /** Where a command goes. Defaults to `POST .../commands`. */
  readonly issueCommand?: CommandIssuer;
  /** Called when the operator dismisses the panel. */
  readonly onPanelClose?: () => void;
  /** Called when the operator changes the filter. Fires on input only, never on
   *  a frame arriving — a frame is not a decision. */
  readonly onFilterChange?: (selection: FilterSelection) => void;
}

/**
 * The fleet surface: detail panel, facet filter, and the counts that describe
 * them.
 *
 * `update` is called once per received snapshot with **every** asset, not only
 * the visible ones: the facets have to offer the values that would bring hidden
 * assets back, and a filter that could only ever narrow further is a trap.
 */
export class FleetUi {
  private readonly _panel: AssetPanel;
  private readonly _filter: AssetFilter;

  /** Filterable projections of the last frame's assets, in publication order. */
  private _filterables: FilterableAsset[] = [];
  /** Ids the current selection leaves visible. */
  private _visible = new Set<string>();
  private _hiddenCount = 0;

  constructor(options: FleetUiOptions = {}) {
    this._panel = new AssetPanel({
      ...(options.panelMount === undefined ? {} : { mount: options.panelMount }),
      ...(options.pickTarget === undefined ? {} : { pickTarget: options.pickTarget }),
      ...(options.loadCapabilities === undefined
        ? {} : { loadCapabilities: options.loadCapabilities }),
      ...(options.issueCommand === undefined ? {} : { issueCommand: options.issueCommand }),
    });
    if (options.onPanelClose) this._panel.onClose(options.onPanelClose);

    this._filter = new AssetFilter({
      ...(options.filterMount === undefined ? {} : { mount: options.filterMount }),
      ...(options.filterStorage === undefined ? {} : { storage: options.filterStorage }),
    });
    if (options.onFilterChange) this._filter.onChange(options.onFilterChange);
  }

  /** The detail panel, for a host that needs its element. */
  get panel(): AssetPanel {
    return this._panel;
  }

  /** The facet control, for a host that needs its element. */
  get filter(): AssetFilter {
    return this._filter;
  }

  /**
   * Reconcile the facets with a frame and return the assets that survive the
   * current selection, in publication order.
   *
   * The returned subset is what the caller feeds the scene, the mini-map and the
   * outliner, so filtering is one decision applied in one place rather than six
   * surfaces each re-deriving it and disagreeing about the answer.
   */
  update(assets: readonly SceneAsset[]): SceneAsset[] {
    this._filterables = assets.map((a) => filterableFromV2(a.descriptor, a.state));
    this._filter.update(this._filterables);

    const visible: SceneAsset[] = [];
    this._visible = new Set<string>();
    for (let i = 0; i < assets.length; i++) {
      const asset = assets[i];
      const filterable = this._filterables[i];
      if (asset === undefined || filterable === undefined) continue;
      if (!this._filter.matches(filterable)) continue;
      visible.push(asset);
      this._visible.add(asset.view.id);
    }
    this._hiddenCount = assets.length - visible.length;
    return visible;
  }

  /** Whether one asset survives the current selection. An id not seen in the last
   *  `update` reads as hidden — an asset the filter has never been shown is not
   *  one it can vouch for. */
  isVisible(id: string): boolean {
    return this._visible.has(id);
  }

  /** Visible ids, in publication order. What keyboard selection cycling walks, so
   *  `[` and `]` skip whatever the operator has filtered out. */
  visibleIds(): string[] {
    return this._filterables.filter((f) => this._visible.has(f.id)).map((f) => f.id);
  }

  /**
   * One sentence describing the fleet, for the polite live region.
   *
   * Counts the *visible* assets and names how many the filter is holding back,
   * because "no assets in view" and "six assets, all filtered out" call for
   * different actions from an operator who cannot see the scene.
   */
  summaryText(): string {
    const visible = this._filterables.filter((f) => this._visible.has(f.id));
    return fleetSummaryText(visible, this._hiddenCount);
  }

  /** Show or refresh the detail panel; `null` hides it. `simulationNowMs` is the
   *  frame's instant on the *simulation* clock — the only ruler a track's age may
   *  be measured with, since that is the clock its report was stamped from — and
   *  defaults to null, meaning unknown, for the callers (hiding, dismissing) that
   *  have no frame to age against. Never the wall clock, which disagrees with
   *  those stamps at every speed multiplier and after every pause. */
  renderSubject(subject: PanelSubject | null, simulationNowMs: number | null = null): void {
    this._panel.render(subject, simulationNowMs);
  }

  /** Identifier of whatever the panel is showing, or null. */
  get subjectId(): string | null {
    return this._panel.subjectId;
  }

  /** Whether Escape may currently treat the context panel as its active owner. */
  get contextVisible(): boolean {
    return this._panel.isVisible;
  }

  /** Detaches both widgets. */
  dispose(): void {
    this._panel.dispose();
    this._filter.dispose();
  }
}
