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

import { ok, strictEqual } from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, it } from "node:test";

const HERE = dirname(fileURLToPath(import.meta.url));
const CHECKER = join(HERE, "check-areas.ts");
const REPO = resolve(HERE, "..", "..");
const REAL_AREAS = join(REPO, "data", "areas.json");
const REAL_REGISTRY = join(REPO, "tools", "licences", "licences.json");

/** Runs the checker over an areas document, against the real licence register. */
function run(mutate?: (doc: any) => void): { status: number; out: string } {
    const doc = JSON.parse(readFileSync(REAL_AREAS, "utf8"));
    mutate?.(doc);
    const dir = mkdtempSync(join(tmpdir(), "areas-"));
    const path = join(dir, "areas.json");
    writeFileSync(path, JSON.stringify(doc));
    const p = spawnSync(process.execPath,
        ["--experimental-strip-types", CHECKER, "--areas", path, "--registry", REAL_REGISTRY],
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

describe("the licence cross-check", () => {
    it("reports an area blocked when a source is unverified or unhashed", () => {
        const r = run();
        // usgs-3dep is both today, and it is the single biggest blocker.
        ok(r.out.includes("usgs-3dep"), r.out);
        ok(/never verified|not vendored or hashed/.test(r.out), r.out);
    });

    it("does not call an area bakeable when its source is not in the register", () => {
        const r = run((doc) => { doc.areas[0].sources.elevation = "no-such-source"; });
        ok(r.out.includes("not in the licence register"), r.out);
        // The definition is still valid — the area is blocked, not malformed.
        strictEqual(r.status, 0, r.out);
    });
});
