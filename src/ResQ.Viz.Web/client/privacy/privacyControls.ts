// ResQ Viz - analytics consent banner and privacy settings dialog
// SPDX-License-Identifier: Apache-2.0
//
// The markup lives in index.html (hidden until needed); this module wires it.
// It is loaded lazily by client/analytics.ts, only when there is a choice to
// ask for or the visitor opens Settings → Privacy settings, so its CSS stays
// out of the entry bundle.
//
// Accept and Decline are the same element type with the same class in both
// places: neither choice is easier to reach or more prominent than the other.

import '../styles/privacy.css';
import { type ConsentDecision, type ConsentState, readConsent } from './consent';

/** What the banner and dialog need from the analytics bootstrap. */
export interface PrivacyControls {
  /** Whether this build has an analytics provider to ask about. */
  readonly configured: boolean;
  /** Store and apply the visitor's decision. */
  decide(decision: ConsentDecision): void;
}

const CHOICE_TEXT: Record<ConsentState, string> = {
  granted: 'Current choice: analytics accepted.',
  denied: 'Current choice: analytics declined.',
  unset: 'Current choice: none yet. Nothing loads until you accept.',
};

function element<T extends HTMLElement>(doc: Document, id: string): T | null {
  return doc.getElementById(id) as T | null;
}

/** Attach a click handler once, however many times the surface is opened. */
function onClickOnce(target: HTMLElement | null, handler: (event: MouseEvent) => void): void {
  if (!target || target.dataset['privacyWired'] === 'true') return;
  target.dataset['privacyWired'] = 'true';
  target.addEventListener('click', handler);
}

/** Hide the first-visit banner. */
export function hideConsentBanner(doc: Document): void {
  const banner = element(doc, 'consent-banner');
  if (banner) banner.hidden = true;
}

/** Reveal the first-visit banner and wire its three buttons. */
export function showConsentBanner(doc: Document, controls: PrivacyControls): void {
  const banner = element(doc, 'consent-banner');
  if (!banner) return;

  const choose = (decision: ConsentDecision) => () => {
    controls.decide(decision);
    hideConsentBanner(doc);
  };
  onClickOnce(element(doc, 'consent-accept'), choose('granted'));
  onClickOnce(element(doc, 'consent-decline'), choose('denied'));
  onClickOnce(element(doc, 'consent-notice-open'), (event) => {
    openPrivacyDialog(doc, controls, event.currentTarget as HTMLElement);
  });

  banner.hidden = false;
}

/**
 * Open the privacy dialog: the full notice, the current choice, and Accept /
 * Decline to change it. Focus returns to `opener` when it closes.
 */
export function openPrivacyDialog(
  doc: Document,
  controls: PrivacyControls,
  opener: HTMLElement | null,
): void {
  const dialog = element<HTMLDialogElement>(doc, 'privacy-dialog');
  if (!dialog) return;

  const choiceLine = element(doc, 'privacy-choice');
  const refreshChoice = () => {
    if (choiceLine) choiceLine.textContent = CHOICE_TEXT[readConsent()];
  };
  const unconfigured = element(doc, 'privacy-unconfigured');
  if (unconfigured) unconfigured.hidden = controls.configured;
  refreshChoice();

  const choose = (decision: ConsentDecision) => () => {
    controls.decide(decision);
    hideConsentBanner(doc);
    refreshChoice();
    dialog.close();
  };
  onClickOnce(element(doc, 'privacy-accept'), choose('granted'));
  onClickOnce(element(doc, 'privacy-decline'), choose('denied'));
  onClickOnce(element(doc, 'privacy-close'), () => dialog.close());

  if (dialog.dataset['privacyWired'] !== 'true') {
    dialog.dataset['privacyWired'] = 'true';
    // The console's single-key shortcuts (R resets the simulation, Space stops
    // it) must not fire while the visitor is reading the notice.
    dialog.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') event.stopPropagation();
    });
  }
  dialog.addEventListener(
    'close',
    () => {
      const target = opener?.isConnected && !opener.closest('[hidden]') ? opener : null;
      (target ?? element(doc, 'hud-settings-toggle'))?.focus();
    },
    { once: true },
  );

  if (!dialog.open) dialog.showModal();
}
