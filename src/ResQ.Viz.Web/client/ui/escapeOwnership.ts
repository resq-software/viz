// ResQ Viz - early modal Escape ownership
// SPDX-License-Identifier: Apache-2.0

/** Handles Escape for the one active modal owner before ordinary shortcuts. */
export function handleOwnedEscape(
  event: KeyboardEvent,
  hasPendingTarget: boolean,
  hintsVisible: boolean,
  isContextPanelVisible: () => boolean,
  cancelTarget: () => void,
  closeHints: () => void,
  closeContextPanel: () => void,
): boolean {
  if (event.key !== 'Escape' || event.defaultPrevented
      || event.ctrlKey || event.metaKey || event.altKey) return false;

  const dismiss = hasPendingTarget
    ? cancelTarget
    : hintsVisible
      ? closeHints
      : isContextPanelVisible()
        ? closeContextPanel
        : null;
  if (dismiss === null) return false;
  event.preventDefault();
  dismiss();
  return true;
}
