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

import { readFileSync, writeFileSync, readdirSync, lstatSync, realpathSync, existsSync } from "node:fs";
import { createHash } from "node:crypto";
import { join, relative, extname, resolve, sep } from "node:path";

import { evaluateRestrictions, KNOWN_KINDS, parseIso, type BBox, type Restriction } from "./restrictions.ts";
import { ancestorsOf, resolveLineage } from "./lineage.ts";

// ---------------------------------------------------------------- types

type LicenceClass =
  | "public-domain"
  | "attribution"
  /** Permissive, but obliges US to bind our own downstream users by contract.
   *  A Creative Commons licence never does that — it licenses downstream
   *  automatically — so filing the two together would let a flow-down source
   *  through on a notice string alone. */
  | "flow-down"
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
  /** v2: repo-relative path to the verbatim licence text. REQUIRED when
   *  notice_kind is "full-licence-text" — a licence that obliges the text to
   *  travel with the data is not discharged by a promise to add it later. */
  licence_text_path?: string;
  /** v2: layer kinds this source may legitimately supply. A bathymetry layer
   *  sourced from a land-cover product is a provenance error, not a licence one,
   *  but it invalidates the notice generated for it either way. */
  permitted_layers?: string[];
  /** v2: obligations that scope a source rather than admitting or barring it. */
  restrictions?: Restriction[];
  /** v3: registry keys this source is built from. Walked transitively, because
   *  a merged product carries its inputs' licences and a licence badge
   *  describes only the aggregator's own rights. Version-pinned, since a
   *  product can acquire an ancestor at a version bump. */
  derived_from?: string[];
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

/** Symlinks encountered inside a scanned data root; reported by the caller. */
const symlinksFound: string[] = [];

function walk(dir: string, out: string[] = []): string[] {
  let entries: string[];
  try { entries = readdirSync(dir); } catch { return out; }
  for (const entry of entries) {
    const abs = join(dir, entry);
    const rel = relative(ROOT, abs);
    if (ignored(rel)) continue;

    // lstat, NOT stat: stat follows the link, so a symlinked DIRECTORY reports
    // as an ordinary one and the walk descends through it, recording files that
    // are physically outside the root under paths that look inside it. A data
    // root holds bytes we redistribute, so those must be real files — a link of
    // any kind is refused rather than resolved.
    let st;
    try { st = lstatSync(abs); } catch { continue; }
    if (st.isSymbolicLink()) { symlinksFound.push(rel); continue; }

    if (st.isDirectory()) walk(abs, out);
    else if (st.isFile() && scanExts.has(extname(entry).toLowerCase())) out.push(rel);
  }
  return out;
}

/** Content hash, or null when the file cannot be read at all.
 *  One read rather than exists-then-read: the pre-check is a race, and the
 *  read's own failure already carries the answer. */
function sha256OrNull(abs: string): string | null {
  try {
    return createHash("sha256").update(readFileSync(abs)).digest("hex");
  } catch {
    return null;
  }
}

/** Validates a licence text file, returning a reason phrase or null if fine.
 *  `escaped` is the containment failure from pathEscape, checked first so a path
 *  outside the root is never read. */
function readLicenceText(abs: string, escaped: string | null): string | null {
  if (escaped) return "is not inside the scanned root.";
  let text: string;
  try {
    text = readFileSync(abs, "utf8");
  } catch (e) {
    // ENOENT (missing), EISDIR (a directory) and EACCES all land here, which is
    // exactly the set of ways this can be misconfigured.
    return `is not a readable regular file: ${(e as Error).message}`;
  }
  return text.trim().length === 0 ? "is empty." : null;
}

/**
 * Returns a reason string when a manifest path reaches outside ROOT, else null.
 *
 * `resolve` alone closes only the `../` escape. A symlink placed inside the root
 * and pointing out of it resolves clean lexically, so the physical path has to
 * be checked too — `realpathSync` follows every link in the chain. A dangling
 * symlink throws there, and is reported rather than swallowed: a path the gate
 * cannot resolve is one it cannot vouch for.
 */
function pathEscape(relPath: string): string | null {
  const lexical = resolve(ROOT, relPath);
  if (lexical !== ROOT && !lexical.startsWith(ROOT + sep)) {
    return "Manifest path resolves outside the scanned root. Provenance can only be asserted for files inside it.";
  }
  if (!existsSync(lexical)) return null;   // reported separately as missing-file
  try {
    // realpath the WHOLE path, not just lstat the terminal entry. Checking only
    // the last component misses the case that matters: a regular file below a
    // SYMLINKED PARENT. lstat on the file says "not a link" and the lexical
    // path looks contained, while the bytes live somewhere else entirely.
    // Measured before this: a tile at vendor/tiles/t.tif, where vendor/tiles
    // links outside, passed the gate with a matching hash of external bytes.
    //
    // ROOT is realpathed too — it can itself sit under a link (/tmp on macOS),
    // and comparing a resolved child against an unresolved root reports every
    // path as an escape.
    const physicalRoot = realpathSync(ROOT);
    const physical = realpathSync(lexical);
    if (physical !== physicalRoot && !physical.startsWith(physicalRoot + sep)) {
      return `Manifest path resolves outside the scanned root (physically ${physical}). `
        + `The gate would hash bytes it is not gating.`;
    }
  } catch (e) {
    return `Manifest path could not be resolved: ${(e as Error).message}`;
  }
  return null;
}

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

  // A flow-down source obliges us to bind our own customers by contract. That
  // obligation belongs to the CLASS, not to whether someone remembered to
  // attach a restriction — otherwise the next source of this kind sails
  // through on a notice string.
  if (entry.class === "flow-down"
    && !(entry.restrictions ?? []).some((r) => r.kind === "require-eula-clause")) {
    add("error", "flow-down-without-clause", `registry:${key}`,
      `is class "flow-down" but declares no require-eula-clause restriction. This class exists `
      + `because the licence obliges US to bind downstream users contractually; without a named `
      + `clause the gate cannot tell whether that was done.`);
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

    // A manifest path that reaches outside ROOT would have the gate hash a file
    // it is not gating — the provenance record would describe bytes that are
    // not the shipped bytes. Two distinct escapes, and `resolve` only closes the
    // first: `../` in the path, and a SYMLINK inside the root pointing out of
    // it. Verified: before this check, a symlinked tile pointing outside made
    // the gate hash the external file and report "Licence gate passed".
    const escape = pathEscape(norm);
    if (escape) {
      add("error", "path-escapes-root", norm, escape);
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

      // Lineage. The class check above clears this source; this clears what it
      // is MADE OF. Three findings in this register were merged products
      // importing an excluded input, each recorded in prose while the gate
      // waved the product through on its own class.
      for (const p of resolveLineage(layer.source, sources, allowed)) {
        add("error", p.code, where, `${entry.name} ${p.message}`);
      }

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

      // A licence that obliges its full text to travel with the data is not
      // discharged by a credit line, and certainly not by a TODO in the
      // generated notice — which is what this emitted before. Require the text
      // to exist and be non-empty at generation time.
      if (entry.notice_kind === "full-licence-text") {
        const p = entry.licence_text_path;
        if (!p) {
          add("error", "missing-licence-text", where,
            `${entry.name} is notice_kind "full-licence-text" but declares no licence_text_path. `
            + `Its licence requires the verbatim text to ship with the data.`);
        } else {
          // Read it and handle failure, rather than checking first and reading
          // after. An existsSync/statSync pre-check is a time-of-check to
          // time-of-use race (CodeQL js/file-system-race), and it is also more
          // code for less: one readFileSync already distinguishes every case we
          // care about — ENOENT for missing, EISDIR for a directory, EACCES for
          // unreadable. A directory used to crash the gate here with an
          // unhandled EISDIR; now it is a finding like any other.
          const problem = readLicenceText(resolve(ROOT, p), pathEscape(p));
          if (problem) {
            add("error", "missing-licence-text", where,
              `${entry.name}: licence_text_path "${p}" ${problem}`);
          }
        }
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
        // `verified_on` is the one field asserting a human read the licence, so
        // it must not be satisfiable by a value that is not a date. This used
        // Date.parse directly: an unparseable value gave NaN and a future value
        // gave a negative age, and BOTH compared false against the limit — so
        // "not-a-date" and "verified in 2099" each read as freshly verified.
        const verifiedMs = parseIso(entry.verified_on);
        if (verifiedMs === null) {
          add("error", "invalid-verification-date", where,
            `${entry.name}: verified_on is ${JSON.stringify(entry.verified_on)}, which is not a valid ISO date. `
            + `This field asserts someone read the licence; it must not be satisfiable by a non-date.`);
        } else if (verifiedMs > Date.now()) {
          add("error", "invalid-verification-date", where,
            `${entry.name}: verified_on is ${JSON.stringify(entry.verified_on)}, which is in the future. `
            + `A licence cannot have been read on a date that has not happened yet.`);
        } else {
          const ageDays = (Date.now() - verifiedMs) / 86_400_000;
          if (ageDays > registry.policy.verification_max_age_days) {
            add("warn", "stale-verification", where,
              `${entry.name}: last verified ${Math.floor(ageDays)} days ago (limit ${registry.policy.verification_max_age_days}).`);
          }
        }
      }
    }

    // Content hash must match, or the provenance record describes different bytes.
    const abs = join(ROOT, norm);
    if (escape) {
      // Do not read it. A hash of bytes outside the root is worse than no hash:
      // it would MATCH the manifest and read as corroboration.
    } else {
      // Same reasoning as the licence text above: hash it and handle the
      // failure, rather than existsSync-then-read, which is a check-then-use
      // race and one syscall more than the read already tells us.
      const actual = sha256OrNull(abs);
      if (actual === null) {
        add("error", "missing-file", norm, `Manifest references a file that cannot be read.`);
      } else if (actual !== tile.sha256) {
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
for (const rel of symlinksFound) {
  add("error", "symlink-in-data-root", rel,
    `Symlink inside a scanned data root. The gate asserts provenance for bytes it hashes, and a `
    + `link can point anywhere — including outside the root. Commit the real file instead.`);
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
    lines.push("## Licence texts", "",
      "The licences below require their full text to travel with the data. Reproduced",
      "verbatim from the path each entry records in the registry; the gate refuses to",
      "generate this file if one of them is missing or empty.",
      "");
    for (const [, s] of fullText) {
      lines.push(`### ${s.name} — ${s.spdx ?? "see licence"}`, "", `<${s.licence_url}>`, "", "```");
      // Validated in the main loop, so this read cannot be reached with a bad path.
      lines.push(readFileSync(resolve(ROOT, s.licence_text_path!), "utf8").trimEnd());
      lines.push("```", "");
    }
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
