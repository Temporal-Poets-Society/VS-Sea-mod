# Caravel dry-clip fixture

`caravel-dry-clip.json` is a frozen **Sea WaterClipBox**, not a game asset. It was generated offline from Hansa's
`ShipDefinition.Init` and `ShipWater.Clip` for `caravel`, level at the origin, dry, calm zone 1.5. It stores the
16 footprint widths and fitted 4 × 32 dry widths so GPU clip regressions can run without Hansa installed.
Source asset revision and reproduction are recorded in `../../docs/hold-water.md` (SEA-D47).

The fixture checks Sea's CPU/GPU identity for the actual supplied bounds. It does not establish the water state
of a live ship or justify hiding Hansa's independently rendered floodwater.
