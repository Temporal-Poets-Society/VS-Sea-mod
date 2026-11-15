# Sea 0.6.1 (15 November 2026)

0.6.0 with **Storm wave height** taken off the world-creation screen (SEA-D80; the author's look at the creation
screen, relayed by the coordinator). New worlds still save "json" (the server's `sea.json` `StormHeightScale`), and
admins set a world's height with `/worldconfig seaStormHeight json|1|1.5|2.2` (restart to apply). Code and assets are
0.6.0's; `worldconfig.json` gains `onCustomizeScreen: false` and `modinfo.json` the version.

## The file

`release/sea-0.6.1.zip`, 208,079 bytes, SHA-256 `a6e3771057ff2ab78edc11c77523594e13b726ce33ae302d905710b8be6a4bb0`,
built in the main checkout from `097aff3` (0 warnings). Against the 0.6.0 zip: the same entries; `worldconfig.json` and
`modinfo.json` differ, and the DLL and PDB only by the rebuild (no source change since `v0.6.0`).

## Verification

- `dotnet run --project tests\SeaTests`: ALL OK, with the new check that the option is off the screen.
- Bench (AgentBridge, own data folders): the customize screen with Earth 2.0.0, Cylinder World 1.1.1, Sea 0.6.1, World
  Map 0.5.3 and Hydrology 0.6.2 has no Storm wave height on the Earth tab (top to bottom); a new plain world with
  Cylinder World 1.1.1 and Sea 0.6.1 saved `seaStormHeight` "json", and `/worldconfig seaStormHeight 1.5` set it and
  kept it over a restart.

Not tagged, pushed or uploaded.
