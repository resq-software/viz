# Heightmap import

Drop a grayscale PNG/JPG here, then pass it via URL param to render real-world
terrain without touching the procedural presets:

```
https://localhost:5001/?heightmap=/heightmaps/mount-washington.png
```

## Recommended sources

| Source | Output | Licence | Best for |
|---|---|---|---|
| [Tangram Heightmapper](https://tangrams.github.io/heightmapper/) | PNG, any bbox | CC-BY | Fastest — pan + "Export PNG". Mapzen DEM @ 1 arc-sec |
| [USGS 3DEP Downloader](https://apps.nationalmap.gov/downloader/) | GeoTIFF → PNG (QGIS) | Public domain | US terrain, 1/3 arc-sec precision |
| [OpenTopography](https://portal.opentopography.org/raster?opentopoID=OTSRTM.082015.4326.1) | GeoTIFF → PNG | SRTM CC0 | Global, 30 m resolution |
| [Sonny's LiDAR](https://sonny.4lima.de/) | PNG directly | CC-BY-4.0 | Europe, 1 m resolution |

## URL params

- `heightmap=<path>` — required. Path under `/` (e.g. `/heightmaps/alps.png`)
- `heightScale=<m>` — pixel 255 → this many metres. Default `400`.
- `worldSize=<m>` — width/height the image covers. Default `4000`.
- `baseOffset=<m>` — sea-level bias added to every sample. Default `0`.
- `encoding=<gray8|rg16>` — how elevation is packed. Default `gray8`.

## Encoding, and why 8-bit is not enough for a baked tile

`gray8` is what a hand-made grayscale PNG uses: the red channel carries 0..255, so
there are **256 elevation levels across the whole of `heightScale`**. That quantum
is `heightScale / 255` — 1.57 m at the default 400 m scale.

That is not merely a loss of detail. At the bake grid's spacing (2048 samples
across 4000 m, so 1.95 m per cell) the quantum lands as a *slope* between adjacent
cells:

| `heightScale` | quantum | apparent slope across one cell |
|---|---|---|
| 400 m | 1.569 m | 38.8° |
| 800 m | 3.137 m | 58.1° |
| 1500 m | 5.882 m | 71.6° |

A wheeled or tracked ground vehicle tops out near 30°, so on any real slope every
quantisation boundary becomes a false cliff the mobility model reads as
impassable — terrain grows obstacles that are not there. Below the quantum,
features vanish: a 0.5 m levee crest rounds to nothing, while 1 m and 2 m steps
both collapse onto the same 1.57 m level.

`rg16` packs a 16-bit value big-endian across two channels — **red is the high
byte, green the low byte** — for 65536 levels, or 6.1 mm at a 400 m scale. Use it
for anything derived from a real DEM.

> **Why split the value rather than ship a 16-bit PNG?** The loader decodes
> through a 2D canvas, and `getImageData` is 8-bit per channel by specification.
> A genuinely 16-bit PNG is silently truncated on the way through. Splitting
> across two 8-bit channels is what survives that decode.

`gray8` stays the default so the hand-made files described above keep meaning what
they did.

Example with a deep-valley DEM:

```
?heightmap=/heightmaps/grand-canyon.png&heightScale=800&baseOffset=200
```

## Notes

- Image should be grayscale (R=G=B). The red channel is sampled.
- 512² — 2048² is the sweet spot. Larger images waste memory without adding
  detail past `TERRAIN_SEGS = 320`.
- The biome textures (grass/rock/snow/sand) still track the active preset
  (`Shift+1..5`), so a DEM of the Alps will read as "alpine" tiers by default.
- Backend physics **does** consume an uploaded DEM: the client POSTs the decoded
  grid to `/api/sim/heightmap` and the server installs it as authoritative
  terrain, so drone contact tracks the DEM rather than the procedural heightFn.
- That upload is opt-in and best-effort, though, so the two can still diverge.
  It fires only when the page is opened with `?heightmap=<url>`, the decode
  succeeds, the session is ready, and the operator mutation gate permits
  `environment.heightmap`. A failed upload warns and continues — it is not
  retried — leaving the client on the DEM and the server on procedural terrain.
