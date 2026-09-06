# Copyright 2026 ResQ Systems, Inc.
# SPDX-License-Identifier: Apache-2.0
"""Shared loader for the canonical ResQ mark vector."""
import pathlib

HERE = pathlib.Path(__file__).resolve().parent


def mark_body() -> str:
    """Return the mark's drawing content, ready to embed in another SVG.

    The canonical file sets `fill="none"` on its root <svg>, which several
    ring paths rely on. Lifting the children out of that root drops the
    inherited default and SVG falls back to black — painting an opaque disc
    behind the mark. Re-establishing it on a wrapper <g> preserves the
    artwork exactly without touching the vendored file.
    """
    src = (HERE / "resq-mark-color.svg").read_text()
    body = src[src.index(">", src.index("<svg")) + 1: src.rindex("</svg>")].strip()
    return f'<g fill="none">{body}</g>'
