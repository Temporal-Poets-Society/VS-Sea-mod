# Sea — waves that follow the wind, the same sea for everyone (Vintage Story 1.22)

**0.6.0-rc.2 (release candidate, 13 November 2026, not released):** flowing water is drawn alike whichever way it
flows, so rivers no longer show colour patches by flow direction (SEA-D73; vanilla draws diagonal flow twice as
opaque); the drawn water runs one loop over the wave trains for surface and slope (SEA-D74); `.sea cost`.

**0.6.0-rc.1 (release candidate, 13 November 2026, not released):** 0.6.0-pre.1 below, plus the open findings of the
0.4.2 code review fixed (SEA-D72: client and server wave heights agree, no state mixed across threads, vanilla's wind
read on the main thread, listeners removed on dispose, calendar changes followed); Earth's weather role read through the
open sea contracts, seen in game with Earth 2.0.

**0.6.0-pre.1 (development, not released; checked in game on one client, 7 November 2026; [wiki](https://github.com/Temporal-Poets-Society/VS-Sea-mod/wiki)):**
- **Waves on beaches.** Sea reads the depth under the water near coasts from the blocks and works out how long a wave
  takes to reach the shore from each column. Over shallow water the swell turns until its crests run along the beach,
  grows as the water shallows, is held to a height the depth allows (significant height at most 0.6 × depth) and
  steepens into breakers with a peaked crest and a steep shoreward face. Breaking surf draws whitewater and throws
  spray. Boats, ships and the open contracts ride the same surface the shader draws. Config `Surf` (on).
- **The lee of land.** Islands and headlands keep waves off the water behind them, deep water too: per column the coast
  map looks upwave in 8 directions for land and lets less through the nearer it is. Swell and long waves bend in
  behind it by that share; the wind's own sea comes back with the fetch, its short waves first and its longer waves
  only further off (they need more open water to grow).
- **Waves thrown back.** A cliff, a sea wall or a steep rocky shore reflects the waves running at it (Seelig and
  Ahrens: almost all from a wall, next to nothing from a sandy beach). The reflected train meets the incoming one and
  the water before a wall stands up and down in place, with crests up to twice as high.
- **Crests that meet the shore at an angle.** The surf keeps the phase the open sea has along the shore line, so
  crests run along a beach and reach it one stretch after another instead of everywhere at once; the longshore current
  bends them by its Doppler shift.
- **Currents in the surf.** Waves breaking at an angle drive water along the beach (Komar 1979), and the water they
  pile up runs back out to sea through rip channels: fixed places along gentle sandy beaches, about 250 blocks apart
  on average. Over a rip the breakers have a gap and the outgoing current holds the waves back, so their crests bow
  seaward. Rafts, sailboats and Ships' rowboats drift with the current; ships do in Ships.
- **Swash.** Each wave runs up the beach as a thin sheet with a white front, as high as Stockdon's run-up for the
  sea reaching that stretch of beach (sheltered coves get less), timed by the surf's own phase at the nearest shore
  point, so it runs up one part of a beach after another. The sand stays dark and wet for a few seconds.
- **Steeper storms.** A gale's sea keeps the old shape (30 times as long as it is high); stronger winds make it
  steeper, down to 18 times at Beaufort 12, as a young storm sea is. Force 12 is now 5.5 blocks high and 99 long,
  every 8 s (was 165 long, 10.3 s). In a storm the whitecaps break more easily and leave a longer shoulder.
  `StormHeightScale` keeps the same steepness.
- **Spray off the open sea's crests.** From about Beaufort 5 the wind tears spray off breaking crests near you, and from
  a gale it also blows spindrift low and fast downwind. The crests are found as the water shader finds its whitecaps
  (the drawn surface's own compression, `WavePlace.CrestAt`). Only a crest or its rising front throws spray: it starts
  at the crest's tip as a thin curtain of fine droplets and travels with the crest, so it stays on the breaking
  crest. Local and cosmetic; off with `.sea foam off`. Before this only surf on beaches threw spray. In game at
  force 12: 59 fps with the spray at its limit.
- **The coast map follows the blocks.** A dug channel, a pier or a sea wall: the chunk is scanned again and the tiles
  that see it are solved again. Tiles are solved on a background thread (about 15–25 ms each offline); `.sea coast`
  shows what the map has cost.
- **Open sea contracts** ([spec](docs/open-sea-contracts.md)): Sea publishes `seacontracts:seastate`, `surface` and
  `hulls` in `api.ObjectCache` with .NET base types only, so other mods (Rough Seas' boats among them) can read the
  waves, pose a boat on them and have water cut from hulls without referencing `Sea.dll`. It follows a published
  `weather` owner. The typed API stays. Every side answers for the asked place itself, and a hull registered by record
  is cut as finely as one through the typed API. Checked against Ships' contract reader on a dedicated server: the
  same numbers as the typed API.
- **Every shader name Sea injects starts with `sea_`** (no clash with Rough Seas' `seaWaves`). Wmap 0.5's far programs
  keep the old names, which Sea still sets.

**0.5.2:** the water's moon glint and sun colour follow **Astra Terra for Earth** again. The fork's mod ID is
`astraearth`, and 0.5.1 only connected to a mod named `astraterra`, so Earth worlds got vanilla's moon on the water.
Sea now accepts either ID (same assembly and types). No other change.

**0.5.1 — development release for Earth:** ship/swimmer wakes, splash rings and Sea's bow-spray effect are
removed from this release, including their renderer, shader path, commands and world/config controls. Breaking-wave
crest foam remains. Older wake settings are ignored. The previous 0.5.0 snapshot is preserved.

Sea was developed primarily for [Earth](https://mods.vintagestory.at/earth). Following discussion with
[hoblin](https://mods.vintagestory.at/show/user/99CD92DD551C516918AE), [Rough Seas](https://mods.vintagestory.at/roughseas)
and Sea stay separate mods: Sea keeps its own wave surface and hull water cut, and instead publishes open contracts
(weather, sea state, wave surface, hull cut; [spec](docs/open-sea-contracts.md), a proposal not yet agreed with hoblin)
that his mod and others can read without referencing ours. On Earth worlds the weather is Earth's, published through
the same contracts. Sea works with or without Earth.
[Publication description](docs/moddb-description.html), [release record](docs/release-0.5.1.md).

**0.4.7-pre.15:** vanilla rafts/sailboats follow Sea without Ships. The sailboat automatically braces its yard
and cloth to the wind, with useful drive at 45° off the wind and a 40° no-go cone. Mounted underwater fog follows
the actual waves. Water overtopping the gunwale accumulates, reduces buoyancy and can sink the sailboat; sneak +
right-click with an empty bucket to bail. Ships dev.23 fits its rowing-boat water cut to the actual hull.
[Behavior and verification](docs/boat-waves.md). Graphical-client appearance and sailing balance await playtesting.

Vanilla's waves are the same random bumps everywhere. Sea gives the world waves that travel downwind, build up and
calm down with the wind of the past hours, and are the same on the server and every client, so ships can ride them.
It also owns the water shaders, so ship mods (Magelan, Hansa) ask Sea to keep waves out of their hulls instead of each
patching the shaders.

## Historical development builds

The wake features and controls in pre.9–pre.12 below were removed in 0.5.1.

Version **0.4.7-pre.12** (31 October 2026): the **Earth** world-settings tab offers storm wave height
(`seaStormHeight`: 1, 1.5 or 2.2; default 1) and ship/swimmer wakes (`seaWakes`: default on).
New worlds save these choices; worlds without either key keep the corresponding `StormHeightScale` / `WakeEnabled`
value from `ModConfig/sea.json`, including custom values. The server resolves them and sends its effective config
through the existing sync; world choices never overwrite `sea.json`. Both can be changed after creation with
`/worldconfig seaStormHeight 1.5` or `/worldconfig seaWakes false`; restart the world/server to apply.
Earth is not required, and Sea adds no play style. SEA-D51. Offline coverage added; coordinator build/tests pending.
No game client launched or installation changed.

Version **0.4.7-pre.11** (31 October 2026, issue #37): bounded Gerstner horizontal orbits sharpen wave crests and
broaden troughs. Ships invert the same surface at world X/Z; the spectrum, heights, periods and short texture
amplitudes remain unchanged. Downwind foam rows and independent surface spume are removed. Wind foam occurs only
in short patches on compressed crest tops, fading into lace behind them; sheltered water cannot trigger it.
[Geometry, physics references, CPU/GPU agreement and before/after GPU views](docs/crest-shape.md), SEA-D50.
No client launched; coast edges, ship motion and the near/far join need an in-game look.

Version **0.4.7-pre.10** (31 October 2026, issue #35; windrows superseded by pre.11): broken crest patches, thin windrows from about Beaufort 8,
and porous bubble/lace foam replace pre.9's smooth white ribbons. Windrows follow the wind's **travel** direction;
wake texture follows its own world-space trail and ages independently. `.sea` now reports the exact current local
foam wind/coverage input (Earth overrides the fallback `SeaWindStrength`); `.sea foam all|wind|wakes|off` isolates
the layers without deleting wake history. Wave/ship samples and the accepted 250,000-sample wake cap are unchanged.
[Cause, compass audit, four-strength GPU comparisons and verification limits](docs/foam-direction.md), SEA-D49.
Offline checks pass; **in-game appearance and the screenshot ribbon's exact source remain to be checked**.

Version **0.4.7-pre.9** (30 October 2026, issue #29): client-side ship/swimmer wakes, splash rings and bow spray.
Kelvin arms, bow foam, stern wash and a spreading foam trail are painted onto Sea's own displaced water, using the
wind-whitecap lighting and dry-hull cut. Ships can declare waterline dimensions with `SetWakeHull`; otherwise Sea
uses their registered hull clips or collision boxes. `WakeEnabled` and `WakeStrength` control the bounded effect;
`.sea wakes off|default|0..2` tunes it locally. Shared wave samples and ship physics are unchanged.
[Implementation, physics, limits and offline evidence](docs/wakes.md), SEA-D48; **in-game verification pending**.
Feature reference/credit: Bobrik00's [Immersive Wakes 0.9.1](https://github.com/Bobrik00/ImmersiveWakes), a port of
Goby56's Minecraft Wakes. Sea's implementation is original, under GPL-3.0; no upstream code/assets were copied.

Version **0.4.7-pre.8** (30 October 2026, storm-wave work for issue #28): storms put their energy into long waves,
with a Pierson–Moskowitz shape and peak wavelength 30 times gameplay significant height, while short-wave slope stays
near the breeze's. `StormHeightScale` defaults to 1; 2.2 gives about 12-block hurricane seas with proportionally longer
waves. The default 0.3 breeze keeps its geometry and phases. Wind foam starts faintly at force 4–5, with broken
crests, downwind streaks and surface spume dense at force 10–12; vanilla's shore/contact foam stays in place.
[Cause, physics references and before/after numbers](docs/storm-waves.md), SEA-D46. Offline build, spectrum and GPU
checks passed; **not seen in game**, not installed. Wmap receives the longer wave trains; its far sea has no wind foam yet.
Issue #30's hold water is investigated separately: a [Hansa clip-pivot correction is prepared](docs/hold-water.md)
(SEA-D47); Hansa also draws genuine storm-admitted floodwater independently of Sea. Hansa remains unchanged here.

Version **0.4.7-pre.7** (26 October 2026, `release/sea-0.4.7-pre.7.zip`, branch `fix/sunset-glint-flicker-2026-10-26`
from `playtest/2026-10-26`, a play-test build; not merged), after issues 14 and 15 of the pre.6 play-test. **The glitter
path reaches the horizon:** the near water's glints last until the game's water has faded (0.76 to 0.8 of the view
distance, was 0.5 to 0.74) and Wmap 0.5.0-pre.8's far sea carries the same glint on (`seaGlint` with the sun, moon, sun
colour and weather). **No flicker at sunset:** the real twilight (night.1) replaced the sun and reflect colours only where
the table read darker than the game's own; just after sunset both are one texel apart, so the colours switched each tick,
and with them the game's 0.29 brightening of the reflect colour a tenth of a degree down, which tints the fog. Now the
twilight always applies below the horizon and the reflect colour only darkens. The game's shadows, cast from whichever of
sun and moon has the larger light term with no margin, fade out while that body is under 3° and around the crossing, and
the shading direction turns over the same span. Not yet seen in game.

Version **0.4.7-pre.6** (26 October 2026, `release/sea-0.4.7-pre.6.zip`, branch `playtest/2026-10-26`, a play-test
build; not merged): pre.5 (sunset colour, sky glow, near/far seam) and 0.4.7-night.1 (night light) merged into one build,
for AstraTerra 0.15.0-dev.21 and Wmap 0.5.0-pre.7. Both are described below. Not yet seen in game.

Version **0.4.7-pre.5** (25 October 2026, `release/sea-0.4.7-pre.5.zip`, branch `playtest/sunset-seam-2026-10-25`
from pre.4, a play-test build; not merged), after issue 5 of the 25 October play-test (an orange glitter path under a pale
sun, no glow in the sky, a band where the near waves meet Wmap's far sea). While AstraTerra draws the sun the glitter
path takes AstraTerra's sun colour (`Twilight.SunColour`) instead of Sea's copy capped by the game's reflect colour; the
sky reflection draws AstraTerra's sunset glow (`SunsetScatter.Shown`, dev.20) and the game's flat fog and eye height as
the dome has them; and the near water fades each wave train shorter than about four pixels by Wmap's far-sea rule, the
lost slope widening the glint. Wmap 0.5.0-pre.6 takes `seaWaveFactor` (the open sea's divisor; it took still water's,
a fifth of the slope) and `seaSunset`. [Sky light model](docs/sky-light-model.md). Not yet seen in game.

Version **0.4.7-night.1** (25 October 2026, `release/sea-0.4.7-night.1.zip`, branch `night-light-2026-10-25` from
0.4.7-pre.4, not merged or installed): night light recalibrated after the user's issue #7 ([night light](docs/night-light.md)).
The client's rendered day's light gets real twilight (dark by the sun's 12–18° depression instead of vanilla's 46°), its
moon term is cut by weather, and the night floor is 0.10 moonless / 0.40 full moon (was 0.15 / 0.45), with the moon by
phase and height and thick fog counted. Ground, sky, clouds and Wmap's far terrain read the same number. The server,
spawning and gameplay light are untouched. Not yet seen in game.

Version **0.4.7-pre.4** (25 October 2026, `release/sea-0.4.7-pre.4.zip`, branch `playtest/sunrise-2026-10-24`, a
play-test build; not merged): after pre.3's sunset play-test, the glitter path stays full while the sun's centre is up and
goes only when its top has set (pre.3 faded it from 2 degrees up), and it reddens with the sun: white above 25 degrees,
yellow, then orange on the horizon (`NightSkyMath.SunThroughAir`, the same table as AstraTerra's drawn sun), where the
game's reflect colour stays pale yellow. Not yet confirmed in game.

Version **0.4.7-pre.3** (24 October 2026, `release/sea-0.4.7-pre.3.zip`, branch `playtest/sunrise-2026-10-24`, a
play-test build; not merged): 0.4.7-pre.2 with a sunrise and sunset glitter path. The sun's glint used to fade out from 6
degrees altitude (and needed its reflected ray 4.6 degrees up for full strength), so a low sun, whose path on the sea is
longest and brightest, had none; it now fades only as the sun itself sets. Not yet confirmed in game.

Version **0.4.7-pre.2** (24 October 2026, `release/sea-0.4.7-pre.2.zip`, branch `playtest/2026-10-24`, a play-test
build; not merged): 0.4.7-pre.1 with the review fixes of `claude/review-fixes-sea` merged in. After an upload error
Wmap's far waves are switched off too, as the near water's are. Not yet confirmed in game.

Version **0.4.7-pre.1** (23 October 2026, `release/sea-0.4.7-pre.1.zip`, branch `wmap-far-waves`, a pre-release for
testing with Wmap's 0.5.0-pre.2 mid-distance view; not merged): Wmap's distant sea continues Sea's waves. Wmap 0.5 declares
the far-water uniforms itself (`seaNightFloor`, `seaSkyReflect`, `seaWaveCount`, `seaWave`) and shades its distant sea
with them in all its far programs (its terraces too), and Sea now sends its wave trains to Wmap's programs as well as its
sky reflection and night floor, so the far sea's surface follows the same waves (normals only, fading out with distance)
and meets the near water without a ring. Sea leaves a Wmap shader that declares `seaNightFloor` unpatched (as 0.4.x
already did); with Wmap 0.4.x the text patch works as before. Sea 0.4.6 works with Wmap 0.5 too, without the waves.
Not yet confirmed in game.

Version **0.4.6** (22 October 2026, `release/sea-0.4.6.zip`, installed): no red-brown horizon at night. The sea's sky
reflection (near water and Wmap's distant sea) took the colour the game's sky function returns, which at night is the
fog colour; the sky dome shows it only at its fog amount over dark space, and the reflection now does the same
(reported in game). Not yet confirmed in game. Version **0.4.5** (21 October 2026, `release/sea-0.4.5.zip`): leaving a world no longer closes the game.
Sea switched its shader code off through the game's `Use`, which mid-teardown also uploads vanilla's per-frame uniforms
and crashed the process (access violation in `ShaderProgramBase.Uniforms4`, seen twice on 21 October); it now sets its
uniforms straight through GL. Confirmed in game on 22 October: leaving the world returns to the main menu. Version **0.4.4** (21 October 2026, `release/sea-0.4.4.zip`): the water reflects the sky the game
draws, by Fresnel, near and on Wmap's distant sea, instead of star reflections (which showed as blotches on small waves);
night visibility at 1 is twice 0.4.3's, as chosen in game; fog no longer dims glints or the night floor. See
[the sky and water notes](docs/sky-water.md). Version **0.4.3** (21 October 2026, `release/sea-0.4.3.zip`): adds live client tuning of the night and
reflection strengths (`.sea night`, `.sea sky`, `.sea glint`; see Configuration). Version **0.4.2** (21 October 2026, `release/sea-0.4.2.zip`, installed, play-tested at night with Earth and AstraTerra): fixes from a Claude +
Codex review. Leaving a Sea world no longer leaves its waves, hull cut-outs and glints in the next world's water, and a
world joined after another always gets Sea's shader code; the wave clock can't be stuck at NaN by a player without a
ping; `/sea clearwind` lets the storm's sea die down, and setwind/clearwind take effect at the same wave sample on every
side; the surface at a place no longer depends on where a ship came from (a 64-block sampling grid), and the server's
wave probe is safe from several threads; bad hull clips can't reach the shader; less work per frame (water uniforms on
the game's own shader use, the AstraTerra adapter and the star atlas). See [decisions SEA-D31–D34](docs/DECISIONS.md);
what the review left open is in [open issues](docs/open-issues.md).
0.4.1: the night floor on Wmap 0.4.3's far terrain. 0.4.0: **the wind comes from a provider; Sea has no weather of its own any more.** Sea's prevailing belts,
drifting weather, travelling storms and gusts moved to the Earth mod, which now owns climate and weather (winds, storms,
rain, clouds) and drives vanilla's weather with them. Sea only consumes the wind, through a generic call any mod can make
(`SetWindProvider`, see "For other mods"). Without a provider (no Earth) the wind is vanilla's, and the sea follows a
steady wind from the config, the same on every side. The patches that put Sea's wind into vanilla's weather (wind
lookups, wind sound, rain direction and rain streaks, cloud drift) went to Earth with it ([SEA-D30](docs/DECISIONS.md)).

0.3.8 added sun glitter, phase-aware moon trails and faint source-sky star reflections using Sea's wave normals. A
separate outdoor night-visibility floor aims for useful clear-night navigation, with reduced light under clouds, rain and
fog. Vanilla works without astronomy, geography or ship mods; AstraTerra **0.15.0** has an optional source-data adapter.
Wmap distant lighting has an optional adapter and near reflections fade before its edge. GPU/automated checks pass;
**appearance, navigation brightness and in-game compatibility are not verified**. See
[inspection, controls and comparison procedure](docs/sky-water.md) and
[build/verification record](docs/verification-celestial.md).

Wind and water foundation (through 0.3.7; stage 2 moved to Earth in 0.4.0):

- **Stage 1: water shaders.** Sea adds its code to vanilla's water shaders. That code keeps waves out of ship hulls,
  which Magelan used to do itself.
- **Stage 2: wind and sea state.** Until 0.3.8 every vanilla effect that reads the wind got Sea's wind (trees, rain and
  its streaks, clouds, wind chill, wind sound). Since 0.4.0 that's Earth's: Sea leaves vanilla's weather alone.
- **Stage 3: moving waves.** Vanilla's random bumps are replaced by waves that travel downwind. They're flat in a
  calm and tall in a storm (see below).
- **Stage 4: the same sea for everyone.** The waves are a fixed set of wave trains every side knows, running on a
  clock the server shares, so the server and every client have the same surface at the same moment. Ships (Magelan)
  ride it: everyone sees a ship rise, pitch and roll the same way, and the water around a hull follows its tilt.
  0.3.6: a ship new to a place, or one that missed a second, gets the same surface as one that has been there all
  along.

- **Stage 5: dry hulls.** A ship mod can say where its hulls are dry inside, and rooms below the waterline become
  possible. Sea cuts the water out of those hulls pixel by pixel, the waves run right up to the hull, and inside one
  your view isn't under water (no blue fog, tint or muffled sound) and creatures can breathe (see "Dry hulls" below).
  0.3.1: the cut can follow the hull's own outline at several heights (a transom stern is much wider up high than at
  the waterline), it leans with the hull's pitch and roll, and nobody walking in a dry hull splashes. 0.3.2: the
  detailed outline's stern end can be a raked plane through a transom (`DryRake`), so the water is cut off square
  there instead of a wedge behind it and a swell beside the stern quarters. 0.3.3: a dry hull with water in it (a leak,
  a hole) gives its flood level (`Flooded`, `FloodY`), and under it the sea isn't cut away. 0.3.6: the cut follows the
  hull's exact tilt, so a hull that's pitched and rolled together has no strips of water left inside.

0.3.7 (review fixes, no new features): a server's config settings of 0 reach clients as 0 (they used to arrive as 1);
a fixed wind with a NaN or infinite number in it is refused (it used to reach every client's waves); a config's negative
or NaN numbers go back to the defaults; `WaveScale` scales the chop too (0 now draws a flat sea); while the sea turns,
the strongest 24 trains change without the surface jumping; dry places only apply in the world itself, not in other
dimensions; less garbage per frame and per wind lookup; `/sea` shows the sheltered wave height where you are;
`tools/run_selftest.sh` fails when the self-test does.

## The wind

Sea doesn't make weather (0.4.0). The wind the sea and ships follow is, in this order:

1. **An admin's fixed wind** (`/sea setwind`), while it lasts.
2. **A wind provider's**: a mod that registered with `SetWindProvider` on every side, normally **Earth** (its
   prevailing winds by month, travelling storms and gusts). Ships get its wind with gusts; the sea state follows it
   without them.
3. **Otherwise vanilla's** wind for ships and other mods (`WindAt`): its strength, blowing east, since vanilla's wind
   has no direction. The sea can't follow that (it changes from tick to tick and reaches each client at another
   moment), so it follows a **steady wind from the config** (`SeaWindFrom`, `SeaWindStrength`; by default a medium
   breeze, 0.3, from the west), the same on every side.

`/sea` and `.sea` say which one it is. Vanilla's own weather effects (trees, rain, clouds, wind sound, wind chill) are
the provider's to drive, or vanilla's without one: a fixed wind from `/sea setwind` only reaches Sea's sea and ships.

**Strength** is in vanilla's units:

| Strength | Vanilla name | Beaufort |
|---|---|---|
| 0.15 | light breeze | 3 |
| 0.3 | medium breeze | 4 |
| 0.6 | strong breeze | 7 |
| 1 | storm | 10 |

The maximum is 1.5: a provider's stronger wind is capped there, and a NaN, infinite or failing answer counts as calm
(`.sea` counts them).

**Sea state:** wave height follows the wind of the past hours at the same place. At the default day length (a game
hour is 2 real minutes) the sea takes about 6 real minutes to get up.
- In a storm (strength 1) the sea reaches about 1.3 blocks after an hour and 2.1 after four. Fully built it's 2.5
  blocks.
- After the wind drops, the swell lingers for hours.
- The waves run the way the wind has been blowing.
- Without a provider, the default steady wind keeps a sea of about 0.3 blocks running east.

**What you see on the water:**

- **Swell:** a library of wave trains, 20 wavelengths half an octave apart (4 to 2896 blocks), each in 18 directions
  20° apart, every one with its own fixed starting phase. Breezes keep their original spectrum. From strength 0.4 to
  0.8 it blends to a Pierson–Moskowitz shape with peak parameter 30 × significant height: at 1.5, 165 blocks by default,
  or 363 with `StormHeightScale: 2.2`. Directions split between two neighbours, shorter bands spread up to ±40°.
  Each runs at its deep-water speed (strength 1 default peak about 11 blocks/s).
- **Chop:** 6.5 and 4.3 blocks from the local wind, plus 9.7 in storms. Breeze chop stays as before; storm chop is
  bounded at roughly the breeze's short-wave RMS slope, without growing with wind or fading to a mirror.
- **Crest shape:** Gerstner horizontal displacement concentrates vertices at crests, leaving broader troughs.
  The current spectrum supplies steepness, globally bounded against loops. Ship height queries invert the horizontal
  map, including the existing sheltered-height multiplier. Calm/default amplitudes and periods stay unchanged.
- **Wind foam:** short, porous breaking-crest patches, opening into lace and fading behind the crest. Local wind
  controls their density, but cannot whiten an uncompressed or sheltered surface. No downwind stripe/spume layer.
  Foam uses the water's light and fog; dry hulls keep their existing cut and vanilla keeps contact/depth foam.
- **Nothing jumps:** when the sea state or the wind changes, trains grow and fade (blended over a second); none of
  them ever changes its phase. Up to 24 trains run at once.
- **The same for everyone:** the trains come from the world's wind key, their strengths from the sea state and wind
  at the place (sampled once a second on the wave clock), and the wave clock is the server's (see Multiplayer).
- **Seamless ring:** on a Cylinder World, every train fits a whole number of waves around the ring.
- **Calm and storm:** a calm sea is nearly flat. A storm sea's waves are about 2.5 blocks high (strength 1, fully
  built) and over 5 at strength 1.5.
- **Sheltered water:** lakes get a fifth of it, like vanilla. Lava and weakly waving liquids keep vanilla's warp.
  (The fraction comes from the world's ocean map, the same way the game's chunk mesher gets it, so ships on a lake
  move with the smaller waves drawn there.)
- **Ship hulls:** inside a hull's waterline footprint the water stays below the waterline, which tilts with a hull
  that pitches and rolls. Around the hull the waves settle to the level it rides at, so no crest reaches into the
  ship between the water mesh's 1-block vertices: over 4 blocks for a hull that stays level, over whatever the ship
  mod asks for one that rides the waves (Magelan: 1.5).

**Latitude** (shown by `/sea`) is the game calendar's: vanilla's "realistic climate", Cylinder World's or Earth's.
With Cylinder World the waves repeat exactly around the ring; the wind is the provider's to make seamless there.

## Multiplayer

Nothing about the wind or the waves is sent while you play, except a clock:
- **When you join,** the server sends one small packet: a wind key (random, made for the world and saved with it; it
  tells nothing about the seed; since 0.4.0 it only picks the wave trains' phases), the config, and the ring and
  calendar sizes.
- **After that,** the server and every client compute the same sea from the game time, which vanilla already keeps
  in sync. The wind provider must give the same wind on every side for that (Earth's is a pure function of the world
  seed, the place and the game time); Sea never sends it.
- **The sample hours (0.3.6):** the sea state and wind behind each second of waves are taken at that second's own game
  hour. The server sends that with the clock (from a few seconds ahead, at the calendar's speed then; at once when an
  admin sets the time or everyone starts sleeping), so every side agrees exactly, even though the calendar moves in
  steps and races while everyone sleeps.
- **The wave clock:** the waves run on the server's own running time. The server sends it to each player every 5
  seconds (plus half their ping). A client keeps its own clock and eases toward the server's by at most 2%, so the
  waves never jump; it only snaps when it's more than a second off (joining, a long pause). The game calendar isn't
  used for this: it jumps on every update from the server and runs fast while everyone sleeps.
- **The server decides anything that matters**, such as ships' speed and height. Clients use the wind for looks.
- **An admin's fixed wind** is the only other thing sent later.

## Commands

- `/sea`: wind and waves where you stand, your latitude, and where the wind comes from (the provider's name, or
  vanilla with the sea's steady wind).
- `/sea setwind <from> <strength> [game hours] [built]` (admins): fix the wind everywhere for the sea and ships (over
  the provider's; vanilla's weather effects stay the provider's).
  - `from` is a compass point (`N`, `NE`, `SSW`…) or degrees.
  - Hours `0` means it stays fixed until `/sea clearwind`.
  - `built` makes the sea fully up at once, as if the wind had blown for a day. Without it, the sea builds over
    about 3 game hours.
  - Example: `/sea setwind E 1.5 0 built`.
  - `NaN` and `Infinity` are refused for every number (0.3.7).
  - A fixed wind is saved with the world.
  - (0.4.2) It takes over a few seconds after the command, on the server and every client at the same wave sample
    (clients hear of it a moment after the server makes it).
- `/sea clearwind` (admins): back to the weather (the provider's wind, or vanilla's). (0.4.2) The sea the fixed wind
  raised dies down over the next game hours, as after any wind, instead of vanishing at once; a setwind over another
  does the same for the old one.
- `/sea storms` is gone (0.4.0): storms are Earth's.

- `.sea coast` (client, 0.6.0): the coast where you stand: depth, wave travel time from the shore, the share of the
  sea that gets past the land, how far off the shore is and how steep, the share of the waves it throws back,
  breaking, whitewater, the current and any rip channel; how far the coast map has got and
  what it has cost (column scans on the main thread, tile solves in the background, rescans after block changes, the
  swash's frame time).
- `/sea probe [x] [z] [t]` (server) and `.sea probe [x] [z] [t]` (client), 0.6.0: what the open sea contracts answer at
  a place and wave time (default: where you stand, at the last whole 10 s), as one line: height, shelter, the pose of a
  30 × 8 block hull, the number of trains and their summed amplitude. The same arguments on both sides must give the
  same line (rule R5); a difference is a bug.
- `.sea drawn` (client, 0.6.0): how far the drawn water (the camera's trains) is from the per-place answer, at 0 to
  512 blocks around you in 16 directions (open issue O1).
- `.sea` (client, with a dot): what Sea is doing on your own client.
  - where the wind comes from (the provider's name, and any of its answers Sea refused)
  - the wind it sees, and what vanilla sees
  - the sea state and the drawn waves (how many trains, the sheltered-water factor where you are)
  - the wave clock, and how far it still is from the server's
  - whether its water shader code and moving waves went in
  - how many hulls are kept dry
  - which patches are active

## Config — `VintagestoryData/ModConfig/sea.json` (server; clients get the server's)

| Key | Default | Meaning |
|---|---|---|
| `WaveScale` | 1 | multiplies wave heights, the chop too (0.3.7) |
| `StormHeightScale` | 1 | 0–4, storm-only height multiplier, blended over wind strengths 0.4–0.8; 2.2 gives about 12-block Hs at 1.5. Wavelength scales with height; breeze/chop stay unchanged. Larger seas are opt-in and need ship/shore playtesting |
| `SeaWindFrom` | 270 | without a wind provider: the compass bearing (degrees) the steady wind the sea follows blows from (0.4.0) |
| `SeaWindStrength` | 0.3 | without a wind provider: that steady wind's strength, 0–1.5 (0 = a calm sea) (0.4.0) |
| `CelestialReflections` | 1 | sun/moon trails; 0 restores vanilla highlights; range 0–2 |
| `SkyReflections` | 1 | the sky reflected by Fresnel, near and on Wmap's distant sea (0.4.4; no star reflections); 1 physical; 0 disables; range 0–2 |
| `Surf` | true | (0.6.0) waves feel the sea floor near coasts: they turn toward the beach, steepen, break (whitewater, spray) and drive a longshore current. Off on a Cylinder World ring |
| `NightVisibility` | 1 | outdoor ambient night floor, real twilight and local-moon correction on the client's rendering ([night light](docs/night-light.md)); 1 = clear moonless 0.10, high full moon 0.40 (0.4.7-night; 0.4.4–0.4.7-pre: 0.15 / 0.45, now 1.5); 0 keeps the game's own night and twilight; range 0–2 |

A negative, NaN or infinite number goes back to its default when the server loads the file (0.3.7); `SeaWindFrom` may
be any finite number (it's taken round the compass). The weather settings of 0.3 (`WindModel`, `PrevailingStrength`,
`Variability`, `Gusts`, `StormFrequency`, `StormRadius`, `StormSpeed`, `SeasonShift`) are gone with Sea's weather (Earth
has its own); an old file keeps them until the server writes it back without them.

Tuning by eye (0.4.3): on a client, `.sea night <0–4>`, `.sea sky <0–4>` and `.sea glint <0–4>` change these three
strengths on that client at once, until you leave the world; `.sea night` alone shows the value, `.sea night default`
and `.sea reset` go back to `sea.json`, which they never change.
`.sea foam all|wind|off` shows or hides breaking-wave crest foam locally. Vanilla contact foam retains its own
settings. The removed wake commands cannot enable wakes.

## Dry hulls (for ship mods)

The world has water inside a ship's hull wherever the hull is below the waterline: the ship is an entity, not
blocks. A ship mod that wants rooms down there tells Sea two things.

- **Where the water isn't drawn:** mark the hull's water clip `Dry`, with how far its outline is inside the clip's
  footprint (`DryInset`, the footprint's margin) and how deep and high the dry space reaches from its waterline
  (`DryDepth`, `DryHeight`).
  - The water fragment shaders then discard every water pixel inside that outline, at any wave height.
  - The outline and the heights are in the hull's own frame: they lean with the clip's `SlopeAlong` and `SlopeAcross`
    (its pitch and roll), so a heeling hull's side doesn't show the waves inside. The frame is the hull's exact one,
    rebuilt from those two slopes (roll about the keel, then pitch), measured from the waterline above the clip's
    `X`, `Z`: put the hull's origin on its waterline and it's exact.
  - **A detailed outline** (`DryProfile`, optional): 4 rows of 32 half-widths, the lowest row first, row j at
    `DryLevel0 + j * DryLevelStep` above the waterline, stations from stern to bow (the footprint less `DryInset`).
    The shader interpolates between stations and rows and holds the end rows beyond them, so give values that cover
    the hull in between (Magelan fits them to its hull's exact widths). The nearest four dry hulls with one use it;
    others, and hulls without one, the footprint's outline less `DryInset` at every height.
  - **A raked transom** (`DryRake`, with a detailed outline): its stern end is a plane across the hull, at the stern
    station at the waterline and moving `DryRake` blocks along the keel per block up in the hull's frame (negative:
    aft, a transom leaning out over the water); aft of the first station the first station's widths hold. Give the
    first station the transom's widths: the water is then cut off square at the transom. (Interpolating from nothing
    behind a transom to its full width at the next station, a block on, made the fit swell out beside the stern.)
  - **Water in the hull** (`Flooded`, `FloodY`: world y, a level plane): under it the sea isn't cut away, so where the
    waves inside the hull's outline dip below the water in her they show (the ship mod draws that water's surface, and
    says it's wet under it). Level in the world, whatever the hull's pitch and roll; over it the cut is as before.
  - Nothing is held down or calmed around a dry hull, so the waves run right up to it.
  - Hulls not marked dry keep the old clip: the water inside held at the waterline, a calm ring around it.
- **Where it's dry for creatures and the camera:** register an `IDrySpaces` on both sides and answer `IsDry(x, y, z)`
  (inside a room of a hull, say). Sea then:
  - tells the client your eyes aren't under water there, which turns off the blue fog, the tint and the muffled
    underwater sound (the game's `EyesInWaterDepth` and `GetEyesInWaterColorShift`)
  - lets creatures with their heads in water there breathe, on the server (`EntityBehaviorBreathe.Check`)
  - makes no splashes, sediment or glowing plankton round someone walking there, and no splash when they "enter" the
    water (`EntityAgent.SpawnFloatingSediment`, `Entity.OnCollideWithLiquid`): vanilla makes them for anyone moving
    with their feet in water, and aboard a sailing ship everyone always moves
  - shows none of vanilla's water specks floating in the water there (`ModSystemAmbientParticles`), nor rain's splashes
    or a storm's spray on the water's surface there (`WeatherSimulationParticles`): `DryParticles` (0.3.4). Since 0.3.5
    a spawn is judged over its whole life: a speck stands still in the world's water for two seconds and a hull moving
    through it would have it inside a moment later. Answer `IsDryWithin(x, y, z, seconds)` (a default interface
    method: unless you do, it's `IsDry` now) with whether the place is dry at any moment of the next seconds, e.g. from
    where your hull will be as it sails, rolls and heaves.
  - Players riding a ship are never counted as swimming, so nothing else needs changing for them.
  - Only the world itself (dimension 0, where ships sail) has dry places (0.3.7). `IsDry` gets no dimension, so a
    creature, camera or particle in another dimension is never treated as dry.
- **If a graphics driver won't take the new shader code:** Sea logs the compiler's error and tries again without the
  dry-hull cut, and if that fails too with vanilla's water. `.sea` says so.

## For other mods

```csharp
var sea = api.ModLoader.GetModSystem<Sea.SeaSystem>();
Sea.Wind w = sea.WindAt(x, z);        // w.X, w.Z: where the air goes; w.Strength; w.FromDegrees (compass)
Sea.SeaState s = sea.SeaAt(x, z);     // Height (blocks), DirX/DirZ (where waves run), Period (s), Wavelength
sea.Model?.At(x, z, totalHours);      // any place and game time (pure, thread-safe; calm without a provider)
sea.AddWaterClipSource(src);          // client: an IWaterClipSource giving hull footprints each frame (width profile, tilt, calm ring)
// Retired in 0.5.1: SetWakeHull returns false; RemoveWakeHull is a compatibility no-op.

// a wind provider (Earth): call on every side, any time, with the same pure, thread-safe functions (BCL types only,
// so it can be bound by reflection without referencing Sea)
bool ok = sea.SetWindProvider("earth",
    (x, z, totalHours, gusts) => (toX, toZ),    // vanilla units: +X east, +Z south, length = strength 0..1.5; gusts = false: the steady wind the sea follows
    (x, z, totalHours) => (toX, toZ));          // the month's prevailing wind (Wmap's layer), or null: windAt without gusts
sea.ClearWindProvider("earth");       // back to vanilla's wind (only the provider of that name can clear it)
sea.WindProviderName;                 // who decides the wind on this side, or null

// the waves, the same on the server and every client
var place = new Sea.WavePlace();      // one per thing that rides or looks at the waves (a ship)
sea.SampleWaves(place, x, z);         // each tick or frame: the trains running there now, at sea.WaveTime
                                      // (0.4.2: from the sea state on a 64-block grid around it, so the same for
                                      // everyone there, wherever they came from)
place.HeightAt(x, z);                 // the open-sea surface (Count, Kx, Kz, Omega, Phase, Amp: the trains themselves)
sea.WaveFactorAt(x, z);               // 1 on the open sea .. 0.2 sheltered (multiply heights by it, as the shader does)
                                     // pre.11: looks up shelter at the inverse mesh coordinate, including across gradients
sea.WaveHeightAt(x, z);               // both sides: the surface there now, sheltered water included
sea.WaveTime;                         // seconds on the shared wave clock

// a fixed wind (server)
sea.SetOverride(ov);                  // at once, the sea's past rewritten (tests; null clears)
sea.ChangeOverride(ov);               // (0.4.2) as an admin's setwind/clearwind: from sea.NextActivation on every side, the old one's sea dying down
sea.FixedWindNow;                     // the fixed wind in force now, or null

// dry hulls (rooms below the waterline)
sea.AddDrySpaces(src);                // both sides: an IDrySpaces answering IsDry(x, y, z) quickly (every tick, maybe off-thread)
sea.IsDry(x, y, z);                   // both sides: is it dry there though the world has water?
// and in IWaterClipSource.AddWaterClips: new WaterClipBox { …, Dry = true, DryInset = margin, DryDepth = …, DryHeight = …,
//     DryProfile = 4 × 32 half-widths (optional), DryLevel0 = …, DryLevelStep = …, DryRake = … (a raked transom),
//     Flooded = true, FloodY = … (water in her: world y, level; the sea isn't cut away under it) }
clip.InDryHull(x, y, z);              // the shader's own test, in C#
```

Reference `Sea.dll` without copying it (`Private="false"`) and add `"sea"` to your modinfo dependencies.

(0.6.0) Coasts: `place.HeightAt` near a beach includes the surf (`SampleWaves` sets `WavePlace.Coast`);
`place.SurfAt(x, z)` gives breaking, whitewater and depth; `sea.CoastAt(x, z)` the coast's depth, travel time,
direction, lee per direction and rip; `sea.LeeAt(place, x, z)` the share of the sea that gets past the land;
`sea.CurrentAt(place, x, z)` the surf's current (longshore and rips) in m/s.

**Without referencing Sea.dll** (0.6.0): the open contracts in `api.ObjectCache`: `seacontracts:seastate` (`at`,
`shelter`, `current`, `surf`, `lee`), `seacontracts:surface` (`seconds`, `trains`, `height`, and `pose` with Rough Seas'
`PoseOf` meaning) and `seacontracts:hulls` (`register` hull records, `registerDry`, `isDry`). See
[docs/open-sea-contracts.md](docs/open-sea-contracts.md).

## Build and test

Quit the game completely before swapping the zip in your Mods folder: a world loaded again from the main menu
keeps the old `Sea.dll` and refuses the new one ("Assembly with same name is already loaded").

```bash
dotnet build -c Release                  # release/sea-<version>.zip; refuses an existing ZIP, preserves older snapshots
dotnet run --project tests/SeaTests      # offline: steady sea wind, wind provider, sea state, speed, waves, shader injection, dry hulls, water particles, configs
dotnet run --project tests/ShaderCheck   # on this machine's GPU: the edited water shaders compile and link; the dry cut is right
dotnet run --project tests/SkyIntegrationCheck -- --vanilla # no-AstraTerra adapter path, without a game/save
dotnet run --project tests/SkyIntegrationCheck # exact installed DLL extracted to obj/sky-inspection/AstraTerra.dll
tools/run_selftest.sh [--with-cworld]    # headless server: no vanilla weather patched, vanilla's wind without a provider, a provider's wind, fixed wind, the waves, the ring, speed, breathing in dry places; exits 1 unless "SELFTEST ALL OK"
```

`tests/ShaderCheck` needs a desktop session. It opens a hidden window with the game's own OpenTK and GLFW. It builds
the game's water shaders the way the game does (includes expanded, its `#define` lines after
`#version`, low and high settings), with and without Sea's code, and compiles and links them on your graphics driver.
It then runs Sea's dry-hull test on the GPU over a grid around a shaped hull (level, pitched and rolled, with a
detailed outline, a raked transom, and flooded: the sea shown under the water in her) and compares every pixel with the
C# twin.
Run it after any change to Sea's shader code; the offline tests only see the text.

The offline wave tests check that the waves are the same wherever and however often they're worked out, the
right height, moving downwind, turning with the sea, never jumping when the sea changes, seamless around a ring, the
same in the shader as on the CPU (also far from the world's origin), and that the wave clock only eases.

Engine facts behind this are in `docs/engine-notes.md`. Why it's built this way: `docs/DECISIONS.md`. Vanilla without
Earth, Terra Prety and other world generators: `docs/compatibility.md`.

## Not yet

- **What the 0.4.2 review left open** (by design, deferred, or not yet play-tested): [docs/open-issues.md](docs/open-issues.md).
- **Coasts, what 0.6.0 leaves out.** The lee is a geometric estimate (rays upwave, no real diffraction pattern).
  Fetch is judged only within the coast map's 64-block reach: the open sea's state comes from the weather, so a
  sheltered gulf or a lake a few kilometres across gets the sea's own waves away from its shores. Growth limited by
  depth (shallow banks far from land holding the sea down) is not applied: done locally it also held down the sea
  arriving at beaches. Both need world-scale land and depth that the weather and Sea agree on (likely from Earth).
  Reflected waves carry no orbit of their own, so a boat before a wall rises and falls without being pushed to and
  fro. Where the size of the waves changes within a block or two (the foot of a cliff, a rip's edge in a storm) the
  drawn surface can fold over itself at a few points; a boat's height there may be up to 0.2 blocks off the drawn
  one. Ships rides the surf by a plane fitted under her hull, not train by train.

For isolated comparisons, use `tools/New-CelestialTestProfile.ps1`; see [the procedure](docs/sky-water.md#reproducible-in-game-comparison-pending). It creates a fresh data directory without overwriting packages or saves. The older shell self-test resets its testserver save/log folders, so use the fresh-profile workflow to retain those snapshots. For another Release build, choose a new version or a fresh explicit `-p:PackageFile=C:\path\to\a-new-unique.zip`.
