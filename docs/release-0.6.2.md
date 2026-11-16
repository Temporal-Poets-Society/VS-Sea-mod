# Sea 0.6.2 (16 November 2026)

0.6.1 with the coasts off by default and swash off, both until they are reworked.

- **Coasts off by default** (SEA-D82, backlog SE-0077). The author saw borders in the waves by an islet, where they
  turn toward the shore, and chose the whole coast off for the release ("Yes, coasts off"). The config's `Surf`
  (on) is replaced by `Coasts` (off), so servers whose `sea.json` 0.6.0 or 0.6.1 wrote with `"Surf": true` go off
  too. With `"Coasts": true` the coast comes back as in 0.6.1, without swash.
- **Swash off** (SEA-D81, backlog SE-0076). A player's video showed pale, shimmering squares on grass at the water's
  edge (western Norway), and the author saw a patchwork of pale and dark squares on a sand beach in the Cyclades; the
  author: "Turn off swash for the release". The client no longer creates or registers `SwashRenderer`.

`.sea coast` says whether the coasts are on and that swash is off.

## The file

`release/sea-0.6.2.zip`, 208,192 bytes, SHA-256 `0c404df9301ab5ef1422baf436c8a595a24104f48410a3caeaaacb831433bbd6`,
built in the main checkout from `d333fde` (0 warnings). (A first 0.6.2 build with swash off alone, from `e5849e2`,
208,180 bytes, SHA-256 `77be45a7…`, went into the author's game for a look; it was never published.)

## Verification

- `dotnet run --project tests\SeaTests`: ALL OK.
- `dotnet build Sea.csproj -c Release`: 0 warnings, 0 errors.
- Nothing outside Sea reads `SeaSystem.Swash` or `SeaConfig.Surf` (searched World Map, Earth, Ships, Sea Monsters,
  Cylinder World and Magelan).
- In game: the author, with this build installed, 16 November: "Coast looks ok now".

Not tagged, pushed or uploaded.
