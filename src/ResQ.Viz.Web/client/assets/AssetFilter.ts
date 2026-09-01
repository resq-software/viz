// ResQ Viz - mixed-fleet asset filtering and counting
// SPDX-License-Identifier: Apache-2.0
//
// With one domain a marker list is legible unfiltered. With three it is a pile:
// twelve markers where the operator needs "the two vessels my agency owns, and
// which of them has gone stale". This module is the faceted narrowing that turns
// the second question back into the first.
//
// Two halves, deliberately separable:
//
//  * Pure functions over a `FilterableAsset[]` — `matchesFilter`, `applyFilter`,
//    `computeFacets`, `fleetSummaryText`. No DOM, no storage, no clock. These are
//    what the keyboard cycling in `app.ts` and the a11y live region need, and they
//    are testable in the node environment.
//  * `AssetFilter`, a small DOM control that owns a selection, persists it the way
//    `../settings.ts` persists everything else, and renders the facets as native
//    `fieldset`/`legend`/`checkbox` so grouping, labelling and keyboard operation
//    come from the platform rather than from re-implemented ARIA.
//
// A facet value is a **token** — a stable camelCase string — never an enum number
// and never a display label. Numbers would silently re-point at a different value
// if the wire enum ever gained a member, and a persisted label breaks the moment
// wording changes. Tokens are derived from the C# member names, which are the part
// of the contract that does not move.

import '../styles/assets.css';

import type { AssetDescriptor, AssetState } from './types';
import { AssetDomain, DataFreshness, OperationalState, VehicleClass } from './types';
import type { AssetView } from './assetView';

// ── Enum presentation ───────────────────────────────────────────────────────

/** A wire enum's members indexed for display: label, persisted token, and the
 *  declaration order that facet rows are sorted by. */
interface EnumIndex {
  readonly labels: ReadonlyMap<number, string>;
  readonly tokens: ReadonlyMap<number, string>;
  readonly order: ReadonlyMap<string, number>;
}

const ENUM_INDEX = new WeakMap<object, EnumIndex>();

/**
 * Turns `SmallUnmannedAircraft` into `Small unmanned aircraft`.
 *
 * Also correct for the camelCase command kinds the capability report carries
 * (`resumeAutonomy` -> `Resume autonomy`), which is why `AssetPanel` labels its
 * buttons with this rather than a hardcoded table: a command kind the server adds
 * tomorrow gets a sensible label today, and no second catalog can drift from the
 * first.
 */
export function humanise(name: string): string {
  const spaced = name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function indexEnum(members: Readonly<Record<string, number>>): EnumIndex {
  const cached = ENUM_INDEX.get(members);
  if (cached) return cached;

  const labels = new Map<number, string>();
  const tokens = new Map<number, string>();
  const order = new Map<string, number>();
  let rank = 0;
  for (const [name, value] of Object.entries(members)) {
    // First declaration wins for an aliased value; a later alias is a synonym,
    // not a distinct thing to offer the operator as a separate filter row.
    if (labels.has(value)) continue;
    const token = name.charAt(0).toLowerCase() + name.slice(1);
    labels.set(value, humanise(name));
    tokens.set(value, token);
    order.set(token, rank++);
  }
  const index: EnumIndex = { labels, tokens, order };
  ENUM_INDEX.set(members, index);
  return index;
}

/** Display label for a wire enum value. An unrecognised value reads as unknown
 *  and carries its number, because inventing a name for it would be a lie the
 *  operator cannot see through. */
export function enumLabel(members: Readonly<Record<string, number>>, value: number): string {
  return indexEnum(members).labels.get(value) ?? `Unknown (${value})`;
}

/** Persisted token for a wire enum value. */
export function enumToken(members: Readonly<Record<string, number>>, value: number): string {
  return indexEnum(members).tokens.get(value) ?? `#${value}`;
}

/** Medium an asset operates in, as an operator-facing word. */
export function domainLabel(domain: number): string {
  return enumLabel(AssetDomain, domain);
}

/** Coarse operational state, as an operator-facing word. */
export function operationalStateLabel(state: number): string {
  return enumLabel(OperationalState, state);
}

/** How far a report can still be trusted, as an operator-facing word. */
export function freshnessLabel(freshness: number): string {
  return enumLabel(DataFreshness, freshness);
}

/** Mobility archetype, as an operator-facing word. */
export function vehicleClassLabel(vehicleClass: number): string {
  return enumLabel(VehicleClass, vehicleClass);
}

// ── The filterable projection ───────────────────────────────────────────────

/**
 * The fields filtering and counting actually read.
 *
 * Not `AssetDescriptor & AssetState`: those carry covariances, fault codes and
 * mesh paths that no facet keys on, and the v1 drone stream cannot produce them
 * at all. This shape is a superset of the corresponding `AssetView` fields plus
 * the two descriptor identifiers a mixed-agency picture is organised by, so an
 * `AssetView` with `agencyId`/`fleetId` attached satisfies it structurally.
 */
export interface FilterableAsset {
  readonly id: string;
  readonly displayName: string;
  readonly domain: number;
  readonly vehicleClass: number;
  /** Owning agency, or null when the source does not say. Null is not "none". */
  readonly agencyId: string | null;
  readonly fleetId: string | null;
  readonly operationalState: number;
  readonly freshness: number;
}

/** Projects a v2 descriptor + state pair onto the filterable shape. */
export function filterableFromV2(
  descriptor: AssetDescriptor,
  state: AssetState,
): FilterableAsset {
  return {
    id: descriptor.assetId,
    displayName: descriptor.displayName || descriptor.assetId,
    domain: descriptor.domain,
    vehicleClass: descriptor.vehicleClass,
    agencyId: descriptor.agencyId,
    fleetId: descriptor.fleetId,
    operationalState: state.operationalState,
    freshness: state.freshness,
  };
}

/**
 * Projects a scene view onto the filterable shape.
 *
 * `descriptor` is optional because the v1 drone stream has no descriptor to give:
 * agency and fleet come back null there, which the facets render as unassigned
 * rather than inventing a fleet nobody declared.
 */
export function filterableFromView(
  view: AssetView,
  descriptor?: AssetDescriptor | null,
): FilterableAsset {
  return {
    id: view.id,
    displayName: view.displayName,
    domain: view.domain,
    vehicleClass: view.vehicleClass,
    agencyId: descriptor?.agencyId ?? null,
    fleetId: descriptor?.fleetId ?? null,
    operationalState: view.operationalState,
    freshness: view.freshness,
  };
}

// ── Facets ──────────────────────────────────────────────────────────────────

/** The dimensions a mixed fleet is narrowed along. */
export type FacetKey = 'domain' | 'class' | 'agency' | 'fleet' | 'state' | 'freshness';

/** Token standing for "the source did not say", kept distinct from any real id.
 *  Parenthesised so it cannot collide with an agency actually called `none`. */
export const UNASSIGNED_TOKEN = '(unassigned)';

/** A selection per facet. An **empty list means unconstrained**, not "nothing
 *  matches" — the difference decides whether a fresh session shows the fleet or
 *  an empty scene. */
export type FilterSelection = Readonly<Record<FacetKey, readonly string[]>>;

interface FacetSpec {
  readonly key: FacetKey;
  readonly legend: string;
  token(asset: FilterableAsset): string;
  label(token: string): string;
  /** Sort rank within the facet; ties fall back to label order. */
  rank(token: string): number;
}

function enumSpec(
  key: FacetKey,
  legend: string,
  members: Readonly<Record<string, number>>,
  read: (asset: FilterableAsset) => number,
): FacetSpec {
  const index = indexEnum(members);
  const byToken = new Map<string, string>();
  for (const [value, token] of index.tokens) byToken.set(token, index.labels.get(value) ?? token);
  return {
    key,
    legend,
    token: (asset) => enumToken(members, read(asset)),
    label: (token) => byToken.get(token) ?? token,
    rank: (token) => index.order.get(token) ?? Number.MAX_SAFE_INTEGER,
  };
}

function idSpec(
  key: FacetKey,
  legend: string,
  read: (asset: FilterableAsset) => string | null,
): FacetSpec {
  return {
    key,
    legend,
    token: (asset) => read(asset) ?? UNASSIGNED_TOKEN,
    label: (token) => (token === UNASSIGNED_TOKEN ? 'Unassigned' : token),
    // Unassigned sorts last: it is the residue, not a peer of the named fleets.
    rank: (token) => (token === UNASSIGNED_TOKEN ? 1 : 0),
  };
}

const FACETS: readonly FacetSpec[] = [
  enumSpec('domain', 'Domain', AssetDomain, (a) => a.domain),
  enumSpec('class', 'Class', VehicleClass, (a) => a.vehicleClass),
  idSpec('agency', 'Agency', (a) => a.agencyId),
  idSpec('fleet', 'Fleet', (a) => a.fleetId),
  enumSpec('state', 'State', OperationalState, (a) => a.operationalState),
  enumSpec('freshness', 'Freshness', DataFreshness, (a) => a.freshness),
];

/** The facet keys, in the order the control renders them. */
export const FACET_KEYS: readonly FacetKey[] = FACETS.map((f) => f.key);

/** A selection that constrains nothing. */
export function emptySelection(): FilterSelection {
  return { domain: [], class: [], agency: [], fleet: [], state: [], freshness: [] };
}

function passesFacet(asset: FilterableAsset, spec: FacetSpec, selection: FilterSelection): boolean {
  const chosen = selection[spec.key];
  return chosen.length === 0 || chosen.includes(spec.token(asset));
}

/** Whether one asset survives every constrained facet. */
export function matchesFilter(asset: FilterableAsset, selection: FilterSelection): boolean {
  return FACETS.every((spec) => passesFacet(asset, spec, selection));
}

/** The assets a selection leaves visible, in input order. Generic so a caller can
 *  filter its own richer records — scene views, panel rows — without a second
 *  projection and a lookup back. */
export function applyFilter<T extends FilterableAsset>(
  assets: readonly T[],
  selection: FilterSelection,
): T[] {
  return assets.filter((a) => matchesFilter(a, selection));
}

/** One offered value within a facet. */
export interface FacetValue {
  readonly token: string;
  readonly label: string;
  /** How many assets this value would leave visible. Counted against the *other*
   *  facets only, so ticking a second domain shows what it would add rather than
   *  the zero its own facet currently implies. */
  readonly count: number;
  readonly selected: boolean;
}

/** One facet and the values present in the current fleet. */
export interface Facet {
  readonly key: FacetKey;
  readonly legend: string;
  readonly values: readonly FacetValue[];
}

/**
 * The values worth offering, with counts.
 *
 * Values come from the assets actually present plus anything currently selected —
 * a ticked box whose last asset just landed must stay on screen, or the operator
 * is left with an invisible constraint and an empty scene they cannot explain.
 */
export function computeFacets(
  assets: readonly FilterableAsset[],
  selection: FilterSelection,
): Facet[] {
  return FACETS.map((spec) => {
    const others = FACETS.filter((f) => f.key !== spec.key);
    const counts = new Map<string, number>();
    for (const asset of assets) {
      const token = spec.token(asset);
      if (!counts.has(token)) counts.set(token, 0);
    }
    for (const token of selection[spec.key]) {
      if (!counts.has(token)) counts.set(token, 0);
    }
    for (const asset of assets) {
      if (!others.every((f) => passesFacet(asset, f, selection))) continue;
      const token = spec.token(asset);
      counts.set(token, (counts.get(token) ?? 0) + 1);
    }

    const chosen = selection[spec.key];
    const values = Array.from(counts, ([token, count]): FacetValue => ({
      token,
      label: spec.label(token),
      count,
      selected: chosen.includes(token),
    }));
    values.sort((a, b) => spec.rank(a.token) - spec.rank(b.token) || a.label.localeCompare(b.label));
    return { key: spec.key, legend: spec.legend, values };
  });
}

// ── Fleet summary ───────────────────────────────────────────────────────────

const ATTENTION_STATES: readonly number[] = [OperationalState.Emergency, OperationalState.Faulted];
const DEGRADED_FRESHNESS: readonly number[] = [DataFreshness.Stale, DataFreshness.Lost];

/**
 * One sentence describing a mixed fleet, for the polite live region.
 *
 * Replaces the drone-only count that region announces today. Domains are named
 * because that is the fact a screen-reader user cannot get from the scene; the
 * two exception counts follow because "six assets" is not worth interrupting for
 * if it omits that one of them is in emergency. Nothing is announced that was not
 * reported: an empty fleet says so rather than reading zeros.
 */
export function fleetSummaryText(
  assets: readonly FilterableAsset[],
  hiddenByFilter = 0,
): string {
  if (assets.length === 0) {
    return hiddenByFilter > 0
      ? `No assets shown; ${hiddenByFilter} hidden by the fleet filter.`
      : 'No assets in view.';
  }

  const byDomain = new Map<number, number>();
  for (const asset of assets) byDomain.set(asset.domain, (byDomain.get(asset.domain) ?? 0) + 1);
  const parts = Array.from(byDomain, ([domain, count]) => [domain, count] as const)
    .sort((a, b) => a[0] - b[0])
    .map(([domain, count]) => `${count} ${domainLabel(domain).toLowerCase()}`);

  const attention = assets.filter((a) => ATTENTION_STATES.includes(a.operationalState)).length;
  const degraded = assets.filter((a) => DEGRADED_FRESHNESS.includes(a.freshness)).length;

  const noun = assets.length === 1 ? 'asset' : 'assets';
  let text = `${assets.length} ${noun}: ${parts.join(', ')}.`;
  if (attention > 0) text += ` ${attention} needing attention.`;
  if (degraded > 0) text += ` ${degraded} with degraded telemetry.`;
  if (hiddenByFilter > 0) text += ` ${hiddenByFilter} hidden by the fleet filter.`;
  return text;
}

// ── Persistence ─────────────────────────────────────────────────────────────

/** Storage surface the control needs. Narrowed to two methods so a test can pass
 *  a plain object and so a privacy-mode `localStorage` that throws on write is
 *  substitutable. */
export type SelectionStorage = Pick<Storage, 'getItem' | 'setItem'>;

const STORAGE_KEY = 'resq-viz-asset-filter';
/** Bumped when the persisted shape changes; an older payload is discarded rather
 *  than half-read, matching `../settings.ts`. */
const SCHEMA_VERSION = 1;

function defaultStorage(): SelectionStorage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}

function sanitiseTokens(raw: unknown): string[] {
  return Array.isArray(raw) ? raw.filter((t): t is string => typeof t === 'string') : [];
}

/** Reads a persisted selection, falling back to unconstrained on anything the
 *  current schema does not recognise. A filter is a view preference; refusing to
 *  start because one is malformed would be the wrong trade. */
export function loadSelection(storage: SelectionStorage | null = defaultStorage()): FilterSelection {
  const selection = emptySelection() as Record<FacetKey, string[]>;
  if (!storage) return selection;
  try {
    const raw = storage.getItem(STORAGE_KEY);
    if (!raw) return selection;
    const parsed = JSON.parse(raw) as { _v?: number } & Partial<Record<FacetKey, unknown>>;
    if ((parsed._v ?? 0) !== SCHEMA_VERSION) return selection;
    for (const key of FACET_KEYS) selection[key] = sanitiseTokens(parsed[key]);
  } catch {
    /* A view preference is never worth throwing over. */
  }
  return selection;
}

/** Persists a selection. Silent on failure for the same reason. */
export function saveSelection(
  selection: FilterSelection,
  storage: SelectionStorage | null = defaultStorage(),
): void {
  if (!storage) return;
  try {
    storage.setItem(STORAGE_KEY, JSON.stringify({ ...selection, _v: SCHEMA_VERSION }));
  } catch {
    /* Quota or a blocked origin; the session still works, it just will not remember. */
  }
}

// ── The control ─────────────────────────────────────────────────────────────

/** Construction options. Everything is injectable so the control can be driven
 *  headlessly in a test. */
export interface AssetFilterOptions {
  /** Element the control appends itself to. Defaults to `document.body`. */
  readonly mount?: HTMLElement;
  /** Persistence target, or `null` to keep the selection in memory only. */
  readonly storage?: SelectionStorage | null;
  /** Whether to hide a facet whose values would offer no choice. On by default:
   *  a "Domain: air" group in an all-air session is chrome, not information. */
  readonly hideSingleValueFacets?: boolean;
}

interface ChipParts {
  readonly label: HTMLLabelElement;
  readonly input: HTMLInputElement;
  readonly text: HTMLSpanElement;
  readonly count: HTMLSpanElement;
}

interface FacetParts {
  readonly fieldset: HTMLFieldSetElement;
  /** Chips live in their own element rather than directly in the fieldset: a
   *  `legend` is rendered out of normal flow by the UA, and laying it out as a
   *  flex item is inconsistent across engines. */
  readonly host: HTMLDivElement;
  readonly chips: Map<string, ChipParts>;
}

/**
 * The fleet filter: facet checkboxes with live counts, plus the selection they
 * produce.
 *
 * Rendering is a keyed diff rather than a re-render. At 10 Hz a rebuilt subtree
 * would drop focus out of whichever checkbox a keyboard user was on, every tenth
 * of a second — the control would be unusable by exactly the people this file's
 * semantics are for.
 */
export class AssetFilter {
  private readonly _root: HTMLElement;
  private readonly _tally: HTMLParagraphElement;
  private readonly _clear: HTMLButtonElement;
  private readonly _facetHost: HTMLDivElement;
  private readonly _parts = new Map<FacetKey, FacetParts>();
  private readonly _storage: SelectionStorage | null;
  private readonly _hideSingle: boolean;
  private readonly _listeners: Array<(selection: FilterSelection) => void> = [];

  private _selection: Record<FacetKey, string[]>;
  private _lastTally = '';

  constructor(options: AssetFilterOptions = {}) {
    this._storage = options.storage === undefined ? defaultStorage() : options.storage;
    this._hideSingle = options.hideSingleValueFacets ?? true;
    this._selection = loadSelection(this._storage) as Record<FacetKey, string[]>;

    this._root = document.createElement('section');
    this._root.className = 'asset-filter';
    this._root.setAttribute('aria-label', 'Fleet filter');

    const head = document.createElement('header');
    head.className = 'af-head';

    const title = document.createElement('h2');
    title.className = 'af-title';
    title.textContent = 'Fleet';

    this._tally = document.createElement('p');
    this._tally.className = 'af-tally';

    this._clear = document.createElement('button');
    this._clear.type = 'button';
    this._clear.className = 'btn af-clear';
    this._clear.textContent = 'Clear';
    this._clear.addEventListener('click', () => this.clear());

    head.append(title, this._tally, this._clear);

    this._facetHost = document.createElement('div');
    this._facetHost.className = 'af-facets';

    this._root.append(head, this._facetHost);
    (options.mount ?? document.body).appendChild(this._root);
  }

  /** The control's root element, for a host that wants to place it itself. */
  get element(): HTMLElement {
    return this._root;
  }

  /** The current selection. A copy: mutating the returned lists would change what
   *  is filtered without notifying anyone. */
  get selection(): FilterSelection {
    const out = emptySelection() as Record<FacetKey, string[]>;
    for (const key of FACET_KEYS) out[key] = [...this._selection[key]];
    return out;
  }

  /** Replaces the selection wholesale, persisting and notifying. Unnamed facets
   *  are cleared, so a caller cannot leave a constraint it forgot about in place. */
  setSelection(selection: Partial<Record<FacetKey, readonly string[]>>): void {
    const next = emptySelection() as Record<FacetKey, string[]>;
    for (const key of FACET_KEYS) next[key] = [...(selection[key] ?? [])];
    this._selection = next;
    this._commit();
  }

  /** Drops every constraint. */
  clear(): void {
    this._selection = emptySelection() as Record<FacetKey, string[]>;
    this._commit();
  }

  /** Registers a change listener. Fires on operator input and on `setSelection`,
   *  never on `update` — a frame arriving is not a decision. */
  onChange(listener: (selection: FilterSelection) => void): void {
    this._listeners.push(listener);
  }

  /** Whether one asset survives the current selection. */
  matches(asset: FilterableAsset): boolean {
    return matchesFilter(asset, this._selection);
  }

  /** The assets the current selection leaves visible. */
  apply<T extends FilterableAsset>(assets: readonly T[]): T[] {
    return applyFilter(assets, this._selection);
  }

  /** Reconciles the offered values and counts with the current fleet. */
  update(assets: readonly FilterableAsset[]): void {
    const facets = computeFacets(assets, this._selection);
    for (const facet of facets) this._renderFacet(facet);

    const visible = applyFilter(assets, this._selection).length;
    const tally = visible === assets.length
      ? `${assets.length} shown`
      : `${visible} of ${assets.length} shown`;
    if (tally !== this._lastTally) {
      this._lastTally = tally;
      this._tally.textContent = tally;
    }
    this._clear.disabled = FACET_KEYS.every((key) => this._selection[key].length === 0);
  }

  /** Detaches the control and drops its listeners. */
  dispose(): void {
    this._listeners.length = 0;
    this._parts.clear();
    this._root.remove();
  }

  private _renderFacet(facet: Facet): void {
    let parts = this._parts.get(facet.key);
    if (!parts) {
      const fieldset = document.createElement('fieldset');
      fieldset.className = 'af-facet';
      fieldset.dataset['facet'] = facet.key;
      const legend = document.createElement('legend');
      legend.textContent = facet.legend;
      const host = document.createElement('div');
      host.className = 'af-chips';
      fieldset.append(legend, host);
      this._facetHost.appendChild(fieldset);
      parts = { fieldset, host, chips: new Map() };
      this._parts.set(facet.key, parts);
    }

    const live = new Set<string>();
    for (const value of facet.values) {
      live.add(value.token);
      let chip = parts.chips.get(value.token);
      if (!chip) {
        chip = this._createChip(facet.key, value.token);
        parts.chips.set(value.token, chip);
        parts.host.appendChild(chip.label);
      }
      if (chip.text.textContent !== value.label) chip.text.textContent = value.label;
      const count = String(value.count);
      if (chip.count.textContent !== count) chip.count.textContent = count;
      if (chip.input.checked !== value.selected) chip.input.checked = value.selected;
      chip.label.classList.toggle('is-empty', value.count === 0 && !value.selected);
    }

    for (const [token, chip] of parts.chips) {
      if (live.has(token)) continue;
      chip.label.remove();
      parts.chips.delete(token);
    }

    // A facet offering one value narrows nothing; hide it unless it is carrying a
    // constraint, in which case hiding it would strand the operator.
    const constrained = this._selection[facet.key].length > 0;
    parts.fieldset.hidden = this._hideSingle && facet.values.length < 2 && !constrained;
  }

  private _createChip(key: FacetKey, token: string): ChipParts {
    const label = document.createElement('label');
    label.className = 'af-chip';

    const input = document.createElement('input');
    input.type = 'checkbox';
    input.className = 'af-chip-input';
    input.value = token;
    input.addEventListener('change', () => this._toggle(key, token, input.checked));

    const text = document.createElement('span');
    text.className = 'af-chip-label';

    const count = document.createElement('span');
    count.className = 'af-chip-count';
    // The count is decoration beside a labelled checkbox; announcing "Air 6" as
    // one string would read as part of the name.
    count.setAttribute('aria-hidden', 'true');

    label.append(input, text, count);
    return { label, input, text, count };
  }

  private _toggle(key: FacetKey, token: string, on: boolean): void {
    const current = this._selection[key];
    this._selection[key] = on
      ? current.includes(token) ? current : [...current, token]
      : current.filter((t) => t !== token);
    this._commit();
  }

  private _commit(): void {
    saveSelection(this._selection, this._storage);
    const snapshot = this.selection;
    for (const listener of this._listeners) listener(snapshot);
  }
}
