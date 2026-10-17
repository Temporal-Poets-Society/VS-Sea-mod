# Vintage Story 1.22 engine notes (what Sea relies on)

Read from the decompiled 1.22.7 DLLs (`VintagestoryAPI`, `VintagestoryLib`, `VSEssentials`, `VSSurvivalMod`). If a
game update breaks something, look here first. Water shader facts (how waves are drawn, camera-relative positions)
are in Magelan's `docs/engine-notes.md`.

## Wind

- **All wind lookups** go through `IBlockAccessor.GetWindSpeedAt(pos)`. The client and server world maps create an
  empty `Vec3d` and fire `IEventAPI.OnGetWindSpeed(pos, ref windSpeed)`; handlers run in subscription order.
- **Vanilla's handler** (`WeatherSystemBase.Start`) sets only `windSpeed.X`:
  - It uses `WeatherDataSlowAccess.GetWindSpeed(pos)`, a bilinear blend of the four nearest 512-block weather
    regions' `GetWindSpeed(posY)`.
  - Each region's value is its wind pattern's `Strength`: still 0, light 0.15, medium 0.3, strong 0.6, storm 1.
  - Above sea level it's × max(1, 0.9 + (y − sea) / 100), capped at 1.5. Below sea level it's ÷ (1 + (sea − y) / 4).
- **Sea subscribes in `StartServerSide` / `StartClientSide`.** Those run after every mod's `Start`, so Sea's handler
  comes after vanilla's and has the last word.
- **X is "the strength" everywhere in vanilla.** Sea therefore keeps the strength in X and keeps the direction in its
  own API:
  - `DefaultShaderUniforms`: `WindSpeed = CurrentSurfaceWindSpeedClient.X`,
    `WaterWaveIntensity = 0.75 + X * 0.9`, and the wind wave counters.
  - `ModSystemAmbientParticles`, `BlockFruitTreePart`, `BlockBamboo` (its sign) and `EntityBoat` speed.
  - Body temperature uses `Length()`.
- **`GlobalConstants.CurrentWindSpeedClient` / `CurrentSurfaceWindSpeedClient`:**
  - `WeatherSystemClient.OnRenderFrame` smooths `GetWindSpeedAt` at the player (and at the rain map height) toward
    the lookup at rate dt.
  - It adds a small Z wobble (× X).
- **Vanilla's sailboat** (`EntityBoat`) takes a sail angle from `atan2(CurrentWindSpeedClient.X, .Z)`, i.e. it
  assumes wind along +X.
- **The wind sound** (`WeatherSimulationSound`) reads the region's `weatherData.curWindSpeed.X`, not the event:
  - `WeatherSimulationRegion.TickEvery25ms` moves it toward `GetWindSpeed(seaLevel)` by at most 0.001 per tick.
  - Sea postfixes `WeatherSimulationRegion.GetWindSpeed` (region centre = (regionX + 0.5) × RegionSize), so the
    sound follows too.
  - The region keeps `ws` protected; Harmony's `___ws` reaches it.
- **Rain, snow and hail** (`WeatherSimulationParticles.asyncParticleSpawn`) take
  `windSpeed = GetWindSpeedAt(pos).ToVec3f()` and use X and Z for particle velocities.
  - Sea transpiles that one `ToVec3f()` call into `Hooks.RainWind`, which turns the strength into Sea's direction
    at the player.
  - `parentVeloSnow` uses max(0, …) on X and Z, so snow's extra parent velocity only applies toward +X / +Z.
  - Rain drops move at 8 × that vector (downwind), with no extra wind push (`WindAffectednes` 0).
  - **The streaks' slant** is drawn by `particlesquad.vsh`, not by the drops' motion. For rain (render flags < 0) it
    turns the quad by `rotationZ(acos(dot(u.xy, (0, -1))))` about the world Z axis, where u is the drop's direction
    (`particleDir`, the normalised velocity packed into signed bytes). That's the right angle from vertical but
    always toward +X, so every streak leans east whatever way the drops go. Sea swaps that line for
    `mvmat[1] = modelViewMatrix * vec4(-u, 0.0)` (`RainShader`): vanilla's billboarding then resets only the x and z
    axes for rain, so each streak lies along its drop's path.
- **Clouds:** both renderers (FluffyClouds `CloudRendererMap`, and the old `CloudRenderer`) drift in `CloudTick`.
  - Every 20-120 s (`windChangeTimer` against `capi.ElapsedMilliseconds`) they pick a random `targetCloudSpeedX`
    of 0..5 and a Z of 0..0.5.
  - They ease `cloudSpeedX` toward `target + 3 × WeatherDataAtPlayer.GetWindSpeed(y)`.
  - Sea's prefix keeps the timer from firing and sets the targets to its own drift, minus that 3 × strength on X.
- **Weather commands:** `/weather setw` sets a region's wind pattern. With Sea on, that no longer shows because the
  lookups are overridden.

## The wave clock

- The game calendar isn't a good clock for waves: clients get `TotalSeconds` as whole game seconds every 5 s (and
  the full calendar every 60 s) and jump to it, and sleeping speeds it up.
- **The calendar moves in steps:** `ServerSystemCalendar` ticks it every 200 ms (`GetUpdateInterval`). It sends
  clients an update every 5 s and a full packet every `MagicNum.CalendarPacketSecondInterval`. So `TotalHours` can't
  tell the game hour of a wave sample. Sea's server publishes anchors for that (`SampleHours`, 0.3.6).
- Sea's wave clock is the server's `World.ElapsedMilliseconds` (unpaused running time). The server sends it to each
  player every 5 s plus half of `IServerPlayer.Ping` (a round trip, in seconds). Clients run it on
  `capi.InWorldEllapsedMilliseconds` (stops while paused, like the server's in singleplayer) and ease toward the
  server's by at most 2% of the time passed. They snap when more than 1 s off.
- On the client the wave time is read once per frame, in an `EnumRenderStage.Before` renderer at order 0.05, after
  the camera moved (0) and before Magelan's ships (0.1) and the water renderer (Opaque).

## Under water, and dry places

- **The camera:** `ClientMain.EyesInWaterDepth()` and `GetEyesInWaterColorShift()` read the block at
  `MainCamera.CameraEyePos` (the eye, world position; `MainCamera` is a public field, `CameraEyePos` a public field
  of `Camera`). They drive everything underwater for the viewer:
  - `SystemPlayerEnvAwarenessTracker` copies them into the player properties each tick. `AmbientManager` turns
    those into fog colour, density and ambient, and into the shaders' `cameraUnderwater` uniform.
  - `SystemPlayerSounds` plays the underwater sound, and `ClientMain` / `SystemSoundEngine` muffle sounds.
  - Sea postfixes both functions: 0 when the eye is in a dry place.
- **Breathing:** `EntityBehaviorBreathe.Check()` (public, server only; it returns on the client) looks at the block at
  pos.Y + EyeHeight (SwimmingEyeHeight when swimming) plus LocalEyePos.X/Z, layer MostSolid. Solid blocks and liquids
  above the eye's height within the block take the air away. Sea's postfix gives the air back when the block there
  is a liquid (or air) and the eyes are in a dry place.
- **Riders:** the player physics sets `Swimming` (and `FeetInLiquid`) false for anyone mounted, so a ship's crew is
  never swimming, never wet from it (body temperature adds wetness only while swimming), never gets swim sounds.
- **Chickens** (`game:chicken-rooster` / `-hen`) have the breathe behavior; Sea's self-test drowns one.

## Shaders: how the game builds them

- `ShaderRegistry.LoadShaderProgram` (Sea's postfix edits the code here) reads `shaders/<pass>.vsh/.fsh` and expands
  `^#include <file>` lines from `shaderincludes/` and `shaders/`, by lower-case file name, each file once.
- It then adds `#define` lines as prefix code. Fragment: FXAA, SSAOLEVEL, NORMALVIEW, BLOOM, GODRAYS, FOAMEFFECT,
  SHINYEFFECT, SHADOWQUALITY, DYNLIGHTS, USEOIT. Vertex: USESSBO, WAVINGSTUFF, FOAMEFFECT, SSAOLEVEL, NORMALVIEW,
  SHINYEFFECT, GODRAYS, MINBRIGHT, SHADOWQUALITY, DYNLIGHTS, MAXANIMATEDELEMENTS (230).
- `ClientPlatformWindows.CompileShader` puts the prefix on the line after `#version`.
- `USEOIT` is 1 unless the program says otherwise (`ShaderProgramBase.Oit` defaults to true). Without it
  `oit.fsh` doesn't define `OIT(...)` and the water and particle fragment shaders don't compile.
- `ShaderProgram.Compile()` returns false on a failed compile or link (the error is logged by the platform) and
  keeps nothing else. Sea postfixes it to notice its own water code failing and step back.
- A water pass's vertex and fragment stages share uniforms by name: Sea declares the same `seaClip*` arrays, with the
  same initialisers, in both.
- The water pass (`chunkliquid.vsh`) already hands the drawn position to its fragment stage as `fWorldPos`. Sea adds
  its own `seaFragPos` to both water passes, since the depth pass has none.

## Oceanity (sheltered water)

- The water shader divides the waves by 5 × max(0.2, 1 − oceanity). Oceanity is packed into each water vertex's
  flags (bits 2-9) by the chunk tesselator: `ClientWorldMap.LoadOceanityCorners` reads the region's `OceanMap` at the
  chunk's four corners with `IntDataMap2D.GetIntLerpedCorrectly(x, z)` (x = (chunkX·32 mod regionSize) / regionSize ×
  InnerSize, the next corner +1), and `ChunkTesselator` blends them across the chunk (`BiLerp`, cast to a byte). A
  vertex whose neighbours aren't the same liquid gets 0.
- The server has the same map (`IBlockAccessor.GetMapRegion(rx, rz).OceanMap`), so `SeaSystem.WaveFactorAt` gives
  both sides the same factor. No ocean map (or not loaded) means 0.2, as the flags would be 0.

## Latitude, seasons, calendar

- **`IGameCalendar.OnGetLatitude(z)`** returns −1 at the south pole, 0 at the equator and 1 at the north pole.
  - Vanilla (`GenMaps`) sets it on the server at worldgen init, and on clients when the `latitudedata` packet arrives.
  - With realistic climate it's a triangle wave in z (`polarEquatorDistance`, `ZOffset`); otherwise it's always 0.5.
  - Cylinder World (with its polar climate on) replaces it on both sides with (EquatorZ − z) / PoleDistance.
    North is small z.
- **Calendar values** are pure functions of `TotalDays`:
  - `YearRel = mod(TotalDays, DaysPerYear) / DaysPerYear`; 0 is the start of January.
  - `DaysPerYear = DaysPerMonth × 12`.
  - `TotalHours` is synced by vanilla.
- **Cylinder World's ring:** `CylinderWorldCore.Params` (read by reflection) has `Circumference`
  (= WorldWidth − 2 × JumpInset) and `JumpInset`. They're set at `ServerRunPhase.WorldReady`, so Sea builds its
  model at `RunGame`.

## Chat commands

- A command can have both a handler and subcommands: `ChatCommandImpl.Execute` runs a matching subcommand, otherwise
  the handler.
- Parsers include `Word`, `OptionalWord`, `Double`, `OptionalDouble`, `Float`, `OptionalFloat` and `WordRange`.

## Water shader

- **Vanilla's warp:** both water passes call
  `applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div)` for animated liquids.
  - `div` is 5 × max(0.2, 1 − oceanity), or 90 for "weak wave" liquids.
  - Sea replaces that one call with `seaWaves(...)`, which sums its wave trains and divides by `div` like vanilla.
    It falls back to vanilla's function for lava and when no waves are uploaded.
  - The second call in chunkliquid (not animated but weak waves, divisor 90) stays vanilla's.
- **Waves regardless of the setting:** vanilla's warp only runs with the "waving stuff" graphics option on
  (`#if WAVINGSTUFF == 1`). Sea's waves run either way.
- **`applyPerceptionWarping`** (the drunk effect) is defined outside that `#if`, and Sea still calls it.
- **Through 0.3.7 normals weren't changed.** Since 0.3.8-pre.1, celestial reflection shading interpolates the analytic
  gradient of Sea's waves. Packed normals still select top/side faces and vanilla foam/lighting behavior.
- **Phases:** `worldPos` is camera-relative, so each wave's phase is uploaded for the camera position (computed in
  doubles on the CPU relative to an anchor near the player). The shader only works with small numbers.
- **Hull clip:** it uses the unwarped position (`xyz + origin`) to find the footprint and to calm the waves around it.
  The clip's height is a plane (height at the hull's origin, rise per block along the keel and across it) measured
  level from the origin, so a pitching, rolling hull clips along its own tilted waterline.
- **Uniform arrays:** `seaWave[24]`, `seaClipA/B/C[8]`, `seaClipP[32]`, all vec4.

## Splashes in dry places

- **Walking in water makes particles:** `EntityPlayer.OnAsyncParticleTick` calls `EntityAgent.SpawnFloatingSediment`
  whenever the player is `FeetInLiquid` or `Swimming` and moving at all (`Pos.Motion.LengthSq() > 1e-5`). That spawns
  `Entity.SplashParticleProps` (they look like bubbles), floating sediment, and `SpawnWaterMovementParticles`
  (bioluminescent plankton where it's warm). A player mounted on a moving ship always has motion, so in a dry hull
  they'd splash all the time. `Entity.OnCollideWithLiquid` (server) makes the splash sound and particles on entering.
- Sea skips both with a prefix when `IsDry` holds at the feet. `AirBubbleParticles` is registered but nothing spawns it.
- **Water specks:** `ModSystemAmbientParticles.AsyncParticleSpawnTick` (VSEssentials) spawns up to `particleLevel / 2`
  motes a tick round the player (±16 across, ±10 up and down) wherever the block is a full liquid: `liquidParticles`, a
  0.1 glowing cube, two seconds, `ShouldDieInAir` (it dies only where the block isn't liquid: `ParticleGeneric.TickNow`).
  In a hull below the waterline, the world's sea, so they float in a dry hold ("water bubbles in the cargo hold").
  Particles aren't cut by the water shader. Still water blocks have no particles of their own (`saltwater.json`).
- **Rain on the water:** `WeatherSimulationParticles.SpawnRainParticles` and `SpawnDustParticles` (a storm's spray) put
  splashes where the rain map's top block is liquid: inside a hull, the sea's level under her decks.
- Sea routes those methods' `IAsyncParticleManager.Spawn` calls through `DryParticles` (transpilers): a mote that dies in
  air in a dry place, a splash or spray (spawn box up to 2 each way) whose middle is dry, isn't spawned. The rain and
  storm dust (spawned over tens of blocks), summer air motes and fireflies are judged by nothing.

## Fragment uniforms

- The dry cut's detailed outlines take 128 vec4 (4 hulls × 32) on top of the clips' 104 vec4 in the water fragment
  shaders, besides vanilla's own. OpenGL 3.3 guarantees 1024 components (256 vec4); desktop drivers give 4096 or more.
  If a driver refuses, Sea's fallback (no dry cut) takes over. The GPU check (`tests/ShaderCheck`) links them here.
- **A flooded hull (0.3.3)** needs no new uniform: `seaClipB.y` was unused. It's the flood's height over the clip's water
  y (`WaterClipBox.FloodUniform`), -10000 when there's none; the fragment test skips the clip where `p.y - b.z` is under
  it, before anything else of the hull. The vertex stage's hull clip doesn't read `.y`. The C# twin compares in single
  precision like the shader (`(float)dy < FloodUniform`); the GPU check's heights stay clear of the flood's so a
  rounding difference there can't show.


## Celestial water and outdoor night presentation (0.3.8-pre.1)

The source audit, exact installed AstraTerra 0.15.0 package hash, engine/astronomy type and shader references, implementation boundaries and verification distinctions are in [sky-water.md](sky-water.md). The engine sky lookup does not capture stars or the moon. Source cubemap/projected-star data avoid camera-frustum dependence; full graphical-client comparisons remain pending. Night presentation uses the existing propagated sunlight channel, with a floor and a narrowly detected local-moon correction, without changing gamma, exposure or calendar state.
