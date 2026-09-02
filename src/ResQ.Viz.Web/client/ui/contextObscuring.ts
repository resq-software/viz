// ResQ Viz - non-modal context surface obscuring
// SPDX-License-Identifier: Apache-2.0

const previousPointerEvents = new WeakMap<HTMLElement, string>();

/** Makes a context surface inaccessible while a sibling sheet owns its region. */
export function setContextObscured(
  surface: HTMLElement | null,
  obscured: boolean,
  focusTarget: HTMLElement | null,
): void {
  if (surface === null) return;

  if (obscured) {
    const active = surface.ownerDocument.activeElement;
    if (active instanceof Element && surface.contains(active)) focusTarget?.focus();
    if (!previousPointerEvents.has(surface)) {
      previousPointerEvents.set(surface, surface.style.pointerEvents);
    }
    surface.setAttribute('inert', '');
    surface.setAttribute('aria-hidden', 'true');
    surface.style.pointerEvents = 'none';
    return;
  }

  surface.removeAttribute('inert');
  surface.setAttribute('aria-hidden', String(surface.hidden));
  surface.style.pointerEvents = previousPointerEvents.get(surface) ?? '';
  previousPointerEvents.delete(surface);
}
