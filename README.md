# Sea — wind with a direction, travelling storms, seas that build up (Vintage Story 1.22)

Vanilla wind has a strength but no direction, and its waves are the same random bumps everywhere. Sea gives the
world a wind you can learn and plan around, and waves that follow it. It also owns the water shaders, so ship mods
(Magelan) ask Sea to keep waves out of their hulls instead of each patching the shaders.

Version 0.3.6:

- **Stage 1: water shaders.** Sea adds its code to vanilla's water shaders. That code keeps waves out of ship hulls,
  which Magelan used to do itself.
- **Stage 2: wind and sea state.** Every vanilla effect that reads the wind now gets Sea's wind:
  - swaying trees and plants
  - rain, snow and hail, which now blow in Sea's direction (the rain streaks lean with it; vanilla draws every
    streak leaning east)
  - clouds, which now drift with it
  - wind chill
  - wind sound
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

## The wind

It's built in four layers, all pure math from a key, the position and the game time:

1. **Prevailing winds by latitude.** These are Earth's belts:

   | Latitude | Wind |
   |---|---|
   | 0–5° | doldrums, weak |
   | 5–30° | trade winds from the NE (south: from the SE), steady |
   | ~32° | horse latitudes, calm and variable |
   | 35–62° | westerlies from the WSW, stronger in the south ("roaring forties") |
   | 65–90° | polar easterlies |

   The belts move about 8° north in northern summer and south in winter. In July the SE trades cross the equator.
2. **Weather drifting over days.** A smooth random wind over about 5000 blocks and 30 game hours. It's gentle in the
   trades and stronger in the westerlies.
3. **Travelling storms.** Every game day, each 6000-block cell of the map rolls whether a storm forms there:
   - **Where and when:** tropical storms form in late summer; westerly storms are more common in winter.
   - **How they move:** a storm lives 1.5–3.5 game days. Tropical storms drift west and toward the pole; the others
     are carried east.
   - **Their wind** turns around the centre: anticlockwise in the north, clockwise in the south. It spirals slightly
     inward, is calm in the eye and strongest at the eyewall. It's stronger on the side where the storm's own motion
     adds to it.
   - **Reading one:** stand with your back to the wind, and the centre is on your left in the north, on your right in
     the south. A shifting wind means a storm is passing.
4. **Gusts:** about ±20% strength and ±12° direction, changing over tens of seconds.

**Strength** is in vanilla's units:

| Strength | Vanilla name | Beaufort |
|---|---|---|
| 0.15 | light breeze | 3 |
| 0.3 | medium breeze | 4 |
| 0.6 | strong breeze | 7 |
| 1 | storm | 10 |

The maximum is 1.5, and it grows with height above sea level like vanilla's. On the offline test planet the
westerlies blow at 0.6 or more about 15% of the time, the trades about 1.5%.

**Sea state:** wave height follows the wind of the past hours at the same place. At the default day length (a game
hour is 2 real minutes) the sea takes about 6 real minutes to get up.
- In a storm (strength 1) the sea reaches about 1.6 blocks after an hour and 2.2 after four. Fully built it's 2.5
  blocks.
- After the wind drops, the swell lingers for hours.
- The waves run the way the wind has been blowing.

**What you see on the water:**

- **Swell:** a library of wave trains, 12 wavelengths half an octave apart (4 to 181 blocks), each in 18 directions
  20° apart, every one with its own fixed starting phase. The sea state lights up the wavelengths from about 1.5×
  its peak down to a seventh of it (the shorter, the smaller), each shared between the two directions next to where
  it runs, the shorter ones spread up to ±40°. Each runs at its deep-water speed, so long waves run fast (a storm
  swell about 10 blocks/s) and short ones slower.
- **Chop:** two short wavelengths (6.5 and 4.3 blocks) from the wind right where you are. They react within seconds.
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

**Where latitude comes from:** Sea reads the game calendar's latitude.
- In a vanilla world that's the "realistic climate" latitude. Without realistic climate, it's 45°N everywhere.
- Cylinder World and Earth Map set real latitudes.
- With Cylinder World, the wind repeats exactly around the ring, so nothing jumps at the seam.

## Multiplayer

Nothing about the wind or the waves is sent while you play, except a clock:
- **When you join,** the server sends one small packet: a wind key (random, made for the world and saved with it; it tells nothing about the seed), the
  config, and the ring and calendar sizes.
- **After that,** the server and every client compute the same wind from the game time, which vanilla already keeps
  in sync.
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

- `/sea`: wind and waves where you stand, your latitude and wind belt.
- `/sea storms [range]` (admins): storms nearby, with distance, direction, strength, heading and age.
- `/sea setwind <from> <strength> [game hours] [built]` (admins): fix the wind everywhere.
  - `from` is a compass point (`N`, `NE`, `SSW`…) or degrees.
  - Hours `0` means it stays fixed until `/sea clearwind`.
  - `built` makes the sea fully up at once, as if the wind had blown for a day. Without it, the sea builds over
    about 3 game hours.
  - Example: `/sea setwind E 1.5 0 built`.
  - A fixed wind is saved with the world.
- `/sea clearwind` (admins): back to the weather.

- `.sea` (client, with a dot): what Sea is doing on your own client.
  - the wind it sees, and what vanilla sees
  - the sea state and the drawn waves (how many trains, the sheltered-water factor where you are)
  - the wave clock, and how far it still is from the server's
  - whether its water shader code and moving waves went in
  - how many hulls are kept dry
  - where rain and clouds blow
  - which patches are active

Vanilla's `/weather setw …` no longer changes the wind (Sea decides it). Use `/sea setwind`. To see the rain blow,
force it with `/weather setprecip 1`, and go back to the weather with `/weather setprecipa`.

## Config — `VintagestoryData/ModConfig/sea.json` (server; clients get the server's)

| Key | Default | Meaning |
|---|---|---|
| `WindModel` | `Earthlike` | `Earthlike` (all layers), `Steady` (prevailing winds and gusts only), `Off` (vanilla wind for trees, rain, sound and boats; the sea still follows Sea's steady winds, so it's the same for everyone) |
| `PrevailingStrength` | 1 | multiplies the prevailing winds |
| `Variability` | 1 | multiplies the day-to-day drift |
| `Gusts` | 1 | multiplies gusts (0 = none) |
| `StormFrequency` | 1 | multiplies how often storms form (0 = none) |
| `StormRadius` | 2000 | typical storm size in blocks |
| `StormSpeed` | 350 | blocks per game hour (≈ 3 blocks/s at the default day length; the carrack does 7) |
| `SeasonShift` | 8 | degrees the belts move with the seasons |
| `WaveScale` | 1 | multiplies wave heights |

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
- **If a graphics driver won't take the new shader code:** Sea logs the compiler's error and tries again without the
  dry-hull cut, and if that fails too with vanilla's water. `.sea` says so.

## For other mods

```csharp
var sea = api.ModLoader.GetModSystem<Sea.SeaSystem>();
Sea.Wind w = sea.WindAt(x, z);        // w.X, w.Z: where the air goes; w.Strength; w.FromDegrees (compass)
Sea.SeaState s = sea.SeaAt(x, z);     // Height (blocks), DirX/DirZ (where waves run), Period (s), Wavelength
sea.Model?.At(x, z, totalHours);      // any place and game time (pure, thread-safe)
sea.AddWaterClipSource(src);          // client: an IWaterClipSource giving hull footprints each frame (width profile, tilt, calm ring)
sea.SetPrevailingWinds(src);          // both sides: a map's own prevailing winds (IPrevailingWinds) instead of the belts

// the waves, the same on the server and every client
var place = new Sea.WavePlace();      // one per thing that rides or looks at the waves (a ship)
sea.SampleWaves(place, x, z);         // each tick or frame: the trains running there now, at sea.WaveTime
place.HeightAt(x, z);                 // the open-sea surface (Count, Kx, Kz, Omega, Phase, Amp: the trains themselves)
sea.WaveFactorAt(x, z);               // 1 on the open sea .. 0.2 sheltered (multiply heights by it, as the shader does)
sea.WaveHeightAt(x, z);               // both sides: the surface there now, sheltered water included
sea.WaveTime;                         // seconds on the shared wave clock

// dry hulls (rooms below the waterline)
sea.AddDrySpaces(src);                // both sides: an IDrySpaces answering IsDry(x, y, z) quickly (every tick, maybe off-thread)
sea.IsDry(x, y, z);                   // both sides: is it dry there though the world has water?
// and in IWaterClipSource.AddWaterClips: new WaterClipBox { …, Dry = true, DryInset = margin, DryDepth = …, DryHeight = …,
//     DryProfile = 4 × 32 half-widths (optional), DryLevel0 = …, DryLevelStep = …, DryRake = … (a raked transom),
//     Flooded = true, FloodY = … (water in her: world y, level; the sea isn't cut away under it) }
clip.InDryHull(x, y, z);              // the shader's own test, in C#
```

Reference `Sea.dll` without copying it (`Private="false"`) and add `"sea"` to your modinfo dependencies.

## Build and test

Quit the game completely before swapping the zip in your Mods folder: a world loaded again from the main menu
keeps the old `Sea.dll` and refuses the new one ("Assembly with same name is already loaded").

```bash
dotnet build -c Release                  # release/sea-<version>.zip (version from modinfo.json); deletes older sea-*.zip there
dotnet run --project tests/SeaTests      # offline: belts, seasons, storms, cache, sea state, speed, waves, shader injection, dry hulls, water particles
dotnet run --project tests/ShaderCheck   # on this machine's GPU: the edited water and particle shaders compile and link; the dry cut is right
tools/run_selftest.sh [--with-cworld]    # headless server: vanilla sees Sea's wind, fixed wind, the ring, the waves, speed, breathing in dry places
```

`tests/ShaderCheck` needs a desktop session. It opens a hidden window with the game's own OpenTK and GLFW. It builds
the game's water and particle shaders the way the game does (includes expanded, its `#define` lines after
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

- **Rain amount doesn't follow the storms yet.** Vanilla's rain comes from its own per-region weather patterns and a
  rainfall noise, so a storm's wind can arrive under a clear sky.
- **Waves don't know about coasts.** Only the ocean map shelters them (lakes and inland water get a fifth); a bay or
  the lee of an island on the open sea gets the whole sea.
