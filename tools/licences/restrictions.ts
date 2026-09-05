// Restriction rule engine for the bake-time licence gate.
//
// WHY THIS EXISTS
//
// v1 of the gate answered one question per layer: is this source's CLASS on the
// allowlist? That is the right question only when a source is wholly usable or
// wholly not. The 2026-09-05 verification sweep found that is rarely true:
//
//   - 3DEP is public domain INSIDE US territory; the seamless grid also covers
//     Canada under NRCan terms.
//   - NASADEM is public domain only once JAXA-sourced pixels are masked out.
//   - ArcticDEM strips are clean CC BY EXCEPT over Alaska from 2022-06-15.
//   - Microsoft footprints are permissive only if fetched after the relicensing.
//
// Every one of those was written in prose in the registry's `notes` field, where
// nothing could enforce it. A rule the build cannot check is a convention, not a
// control. This module turns each into a predicate over (area, tile, layer).
//
// The engine is deliberately total and deliberately fail-closed: an unrecognised
// restriction kind is an ERROR, not a skip. A registry that declares a rule the
// gate does not understand must not quietly pass.

/** [minLon, minLat, maxLon, maxLat], WGS84. */
export type BBox = readonly [number, number, number, number];

export interface Restriction {
    readonly kind: string;
    /** Key into policy.regions. */
    readonly region?: string;
    /** Mask id the layer must declare in `masks`. */
    readonly mask?: string;
    /** EULA clause id the manifest must declare in `eula_clauses`. */
    readonly clause?: string;
    /** ISO YYYY-MM-DD boundary for fetched-after / fetched-before. */
    readonly date?: string;
    /** ISO YYYY-MM-DD from which an exclude-region applies. */
    readonly from?: string;
    /** Permitted values for require-election / require-collection-allowlist. */
    readonly allowed?: readonly string[];
    /** Why this restriction exists. Surfaced verbatim in the failure message. */
    readonly reason?: string;
}

export interface LayerRef {
    readonly layer: string;
    readonly source: string;
    readonly fetched_at: string;
    readonly masks?: readonly string[];
    readonly election?: string;
    readonly collection?: string;
    readonly upstream_licence_header?: string;
}

export interface AreaRef {
    readonly id: string;
    readonly bbox: readonly number[];
}

export interface RegistryPolicy {
    readonly regions?: Readonly<Record<string, readonly BBox[]>>;
}

export interface EvalContext {
    readonly area: AreaRef;
    readonly layer: LayerRef;
    readonly policy: RegistryPolicy;
    /** EULA clause ids the manifest declares the product's legal notice carries. */
    readonly declaredEulaClauses: ReadonlySet<string>;
}

export interface Violation {
    readonly code: string;
    readonly message: string;
}

// --------------------------------------------------------------- geometry

function asBox(b: readonly number[]): BBox | null {
    return b.length === 4 && b.every(Number.isFinite) ? (b as unknown as BBox) : null;
}

/** True when `inner` lies entirely within `outer`. Curated areas are small, so
 *  containment in ONE listed box is the intended test — not in their union. */
export function contains(outer: BBox, inner: BBox): boolean {
    return inner[0] >= outer[0] && inner[1] >= outer[1]
        && inner[2] <= outer[2] && inner[3] <= outer[3];
}

/** True when the boxes share any area. Touching edges do not count. */
export function intersects(a: BBox, b: BBox): boolean {
    return a[0] < b[2] && a[2] > b[0] && a[1] < b[3] && a[3] > b[1];
}

// --------------------------------------------------------------- dates

/** Parses an ISO date or date-time to epoch ms, or null if unusable.
 *  Returning null rather than NaN keeps every comparison explicit — a date the
 *  gate cannot parse must fail closed, not silently compare false. */
export function parseIso(value: string | undefined): number | null {
    if (!value) return null;
    const ms = Date.parse(value);
    return Number.isFinite(ms) ? ms : null;
}

// --------------------------------------------------------------- rules

function regionBoxes(ctx: EvalContext, name: string | undefined): readonly BBox[] | null {
    if (!name) return null;
    const boxes = ctx.policy.regions?.[name];
    return boxes && boxes.length ? boxes : null;
}

type Rule = (r: Restriction, ctx: EvalContext) => Violation | null;

const RULES: Readonly<Record<string, Rule>> = {
    /** Source is licensed only INSIDE the named region. */
    clip(r, ctx) {
        const boxes = regionBoxes(ctx, r.region);
        if (!boxes) {
            return { code: "restriction-unresolvable",
                message: `clip names region "${r.region}", which policy.regions does not define.` };
        }
        const inner = asBox(ctx.area.bbox);
        if (!inner) {
            return { code: "restriction-unresolvable",
                message: `clip needs a 4-number area bbox; got ${JSON.stringify(ctx.area.bbox)}.` };
        }
        if (boxes.some((b) => contains(b, inner))) return null;
        return { code: "outside-licensed-region",
            message: `area bbox is not contained in region "${r.region}". ${r.reason ?? ""}`.trim() };
    },

    /** Source is NOT licensed inside the named region, optionally only from a date. */
    "exclude-region"(r, ctx) {
        const boxes = regionBoxes(ctx, r.region);
        if (!boxes) {
            return { code: "restriction-unresolvable",
                message: `exclude-region names region "${r.region}", which policy.regions does not define.` };
        }
        const inner = asBox(ctx.area.bbox);
        if (!inner) {
            return { code: "restriction-unresolvable",
                message: `exclude-region needs a 4-number area bbox; got ${JSON.stringify(ctx.area.bbox)}.` };
        }
        if (!boxes.some((b) => intersects(b, inner))) return null;

        if (r.from) {
            const from = parseIso(r.from);
            const fetched = parseIso(ctx.layer.fetched_at);
            if (from === null) {
                return { code: "restriction-unresolvable",
                    message: `exclude-region has an unparseable "from" date: ${r.from}` };
            }
            // An unparseable fetch date cannot clear a dated exclusion. Fail closed.
            if (fetched === null) {
                return { code: "restricted-region",
                    message: `area overlaps region "${r.region}" and fetched_at "${ctx.layer.fetched_at}" `
                        + `is unparseable, so it cannot be shown to predate ${r.from}. ${r.reason ?? ""}`.trim() };
            }
            if (fetched < from) return null;
        }
        return { code: "restricted-region",
            message: `area overlaps region "${r.region}"`
                + (r.from ? ` and the data was fetched on or after ${r.from}` : "")
                + `. ${r.reason ?? ""}`.trimEnd() };
    },

    /** The layer must declare that a named mask was applied at bake time. */
    "require-mask"(r, ctx) {
        if (!r.mask) {
            return { code: "restriction-unresolvable", message: `require-mask has no "mask" id.` };
        }
        const applied = ctx.layer.masks ?? [];
        if (applied.includes(r.mask)) return null;
        return { code: "missing-mask",
            message: `layer does not declare mask "${r.mask}" (declares: ${
                applied.length ? applied.join(", ") : "none"}). ${r.reason ?? ""}`.trim() };
    },

    /** Dual-licensed source: the elected limb must be recorded at bake time. */
    "require-election"(r, ctx) {
        const allowed = r.allowed ?? [];
        const elected = ctx.layer.election;
        if (elected && allowed.includes(elected)) return null;
        return { code: "missing-election",
            message: elected
                ? `election "${elected}" is not one of [${allowed.join(", ")}]. ${r.reason ?? ""}`.trim()
                : `no election recorded; must be one of [${allowed.join(", ")}]. ${r.reason ?? ""}`.trim() };
    },

    /** The upstream collection actually queried must be on the allowlist. */
    "require-collection-allowlist"(r, ctx) {
        const allowed = r.allowed ?? [];
        const got = ctx.layer.collection;
        if (got && allowed.includes(got)) return null;
        return { code: "collection-not-allowlisted",
            message: got
                ? `collection "${got}" is not on the allowlist [${allowed.join(", ")}]. ${r.reason ?? ""}`.trim()
                : `no collection recorded; the host serves other missions under different terms. `
                    + `${r.reason ?? ""}`.trim() };
    },

    /** Data received before this date came under different terms. */
    "fetched-after"(r, ctx) {
        const bound = parseIso(r.date);
        const fetched = parseIso(ctx.layer.fetched_at);
        if (bound === null) {
            return { code: "restriction-unresolvable",
                message: `fetched-after has an unparseable date: ${r.date}` };
        }
        if (fetched === null) {
            return { code: "fetched-outside-window",
                message: `fetched_at "${ctx.layer.fetched_at}" is unparseable, so it cannot be shown to `
                    + `fall after ${r.date}. ${r.reason ?? ""}`.trim() };
        }
        if (fetched >= bound) return null;
        return { code: "fetched-outside-window",
            message: `fetched ${ctx.layer.fetched_at}, before ${r.date}. ${r.reason ?? ""}`.trim() };
    },

    /** Data acquired on or after this date is out of the verified scope. */
    "fetched-before"(r, ctx) {
        const bound = parseIso(r.date);
        const fetched = parseIso(ctx.layer.fetched_at);
        if (bound === null) {
            return { code: "restriction-unresolvable",
                message: `fetched-before has an unparseable date: ${r.date}` };
        }
        if (fetched === null) {
            return { code: "fetched-outside-window",
                message: `fetched_at "${ctx.layer.fetched_at}" is unparseable, so it cannot be shown to `
                    + `fall before ${r.date}. ${r.reason ?? ""}`.trim() };
        }
        if (fetched < bound) return null;
        return { code: "fetched-outside-window",
            message: `fetched ${ctx.layer.fetched_at}, on or after ${r.date}. ${r.reason ?? ""}`.trim() };
    },

    /** A clause the product's own legal notice must carry. */
    "require-eula-clause"(r, ctx) {
        if (!r.clause) {
            return { code: "restriction-unresolvable", message: `require-eula-clause has no "clause" id.` };
        }
        if (ctx.declaredEulaClauses.has(r.clause)) return null;
        return { code: "missing-eula-clause",
            message: `the manifest does not declare EULA clause "${r.clause}". ${r.reason ?? ""}`.trim() };
    },
};

/**
 * Evaluates every restriction a source declares against one manifest layer.
 *
 * Returns all violations rather than the first, so a single run reports the whole
 * set. An unknown `kind` is reported as a violation — the gate must not pass a
 * rule it cannot evaluate.
 */
export function evaluateRestrictions(
    restrictions: readonly Restriction[] | undefined,
    ctx: EvalContext,
): Violation[] {
    const out: Violation[] = [];
    for (const r of restrictions ?? []) {
        const rule = RULES[r.kind];
        if (!rule) {
            out.push({ code: "unknown-restriction-kind",
                message: `registry declares restriction kind "${r.kind}", which this gate cannot evaluate. `
                    + `Upgrade the gate or remove the rule; it is not being enforced.` });
            continue;
        }
        const v = rule(r, ctx);
        if (v) out.push(v);
    }
    return out;
}

/** Restriction kinds this build understands. Exported so a test can assert the
 *  registry never declares one the engine has no rule for. */
export const KNOWN_KINDS: readonly string[] = Object.keys(RULES);
