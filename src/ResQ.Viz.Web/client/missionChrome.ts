// ResQ Viz - Mission chrome (top-center scenario / time / phase strip)
// SPDX-License-Identifier: Apache-2.0
//
// Narrative strip rendered above the center of the canvas. Reads its state
// from the last scenario-start event plus the live sim-time in each frame.
//
//     MULTI-AGENCY-SAR   ·   T+ 00:42   ·   DETECTION
//
// Phase labels are time-gated on the scenario's elapsed clock for demo
// framing; they are not a real consensus-round signal yet. Wired to be
// swapped for a server-driven phase once `MeshVizState` exposes it.

/** Phase cutoffs (elapsed seconds) — match the narrative sequence in the spec. */
const PHASES: { at: number; label: string }[] = [
    { at:   0, label: 'Recon'      },
    { at:  15, label: 'Detection'  },
    { at:  45, label: 'Engagement' },
    { at:  90, label: 'RTL'        },
];

function phaseAt(t: number): string {
    let current = PHASES[0]?.label ?? '';
    for (const p of PHASES) {
        if (t >= p.at) current = p.label;
        else break;
    }
    return current;
}

/** Pretty-prints elapsed seconds as `HH:MM:SS` (or `MM:SS` under 1h). */
function formatElapsed(sec: number): string {
    const s = Math.max(0, Math.floor(sec));
    const hh = Math.floor(s / 3600);
    const mm = Math.floor((s % 3600) / 60);
    const ss = s % 60;
    const p2 = (n: number) => n < 10 ? `0${n}` : String(n);
    return hh > 0 ? `${p2(hh)}:${p2(mm)}:${p2(ss)}` : `${p2(mm)}:${p2(ss)}`;
}

export class MissionChrome {
    private readonly _el: HTMLDivElement;
    private readonly _nameEl: HTMLSpanElement;
    private readonly _timeEl: HTMLSpanElement;
    private readonly _phaseEl: HTMLSpanElement;

    private _scenarioName: string | null = null;
    /** Sim time at which the current scenario was anchored as t=0. */
    private _scenarioStartTime = 0;
    /** True until the first frame arrives after a scenario-start. */
    private _needsAnchor = false;

    constructor() {
        this._el = document.createElement('div');
        this._el.className = 'mission-chrome hidden';
        this._el.setAttribute('aria-hidden', 'true');

        this._nameEl  = document.createElement('span');
        this._timeEl  = document.createElement('span');
        this._phaseEl = document.createElement('span');
        this._nameEl.className  = 'mc-name';
        this._timeEl.className  = 'mc-time';
        this._phaseEl.className = 'mc-phase';

        const sep1 = document.createElement('span');
        const sep2 = document.createElement('span');
        sep1.className = 'mc-sep';
        sep2.className = 'mc-sep';
        sep1.textContent = '·';
        sep2.textContent = '·';

        this._el.append(this._nameEl, sep1, this._timeEl, sep2, this._phaseEl);
        document.body.appendChild(this._el);

        document.addEventListener('resq:scenario-start', (ev) => {
            const name = (ev as CustomEvent<{ name: string }>).detail?.name;
            if (name) this._onScenarioStart(name);
        });
    }

    /**
     * Call each frame with the authoritative sim-time from the VizFrame.
     * First frame after a scenario-start anchors t=0 to the current sim
     * clock so elapsed counts from _now_, not from whatever the long-
     * running sim clock had already accumulated.
     */
    update(simTime: number): void {
        if (this._scenarioName === null) return;
        if (this._needsAnchor) {
            this._scenarioStartTime = simTime;
            this._needsAnchor = false;
        }
        const elapsed = Math.max(0, simTime - this._scenarioStartTime);
        this._timeEl.textContent  = `T+ ${formatElapsed(elapsed)}`;
        this._phaseEl.textContent = phaseAt(elapsed);
    }

    private _onScenarioStart(name: string): void {
        this._scenarioName = name;
        this._needsAnchor = true;
        this._nameEl.textContent  = name.toUpperCase();
        this._timeEl.textContent  = 'T+ 00:00';
        this._phaseEl.textContent = PHASES[0]?.label ?? '';
        this._el.classList.remove('hidden');
        this._el.setAttribute('aria-hidden', 'false');
    }
}
