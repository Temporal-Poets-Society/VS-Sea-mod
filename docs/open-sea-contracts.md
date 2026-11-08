# Open sea contracts

**Status: proposal, 7 November 2026, not agreed with hoblin. Our side partly implemented (unreleased; tested in game on one client, 7 November, section 14):** Sea 0.6.0-pre.1 on branch `claude/open-contracts` publishes `seastate`, `surface` and `hulls` and follows a published `weather` owner; Ships on branch `claude/sea-contracts` (`31d1668`, `aa1c411`, `73a4367`, `0946ceb`) reads its waves through its own trains type, with the contracts as its fallback (wind, sea state, `pose`), rides the surf and drifts with the current; since `0946ceb` it no longer needs Sea.dll: a bridge DLL in its package, loaded only when Sea is installed, holds the Sea-typed code. Earth publishes the `weather` role on branch `claude/weather-contract` (`1895a7f`, offline-tested, not merged). Lines saying "today" describe Sea 0.5.2; section 12 marks what the branches did.

**Agreed direction (the author):** Earth owns weather (31 October). Sea keeps its own wave surface and its hull water cut, Ships keeps its own wave motion (`ShipMotion`), and our side becomes more open so that other mods can use it (7 November). Rough Seas' drawn surface is not adopted as the canonical one.

**Read against:** Sea 0.5.2 (`release/0.5`, `1cf7687`), Earth `src/EarthWeather.cs`, Ships (folder `Hansa`) `src/Ship/ShipMotion.cs`, Rough Seas `master` `1c26b77` (6 November) and its wiki. Background: [roughseas-merge-assessment.md](roughseas-merge-assessment.md).

## 1. Goal

A ship or boat mod reads the wind, the sea and the waves, and registers its hulls to have the water cut out of them, **the same way whichever mods supply those things**. Today every consumer compiles against a concrete type (`Sea.SeaSystem`, `RoughSeas.SeaAndWeather.SeaWeatherSystem`), so a ship written for one never works with the other.

The contracts give the **environment** (weather, sea state, wave surface, hull cut). They do not give a ship's answer to it: roll, pitch, heave, yaw, heel, damage and flooding stay in each ship mod.

## 2. Layers and owners

One owner per role per world. A role with no owner falls back as the last column says.

| Role | Owner on an Earth world | Owner elsewhere | Without an owner |
|---|---|---|---|
| `weather`: wind, storms, rain, clouds | Earth | an optional default (Rough Seas' model, if hoblin registers it) | Sea's configured steady wind; vanilla's wind for vanilla |
| `seastate`: wave height, period, direction, wind at the sea | Sea | Sea, or Rough Seas where Sea is absent | none: consumers assume a calm sea |
| `surface`: the drawn waves, their clock, heights, poses, trains | Sea (the mod whose shader draws the swell) | same | vanilla water; consumers use their own rocking |
| `hulls`: water cut out of hulls, dry spaces | the `surface` owner (only the shader that draws the water can cut it) | same | vanilla's `hidewatersurface` lids only |
| ship motion, damage, flooding | each ship mod | each ship mod | — |

Temperature and geography are not roles here: they already go through vanilla. Earth replaces vanilla's ocean layer (`GenMaps.oceanGen` becomes `EarthOceanLayer`, Earth `src/EarthWorldGen.cs:808`), sets latitude (`OnGetLatitude`) and adjusts the climate (`OnGetClimate`), and Rough Seas reads all three.

## 3. Rules for every contract

**R1. Only .NET base types cross a mod boundary.** No mod ships or references another mod's DLL to use a contract. This is how Earth already binds Sea's `SetWindProvider` (reflection, BCL delegates). Sea's typed API (`SeaSystem`, `WavePlace`, `IWaterClipSource`, `IDrySpaces`) stays as it is for mods that already compile against `Sea.dll` (Ships, Magelan, Sea monsters, Wmap).

**R2. Discovery through `api.ObjectCache`.** Each role is one entry, on each side, under the key `seacontracts:<role>`. Its value is a `Dictionary<string, object>` of named members: delegates, plus these plain values:

| Member | Type | What it is |
|---|---|---|
| `owner` | `string` | the producing mod's ID |
| `version` | `int` | the contract version, 1 for this document |
| `priority` | `int` | 100 for the world's owner (Earth on an Earth world), 50 for a mod's default, 0 for a fallback |
| `revision` | `Func<long>` | changes whenever earlier answers may now read differently; consumers drop their caches when it changes |

A producer claims a role in `Start`/`StartServerSide`/`StartClientSide` (the main thread). It replaces an existing entry only with a higher `priority`. Before it generates or writes anything for that role, it checks it is still the entry's `owner`, so a default that lost to Earth stops by itself. Consumers look the entry up when they need it, never cache it across a `revision` change, and never assume one exists.

`api.ObjectCache` is part of the game API (`ICoreAPI.ObjectCache`, `Dictionary<string, object>`), shared by every mod on a side.

A producer that lost a role to a higher priority (say a priority-50 default on a client, before the world's owner withdrew at join) gets it back only if it checks again: producers re-check their roles on joining a world and whenever another producer's entry goes away (Sea re-checks every few seconds).

**R3. Units and axes.** 1 block = 1 m. +X is east, +Z south, +Y up. Directions are vectors pointing **toward** where something goes. Wind in the new contracts is in **m/s**. Vanilla's wind unit (1.0 = 25 m/s) appears only on vanilla's own channel and in Sea's existing `SetWindProvider`.

**R4. Time.** Weather functions take **total game hours** (the calendar), so they can answer the past: wave growth needs at least the 72 hours before the asked hour. Surface functions take the **surface owner's wave seconds**, read from the contract (`surface.seconds`). Consumers never build their own wave clock.

**R5. Determinism and threads.** Every contract function is pure and thread-safe: the same `(revision, position, time)` gives the same answer on the server and every client. Random consequences (a wave breaking aboard, a lightning strike, a sound) are each mod's own business and stay authoritative on the server.

**R6. Versions.** Adding a member is a minor change within a version. Removing one or changing its meaning publishes a new key (`seacontracts:<role>:v2`) beside the old one for at least one release.

**R7. Failure.** An exception, NaN or infinity counts as "no answer": the consumer uses the role's fallback (section 2) for that call and logs once.

**R8. Shader names.** Every GLSL identifier a mod injects into vanilla's shaders starts with its mod ID: `sea_…`, `roughseas_…`. Today Sea's function `seaWaves` and Rough Seas' uniform array `seaWaves` collide, so the shader does not compile with both.

## 4. Weather (`seacontracts:weather`)

**Producer:** Earth on an Earth world (priority 100). Elsewhere optionally Rough Seas' own weather model (priority 50), if hoblin chooses.

| Member | Signature | What it answers |
|---|---|---|
| `wind` | `Func<double x, double z, double hours, bool gusts, (double X, double Z)>` | the wind toward (X, Z), m/s. `gusts = false` is the steady wind that seas grow from |
| `prevailing` | `Func<double x, double z, double hours, (double X, double Z)>` or absent | the season's prevailing wind, m/s; absent means `wind` without gusts |
| `storm` | `Func<double x, double z, double hours, (double Presence, double WindMps, string Kind)>` | `Presence` 0–1, all storms together; `WindMps` the storms' own wind; `Kind` of the strongest: `squall`, `gale`, `hurricane`, or null |
| `rain` | `Func<double x, double z, double hours, (double Cloudness, double Precipitation)>` | both 0–1, vanilla's meaning of rain-cloud overlay and precipitation |
| `writesVanilla` | `bool` | true: this producer drives vanilla's wind, rain, clouds, region patterns and weather events, and no other mod may |

The `Kind` codes are Rough Seas' (`SeaState.StormCode`), so his storm consumers (lightning, the Extreme Weathers tornado bridge) map without translation.

**Today:** Sea's `SetWindProvider(name, windAt, prevailingAt)` (`src/SeaSystem.cs:174`) carries wind only, in vanilla units. Earth calls it by reflection (`EarthWeather.LinkSea`). Earth's model already knows the rest internally (`WeatherModel.Sample`: rain chance, cloud cover, pressure, storms; `WeatherStorms.Influence`), but nothing exports it.

**Sea keeps `SetWindProvider`** as a facade: when a `weather` role exists, Sea reads it (dividing m/s by 25) and the facade only matters for older Earth builds.

## 5. Vanilla's wind as a vector

Vanilla fills only `X` of `GetWindSpeedAt` with a strength. Rough Seas fills `X` east and `Z` south, the length being vanilla's strength (1.0 = 25 m/s). Earth today writes the strength into `X` alone (`EarthWeather.cs:350`); with both mods running, Earth's X and Rough Seas' Z mix into a wrong direction.

**Contract:** whoever has `writesVanilla` writes the vector: X east, Z south, length = strength.

Vanilla code that reads `X` as the strength (`DefaultShaderUniforms.Update`, `EntityBoat.OnRenderFrame`) must then be handed the vector's length for the call. Rough Seas already patches both. Exactly one mod may apply that patch, or the second one takes the length of a length. The first mod to apply it sets `api.ObjectCache["seacontracts:vanillawind-vector"]` to its mod ID; any other mod sees the key and skips its own patch.

Vanilla also reads `X` as a strength in two cosmetic places: leaf fall (`BlockFruitTreePart`) and the calm-air checks of `ModSystemAmbientParticles`. They are not in the guarded patch (Earth's `1895a7f` leaves them); with a vector they see only its east part. A later version may add them.

## 6. Sea state (`seacontracts:seastate`)

**Producer:** Sea. Rough Seas could produce it where Sea is absent.

| Member | Signature | What it answers |
|---|---|---|
| `at` | `Func<double x, double z, double hours, (double Height, double Period, double DirX, double DirZ, double WindX, double WindZ, double Storm)>` | significant wave height (m), peak period (s), unit travel direction, wind at the sea (m/s), storm presence 0–1 |
| `shelter` | `Func<double x, double z, double>` | the share of the open-sea waves that reaches here, 0–1 |
| `current` | `Func<double x, double z, (double X, double Z)>` or absent | the water's own drift at (x, z) now, m/s. Sea 0.6.0: the longshore current that breaking waves drive along a beach (Komar 1979) and the seaward flow through rip channels, 0 elsewhere. Things afloat move with it. Ocean currents and water temperature would be a later member |
| `surf` | `Func<double x, double z, (double Depth, double Breaking, double Foam)>` or absent | the coast now: depth to the sea floor in blocks (32 or more off the coast), how far the waves break there (0–1), the whitewater drawn (0–1) |
| `lee` | `Func<double x, double z, double>` or absent | the share of the sea's height that gets past the land to (x, z) now: 1 on open water, less behind islands and headlands, deep water too. Since 7 November it includes the fetch: behind land the wind's own short sea comes back sooner than its longer waves (a power of the lee, SEA-D65). `surface.height` already includes it; `shelter` (the ocean map's) does not |

**Wind without a weather owner:** `at`'s `WindX`, `WindZ` is the wind the sea follows. A consumer that finds no `weather` entry should take its wind from here rather than from vanilla's, so its sails and the waves agree. Ships does: `weather.wind`, then `seastate.at`'s wind, then vanilla's (with a warning that vanilla's is no wind model).

**Today:** Sea's `SeaAt(x, z)` returns `SeaState` (height in blocks = m, unit direction, period) and `WindAt` returns `Wind` in vanilla units (`src/Wind/Wind.cs`). `WaveFactorAt` is the shelter, 0.2–1 in 0.5.2 and 0 on inland water in the calm-rivers build.

Field by field against Rough Seas' `SeaState`:

| Contract | Sea today | Rough Seas today |
|---|---|---|
| `Height` | `SeaState.Height` | `WaveHeight` |
| `Period` | `SeaState.Period` | `WavePeriod` |
| `DirX`, `DirZ` | `SeaState.DirX`, `DirZ` | the wave generation's direction (`IDrawnSea` side), not in `SeaState` |
| `WindX`, `WindZ` | `Wind.X`, `Wind.Z` × 25 | `WindX`, `WindZ` |
| `Storm` | from height (`WindModel.StormForHeight`) | `StormPresence` |

## 7. Wave surface (`seacontracts:surface`)

**Producer:** exactly one per world, the mod whose water shader draws the swell. Sea when it is installed (our position; see section 13 for the case with both mods).

| Member | Signature | What it answers |
|---|---|---|
| `seconds` | `Func<double>` | the waves' clock now, the same on every side |
| `clock` | `string` | `elapsed` (runs while the calendar stands) or `calendar` (stands and races with it); for information only |
| `trains` | `Func<double x, double z, double seconds, double[] kx, double[] kz, double[] omega, double[] phase, double[] amp, int>` | fills the arrays (room for at least 24) with the trains running at (x, z) and returns how many. Each train is the height `amp · sin(kx·x + kz·z − omega·t + phase)` in m, before shelter. For a ship's physics |
| `height` | `Func<double x, double z, double>` | the drawn surface's height above still water at (x, z) **now**, in m, shelter and horizontal crest movement included |
| `pose` | `Func<double x, double z, double yaw, double length, double beam, (double Hold, double Lift, double Roll, double Pitch)>` | how a hull of `length` by `beam` m centred at (x, z) lies on the water now. For simple boats |

`pose` uses Rough Seas' meaning of `IDrawnSea.PoseOf`, so a vessel written for his drawn sea rides ours unchanged:
- `yaw` is the heading as the renderer turns the shape: `Pos.Yaw + rotateY · DEG2RAD`; the hull's length runs along the shape's X axis.
- `Hold` 0–1: how much of the drawn swell is under the hull (0 = none drawn: rock as before).
- `Lift` m added to the model's height; `Roll` rad about the length (the renderer's `xangle`); `Pitch` rad about the beam (the renderer's `zangle`).
- It is the water's pose, without the hull's own inertia. Ships convert to their own frame: `ShipAttitude` has pitch bow-up positive and roll starboard-down positive.

**Near a coast (Sea 0.6.0):** `trains` are the open sea's trains. The drawn surface over shallow water also follows the bottom: the trains hand over to shoreward surf trains that turn parallel to the beach, shoal, are capped at significant height 0.6 × depth and steepen into breakers. Behind land the trains are cut by the lee (`seastate.lee`, with the fetch), and over rip channels the surf is held back and breaks less. Before cliffs and walls part of each train is reflected (Seelig–Ahrens) and stands against the incoming one; the reflected part moves `height` but not the horizontal orbit. Surf crests reach the shore at the open sea's angle along it, bent by the longshore current. `height` and `pose` include all of that, so a boat in the surf rides what is drawn; a ship that integrates `trains` itself sees the open sea's waves there unless it adds the difference (Ships fits a plane to `height` less its trains under its hull).

**Sides:** all members on the server and every client, for any position. Since 7 November Sea answers `height`, `pose` and `shelter` from the asked position's own waves on clients too, so every side gives the same answer (R5). Sea's shader still draws the open sea from the camera's trains, so far from the camera the drawn water can differ from these answers by how much the sea state changes over that distance, as it already did for Ships' own ships (`docs/open-issues.md`, O1).

**Today:** `SampleWaves(WavePlace, x, z)` fills `WavePlace.Kx, Kz, Omega, Phase, Amp` (`src/Water/WaveField.cs`, up to `MaxWaves` = 24); `WaveTime` is the clock (server elapsed seconds, clients follow it); `WaveHeightAt` is the height. There is no `pose`.

## 8. Hull cut and dry spaces (`seacontracts:hulls`)

**Producer:** the `surface` owner, since only the shader that draws the water can cut it.

| Member | Signature | What it does |
|---|---|---|
| `register` | `Action<string id, Func<double[][]> hulls>` | client: a mod registers a function that Sea calls every frame before drawing water; it returns one record per hull |
| `unregister` | `Action<string id>` | |
| `registerDry` | `Action<string id, Func<double, double, double, bool> isDry>` | both sides: a mod's dry places where the world has water (no underwater fog, tint, muffled sound; creatures breathe) |
| `unregisterDry` | `Action<string id>` | |
| `isDry` | `Func<double x, double y, double z, bool>` | both sides: is any registered place dry here |

A hull record is a `double[]` with these indices:

| Index | Field | Meaning |
|---|---|---|
| 0, 1 | `X`, `Z` | the hull's origin |
| 2 | `WaterY` | the highest the water may be drawn inside the footprint (world y) |
| 3 | `Yaw` | heading, radians, as `WaterClipBox.Yaw` |
| 4, 5 | `HalfLength`, `HalfBeam` | the footprint |
| 6 | `CentreOffset` | the footprint's centre along the keel |
| 7, 8 | `SlopeAlong`, `SlopeAcross` | the waterline's rise per m toward the bow and toward starboard (pitch and roll) |
| 9 | `Dry` | 1: cut the water inside the hull too, not just its surface; 0: surface only |
| 10, 11 | `DryDepth`, `DryHeight` | how far the dry hull reaches below and above the waterline |
| 12 | `FloodY` | world y of the water flooding her, or NaN for none: below it the sea is not cut |
| 13–28 (optional) | `Profile` | 16 half-widths from stern to bow; absent, or NaN at 13, means `HalfBeam` all along |
| 29 (optional, with 13–28) | `CalmSlopeAcross` | the rise per m toward starboard of the level the waves beside her settle to (her roll without her lean in the wind); NaN: `SlopeAcross` |
| 30 | `CalmZone` | m around the footprint over which the waves die down to that level; 0: the owner's default |
| 31 | `DryInset` | the footprint's margin around the hull: a dry hull's outline is the footprint less this |
| 32 | `DryRake` | the stern's rake: m along the keel per m up (negative: a transom leaning out aft) |
| 33, 34 | `DryLevel0`, `DryLevelStep` | the detailed dry outline's lowest level above the waterline and the step between levels |
| 35–162 (optional) | `DryProfile` | the dry outline in detail: 4 levels of 32 half-widths, the lowest first, stations from stern to bow over the footprint less the inset; NaN at 35: none |
| 163–178 (optional, with 35–162) | `DryTops` | how high the cut reaches above the waterline at 16 stations from stern to bow over the same span (the rail there: a raised bow or stern castle stands higher than the waist); NaN at 163: none, `DryHeight` all along. *Since 8 November* |

A hull cut owner publishes `recordLength` (`int`), the longest record it reads; Sea's is 179 (163 before 8 November, without `DryTops`). A mod hands `DryTops` only to an owner whose `recordLength` takes them. A mod hands the detailed record (29 values or more) only to an owner that publishes it; any other owner gets the first 13 or 29, so a dry hull's record there is the footprint already less the inset.

**Today:** `AddWaterClipSource(IWaterClipSource)` with `WaterClipBox` (`src/Water/WaterShader.cs:281`), and `AddDrySpaces(IDrySpaces)`. The typed form stays. Since 7 November the record carries the same detail (dry outline, rake, inset, calm zone): read back through Sea it is the typed box field for field, and it cuts the same. Rough Seas' boats today use vanilla's `hidewatersurface` lid (a mesh drawn into depth just before the water); that keeps working, and registering a record is an optional upgrade for a hull that tilts or floods.

## 9. Ship motion stays in the ship mods

The contracts give the water. Each ship mod keeps its own answer to it:
- **Ships** keeps `ShipMotion`: each train integrated over the waterplane, encounter frequency, separate heave/pitch/roll periods and damping, sail steadying, wind heel and wave yaw. It reads `surface.trains` through a small adapter instead of `Sea.WavePlace`. Where a `surface` owner gives no `trains`, Ships falls back to `pose`.
- **Rough Seas' vessels** keep `HullResponse` on top of `pose`.

## 10. Later, not in version 1

- **Masts for lightning:** ships publish their masthead positions so a storm's lightning (Earth's or Rough Seas') can seek them; Rough Seas has `IMasted` for its own vessels.
- **Rams** between vessels of different mods.
- **Ocean currents** drifting unsteered ships of any mod. Sea 0.6.0 publishes only the surf zone's currents (longshore and rips, `seastate.current`); vanilla boats and Ships' ships and rowboats drift with them.

## 11. What Rough Seas would do (all optional)

| Rough Seas today | With the contracts |
|---|---|
| Always builds its own weather model (`SeaWeatherSystem.CreateModel`) | Registers it as `weather` at priority 50; when Earth holds the role, reads wind, storms and rain from it at that one place, and its vanilla writers (wind event, region patterns, weather events, rain, cloud drift) stay off |
| Writes vanilla wind as a vector and patches the X-as-strength readers | Unchanged; sets `seacontracts:vanillawind-vector` so Earth skips its own patch |
| Draws its own swell; `IDrawnSea` answers from it | On a world where Sea owns `surface`: doesn't inject its shader, and `IDrawnSea` answers from `surface.height` and `surface.pose`, so his vessels ride Sea's waves with their own `HullResponse` |
| Boats hide water with vanilla lids | May register hull records for a cut that follows the tilt and the flood |
| Wave growth from fetch, depth and duration | Unchanged, and a model for Sea's own growth (Sea grows height from wind strength and coastal shelter only) |

His metres per block come from the world's `polarEquatorDistance`; on an Earth world that must match Earth's map scale.

## 12. Our work items

**Sea**
1. Publish `seastate`, `surface` and `hulls` in `api.ObjectCache`; read `weather` when present. The typed API stays. *Done in 0.6.0-pre.1.*
2. Rename injected GLSL identifiers to `sea_…` (R8). *Done in 0.6.0-pre.1; Wmap 0.5's far programs keep their old names, which Sea still sets.*
3. Per-position `height` and `trains` on the client, not the camera's. *Done 7 November: `trains`, `height`, `pose` and `shelter` are per position on every side. The drawn water away from the camera is still the camera's trains (O1).*
4. Add `pose`. *Done in 0.6.0-pre.1.*
5. Separately: wave growth from fetch, depth and duration; crest spray particles; less geometric foam. *Crest spray done 7 November (open sea, SEA-D69).* *0.6.0-pre.1 adds waves on beaches (refraction, shoaling, depth-limited breaking, breaker shape, surf whitewater and spray, swash), the lee of land, rip channels and the surf's currents, which move boats. 7 November: reflection off steep shores, crests meeting the shore at an angle and bent by the longshore current, swash per shore point, and fetch behind land within the coast map's 64 blocks. Growth from depth, and fetch at world scale (km), are not done: they need world-scale land and depth both the weather and the sea agree on.*

**Earth**
1. Publish `weather` (priority 100, `writesVanilla` true): export storm presence, kind and rain from its model.
2. Write vanilla's wind as a vector, with the reader patch guarded by `seacontracts:vanillawind-vector` (section 5).
3. Keep calling `SetWindProvider` for older Sea builds.

**Ships**
1. Read the waves through a trains adapter (section 9). *Done on `claude/sea-contracts` (`31d1668`): Sea's typed API when Sea is loaded, the contracts otherwise; the `pose` fallback followed in `aa1c411`, and `0946ceb` moved the Sea-typed code into a bridge loaded only with Sea, so Ships loads without `Sea.dll`.*
2. Keep registering hulls through Sea's typed API. *Without Sea, Ships hands the detailed record to an owner that publishes `recordLength`, the plain one to any other.*
3. Read wind and sea state through the contracts, so a Ships build can run where another mod owns them. *Done on `claude/sea-contracts`: without Sea's bridge the wind comes from `weather.wind`, then `seastate.at`'s wind, then vanilla's with a warning in the log, in chat and on the wind panel. A development switch (`ModConfig/ships-dev.json`, `{ "SeaBridge": false }`) runs Ships through the contracts alone with Sea installed, as another mod's boat would.*

## 13. Open questions

1. **Both Sea and Rough Seas installed:** who owns `surface`? Our position is Sea. The alternative is two separate setups that share only `weather` and `seastate`.
2. **Wave clock:** Sea's waves run on elapsed time (they move under `/time stop`); Rough Seas' run on the calendar. The contract only requires one clock per world; which feels right for ships is a play-test question.
3. **Earth's storm kinds:** what makes an Earth storm a `squall`, `gale` or `hurricane`, and how Earth's rain boost becomes 0–1 precipitation.
4. **Exclusive vanilla writer:** does hoblin accept turning his vanilla writers off when Earth holds `weather`?
5. **License of this document:** offer the contract text itself under CC0 or MIT, so hoblin can implement it freely. Sea's code is GPL-3.0; calls through delegates between separate mods do not mix code.
6. **A shared contracts DLL instead of `ObjectCache`:** rejected for now, because two mods shipping the same assembly risk load conflicts and Rough Seas aims to depend on no other mod.

## 14. How we would know it works

1. Earth + Sea + Ships behave exactly as before (regression).
2. A test boat that uses only the `ObjectCache` contracts rides Sea's waves and gets its water cut, without referencing `Sea.dll`. *7 November, Ships' server self-test `sea-contracts` (`tools/run-sea-contracts.ps1`): with the real Sea on a dedicated server, at 507 places (421 on open sea), Ships' contract reader gets exactly what Sea's typed API gives (trains, height, shelter, pose, current, sea state: no difference at all), and a dry place registered through `hulls` is dry in Sea's own answer. Offline (HansaTests `sea-bridge`), a detailed hull record read through Sea's `ToClip` is Ships' clip field for field and cuts the same. *In game, 7 November (AgentBridge bench, singleplayer, one client): with the development switch off, Ships' caravel ran on the contracts alone. Its hull was cut through `seacontracts:hulls`, the deck was dry seen from above, and the wind panel showed Sea's wind (force 8 from the north-west) from `seastate.at`. With the bridge on it behaved as before. About 60 fps (the cap) at open sea, at the shore and aboard. Multiplayer is not checked.*
3. Earth + Rough Seas: his waves build behind Earth's storms, and only Earth writes vanilla weather.
4. Dedicated server and two clients: the same `trains`, `height` and `pose` at the same place and time on all three. *In game, 7 November, one client: `/sea probe` (server) and `.sea probe` (client) gave identical height, shelter, pose and trains on open sea and at 21 of 21 fresh shore points. That held only after a fix: the server solved coast tiles only where something sampled, so a first ask at a shore answered as open sea there (SEA-D68). `.sea drawn` measured the drawn water against the per-place answer: 0.0000 m apart out to 512 blocks, also at force 11, because a uniform wind gives every place the same trains (O1 can only show where the wind varies across space, as on Earth). Two clients and an Earth world are not checked.*
