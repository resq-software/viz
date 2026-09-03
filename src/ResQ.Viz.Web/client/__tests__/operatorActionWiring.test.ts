// SPDX-License-Identifier: Apache-2.0
//
// Guards the integration root of the live/replay mutation gate.
//
// `app.ts` cannot be imported under Vitest — it boots the renderer, opens a
// SignalR connection and touches WebGL at module scope — so the property that
// matters most about it is asserted at the source level, which is the level a
// missed call site actually lives at.
//
// The property: every mutating handler in `app.ts` goes through
// `operatorActions`, and none of them reaches `apiPost`/`apiPostOrWarn` (or a
// raw mutation URL) on its own. A second gate is a gate that drifts, and a
// handler that skips the gate is a control that keeps working after the
// operator has scrubbed off the live edge — commanding a world they are no
// longer looking at.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

const appSrc = readFileSync(
    resolve(dirname(fileURLToPath(import.meta.url)), '../app.ts'),
    'utf8',
);

/** Source between the first `open` at/after `anchor` and its matching `close`. */
function balancedAfter(anchor: string, open: string, close: string): string {
    const start = appSrc.indexOf(anchor);
    expect(start, `anchor not found in app.ts: ${anchor}`).toBeGreaterThan(-1);
    const from = appSrc.indexOf(open, start + anchor.length - 1);
    let depth = 0;
    for (let i = from; i < appSrc.length; i++) {
        if (appSrc[i] === open) depth++;
        else if (appSrc[i] === close && --depth === 0) return appSrc.slice(from + 1, i);
    }
    throw new Error(`unbalanced ${open}${close} after ${anchor}`);
}

/** The body of the handler/options object opened at `anchor`. */
function blockAfter(anchor: string): string {
    return balancedAfter(anchor, '{', '}');
}

/** The argument list of the call opened at `anchor`. */
function argsAfter(anchor: string): string {
    return balancedAfter(anchor, '(', ')');
}

/** The mutating handler bodies the gate has to cover, by the anchor that opens
 *  them: the DVR's server callbacks, the scene pointer handler, and the global
 *  keyboard handler. */
const HANDLERS: ReadonlyArray<readonly [string, string]> = [
    ['DVR server callbacks', 'dvr = new m_dvr.Dvr('],
    ['scene pointer handler', "viz.renderer.domElement.addEventListener('click'"],
    ['global keyboard handler', "window.addEventListener('keydown'"],
];

describe('operator action wiring in app.ts', () => {
    it('constructs one interaction mode and one OperatorActions over it', () => {
        expect(appSrc).toMatch(/new InteractionMode\(\)/);
        expect(appSrc).toMatch(/new OperatorActions\(\s*interactionMode\.guard\s*,/);
        // One gate, not five. A second InteractionMode would be a second answer
        // to "can I mutate right now".
        expect(appSrc.match(/new InteractionMode\(/g)).toHaveLength(1);
        expect(appSrc.match(/new OperatorActions\(/g)).toHaveLength(1);
    });

    it.each(HANDLERS)('routes the %s through operatorActions', (_label, anchor) => {
        expect(blockAfter(anchor)).toContain('operatorActions.');
    });

    it.each(HANDLERS)('keeps direct POST helpers out of the %s', (_label, anchor) => {
        const body = blockAfter(anchor);
        expect(body).not.toMatch(/\bapiPostOrWarn\s*\(/);
        expect(body).not.toMatch(/\bapiPostJson\s*\(/);
        expect(body).not.toMatch(/\bapiPost\s*\(/);
    });

    it.each(HANDLERS)('keeps raw mutation URLs out of the %s', (_label, anchor) => {
        const body = blockAfter(anchor);
        expect(body).not.toContain('/api/sim/mesh/backhaul');
        expect(body).not.toContain('/api/sim/drone/');
        expect(body).not.toContain('/api/sim/reset');
    });

    it('names the backhaul route exactly once, in the injected effect', () => {
        // The effect construction is the one place the URL is allowed to appear;
        // a second occurrence is a handler that started posting on its own again.
        expect(appSrc.match(/\/api\/sim\/mesh\/backhaul/g)).toHaveLength(1);
    });

    it('drives the interaction mode from the DVR live/replay transition', () => {
        const dvrOptions = blockAfter('dvr = new m_dvr.Dvr(');
        expect(dvrOptions).toMatch(/onModeChange/);
        expect(dvrOptions).toContain('interactionMode.goLive()');
        expect(dvrOptions).toContain('interactionMode.enterReplay()');
    });

    it('hands the same gate to every collaborator that owns a mutation', () => {
        // ControlPanel, the transform gizmo, the scene-config panel and the asset
        // panel's command issuer each check the gate themselves; app.ts is what
        // makes it the *same* gate.
        expect(argsAfter('new ControlPanel(')).toContain('interactionMode.guard');
        expect(blockAfter('gizmo = new m_gizmo.TransformGizmo(')).toContain('interactionMode.guard');
        expect(blockAfter('new m_cfg.SceneConfigPanel(')).toContain('interactionMode.guard');
        expect(appSrc).toMatch(/gatedCommandIssuer\(\s*interactionMode\.guard/);
    });
});
