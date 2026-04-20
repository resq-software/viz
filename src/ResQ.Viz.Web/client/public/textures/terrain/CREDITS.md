# Terrain texture credits

All textures here are CC0 (public domain) from [ambientCG.com](https://ambientCG.com),
by Lennart Demes. No attribution is required, but we include it as a courtesy.

| Tier | Source                                                           | Use                 |
|------|------------------------------------------------------------------|---------------------|
| grass | [Ground037](https://ambientCG.com/view?id=Ground037)           | Low-elevation base  |
| rock  | [Rock030](https://ambientCG.com/view?id=Rock030)               | Mid-slope + peaks   |
| snow  | [Snow006](https://ambientCG.com/view?id=Snow006)               | Alpine high band    |
| sand  | [Ground054](https://ambientCG.com/view?id=Ground054)           | Dunes, coastal base |

## Water normals (`../waternormals.jpg`)

From Three.js's canonical examples — [mrdoob/three.js/examples/textures/waternormals.jpg](https://github.com/mrdoob/three.js/blob/master/examples/textures/waternormals.jpg).
MIT License (bundled with Three.js distribution). 1024² tangent-space normal
map used by the `Water` addon's animated reflection surface.

Each tier ships three 1K JPGs:

- `albedo.jpg` — diffuse / base color (sRGB)
- `normal.jpg` — tangent-space normal map (**OpenGL convention**, green up)
- `roughness.jpg` — per-texel roughness (0 = mirror, 1 = matte)

PR 5 of the visual roadmap will recompress these to KTX2 / BasisU — the
raw JPGs are ~17 MB total, KTX2 will drop that to ~3 MB with better
normal-map fidelity. Do not swap normal conventions (GL vs DX) without
updating the shader sampling sign.
