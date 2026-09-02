// ResQ Viz - searchable scenario catalog modal
// SPDX-License-Identifier: Apache-2.0

import '../styles/operator-dialogs.css';

import type { ApiFailure, Result } from '../api';
import type { ScenarioPresentation } from './scenarioPresentation';
import type {
  ScenarioCatalogResponse,
  ScenarioReplacementContext,
  ScenarioStartResponse,
  ScenarioSummary,
} from './types';

const CATEGORY_ORDER = ['Exercise', 'Response', 'Mixed domain', 'Safety', 'Scale', 'Other'] as const;

export interface ScenarioCatalogSession extends ScenarioReplacementContext {
  readonly activeName: string | null;
}

export interface ScenarioCatalogOptions {
  readonly mount: HTMLElement;
  readonly trigger: HTMLButtonElement;
  readonly scenarios: ScenarioCatalogResponse;
  readonly presentation: (name: string) => ScenarioPresentation;
  readonly getSession: () => ScenarioCatalogSession;
  readonly startScenario: (
    name: string,
  ) => Promise<Result<ScenarioStartResponse, ApiFailure>>;
  readonly confirmReplace: (name: string) => boolean;
  readonly fallbackFocus?: HTMLElement;
  readonly onClose?: () => void;
}

/** Modal view over the server catalog; streamed state remains authoritative. */
export class ScenarioCatalog {
  private readonly _dialog: HTMLDialogElement;
  private readonly _search: HTMLInputElement;
  private readonly _groups: HTMLElement;
  private readonly _error: HTMLElement;
  private readonly _options: ScenarioCatalogOptions;
  private _requestInFlight = false;
  private _generation = 0;

  constructor(options: ScenarioCatalogOptions) {
    this._options = options;
    const built = this._build();
    this._dialog = built.dialog;
    this._search = built.search;
    this._groups = built.groups;
    this._error = built.error;
    options.mount.appendChild(this._dialog);
    options.trigger.setAttribute('aria-haspopup', 'dialog');
    options.trigger.setAttribute('aria-controls', this._dialog.id);
    options.trigger.setAttribute('aria-expanded', 'false');
    this._dialog.setAttribute('aria-busy', 'false');
    this._search.addEventListener('input', () => this._render());
    built.close.addEventListener('click', () => this.close());
    this._dialog.addEventListener('keydown', event => this._onKeyDown(event));
    this._dialog.addEventListener('cancel', event => {
      event.preventDefault();
    });
    this._render();
  }

  get isOpen(): boolean {
    return this._dialog.open;
  }

  open(): void {
    if (!this._dialog.open) this._dialog.showModal();
    this._options.trigger.setAttribute('aria-expanded', 'true');
    this.refreshSession();
    this._search.focus();
  }

  /** Patches only the streamed active marker; search results and focus stay intact. */
  refreshSession(): void {
    const activeName = this._options.getSession().activeName;
    for (const button of this._groups.querySelectorAll<HTMLButtonElement>('[data-scenario]')) {
      const active = button.dataset['scenario'] === activeName;
      if (active) button.setAttribute('aria-current', 'true');
      else button.removeAttribute('aria-current');
      const existing = button.querySelector<HTMLElement>('.scenario-catalog-current');
      if (active && existing === null) {
        const current = document.createElement('span');
        current.className = 'scenario-catalog-current';
        current.textContent = 'Current';
        button.appendChild(current);
      } else if (!active) {
        existing?.remove();
      }
    }
  }

  /** OperatorModalHost refresh seam. */
  refresh(): void {
    this.refreshSession();
  }

  close(): void {
    const wasOpen = this._dialog.open;
    this._generation++;
    if (wasOpen) this._dialog.close();
    if (!wasOpen) return;
    this._options.trigger.setAttribute('aria-expanded', 'false');
    const target = usableFocusTarget(this._options.trigger)
      ? this._options.trigger
      : this._options.fallbackFocus;
    if (target && usableFocusTarget(target)) target.focus();
    this._options.onClose?.();
  }

  /** Retires this modal generation so late work cannot repaint a newer surface. */
  invalidate(): void {
    this.close();
  }

  private _render(): void {
    const query = this._search.value.trim().toLocaleLowerCase();
    const groups = new Map<string, Array<{
      readonly summary: ScenarioSummary;
      readonly copy: ScenarioPresentation;
    }>>();
    for (const summary of this._options.scenarios.scenarios) {
      const copy = this._options.presentation(summary.name);
      const haystack = `${summary.name} ${copy.displayName} ${copy.category} ${copy.purpose}`
        .toLocaleLowerCase();
      if (query !== '' && !haystack.includes(query)) continue;
      const members = groups.get(copy.category) ?? [];
      members.push({ summary, copy });
      groups.set(copy.category, members);
    }

    const fragment = document.createDocumentFragment();
    const orderedGroups = [...groups].sort(([left], [right]) =>
      categoryRank(left) - categoryRank(right));
    for (const [category, members] of orderedGroups) {
      const section = document.createElement('section');
      section.className = 'scenario-catalog-group';
      const heading = document.createElement('h3');
      heading.textContent = category;
      section.appendChild(heading);
      for (const member of members) section.appendChild(this._card(member.summary, member.copy));
      fragment.appendChild(section);
    }
    if (groups.size === 0) {
      const empty = document.createElement('p');
      empty.className = 'operator-dialog-empty';
      empty.textContent = 'No matching scenarios';
      fragment.appendChild(empty);
    }
    this._groups.replaceChildren(fragment);
  }

  private _card(summary: ScenarioSummary, copy: ScenarioPresentation): HTMLButtonElement {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'scenario-catalog-card';
    button.dataset['scenario'] = summary.name;
    const title = document.createElement('strong');
    title.textContent = copy.displayName;
    const purpose = document.createElement('span');
    purpose.textContent = copy.purpose;
    const counts = document.createElement('span');
    const { air, ground, surface } = summary.domainCounts;
    counts.textContent = `${summary.assetCount} ${summary.assetCount === 1 ? 'asset' : 'assets'} · `
      + `${air} Air · ${ground} Ground · ${surface} Surface`;
    button.append(title, purpose, counts);
    if (copy.environment !== null) {
      const environment = document.createElement('span');
      environment.textContent = `Environment · ${copy.environment}`;
      button.appendChild(environment);
    }
    button.addEventListener('click', () => { void this._start(summary.name); });
    if (this._options.getSession().activeName === summary.name) {
      button.setAttribute('aria-current', 'true');
      const current = document.createElement('span');
      current.className = 'scenario-catalog-current';
      current.textContent = 'Current';
      button.appendChild(current);
    }
    return button;
  }

  private async _start(name: string): Promise<void> {
    if (this._requestInFlight) return;
    const session = this._options.getSession();
    if ((session.assetCount > 0 || session.tick > 0)
      && !this._options.confirmReplace(name)) return;

    const generation = this._generation;
    this._requestInFlight = true;
    this._showError(null);
    this._setBusy(true);
    let result: Result<ScenarioStartResponse, ApiFailure>;
    try {
      result = await this._options.startScenario(name);
    } catch (error: unknown) {
      result = {
        success: false,
        error: {
          kind: 'network',
          message: error instanceof Error ? error.message : String(error),
        },
      };
    }
    this._requestInFlight = false;
    if (generation !== this._generation) {
      this._setBusy(false);
      if (this._dialog.open) this.refreshSession();
      return;
    }
    this._setBusy(false);
    if (result.success) {
      this.close();
      return;
    }
    this._showError(result.error);
  }

  private _setBusy(busy: boolean): void {
    this._dialog.setAttribute('aria-busy', String(busy));
    for (const button of this._dialog.querySelectorAll<HTMLButtonElement>('.scenario-catalog-card')) {
      button.disabled = busy;
    }
  }

  private _showError(failure: ApiFailure | null): void {
    this._error.hidden = failure === null;
    this._error.textContent = failure === null
      ? ''
      : failure.kind === 'problem'
        ? `${failure.problem.reasonCode ?? failure.problem.code} · ${failure.problem.detail}`
        : failure.message;
  }

  private _onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return;
      event.preventDefault();
      event.stopPropagation();
      this.close();
      return;
    }
    if (event.key !== 'Tab' || event.ctrlKey || event.metaKey || event.altKey) return;
    const focusable = Array.from(this._dialog.querySelectorAll<HTMLElement>(
      'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), '
      + 'a[href], [tabindex]:not([tabindex="-1"])',
    )).filter(element => !element.hidden && element.closest('[hidden], [inert]') === null);
    if (focusable.length === 0) {
      event.preventDefault();
      this._dialog.focus();
      return;
    }
    const first = focusable[0]!;
    const last = focusable[focusable.length - 1]!;
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  }

  private _build(): {
    readonly dialog: HTMLDialogElement;
    readonly search: HTMLInputElement;
    readonly groups: HTMLElement;
    readonly error: HTMLElement;
    readonly close: HTMLButtonElement;
  } {
    const dialog = document.createElement('dialog');
    dialog.id = 'operator-scenario-catalog';
    dialog.className = 'operator-dialog scenario-catalog';
    const title = document.createElement('h2');
    title.id = 'scenario-catalog-title';
    title.textContent = 'Change scenario';
    dialog.setAttribute('aria-labelledby', title.id);
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'operator-dialog-close';
    close.setAttribute('aria-label', 'Close scenario catalog');
    close.textContent = '×';
    const label = document.createElement('label');
    label.className = 'operator-dialog-search';
    label.textContent = 'Search scenarios';
    const search = document.createElement('input');
    search.type = 'search';
    search.autocomplete = 'off';
    label.appendChild(search);
    const groups = document.createElement('div');
    groups.className = 'scenario-catalog-groups';
    const error = document.createElement('p');
    error.className = 'operator-dialog-error';
    error.setAttribute('role', 'alert');
    error.hidden = true;
    dialog.append(title, close, label, error, groups);
    return { dialog, search, groups, error, close };
  }
}

function usableFocusTarget(target: HTMLElement): boolean {
  if (!target.isConnected || target.matches(':disabled')
    || target.getAttribute('aria-disabled')?.toLocaleLowerCase() === 'true'
    || target.closest('[hidden], [inert]') !== null) return false;

  const view = target.ownerDocument.defaultView;
  if (view === null) return true;
  for (let current: HTMLElement | null = target; current !== null; current = current.parentElement) {
    if (current.getAttribute('aria-hidden')?.toLocaleLowerCase() === 'true') return false;
    const style = view.getComputedStyle(current);
    if (style.display === 'none' || style.visibility === 'hidden'
      || style.visibility === 'collapse' || style.contentVisibility === 'hidden') return false;
  }
  return true;
}

function categoryRank(category: string): number {
  const index = CATEGORY_ORDER.indexOf(category as (typeof CATEGORY_ORDER)[number]);
  return index < 0 ? CATEGORY_ORDER.length - 1 : index;
}
