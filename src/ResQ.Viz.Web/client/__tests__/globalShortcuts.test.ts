// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { GLOBAL_SHORTCUTS } from '../ui/globalShortcuts';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

describe('active global shortcut ownership', () => {
  it('assigns one unique code to every standalone owner', () => {
    expect(GLOBAL_SHORTCUTS).toEqual({
      cockpit: 'KeyI',
      sensorStats: 'F2',
      onboardPip: 'KeyP',
      onboardPipMode: 'KeyO',
      editorDock: 'Backslash',
      transportPlayPause: 'Space',
      transportStep: 'Period',
    });

    const codes = Object.values(GLOBAL_SHORTCUTS);
    expect(new Set(codes).size).toBe(codes.length);
  });

  it('wires every active owner to the registry and leaves legacy Transport button-only', () => {
    const app = read('../app.ts');
    const dvr = read('../editor/dvr.ts');
    const pip = read('../sensors/onboardPip.ts');
    const dock = read('../editor/dock.ts');
    const sensor = read('../sensorStatsOverlay.ts');
    const transport = read('../editor/transport.ts');
    const html = read('../index.html');

    expect(app).toContain('GLOBAL_SHORTCUTS.cockpit');
    expect(dvr).toContain('GLOBAL_SHORTCUTS.transportPlayPause');
    expect(dvr).toContain('GLOBAL_SHORTCUTS.transportStep');
    expect(pip).toContain('GLOBAL_SHORTCUTS.onboardPip');
    expect(pip).toContain('GLOBAL_SHORTCUTS.onboardPipMode');
    expect(dock).toContain('GLOBAL_SHORTCUTS.editorDock');
    expect(sensor).toContain('GLOBAL_SHORTCUTS.sensorStats');
    expect(sensor).toContain('press "F2" to toggle');
    expect(transport).not.toContain("addEventListener('keydown'");
    expect(transport).not.toContain('this._bindKeyboard()');
    expect(html).toContain('<kbd>F2</kbd> Sensor diagnostics');
  });
});
