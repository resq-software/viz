/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

import { deepStrictEqual, ok, strictEqual } from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, it } from "node:test";

import { KNOWN_KINDS } from "../licences/restrictions.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const CHECKER = join(HERE, "check-areas.ts");
const REPO = resolve(HERE, "..", "..");
const REAL_AREAS = join(REPO, "data", "areas.json");
const REAL_REGISTRY = join(REPO, "tools", "licences", "licences.json");

/** Runs the checker over an areas document, against the real licence register. */
function run(
    mutate?: (doc: any) => void,
    mutateRegistry?: (reg: any) => void,
): { status: number; out: string } {
    const doc = JSON.parse(readFileSync(REAL_AREAS, "utf8"));
    mutate?.(doc);
    const dir = mkdtempSync(join(tmpdir(), "areas-"));
    const path = join(dir, "areas.json");
    writeFileSync(path, JSON.stringify(doc));

    // Tests that care about BLOCKED behaviour construct the block themselves rather than
    // relying on whichever sources happen to be uncleared today. The first version of these
    // asserted on the real registry's current state and broke the moment licence work landed,
    // which made progress look like regression.
    let registryPath = REAL_REGISTRY;
    if (mutateRegistry) {
        const reg = JSON.parse(readFileSync(REAL_REGISTRY, "utf8"));
        mutateRegistry(reg);
        registryPath = join(dir, "licences.json");
        writeFileSync(registryPath, JSON.stringify(reg));
    }

    const p = spawnSync(process.execPath,
        ["--experimental-strip-types", CHECKER, "--areas", path, "--registry", registryPath],
        { encoding: "utf8" });
    return { status: p.status ?? -1, out: `${p.stdout}\n${p.stderr}` };
}

describe("the shipped area definitions", () => {
    it("are self-consistent", () => {
        const r = run();
        strictEqual(r.status, 0, r.out);
    });

    it("all declare an elevation source", () => {
        // Terrain is the entire point of a bake; an area without one is a typo, not a choice.
        //
        // NOTE: this asserts on the shipped DATA and never runs the checker. That is deliberate
        // and it is not coverage of the `no-elevation` guard — the guard is driven separately in
        // "guards that only fire on data nobody ships" below. Read together they say two
        // different things: the data is right today, and the checker would catch it if it were
        // not. This test used to be the only thing with "elevation source" in its name, which
        // made the guard look covered when nothing drove it.
        const doc = JSON.parse(readFileSync(REAL_AREAS, "utf8"));
        for (const a of doc.areas) {
            ok(a.sources?.elevation, `${a.id} declares no elevation source`);
        }
    });

    it("report which areas are bakeable rather than leaving it to be worked out by hand", () => {
        // The report is the point of the tool. Working this out by reading two files got it
        // wrong: areas assumed ready were blocked on unhashed licence texts, and the count of
        // areas one source held up was off by one.
        const r = run();
        ok(/\d+ bakeable today/.test(r.out), r.out);
        ok(r.out.includes("Blocking sources, by how many areas each holds up"), r.out);
    });
});

describe("geometry that would otherwise be wrong quietly", () => {
    it("rejects a bbox that is not the world's size", () => {
        // The failure this exists for: the scene is a fixed 4 km square, so a bbox of another
        // size rescales the terrain under every asset in it. Slope comes out wrong by the ratio
        // and nothing reports it — the mobility model just answers differently.
        const r = run((doc) => {
            const a = doc.areas[0];
            a.bbox = [a.bbox[0], a.bbox[1], a.bbox[0] + (a.bbox[2] - a.bbox[0]) * 2, a.bbox[3]];
        });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("bbox-not-world-sized"), r.out);
    });

    it("rejects an origin that is not the bbox centre", () => {
        const r = run((doc) => { doc.areas[0].origin.latitudeDeg += 0.01; });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("origin-off-centre"), r.out);
    });

    it("rejects an originId that does not track the area id", () => {
        // Local positions are only comparable across scenes sharing an origin id, so a stale id
        // makes two different places look like the same frame.
        const r = run((doc) => { doc.areas[0].origin.originId = "scene-alpine-default"; });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("origin-id-mismatch"), r.out);
    });

    it("rejects a UTM zone the centre does not fall in", () => {
        const r = run((doc) => { doc.areas[0].targetCrs = "EPSG:32601"; });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("wrong-utm-zone"), r.out);
    });

    it("rejects an inverted bbox", () => {
        const r = run((doc) => {
            const a = doc.areas[0];
            a.bbox = [a.bbox[2], a.bbox[3], a.bbox[0], a.bbox[1]];
        });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("degenerate-bbox"), r.out);
    });

    it("rejects a duplicate id", () => {
        const r = run((doc) => { doc.areas.push({ ...doc.areas[0] }); });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("duplicate-id"), r.out);
    });

    it("rejects an id that is not kebab-case, since it becomes a path segment", () => {
        const r = run((doc) => {
            doc.areas[0].id = "Not Kebab";
            doc.areas[0].origin.originId = "area-Not Kebab";
        });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("bad-id"), r.out);
    });
});

describe("inputs this checker must refuse rather than misread", () => {
    it("rejects a schema it does not understand", () => {
        // The AreaDoc interface is an assertion, not a check. A schema-2 document with similar
        // fields would validate under schema-1 rules and be reported clean — a pass from
        // something that never actually checked it.
        const r = run((doc) => { doc.schema = 2; });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("only"), r.out);
        ok(r.out.includes("schema"), r.out);
    });
});

describe("the licence cross-check", () => {
    it("reports an area blocked when a source is unverified or unhashed", () => {
        // Constructs the block rather than assuming one exists: which sources are outstanding
        // changes as licence work lands, and a test pinned to that reads a clearance as a break.
        const r = run(undefined, (reg) => {
            reg.sources["esa-worldcover"].verified_on = null;
            reg.sources["esa-worldcover"].licence_text_sha256 = null;
        });
        ok(r.out.includes("esa-worldcover"), r.out);
        ok(/never verified/.test(r.out), r.out);
        ok(/not vendored or hashed/.test(r.out), r.out);
    });

    it("blocks on a drafted contract term nobody has ratified", () => {
        // The gate refuses an unratified contract term. If this checker asked only whether the
        // clause had TEXT, drafting one would make an area read bakeable here while its first
        // tile was still refused — the two tools disagreeing about the same registry, which is
        // exactly what this cross-check exists to prevent. It happened once with
        // permitted_layers; this is the same shape.
        const r = run(undefined, (reg) => {
            reg.clauses["copernicus-6e-flowdown"].needs_drafting = false;
            reg.clauses["copernicus-6e-flowdown"].text = "Licensee shall bind subsequent users.";
            reg.clauses["copernicus-6e-flowdown"].kind = "contract-term";
            reg.clauses["copernicus-6e-flowdown"].ratified = null;
            // Clear the unrelated blocker so this one is what the assertion sees.
            reg.clauses["jaxa-commercial-use-notification"].needs_drafting = false;
            reg.clauses["jaxa-commercial-use-notification"].text = "Notified.";
        });
        ok(r.out.includes("contract term nobody has ratified"), r.out);
        ok(/rhine-meuse-delta/.test(r.out), r.out);
    });

    it("stops blocking once that term is ratified", () => {
        const r = run(undefined, (reg) => {
            reg.clauses["copernicus-6e-flowdown"].needs_drafting = false;
            reg.clauses["copernicus-6e-flowdown"].text = "Licensee shall bind subsequent users.";
            reg.clauses["copernicus-6e-flowdown"].kind = "contract-term";
            reg.clauses["copernicus-6e-flowdown"].ratified = { by: "Example Counsel", on: "2026-09-09" };
            reg.clauses["jaxa-commercial-use-notification"].needs_drafting = false;
            reg.clauses["jaxa-commercial-use-notification"].text = "Notified.";
        });
        ok(!r.out.includes("contract term nobody has ratified"), r.out);
    });

    it("blocks on an action clause whose evidence file is gone", () => {
        // Third clause kind, same contract. An action is discharged by evidence of something
        // done outside this repository, so having text says nothing about whether it happened.
        // Found by deleting the JAXA reply: the gate refused the tile while this checker still
        // called sendai-plain bakeable. Adding a kind silently reopened the disagreement the
        // test above exists to close, so this pins the third one too.
        const r = run(undefined, (reg) => {
            reg.clauses["jaxa-commercial-use-notification"].discharged.record =
                "tools/licences/outreach/no-such-file.md";
        });
        ok(r.out.includes("which does not exist"), r.out);
        ok(/sendai-plain/.test(r.out), r.out);
    });

    it("blocks on an action clause nobody recorded doing", () => {
        const r = run(undefined, (reg) => {
            reg.clauses["jaxa-commercial-use-notification"].discharged = null;
        });
        ok(r.out.includes("action nobody has recorded doing"), r.out);
        ok(/sendai-plain/.test(r.out), r.out);
    });

    it("does not call an area bakeable when its source is not in the register", () => {
        const r = run((doc) => { doc.areas[0].sources.elevation = "no-such-source"; });
        ok(r.out.includes("not in the licence register"), r.out);
        // The definition is still valid — the area is blocked, not malformed.
        strictEqual(r.status, 0, r.out);
    });

    it("blocks a source used for a layer its licence does not cover", () => {
        // The licence gate enforces permitted_layers per manifest layer. Without the same check
        // here an area reads bakeable and then has its first tile rejected — the disagreement
        // this cross-check exists to prevent. usgs-3dep is elevation-only.
        const r = run((doc) => { doc.areas[0].sources.landcover = "usgs-3dep"; });
        ok(r.out.includes('does not supply the "landcover" layer'), r.out);
        strictEqual(r.status, 0, r.out);
    });

    it("counts areas, not occurrences, when ranking blockers", () => {
        // noaa-cudem serves BOTH elevation and bathymetry at tangier-sound, so counting reasons
        // instead of areas overstated it by one. Blocked here on purpose so the arithmetic is
        // exercised whatever the registry's real state is.
        const r = run(undefined, (reg) => {
            reg.sources["noaa-cudem"].licence_text_sha256 = null;
        });
        const line = r.out.split("\n").find((l) => /^\s+\d+\s+noaa-cudem\s*$/.test(l));
        ok(line, `no ranking line for noaa-cudem:\n${r.out}`);

        // Independently: how many DISTINCT areas name it, in any layer.
        const areas = JSON.parse(readFileSync(REAL_AREAS, "utf8")).areas;
        const distinct = areas.filter((a: any) =>
            Object.values(a.sources).includes("noaa-cudem")).length;
        strictEqual(line!.trim().split(/\s+/)[0], String(distinct),
            `ranking must count distinct areas (${distinct}), not source occurrences: ${line}`);
    });
});

describe("the two tools agree about the whole registry, not just clauses", () => {
    // This cross-check has now failed four times, each on a different axis: permitted_layers,
    // then clause kinds, then sign-off DATES, then geometry. Each fix closed one axis and left
    // the next one open, because the checker re-implemented a subset of the gate's rules by hand
    // instead of asking the gate's own evaluator.
    //
    // Found by a mutation sweep: moving 3DEP's licensed region into the Pacific left all seven
    // areas that draw on it reading "bakeable", and a discharge dated 2099 left sendai-plain
    // "bakeable" while the gate refused its first tile with "the date 2099-01-01 is in the future".

    it("blocks an area whose source sign-off is dated in the future", () => {
        const r = run(undefined, (reg) => {
            reg.clauses["jaxa-commercial-use-notification"].discharged.on = "2099-01-01";
        });
        ok(r.out.includes("is in the future"), r.out);
        ok(/sendai-plain/.test(r.out), r.out);
    });

    it("blocks an area whose sign-off date is not a real date", () => {
        // The gate's parseIso rejects impossible calendar dates rather than rolling them over.
        const r = run(undefined, (reg) => {
            reg.clauses["jaxa-commercial-use-notification"].discharged.on = "2026-02-30";
        });
        ok(r.out.includes("is not a real date"), r.out);
    });

    it("blocks a contract term ratified with a future date", () => {
        const r = run(undefined, (reg) => {
            reg.clauses["copernicus-6e-flowdown"].ratified =
                { by: "Example Counsel", on: "2099-01-01" };
        });
        ok(r.out.includes("is in the future"), r.out);
    });

    it("blocks an area that sits outside its source's licensed region", () => {
        // `clip` was never asked here at all — the loop skipped every kind but require-eula-clause.
        // The region is NAMED and resolved through policy.regions, so the honest mutation is to
        // move the named region rather than to overwrite the name with a raw box: that would
        // block for "region not defined", which is a different rule firing.
        const r = run(undefined, (reg) => {
            reg.policy.regions["us-territory"] = [[-160.0, 20.0, -159.0, 21.0]];
        });
        ok(r.out.includes('not contained in region "us-territory"'), r.out);
        ok(!/12 declared, 10 bakeable/.test(r.out),
            "moving 3DEP's licensed region must change the bakeable count");
    });

    it("reports a restriction kind it does not classify, rather than skipping it", () => {
        // The silent `continue` is how geometry went unasked for as long as it did. A new rule in
        // restrictions.ts must force a decision here, not slip through as satisfied.
        const r = run(undefined, (reg) => {
            reg.sources["usgs-3dep"].restrictions.push({ kind: "require-carbon-offset" });
        });
        ok(r.out.includes("is not classified by this checker"), r.out);
    });

    it("classifies every restriction kind the gate can evaluate", () => {
        // The structural guard. KNOWN_KINDS is the gate's own list; if restrictions.ts gains a
        // rule and nobody classifies it here, this fails instead of the checker quietly not
        // asking it. That is the failure mode all four rounds shared.
        const src = readFileSync(CHECKER, "utf8");
        const listed = (name: string): string[] => {
            const decl = `const ${name}: readonly string[] = [`;
            const at = src.indexOf(decl);
            ok(at !== -1, `${name} not found in check-areas.ts`);
            // Start after the declaration, not at it: the declaration itself contains a "]"
            // inside `readonly string[]`, and searching from `at` finds that one instead of the
            // array's closing bracket — yielding an empty list and a test that fails for the
            // wrong reason.
            const from = at + decl.length;
            const body = src.slice(from, src.indexOf("]", from));
            return (body.match(/"[^"]+"/g) ?? []).map((q) => q.slice(1, -1));
        };
        const classified = new Set([...listed("AREA_EVALUABLE_KINDS"), ...listed("TILE_ONLY_KINDS")]);
        deepStrictEqual(KNOWN_KINDS.filter((k) => !classified.has(k)), [],
            "restriction kinds the gate evaluates but check-areas.ts does not classify");
    });
});

describe("registry fields are JSON, not the types they are annotated as", () => {
    // check-areas.ts and check-licences.ts both read the register through JSON.parse, so a field
    // declared `string` can be a number at runtime. Review caught that `by: 42` reached .trim()
    // and threw `TypeError: signoff?.by?.trim is not a function` — a stack trace instead of the
    // "nobody has recorded doing" this is supposed to say. Guarded in restrictions.ts so the one
    // fix covers both callers, which is this file's whole argument.
    it("reports an unusable sign-off instead of crashing on it", () => {
        const r = run(undefined, (reg) => {
            const d = reg.clauses["jaxa-commercial-use-notification"].discharged;
            d.by = 42;
            d.on = 20260910;
        });
        ok(!/TypeError/.test(r.out), r.out);
        ok(r.out.includes("nobody has recorded doing"), r.out);
        ok(/sendai-plain/.test(r.out), r.out);
    });

    it("reports a non-string DATE instead of crashing on it", () => {
        // `by` is a valid string here on purpose. With both fields bad, the `by` check short-
        // circuits and parseIso is never reached — so removing parseIso's type guard left all
        // 27 tests green. An unguarded guard, found by mutating it. This reaches it.
        const r = run(undefined, (reg) => {
            reg.clauses["jaxa-commercial-use-notification"].discharged.on = 20260910;
        });
        ok(!/TypeError/.test(r.out), r.out);
        ok(r.out.includes("is not a real date"), r.out);
        ok(/sendai-plain/.test(r.out), r.out);
    });

    it("reports an unusable ratification instead of crashing on it", () => {
        const r = run(undefined, (reg) => {
            reg.clauses["copernicus-6e-flowdown"].ratified = { by: { name: "counsel" }, on: [2026] };
        });
        ok(!/TypeError/.test(r.out), r.out);
        ok(r.out.includes("nobody has ratified"), r.out);
    });
});

describe("guards that only fire on data nobody ships", () => {
    // `no-sources` and `no-elevation` were the only two of the checker's nine fail codes with no
    // test driving them. The test that sounded like coverage — "all declare an elevation source"
    // — reads data/areas.json in the test process and never invokes the checker, so it verifies
    // the data rather than the guard. And because every shipped area does declare an elevation
    // source, neither guard fires in any other run either: present, correct-looking, and never
    // once executed.
    it("refuses an area that declares no sources at all", () => {
        const r = run((doc) => { doc.areas[0].sources = {}; });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("no-sources"), r.out);
    });

    it("refuses an area that declares sources but no elevation", () => {
        const r = run((doc) => {
            doc.areas[0].sources = { landcover: "esa-worldcover" };
        });
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("no-elevation"), r.out);
    });

    it("names the offending area, not just the code", () => {
        // A fail code with no id in it sends a reader to grep 12 areas by hand.
        const r = run((doc) => { doc.areas[2].sources = { landcover: "esa-worldcover" }; });
        strictEqual(r.status, 1, r.out);
        const doc = JSON.parse(readFileSync(REAL_AREAS, "utf8"));
        ok(r.out.includes(doc.areas[2].id), r.out);
    });
});
