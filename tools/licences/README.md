# Licence gate

Makes excluded data sources structurally impossible to ship, instead of relying on
remembering an exclusion list.

Anything committed to this repository is redistribution — including a single test
fixture tile. The gate exists so that fact is enforced at build time rather than
remembered at review time.

## Layout

```
tools/licences/
  licences.json           canonical source registry — the allowlist
  restrictions.ts         rule engine for obligations that SCOPE a source
  check-licences.ts       the gate
  check-licences.test.ts  its tests
  manifest.schema.json    schema for the provenance manifest
  texts/                  verbatim licence texts that must ship with the data
data/manifest.json        emitted by the bake pipeline
NOTICE.md                 generated — never hand-edited
.github/workflows/licence-gate.yml
```

## Run

```bash
node --experimental-strip-types tools/licences/check-licences.ts \
  --root . --registry tools/licences/licences.json \
  --manifest data/manifest.json --emit-notice NOTICE.md

node --experimental-strip-types --test tools/licences/check-licences.test.ts
```

`--strict` promotes "licence text not hashed" and "licence never read against its
publisher page" to errors. CI runs it that way on every pull request and on push
to main, so run it locally before pushing.

## The idea

A source is rarely wholly usable or wholly not. It is usable **inside a bounding
box**, or **with a mask applied**, or **only if fetched after a date**. Classifying
by licence name alone gets those wrong in both directions, so a source carries a
`class` *and* a list of `restrictions`, and both must pass.

| Restriction | Means |
| --- | --- |
| `clip` | licensed only inside a named region |
| `exclude-region` | not licensed inside a named region, optionally only from a date |
| `require-mask` | a named mask must have been applied at bake time |
| `require-election` | for a dual-licensed source, the elected limb must be recorded |
| `require-collection-allowlist` | the upstream collection queried must be on a list |
| `fetched-after` / `fetched-before` | data outside the window came under different terms |
| `require-eula-clause` | the product's own legal notice must carry a named clause |

An unrecognised restriction kind is an **error**, not a skip. A rule the gate
cannot evaluate must not read as a passing one.

## What the bake pipeline has to emit

A manifest entry per tile with a `layers` array recording which upstream source
each layer actually came from — per *layer*, not per tile, because obligations
scope per feature type. One tile can legitimately carry public-domain elevation
beside attribution-class land cover.

```jsonc
{
  "path": "data/tiles/example.tif",
  "sha256": "…",
  "layers": [{
    "layer": "elevation",
    "source": "usgs-3dep",
    "fetched_at": "2026-08-01T00:00:00Z",
    "masks": ["prism-cded"],          // when the source requires one
    "election": "CC-BY-4.0",          // when dual-licensed
    "collection": "SENTINEL-2-L2A",   // when the host serves several missions
    "upstream_licence_header": "…"    // whatever the service actually returned
  }]
}
```

Record `upstream_licence_header` wherever the upstream exposes one. It credits the
source *actually loaded* rather than the source we assumed, and the gate compares
the two.

## Failure codes

| Code | Level | Meaning |
| --- | --- | --- |
| `unknown-source` | error | Source key not in the registry. Fail closed — adding it requires reading the licence. |
| `excluded-class` | error | Source resolved, but its class is barred from the shipped path. |
| `outside-licensed-region` | error | Area lies outside the region a `clip` permits. |
| `restricted-region` | error | Area overlaps a region the source is not licensed in. |
| `missing-mask` | error | A mask the source's licence depends on was not applied. |
| `missing-election` | error | Dual-licensed source with no elected limb recorded. |
| `collection-not-allowlisted` | error | Upstream collection is not one this licence covers. |
| `fetched-outside-window` | error | Data acquired when different terms applied. |
| `missing-eula-clause` | error | A clause the licence forces into the product's legal notice is not declared. |
| `layer-source-mismatch` | error | This source does not supply that layer kind. |
| `upstream-header-mismatch` | error | The captured header names a producer the declared source is not. |
| `unknown-restriction-kind` | error | The registry declares a rule this build cannot evaluate. |
| `symlink-in-data-root` | error | A link inside a scanned root. Data roots hold bytes we redistribute; those must be real files. |
| `invalid-verification-date` | error | `verified_on` is not a valid past ISO date. |
| `missing-licence-text` | error | A `full-licence-text` source has no readable `licence_text_path`. |
| `unmanifested-asset` | error | Asset in a scanned root with no provenance record. |
| `hash-mismatch` | error | File contents changed without a re-bake. |
| `missing-file` / `path-escapes-root` | error | Manifest describes a file the gate cannot verify. |
| `missing-notice` | error | Notice required but the registry carries no text. |
| `duplicate-tile` / `tile-without-layers` | error | Provenance ambiguous or absent. |
| `unhashed-licence` | warn | Licence text not hashed. Upstream terms change silently. |
| `unverified-source` | warn | Licence never read against its primary publisher page. |
| `stale-verification` | warn | Last read more than `verification_max_age_days` ago. |

## Design notes

**Fail closed.** An unrecognised source is an error, not a pass. The only way to
add one is to write a registry entry, and writing one honestly requires having
read the licence.

**Hash the licence text.** `licence_text_sha256` pins the licence text *this
repo ships*, so our vendored copy cannot drift or be edited unnoticed. It does
not detect the publisher relicensing — nothing can, automatically. That is what
re-reading the page and bumping `verified_on` is for, and why `--strict` fails
on a stale one. Upstream terms do change: one dataset in this register moved
from ODbL to CDLA-Permissive-2.0, and third-party catalogues still describe it
by its old licence, so anything fetched before that date came under the old
terms. That is what `fetched-after` encodes.

**Trace the lineage, not the badge.** The costliest errors in this register came
from aggregates: a permissively-badged global relief model whose land layer is a
non-commercial dataset, and a CC BY elevation mosaic vertically registered against
a source carrying its own notice obligations. A licence badge describes the
aggregator's own rights, not the rights of everything inside it.

**Scope the scan.** `scan_roots` limits the asset walk to data directories.
Whole-tree scanning flagged every UI icon in the repo, which is the fastest way to
get a gate switched off. Widen the roots, never the ignore list.

**Unverified licences are warnings; the blocking gate runs `--strict`.** Plain
runs report the three verification warnings — `unverified-source` (never read),
`unhashed-licence` (no `licence_text_sha256`) and `stale-verification` (read
longer ago than `verification_max_age_days`) — as warnings, so local development
is not blocked. CI runs the gate with `--strict` on every pull request and on
push to main, which promotes **all three** to errors. So an unread licence, an
unhashed one, and one nobody has re-read inside the window are equally unable to
reach main. The job triggered by `release: published` fires after a release
exists and can only audit it, never prevent it.

## Not covered by this tool

The not-for-navigation statement. The gate writes it into `NOTICE.md` when
elevation or bathymetry is present, but the obligation is discharged at the point
of the verdict in the interface — beside a number someone might act on, not in a
notice file. No build check will catch its absence.
