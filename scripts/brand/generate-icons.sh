#!/usr/bin/env bash
# Copyright 2026 ResQ Systems, Inc.
# SPDX-License-Identifier: Apache-2.0
#
# Regenerates the ResQ Viz icon set from the canonical ResQ mark.
#
# The mark itself is NEVER redrawn here — `resq-mark-color.svg` is vendored
# verbatim from the shared ResQ brand asset set. This
# script only *packages* it: compositing onto the viz console surface at the
# padding each target requires. Per the brand style guide, restyling the mark
# is out of scope; packaging it correctly is not.
#
# Why not just copy the apex PWA PNGs? They ship with a transparent
# background (mean alpha ~0.41) yet are declared `purpose: "maskable"`.
# A maskable icon must be fully opaque with its content inside the 80%
# safe-zone circle, or the launcher crops into transparent pixels.
#
# Requires: rsvg-convert, magick (ImageMagick).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/../../src/ResQ.Viz.Web/client/public"
MARK="$HERE/resq-mark-color.svg"

# Viz console surface — must equal `theme_color`/`background_color` in
# site.webmanifest and <meta name="theme-color"> in index.html.
SURFACE="#111319"

# Render the mark to a transparent PNG at the given edge length.
render() { rsvg-convert -w "$1" -h "$1" -a "$MARK" -o "$2"; }

# Compose: <size> <mark-scale-%> <corner-radius-%> <output>
#   mark-scale   fraction of the canvas the artwork occupies
#   radius       0 for full-bleed (OS applies its own mask)
compose() {
  local size=$1 scale=$2 radius=$3 out=$4
  local inner rpx tmp
  inner=$(( size * scale / 100 ))
  rpx=$(( size * radius / 100 ))
  tmp=$(mktemp --suffix=.png)
  render "$inner" "$tmp"
  if [ "$rpx" -gt 0 ]; then
    # Rounded plate in the console surface, mark centred on top.
    magick -size "${size}x${size}" xc:none -fill "$SURFACE" \
      -draw "roundrectangle 0,0,$((size-1)),$((size-1)),$rpx,$rpx" \
      "$tmp" -gravity center -composite -strip "$out"
  else
    magick -size "${size}x${size}" xc:"$SURFACE" \
      "$tmp" -gravity center -composite -strip "$out"
  fi
  rm -f "$tmp"
}

# `any` icons: rounded plate, mark with breathing room.
compose 512 72 22 "$OUT/icon-512.png"
compose 192 72 22 "$OUT/icon-192.png"
compose  32 82 22 "$OUT/favicon-32.png"

# apple-touch-icon: full-bleed; iOS applies its own squircle mask and
# ignores alpha, so a transparent PNG would render on black.
compose 180 74 0 "$OUT/apple-touch-icon.png"

# maskable: full-bleed, artwork inside the 80% safe-zone circle.
compose 512 62 0 "$OUT/icon-maskable-512.png"

echo "Generated:"
for f in icon-512 icon-192 favicon-32 apple-touch-icon icon-maskable-512; do
  printf '  %-24s %s\n' "$f.png" "$(magick identify -format '%wx%h %[channels]' "$OUT/$f.png")"
done
