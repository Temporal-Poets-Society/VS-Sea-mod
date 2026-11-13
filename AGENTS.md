# Sea (`sea`)

Waves that travel with the wind and are the same on the server and every client, drawn by Sea's own water-shader
code; ships and vanilla boats ride them, ship hulls stay dry inside, and coasts get surf, lee and currents. Suite
rules: `C:\VS mods\AGENTS.md`. Current state: the card `C:\VS mods\overview\mods\sea.md`. Open issues:
`tools\Update-Backlog.ps1 -Mod sea` from `C:\VS mods` (prefix SE). Decisions: `docs\DECISIONS.md` (SEA-Dnn); the
contracts: `docs\open-sea-contracts.md`; engine facts: `docs\engine-notes.md`.

## Repository
- Trunk `main`, tracking the private `origin` (`andrisberzins-1/VS-Sea-mod`): all work, branches and the release
  tags (`v0.5.1`, `v0.5.2`); old lines are `archive/*` tags.
- Public `tps` (`Temporal-Poets-Society/VS-Sea-mod`, with a wiki) gets semi-clean release versions only: `main`
  only, no tags, and it is behind on purpose. Push there only on the author's word.

## Build
- `dotnet build Sea.csproj -c Release` gives `release\sea-<version>.zip`. It refuses to overwrite an existing zip:
  bump the version, or pass an explicit `-p:PackageFile=<path>`. Game path: `-p:VintageStoryPath`,
  `VINTAGE_STORY` or `%APPDATA%\Vintagestory` (also OpenTK from its `Lib`).
- Ships builds its bridge against `..\Sea\bin\<Configuration>\mod\Sea.dll`, and Magelan builds `Sea.csproj`
  itself: a change to Sea's public types can break both.

## Test
- `dotnet run --project tests\SeaTests` (offline: waves, wind, sea state, shader injection, dry hulls, configs).
- `dotnet run --project tests\ShaderCheck`: compiles the water shaders on this GPU and compares the dry cut
  pixel by pixel; needs a desktop session. Run it after any shader change (the offline tests only see the text).
  `-- --vertex-cost` adds GPU timings of the water's vertex stage (SEA-D74); in game `.sea cost` times Sea's Use
  postfixes.
- `dotnet run --project tests\SkyIntegrationCheck -- --vanilla`, and without `--vanilla` against the installed
  AstraTerra DLL. `tests\ShipClipCheck` needs `HANSA_ROOT` set to a Ships checkout.
- Server: `tools/run_selftest.sh [--with-cworld]` in Git Bash after a Release build (data in `testserver\`); exits
  1 unless `SELFTEST ALL OK`. Template: `..\Earth\testserver\serverconfig.json` (`TEMPLATE_CONFIG` to override).
  `--with-cworld` copies `..\Cworld\release\cylinderworld-<its version>.zip`, which must be built first.
- Boats: build `tests\BoatProbe`, then `tools\Test-Boats.ps1 [-WithShips -ShipsZip <zip>]` (runs in `.local\`).
- In game: the AgentBridge bench; `/sea probe` (server) and `.sea probe` (client) must print the same line, and
  `.sea drawn` measures the drawn surface. For the weather role, an Earth world (Earth, Cylinder World, Hydrology).

## Release
- Bump `version` in `modinfo.json`; add the version paragraph at the top of `README.md`; keep the verification
  in `docs\` (`release-<version>.md`). Build, commit, tag `vX.Y.Z` on `main`. The last Mod DB upload set (changelog,
  description) is in `C:\VS mods\overview\archive\release-2026-11-01\sea\`.
- A release may then go to `tps/main`, on the author's word.

## Gotchas
- Quit the game completely before swapping the zip: a world reloaded from the main menu keeps the old `Sea.dll`
  ("Assembly with same name is already loaded").
- Never unsubscribe `GameWorldSave` in `Dispose`: the 1.22 server disposes mods before its shutdown save (a cleared
  storm's sea was lost that way, SE-0018).
- The waves must stay the same on the server and every client (server wave clock, SEA-D06/D07); SeaTests checks
  it. Shader names use the `sea_` prefix (SEA-D57).
- The open sea contracts (`seacontracts:*`) are read by Earth, Ships and Sea Monsters, and proposed to Rough Seas
  but not agreed with hoblin: keep them compatible. Earth owns weather (D23); Sea takes its wind from a provider.
- Not checked: multiplayer, Rough Seas alongside, Terra Prety and Watersheds (SE-0029).
