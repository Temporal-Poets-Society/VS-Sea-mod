# Commands

Commands with a slash (`/sea`) run on the server; commands with a dot (`.sea`) run on your own game.

## Server commands

| Command | Who | What it does |
| --- | --- | --- |
| `/sea` | every player | The wind and waves where you stand, your latitude, and where the wind comes from (a weather mod, an admin's fixed wind, or the config's steady wind). |
| `/sea setwind <from> <strength> [hours] [built]` | admins (`controlserver`) | Fix the wind everywhere for the sea and boats. `from` is a compass point (`N`, `NE`, `SSW`…) or degrees; `strength` 0 to 1.5 (0.3 medium breeze, 1 storm). `hours` is how many game hours it lasts (0 or left out: until `/sea clearwind`). `built` puts the sea fully up at once; without it the sea builds over about 3 game hours. Saved with the world. Works from the server console too. The weather's own effects (rain, trees, sounds) stay the weather mod's. |
| `/sea clearwind` | admins | Let the weather decide the wind again. The sea the fixed wind raised dies down over the next game hours. |
| `/sea probe [x] [z] [t]` | every player | A check: the wave height, shelter and a test hull's pose at a place and wave time, as one line. `.sea probe` with the same numbers must print the same line. |

Examples:

```
/sea setwind E 1.5 0 built
/sea setwind SW 0.6 12
/sea clearwind
```

## Your own game

| Command | What it does |
| --- | --- |
| `.sea` | What Sea is doing on your game: the wind it sees, the sea state and the waves drawn, the wave clock against the server's, whether the water code went in, how many hulls are kept dry. |
| `.sea coast` | The coast where you stand: depth, surf, current, rips. |
| `.sea night <0-2>` / `.sea sky <0-2>` / `.sea glint <0-2>` | Try a night visibility, sky reflection or sun/moon glitter strength on your game until you leave the world. Without a value: show it; `default`: back to the server's. |
| `.sea reset` | Night, sky and glitter back to the server's settings, and Sea's foam shown again. |
| `.sea foam all\|off` | Show or hide Sea's foam on your game: whitecaps, surf whitewater and spray. |
| `.sea probe [x] [z] [t]` | The same check as `/sea probe`, worked out on your game. |
| `.sea drawn` | A check: how far the drawn water is from the exact wave height up to 512 blocks around you. |
| `.sea cost` | A check: start measuring what Sea's water drawing costs a frame; again to stop and see it. |
| `.sea uploaderror` | A check: makes Sea's next water upload fail, to show the fallback (vanilla waves, no dry hulls, no foam) until you leave the world. |
| `.sea sheyderwater on\|off` | With SheyderMod installed: show its own water reflection for comparison, until you leave the world. |
