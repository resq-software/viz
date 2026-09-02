// ResQ Viz - standalone global keyboard shortcut ownership
// SPDX-License-Identifier: Apache-2.0

/** One distinct primary key code per active standalone shortcut owner. */
export const GLOBAL_SHORTCUTS = {
  cockpit: 'KeyI',
  sensorStats: 'F2',
  onboardPip: 'KeyP',
  onboardPipMode: 'KeyO',
  editorDock: 'Backslash',
  transportPlayPause: 'Space',
  transportStep: 'Period',
} as const;
