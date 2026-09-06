// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the transport bar's pure speed-cycle logic plus its share of
// the live/replay mutation gate. The Transport DOM class is compatibility code
// — the active bottom bar is the DVR — but it still owns four server mutations,
// so it takes the same gate every other boundary does rather than being trusted
// because nothing currently constructs it.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { nextSpeed, SPEEDS, Transport } from '../editor/transport';
import type { MutationGate } from '../operator/interactionMode';

describe('nextSpeed', () => {
    it('cycles 1 → 2 → 4 → 1', () => {
        expect(nextSpeed(1)).toBe(2);
        expect(nextSpeed(2)).toBe(4);
        expect(nextSpeed(4)).toBe(1);
    });

    it('resets to the first speed for an unknown current value', () => {
        expect(nextSpeed(3)).toBe(1);
        expect(nextSpeed(8)).toBe(1);
        expect(nextSpeed(0)).toBe(1);
    });

    it('only exposes the documented speeds', () => {
        expect([...SPEEDS]).toEqual([1, 2, 4]);
    });
});

const REPLAY_GATE: MutationGate = (action) => ({
    success: false,
    error: { kind: 'replay', code: 'interaction.replay', action },
});

function click(selector: string): void {
    document.querySelector<HTMLButtonElement>(selector)
        ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

/** Presses play/pause, step, speed and reset — every mutation the bar owns. */
function driveEveryTransportButton(): void {
    for (const cls of ['.rt-play', '.rt-step', '.rt-speed', '.rt-reset']) click(cls);
}

describe('Transport replay gate', () => {
    beforeEach(() => document.body.replaceChildren());
    afterEach(() => vi.unstubAllGlobals());

    it('posts each transport mutation at the live edge', () => {
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new Transport();

        driveEveryTransportButton();

        const urls = fetchMock.mock.calls.map(call => String(call[0]));
        expect(urls).toContain('/api/sim/pause');
        expect(urls).toContain('/api/sim/step');
        expect(urls).toContain('/api/sim/speed');
        expect(urls).toContain('/api/sim/reset');
    });

    it('posts nothing while replaying', () => {
        const fetchMock = vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        new Transport(REPLAY_GATE);

        driveEveryTransportButton();

        expect(fetchMock).not.toHaveBeenCalled();
    });

    it('does not fake an optimistic state change the gate refused', () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 })));
        new Transport(REPLAY_GATE);
        const play = document.querySelector<HTMLButtonElement>('.rt-play')!;
        const speed = document.querySelector<HTMLButtonElement>('.rt-speed')!;

        driveEveryTransportButton();

        // Running, 1× — exactly what it read before the refused presses.
        expect(play.getAttribute('aria-label')).toBe('Pause');
        expect(speed.textContent).toBe('1×');
    });

    it('still reconciles displayed state from an authoritative frame in replay', () => {
        vi.stubGlobal('fetch', vi.fn<typeof fetch>().mockResolvedValue(new Response(null, { status: 200 })));
        const transport = new Transport(REPLAY_GATE);

        transport.update({ t: 0, drones: [], paused: true, speed: 4 } as never);

        expect(document.querySelector('.rt-play')?.getAttribute('aria-label')).toBe('Play');
        expect(document.querySelector('.rt-speed')?.textContent).toBe('4×');
    });
});
