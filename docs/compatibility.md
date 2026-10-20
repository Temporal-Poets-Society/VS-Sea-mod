# Compatibility: vanilla, Earth and other world-generation mods

Status: analysis as of 0.3.6 (16 October 2026).
Celestial update, 17 October (0.3.8-pre.1): vanilla sun/moon/stars require no optional mod. The exact installed
AstraTerra 0.15.0 DLL was inspected and its optional sky adapter exercised offline. Wmap 0.4.1's optional distant
lighting patch compiles on the GPU; reflections fade before its near edge. **Graphical-client combinations remain
unverified**. See [source audit, limits and comparison matrix](sky-water.md) and [verification record](verification-celestial.md).
- **Vanilla:** verified by `tools/run_selftest.sh` (a vanilla world, seed 4242, no Earth, no Cylinder World).
- **Terra Prety:** read from its mod page and one source file in its public repository (`TerraPrety/src/Patch.cs`, which
  may not match the 7.11 release). It has not been run with Sea.
- **Watersheds 7.0.1** (Terra Prety's required companion): decompiled from a copy downloaded from a multiplayer server. It has not been run with Sea either.

Cylinder World compatibility is tracked in Cylinder World's own
[terrain-mod-compatibility.md](../../Cworld/docs/terrain-mod-compatibility.md).

## What Sea takes from the world

Sea doesn't generate terrain. Everything it needs is one of these:

| Input | Read from | Used for | If it's missing or different |
|---|---|---|---|
| Wind | A wind provider (`SetWindProvider`, normally Earth), since 0.4.0 | The sea state, the chop, ships' wind (`WindAt`), Wmap's wind layers | Without one: vanilla's wind for `WindAt`, and the sea follows the config's steady wind (SEA-D30) |
| Latitude | `Calendar.OnGetLatitude(z)`, read live | Shown by `/sea` only (0.4.0; the belts and storms that used it moved to Earth) | The calendar's default is 0.5: 45°N everywhere |
| Sea level | `World.SeaLevel` | Vanilla's wind at sea level for `WindAt` without a provider | Any value works. Terra Prety's taller worlds just move it |
| Oceanity | The region's `OceanMap`, read exactly as the chunk tesselator packs it into water vertices | The sheltered-water factor: waves × 1 on the open sea down to × 0.2 on lakes, the same for ships and the drawn water | No map, or one not loaded: 0.2 everywhere, the same as the vertices get |
| Region size | `BlockAccessor.RegionSize` | The ocean map lookup | — |
| The ring | Cylinder World's `Params` by reflection | The waves repeat around the ring (the provider makes its wind seamless there) | Without it the world doesn't wrap. A read failure is logged |
| Game internals | Harmony patches (listed by `.sea`) | Underwater effects, particles in dry places, the water shaders (0.4.0: no weather patches) | A patch that fails only switches off its own feature (SEA-D14) |

The sheltered-water factor follows `OceanMap`, not the actual coastline (SEA-D09). That is deliberate: the game itself
draws the waves from the same map, so ships and the drawn water always agree, even when the map and the terrain don't.

## Without Earth (vanilla terrain)

Sea doesn't depend on Earth, and doesn't reference it. Since 0.4.0 Earth (by reflection, BCL types only) registers
itself as Sea's wind provider when both are installed; without Earth Sea takes vanilla's wind. In a vanilla world:

| Feature | Without Earth | Notes |
|---|---|---|
| Wind | Vanilla's (strength only, no direction); the sea follows the config's steady wind | Self-test: `WindAt` is vanilla's, Sea patches none of vanilla's weather, the sea state is the steady wind's. Vanilla's weather stays vanilla's (no storms, no directional rain or clouds) |
| Sea state, waves, the wave clock | Works | Self-test: the waves are the same for every caller |
| Latitudes | Vanilla's realistic climate: a triangle wave in z between the poles | Only shown by `/sea` |
| Sheltered water | Vanilla `OceanMap` | Self-test: 0.20 at the inland spawn of seed 4242 |
| Ship services (dry hulls, clips, dry spaces) | Works | Terrain-independent |
| Real monthly winds, storms | Not available | Earth's, through `SetWindProvider` (any mod can provide; one at a time) |

## Terra Prety (+ Watersheds)

### What they change, as far as Sea is concerned

- **Ocean map.** Terra Prety replaces the ocean layer through `GenMaps.GetOceanMapGen` (its own `MapLayerOceansSmooth`).
  The region's `OceanMap` then holds its Voronoi continents. Landcover settings are ignored.
- **Map and terrain generators.** Watersheds replaces vanilla's `GenMaps` and `GenTerra`. Harmony prefixes skip
  vanilla's `StartServerSide` and `ShouldLoad`, and `WatershedsGenMaps` takes their place.
  - It still fills `OceanMap` from the ocean layer (with 5 cells of padding). Sea reads the map by its inner size, so
    padding doesn't matter.
  - It still sets `Calendar.OnGetLatitude` on the server and, through the `latitudedata` channel, on clients.
  - It feeds `OceanMap` into Terra Prety's coast map. Coasts and the ocean map therefore come from the same source.
- **Heights.** World height 448 is recommended, and mountain ranges reach 64 blocks higher. Sea level follows the
  world height. Terra Prety's `GenBlockLayers` change removes vanilla's sea-level-rise term from the block layers, not
  from `World.SeaLevel`.
- **Rivers and lakes.** Watersheds carves rivers about 9 blocks below sea level and adds lakes. Inland, the ocean map is
  low, so their waves are a fifth of the open sea's, like vanilla lakes. Sea doesn't tell salt water from fresh.
- **Not touched** (Terra Prety's `Patch.cs`): `GenTerra` directly, latitude, climate, the water shaders, wind and weather.

### Expected result

**Compatible, no code change expected.** Sea gets latitudes, a sea level and an ocean map that follows Terra Prety's
continents. Ships and the drawn water agree by construction (SEA-D09).

Open points, in order of risk:

1. **Coast fidelity.** If Terra Prety's terrain puts land where its ocean map says ocean (or the reverse), coastal water
   gets the wrong share of the waves. It would still be the same for ships and the drawn water, and vanilla would show
   the same mismatch.
2. **The client's latitudes arrive by packet** (Watersheds' channel, like vanilla's). Since 0.4.0 Sea's wind doesn't
   use latitudes (SEA-D19 is superseded); a provider using them must handle a late arrival itself.
3. **River water.** If Watersheds' rivers are "animated" water at the river mouth, they get Sea's waves at the
   oceanity there. Flowing water keeps vanilla's look. Untested.
4. **Harmony overlap.** Watersheds patches block placement, ambient water sounds and worldgen. Sea patches none of
   those. No overlapping targets are known.

### What to verify (SEA-P03)

No code change. A short session with Terra Prety 7.11.x, Watersheds 7.x and its recommended world settings
(height 448, upheaval 0 %, landform scale 300 %):

1. The server log has `Sea ready; wind from …` and no `[sea]` warnings. `.sea` lists every patch as active.
2. `/sea` far out at sea, on a coast and on an inland lake. The latitude should change with z as on the world map.
   The sheltered factor (`.sea`, "sheltered-water factor") should be ~1 offshore and 0.2 inland.
3. Waves visible on the open sea, smaller on lakes and rivers, with no seams along chunk or region borders.
4. With a ship mod: the ship rides in step with the drawn water near a Terra Prety coast.

## Other mods

| Kind of mod | Expected result |
|---|---|
| Landform or terrain packs that keep vanilla's `GenMaps` / `GenTerra` | Compatible. Sea reads the same maps |
| Generators replacing the ocean layer (Terra Prety, Continental-style) | Compatible. The sheltered factor follows their ocean map |
| A generator that leaves `OceanMap` empty or meaningless | The sheltered factor is wrong (0.2 everywhere if empty), the same for ships and the drawn water |
| A climate mod that sets `OnGetLatitude` | Followed, live, on both sides, as long as both sides set the same function |
| Superflat or a mod's own world type without `GenMaps` | 45°N everywhere, no ocean map (waves × 0.2) |
| Weather or wind mods | Since 0.4.0 Sea patches none of vanilla's weather. A mod that wants the sea to follow its wind registers as the wind provider (`SetWindProvider`; refused while another one holds it) |
| Shader packs or mods editing `chunkliquid` / `chunkliquiddepth` | If they change the lines Sea anchors on, Sea falls back (log warning, `.sea` says "MISSING"): vanilla waves, and no dry hulls (water shows in ships). SEA-P05 |
