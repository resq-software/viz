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

# Preflight, so a registry problem reads as one. `docker build` on a denied pull reports
# `denied: denied`, which looks like a credentials mistake and is usually not: the GDAL images
# live on ghcr.io, and a runtime that cannot reach it fails exactly this way while plain HTTPS to
# the same registry still works. Measured on one such host — curl with an anonymous bearer token
# read the manifest fine while the daemon was refused.
if ! "$runtime" image inspect "$image" >/dev/null 2>&1; then
    if ! "$runtime" build -t "$image" "$HERE" >&2; then
        echo >&2
        echo "error: could not build the bake image." >&2
        echo >&2
        echo "If the failure above says 'denied', the likely cause is the container runtime being" >&2
        echo "unable to pull from ghcr.io — not your credentials. Check it directly:" >&2
        echo >&2
        echo "  $runtime pull ghcr.io/osgeo/gdal:ubuntu-small-3.9.2     # runtime path" >&2
        echo "  TOK=\$(curl -s 'https://ghcr.io/token?scope=repository:osgeo/gdal:pull' | jq -r .token)" >&2
        echo "  curl -sI -H \"Authorization: Bearer \$TOK\" \\" >&2
        echo "    https://ghcr.io/v2/osgeo/gdal/manifests/ubuntu-small-3.9.2   # plain HTTPS path" >&2
        echo >&2
        echo "If the second works and the first does not, it is the runtime's registry access," >&2
        echo "and the bake has to run somewhere that can reach ghcr.io. Do NOT repin to" >&2
        echo "docker.io/osgeo/gdal to work around it: that mirror stopped at GDAL 3.6.3 in March" >&2
        echo "2023 and is not the same toolchain." >&2
        exit 1
    fi
fi

# --network=none deliberately: fetching happens in its own step with its own provenance record.
# A bake that can reach the network is a bake that can quietly substitute an input.
#
# --entrypoint and "$@", NOT `bash -c "... $*"`. Interpolating the arguments into a shell string
# let them BE shell: `bake.sh 'x; true'` ran run-bake.sh with x, then ran `true`, whose zero exit
# replaced the stub's failure — and this script went on to print a generator string for a bake
# that never happened. A wrapper whose entire job is recording provenance must not be able to
# claim provenance for nothing. Passing the arguments as arguments also keeps their boundaries,
# so an area id with a space stays one id.
"$runtime" run --rm --network=none \
    -v "$ROOT:/work" -w /work \
    --entrypoint /work/tools/bake/run-bake.sh \
    "$image" "$@" >&2

# The line the manifest wants. Emitted last and on stdout, so a caller can capture it while the
# build and bake chatter above goes to stderr.
echo "resq-viz-bake/1 gdal@${TOOLCHAIN_DIGEST}"
