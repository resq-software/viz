// ResQ Viz - the session's authority trail, read-only
// SPDX-License-Identifier: Apache-2.0
//
// Two bounded windows: what the command gate decided, and what happened to
// leases. Both are read-only here, and there is deliberately no control in this
// panel that acts on a record — an audit view that can change what it shows is
// not an audit view.
//
// **The dropped counts are the point.** Both windows are bounded, so an empty
// or short list is ambiguous on its own: a quiet session and a truncated one
// look identical. Publishing what each half discarded is what separates them,
// and is why this reads the counts rather than only the rows.
//
// It is also the one surface here that stays available away from the live edge.
// Reading what already happened is not a mutation, and a recording is exactly
// when an operator wants the trail. What it reads is the **live session's**
// trail rather than the recording's, because that is the only one the server
// keeps — the panel says nothing that implies otherwise.

import type {
  CommandAuditRecord,
  CommandAuditResponse,
  ControlAuditRecord,
} from './types';
import { actionButton, panelCard, setDisabled, setHidden, setText } from './panelDom';

/** `CommandDecision` on the wire. Names, so a record reads without a lookup. */
const DECISIONS: Readonly<Record<number, string>> = {
  0: 'unspecified', 1: 'accepted', 2: 'rejected', 3: 'preempted', 4: 'policy-modified',
};

/** `ControlAuditKind` on the wire. */
const LEASE_KINDS: Readonly<Record<number, string>> = {
  0: 'unspecified', 1: 'acquired', 2: 'renewed', 3: 'released',
  4: 'preempted', 5: 'expired', 6: 'revoked', 7: 'denied',
};

export interface AuditPanelOptions {
  readonly mount: HTMLElement;
  readonly onLoad: () => void;
}

/** The bounded command-decision and lease windows, with their truncation counts. */
export class AuditPanel {
  private readonly _load: HTMLButtonElement;
  private readonly _decisions: HTMLElement;
  private readonly _leases: HTMLElement;
  private readonly _dropped: HTMLElement;
  private readonly _status: HTMLElement;

  constructor(options: AuditPanelOptions) {
    const card = panelCard(
      options.mount,
      'audit',
      'Authority trail',
      'What the command gate decided and what happened to leases in this session. '
      + 'Read-only, bounded, and available during replay.',
    );

    this._load = actionButton('load-audit', 'Load trail');
    const actions = document.createElement('div');
    actions.className = 'advanced-actions';
    actions.append(this._load);

    this._dropped = document.createElement('p');
    this._dropped.className = 'advanced-dropped';
    this._dropped.hidden = true;

    this._decisions = document.createElement('ol');
    this._decisions.className = 'advanced-trail';
    this._leases = document.createElement('ol');
    this._leases.className = 'advanced-trail';

    const decisionHeading = document.createElement('h3');
    decisionHeading.className = 'advanced-subhead';
    decisionHeading.textContent = 'Command decisions';
    const leaseHeading = document.createElement('h3');
    leaseHeading.className = 'advanced-subhead';
    leaseHeading.textContent = 'Lease records';

    this._status = card.status;
    card.body.append(
      actions, this._dropped,
      decisionHeading, this._decisions,
      leaseHeading, this._leases,
    );
    this._load.addEventListener('click', () => options.onLoad());
  }

  setBusy(busy: boolean): void {
    setDisabled(this._load, busy);
    setText(this._load, busy ? 'Loading…' : 'Load trail');
  }

  setStatus(message: string | null, isError = false): void {
    setHidden(this._status, message === null);
    setText(this._status, message ?? '');
    this._status.setAttribute('role', isError ? 'alert' : 'status');
    this._status.classList.toggle('is-error', isError);
  }

  render(audit: CommandAuditResponse): void {
    this._decisions.replaceChildren(
      ...audit.decisions.map(record => row(describeDecision(record))),
    );
    this._leases.replaceChildren(
      ...audit.leases.map(record => row(describeLease(record))),
    );
    setHidden(this._dropped, false);
    setText(
      this._dropped,
      `${audit.droppedDecisionCount} older command decisions and `
      + `${audit.droppedLeaseCount} older lease records have been dropped from these `
      + 'bounded windows.',
    );
  }
}

function row(text: string): HTMLLIElement {
  const item = document.createElement('li');
  item.textContent = text;
  return item;
}

function describeDecision(record: CommandAuditRecord): string {
  const parts = [
    `#${record.sequence}`,
    DECISIONS[record.decision] ?? String(record.decision),
    record.assetId,
    record.kind ?? 'no command',
    `issuer ${record.issuerId}`,
  ];
  if (record.leaseId !== null) parts.push(`lease ${record.leaseId}`);
  if (record.reasonCode !== null) parts.push(record.reasonCode);
  return parts.join(' · ');
}

function describeLease(record: ControlAuditRecord): string {
  const parts = [
    `#${record.sequence}`,
    LEASE_KINDS[record.kind] ?? String(record.kind),
    record.assetId,
  ];
  if (record.leaseId !== null) parts.push(`lease ${record.leaseId}`);
  if (record.holderId !== null) parts.push(`holder ${record.holderId}`);
  if (record.denialCode !== null) parts.push(record.denialCode);
  if (record.justification !== null) parts.push(`“${record.justification}”`);
  return parts.join(' · ');
}
