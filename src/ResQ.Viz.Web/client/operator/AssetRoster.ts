// ResQ Viz - keyed mixed-domain fleet roster
// SPDX-License-Identifier: Apache-2.0

import type { FilterSelection } from '../assets/AssetFilter';
import {
  domainLabel,
  enumLabel,
  filterableFromV2,
  matchesFilter,
  operationalStateLabel,
  vehicleClassLabel,
} from '../assets/AssetFilter';
import type { SceneAsset } from '../assets/sceneFrame';
import type { ExternalTrackState } from '../assets/types';
import {
  ComponentHealthStatus,
  TrackClassification,
  TrackSourceKind,
} from '../assets/types';

export type RosterKind = 'asset' | 'track';

export interface RosterSelection {
  readonly kind: RosterKind;
  readonly id: string;
}

/** The one immutable boundary consumed by the roster's visual reconciliation. */
export interface RosterInput {
  readonly assets: readonly SceneAsset[];
  readonly contacts: readonly ExternalTrackState[];
  readonly assetFilter: FilterSelection;
  readonly query: string;
  readonly selected: RosterSelection | null;
}

export interface RosterCounts {
  readonly assetsMatching: number;
  readonly contactsMatching: number;
}

export interface AssetRosterOptions {
  readonly mount: HTMLElement;
  readonly selectAsset: (id: string) => void;
  readonly selectTrack: (id: string) => void;
  readonly onQueryChange: (query: string) => void;
  readonly onClearFilters: () => void;
  readonly onFocusFallback?: () => void;
  readonly scheduleFrame?: (callback: () => void) => number;
  readonly cancelFrame?: (handle: number) => void;
}

interface RowParts {
  readonly key: string;
  readonly item: HTMLLIElement;
  readonly button: HTMLButtonElement;
  readonly domain: HTMLSpanElement;
  readonly name: HTMLSpanElement;
  readonly identifier: HTMLSpanElement;
  readonly state: HTMLSpanElement;
  readonly health: HTMLSpanElement;
  readonly outside: HTMLSpanElement;
}

function rosterKey(kind: RosterKind, id: string): string {
  return `${kind}:${id}`;
}

function normalized(query: string): string {
  return query.trim().toLowerCase();
}

function includesQuery(values: readonly (string | null | undefined)[], query: string): boolean {
  if (query === '') return true;
  return values.some(value => value?.toLowerCase().includes(query) === true);
}

export function assetMatchesRosterQuery(asset: SceneAsset, query: string): boolean {
  const descriptor = asset.descriptor;
  const state = asset.state;
  return includesQuery([
    descriptor.assetId,
    descriptor.displayName,
    descriptor.vendor,
    vehicleClassLabel(descriptor.vehicleClass),
    descriptor.agencyId,
    descriptor.fleetId,
    operationalStateLabel(state.operationalState),
  ], normalized(query));
}

export function contactMatchesRosterQuery(contact: ExternalTrackState, query: string): boolean {
  const values: Array<string | null> = [
    contact.trackId,
    enumLabel(TrackClassification, contact.classification),
  ];
  for (const source of contact.sources) {
    values.push(source.sourceId, enumLabel(TrackSourceKind, source.kind));
  }
  return includesQuery(values, normalized(query));
}

function healthWarning(asset: SceneAsset): string | null {
  const health = asset.state.health;
  const warned = health.overall !== ComponentHealthStatus.Nominal
    && health.overall !== ComponentHealthStatus.NotPresent;
  if (!warned && health.faults.length === 0) return null;
  return health.summary.trim() || enumLabel(ComponentHealthStatus, health.overall);
}

function isAccessible(element: HTMLElement): boolean {
  for (let current: HTMLElement | null = element; current; current = current.parentElement) {
    if (current.hidden || current.hasAttribute('inert')
      || current.getAttribute('aria-hidden') === 'true') return false;
  }
  return true;
}

function patchText(element: HTMLElement, text: string): void {
  if (element.textContent !== text) element.textContent = text;
}

function patchHidden(element: HTMLElement, hidden: boolean): void {
  if (element.hidden !== hidden) element.hidden = hidden;
}

function patchAttribute(element: HTMLElement, name: string, value: string): void {
  if (element.getAttribute(name) !== value) element.setAttribute(name, value);
}

function patchClass(element: HTMLElement, name: string, on: boolean): void {
  if (element.classList.contains(name) !== on) element.classList.toggle(name, on);
}

/** Keyed, frame-coalesced roster for simulated assets and observed contacts. */
export class AssetRoster {
  private readonly _root: HTMLElement;
  private readonly _doc: Document;
  private readonly _search: HTMLInputElement;
  private readonly _scroll: HTMLElement;
  private readonly _assetCount: HTMLElement;
  private readonly _contactCount: HTMLElement;
  private readonly _assetList: HTMLUListElement;
  private readonly _contactList: HTMLUListElement;
  private readonly _assetEmpty: HTMLElement;
  private readonly _contactEmpty: HTMLElement;
  private readonly _rows = new Map<string, RowParts>();
  private readonly _options: AssetRosterOptions;
  private readonly _scheduleFrame: (callback: () => void) => number;
  private readonly _cancelFrame: (handle: number) => void;

  private _pending: RosterInput | null = null;
  private _frameHandle: number | null = null;
  private _disposed = false;
  private _counts: RosterCounts = { assetsMatching: 0, contactsMatching: 0 };

  constructor(options: AssetRosterOptions) {
    this._options = options;
    this._doc = options.mount.ownerDocument;
    const view = options.mount.ownerDocument.defaultView;
    this._scheduleFrame = options.scheduleFrame ?? ((callback) => {
      if (view?.requestAnimationFrame) return view.requestAnimationFrame(() => callback());
      return view?.setTimeout(callback, 0) ?? 0;
    });
    this._cancelFrame = options.cancelFrame ?? ((handle) => {
      if (view?.cancelAnimationFrame) view.cancelAnimationFrame(handle);
      else view?.clearTimeout(handle);
    });

    this._root = this._doc.createElement('section');
    this._root.className = 'asset-roster';
    this._root.setAttribute('aria-label', 'Fleet roster');

    const searchLabel = this._doc.createElement('label');
    searchLabel.className = 'ar-search-label';
    const searchText = this._doc.createElement('span');
    searchText.textContent = 'Search fleet';
    this._search = this._doc.createElement('input');
    this._search.type = 'search';
    this._search.className = 'ar-search';
    this._search.autocomplete = 'off';
    this._search.addEventListener('input', () => this._options.onQueryChange(this._search.value));
    searchLabel.append(searchText, this._search);

    this._scroll = this._doc.createElement('div');
    this._scroll.className = 'ar-scroll';

    const assets = this._createGroup('Assets');
    this._assetCount = assets.count;
    this._assetList = assets.list;
    this._assetEmpty = this._doc.createElement('div');
    this._assetEmpty.className = 'ar-empty';
    this._assetEmpty.hidden = true;
    const assetEmptyText = this._doc.createElement('p');
    assetEmptyText.textContent = 'No matching assets';
    const clear = this._doc.createElement('button');
    clear.type = 'button';
    clear.className = 'btn ar-clear';
    clear.dataset['action'] = 'clear-filters';
    clear.textContent = 'Clear filters';
    clear.addEventListener('click', () => this._options.onClearFilters());
    this._assetEmpty.append(assetEmptyText, clear);
    assets.section.appendChild(this._assetEmpty);

    const contacts = this._createGroup('Observed contacts');
    this._contactCount = contacts.count;
    this._contactList = contacts.list;
    this._contactEmpty = this._doc.createElement('p');
    this._contactEmpty.className = 'ar-empty';
    this._contactEmpty.textContent = 'No matching contacts';
    this._contactEmpty.hidden = true;
    contacts.section.appendChild(this._contactEmpty);

    this._scroll.append(assets.section, contacts.section);
    this._root.append(searchLabel, this._scroll);
    options.mount.appendChild(this._root);
  }

  get element(): HTMLElement {
    return this._root;
  }

  get counts(): RosterCounts {
    return this._counts;
  }

  rowFor(kind: RosterKind, id: string): HTMLButtonElement | null {
    return this._rows.get(rosterKey(kind, id))?.button ?? null;
  }

  focusRow(kind: RosterKind, id: string): boolean {
    const parts = this._rows.get(rosterKey(kind, id));
    const row = parts?.button ?? null;
    if (!parts || !row || !parts.outside.hidden || !row.isConnected || !isAccessible(row)) return false;
    row.focus();
    return row.ownerDocument.activeElement === row;
  }

  update(input: RosterInput): void {
    if (this._disposed) return;
    this._pending = input;
    if (this._frameHandle !== null) return;
    let ranSynchronously = false;
    const handle = this._scheduleFrame(() => {
      ranSynchronously = true;
      this._frameHandle = null;
      const pending = this._pending;
      this._pending = null;
      if (!this._disposed && pending) this._render(pending);
    });
    if (!ranSynchronously) this._frameHandle = handle;
  }

  dispose(): void {
    if (this._disposed) return;
    this._disposed = true;
    if (this._frameHandle !== null) this._cancelFrame(this._frameHandle);
    this._frameHandle = null;
    this._pending = null;
    this._rows.clear();
    this._root.remove();
  }

  private _createGroup(label: string): {
    section: HTMLElement;
    count: HTMLElement;
    list: HTMLUListElement;
  } {
    const section = this._doc.createElement('section');
    section.className = 'ar-group';
    const heading = this._doc.createElement('h3');
    heading.className = 'ar-group-heading';
    const title = this._doc.createElement('span');
    title.textContent = label;
    const count = this._doc.createElement('span');
    count.className = 'ar-count';
    heading.append(title, count);
    const list = this._doc.createElement('ul');
    list.className = 'ar-list';
    section.append(heading, list);
    return { section, count, list };
  }

  private _render(input: RosterInput): void {
    const active = this._root.ownerDocument.activeElement;
    const activeKey = active instanceof HTMLElement
      ? active.closest<HTMLElement>('[data-roster-key]')?.dataset['rosterKey'] ?? null
      : null;
    const scrollTop = this._scroll.scrollTop;
    if (this._search.value !== input.query) this._search.value = input.query;

    const live = new Set<string>();
    const assetMatches = new Map<string, boolean>();
    const contactMatches = new Map<string, boolean>();

    for (const asset of input.assets) {
      const key = rosterKey('asset', asset.view.id);
      live.add(key);
      const matches = matchesFilter(filterableFromV2(asset.descriptor, asset.state), input.assetFilter)
        && assetMatchesRosterQuery(asset, input.query);
      assetMatches.set(key, matches);
      this._renderAssetRow(asset, input.selected, matches);
    }
    for (const contact of input.contacts) {
      const key = rosterKey('track', contact.trackId);
      live.add(key);
      const matches = contactMatchesRosterQuery(contact, input.query);
      contactMatches.set(key, matches);
      this._renderContactRow(contact, input.selected, matches);
    }

    for (const [key, parts] of this._rows) {
      if (live.has(key)) continue;
      parts.item.remove();
      this._rows.delete(key);
    }

    const orderedAssets = this._orderedKeys('asset', input.assets.map(asset => asset.view.id), input.selected, assetMatches);
    const orderedContacts = this._orderedKeys('track', input.contacts.map(contact => contact.trackId), input.selected, contactMatches);
    this._syncOrder(this._assetList, orderedAssets);
    this._syncOrder(this._contactList, orderedContacts);

    let assetsMatching = 0;
    let contactsMatching = 0;
    for (const matches of assetMatches.values()) if (matches) assetsMatching += 1;
    for (const matches of contactMatches.values()) if (matches) contactsMatching += 1;
    this._counts = { assetsMatching, contactsMatching };
    patchText(this._assetCount, `${assetsMatching} matching`);
    patchText(this._contactCount, `${contactsMatching} matching`);
    patchHidden(this._assetEmpty, assetsMatching > 0);
    patchHidden(this._contactEmpty, contactsMatching > 0);
    this._scroll.scrollTop = scrollTop;

    if (activeKey) {
      const activeRow = this._rows.get(activeKey)?.button ?? null;
      if (!activeRow || activeRow.hidden || !isAccessible(activeRow)) {
        this._options.onFocusFallback?.();
      }
    }
  }

  private _renderAssetRow(
    asset: SceneAsset,
    selected: RosterSelection | null,
    matches: boolean,
  ): void {
    const id = asset.view.id;
    const parts = this._row('asset', id, this._assetList, () => this._options.selectAsset(id));
    const isSelected = selected !== null && selected.kind === 'asset' && selected.id === id;
    const outside = isSelected && !matches;
    patchHidden(parts.item, !matches && !outside);
    patchHidden(parts.button, parts.item.hidden === true);
    patchClass(parts.button, 'is-selected', isSelected);
    patchAttribute(parts.button, 'aria-current', isSelected ? 'true' : 'false');
    patchText(parts.domain, domainLabel(asset.view.domain));
    const displayName = asset.descriptor.displayName.trim();
    patchText(parts.name, displayName && displayName !== id
      ? displayName
      : vehicleClassLabel(asset.descriptor.vehicleClass));
    patchText(parts.identifier, id);
    patchText(parts.state, operationalStateLabel(asset.state.operationalState));
    const health = healthWarning(asset);
    patchText(parts.health, health ?? '');
    patchHidden(parts.health, health === null);
    patchHidden(parts.outside, !outside);
  }

  private _renderContactRow(
    contact: ExternalTrackState,
    selected: RosterSelection | null,
    matches: boolean,
  ): void {
    const id = contact.trackId;
    const parts = this._row('track', id, this._contactList, () => this._options.selectTrack(id));
    const isSelected = selected !== null && selected.kind === 'track' && selected.id === id;
    const outside = isSelected && !matches;
    patchHidden(parts.item, !matches && !outside);
    patchHidden(parts.button, parts.item.hidden === true);
    patchClass(parts.button, 'is-selected', isSelected);
    patchAttribute(parts.button, 'aria-current', isSelected ? 'true' : 'false');
    patchText(parts.domain, 'Contact');
    patchText(parts.name, contact.label?.trim() || enumLabel(TrackClassification, contact.classification));
    patchText(parts.identifier, id);
    patchText(parts.state, enumLabel(TrackClassification, contact.classification));
    patchText(parts.health, contact.sources[0]?.sourceId ?? '');
    patchHidden(parts.health, contact.sources.length === 0);
    patchHidden(parts.outside, !outside);
  }

  private _row(
    kind: RosterKind,
    id: string,
    host: HTMLUListElement,
    select: () => void,
  ): RowParts {
    const key = rosterKey(kind, id);
    const held = this._rows.get(key);
    if (held) return held;

    const item = this._doc.createElement('li');
    item.className = 'ar-item';
    const button = this._doc.createElement('button');
    button.type = 'button';
    button.className = 'ar-row';
    button.dataset['rosterKind'] = kind;
    button.dataset['rosterKey'] = key;
    const domain = this._doc.createElement('span');
    domain.className = 'ar-domain';
    const name = this._doc.createElement('span');
    name.className = 'ar-name';
    const identifier = this._doc.createElement('span');
    identifier.className = 'ar-id';
    const state = this._doc.createElement('span');
    state.className = 'ar-state';
    const health = this._doc.createElement('span');
    health.className = 'ar-health';
    const outside = this._doc.createElement('span');
    outside.className = 'ar-outside';
    outside.textContent = 'Outside filters';
    outside.hidden = true;
    button.append(domain, name, identifier, state, health, outside);
    button.addEventListener('click', select);
    item.appendChild(button);
    host.appendChild(item);
    const parts = { key, item, button, domain, name, identifier, state, health, outside };
    this._rows.set(key, parts);
    return parts;
  }

  private _orderedKeys(
    kind: RosterKind,
    ids: readonly string[],
    selected: RosterSelection | null,
    matches: ReadonlyMap<string, boolean>,
  ): string[] {
    const keys = ids.map(id => rosterKey(kind, id));
    const outside = selected?.kind === kind
      ? rosterKey(kind, selected.id)
      : null;
    const pinned = outside && matches.has(outside) && matches.get(outside) === false ? [outside] : [];
    return [
      ...pinned,
      ...keys.filter(key => matches.get(key) === true),
      ...keys.filter(key => matches.get(key) === false && key !== outside),
    ];
  }

  private _syncOrder(host: HTMLUListElement, keys: readonly string[]): void {
    keys.forEach((key, index) => {
      const item = this._rows.get(key)?.item;
      if (!item) return;
      const at = host.children.item(index);
      if (at !== item) host.insertBefore(item, at);
    });
  }
}
