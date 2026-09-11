# Bake toolchain — pinned by digest, so a tile can be re-derived rather than taken on trust.
#
# The bake is not a CI step. It runs rarely and produces artefacts CI then verifies by hash, so
# GDAL never has to enter the application's dependency tree. But a bake that only runs on one
# laptop makes the provenance manifest unverifiable: a lineage nobody can re-derive is a story
# rather than a record, which is the defect class this repository keeps finding in its own checks.
#
# PINNED BY DIGEST, NOT BY TAG. `ubuntu-small-3.9.2` is a moving target — the publisher can and
# does republish it — so a manifest recording the tag has said almost nothing about what produced
# its bytes. The digest below is what `bake.sh` stamps into the manifest's `generator` field,
# which is what makes "these bytes came from those inputs through this toolchain" checkable.
#
# Resolved 2026-09-10 from the ghcr.io registry API for tag ubuntu-small-3.9.2.
#
# To move the toolchain, resolve the new digest and update BOTH this line and TOOLCHAIN_DIGEST in
# bake.sh, then re-bake. Tiles baked under an older toolchain and never re-derived are exactly
# what recording the digest exists to make visible:
#
#   TOK=$(curl -s "https://ghcr.io/token?scope=repository:osgeo/gdal:pull" | jq -r .token)
#   curl -sI -H "Authorization: Bearer $TOK" \
#     -H "Accept: application/vnd.oci.image.index.v1+json" \
#     https://ghcr.io/v2/osgeo/gdal/manifests/<tag> | grep -i docker-content-digest
FROM ghcr.io/osgeo/gdal@sha256:6af57cbe64534fd8c3803ac4158e9bf812dfb1968cf13e8922b80f665d76d063

# Pinned for the same reason as the base image, and pyproj especially: it bundles PROJ's datum
# grids, and a shift that changes between releases moves every reprojected pixel.
COPY requirements.txt /tmp/requirements.txt
RUN python3 -m pip install --no-cache-dir --break-system-packages -r /tmp/requirements.txt \
    && python3 -c "import rasterio, pyproj, numpy, PIL; print('bake toolchain ready')"

WORKDIR /work
ENTRYPOINT ["/bin/bash"]
