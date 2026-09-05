// ResQ Viz - scenario card catalog: the rail's copy, order, grouping, hotkeys
// SPDX-License-Identifier: Apache-2.0
//
// Card copy used to be reverse-engineered from `ScenarioEnvironment.displayName`
// by splitting on an em dash. That made rail text a side effect of the
// terrain/sky physics table; it could not serve the presets that must never have
// an environment (`applyScenarioEnvironment` returning false for a dev fixture is
// a deliberate contract); and it failed silently to the literal string "preset".
// Four more cards were hand-written in `index.html` in sentence case, so the rail
// showed two labelling systems side by side.
//
// Labels are authored ALL CAPS in the DATA, never via `text-transform`. A CSS
// transform would uppercase the markup labels too and mask the drift rather than
// fix it; the accessible name and any export read the authored string rather than
// the painted one; a test can assert exact rendered text only if the data carries
// it; and caps make acronym mis-casing structurally impossible — which is what
// turned a humanised id into "Multi Agency Sar".
//
// Counts and domains are transcribed from `appsettings.json > Scenarios` and
// verified against it. Two traps for anyone re-deriving them: `_comment` rides on
// the first real asset object rather than being its own entry, and the domain key
// is lowercase `domain` with no count field, so one entry means one asset.

/**
 * Which chip a scenario answers to.
 *
 * `multi` means "fields more than one domain", which is checkable against the
 * row's own domain chips — so the chip cannot quietly come to mean two things.
 */
export type ScenarioGroup = 'disaster' | 'multi' | 'dev';

/** One row's copy and facts. */
export interface ScenarioCard {
    /** Rail label. ALL CAPS, at most 15 characters. */
    readonly label: string;
    /**
     * One-line operator context, rendered as the row's `title` and never as a
     * second line: a second line costs height on every one of nineteen rows and
     * re-opens the ragged-height problem a fixed row exists to close.
     */
    readonly situation: string;
    /** Assets the preset places. */
    readonly count: number;
    /** Domains present, a subset of `AGS` in that order. */
    readonly dom: string;
    readonly group: ScenarioGroup;
    /** Digit bound in `ControlPanel._bindKeyboard`, if any. */
    readonly hotkey?: string;
}

/**
 * Render order for the rail, with groups contiguous.
 *
 * Disasters lead and the multi-domain presets follow, because those are what this
 * build exists to show. The dev fixtures and load rigs land last: they are the
 * ones carrying hotkeys, so they stay reachable without the best real estate.
 */
export const SCENARIO_CARDS: ReadonlyMap<string, ScenarioCard> = new Map<string, ScenarioCard>([
    ['wildfire-interface', { label: 'WILDFIRE', situation: 'Recon ring over a wildland-urban front', count: 5, dom: 'A', group: 'disaster' }],
    ['hurricane-melissa', { label: 'HURRICANE', situation: 'Storm ISR ring at landfall', count: 6, dom: 'A', group: 'disaster' }],
    ['flood-riverine', { label: 'RIVER FLOOD', situation: 'Survey pass over an inundated valley', count: 5, dom: 'A', group: 'disaster' }],
    ['urban-collapse', { label: 'URBAN COLLAPSE', situation: 'Structure search over a collapse zone', count: 6, dom: 'A', group: 'disaster' }],
    ['alpine-sar', { label: 'ALPINE SAR', situation: 'Avalanche search on high ground', count: 4, dom: 'A', group: 'disaster' }],
    ['canyon-sar', { label: 'CANYON SAR', situation: 'Slot-gorge search under tight walls', count: 4, dom: 'A', group: 'disaster' }],
    // Twelve air assets drawn from three agencies: multi-AGENCY, not
    // multi-DOMAIN. Filing it under `multi` would make that chip mean two
    // different things, and the row's own domain chips would contradict it.
    ['multi-agency-sar', { label: 'MULTI-AGENCY', situation: 'Three agencies, one shared air picture', count: 12, dom: 'A', group: 'disaster', hotkey: '5' }],
    ['flood-response', { label: 'FLOOD RESCUE', situation: 'Boats and rovers under air overwatch', count: 8, dom: 'AGS', group: 'multi' }],
    ['coastal-search', { label: 'COASTAL SEARCH', situation: 'Offshore sweep for a person in water', count: 8, dom: 'AGS', group: 'multi' }],
    ['port-incident', { label: 'PORT INCIDENT', situation: 'Harbor response across three domains', count: 8, dom: 'AGS', group: 'multi' }],
    ['coastal-transit', { label: 'CHANNEL RUN', situation: 'Surface transit under one air escort', count: 4, dom: 'AS', group: 'multi' }],
    ['ground-convoy', { label: 'GROUND CONVOY', situation: 'Rover column climbing a graded ascent', count: 4, dom: 'AG', group: 'multi' }],
    ['mixed-ground', { label: 'COMBINED TEAM', situation: 'Rovers and air working one hillside', count: 6, dom: 'AG', group: 'multi' }],
    ['single', { label: 'SINGLE', situation: 'One drone, the smallest live world', count: 1, dom: 'A', group: 'dev', hotkey: '1' }],
    ['swarm-5', { label: 'SWARM 5', situation: 'Five-drone formation and mesh drill', count: 5, dom: 'A', group: 'dev', hotkey: '2' }],
    ['swarm-20', { label: 'SWARM 20', situation: 'Twenty-drone saturation trial', count: 20, dom: 'A', group: 'dev', hotkey: '3' }],
    ['sar', { label: 'SAR SWEEP', situation: 'Lead, scout and relay in a search box', count: 3, dom: 'A', group: 'dev', hotkey: '4' }],
    ['link-loss-divergence', { label: 'LINK LOSS', situation: 'Mesh divergence when the link drops', count: 3, dom: 'AGS', group: 'dev' }],
    ['mixed-load-150', { label: 'LOAD 150', situation: '150 agents, 50 per domain, under load', count: 150, dom: 'AGS', group: 'dev' }],
]);

/** Catalog order, so the rail lays out independently of server list order. */
export const SCENARIO_ORDER: readonly string[] = [...SCENARIO_CARDS.keys()];

/** Sticky heading text per group. */
export const SCENARIO_GROUP_LABELS: ReadonlyMap<ScenarioGroup, string> = new Map([
    ['disaster', 'Disaster response'],
    ['multi', 'Multi-domain'],
    ['dev', 'Dev & load'],
] as const);

/**
 * `KeyboardEvent.code` to scenario id.
 *
 * Derived from the catalog rather than restated, so the badge painted on a row
 * and the key that actually starts it cannot drift apart.
 */
export const SCENARIO_HOTKEYS: ReadonlyMap<string, string> = new Map(
    [...SCENARIO_CARDS]
        .filter(([, card]) => card.hotkey !== undefined)
        .map(([id, card]) => [`Digit${card.hotkey}`, id]),
);

const DOM_WORDS: Readonly<Record<string, string>> = { A: 'AIR', G: 'GND', S: 'SEA' };
const DOM_SPOKEN: Readonly<Record<string, string>> = { A: 'air', G: 'ground', S: 'surface' };

/**
 * The card for a scenario id, or a visible gap for one this build has not heard of.
 *
 * A preset the server offers but the catalog does not know renders its id upcased
 * — acronym-safe by construction — with an em dash where the count goes, and
 * lands in the dev group, which is where an unvetted preset belongs. That reads
 * as a gap on purpose; the old fallback printed the word "preset" as a
 * description, which looked like a real card carrying no information.
 *
 * @param id Scenario id from the server.
 * @returns The catalog entry, or a fallback describing an unlisted preset.
 */
export function scenarioCardFor(id: string): ScenarioCard {
    return SCENARIO_CARDS.get(id) ?? {
        label: id.replace(/-/g, ' ').toUpperCase(),
        situation: 'Unlisted preset',
        count: 0,
        dom: '',
        group: 'dev',
    };
}

/**
 * Visible domain chips, in fixed A-G-S order.
 *
 * An absent domain yields no chip at all, so the WIDTH of the chip run carries
 * the fact and the column can be read down without a legend.
 *
 * @param dom Domain string from a catalog entry.
 * @returns One word per domain present.
 */
export function domainWords(dom: string): string[] {
    return [...dom].map(c => DOM_WORDS[c] ?? c);
}

/**
 * Accessible name for a row.
 *
 * The caps are for the eye. Some screen readers spell a short all-caps token out
 * letter by letter, so the spoken form is sentence case and names the domains as
 * words rather than as the three-letter chips.
 *
 * @param card Catalog entry for the row.
 * @returns A name giving the preset, its size and its domains.
 */
export function scenarioSpokenName(card: ScenarioCard): string {
    const spoken = card.label.charAt(0) + card.label.slice(1).toLowerCase();
    if (card.count === 0) return `${spoken}, unlisted preset`;
    const words = [...card.dom].map(c => DOM_SPOKEN[c] ?? c);
    const domains = words.length > 1
        ? `${words.slice(0, -1).join(', ')} and ${words[words.length - 1]}`
        : words[0];
    return `${spoken}, ${card.count} asset${card.count === 1 ? '' : 's'}, ${domains}`;
}

/** Hover context, which doubles as the fallback for a label the rail truncates. */
export function scenarioTitle(card: ScenarioCard): string {
    return `${card.label} — ${card.situation}`;
}
