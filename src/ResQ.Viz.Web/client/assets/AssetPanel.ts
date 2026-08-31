// ResQ Viz - capability-driven asset detail panel
// SPDX-License-Identifier: Apache-2.0
//
// Replaces `../ui/dronePanel.ts`, whose three buttons were hardcoded in the page
// and issued to whatever happened to be selected. That is the exact shape of bug
// the server side of this stack spent five fixes removing — a command advertised
// and then refused — and this panel closes it from the client side.
//
// The contract, in one line each:
//
//   * a command the asset cannot accept is ABSENT: controls are generated from
//     `GET /api/v2/sim/assets/{id}/capabilities`, never from a table in here;
//   * a command it cannot accept right now is DISABLED WITH A REASON, decided by
//     `evaluateCommand` in `./panelCommands`, which mirrors the validator's
//     remaining gates;
//   * an external track gets no command surface at all — it is an observed
//     contact, and there is nothing to bind an affordance to.
//
// Rendering is a keyed diff, not a rebuild. `render` runs at frame rate, and a
// rebuilt footer would drop focus out of whichever control a keyboard operator was
// on ten times a second.
//
// The module pulls in no Three.js and holds no scene references, so a host may
// import it eagerly or behind a dynamic `import()` without any change here.

import '../styles/assets.css';

import { getLogger } from '../log';
import { prefersReducedMotion } from '../reducedMotion';
import { formatAge } from './assetView';
import type { AssetView } from './assetView';
import { domainLabel, enumLabel, humanise, operationalStateLabel } from './AssetFilter';
import { buildAssetCards, buildTrackCards } from './panelCards';
import type { PanelCard } from './panelCards';
import {
  DESTRUCTIVE_COMMANDS,
  PARAMETER_SPECS,
  VERTICAL_REFERENCES,
  evaluateCommand,
  loadAssetCapabilities,
  newIdempotencyKey,
  pointTarget,
  postAssetCommand,
  surfaceElevationUnderAssetM,
  targetForAsset,
} from './panelCommands';
import type {
  AssetCapabilitiesReport,
  AssetCommandCapability,
  AssetCommandRequestBody,
  CommandAvailability,
  CommandContext,
  CommandIssuer,
  ParameterBoundsContext,
  TargetPicker,
} from './panelCommands';
import type { AssetDescriptor, AssetState, ExternalTrackState, MotionConstraints } from './types';
import { DataFreshness, TrackClassification } from './types';

const log = getLogger('assetPanel');

/** What the panel is showing: a controllable asset, or an observed contact. */
export type PanelSubject =
  | {
    readonly kind: 'asset';
    readonly view: AssetView;
    /** Descriptor when one is held; null on the v1 stream, which has none. */
    readonly descriptor?: AssetDescriptor | null;
    /** Full v2 state when one is held; null on the v1 stream. */
    readonly state?: AssetState | null;
  }
  | { readonly kind: 'track'; readonly track: ExternalTrackState };

/** Construction options. Every collaborator is injectable, so the panel can be
 *  driven with no server and no scene in a test. */
export interface AssetPanelOptions {
  readonly mount?: HTMLElement;
  readonly issueCommand?: CommandIssuer;
  readonly loadCapabilities?: (assetId: string) => Promise<AssetCapabilitiesReport | null>;
  /** Supplied when the host can turn a gesture into a scene point. Absent means
   *  target-taking commands are disabled *with that reason*, never hidden: the
   *  asset accepts them, this client just cannot aim them yet. */
  readonly pickTarget?: TargetPicker | null;
  /** First delay before a failed capability fetch is retried, in milliseconds.
   *  Doubles per consecutive failure up to {@link CAPABILITY_RETRY_CEILING}.
   *  Injectable so a test can drive the recovery without a real wait. */
  readonly capabilityRetryMs?: number;
}

/** Default first retry delay for a failed capability fetch. */
export const CAPABILITY_RETRY_MS = 5_000;

/** Longest a retry is ever deferred. The panel keeps trying — an operator who
 *  leaves an asset selected through an outage should find its commands back
 *  without touching anything — but at a cadence that cannot become a load. */
export const CAPABILITY_RETRY_CEILING_MS = 60_000;

/** What is known about the selected asset's capability report.
 *
 *  `failed` exists precisely because it is not `ready` with nothing in it. An
 *  asset that declares no commands and a fetch that never answered look identical
 *  in a `report | null`, and collapsing them is what made one dropped request
 *  permanent for the rest of the session. */
type ReportStatus = 'idle' | 'pending' | 'ready' | 'failed';

interface CardParts {
  readonly section: HTMLElement;
  readonly title: HTMLHeadingElement;
  readonly list: HTMLElement;
  readonly note: HTMLParagraphElement;
  rowSignature: string;
  rows: Map<string, HTMLElement>;
}

interface CommandParts {
  readonly wrap: HTMLElement;
  readonly button: HTMLButtonElement;
  readonly reason: HTMLElement;
  readonly capability: AssetCommandCapability;
  readonly inputs: Map<string, HTMLInputElement>;
  readonly datum: HTMLSelectElement | null;
}

/** The selected asset's or track's detail panel. */
export class AssetPanel {
  private readonly _root: HTMLElement;
  private readonly _title: HTMLHeadingElement;
  private readonly _domainTag: HTMLElement;
  private readonly _badge: HTMLElement;
  private readonly _body: HTMLElement;
  private readonly _commandHost: HTMLElement;
  private readonly _commandNote: HTMLParagraphElement;
  private readonly _status: HTMLParagraphElement;
  private readonly _cards = new Map<string, CardParts>();
  private readonly _commands = new Map<string, CommandParts>();

  private readonly _retry: HTMLButtonElement;

  private readonly _issue: CommandIssuer;
  private readonly _loadCapabilities: (assetId: string) => Promise<AssetCapabilitiesReport | null>;
  private readonly _pickTarget: TargetPicker | null;
  private readonly _retryBaseMs: number;

  private _closeFn: (() => void) | null = null;
  private _subjectId: string | null = null;
  /** The view last rendered, kept because a command is issued from a click that
   *  arrives between frames and still has to be aimed at *this* asset. */
  private _view: AssetView | null = null;
  private _report: AssetCapabilitiesReport | null = null;
  private _reportAssetId: string | null = null;
  private _reportStatus: ReportStatus = 'idle';
  private _retryTimer: ReturnType<typeof setTimeout> | null = null;
  private _retryAttempt = 0;
  private _commandSignature = '';
  private _busy = false;

  constructor(options: AssetPanelOptions = {}) {
    this._issue = options.issueCommand ?? postAssetCommand;
    this._loadCapabilities = options.loadCapabilities ?? loadAssetCapabilities;
    this._pickTarget = options.pickTarget ?? null;
    this._retryBaseMs = options.capabilityRetryMs ?? CAPABILITY_RETRY_MS;

    this._root = document.createElement('aside');
    this._root.className = 'asset-panel';
    this._root.hidden = true;
    this._root.setAttribute('aria-label', 'Selected asset');

    const header = document.createElement('header');
    header.className = 'ap-head';

    const identity = document.createElement('div');
    identity.className = 'ap-identity';

    // Domain reads as a word, not only as a silhouette or a hue: the scene carries
    // it as shape, this carries it as text, and neither leans on colour.
    this._domainTag = document.createElement('span');
    this._domainTag.className = 'ap-domain';

    this._title = document.createElement('h2');
    this._title.className = 'ap-title';

    this._badge = document.createElement('span');
    this._badge.className = 'badge ap-badge';

    identity.append(this._domainTag, this._title, this._badge);

    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'ap-close';
    close.setAttribute('aria-label', 'Close asset panel');
    close.textContent = '×';
    close.addEventListener('click', () => this._dismiss());

    header.append(identity, close);

    this._body = document.createElement('div');
    this._body.className = 'ap-body';

    const footer = document.createElement('footer');
    footer.className = 'ap-foot';

    this._commandNote = document.createElement('p');
    this._commandNote.className = 'ap-cmd-note';
    this._commandNote.hidden = true;

    // The operator's own way out of a failed fetch. The automatic retry backs
    // off, so without this a transient failure would leave the panel looking
    // inert for however long the backoff had grown to.
    this._retry = document.createElement('button');
    this._retry.type = 'button';
    this._retry.className = 'btn ap-cmd-retry';
    this._retry.textContent = 'Retry';
    this._retry.hidden = true;
    this._retry.addEventListener('click', () => this._retryCapabilities());

    this._commandHost = document.createElement('div');
    this._commandHost.className = 'ap-cmds';

    this._status = document.createElement('p');
    this._status.className = 'ap-status';
    this._status.setAttribute('role', 'status');
    this._status.setAttribute('aria-live', 'polite');

    footer.append(this._commandNote, this._retry, this._commandHost, this._status);

    this._root.append(header, this._body, footer);
    this._root.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') this._dismiss();
    });

    (options.mount ?? document.body).appendChild(this._root);
  }

  /** The panel's root element, for a host that wants to place it itself. */
  get element(): HTMLElement {
    return this._root;
  }

  /** Identifier of whatever is shown, or null when hidden. */
  get subjectId(): string | null {
    return this._subjectId;
  }

  /** Called when the operator dismisses the panel. */
  onClose(fn: () => void): void {
    this._closeFn = fn;
  }

  /** Hides the panel and forgets its subject. */
  hide(): void {
    this._root.hidden = true;
    this._subjectId = null;
    this._view = null;
    this._forgetReport();
    this._clearCommands();
  }

  /**
   * Shows or refreshes the panel; `null` hides it.
   *
   * `simulationNowMs` is the instant on the **simulation** clock that the frame
   * describes — `SceneSnapshot.simulationNowMs` — and it is the only ruler a
   * track's age may be measured with: the server stamps `lastUpdateTime` from
   * that same clock, which runs at the speed multiplier and stops with a pause,
   * so a wall-clock reading disagrees with it everywhere except an unpaused 1x
   * run. It is injected rather than read here for the same reason
   * `assetViewFromV2` takes it, and it defaults to null — *unknown* — rather
   * than to `Date.now()`: a caller with no frame to age against has no age to
   * report, and a plausible wrong number is worse than an honest dash.
   *
   * Only the track path reads it. An asset's age already rides on its view,
   * measured by the projection against this very instant, which is what keeps
   * the panel and the overlay agreeing about the same entity.
   */
  render(subject: PanelSubject | null, simulationNowMs: number | null = null): void {
    if (!subject) {
      this.hide();
      return;
    }
    this._root.hidden = false;
    if (subject.kind === 'track') {
      this._renderTrack(subject.track, simulationNowMs);
    } else {
      this._renderAsset(subject);
    }
  }

  /** Detaches the panel and drops its listeners. */
  dispose(): void {
    this._cancelRetry();
    this._clearCommands();
    this._cards.clear();
    this._closeFn = null;
    this._view = null;
    this._root.remove();
  }

  private _dismiss(): void {
    this.hide();
    this._closeFn?.();
  }

  // ── Asset ─────────────────────────────────────────────────────────────────

  private _renderAsset(subject: Extract<PanelSubject, { kind: 'asset' }>): void {
    const view = subject.view;
    const descriptor = subject.descriptor ?? null;
    const state = subject.state ?? null;

    if (this._subjectId !== view.id) {
      this._subjectId = view.id;
      this._status.textContent = '';
      this._requestCapabilities(view.id);
    }
    this._view = view;

    this._title.textContent = view.displayName || view.id;
    this._domainTag.textContent = domainLabel(view.domain);
    this._domainTag.dataset['domain'] = String(view.domain);
    this._badge.textContent = operationalStateLabel(view.operationalState);
    this._badge.dataset['state'] = String(view.operationalState);
    this._applyFreshnessCue(view.freshness, view.ageSeconds);

    this._renderCards(buildAssetCards(view, descriptor, state));
    this._renderCommands(view);
  }

  private _applyFreshnessCue(freshness: number, ageSeconds: number | null): void {
    const degraded = freshness === DataFreshness.Stale || freshness === DataFreshness.Lost;
    this._root.dataset['freshness'] = String(freshness);
    // A pulse is never the whole cue — the age is spelled out in the freshness
    // card regardless — and the pulse itself is dropped for anyone who asked the
    // OS for less motion.
    this._root.classList.toggle('is-pulsing', degraded && !prefersReducedMotion());
    this._badge.title = degraded && ageSeconds !== null
      ? `Last report ${formatAge(ageSeconds)} old`
      : '';
  }

  // ── Track ─────────────────────────────────────────────────────────────────

  private _renderTrack(track: ExternalTrackState, simulationNowMs: number | null): void {
    if (this._subjectId !== track.trackId) {
      this._subjectId = track.trackId;
      this._status.textContent = '';
    }
    // Any report cached from a previously selected asset is dropped — along with
    // any retry still pending for it — so one asset's buttons can never be left
    // standing beside another entity's data.
    this._view = null;
    this._forgetReport();

    this._title.textContent = track.label ?? track.trackId;
    this._domainTag.textContent = 'Track';
    this._domainTag.dataset['domain'] = 'track';
    this._badge.textContent = enumLabel(TrackClassification, track.classification);
    this._badge.dataset['state'] = 'track';
    this._applyFreshnessCue(track.freshness, null);

    this._renderCards(buildTrackCards(track, simulationNowMs));

    this._clearCommands();
    this._retry.hidden = true;
    this._commandNote.textContent = 'Observed contact — not commandable.';
    this._commandNote.hidden = false;
  }

  // ── Cards ─────────────────────────────────────────────────────────────────

  private _renderCards(cards: readonly PanelCard[]): void {
    const live = new Set<string>();
    for (const card of cards) {
      live.add(card.id);
      let parts = this._cards.get(card.id);
      if (!parts) {
        parts = this._createCard(card.id);
        this._cards.set(card.id, parts);
      }
      // Re-appending an existing node moves it, so card order is maintained
      // without discarding and recreating elements.
      this._body.appendChild(parts.section);
      if (parts.title.textContent !== card.title) parts.title.textContent = card.title;

      const note = card.note ?? '';
      if (parts.note.textContent !== note) parts.note.textContent = note;
      parts.note.hidden = note === '';

      const signature = card.rows.map((r) => r.key).join('|');
      if (signature !== parts.rowSignature) {
        parts.list.textContent = '';
        parts.rows = new Map();
        for (const r of card.rows) {
          const pair = document.createElement('div');
          pair.className = 'ap-row';
          const term = document.createElement('dt');
          term.textContent = r.label;
          const value = document.createElement('dd');
          pair.append(term, value);
          parts.list.appendChild(pair);
          parts.rows.set(r.key, value);
        }
        parts.rowSignature = signature;
      }

      for (const r of card.rows) {
        const value = parts.rows.get(r.key);
        if (!value) continue;
        if (value.textContent !== r.value) value.textContent = r.value;
        const tone = r.tone ?? '';
        if (value.dataset['tone'] !== tone) value.dataset['tone'] = tone;
      }
    }

    for (const [id, parts] of this._cards) {
      if (live.has(id)) continue;
      parts.section.remove();
      this._cards.delete(id);
    }
  }

  private _createCard(id: string): CardParts {
    const section = document.createElement('section');
    section.className = 'ap-card';
    section.dataset['card'] = id;

    const title = document.createElement('h3');
    title.className = 'ap-card-title';

    // A description list, because that is what a panel of name/value pairs is.
    const list = document.createElement('dl');
    list.className = 'ap-rows';

    const note = document.createElement('p');
    note.className = 'ap-note';
    note.hidden = true;

    section.append(title, list, note);
    return { section, title, list, note, rowSignature: '', rows: new Map() };
  }

  // ── Commands ──────────────────────────────────────────────────────────────

  /**
   * Fetch the asset's declared commands, once per selection — and again after a
   * failure.
   *
   * The short-circuit deliberately does *not* cover `failed`. Keying it on the
   * asset id alone is what made one dropped request permanent: the id stayed set,
   * every later call returned immediately, and the panel showed a static "no
   * commands" note for that asset until it was deselected.
   */
  private _requestCapabilities(assetId: string, isRetry = false): void {
    if (!isRetry && this._reportAssetId === assetId && this._reportStatus !== 'failed') return;
    this._cancelRetry();
    this._report = null;
    this._reportAssetId = assetId;
    this._reportStatus = 'pending';
    this._commandSignature = '';
    void this._loadCapabilities(assetId)
      .then((report) => {
        // A late answer for an asset no longer selected is dropped rather than
        // painted over the current one.
        if (this._reportAssetId !== assetId) return;
        if (!report) {
          // `loadAssetCapabilities` resolves null only when the report could not
          // be read. That is a failure, not an asset with nothing to offer.
          this._failReport(assetId, 'report unreadable');
          return;
        }
        this._reportStatus = 'ready';
        this._retryAttempt = 0;
        this._report = report;
        this._commandSignature = '';
      })
      .catch((err: unknown) => {
        if (this._reportAssetId !== assetId) return;
        this._failReport(assetId, String(err));
      });
  }

  /** Records a failed fetch and queues the next attempt. */
  private _failReport(assetId: string, reason: string): void {
    log.warn('capability report unavailable', { assetId, error: reason });
    this._report = null;
    this._reportStatus = 'failed';
    this._commandSignature = '';

    // Exponential, ceilinged: a server that is down stays asked about roughly
    // once a minute rather than ten times a second, and a server that blipped is
    // asked again within seconds.
    const delay = Math.min(
      this._retryBaseMs * 2 ** this._retryAttempt,
      CAPABILITY_RETRY_CEILING_MS,
    );
    this._retryAttempt += 1;
    this._retryTimer = setTimeout(() => {
      this._retryTimer = null;
      if (this._reportAssetId === assetId && this._reportStatus === 'failed') {
        this._requestCapabilities(assetId, true);
      }
    }, delay);
  }

  /** The operator asking for the retry now rather than at the next backoff. */
  private _retryCapabilities(): void {
    const assetId = this._reportAssetId;
    if (assetId === null || this._reportStatus !== 'failed') return;
    // A deliberate press restarts the backoff: the operator has told us the
    // conditions changed, and making them wait a minute to find out would be
    // treating their judgement as noise.
    this._retryAttempt = 0;
    this._requestCapabilities(assetId, true);
    this._retry.hidden = true;
    this._commandNote.textContent = 'Reading declared capabilities…';
    this._commandNote.hidden = false;
  }

  private _cancelRetry(): void {
    if (this._retryTimer === null) return;
    clearTimeout(this._retryTimer);
    this._retryTimer = null;
  }

  /** Drops every trace of the current report, including a queued retry. */
  private _forgetReport(): void {
    this._cancelRetry();
    this._report = null;
    this._reportAssetId = null;
    this._reportStatus = 'idle';
    this._retryAttempt = 0;
  }

  private _renderCommands(view: AssetView): void {
    const report = this._report;
    if (!report || report.assetId !== view.id) {
      // No report means no basis for any button. Saying so is honest; guessing a
      // set from the vehicle class would reinvent the capability gate here, where
      // it would drift from the one that actually decides.
      this._clearCommands();
      // Three different things, said as three different things: still reading,
      // could not read, and read a report addressed elsewhere. Only the middle
      // one is recoverable by asking again, so only it offers the control.
      const failed = this._reportStatus === 'failed';
      const unavailable = 'Declared capabilities unavailable — no commands offered.';
      let note = unavailable;
      if (this._reportStatus === 'pending') note = 'Reading declared capabilities…';
      else if (failed) note = `${unavailable} Retrying…`;
      this._commandNote.textContent = note;
      this._commandNote.hidden = false;
      this._retry.hidden = !failed;
      return;
    }

    this._retry.hidden = true;

    // A report that was read and lists nothing is an answer, and reads as one.
    // Silence here would be indistinguishable from the failure above.
    if (report.commands.length === 0) {
      this._clearCommands();
      this._commandNote.textContent = 'This asset declares no commands.';
      this._commandNote.hidden = false;
      return;
    }

    this._commandNote.hidden = true;

    const signature = report.commands.map((c) => c.kind).join('|');
    if (signature !== this._commandSignature) {
      this._clearCommands();
      for (const capability of report.commands) {
        const parts = this._createCommand(capability, view, report.motion);
        this._commands.set(capability.kind, parts);
        this._commandHost.appendChild(parts.wrap);
      }
      this._commandSignature = signature;
    }

    const context: CommandContext = {
      operationalState: view.operationalState,
      freshness: view.freshness,
      ageSeconds: view.ageSeconds,
      canPickTarget: this._pickTarget !== null,
    };
    for (const capability of report.commands) {
      const parts = this._commands.get(capability.kind);
      if (!parts) continue;
      // Re-published every frame because the datum select can change under the
      // operator's hand, and an altitude's accepted range moves with it.
      this._syncBounds(parts, view, report.motion);
      this._applyAvailability(parts, evaluateCommand(capability, context), report.motion, view);
    }
  }

  /** The bounds context for one control: this asset's surface elevation and the
   *  datum the control itself currently names. */
  private _boundsContext(parts: CommandParts, view: AssetView): ParameterBoundsContext {
    return {
      surfaceElevationM: surfaceElevationUnderAssetM(view),
      verticalReference: parts.datum?.value ?? null,
    };
  }

  /** Keeps each field's advertised range in step with the datum beside it. */
  private _syncBounds(parts: CommandParts, view: AssetView, motion: MotionConstraints): void {
    const ctx = this._boundsContext(parts, view);
    for (const [key, input] of parts.inputs) {
      const spec = PARAMETER_SPECS[key];
      if (!spec) continue;
      const { min, max } = spec.bounds(motion, ctx);
      // An unbounded side carries no attribute at all rather than a placeholder:
      // `min=""` and `min="-Infinity"` both read as a constraint that is not one.
      const lo = min === null ? '' : String(min);
      const hi = max === null ? '' : String(max);
      if (input.min !== lo) input.min = lo;
      if (input.max !== hi) input.max = hi;
    }
  }

  private _applyAvailability(
    parts: CommandParts,
    availability: CommandAvailability,
    motion: MotionConstraints,
    view: AssetView,
  ): void {
    // A refusal always carries a reason, including the transient one: a control
    // that goes grey with nothing to say reads as broken rather than as busy.
    const reason = availability.reason
      ?? this._parameterProblem(parts, motion, view)
      ?? (this._busy ? 'a command is already in flight' : null);
    const enabled = reason === null;

    // `aria-disabled` rather than the `disabled` attribute. A disabled control
    // leaves the tab order, so a keyboard operator loses their place the instant
    // an asset goes stale and never discovers why the command went away. This one
    // stays focusable, carries the reason through `aria-describedby`, and refuses
    // activation in `_activate`.
    parts.button.setAttribute('aria-disabled', enabled ? 'false' : 'true');
    parts.wrap.classList.toggle('is-blocked', !enabled);

    const shown = reason ?? '';
    if (parts.reason.textContent !== shown) parts.reason.textContent = shown;
    parts.reason.hidden = shown === '';
    parts.button.title = shown;
  }

  /**
   * The reason a command's own inputs are not yet usable, or null.
   *
   * Bounds come from this asset's motion limits *and* from the datum the value is
   * quoted against, so the check the operator sees is the one the server will
   * apply. The server range-checks an altitude only after folding the datum in —
   * `aboveGround` and `terrain` gain the surface elevation on the way — so
   * checking the typed number against a fixed envelope would be checking a
   * different quantity and disagreeing with the server in both directions.
   *
   * Where the datum cannot be folded in — an asset whose stream reports no
   * surface under it — nothing is claimed about the range beyond finiteness, and
   * the server stays authoritative. One validation, or none; never two that
   * disagree.
   */
  private _parameterProblem(
    parts: CommandParts,
    motion: MotionConstraints,
    view: AssetView,
  ): string | null {
    const ctx = this._boundsContext(parts, view);
    for (const [key, input] of parts.inputs) {
      const spec = PARAMETER_SPECS[key];
      if (!spec) return `this client cannot supply the "${key}" parameter`;
      const value = Number(input.value);
      if (input.value.trim() === '' || !Number.isFinite(value)) {
        return `${spec.label.toLowerCase()} must be a number`;
      }
      const { min, max } = spec.bounds(motion, ctx);
      const belowFloor = min !== null && value < min;
      const aboveCeiling = max !== null && value > max;
      if (!belowFloor && !aboveCeiling) continue;
      if (min !== null && max !== null) {
        return `${spec.label.toLowerCase()} must be between ${min} and ${max} ${spec.unit}`;
      }
      return belowFloor
        ? `${spec.label.toLowerCase()} must be at least ${String(min)} ${spec.unit}`
        : `${spec.label.toLowerCase()} must be at most ${String(max)} ${spec.unit}`;
    }
    return null;
  }

  private _createCommand(
    capability: AssetCommandCapability,
    view: AssetView,
    motion: MotionConstraints,
  ): CommandParts {
    const wrap = document.createElement('div');
    wrap.className = 'ap-cmd';
    wrap.dataset['kind'] = capability.kind;

    const inputs = new Map<string, HTMLInputElement>();
    let datum: HTMLSelectElement | null = null;

    for (const key of capability.requiredParameters) {
      const spec = PARAMETER_SPECS[key];
      // An unsupported key is not skipped silently: `evaluateCommand` has already
      // blocked the command and named the key, and the button stays visible so the
      // operator can see what the asset accepts and this client cannot yet send.
      if (!spec) continue;

      const field = document.createElement('label');
      field.className = 'ap-field';

      const caption = document.createElement('span');
      caption.className = 'ap-field-label';
      caption.textContent = `${spec.label} (${spec.unit})`;

      const input = document.createElement('input');
      input.type = 'number';
      input.className = 'ap-field-input';
      input.step = String(spec.step);
      // Bounds are published by `_syncBounds` once the datum control exists —
      // the altitude field's range depends on a select built later in this loop.
      input.value = String(spec.initial(view, motion));
      inputs.set(key, input);

      field.append(caption, input);
      wrap.appendChild(field);

      if (spec.needsVerticalReference) {
        const datumField = document.createElement('label');
        datumField.className = 'ap-field';
        const datumCaption = document.createElement('span');
        datumCaption.className = 'ap-field-label';
        datumCaption.textContent = 'Measured against';
        datum = document.createElement('select');
        datum.className = 'ap-field-input';
        for (const [value, label] of VERTICAL_REFERENCES) {
          const option = document.createElement('option');
          option.value = value;
          option.textContent = label;
          datum.appendChild(option);
        }
        datumField.append(datumCaption, datum);
        wrap.appendChild(datumField);
      }
    }

    const button = document.createElement('button');
    button.type = 'button';
    const destructive = DESTRUCTIVE_COMMANDS.has(capability.kind) ? ' btn-danger' : '';
    button.className = `btn ap-cmd-btn${destructive}`;
    // The ellipsis is the standard promise that a further step follows; a
    // target-taking command does not fire on the press.
    button.textContent = capability.requiresTarget
      ? `${humanise(capability.kind)}…`
      : humanise(capability.kind);

    const reason = document.createElement('span');
    reason.className = 'ap-cmd-reason';
    reason.id = `ap-reason-${capability.kind}`;
    reason.hidden = true;
    button.setAttribute('aria-describedby', reason.id);

    wrap.append(button, reason);

    const parts: CommandParts = { wrap, button, reason, capability, inputs, datum };
    button.addEventListener('click', () => void this._activate(parts));
    return parts;
  }

  private async _activate(parts: CommandParts): Promise<void> {
    const capability = parts.capability;
    if (parts.button.getAttribute('aria-disabled') === 'true') {
      // The control refuses rather than sending, and repeats the reason into the
      // live region so the refusal is audible and not only visible.
      const why = parts.reason.textContent;
      this._announce(why
        ? `${humanise(capability.kind)} unavailable: ${why}.`
        : `${humanise(capability.kind)} is not available.`);
      return;
    }
    if (this._busy) return;

    const assetId = this._subjectId;
    const view = this._view;
    if (!assetId || !view || !this._report) return;

    let target: unknown;
    if (capability.requiresTarget) {
      const picker = this._pickTarget;
      if (!picker) return;
      this._announce(`Pick a destination for ${humanise(capability.kind).toLowerCase()}.`);
      const picked = await picker(capability.kind, humanise(capability.kind));
      if (!picked) {
        this._announce('Destination cancelled.');
        return;
      }
      // A map pick answers *where*, not *how high*: its `Y` is the surface the ray
      // hit. `targetForAsset` decides the height by domain — the surface for
      // something that drives or floats on it, the reported altitude for something
      // that flies — so a picked `goTo` is not a commanded descent into terrain.
      target = pointTarget(targetForAsset(picked, view));
    }

    const parameters: Record<string, string> = {};
    for (const [key, input] of parts.inputs) {
      const spec = PARAMETER_SPECS[key];
      if (!spec) return;
      parameters[key] = String(spec.toWire(Number(input.value)));
      // An altitude without its datum is refused at the boundary, and rightly:
      // above-ground and mean-sea-level differ by the hill under the asset.
      if (spec.needsVerticalReference && parts.datum) {
        parameters['verticalReference'] = parts.datum.value;
      }
    }

    const request: AssetCommandRequestBody = {
      kind: capability.kind,
      idempotencyKey: newIdempotencyKey(),
      ...(target === undefined ? {} : { target }),
      ...(Object.keys(parameters).length === 0 ? {} : { parameters }),
    };

    this._busy = true;
    parts.wrap.classList.add('is-busy');
    try {
      const outcome = await this._issue(assetId, request);
      this._announce(outcome.message);
    } catch (err: unknown) {
      log.error('command failed to send', err);
      this._announce(`${humanise(capability.kind)} failed to send.`);
    } finally {
      this._busy = false;
      parts.wrap.classList.remove('is-busy');
    }
  }

  private _announce(message: string): void {
    this._status.textContent = message;
  }

  private _clearCommands(): void {
    this._commandHost.textContent = '';
    this._commands.clear();
    this._commandSignature = '';
  }
}
