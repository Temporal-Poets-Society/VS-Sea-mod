# Caravel dry-clip fixture

`caravel-dry-clip.json` is a frozen **Sea WaterClipBox**, not a game asset. It was generated offline from Hansa's
`ShipDefinition.Init` and `ShipWater.Clip` for `caravel`, level at the origin, dry, calm zone 1.5. It stores the
16 footprint widths and fitted 4 × 32 dry widths so GPU clip regressions can run without Hansa installed.
Source asset revision and reproduction are recorded in `../../docs/hold-water.md` (SEA-D47).

The fixture checks Sea's CPU/GPU identity for the actual supplied bounds. It does not establish the water state
of a live ship or justify hiding Hansa's independently rendered floodwater.

## Native small boats — pre.14

`rowboat-dry-clip.json` and `sailboat-dry-clip.json` freeze the fitted production 4 × 32 profiles for GPU comparisons.
Their source assets retain SHA-256 model provenance. The rowboat geometric audit is `overview/verification/rowboat-hull-fit.json`;
its 328 native renderer boundary checks are separate from GPU/CPU identity checks. The sailboat uses the native game
shape; only sail-trim JSON patches alter the rig at runtime. Neither fixture is a loadable replacement model.
