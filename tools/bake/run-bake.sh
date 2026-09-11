#!/usr/bin/env bash
# The bake itself. NOT IMPLEMENTED — and it exits non-zero rather than pretending.
#
# tools/bake/bake.sh settles the reproducibility question: which toolchain ran, pinned by digest
# and stamped into the manifest. It does not settle what the bake DOES. That is still greenfield —
# there are no geospatial bytes in this repository and none of the stages below has ever run.
#
# This file exists so `bake.sh` calls something real. A stub that returned success, or a missing
# file producing a confusing shell error, would both be worse than this: the first would let a
# caller believe a bake happened, and this repository has spent a lot of effort on exactly that
# class of defect.
set -euo pipefail

cat >&2 <<'MSG'
tools/bake/run-bake.sh: the bake pipeline is not implemented.

What IS settled, and is what tools/bake/ currently provides:
  - the toolchain is pinned by digest (Dockerfile), so a bake is reproducible
  - bake.sh verifies the Dockerfile and its own digest agree before running
  - bake.sh emits the generator string the provenance manifest records
  - the container runs with --network=none, so a bake cannot substitute an input

What is NOT, and must be built before this returns success:
  1. fetch    — pull source rasters for an area bbox, recording fetched_at per layer
  2. reproject— source CRS to the area's targetCrs (data/areas.json)
  3. resample — onto the bake grid, 2048 square over 4000 m
  4. encode   — rg16 PNG, NOT gray8: 8 bits over heightScale is a 1.57 m quantum, which at
                1.95 m per cell is a 38.8 degree false slope, past what a ground vehicle climbs
  5. manifest — emit per-tile, per-layer provenance with sha256 and this toolchain's generator
  6. verify   — tools/licences/check-licences.ts --strict must pass on the result

Start from the areas tools/areas/check-areas.ts reports bakeable, and expect the first real tile
to light up licence checks that have never run: most are scoped per manifest layer and nothing
references those sources yet.
MSG
exit 1
