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

import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

import { KNOWN_KINDS, evaluateRestrictions, parseIso } from "../licences/restrictions.ts";

/**
 * Restriction kinds an AREA can be judged on, before any tile exists.
 *
 * These turn only on where the area is, which data/areas.json already states. The gate applies
 * the very same rules per baked tile through the very same evaluator, so agreeing here is not a
 * coincidence — it is the same code.
 */
const AREA_EVALUABLE_KINDS: readonly string[] = ["clip", "exclude-region"];

/**
 * Restriction kinds that cannot be settled until a tile is baked.
 *
 * Each needs a fact only the manifest carries — when the bytes were fetched, which mask was
 * applied, which licence limb was elected, which upstream collection served it. Reporting these
 * as satisfied would be a lie, and skipping them silently was the bug: an area came back
 * "bakeable" while the gate stood ready to refuse its first tile.
 */
const TILE_ONLY_KINDS: readonly string[] = [
  "require-eula-clause", "fetched-after", "fetched-before",
  "require-mask", "require-election", "require-collection-allowlist",
];

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

interface RegistryEntry {
  class: string;
  verified_on?: string | null;
  licence_text_sha256?: string | null;
  permitted_layers?: string[];
  restrictions?: { kind: string; clause?: string }[];
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

// The interface above is an assertion, not a check — a schema-2 document with structurally
// similar fields would be validated under schema-1 rules and reported clean. Refuse what this
// checker does not know how to read, the same way the licence gate refuses an unknown
// restriction kind rather than skipping it.
const SUPPORTED_SCHEMA = 1;
if (doc.schema !== SUPPORTED_SCHEMA) {
  console.error(
    `${AREAS_PATH} declares schema ${JSON.stringify(doc.schema)}, but this checker only `
    + `understands ${SUPPORTED_SCHEMA}. Validating it under the wrong rules would report a clean `
    + `result for a document nothing has actually checked.`);
  process.exit(1);
}
const registry = JSON.parse(readFileSync(REGISTRY_PATH, "utf8"));
const allowed = new Set<string>(registry.policy.allowed_classes);
const clauses: Record<string, {
  text?: string | null;
  kind?: string;
  ratified?: { by?: string; on?: string } | null;
  discharged?: { by?: string; on?: string; record?: string } | null;
}> = registry.clauses ?? {};

/**
 * Why a dated sign-off does not count, or null when it does.
 *
 * The gate validates these dates with parseIso and refuses a future one. This checker used to
 * read only `.by`, so a discharge dated 2099 left an area "bakeable" while the gate refused its
 * first tile — the same disagreement, on a third axis, after two rounds of closing it. Uses the
 * gate's own parseIso so the two cannot drift on what a date even is.
 *
 * @param signoff The clause's `ratified` or `discharged` record.
 * @param noun What the date is called in the message ("review" or "record").
 * @returns A phrase completing "is a contract term ..." / "is an action ...", or null.
 */
function dateProblem(
  signoff: { by?: string; on?: string } | null | undefined,
  noun: string,
): string | null {
  if (!signoff?.by?.trim()) return `nobody has ${noun === "review" ? "ratified" : "recorded doing"}`;
  const at = parseIso(signoff.on);
  if (at === null) return `whose ${noun} date ${JSON.stringify(signoff.on ?? null)} is not a real date`;
  if (at > Date.now()) return `whose ${noun} date ${signoff.on} is in the future`;
  return null;
}

/**
 * Why a source cannot be baked today, or null when it can.
 *
 * Deliberately asks the same questions the licence gate asks of a manifest layer, so an area
 * reported bakeable here does not then fail the gate on its first tile.
 *
 * @param key Registry key.
 * @param layer Layer the area draws from this source.
 * @returns Semicolon-joined reasons, or null when nothing blocks it.
 */
function blockedBecause(key: string, layer: string, area: Area): string | null {
  const entry = registry.sources[key];
  if (!entry || typeof entry === "string") return "not in the licence register";
  if (!allowed.has(entry.class)) return `class "${entry.class}" is excluded`;

  const reasons: string[] = [];

  // The gate enforces this per manifest layer, so without it here an area could be reported
  // bakeable and then have its first tile rejected — which is exactly the disagreement this
  // function's contract promises not to have.
  if (entry.permitted_layers && !entry.permitted_layers.includes(layer)) {
    reasons.push(
      `does not supply the "${layer}" layer (permits ${entry.permitted_layers.join(", ")})`);
  }

  if (!entry.verified_on) reasons.push("never verified against its publisher page");
  if (!entry.licence_text_sha256) reasons.push("licence text not vendored or hashed");

  // Geometry the area already determines, judged by the SAME evaluator the gate uses per tile.
  // Hand-rolling a subset here is what let an area read "bakeable" while sitting outside its
  // source's licensed region: this loop used to `continue` past every kind but require-eula-clause,
  // so `clip` and `exclude-region` were never asked. Verified by moving 3DEP's licensed region to
  // the Pacific — all seven areas that draw on it stayed "bakeable".
  const geometry = (entry.restrictions ?? []).filter(
    (r: { kind: string }) => AREA_EVALUABLE_KINDS.includes(r.kind));
  if (geometry.length) {
    for (const v of evaluateRestrictions(geometry as never, {
      area: { id: area.id, bbox: area.bbox },
      // Geometry rules read none of these; the tile-only kinds that would are filtered out above.
      layer: { layer, source: key, fetched_at: "" },
      policy: registry.policy,
      declaredEulaClauses: new Set<string>(),
    })) {
      reasons.push(v.message);
    }
  }

  // A kind in neither list is a rule nobody classified. Loud rather than skipped: a silent
  // fall-through here is precisely how the geometry rules went unasked for as long as they did.
  for (const r of entry.restrictions ?? []) {
    if (!AREA_EVALUABLE_KINDS.includes(r.kind) && !TILE_ONLY_KINDS.includes(r.kind)) {
      reasons.push(
        `restriction kind "${r.kind}" is not classified by this checker, so it is not being `
        + `asked. Add it to AREA_EVALUABLE_KINDS or TILE_ONLY_KINDS in check-areas.ts.`);
    }
  }

  for (const r of entry.restrictions ?? []) {
    if (r.kind !== "require-eula-clause" || !r.clause) continue;
    const clause = clauses[r.clause];
    if (!clause?.text?.trim()) {
      reasons.push(`EULA clause "${r.clause}" has no drafted text`);
    } else if (clause.kind === "contract-term" && dateProblem(clause.ratified, "review")) {
      // Asks the same question the gate asks. Checking only for text would report an area
      // bakeable the moment someone DRAFTED a contract term, while the gate still refused the
      // tile for want of a ratification — the two tools disagreeing about the same registry,
      // which is the failure this cross-check exists to prevent.
      reasons.push(
        `EULA clause "${r.clause}" is a contract term ${dateProblem(clause.ratified, "review")}`);
    } else if (clause.kind === "action") {
      // Same contract, third kind. An action clause is discharged by evidence of something done
      // outside this repository, so text says nothing about whether it happened. Found by
      // deleting the JAXA reply: the gate refused the tile while this tool still called the area
      // bakeable — the disagreement the comment above promises not to have, reappearing the
      // moment a new kind was added. The record is checked for existence here too, because a
      // path is only evidence while the file is there.
      const record = clause.discharged?.record?.trim();
      const when = dateProblem(clause.discharged, "record");
      if (when) {
        reasons.push(`EULA clause "${r.clause}" is an action ${when}`);
      } else if (!record) {
        reasons.push(`EULA clause "${r.clause}" is an action with no evidence path recorded`);
      } else if (!existsSync(resolve(record))) {
        reasons.push(
          `EULA clause "${r.clause}" names evidence at "${record}", which does not exist`);
      }
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
    const why = blockedBecause(key, layer, area);
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
// Count AREAS, not occurrences. One source can serve two layers in the same area — noaa-cudem
// supplies both elevation and bathymetry at tangier-sound — and counting the reasons instead of
// the areas overstated it by one, which is the sort of number that then gets repeated.
const cost = new Map<string, Set<string>>();
for (const b of blocked) {
  for (const r of b.reasons) {
    const key = r.slice(r.indexOf("(") + 1, r.indexOf(")"));
    (cost.get(key) ?? cost.set(key, new Set()).get(key)!).add(b.id);
  }
}
if (cost.size) {
  console.log("Blocking sources, by how many areas each holds up:");
  for (const [key, ids] of [...cost].sort((a, b) => b[1].size - a[1].size)) {
    console.log(`  ${String(ids.size).padStart(2)}  ${key}`);
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
