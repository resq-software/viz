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
    if (!surface.hasAttribute('data-context-visible')) {
      surface.setAttribute('data-context-visible', String(!surface.hidden));
    }
    surface.setAttribute('data-context-obscured', '');
    surface.hidden = true;
    surface.setAttribute('inert', '');
    surface.setAttribute('aria-hidden', 'true');
    surface.style.pointerEvents = 'none';
    return;
  }

  surface.removeAttribute('data-context-obscured');
  surface.removeAttribute('inert');
  const visible = surface.getAttribute('data-context-visible') === 'true';
  surface.hidden = !visible;
  surface.setAttribute('aria-hidden', String(!visible));
  surface.style.pointerEvents = previousPointerEvents.get(surface) ?? '';
  previousPointerEvents.delete(surface);
}
