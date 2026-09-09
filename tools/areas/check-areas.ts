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

// Validates data/areas.json — the areas the bake targets and the geodetic anchor each one
// places its scene frame on.
//
// Two jobs, and the second is the one worth having. It checks the geometry is self-consistent,
// and it cross-checks every declared source against the licence register so that "which areas
// can we actually bake" is answered by running something. That question was being answered by
// hand and the hand-held answer was wrong: the areas assumed ready were blocked on unhashed
// licence texts, and the two that were ready were not the ones anyone had picked.
//
// Usage:
//   node --experimental-strip-types tools/areas/check-areas.ts [--areas P] [--registry P] [--strict]
//
// --strict additionally fails when NO area is bakeable — the state that should stop a release
// rather than merely inform one.

import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/** Metres per degree of latitude. Spherical, matching how the bboxes were generated. */
const M_PER_DEG_LAT = 110_574;

/** Metres per degree of longitude at the equator. */
const M_PER_DEG_LON_EQ = 111_320;

/** Tolerance on a bbox side, in metres. Generous: bboxes are stored rounded to 6 decimals. */
const SIDE_TOLERANCE_M = 2;

interface Origin {
  originId: string;
  latitudeDeg: number;
  longitudeDeg: number;
  verticalReference: string;
}

interface Area {
  id: string;
  name: string;
  tier: string;
  bbox: [number, number, number, number];
  origin: Origin;
  targetCrs: string;
  sources: Record<string, string>;
  notes?: string;
}

interface AreaDoc {
  schema: number;
  worldSizeMeters: number;
  areas: Area[];
}

const arg = (name: string, fallback: string) => {
  const i = process.argv.indexOf(`--${name}`);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1]! : fallback;
};
const STRICT = process.argv.includes("--strict");

const AREAS_PATH = resolve(arg("areas", "data/areas.json"));
const REGISTRY_PATH = resolve(arg("registry", "tools/licences/licences.json"));

const problems: { area: string; code: string; message: string }[] = [];
const fail = (area: string, code: string, message: string) =>
  problems.push({ area, code, message });

const doc: AreaDoc = JSON.parse(readFileSync(AREAS_PATH, "utf8"));
const registry = JSON.parse(readFileSync(REGISTRY_PATH, "utf8"));
const allowed = new Set<string>(registry.policy.allowed_classes);
const clauses: Record<string, { text?: string | null }> = registry.clauses ?? {};

/**
 * Why a source cannot be baked today, or null when it can.
 *
 * Deliberately asks the same questions the licence gate asks of a manifest layer, so an area
 * reported bakeable here does not then fail the gate on its first tile.
 *
 * @param key Registry key.
 * @returns Semicolon-joined reasons, or null when nothing blocks it.
 */
function blockedBecause(key: string): string | null {
  const entry = registry.sources[key];
  if (!entry || typeof entry === "string") return "not in the licence register";
  if (!allowed.has(entry.class)) return `class "${entry.class}" is excluded`;

  const reasons: string[] = [];
  if (!entry.verified_on) reasons.push("never verified against its publisher page");
  if (!entry.licence_text_sha256) reasons.push("licence text not vendored or hashed");

  for (const r of entry.restrictions ?? []) {
    if (r.kind !== "require-eula-clause" || !r.clause) continue;
    if (!clauses[r.clause]?.text?.trim()) {
      reasons.push(`EULA clause "${r.clause}" has no drafted text`);
    }
  }
  return reasons.length ? reasons.join("; ") : null;
}

const seen = new Set<string>();

for (const area of doc.areas) {
  const where = area.id;

  if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(area.id)) {
    fail(where, "bad-id", `"${area.id}" is not kebab-case, and the id becomes a path segment.`);
  }
  if (seen.has(area.id)) {
    fail(where, "duplicate-id", `"${area.id}" appears more than once.`);
  }
  seen.add(area.id);

  const [minLon, minLat, maxLon, maxLat] = area.bbox;
  if (minLon >= maxLon || minLat >= maxLat) {
    fail(where, "degenerate-bbox", `bbox is inverted or empty: ${JSON.stringify(area.bbox)}.`);
    continue;
  }

  // The scene is a fixed square. A bbox of a different size does not fail loudly — it rescales
  // the terrain under every asset, so a rover's slope is wrong by the ratio and nothing says so.
  const midLat = (minLat + maxLat) / 2;
  const widthM = (maxLon - minLon) * M_PER_DEG_LON_EQ * Math.cos((midLat * Math.PI) / 180);
  const heightM = (maxLat - minLat) * M_PER_DEG_LAT;
  for (const [side, m] of [["width", widthM], ["height", heightM]] as const) {
    if (Math.abs(m - doc.worldSizeMeters) > SIDE_TOLERANCE_M) {
      fail(where, "bbox-not-world-sized",
        `bbox ${side} is ${m.toFixed(1)} m but the world is ${doc.worldSizeMeters} m. A bbox that `
        + `is not the world's size silently rescales the terrain under every asset in it.`);
    }
  }

  // Local (0,0,0) is the middle of the scene, so the origin has to be the middle of the box.
  const midLon = (minLon + maxLon) / 2;
  const offLat = Math.abs(area.origin.latitudeDeg - midLat) * M_PER_DEG_LAT;
  const offLon = Math.abs(area.origin.longitudeDeg - midLon)
    * M_PER_DEG_LON_EQ * Math.cos((midLat * Math.PI) / 180);
  if (offLat > SIDE_TOLERANCE_M || offLon > SIDE_TOLERANCE_M) {
    fail(where, "origin-off-centre",
      `origin sits ${Math.max(offLat, offLon).toFixed(1)} m from the bbox centre. Local (0,0,0) is `
      + `the middle of the scene, so every geodetic command target would be offset by that much.`);
  }

  if (area.origin.originId !== `area-${area.id}`) {
    // Local positions are only comparable across scenes sharing an OriginId, so the id has to
    // move whenever the coordinates do. Deriving it from the area id is what guarantees that.
    fail(where, "origin-id-mismatch",
      `originId is "${area.origin.originId}" but should be "area-${area.id}" — local positions `
      + `are only comparable across scenes sharing an id, so it must change when coordinates do.`);
  }

  // The UTM zone has to be the one the centre actually falls in, or the reprojection stage
  // lands the area in a neighbouring zone's grid.
  const zone = Math.floor((midLon + 180) / 6) + 1;
  const expected = `EPSG:${(midLat >= 0 ? 32600 : 32700) + zone}`;
  if (area.targetCrs !== expected) {
    fail(where, "wrong-utm-zone",
      `targetCrs is ${area.targetCrs} but the centre falls in ${expected}.`);
  }

  if (!Object.keys(area.sources).length) {
    fail(where, "no-sources", "declares no sources, so there is nothing to bake.");
  }
  if (!area.sources.elevation) {
    fail(where, "no-elevation", "declares no elevation source; terrain is the point of a bake.");
  }
}

// ─── Report ─────────────────────────────────────────────────────────────────

const ready: string[] = [];
const blocked: { id: string; reasons: string[] }[] = [];

for (const area of doc.areas) {
  const reasons: string[] = [];
  for (const [layer, key] of Object.entries(area.sources)) {
    const why = blockedBecause(key);
    if (why) reasons.push(`${layer} (${key}): ${why}`);
  }
  if (reasons.length) blocked.push({ id: area.id, reasons });
  else ready.push(area.id);
}

console.log(`Areas: ${doc.areas.length} declared, ${ready.length} bakeable today.\n`);
if (ready.length) {
  console.log("BAKEABLE — every declared source is admissible, verified and hashed:");
  for (const id of ready) console.log(`  ${id}`);
  console.log("");
}
if (blocked.length) {
  console.log("BLOCKED — licence work outstanding, not an area problem:");
  for (const b of blocked) {
    console.log(`  ${b.id}`);
    for (const r of b.reasons) console.log(`      ${r}`);
  }
  console.log("");
}

// Which source unblocks the most, so the next licence task picks itself rather than being
// chosen by whoever last looked at the list.
const cost = new Map<string, number>();
for (const b of blocked) {
  for (const r of b.reasons) {
    const key = r.slice(r.indexOf("(") + 1, r.indexOf(")"));
    cost.set(key, (cost.get(key) ?? 0) + 1);
  }
}
if (cost.size) {
  console.log("Blocking sources, by how many areas each holds up:");
  for (const [key, n] of [...cost].sort((a, b) => b[1] - a[1])) {
    console.log(`  ${String(n).padStart(2)}  ${key}`);
  }
  console.log("");
}

if (problems.length) {
  console.error("Area definition problems:");
  for (const p of problems) console.error(`  ERROR  ${p.code}  ${p.area}: ${p.message}`);
  console.error(`\n${problems.length} problem(s). Area check FAILED.`);
  process.exit(1);
}

if (STRICT && !ready.length) {
  console.error("No area is bakeable and --strict was given.");
  process.exit(1);
}

console.log("Area check passed.");
