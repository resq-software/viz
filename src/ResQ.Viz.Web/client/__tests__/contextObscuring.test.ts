// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it, vi } from 'vitest';

import { AssetPanel } from '../assets/AssetPanel';
import type { ExternalTrackState } from '../assets/types';
import {
  CoordinateFrame,
  DataFreshness,
  TrackClassification,
  TrackSourceKind,
} from '../assets/types';
import { setContextObscured } from '../ui/contextObscuring';
import { handleOwnedEscape } from '../ui/escapeOwnership';

function read(relative: string): string {
  return readFileSync(fileURLToPath(new URL(relative, import.meta.url)), 'utf8');
}

describe('settings context obscuring', () => {
  it('persists through repeated AssetPanel renders and restores its intended visibility', () => {
    document.body.innerHTML = `
      <div id="asset-panel-mount"></div>
      <button id="settings-close">Close settings</button>
    `;
    const assetPanel = new AssetPanel({ mount: document.getElementById('asset-panel-mount')! });
    const subject = { kind: 'track' as const, track: track() };
    assetPanel.render(subject);
    const panel = assetPanel.element;
    const close = document.getElementById('settings-close') as HTMLButtonElement;
    panel.querySelector<HTMLButtonElement>('.ap-close')!.focus();
    expect(assetPanel.isVisible).toBe(true);

    setContextObscured(panel, true, close);

    expect(document.activeElement).toBe(close);
    expect(panel.hasAttribute('data-context-obscured')).toBe(true);
    expect(panel.hidden).toBe(true);
    expect(panel.hasAttribute('inert')).toBe(true);
    expect(panel.getAttribute('aria-hidden')).toBe('true');
    expect(panel.style.pointerEvents).toBe('none');
    expect(assetPanel.subjectId).toBe('track-1');
    expect(assetPanel.isVisible).toBe(false);

    const clearSelection = vi.fn();
    const escape = new KeyboardEvent('keydown', { key: 'Escape', cancelable: true });
    expect(handleOwnedEscape(
      escape, false, false, () => assetPanel.isVisible, vi.fn(), vi.fn(), clearSelection,
    )).toBe(false);
    expect(escape.defaultPrevented).toBe(false);
    expect(clearSelection).not.toHaveBeenCalled();
    expect(assetPanel.subjectId).toBe('track-1');

    assetPanel.render(subject);
    assetPanel.render(subject);
    expect(panel.hasAttribute('data-context-obscured')).toBe(true);
    expect(panel.hidden).toBe(true);
    expect(panel.hasAttribute('inert')).toBe(true);
    expect(panel.getAttribute('aria-hidden')).toBe('true');
    expect(panel.style.pointerEvents).toBe('none');

    setContextObscured(panel, false, close);
    assetPanel.render(subject);
    expect(panel.hasAttribute('data-context-obscured')).toBe(false);
    expect(panel.hidden).toBe(false);
    expect(panel.hasAttribute('inert')).toBe(false);
    expect(panel.getAttribute('aria-hidden')).toBe('false');
    expect(panel.style.pointerEvents).toBe('');
    expect(assetPanel.isVisible).toBe(true);

    const mount = document.getElementById('asset-panel-mount')!;
    mount.setAttribute('inert', '');
    expect(assetPanel.isVisible).toBe(false);
    mount.removeAttribute('inert');
    mount.hidden = true;
    expect(assetPanel.isVisible).toBe(false);
    mount.hidden = false;
    expect(assetPanel.isVisible).toBe(true);

    assetPanel.render(null);
    expect(panel.hidden).toBe(true);
    expect(panel.getAttribute('aria-hidden')).toBe('true');
  });

  it('obscures a fleet panel created after Settings has already opened', () => {
    const app = read('../app.ts');
    const css = read('../styles/main.css');

    expect(app).toContain("import { setContextObscured } from './ui/contextObscuring'");
    expect(app).toMatch(/function _setSettingsVisible[\s\S]*?setContextObscured\([\s\S]*?\.asset-panel/);
    expect(app).toMatch(/fleetUi = new m\.FleetUi\([\s\S]*?setContextObscured\([\s\S]*?fleetUi\.panel\.element[\s\S]*?settingsPanel\?\.classList\.contains\('open'\)/);
    expect(css).toMatch(/body:has\(#settings-panel\.open\)[\s\S]*?\.asset-panel[\s\S]*?display:\s*none\s*!important/);
    expect(read('../styles/assets.css')).toMatch(/\.asset-panel\[data-context-obscured\][\s\S]*?display:\s*none[\s\S]*?pointer-events:\s*none/);
  });
});

function track(): ExternalTrackState {
  return {
    trackId: 'track-1',
    classification: TrackClassification.Vessel,
    pose: {
      frame: CoordinateFrame.LocalEus,
      originId: null,
      position: { x: 10, y: 0, z: -5 },
      orientation: { x: 0, y: 0, z: 0, w: 1 },
      covariance: null,
      geo: null,
    },
    twist: {
      frame: CoordinateFrame.LocalEus,
      linear: { x: 1, y: 0, z: 0 },
      angular: { x: 0, y: 0, z: 0 },
      originId: null,
      covariance: null,
    },
    sources: [{
      sourceId: 'ais-1',
      kind: TrackSourceKind.Transponder,
      observedAt: '2026-08-30T12:00:00.000Z',
      quality: 0.9,
    }],
    quality: {
      confidence: 0.8,
      positionAccuracyM: null,
      velocityAccuracyMps: null,
      updateCount: 3,
      isFused: false,
    },
    lastUpdateTime: '2026-08-30T12:00:00.000Z',
    freshness: DataFreshness.Fresh,
    label: 'MV Example',
    transponder: null,
  };
}
