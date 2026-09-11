#!/usr/bin/env bash
# Runs a bake inside the pinned toolchain, and records which toolchain that was.
#
# Usage:  tools/bake/bake.sh <area-id> [...]
#
# The point of this wrapper is the LAST line it prints: the generator string that belongs in the
# provenance manifest. Without the toolchain in that string, the manifest records which sources a
# tile drew on but not what turned them into pixels — and a reprojection is only reproducible if
# the projection library is. pyproj bundles PROJ's datum grids; a shift that changes between
# releases moves every pixel.
set -euo pipefail

# Must match the FROM line in this directory's Dockerfile. Checked below rather than trusted,
# because the failure mode of them drifting apart is a manifest that names a toolchain other than
# the one that ran — which is worse than naming none, since it reads as provenance.
TOOLCHAIN_DIGEST="sha256:6af57cbe64534fd8c3803ac4158e9bf812dfb1968cf13e8922b80f665d76d063"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

from_digest="$(grep -oE 'sha256:[0-9a-f]{64}' "$HERE/Dockerfile" | head -1)"
if [ "$from_digest" != "$TOOLCHAIN_DIGEST" ]; then
    echo "error: Dockerfile FROM digest and TOOLCHAIN_DIGEST disagree." >&2
    echo "  Dockerfile:        $from_digest" >&2
    echo "  TOOLCHAIN_DIGEST:  $TOOLCHAIN_DIGEST" >&2
    echo "Update both, or the manifest will name a toolchain that did not run." >&2
    exit 1
fi

if [ "$#" -eq 0 ]; then
    echo "usage: $0 <area-id> [...]" >&2
    echo "Area ids come from data/areas.json; tools/areas/check-areas.ts lists which are bakeable." >&2
    exit 2
fi

runtime=""
for candidate in docker podman; do
    if command -v "$candidate" >/dev/null 2>&1; then runtime="$candidate"; break; fi
done
if [ -z "$runtime" ]; then
    echo "error: neither docker nor podman is available, and the bake does not run outside the" >&2
    echo "pinned image — that is the whole point. Install one, or run this where one exists." >&2
    exit 1
fi

image="resq-viz-bake:${TOOLCHAIN_DIGEST#sha256:}"
"$runtime" build -t "$image" "$HERE" >&2

# --network=none deliberately: fetching happens in its own step with its own provenance record.
# A bake that can reach the network is a bake that can quietly substitute an input.
"$runtime" run --rm --network=none \
    -v "$ROOT:/work" -w /work \
    "$image" -c "tools/bake/run-bake.sh $*" >&2

# The line the manifest wants. Emitted last and on stdout, so a caller can capture it while the
# build and bake chatter above goes to stderr.
echo "resq-viz-bake/1 gdal@${TOOLCHAIN_DIGEST}"
