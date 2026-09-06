// ResQ Viz - safe global keyboard shortcut predicates
// SPDX-License-Identifier: Apache-2.0

/** Modifier keys a specific global shortcut explicitly owns. */
export interface GlobalShortcutOptions {
  readonly allowCtrl?: boolean;
  readonly allowMeta?: boolean;
  readonly allowAlt?: boolean;
}

/**
 * Whether a global shortcut handler must leave this event alone.
 *
 * Native controls and editable descendants own their keys. Ctrl, Meta, and Alt
 * remain reserved for browser/platform chords unless a caller explicitly owns
 * one, while Shift-only shortcuts remain available for camera presets.
 */
export function shouldIgnoreGlobalShortcut(
  event: KeyboardEvent,
  options: GlobalShortcutOptions = {},
): boolean {
  if (event.defaultPrevented) return true;

  const target = event.target;
  if (target instanceof Element
      && target.closest(
        'input, select, textarea, button, summary, a[href], [contenteditable]',
      ) !== null) {
    return true;
  }

  if (event.ctrlKey && !options.allowCtrl) return true;
  if (event.metaKey && !options.allowMeta) return true;
  if (event.altKey && !options.allowAlt) return true;
  return false;
}
