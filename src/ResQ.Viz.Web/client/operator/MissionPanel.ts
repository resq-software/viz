// ResQ Viz - authoritative mission and transport card
// SPDX-License-Identifier: Apache-2.0

import type { ApiFailure } from '../api';
import type { ResourceState } from './ConsoleResources';
import type { ScenarioCatalogResponse } from './types';
import type { MissionBaseKind, MissionView } from './ScenarioRuntime';
import { scenarioPresentation } from './scenarioPresentation';

export { ScenarioCatalogLauncher } from './ScenarioCatalogLauncher';

export interface MissionTransportView {
  readonly paused: boolean;
  readonly speed: number;
  readonly simulationTimeSeconds: number;
}

/** Allows focused panel fixtures to omit runtime-only revision metadata. */
export interface MissionDisplayState {
  readonly kind: MissionView['kind'];
  readonly baseKind?: MissionBaseKind;
  readonly name?: string | null;
  readonly startedAtSimulationSeconds?: number;
  readonly revision?: number;
  readonly pendingName: string | null;
  readonly pendingKind?: 'scenario' | 'reset';
  readonly requestStage?: 'requesting' | 'accepted';
}

export interface MissionPanelState {
  readonly mission: MissionDisplayState;
  readonly transport: MissionTransportView;
  readonly catalog: ResourceState<ScenarioCatalogResponse>;
}

export interface MissionPanelOptions {
  readonly mount: HTMLElement;
  /** Desired authoritative pause state. */
  readonly onTogglePause: (paused: boolean) => void | Promise<void>;
  readonly onReset: () => void | Promise<void>;
  readonly onChange: () => void;
  readonly onRetryCatalog: () => void | Promise<void>;
}

interface MissionElements {
  readonly title: HTMLElement;
  readonly runState: HTMLElement;
  readonly elapsed: HTMLElement;
  readonly speed: HTMLElement;
  readonly pending: HTMLElement;
  readonly catalogStatus: HTMLElement;
  readonly pause: HTMLButtonElement;
  readonly reset: HTMLButtonElement;
  readonly change: HTMLButtonElement;
  readonly retryCatalog: HTMLButtonElement;
}

/** A stable DOM view over streamed mission state; it owns no fetching or mutations. */
export class MissionPanel {
  private readonly _elements: MissionElements;
  private _paused = false;
  private _catalogFailure: string | null = null;
  private _scenarioBrowserFailure: string | null = null;

  constructor(private readonly _options: MissionPanelOptions) {
    this._elements = this._build(_options.mount);
    this._elements.pause.addEventListener('click', () => {
      void this._options.onTogglePause(!this._paused);
    });
    this._elements.reset.addEventListener('click', () => {
      void this._options.onReset();
    });
    this._elements.change.addEventListener('click', () => this._options.onChange());
    this._elements.retryCatalog.addEventListener('click', () => {
      void this._options.onRetryCatalog();
    });
  }

  /** Stable trigger used as the scenario modal's focus-return target. */
  get changeTrigger(): HTMLButtonElement {
    return this._elements.change;
  }

  /** Shows or clears a lazy-chunk failure without conflating it with catalog data. */
  setScenarioBrowserFailure(message: string | null): void {
    this._scenarioBrowserFailure = message;
    this._renderCatalogFailure();
  }

  render(state: MissionPanelState): void {
    const { mission, transport, catalog } = state;
    this._paused = transport.paused;

    const activeName = mission.name ?? null;
    setText(this._elements.title, missionTitle(mission, activeName));
    setText(this._elements.runState, transport.paused ? 'Paused' : 'Running');
    setText(this._elements.pause, transport.paused ? 'Resume' : 'Pause');
    setAttribute(
      this._elements.pause,
      'aria-label',
      transport.paused ? 'Resume simulation' : 'Pause simulation',
    );
    const startedAt = mission.startedAtSimulationSeconds ?? 0;
    const elapsed = Math.max(0, transport.simulationTimeSeconds - startedAt);
    setText(this._elements.elapsed, `${elapsed.toFixed(1)}s`);
    setText(this._elements.speed, `${transport.speed}×`);

    const pending = pendingText(mission);
    if (pending === '') {
      setHidden(this._elements.pending, true);
      setText(this._elements.pending, '');
    } else {
      // Put the live region in the accessibility tree before its text changes.
      setHidden(this._elements.pending, false);
      setText(this._elements.pending, pending);
    }

    const catalogReady = catalog.status === 'ready';
    const destructivePending = mission.kind === 'pending';
    setDisabled(this._elements.change, !catalogReady || destructivePending);
    setDisabled(this._elements.reset, destructivePending);
    const catalogError = catalog.status === 'error';
    this._catalogFailure = catalogError ? failureText(catalog.failure) : null;
    this._renderCatalogFailure();
    setAttribute(this._options.mount, 'aria-busy', String(catalog.status === 'loading'));
  }

  private _renderCatalogFailure(): void {
    const message = this._catalogFailure ?? this._scenarioBrowserFailure;
    setHidden(this._elements.catalogStatus, message === null);
    setText(this._elements.catalogStatus, message ?? '');
    setHidden(this._elements.retryCatalog, this._catalogFailure === null);
    setText(
      this._elements.change,
      this._scenarioBrowserFailure === null ? 'Change…' : 'Retry scenario browser',
    );
  }

  private _build(mount: HTMLElement): MissionElements {
    const kicker = document.createElement('span');
    kicker.className = 'operator-section-kicker';
    kicker.textContent = 'Mission';

    const title = document.createElement('strong');
    title.className = 'operator-mission-title';

    const meta = document.createElement('div');
    meta.className = 'operator-mission-meta';
    const runState = document.createElement('span');
    runState.className = 'operator-mission-state';
    const elapsed = document.createElement('span');
    elapsed.className = 'operator-mission-value';
    const speed = document.createElement('span');
    speed.className = 'operator-mission-value';
    meta.append(runState, elapsed, speed);

    const pending = document.createElement('p');
    pending.className = 'operator-mission-pending';
    pending.setAttribute('role', 'status');
    pending.setAttribute('aria-live', 'polite');

    const catalogStatus = document.createElement('p');
    catalogStatus.className = 'operator-resource-error';
    catalogStatus.setAttribute('role', 'alert');

    const actions = document.createElement('div');
    actions.className = 'operator-mission-actions';
    const pause = action('pause', 'Pause');
    const reset = action('reset', 'Reset');
    reset.classList.add('btn-danger');
    const change = action('change', 'Change…');
    const retryCatalog = action('retry-catalog', 'Retry catalog');
    actions.append(pause, reset, change, retryCatalog);

    mount.replaceChildren(kicker, title, meta, pending, catalogStatus, actions);
    return { title, runState, elapsed, speed, pending, catalogStatus, pause, reset, change, retryCatalog };
  }
}

function action(name: string, label: string): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'btn';
  button.dataset['action'] = name;
  button.textContent = label;
  return button;
}

function missionTitle(mission: MissionDisplayState, activeName: string | null): string {
  const baseKind = mission.kind === 'pending'
    ? (mission.baseKind ?? (activeName === null ? 'none' : 'active'))
    : mission.kind;
  if (baseKind === 'unknown') return 'Waiting for authoritative scenario state';
  if (baseKind === 'none') return 'No active mission';
  if (baseKind === 'custom') return 'Custom session';
  if (activeName !== null) return scenarioPresentation(activeName).displayName;
  return 'No active mission';
}

function pendingText(mission: MissionDisplayState): string {
  if (mission.kind !== 'pending') return '';
  if (mission.requestStage === 'requesting') {
    return mission.pendingKind === 'reset'
      ? 'Requesting mission reset…'
      : `Requesting ${scenarioPresentation(mission.pendingName ?? '').displayName}…`;
  }
  if (mission.pendingKind === 'reset') return 'Resetting mission — awaiting published state';
  return mission.pendingName === null
    ? ''
    : `Starting ${scenarioPresentation(mission.pendingName).displayName} — awaiting published state`;
}

function failureText(failure: ApiFailure): string {
  return failure.kind === 'problem'
    ? `${failure.problem.reasonCode ?? failure.problem.code} · ${failure.problem.detail}`
    : failure.message;
}

function setText(element: Node, value: string): void {
  if (element.textContent !== value) element.textContent = value;
}

function setHidden(element: HTMLElement, hidden: boolean): void {
  if (element.hidden !== hidden) element.hidden = hidden;
}

function setAttribute(element: Element, name: string, value: string): void {
  if (element.getAttribute(name) !== value) element.setAttribute(name, value);
}

function setDisabled(button: HTMLButtonElement, disabled: boolean): void {
  if (button.disabled !== disabled) button.disabled = disabled;
  setAttribute(button, 'aria-disabled', String(disabled));
}
