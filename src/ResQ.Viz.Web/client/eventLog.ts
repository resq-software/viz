// ResQ Viz - Event log (left-edge SIGINT ticker)
// SPDX-License-Identifier: Apache-2.0
//
// Rolling buffer of timestamped events rendered bottom-up on the left
// edge of the canvas. Makes the swarm feel alive in screen recordings —
// scenario starts, mesh partitions, detections, etc. pop as one-liners
// so a viewer without voiceover knows what's happening.
//
//     12:03:42  [OP]    Scenario begin · multi-agency-sar
//     12:03:44  [MESH]  Backhaul link restored
//
// Auto-subscribes to `resq:scenario-start` so ScenarioIntro /
// MissionChrome / EventLog all fire off the same dispatch.

const MAX_ROWS = 8;
const FADE_MS  = 600;

export type EventLevel = 'info' | 'mesh' | 'sar' | 'alert';

interface EventOptions {
    level?: EventLevel;
    /** Short tag shown in brackets. Defaults to an auto-label per level. */
    tag?: string;
}

const DEFAULT_TAG: Record<EventLevel, string> = {
    info:  'SYS',
    mesh:  'MESH',
    sar:   'SAR',
    alert: 'ALERT',
};

function clockStamp(d: Date = new Date()): string {
    const p2 = (n: number) => n < 10 ? `0${n}` : String(n);
    return `${p2(d.getHours())}:${p2(d.getMinutes())}:${p2(d.getSeconds())}`;
}

export class EventLog {
    private readonly _el: HTMLDivElement;

    constructor() {
        this._el = document.createElement('div');
        this._el.className = 'event-log';
        this._el.setAttribute('role', 'log');
        this._el.setAttribute('aria-live', 'polite');
        this._el.setAttribute('aria-relevant', 'additions');
        document.body.appendChild(this._el);

        document.addEventListener('resq:scenario-start', (ev) => {
            const name = (ev as CustomEvent<{ name: string }>).detail?.name;
            if (name) {
                this.push(`Scenario begin · ${name}`, { level: 'info', tag: 'OP' });
            }
        });
    }

    /**
     * Append a one-line event. Oldest row evicted once the buffer hits
     * `MAX_ROWS`. Each row fades in on insertion and inherits a level-
     * specific accent color.
     */
    push(message: string, opts: EventOptions = {}): void {
        const level = opts.level ?? 'info';
        const tag   = opts.tag ?? DEFAULT_TAG[level];

        const row = document.createElement('div');
        row.className = `el-row el-${level} el-enter`;

        const time = document.createElement('span');
        time.className = 'el-time';
        time.textContent = clockStamp();

        const tagEl = document.createElement('span');
        tagEl.className = 'el-tag';
        tagEl.textContent = `[${tag}]`;

        const msg = document.createElement('span');
        msg.className = 'el-msg';
        msg.textContent = message;

        row.append(time, tagEl, msg);
        this._el.appendChild(row);

        // Transition in on next frame so the browser commits the initial
        // `.el-enter` styles before swapping to the target opacity.
        requestAnimationFrame(() => row.classList.remove('el-enter'));

        // Cap the buffer. `children` is live; evict from the front so
        // removal doesn't shift the target.
        while (this._el.children.length > MAX_ROWS) {
            this._el.firstElementChild?.remove();
        }
    }

    /** Convenience for mesh partition transitions. */
    pushPartition(up: boolean): void {
        this.push(up ? 'Backhaul link restored' : 'Backhaul link lost — mesh only', {
            level: up ? 'mesh' : 'alert',
        });
    }
}

// Re-export helpers for tests / future callers that want stamps.
export { clockStamp, FADE_MS };
