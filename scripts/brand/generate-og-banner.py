#!/usr/bin/env python3
# Copyright 2026 ResQ Systems, Inc.
# SPDX-License-Identifier: Apache-2.0
"""Regenerate the ResQ Viz Open Graph banner (1200x630).

Composition follows the apex site's og-banner visual language — dark ground,
faint grid, radar rings, the canonical mark, dashed mesh links to labelled
nodes, mono uppercase data labels, semantic status chips — so a link to
viz.resq.software reads as the same family as resq.software.

Colours are the brand tokens from the style guide, converted from oklch to
sRGB (see PALETTE). Nothing here is eyeballed, and the mark is vendored
verbatim rather than redrawn.

Requires: rsvg-convert.
"""
import pathlib
import subprocess

from mark import mark_body

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE / "../../src/ResQ.Viz.Web/client/public/og-banner.png"

# Brand tokens (STYLE_GUIDE.md oklch -> sRGB).
BG = "#0B0D14"
CARD = "#171C2B"
BORDER = "#1E2438"
FG = "#F0F2FA"
MONO = "#8A9BB8"
MUTED = "#7D8CAE"
RED = "#D43E3F"
BLUE = "#3B8FE8"
GREEN = "#25C68A"
AMBER = "#F5A623"

SANS = "Liberation Sans, Arial, DejaVu Sans, sans-serif"
MONOF = "DejaVu Sans Mono, Liberation Mono, monospace"

# Canonical mark, vendored — never redrawn here.
MARK = mark_body()

CX, CY = 700, 300          # radar / mark centre
MARK_SIZE = 260

def grid() -> str:
    ls = []
    for x in range(0, 1201, 60):
        ls.append(f'<line x1="{x}" y1="0" x2="{x}" y2="630"/>')
    for y in range(0, 631, 60):
        ls.append(f'<line x1="0" y1="{y}" x2="1200" y2="{y}"/>')
    return (f'<g stroke="{BORDER}" stroke-width="1" opacity="0.55">' + "".join(ls) + "</g>")

def rings() -> str:
    out = []
    for r, o in ((90, 0.30), (150, 0.22), (215, 0.15), (285, 0.10), (360, 0.06)):
        out.append(f'<circle cx="{CX}" cy="{CY}" r="{r}" fill="none" '
                   f'stroke="{MONO}" stroke-width="1.5" opacity="{o}"/>')
    return "".join(out)

# Peripheral swarm nodes: (x, y, label, colour, radius)
NODES = [
    (300, 118, "DRN-04 ACTIVE", BLUE, 7),
    (1045, 96, "DRN-07", BLUE, 7),
    (232, 372, None, BLUE, 5),
    (1086, 402, "DRN-09", BLUE, 6),
    (500, 92, None, AMBER, 5),
    (905, 482, None, RED, 5),
]

def mesh() -> str:
    out = []
    for x, y, label, colour, r in NODES:
        out.append(f'<line x1="{CX}" y1="{CY}" x2="{x}" y2="{y}" stroke="{MONO}" '
                   f'stroke-width="1.2" stroke-dasharray="5 7" opacity="0.28"/>')
        out.append(f'<circle cx="{x}" cy="{y}" r="{r + 6}" fill="none" stroke="{colour}" '
                   f'stroke-width="1.2" opacity="0.35"/>')
        out.append(f'<circle cx="{x}" cy="{y}" r="{r}" fill="{colour}"/>')
        if label:
            out.append(f'<text x="{x + 20}" y="{y + 5}" font-family="{MONOF}" font-size="15" '
                       f'letter-spacing="1.2" fill="{MONO}">{label}</text>')
    return "".join(out)

def chip(x: int, label: str, colour: str) -> tuple[str, int]:
    w = 20 + int(len(label) * 9.2)
    svg = (f'<g><rect x="{x}" y="556" width="{w}" height="34" rx="6" fill="{CARD}" '
           f'stroke="{colour}" stroke-opacity="0.55"/>'
           f'<text x="{x + w / 2:.0f}" y="578" font-family="{MONOF}" font-size="14" '
           f'letter-spacing="1.4" fill="{colour}" text-anchor="middle">{label}</text></g>')
    return svg, w

def chips() -> str:
    out, x = [], 1140
    for label, colour in (("ZONE ALPHA", AMBER), ("MESH OK", BLUE), ("12 ONLINE", GREEN)):
        w = 20 + int(len(label) * 9.2)
        x -= w
        svg, _ = chip(x, label, colour)
        out.append(svg)
        x -= 14
    return "".join(out)

off = (1 - MARK_SIZE / 512) / 2
svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630" viewBox="0 0 1200 630">
  <rect width="1200" height="630" fill="{BG}"/>
  {grid()}
  {rings()}
  {mesh()}
  <g transform="translate({CX - MARK_SIZE / 2},{CY - MARK_SIZE / 2}) scale({MARK_SIZE / 512})">
    {MARK}
  </g>

  <text x="60" y="58" font-family="{MONOF}" font-size="15" letter-spacing="2.4" fill="{MUTED}">VIZ.RESQ.SOFTWARE</text>

  <text x="60" y="502" font-family="{SANS}" font-size="76" font-weight="bold" fill="{FG}">ResQ<tspan fill="{BLUE}" dx="20">Viz</tspan></text>
  <text x="62" y="540" font-family="{MONOF}" font-size="16" letter-spacing="3.2" fill="{MUTED}">REAL-TIME SWARM VISUALIZATION</text>

  {chips()}
</svg>
'''

tmp = HERE / "_og-banner.svg"
tmp.write_text(svg)
subprocess.run(["rsvg-convert", "-w", "1200", "-h", "630", str(tmp), "-o", str(OUT)], check=True)
# Strip embedded timestamps so regenerating an unchanged banner is a no-op in git.
subprocess.run(["magick", str(OUT), "-strip", str(OUT)], check=True)
tmp.unlink()
print(f"wrote {OUT.resolve()}")
