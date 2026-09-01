// SPDX-License-Identifier: Apache-2.0
//
// Comms state on the v2 path.
//
// `SceneSnapshot.backhaulAvailable` was written by the projection and read by
// nothing: one write, zero reads. On this server the backhaul is the *only*
// comms fact actually computed — `isPartitioned` is published as null, meaning
// UNKNOWN — so dropping it left a v2 session with no comms state whatsoever.
// The operator could not tell that the uplink had been cut, a state the v1 path
// has always surfaced with its banner.
//
// What this pins:
//
//  1. **A cut backhaul is surfaced on the v2 path** — it raises a banner, and
//     `projected.backhaulAvailable` actually reaches the code that raises it.
//  2. **Unknown is never rendered as healthy.** An unknown partition raises no
//     banner and claims no connectivity in words; an unknown backhaul never
//     reads as `UP`, and never announces a restoration nobody vouched for.
//  3. **Backhaul-cut and partitioned stay distinguishable.** They are different
//     incidents with different responses: a partitioned mesh has split into
//     pieces that cannot hear each other, while a fully connected mesh with its
//     backhaul cut is a healthy swarm nobody outside it can hear.
//  4. **v1 behaves exactly as before** — same banner text, same `body.partitioned`
//     toggle, same ticker transition, driven off the same single mesh flag.
//
// `app.ts` cannot be imported here — it boots the renderer, opens a SignalR
// connection and touches WebGL at module scope — so the decision function's body
// is lifted out and actually run, and the wiring around it is asserted at the
// source level. Same technique as `appSelectionLifecycle.test.ts`.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

const here = dirname(fileURLToPath(import.meta.url));
const read = (rel: string): string => readFileSync(resolve(here, rel), 'utf8');

const appSrc        = read('../app.ts');
const sceneFrameSrc = read('../assets/sceneFrame.ts');
const htmlSrc       = read('../index.html');
const cssSrc        = read('../styles/main.css');

/** Body of a top-level `function <name>(…)…{ … }`, brace-matched. */
function bodyOf(name: string): string {
    const start = appSrc.indexOf(`function ${name}(`);
    expect(start, `${name} not found in app.ts`).toBeGreaterThan(-1);
    const open = appSrc.indexOf('{', start);
    let depth = 0;
    for (let i = open; i < appSrc.length; i++) {
        if (appSrc[i] === '{') depth++;
        else if (appSrc[i] === '}' && --depth === 0) return appSrc.slice(open + 1, i);
    }
    throw new Error(`unbalanced braces in ${name}`);
}

/** The shape `_commsState` has once lifted out of the source. */
interface Comms {
    readonly backhaul: string;
    readonly partition: string;
    readonly banner: string;
    readonly chip: string;
    readonly chipClass: string;
    readonly title: string;
}
type LiftedComms = (isPartitioned: boolean | null, backhaulAvailable: boolean | null) => Comms;

/**
 * `_commsState` lifted out of `app.ts` and made callable.
 *
 * Its body closes over nothing and carries no type annotations, so it runs
 * as-is. That makes this a test of the real decision — every string the operator
 * can be shown is defined inside it — rather than of the presence of a call.
 */
const comms: LiftedComms = (() => {
    const body = bodyOf('_commsState');
    try {
        return new Function('isPartitioned', 'backhaulAvailable', body) as unknown as LiftedComms;
    } catch (err: unknown) {
        throw new Error(
            '_commsState no longer parses as plain JS — this test lifts its body out '
                + 'and runs it, so the function must stay free of type annotations and '
                + `of module-scope references. Underlying error: ${String(err)}`,
        );
    }
})();

// The three backhaul readings, paired with the partition this server actually
// sends alongside them (unknown).
const UP        = (): Comms => comms(null, true);
const CUT       = (): Comms => comms(null, false);
const UNREPORTED = (): Comms => comms(null, null);

// ─── 1. A cut backhaul reaches the operator on v2 ──────────────────────────

describe('a cut backhaul is surfaced on the v2 path', () => {
    it('raises a banner naming the backhaul', () => {
        const cut = CUT();
        expect(cut.banner).not.toBe('');
        expect(cut.banner.toLowerCase()).toContain('backhaul');
    });

    it('shows the cut on the chip, in words and not by colour alone', () => {
        expect(CUT().chip).toBe('CUT');
        expect(CUT().chipClass).toBe('comms-cut');
        expect(CUT().title.toLowerCase()).toContain('cut');
    });

    it('feeds the projection\'s backhaul into the code that raises it', () => {
        // The whole defect was that this value existed and nobody read it.
        expect(
            /_applyLiveEvents\(\s*projected\.frame,[\s\S]*projected\.backhaulAvailable,?\s*\)/
                .test(appSrc),
            'the v2 snapshot handler does not pass projected.backhaulAvailable to '
                + '_applyLiveEvents; the field is written and never read again',
        ).toBe(true);
        expect(bodyOf('_applyLiveEvents')).toMatch(
            /_commsState\(isPartitioned,\s*backhaulAvailable\)/,
        );
    });

    it('still publishes the backhaul from the projection', () => {
        expect(sceneFrameSrc).toMatch(
            /backhaulAvailable:\s*snapshot\.network\?\.backhaulAvailable\s*\?\?\s*null/,
        );
    });
});

// ─── 2. Unknown is never good news ─────────────────────────────────────────

describe('an unknown partition is never rendered as healthy', () => {
    it('raises no banner when connectivity was never assessed', () => {
        // A banner is a claim. "The mesh is fine" is not a claim this server made.
        expect(UP().banner).toBe('');
        expect(UNREPORTED().banner).toBe('');
    });

    it('says connectivity was not reported rather than that the mesh is whole', () => {
        for (const state of [UP(), CUT(), UNREPORTED()]) {
            expect(state.partition).toBe('unknown');
            expect(state.title).toContain('mesh connectivity not reported');
            expect(state.title).not.toContain('connected segment');
        }
    });

    it('keeps "provably one segment" available for a server that does compute it', () => {
        // false is a real answer and must stay distinct from null; conflating them
        // is the same mistake in the other direction.
        const whole = comms(false, true);
        expect(whole.partition).toBe('whole');
        expect(whole.title).toContain('one connected segment');
        expect(whole.banner).toBe('');
    });

    it('never reads an unreported backhaul as up', () => {
        const unknown = UNREPORTED();
        expect(unknown.backhaul).toBe('unknown');
        expect(unknown.chip).toBe('UNK');
        expect(unknown.chipClass).toBe('comms-unknown');
        expect(unknown.title).toContain('not reported');
    });

    it('tells "up", "cut" and "not reported" apart on the chip', () => {
        // Silence cannot carry this: the banner is empty both when the uplink is
        // up and when it was never reported.
        const chips = [UP().chip, CUT().chip, UNREPORTED().chip];
        expect(new Set(chips).size).toBe(3);
        const classes = [UP().chipClass, CUT().chipClass, UNREPORTED().chipClass];
        expect(new Set(classes).size).toBe(3);
        const titles = [UP().title, CUT().title, UNREPORTED().title];
        expect(new Set(titles).size).toBe(3);
    });

    it('announces no restoration while the backhaul is unknown', () => {
        const body = bodyOf('_applyLiveEvents');
        const guard = body.indexOf("comms.backhaul !== 'unknown'");
        const push  = body.indexOf('eventLog.pushPartition');
        expect(guard, 'the ticker is no longer guarded on a known backhaul').toBeGreaterThan(-1);
        expect(push).toBeGreaterThan(guard);
    });

    it('gives the chip an unknown reading before any frame has arrived', () => {
        expect(htmlSrc).toMatch(/id="hud-comms"[^>]*/);
        expect(htmlSrc).toMatch(/class="hud-stat comms-unknown"/);
        expect(htmlSrc).toMatch(/id="comms-state">UNK</);
    });

    it('styles the three chip states distinctly', () => {
        for (const cls of ['comms-up', 'comms-cut', 'comms-unknown']) {
            expect(cssSrc, `#hud-comms.${cls} has no styling`).toContain(`#hud-comms.${cls}`);
        }
        // Colour reinforces the value text; it must not be the same colour for a
        // healthy link and an unmeasured one.
        const colourOf = (cls: string): string => {
            const m = new RegExp(`#hud-comms\\.${cls}\\s+\\.hud-stat-value\\s*\\{[^}]*color:\\s*([^;]+);`)
                .exec(cssSrc);
            expect(m, `no colour rule for ${cls}`).not.toBeNull();
            return m![1]!.trim();
        };
        const colours = ['comms-up', 'comms-cut', 'comms-unknown'].map(colourOf);
        expect(new Set(colours).size).toBe(3);
    });
});

// ─── 3. Two facts, two states ──────────────────────────────────────────────

describe('backhaul-cut and partitioned are distinguishable', () => {
    const cut   = comms(null, false);   // healthy mesh, nobody outside can hear it
    const split = comms(true, true);    // mesh in pieces, uplink fine

    it('gives them different banner wording', () => {
        expect(cut.banner).not.toBe(split.banner);
        expect(split.banner.toLowerCase()).toContain('partition');
        expect(cut.banner.toLowerCase()).not.toContain('partition');
    });

    it('does not answer either question with the other\'s value', () => {
        expect(cut.partition).toBe('unknown');
        expect(cut.backhaul).toBe('cut');
        expect(split.partition).toBe('split');
        expect(split.backhaul).toBe('up');
        // A partitioned mesh with a working uplink is not a link outage.
        expect(split.chip).toBe('UP');
    });

    it('reports both when both are true', () => {
        const both = comms(true, false);
        expect(both.banner.toLowerCase()).toContain('partition');
        expect(both.banner.toLowerCase()).toContain('backhaul');
        expect(both.chip).toBe('CUT');
    });
});

// ─── 4. The v1 path is untouched ───────────────────────────────────────────

describe('the v1 path behaves exactly as before', () => {
    it('keeps the banner text a v1 session has always shown', () => {
        // v1 has one mesh flag and the server sets it from the backhaul kill
        // switch, so a v1 partition banner has always been a backhaul banner.
        expect(comms(null, false).banner).toBe('Backhaul link down — operating mesh-only');
        expect(comms(null, true).banner).toBe('');
    });

    it('reads the v1 mesh flag as the backhaul, and the partition as unknown', () => {
        expect(appSrc).toMatch(
            /_applyLiveEvents\(frame, drones\.length, null, !\(frame\.mesh\?\.partitioned === true\)\)/,
        );
    });

    it('still toggles body.partitioned every frame and still tickers transitions', () => {
        const body = bodyOf('_applyLiveEvents');
        expect(body).toMatch(/document\.body\.classList\.toggle\('partitioned',/);
        expect(body).toMatch(/eventLog\.pushPartition\(!killed\)/);
        expect(body).toMatch(/_backhaulKilled = killed/);
    });

    it('keeps the banner a live region that only speaks on a change', () => {
        const body = bodyOf('_applyLiveEvents');
        expect(body).toMatch(/comms\.banner !== _commsBanner/);
        expect(body).toMatch(/partitionBanner\.setAttribute\('aria-hidden', String\(comms\.banner === ''\)\)/);
        expect(appSrc).toMatch(/partitionBanner\.setAttribute\('aria-live', 'polite'\)/);
    });
});
