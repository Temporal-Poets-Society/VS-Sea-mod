# Compatibility

## Other mods of this suite

| Mod | What they do together |
| --- | --- |
| **Earth** | Earth is the wind for the sea: its real prevailing winds by month, monsoons, travelling storms and hurricanes drive the waves and the boats. Earth works without Sea, and Sea without Earth. |
| **Cylinder World** | The waves repeat exactly around the ring, so the water is the same on both sides of the seam. |
| **World Map** | World Map 0.5 or later draws the far sea, and Sea sets its waves and sky reflections there. World Map also shows Sea's wind. World Map 0.4 is no longer supported for the far sea. |
| **Ships** (and other ship mods) | Ships ride Sea's waves, and Sea keeps the water out of their hulls. |

## Other mods

| Mod | Result |
| --- | --- |
| **AstraTerra** (and its Earth version) | The moon's trail and the sun's colour on the water follow its sky. |
| **SheyderMod** | Works. Sea's water code goes into its water shader, and Sea keeps the water's reflections: SheyderMod's own water reflection is skipped while Sea draws the water. Its other features stay on. |
| **Rough Seas** | A separate mod with its own waves. Running both together hasn't been tested; both draw the sea's waves. Sea publishes its wind, waves and hulls in an open form that any mod can read without depending on Sea; Rough Seas doesn't use it so far. |
| **LOD mods** (Doden's Landscapes, Vintage Horizons, Farseer, ChunkLOD) | Not supported with the suite's worlds; World Map is the far terrain. Sea's glitter and foam fade by your own view distance setting. |
| **Terrain mods** that keep the game's ocean map | Work: lakes and the open sea are told apart from the same map. A world type without an ocean map (superflat) keeps vanilla's ripples, while boats there still rock on small waves that aren't drawn (a known mismatch). |
| **Wind direction mods** (Pelagus Winds, Wind Vanes & Randomized Winds, Simple Wind Direction) | Since 0.6.3, without Earth: ships and the vanilla sailboat sail by the direction they give the wind. The waves keep the steady wind from `sea.json`. Not tested in game with these mods. To sail without the wind, `SailboatsByWind` false in `sea.json` (vanilla sailboats) and `SailByWind` false in `ships.json` (Ships). |
| **Other weather or wind mods** | Sea changes none of the game's weather. A mod that wants the sea to follow its wind either publishes the open weather role or registers as Sea's wind provider. |
| **Shader packs** that change the water shader's lines | Sea may not get its code in: then vanilla waves and no dry hulls, with a warning in the log and in `.sea`. |

## Multiplayer

The server decides everything that matters (boats' height and speed, the wind). Every player sees the same waves,
because they are worked out from the game time and a small packet sent at joining. Every player needs the same Sea
version as the server. The coast and storm features were checked in game with one client.

## Saves

- Sea saves only an admin's fixed wind, the world's wave key and water in boats. Adding Sea to a world, or removing it,
  doesn't change the terrain.
- Removing Sea gives vanilla water and boats back; a flooded boat floats again as in vanilla.
- Worlds made with older Sea versions keep their storm wave height choice.
