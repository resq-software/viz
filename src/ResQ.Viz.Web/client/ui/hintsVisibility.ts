// ResQ Viz - keyboard-hints visibility ownership
// SPDX-License-Identifier: Apache-2.0

/** Synchronizes keyboard-hints visual, accessibility, and focus state. */
export function setHintsVisibleState(
  panel: HTMLElement | null,
  toggle: HTMLElement | null,
  visible: boolean,
): void {
  if (panel === null) return;

  if (!visible) {
    const active = panel.ownerDocument.activeElement;
    if (active instanceof Element && panel.contains(active)) toggle?.focus();
  }

  panel.classList.toggle('hidden', !visible);
  panel.hidden = !visible;
  panel.setAttribute('aria-hidden', String(!visible));
  if (visible) panel.removeAttribute('inert');
  else panel.setAttribute('inert', '');
  toggle?.classList.toggle('active', visible);
  toggle?.setAttribute('aria-pressed', String(visible));
}
