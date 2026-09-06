// Tests for the bake-time licence gate.
//
//   node --test --experimental-strip-types tools/licences/
//
// TWO THINGS THIS SUITE EXISTS TO PREVENT
//
// 1. A rule that cannot fail. The v1 gate declared the Alaska carve-out, the
//    dual-licence election and the upstream-header cross-check in prose, and
//    enforced none of them. Every restriction below therefore has BOTH a case
//    that must fail and a case that must pass — a gate that rejects everything
//    is as useless as one that rejects nothing, and only the pair distinguishes
//    a working rule from a broken one.
//
// 2. Drift between the registry and the engine. "declares only restriction kinds
//    this gate can evaluate" is the load-bearing test: it fails the moment
//    someone adds a rule to licences.json that no code evaluates, which is
//    exactly how the inert rules got there the first time.

import { deepStrictEqual, ok, strictEqual } from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, mkdirSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, it } from "node:test";

import {
    contains, evaluateRestrictions, intersects, KNOWN_KINDS, parseIso,
    type EvalContext, type Restriction,
} from "./restrictions.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const GATE = join(HERE, "check-licences.ts");
const REGISTRY = join(HERE, "licences.json");

// --------------------------------------------------------------- helpers

const ALASKA: [number, number, number, number] = [-172.5, 51.2, -129.9, 71.5];
const CONUS: [number, number, number, number] = [-124.85, 24.35, -66.85, 49.4];

function ctx(over: Partial<EvalContext> = {}): EvalContext {
    return {
        area: { id: "a", bbox: [-100, 35, -99, 36] },
        layer: { layer: "elevation", source: "s", fetched_at: "2026-08-01T00:00:00Z" },
        policy: { regions: { alaska: [ALASKA], "us-territory": [CONUS] } },
        declaredEulaClauses: new Set<string>(),
        ...over,
    };
}

const codes = (rs: Restriction[], c: EvalContext) => evaluateRestrictions(rs, c).map((v) => v.code);

/** sha256 of the single byte the fixture writes, so the content-hash check passes
 *  and every failure below is attributable to the rule under test. */
const TILE_SHA = "2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881";

function manifestWith(layer: Record<string, unknown>, bbox: number[], extra: Record<string, unknown> = {}) {
    return {
        schema: 1, generated_at: "2026-09-05T00:00:00Z", generator: "test/1", ...extra,
        areas: [{
            id: "a", name: "A", bbox,
            tiles: [{ path: "data/tiles/t.tif", sha256: TILE_SHA, layers: [layer] }],
        }],
    };
}

/** Runs the real gate over a throwaway tree. */
function runGate(manifest: Record<string, unknown>): { passed: boolean; codes: string[] } {
    const root = mkdtempSync(join(tmpdir(), "licgate-"));
    mkdirSync(join(root, "data", "tiles"), { recursive: true });
    writeFileSync(join(root, "data", "tiles", "t.tif"), "x");
    writeFileSync(join(root, "m.json"), JSON.stringify(manifest));
    const r = spawnSync(process.execPath,
        ["--experimental-strip-types", GATE, "--root", root,
            "--registry", REGISTRY, "--manifest", join(root, "m.json")],
        { encoding: "utf8" });
    const out = `${r.stdout}\n${r.stderr}`;
    return {
        passed: out.includes("Licence gate passed"),
        codes: [...out.matchAll(/^ERROR {2}([a-z-]+)/gm)].map((m) => m[1]!),
    };
}

// --------------------------------------------------------------- geometry

describe("geometry", () => {
    it("contains is containment, not overlap", () => {
        ok(contains(CONUS, [-100, 35, -99, 36]));
        // Straddling the border is NOT contained — the whole point of the clip
        // rule, since a straddling tile carries foreign-government pixels.
        ok(!contains(CONUS, [-125.5, 48, -124, 49]));
    });

    it("intersects does not count a shared edge", () => {
        ok(intersects(ALASKA, [-149, 70, -148, 70.5]));
        ok(!intersects(ALASKA, [-129.9, 51.2, -120, 60]), "touching the eastern edge is not overlap");
    });
});

describe("parseIso", () => {
    it("returns null rather than NaN for unusable input", () => {
        strictEqual(parseIso(undefined), null);
        strictEqual(parseIso("not-a-date"), null);
        ok(typeof parseIso("2022-06-15") === "number");
    });

    it("rejects impossible calendar dates instead of rolling them over", () => {
        // Date.parse alone accepts these and silently moves them: "2026-02-30"
        // becomes 2 March and "2026-06-31" becomes 1 July. A licence boundary
        // that quietly shifts by a day or two is worse than one that fails.
        strictEqual(parseIso("2026-02-30"), null);
        strictEqual(parseIso("2026-06-31"), null);
        strictEqual(parseIso("2026-13-01"), null);
        strictEqual(parseIso("2026-00-10"), null);
        // A real leap day must still parse.
        ok(typeof parseIso("2024-02-29") === "number");
        strictEqual(parseIso("2026-02-29"), null, "2026 is not a leap year");
    });

    it("still accepts a full ISO date-time", () => {
        ok(typeof parseIso("2026-08-01T00:00:00Z") === "number");
    });
});

describe("malformed bbox", () => {
    // Not a bypass — an inverted box still failed clip and still tripped
    // exclude-region before this validation, both measured. But a rule that
    // answers a licence question from coordinates that cannot describe a place
    // on Earth is guessing, so the engine now refuses rather than evaluates.
    const clip: Restriction[] = [{ kind: "clip", region: "us-territory" }];

    it("is unresolvable rather than silently evaluated", () => {
        for (const bad of [
            [10, 5, 0, 1],        // corners inverted
            [-200, 0, -190, 1],   // longitude out of range
            [0, -100, 1, -95],    // latitude out of range
            [0, 0, 1],            // too few components
        ]) {
            deepStrictEqual(
                codes(clip, ctx({ area: { id: "a", bbox: bad } })),
                ["restriction-unresolvable"],
                `bbox ${JSON.stringify(bad)}`,
            );
        }
    });

    it("does not let an inverted box evade an exclusion", () => {
        const r: Restriction[] = [{ kind: "exclude-region", region: "alaska", from: "2022-06-15" }];
        // Same footprint, corners swapped. Must not come back clean.
        deepStrictEqual(codes(r, ctx({ area: { id: "a", bbox: [-148, 70.5, -149, 70] } })),
            ["restriction-unresolvable"]);
    });
});

// --------------------------------------------------------------- rules

describe("clip", () => {
    const r: Restriction[] = [{ kind: "clip", region: "us-territory" }];
    it("passes inside the licensed region", () =>
        deepStrictEqual(codes(r, ctx()), []));
    it("fails outside it", () =>
        deepStrictEqual(codes(r, ctx({ area: { id: "a", bbox: [-113, 52, -112, 53] } })), ["outside-licensed-region"]));
    it("fails when the region is undefined, rather than passing", () =>
        deepStrictEqual(codes([{ kind: "clip", region: "nowhere" }], ctx()), ["restriction-unresolvable"]));
});

describe("exclude-region", () => {
    const r: Restriction[] = [{ kind: "exclude-region", region: "alaska", from: "2022-06-15" }];
    const inAlaska = { id: "a", bbox: [-149, 70, -148, 70.5] };

    it("fails inside the region on or after the date", () =>
        deepStrictEqual(codes(r, ctx({ area: inAlaska })), ["restricted-region"]));
    it("passes inside the region BEFORE the date", () =>
        deepStrictEqual(codes(r, ctx({
            area: inAlaska,
            layer: { layer: "elevation", source: "s", fetched_at: "2021-05-01T00:00:00Z" },
        })), []));
    it("passes outside the region after the date", () =>
        deepStrictEqual(codes(r, ctx({ area: { id: "a", bbox: [8, 60, 9, 61] } })), []));
    it("fails closed when the fetch date is unparseable", () =>
        deepStrictEqual(codes(r, ctx({
            area: inAlaska,
            layer: { layer: "elevation", source: "s", fetched_at: "whenever" },
        })), ["restricted-region"]));
});

describe("require-mask", () => {
    const r: Restriction[] = [{ kind: "require-mask", mask: "prism-cded" }];
    it("fails when the mask is not declared", () =>
        deepStrictEqual(codes(r, ctx()), ["missing-mask"]));
    it("fails when a DIFFERENT mask is declared", () =>
        deepStrictEqual(codes(r, ctx({
            layer: { layer: "elevation", source: "s", fetched_at: "2026-08-01T00:00:00Z", masks: ["something-else"] },
        })), ["missing-mask"]));
    it("passes when declared", () =>
        deepStrictEqual(codes(r, ctx({
            layer: { layer: "elevation", source: "s", fetched_at: "2026-08-01T00:00:00Z", masks: ["prism-cded"] },
        })), []));
});

describe("require-election", () => {
    const r: Restriction[] = [{ kind: "require-election", allowed: ["CC-BY-4.0"] }];
    it("fails with no election", () => deepStrictEqual(codes(r, ctx()), ["missing-election"]));
    it("fails when the elected limb is the other one", () =>
        deepStrictEqual(codes(r, ctx({
            layer: { layer: "buildings", source: "s", fetched_at: "2026-08-01T00:00:00Z", election: "ODbL-1.0" },
        })), ["missing-election"]));
    it("passes with the permitted election", () =>
        deepStrictEqual(codes(r, ctx({
            layer: { layer: "buildings", source: "s", fetched_at: "2026-08-01T00:00:00Z", election: "CC-BY-4.0" },
        })), []));
});

describe("fetched-after / fetched-before", () => {
    it("fetched-after fails for data taken before the relicensing", () =>
        deepStrictEqual(codes([{ kind: "fetched-after", date: "2026-03-11" }], ctx({
            layer: { layer: "buildings", source: "s", fetched_at: "2025-06-01T00:00:00Z" },
        })), ["fetched-outside-window"]));
    it("fetched-after passes on the boundary date itself", () =>
        deepStrictEqual(codes([{ kind: "fetched-after", date: "2026-03-11" }], ctx({
            layer: { layer: "buildings", source: "s", fetched_at: "2026-03-11T00:00:00Z" },
        })), []));
    it("fetched-before fails on the boundary date itself", () =>
        deepStrictEqual(codes([{ kind: "fetched-before", date: "2025-01-01" }], ctx({
            layer: { layer: "imagery", source: "s", fetched_at: "2025-01-01T00:00:00Z" },
        })), ["fetched-outside-window"]));
});

describe("require-collection-allowlist", () => {
    const r: Restriction[] = [{ kind: "require-collection-allowlist", allowed: ["SENTINEL-2-L2A"] }];
    it("fails when no collection was recorded", () =>
        deepStrictEqual(codes(r, ctx()), ["collection-not-allowlisted"]));
    it("fails for a different mission served by the same host", () =>
        deepStrictEqual(codes(r, ctx({
            layer: { layer: "imagery", source: "s", fetched_at: "2026-08-01T00:00:00Z", collection: "LANDSAT-8" },
        })), ["collection-not-allowlisted"]));
    it("passes for the allowlisted collection", () =>
        deepStrictEqual(codes(r, ctx({
            layer: { layer: "imagery", source: "s", fetched_at: "2026-08-01T00:00:00Z", collection: "SENTINEL-2-L2A" },
        })), []));
});

describe("require-eula-clause", () => {
    const r: Restriction[] = [{ kind: "require-eula-clause", clause: "copernicus-6c-liability" }];
    it("fails when the manifest declares no clauses", () =>
        deepStrictEqual(codes(r, ctx()), ["missing-eula-clause"]));
    it("passes once declared", () =>
        deepStrictEqual(codes(r, ctx({ declaredEulaClauses: new Set(["copernicus-6c-liability"]) })), []));
});

describe("unknown kinds fail closed", () => {
    it("reports rather than skips", () =>
        deepStrictEqual(codes([{ kind: "require-pinky-promise" }], ctx()), ["unknown-restriction-kind"]));
});

// --------------------------------------------------------------- drift guard

describe("registry", () => {
    it("declares only restriction kinds this gate can evaluate", async () => {
        const reg = (await import(`file://${REGISTRY}`, { with: { type: "json" } })).default;
        const unknown: string[] = [];
        for (const [key, entry] of Object.entries<any>(reg.sources)) {
            if (key.startsWith("_")) continue;
            for (const r of entry.restrictions ?? []) {
                if (!KNOWN_KINDS.includes(r.kind)) unknown.push(`${key}:${r.kind}`);
            }
        }
        deepStrictEqual(unknown, [], "a rule no code evaluates is a convention, not a control");
    });

    it("names only regions that policy.regions defines", async () => {
        const reg = (await import(`file://${REGISTRY}`, { with: { type: "json" } })).default;
        const defined = new Set(Object.keys(reg.policy.regions ?? {}));
        const dangling: string[] = [];
        for (const [key, entry] of Object.entries<any>(reg.sources)) {
            if (key.startsWith("_")) continue;
            for (const r of entry.restrictions ?? []) {
                if (r.region && !defined.has(r.region)) dangling.push(`${key}:${r.region}`);
            }
        }
        deepStrictEqual(dangling, []);
    });

    it("gives every notice-requiring source a notice to emit", async () => {
        const reg = (await import(`file://${REGISTRY}`, { with: { type: "json" } })).default;
        const missing: string[] = [];
        for (const [key, e] of Object.entries<any>(reg.sources)) {
            if (key.startsWith("_")) continue;
            const required = e.notice_required ?? (e.class === "attribution");
            if (required && !e.notice) missing.push(key);
        }
        deepStrictEqual(missing, [], "attribution cannot be generated for these");
    });
});

// --------------------------------------------------------------- end to end

describe("gate, end to end", () => {
    const at = "2026-08-01T00:00:00Z";

    it("rejects an excluded class", () => {
        const r = runGate(manifestWith({ layer: "elevation", source: "fabdem", fetched_at: at }, [0, 0, 1, 1]));
        ok(!r.passed);
        ok(r.codes.includes("excluded-class"), r.codes.join(","));
    });

    it("rejects ETOPO, which a licence-name reading called public domain", () => {
        const r = runGate(manifestWith({ layer: "bathymetry", source: "noaa-etopo-2022", fetched_at: at }, [0, 0, 1, 1]));
        ok(!r.passed);
        ok(r.codes.includes("excluded-class"), r.codes.join(","));
    });

    it("rejects a layer kind the source does not supply", () => {
        const r = runGate(manifestWith({ layer: "bathymetry", source: "esa-worldcover", fetched_at: at }, [0, 0, 1, 1]));
        ok(!r.passed);
        ok(r.codes.includes("layer-source-mismatch"), r.codes.join(","));
    });

    it("rejects an upstream header naming a producer the declared source is not", () => {
        const r = runGate(manifestWith({
            layer: "elevation", source: "usgs-3dep", fetched_at: at,
            upstream_licence_header: "(c) Airbus DS / Copernicus WorldDEM-30",
        }, [-100, 35, -99, 36]));
        ok(!r.passed);
        ok(r.codes.includes("upstream-header-mismatch"), r.codes.join(","));
    });

    it("rejects a tile carrying no provenance at all", () => {
        const m = manifestWith({ layer: "elevation", source: "usgs-3dep", fetched_at: at }, [-100, 35, -99, 36]);
        (m.areas[0]!.tiles[0] as any).layers = [];
        const r = runGate(m);
        ok(!r.passed);
        ok(r.codes.includes("tile-without-layers"), r.codes.join(","));
    });

    it("accepts a fully-discharged Copernicus layer", () => {
        const r = runGate(manifestWith(
            { layer: "elevation", source: "copernicus-dem", fetched_at: at }, [0, 0, 1, 1],
            { eula_clauses: ["copernicus-6c-liability", "copernicus-6e-flowdown"] },
        ));
        ok(r.passed, `expected pass, got: ${r.codes.join(",")}`);
    });

    it("accepts 3DEP inside US territory", () => {
        const r = runGate(manifestWith({ layer: "elevation", source: "usgs-3dep", fetched_at: at }, [-100, 35, -99, 36]));
        ok(r.passed, `expected pass, got: ${r.codes.join(",")}`);
    });

    it("rejects a symlink that leaves the scanned root", () => {
        // The lexical `../` escape and the symlink escape are different holes;
        // resolve() closes only the first. Measured before the fix: the gate
        // hashed the external file and reported "Licence gate passed".
        const root = mkdtempSync(join(tmpdir(), "licgate-root-"));
        const outside = mkdtempSync(join(tmpdir(), "licgate-out-"));
        mkdirSync(join(root, "data", "tiles"), { recursive: true });
        writeFileSync(join(outside, "secret.tif"), "x");
        symlinkSync(join(outside, "secret.tif"), join(root, "data", "tiles", "t.tif"));
        writeFileSync(join(root, "m.json"), JSON.stringify(
            manifestWith({ layer: "elevation", source: "usgs-3dep", fetched_at: at }, [-100, 35, -99, 36])));

        const p = spawnSync(process.execPath,
            ["--experimental-strip-types", GATE, "--root", root,
                "--registry", REGISTRY, "--manifest", join(root, "m.json")],
            { encoding: "utf8" });
        const out = `${p.stdout}\n${p.stderr}`;
        ok(!out.includes("Licence gate passed"), "a symlink out of the root must not pass");
        ok(out.includes("path-escapes-root"), out.slice(0, 400));
    });
});

describe("verified_on cannot be faked", () => {
    // This is the one field asserting a human read the licence. Before the fix
    // it used Date.parse directly, so an unparseable value gave NaN and a future
    // value gave a negative age — and both compared false against the staleness
    // limit, making garbage indistinguishable from fresh verification.
    function gateWithVerifiedOn(value: unknown): { passed: boolean; codes: string[] } {
        const reg = JSON.parse(readFileSync(REGISTRY, "utf8"));
        reg.sources["usgs-3dep"].verified_on = value;
        const dir = mkdtempSync(join(tmpdir(), "licgate-reg-"));
        const regPath = join(dir, "licences.json");
        writeFileSync(regPath, JSON.stringify(reg));
        mkdirSync(join(dir, "data", "tiles"), { recursive: true });
        writeFileSync(join(dir, "data", "tiles", "t.tif"), "x");
        writeFileSync(join(dir, "m.json"), JSON.stringify(
            manifestWith({ layer: "elevation", source: "usgs-3dep", fetched_at: "2026-08-01T00:00:00Z" },
                [-100, 35, -99, 36])));
        const p = spawnSync(process.execPath,
            ["--experimental-strip-types", GATE, "--root", dir,
                "--registry", regPath, "--manifest", join(dir, "m.json")],
            { encoding: "utf8" });
        const out = `${p.stdout}\n${p.stderr}`;
        return {
            passed: out.includes("Licence gate passed"),
            codes: [...out.matchAll(/^ERROR {2}([a-z-]+)/gm)].map((m) => m[1]!),
        };
    }

    for (const bad of ["not-a-date", "2099-01-01", "2026-02-30"]) {
        it(`rejects verified_on ${JSON.stringify(bad)}`, () => {
            const r = gateWithVerifiedOn(bad);
            ok(!r.passed, `${JSON.stringify(bad)} must not read as verified`);
            ok(r.codes.includes("invalid-verification-date"), r.codes.join(","));
        });
    }

    it("treats an empty verified_on as never-verified, not as an invalid date", () => {
        // "" is falsy, so it takes the earlier branch and reports the honest
        // warning rather than an error. That is the right answer: empty means
        // nobody has read the licence, which --strict already blocks on.
        const r = gateWithVerifiedOn("");
        deepStrictEqual(r.codes, [], "empty is a warning, not an error");
        ok(r.passed, "non-strict mode still passes; --strict is what blocks it");
    });

    it("accepts a real past date", () => {
        const r = gateWithVerifiedOn("2026-09-05");
        ok(r.passed, `expected pass, got: ${r.codes.join(",")}`);
    });
});

describe("symlinks in a scanned data root", () => {
    // Two different holes. The manifest-path check catches a tile that POINTS
    // out of the root. This catches a link the walk would otherwise follow —
    // `statSync` reports a symlinked directory as an ordinary one, so the scan
    // would descend and record files physically outside the root under paths
    // that look inside it.
    function gateOverTree(build: (root: string) => void): { passed: boolean; codes: string[]; out: string } {
        const root = mkdtempSync(join(tmpdir(), "licgate-sym-"));
        mkdirSync(join(root, "data", "tiles"), { recursive: true });
        build(root);
        writeFileSync(join(root, "m.json"), JSON.stringify(
            manifestWith({ layer: "elevation", source: "usgs-3dep", fetched_at: "2026-08-01T00:00:00Z" },
                [-100, 35, -99, 36])));
        const p = spawnSync(process.execPath,
            ["--experimental-strip-types", GATE, "--root", root,
                "--registry", REGISTRY, "--manifest", join(root, "m.json")],
            { encoding: "utf8" });
        const out = `${p.stdout}\n${p.stderr}`;
        return {
            passed: out.includes("Licence gate passed"),
            codes: [...out.matchAll(/^ERROR {2}([a-z-]+)/gm)].map((m) => m[1]!),
            out,
        };
    }

    it("rejects a symlinked file", () => {
        const outside = mkdtempSync(join(tmpdir(), "licgate-out-"));
        writeFileSync(join(outside, "real.tif"), "x");
        const r = gateOverTree((root) => {
            writeFileSync(join(root, "data", "tiles", "t.tif"), "x");
            symlinkSync(join(outside, "real.tif"), join(root, "data", "tiles", "linked.tif"));
        });
        ok(!r.passed);
        ok(r.codes.includes("symlink-in-data-root"), r.codes.join(","));
    });

    it("does not descend a symlinked directory", () => {
        const outside = mkdtempSync(join(tmpdir(), "licgate-outdir-"));
        mkdirSync(join(outside, "hidden"), { recursive: true });
        writeFileSync(join(outside, "hidden", "sneaky.tif"), "x");
        const r = gateOverTree((root) => {
            writeFileSync(join(root, "data", "tiles", "t.tif"), "x");
            symlinkSync(join(outside, "hidden"), join(root, "data", "linked-dir"));
        });
        ok(!r.passed);
        ok(r.codes.includes("symlink-in-data-root"), r.codes.join(","));
        // The file behind the link must never be enumerated as if it were ours.
        ok(!r.out.includes("sneaky.tif"), "walk followed the symlinked directory");
    });
});

describe("manifest path below a symlinked parent", () => {
    // The nastiest of the traversal cases, because every lexical check passes.
    // The tile is a REGULAR FILE — lstat on it reports no link — but a parent
    // component is a symlink out of the root, so the bytes live elsewhere.
    // Measured before the fix: with the parent outside any scan_root, so the
    // walk never saw the link either, the gate reported "Licence gate passed"
    // with a hash that MATCHED the external bytes.
    it("is rejected, and the external bytes are never hashed", () => {
        const root = mkdtempSync(join(tmpdir(), "licgate-parent-"));
        const outside = mkdtempSync(join(tmpdir(), "licgate-elsewhere-"));
        mkdirSync(join(root, "vendor"), { recursive: true });
        mkdirSync(join(outside, "tiles"), { recursive: true });
        writeFileSync(join(outside, "tiles", "t.tif"), "x");
        symlinkSync(join(outside, "tiles"), join(root, "vendor", "tiles"));

        const manifest = {
            schema: 1, generated_at: "2026-09-05T00:00:00Z", generator: "test/1",
            areas: [{
                id: "a", name: "A", bbox: [-100, 35, -99, 36],
                tiles: [{
                    path: "vendor/tiles/t.tif", sha256: TILE_SHA,
                    layers: [{ layer: "elevation", source: "usgs-3dep", fetched_at: "2026-08-01T00:00:00Z" }],
                }],
            }],
        };
        writeFileSync(join(root, "m.json"), JSON.stringify(manifest));
        const p = spawnSync(process.execPath,
            ["--experimental-strip-types", GATE, "--root", root,
                "--registry", REGISTRY, "--manifest", join(root, "m.json")],
            { encoding: "utf8" });
        const out = `${p.stdout}\n${p.stderr}`;
        ok(!out.includes("Licence gate passed"), out.slice(0, 400));
        ok(out.includes("path-escapes-root"), out.slice(0, 400));
        // The hash must be skipped entirely: a matching hash of external bytes
        // would read as corroboration of provenance it cannot support.
        ok(!out.includes("hash-mismatch"), "external bytes were hashed anyway");
    });
});

describe("licence_text_path pointing at a directory", () => {
    it("is a finding, not a crash", () => {
        // existsSync is true for a directory, and readFileSync then throws
        // EISDIR. That took down the whole gate with a stack trace instead of
        // reporting the misconfiguration.
        const reg = JSON.parse(readFileSync(REGISTRY, "utf8"));
        reg.sources["threejs-examples"].licence_text_path = "tools/licences/texts";
        const dir = mkdtempSync(join(tmpdir(), "licgate-dirpath-"));
        const regPath = join(dir, "licences.json");
        writeFileSync(regPath, JSON.stringify(reg));

        const repoRoot = join(HERE, "..", "..");
        const p = spawnSync(process.execPath,
            ["--experimental-strip-types", GATE, "--root", repoRoot,
                "--registry", regPath, "--manifest", join(repoRoot, "data", "manifest.json")],
            { encoding: "utf8" });
        const out = `${p.stdout}\n${p.stderr}`;
        ok(out.includes("missing-licence-text"), out.slice(-500));
        // EISDIR itself is expected — it is quoted INSIDE the finding, which is
        // the useful part of the message. What must not appear is a stack
        // trace, which is what an unhandled throw looks like.
        ok(!/^\s+at .+:\d+:\d+\)?$/m.test(out), `threw instead of reporting:\n${out.slice(-600)}`);
        ok(!out.includes("Node.js v"), "process crashed rather than exiting cleanly");
        strictEqual(p.status, 1, "should exit as a normal gate failure, not a crash");
    });
});

describe("strict mode", () => {
    it("passes on the committed tree, so the blocking gate can run strict", () => {
        // The gate job runs --strict. If a source loses its verified_on or its
        // licence_text_sha256, CI fails — which is the point, but it should fail
        // for that reason and not because strict was never satisfiable at all.
        const repoRoot = join(HERE, "..", "..");
        const p = spawnSync(process.execPath,
            ["--experimental-strip-types", GATE, "--root", repoRoot,
                "--registry", REGISTRY, "--manifest", join(repoRoot, "data", "manifest.json"), "--strict"],
            { encoding: "utf8" });
        const out = `${p.stdout}\n${p.stderr}`;
        ok(out.includes("Licence gate passed"), out.slice(-600));
    });
});

describe("full-licence-text sources", () => {
    it("every one declares a licence_text_path that exists and is non-empty", async () => {
        const reg = (await import(`file://${REGISTRY}`, { with: { type: "json" } })).default;
        const repoRoot = join(HERE, "..", "..");
        const broken: string[] = [];
        for (const [key, e] of Object.entries<any>(reg.sources)) {
            if (key.startsWith("_") || e.notice_kind !== "full-licence-text") continue;
            if (!e.licence_text_path) { broken.push(`${key}: no licence_text_path`); continue; }
            const p = join(repoRoot, e.licence_text_path);
            if (!existsSync(p)) { broken.push(`${key}: ${e.licence_text_path} missing`); continue; }
            if (readFileSync(p, "utf8").trim().length === 0) broken.push(`${key}: ${e.licence_text_path} empty`);
        }
        deepStrictEqual(broken, [],
            "a licence requiring its full text to ship is not discharged by a placeholder");
    });
});
