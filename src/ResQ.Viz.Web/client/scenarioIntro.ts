// ResQ Viz - Scenario intro overlay
// SPDX-License-Identifier: Apache-2.0
//
// Full-screen title card that fades in on scenario start, holds briefly, then
// fades out to the live HUD. Drives the narrative frame for demo recordings —
// particularly the multi-agency-sar scenario.

interface IntroCopy {
    title: string;
    subtitle: string;
    /** Optional kicker shown above the title in DM Mono. */
    kicker?: string;
}

const COPY: Record<string, IntroCopy> = {
    'multi-agency-sar': {
        kicker:   'Hurricane response · Unified air picture',
        title:    'HURRICANE MELISSA',
        subtitle: '3 Agencies · 12 Drones · 1 Air Picture',
    },
    'swarm-5': {
        kicker:   'Formation flight · 5 drones',
        title:    'SWARM DRILL',
        subtitle: 'Formation integrity · mesh rebuild',
    },
    'swarm-20': {
        kicker:   'High-density swarm · 20 drones',
        title:    'SATURATION TRIAL',
        subtitle: 'Coordination under load',
    },
    sar: {
        kicker:   'Search and rescue · 3 drones',
        title:    'SAR SWEEP',
        subtitle: 'Lead · Scout · Relay',
    },
};

// Timing (ms). Total ≈ 4s per spec.
const FADE_IN_MS   = 500;
const HOLD_MS      = 2500;
const FADE_OUT_MS  = 900;

export class ScenarioIntro {
    private readonly _el: HTMLDivElement;
    private readonly _kickerEl: HTMLSpanElement;
    private readonly _titleEl: HTMLSpanElement;
    private readonly _subtitleEl: HTMLSpanElement;

    private _holdTimer:   number | null = null;
    private _fadeOutTimer: number | null = null;

    constructor() {
        this._el = document.createElement('div');
        this._el.className = 'scenario-intro';
        this._el.setAttribute('role', 'status');
        this._el.setAttribute('aria-live', 'polite');
        this._el.setAttribute('aria-atomic', 'true');

        const inner = document.createElement('div');
        inner.className = 'scenario-intro-inner';

        this._kickerEl   = document.createElement('span');
        this._titleEl    = document.createElement('span');
        this._subtitleEl = document.createElement('span');
        this._kickerEl.className   = 'scenario-intro-kicker';
        this._titleEl.className    = 'scenario-intro-title';
        this._subtitleEl.className = 'scenario-intro-subtitle';

        inner.append(this._kickerEl, this._titleEl, this._subtitleEl);
        this._el.appendChild(inner);
        document.body.appendChild(this._el);

        document.addEventListener('resq:scenario-start', (ev) => {
            const name = (ev as CustomEvent<{ name: string }>).detail?.name;
            if (name) this.play(name);
        });
    }

    /**
     * Play the intro for the named scenario. Falls back silently if no
     * copy is registered (keeps the behaviour opt-in per scenario).
     */
    play(name: string): void {
        const copy = COPY[name];
        if (!copy) return;

        // Cancel any in-flight animation so rapid scenario switches don't
        // leave stale timers that'd hide a newer intro mid-hold.
        if (this._holdTimer    !== null) window.clearTimeout(this._holdTimer);
        if (this._fadeOutTimer !== null) window.clearTimeout(this._fadeOutTimer);

        // Hide the kicker element entirely when empty so its margin-bottom
        // doesn't push the title off-center for scenarios that skip the kicker.
        this._kickerEl.textContent   = copy.kicker ?? '';
        this._kickerEl.style.display = copy.kicker ? 'block' : 'none';
        this._titleEl.textContent    = copy.title;
        this._subtitleEl.textContent = copy.subtitle;

        this._el.style.transition = `opacity ${FADE_IN_MS}ms ease-out`;
        this._el.classList.add('visible');

        this._holdTimer = window.setTimeout(() => {
            this._el.style.transition = `opacity ${FADE_OUT_MS}ms ease-in`;
            this._el.classList.remove('visible');
        }, FADE_IN_MS + HOLD_MS);

        // Clear any inline transition once fully hidden so the CSS default
        // takes over for subsequent toggles.
        this._fadeOutTimer = window.setTimeout(() => {
            this._el.style.transition = '';
        }, FADE_IN_MS + HOLD_MS + FADE_OUT_MS + 50);
    }
}
