# Settings

Sea has one world option and a config file on the server. Players get the server's settings when they join; each
player can also try the look settings on their own game with the `.sea` commands.

## World option (hidden from the creation screen)

Since 0.6.1 Sea has nothing on the world-creation screen. Its one world option is set by an admin with the game's
`/worldconfig` command:

| Key | Values | Default in new worlds | What it does |
| --- | --- | --- | --- |
| `seaStormHeight` | `json`, `1`, `1.5`, `2.2` | `json` | How high storm seas get. `json` uses `StormHeightScale` from the server's `sea.json` (1 by default). `1` is Sea's normal storm seas, `1.5` higher, `2.2` about 12-block waves at hurricane strength. The waves get longer with their height, and breezes and chop stay the same. |

Example: `/worldconfig seaStormHeight 1.5`, then restart the world (single player) or the server. Worlds made with Sea
0.4.7 to 0.6.0 keep the height they saved (up to 0.5.x the default was `1`); such a world ignores `StormHeightScale`
until an admin runs `/worldconfig seaStormHeight json`. Higher storm seas than Sea's normal haven't been played much with boats and
ships.

## Config file: `VintagestoryData/ModConfig/sea.json`

Written with its defaults the first time the server starts with Sea. Edit it while the server is stopped. A number that
is negative, not a number or infinite goes back to its default, a number above its range is set to the top of the
range, and `SeaWindFrom` is taken round the compass; a file that can't be read is left as it is and the
defaults are used.

| Setting | Default | Range | What it does |
| --- | --- | --- | --- |
| `WaveScale` | `1` | 0 to 10 | Multiplies all wave heights, the chop too. |
| `StormHeightScale` | `1` | 0 to 4 | Storm seas' height (and length), used by worlds whose `seaStormHeight` is `json`. Blended in between wind strengths 0.4 and 0.8; breezes stay the same. |
| `SeaWindFrom` | `270` | any compass bearing | Without a weather mod: where the steady wind the sea follows blows from, in degrees (270 = west). The waves only, not sailing. |
| `SeaWindStrength` | `0.3` | 0 to 1.5 | Without a weather mod: that wind's strength (0 = a calm sea). The waves only, not sailing. |
| `Coasts` | `false` | | Waves feel the sea floor near coasts: they turn, steepen, break and drive currents. Off by default since 0.6.2 until the coasts are reworked; set `true` to try them. See [Coasts](Coasts). |
| `CelestialReflections` | `1` | 0 to 2 | Sun glitter and moon trail; 0 gives vanilla's sun highlight. |
| `SkyReflections` | `1` | 0 to 2 | The sky reflected in the water; 1 is physically right, 0 off. |
| `NightVisibility` | `1` | 0 to 2 | How much you see outdoors at night and in twilight; 0 keeps the game's own night. |

Settings from Sea 0.3 (`WindModel`, `StormFrequency` and the others), the old wake settings and `Surf` (0.6.0 and
0.6.1; replaced by `Coasts`) are no longer used.
