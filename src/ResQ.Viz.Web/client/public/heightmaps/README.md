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
