# Sea — waves that follow the wind, the same sea for everyone (Vintage Story 1.22)

Vanilla's waves are the same random bumps everywhere. Sea gives the world waves that travel downwind, build up and
calm down with the wind of the past hours, and are the same on the server and every client, so ships can ride them.
It also owns the water shaders, so ship mods (Magelan, Hansa) ask Sea to keep waves out of their hulls instead of each
patching the shaders.

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
| `SeaWindFrom` | 270 | without a wind provider: the compass bearing (degrees) the steady wind the sea follows blows from (0.4.0) |
| `SeaWindStrength` | 0.3 | without a wind provider: that steady wind's strength, 0–1.5 (0 = a calm sea) (0.4.0) |
| `CelestialReflections` | 1 | sun/moon trails; 0 restores vanilla highlights; range 0–2 |
| `SkyReflections` | 1 | the sky reflected by Fresnel, near and on Wmap's distant sea (0.4.4; no star reflections); 1 physical; 0 disables; range 0–2 |
| `NightVisibility` | 1 | outdoor ambient night floor/local-moon correction; 1 = clear moonless 0.15, high full moon 0.45 (0.4.4, twice 0.4.3's); 0 keeps existing ambient lighting; range 0–2 |

A negative, NaN or infinite number goes back to its default when the server loads the file (0.3.7); `SeaWindFrom` may
be any finite number (it's taken round the compass). The weather settings of 0.3 (`WindModel`, `PrevailingStrength`,
`Variability`, `Gusts`, `StormFrequency`, `StormRadius`, `StormSpeed`, `SeasonShift`) are gone with Sea's weather (Earth
has its own); an old file keeps them until the server writes it back without them.

Tuning by eye (0.4.3): on a client, `.sea night <0–4>`, `.sea sky <0–4>` and `.sea glint <0–4>` change these three
strengths on that client at once, until you leave the world; `.sea night` alone shows the value, `.sea night default`
and `.sea reset` go back to `sea.json`, which they never change.

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
- **Waves don't know about coasts.** Only the ocean map shelters them (lakes and inland water get a fifth); a bay or
  the lee of an island on the open sea gets the whole sea.

For isolated comparisons, use `tools/New-CelestialTestProfile.ps1`; see [the procedure](docs/sky-water.md#reproducible-in-game-comparison-pending). It creates a fresh data directory without overwriting packages or saves. The older shell self-test resets its testserver save/log folders, so use the fresh-profile workflow to retain those snapshots. For another Release build, choose a new version or a fresh explicit `-p:PackageFile=C:\path\to\a-new-unique.zip`.
