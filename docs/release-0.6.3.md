# Sea 0.6.3 (20 November 2026)

0.6.2 with wind direction mods reaching the sails, and a switch for the vanilla sailboat's wind sailing.

- **Wind direction mods steer the sails** (SEA-D83, backlog SE-0079). A Mod DB player on a multiplayer world without
  Earth, running Pelagus Winds, Wind Vanes and Ships, saw Sea override the wind's direction. Without a wind provider
  Sea used only X of the game's wind vector; vanilla sets only X, but those mods turn the vector. Sea now keeps X and
  Z, so Ships' ships (through Ships' bridge, `SeaSystem.WindAt`), vanilla sailboats, World Map and `/sea` get the
  direction. With the game's own wind nothing changes. The waves still follow `SeaWindFrom` and `SeaWindStrength`.
- **`SailboatsByWind`** in `sea.json` (on). Off, the vanilla sailboat's sail thrust is vanilla's whatever the wind and
  its yard isn't braced; waves, flooding and dry hulls stay. Ships' own `SailByWind` in `ships.json` does the same for
  its ships. The author: "Yes, fix both" (20 November).

`/sea` says "east unless a wind mod turns it" for the game's wind.

## The file

`release/sea-0.6.3.zip`, 209,001 bytes, SHA-256 `164b502d2a17cd639e9281f6e7bab35deccd50f2028fd5b5f58310fae3fdf0b1`,
built in the release worktree from `72af48d` (0 warnings), 9 files, `modinfo.json` version 0.6.3.

## Verification

- `dotnet run --project tests\SeaTests`: ALL OK (with the new check: `SailboatsByWind` on by default, off reaching
  clients as off, an older server's packet reading as on).
- `tools/run_selftest.sh` with this zip: SELFTEST ALL OK, with the new check: a handler of the self-test's own turns
  vanilla's wind after vanilla's, and `WindAt` gives it (0.50 from NE) on the main thread and another thread; the sea
  keeps the steady wind.
- `tools/Test-Boats.ps1 -Wait` with this zip and `SEA_SELFTEST=1`: 76 of 78 pass, the new one included ("SailboatsByWind
  off: the sail is vanilla's throttle again, into the wind too (0.1500, vanilla 0.1500)"). The two wave-clock checks
  fail on 0.6.2's `main` too (backlog SE-0080, being worked separately).
- Ships reads its wind with `sea.WindAt` (`Hansa/sea-bridge/SeaBridge.cs`), so its sails get the direction too.
- Not seen in game; not run with Pelagus Winds or Wind Vanes installed (how they set the wind is read from their Mod
  DB pages; vanilla 1.22.7's own handler sets only X).

## Published (20 November 2026, on the author's word: "Yes, publish")

- Private: tag `v0.6.3` on `b461c87`, pushed to `origin` with `main` (then at `2c79c47`, SE-0080's test fix).
- Public (`tps`): a release snapshot `a29242b` on top of `c3bcec5` (0.6.2), tagged `v0.6.3`.
- GitHub release [v0.6.3](https://github.com/Temporal-Poets-Society/VS-Sea-mod/releases/tag/v0.6.3) (Latest) with
  `sea-0.6.3.zip`; downloaded back: 209,001 bytes, the same SHA-256.
- Wiki from `v0.6.3`'s `docs/wiki` (wiki commit `8d95431`: Boats, Compatibility, FAQ, Settings, Waves and wind).
- Mod DB: not uploaded (the author's; package in `C:\VS mods\work\moddb-release-2026-11-20\sea\`).
