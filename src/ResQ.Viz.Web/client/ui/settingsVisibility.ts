// ResQ Viz - Settings surface visibility ownership
// SPDX-License-Identifier: Apache-2.0

/** Synchronizes Settings visual, accessibility, and focus state. */
export function setSettingsVisibleState(
  panel: HTMLElement | null,
  toggle: HTMLElement | null,
  visible: boolean,
): void {
  if (panel === null) return;

  if (!visible) {
    const active = panel.ownerDocument.activeElement;
    if (active instanceof Element && panel.contains(active)) toggle?.focus();
  }

  panel.classList.toggle('open', visible);
  panel.setAttribute('aria-hidden', String(!visible));
  if (visible) panel.removeAttribute('inert');
  else panel.setAttribute('inert', '');
  toggle?.setAttribute('aria-expanded', String(visible));
}
