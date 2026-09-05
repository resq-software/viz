#!/usr/bin/env node
/**
 * Bake-time licence gate.
 *
 * Fails closed. An asset in the working tree that is not accounted for in the
 * provenance manifest, or whose source is not on the allowlist, fails the
 * build. Adding a source requires a registry entry, and writing a registry
 * entry requires having read the licence.
 *
 *   npx tsx check-licences.ts --root . --manifest data/manifest.json
 *   npx tsx check-licences.ts --emit-notice NOTICE.md
 *   npx tsx check-licences.ts --strict          # warnings become errors
 *
 * Node 22+: node --experimental-strip-types check-licences.ts
 */

import { readFileSync, writeFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { join, relative, extname, resolve, sep } from "node:path";

import { evaluateRestrictions, KNOWN_KINDS, type BBox, type Restriction } from "./restrictions.ts";

// ---------------------------------------------------------------- types

type LicenceClass =
  | "public-domain"
  | "attribution"
  | "share-alike"
  | "non-commercial"
  | "govt-restricted"
  | "proprietary";

interface SourceEntry {
  name: string;
  class: LicenceClass;
  spdx: string | null;
  licence_url: string;
  licence_text_sha256: string | null;
  verified_on: string | null;
  notice: string | null;
  courtesy_credit: string | null;
  coverage: string | null;
  notes: string | null;
  /** v2: whether the notice is a licence CONDITION or merely a requested credit.
   *  Recording a requested credit as required makes the pipeline treat a
   *  public-domain source as conditional, which is how Landsat was mis-recorded. */
  notice_required?: boolean;
  /** v2: "full-licence-text" means a one-line credit does not discharge it. */
  notice_kind?: "string" | "full-licence-text";
  /** v2: layer kinds this source may legitimately supply. A bathymetry layer
   *  sourced from a land-cover product is a provenance error, not a licence one,
   *  but it invalidates the notice generated for it either way. */
  permitted_layers?: string[];
  /** v2: obligations that scope a source rather than admitting or barring it. */
  restrictions?: Restriction[];
}

interface Registry {
  version: number;
  policy: {
    allowed_classes: LicenceClass[];
    verification_max_age_days: number;
    scan_extensions: string[];
    ignore_paths: string[];
    /** v2: directories the asset walk covers. Omit for whole-tree. Scoping this
     *  is what stops the gate erroring on every UI icon in the repository. */
    scan_roots?: string[];
    /** v2: named bbox lists referenced by clip / exclude-region restrictions. */
    regions?: Record<string, BBox[]>;
  };
  sources: Record<string, SourceEntry | string>;
}

interface Layer {
  layer: string;
  source: string;
  fetched_at: string;
  upstream_url?: string;
  upstream_licence_header?: string;
  election?: string;
  /** v2: mask ids applied at bake time, e.g. "prism-cded". */
  masks?: string[];
  /** v2: the upstream collection actually queried. */
  collection?: string;
}
interface Tile { path: string; sha256: string; layers: Layer[]; }
interface Area { id: string; name: string; bbox: number[]; notes?: string; tiles: Tile[]; }
interface Manifest {
  schema: number;
  generated_at: string;
  generator: string;
  areas: Area[];
  /** v2: EULA clause ids the product's own legal notice is asserted to carry. */
  eula_clauses?: string[];
}

/** Markers that betray an excluded upstream appearing in a licence header the
 *  bake pipeline captured. The header exists to credit THE SOURCE ACTUALLY
 *  LOADED; if it names something the declared source is not, the manifest is
 *  describing different data than was fetched. */
const EXCLUDED_UPSTREAM_MARKERS: readonly { marker: RegExp; what: string }[] = [
  { marker: /airbus|worlddem|copernicus\s*dem|glo-?30/i, what: "Copernicus WorldDEM-30 / Airbus DS" },
  { marker: /fabdem|fathom/i, what: "FABDEM / FathomDEM (CC BY-NC-SA)" },
  { marker: /openstreetmap|\bosm\b|odbl/i, what: "an ODbL database" },
  { marker: /maxar|vantor/i, what: "Maxar / Vantor (CC BY-NC)" },
  { marker: /\besri\b/i, what: "Esri (master licence agreement)" },
];

type Level = "error" | "warn";
interface Finding { level: Level; code: string; where: string; message: string; }

// ---------------------------------------------------------------- args

function arg(name: string, fallback?: string): string | undefined {
  const i = process.argv.indexOf(`--${name}`);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1] : fallback;
}
const flag = (name: string) => process.argv.includes(`--${name}`);

const ROOT = resolve(arg("root", ".")!);
const REGISTRY_PATH = resolve(arg("registry", join(ROOT, "licences.json"))!);
const MANIFEST_PATH = resolve(arg("manifest", join(ROOT, "data/manifest.json"))!);
const EMIT_NOTICE = arg("emit-notice");
const STRICT = flag("strict");

// ---------------------------------------------------------------- load

function loadJson<T>(path: string, label: string): T {
  if (!existsSync(path)) {
    console.error(`FATAL: ${label} not found at ${path}`);
    process.exit(2);
  }
  try {
    return JSON.parse(readFileSync(path, "utf8")) as T;
  } catch (e) {
    console.error(`FATAL: ${label} is not valid JSON: ${(e as Error).message}`);
    process.exit(2);
  }
}

const registry = loadJson<Registry>(REGISTRY_PATH, "registry");
const manifest = loadJson<Manifest>(MANIFEST_PATH, "manifest");

// Keys beginning with an underscore are comments, not sources.
const sources = new Map<string, SourceEntry>(
  Object.entries(registry.sources)
    .filter((pair): pair is [string, SourceEntry] =>
      !pair[0].startsWith("_") && typeof pair[1] === "object" && pair[1] !== null)
);

const allowed = new Set<LicenceClass>(registry.policy.allowed_classes);
const findings: Finding[] = [];
const add = (level: Level, code: string, where: string, message: string) =>
  findings.push({ level, code, where, message });

// ---------------------------------------------------------------- walk

const scanExts = new Set(registry.policy.scan_extensions.map((e) => e.toLowerCase()));
const ignore = registry.policy.ignore_paths;

function ignored(relPath: string): boolean {
  const segments = relPath.split(sep);
  return ignore.some((ig) =>
    relPath === ig || relPath.startsWith(ig + sep) || segments.includes(ig)
  );
}

function walk(dir: string, out: string[] = []): string[] {
  let entries: string[];
  try { entries = readdirSync(dir); } catch { return out; }
  for (const entry of entries) {
    const abs = join(dir, entry);
    const rel = relative(ROOT, abs);
    if (ignored(rel)) continue;
    let st;
    try { st = statSync(abs); } catch { continue; }
    if (st.isDirectory()) walk(abs, out);
    else if (scanExts.has(extname(entry).toLowerCase())) out.push(rel);
  }
  return out;
}

const sha256 = (abs: string) =>
  createHash("sha256").update(readFileSync(abs)).digest("hex");

// ---------------------------------------------------------------- checks

const manifestPaths = new Map<string, { area: Area; tile: Tile }>();
const usedSources = new Set<string>();
/** Layer kinds actually present, so the notice asserts only what ships. */
const usedLayers = new Set<string>();
const declaredEula = new Set<string>(manifest.eula_clauses ?? []);

// The registry may declare a rule this build cannot evaluate. That is a gate
// defect, not a data defect, and it must be loud: an unenforced rule reads as a
// passing one. Checked once up front so it reports even for unused sources.
for (const [key, entry] of sources) {
  for (const r of entry.restrictions ?? []) {
    if (!KNOWN_KINDS.includes(r.kind)) {
      add("error", "unknown-restriction-kind", `registry:${key}`,
        `declares restriction kind "${r.kind}", which this gate cannot evaluate. `
        + `It is NOT being enforced. Upgrade the gate or remove the rule.`);
    }
  }
}

for (const area of manifest.areas ?? []) {
  for (const tile of area.tiles ?? []) {
    const norm = tile.path.split("/").join(sep);
    if (manifestPaths.has(norm)) {
      add("error", "duplicate-tile", norm,
        `Listed more than once in the manifest; provenance is ambiguous.`);
    }
    manifestPaths.set(norm, { area, tile });

    // A manifest path that resolves outside ROOT would have the gate hash a file
    // it is not gating. Caught incidentally today only when the traversal depth
    // happens not to resolve; make it explicit.
    if (!resolve(ROOT, norm).startsWith(ROOT + sep) && resolve(ROOT, norm) !== ROOT) {
      add("error", "path-escapes-root", norm,
        `Manifest path resolves outside the scanned root. Provenance can only be asserted for files inside it.`);
    }

    // A tile with no layers has no provenance at all, and the layer loop below
    // would iterate zero times and pass it silently.
    if (!tile.layers || tile.layers.length === 0) {
      add("error", "tile-without-layers", norm,
        `Tile has no layers, so it carries no provenance record. Every shipped byte needs a declared source.`);
    }

    // Every layer must resolve to a permitted source.
    for (const layer of tile.layers ?? []) {
      const where = `${area.id}:${tile.path}#${layer.layer}`;
      const entry = sources.get(layer.source);

      if (!entry) {
        add("error", "unknown-source", where,
          `Source "${layer.source}" is not in the registry. Add an entry — which means reading the licence — or remove the data.`);
        continue;
      }
      usedSources.add(layer.source);
      usedLayers.add(layer.layer);

      if (!allowed.has(entry.class)) {
        add("error", "excluded-class", where,
          `${entry.name} is class "${entry.class}", which may not enter the shipped data path. ${entry.notes ?? ""}`.trim());
        continue;
      }

      // v2: keyed on notice_required, not on class. An attribution-class source
      // whose credit is merely REQUESTED does not need one; a public-domain
      // aggregate that carries a third-party condition might.
      const noticeRequired = entry.notice_required ?? (entry.class === "attribution");
      if (noticeRequired && !entry.notice) {
        add("error", "missing-notice", where,
          `${entry.name} requires a notice but the registry carries no notice text. Attribution cannot be generated.`);
      }

      // v2: the layer kind must be one this source can legitimately supply.
      if (entry.permitted_layers && !entry.permitted_layers.includes(layer.layer)) {
        add("error", "layer-source-mismatch", where,
          `${entry.name} does not supply "${layer.layer}" layers (permits: ${
            entry.permitted_layers.length ? entry.permitted_layers.join(", ") : "none — this source is excluded"
          }). The notice generated for this tile would credit the wrong producer.`);
      }

      // v2: the captured upstream header must not name a different, excluded
      // producer. This field was designed to credit the source actually loaded
      // and was never read.
      const header = layer.upstream_licence_header;
      if (header) {
        for (const { marker, what } of EXCLUDED_UPSTREAM_MARKERS) {
          if (!marker.test(header)) continue;
          if (marker.test(entry.name) || marker.test(entry.spdx ?? "")) continue;
          add("error", "upstream-header-mismatch", where,
            `the captured upstream licence header names ${what}, but this layer declares source `
            + `"${layer.source}" (${entry.name}). The bytes fetched are not the bytes the manifest describes. `
            + `Header: ${JSON.stringify(header.slice(0, 120))}`);
          break;
        }
      }

      // v2: obligations that scope a source rather than admitting or barring it.
      for (const v of evaluateRestrictions(entry.restrictions, {
        area: { id: area.id, bbox: area.bbox ?? [] },
        layer,
        policy: registry.policy,
        declaredEulaClauses: declaredEula,
      })) {
        add("error", v.code, where, `${entry.name}: ${v.message}`);
      }

      if (!entry.licence_text_sha256) {
        add("warn", "unhashed-licence", where,
          `${entry.name}: licence text has not been hashed. Upstream terms change silently — Microsoft's building footprints moved from ODbL to CDLA-Permissive-2.0.`);
      }

      if (!entry.verified_on) {
        add("warn", "unverified-source", where,
          `${entry.name}: licence has never been read against its primary publisher page.`);
      } else {
        const ageDays = (Date.now() - Date.parse(entry.verified_on)) / 86_400_000;
        if (ageDays > registry.policy.verification_max_age_days) {
          add("warn", "stale-verification", where,
            `${entry.name}: last verified ${Math.floor(ageDays)} days ago (limit ${registry.policy.verification_max_age_days}).`);
        }
      }
    }

    // Content hash must match, or the provenance record describes different bytes.
    const abs = join(ROOT, norm);
    if (!existsSync(abs)) {
      add("error", "missing-file", norm, `Manifest references a file that is not on disk.`);
    } else {
      const actual = sha256(abs);
      if (actual !== tile.sha256) {
        add("error", "hash-mismatch", norm,
          `Content hash ${actual.slice(0, 12)} does not match manifest ${tile.sha256.slice(0, 12)}. Re-bake; do not hand-edit the manifest.`);
      }
    }
  }
}

// Anything checked in that the manifest does not know about is undocumented
// redistribution. This is the check that catches a single test fixture tile.
// v2: scoped to scan_roots. Whole-tree scanning made the gate error on every UI
// icon in the repository, which is the fastest way to get a gate switched off.
// Narrowing the scope is safe only because anything OUTSIDE these roots is not a
// data asset — widen the roots, never the ignore list, when that stops holding.
const scanRoots = registry.policy.scan_roots?.length
  ? registry.policy.scan_roots
  : ["."];
const scanned: string[] = [];
for (const root of scanRoots) {
  const abs = resolve(ROOT, root);
  if (!existsSync(abs)) continue;
  walk(abs, scanned);
}
for (const rel of scanned) {
  if (!manifestPaths.has(rel)) {
    add("error", "unmanifested-asset", rel,
      `Asset in a scanned data root with no provenance record. Anything committed is redistribution, including test fixtures.`);
  }
}

// ---------------------------------------------------------------- notice

function buildNotice(): string {
  const used = [...usedSources]
    .map((k) => [k, sources.get(k)!] as const)
    .sort((a, b) => a[1].name.localeCompare(b[1].name));

  const needsNotice = (s: SourceEntry) => s.notice_required ?? (s.class === "attribution");
  const pd = used.filter(([, s]) => !needsNotice(s));
  const attr = used.filter(([, s]) => needsNotice(s));
  const fullText = attr.filter(([, s]) => s.notice_kind === "full-licence-text");

  const geo = ["elevation", "bathymetry", "landcover", "imagery", "buildings", "hydrology"]
    .some((k) => usedLayers.has(k));

  const lines: string[] = [
    "# Third-party notices",
    "",
    "Generated by `check-licences.ts` from the provenance manifest. Do not edit by hand;",
    "edits are overwritten on the next bake.",
    "",
    geo
      ? "This product includes geospatial data from the sources below. Terrain, seabed and "
        + "land cover data are resampled and merged from these sources, not redistributed verbatim."
      : "This product includes third-party assets from the sources below, modified for use.",
    "",
  ];

  if (attr.length) {
    lines.push("## Attribution required", "");
    for (const [, s] of attr) {
      lines.push(`### ${s.name}`, "", s.notice!, "", `<${s.licence_url}>`, "");
    }
  }

  if (fullText.length) {
    lines.push("## Licence texts that must ship in full", "",
      "The sources below are not discharged by a credit line. Their licence requires the",
      "full text to travel with the data, so it must be reproduced verbatim below this",
      "heading before release.",
      "");
    for (const [, s] of fullText) {
      lines.push(`- **${s.name}** — ${s.spdx ?? "see licence"}: <${s.licence_url}>`);
    }
    lines.push("", "> TODO: paste the verbatim licence text for each entry above.", "");
  }

  if (pd.length) {
    lines.push("## Public domain", "",
      "The following are works of the United States federal government or are otherwise",
      "in the public domain. No attribution is legally required; credit is given as a courtesy.",
      "");
    for (const [, s] of pd) {
      lines.push(`- ${s.courtesy_credit ?? s.name}`);
    }
    lines.push("");
  }

  // Only assert this when terrain or seabed data is actually present. A legal
  // notice that disclaims data the product does not ship is an over-claim, and it
  // trains readers to skim the section that matters most.
  if (usedLayers.has("elevation") || usedLayers.has("bathymetry")) {
    lines.push(
      "## Not for navigation",
      "",
      "Bathymetric and elevation data in this product are provided for simulation only.",
      "They are not certified for navigation and must not be used for navigational",
      "purposes. Clearance, depth and slope figures produced by this product are",
      "simulated estimates and are not decision-grade.",
      ""
    );
  }

  return lines.join("\n");
}

// ---------------------------------------------------------------- report

const errors = findings.filter((f) => f.level === "error");
const warns = findings.filter((f) => f.level === "warn");

const byCode = new Map<string, Finding[]>();
for (const f of [...errors, ...warns]) {
  if (!byCode.has(f.code)) byCode.set(f.code, []);
  byCode.get(f.code)!.push(f);
}

for (const [code, list] of byCode) {
  const level = list[0].level.toUpperCase();
  console.log(`\n${level}  ${code}  (${list.length})`);
  for (const f of list) console.log(`  ${f.where}\n    ${f.message}`);
}

console.log(
  `\n${manifestPaths.size} tiles, ${usedSources.size} sources, ` +
  `${errors.length} error(s), ${warns.length} warning(s).`
);

if (EMIT_NOTICE) {
  if (errors.length) {
    console.log(`Not writing ${EMIT_NOTICE}: notices are only generated from a clean manifest.`);
  } else {
    writeFileSync(resolve(ROOT, EMIT_NOTICE), buildNotice(), "utf8");
    console.log(`Wrote ${EMIT_NOTICE}.`);
  }
}

if (errors.length || (STRICT && warns.length)) {
  console.error("\nLicence gate FAILED.");
  process.exit(1);
}
console.log("Licence gate passed.");
