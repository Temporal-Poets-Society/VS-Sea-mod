# Sea code review, 14 November 2026

A fresh review of Sea at `main` `b1ead5a` (0.6.0-rc.4): structure, cleanness, performance, maintainability and
correctness risks. It follows the 0.4.2 review (decision SEA-D72, backlog SE-0013 to SE-0026) and the work since:
coasts and surf, the open sea contracts, one train loop (SEA-D74), Sea's own fade distance and the shader-mod guard
(SEA-D75), and the SheyderMod reflection handover (SEA-D76). Read-only: no code was changed. Findings worth doing are
filed as backlog items SE-0036 to SE-0051 (status `new`).

## How it was checked

- Read in full: `SeaSystem.cs`, `Water/WaveField.cs`, `Water/WaterShader.cs`, `Water/WaterRenderer.cs`,
  `Game/Hooks.cs`, `Contracts/SeaContracts.cs`, parts of `Contracts/ContractMath.cs`. Two helper passes read the
  coast, boats, wind, wakes, swash and particles code, and the celestial renderer and shader, commands, self-test and
  the test harnesses. Their findings were checked against the code before they went in; the few that were not are
  marked "not checked".
- Engine facts against `devtools/vs-ref/decompiled/1.22.7` (server map regions are a `ConcurrentDictionary`, so the
  shelter lookup from contract callers on other threads is safe).
- Runs on this PC (AMD RX 6600), 14 November:
  - `dotnet build Sea.csproj -c Release` (to a scratch zip): 0 warnings, 0 errors.
  - `dotnet run --project tests/SeaTests`: **ALL OK** (361 checks, the SE-0013 to SE-0026 regression checks among them).
  - `dotnet run --project tests/ShaderCheck -- --vertex-cost`: **5 FAILED**, all against World Map's current tree
    (finding 1). Vertex cost, a million vertices, 24 trains: open sea 0.63 ms in one loop (0.79 apart), coast
    10.26 ms (13.16 apart).
  - A scratch benchmark of the CPU surface (outside the repo; `WaveField.cs` and `CoastMath.cs` linked, 24 storm
    trains, a constant coast sample, Release): `WavePlace.HeightAt` 3.8 µs open sea, 20.3 µs on a coast;
    `ParameterAt` 14.4 µs and `SurfaceAt` 4.9 µs on a coast; `SeaContractMath.PoseOn` (4 heights) 74.5 µs on a coast.

## The earlier review's fixes still hold

| Item | Where it lives now | Holds? |
| --- | --- | --- |
| SE-0013 client `WaveHeightAt` is the place's own surface | `SeaSystem.WaveHeightAt` → `ProbeAt` (`SeaSystem.cs:592`); `DrawnHeightAt` for `.sea drawn` | yes |
| SE-0014 sample mixing across a change | odd/even `revision`, `Change`, `StableRevision` (`SeaSystem.cs:355-377`); retries in `WavePlace.Sample` (`WaveField.cs:374-410`) | yes |
| SE-0015 fixed winds by wave sample | `WindAt(x, z, hours, sample)`, `NowSample` (`SeaSystem.cs:187-211`) | yes |
| SE-0016 per-pass wave injection | `Hooks.PassWaves` (`Hooks.cs:303-318`) | yes |
| SE-0017 vanilla wind only on the main thread | `VanillaWind`, `RefreshVanillaWind` (`SeaSystem.cs:225-254`) | yes |
| SE-0018 listeners off on dispose | `Listen`, `Dispose` (`SeaSystem.cs:117-175`; `GameWorldSave` kept on purpose) | yes |
| SE-0019 calendar shape change rebuilds the model | `OnParams` (`SeaSystem.cs:1001`) | yes |
| SE-0020, SE-0021, SE-0025, SE-0026 | `WindOverride.Change`, `BlendCorners`, `Inputs`, `blendValid` | yes; `ReviewChecks` passes |
| SE-0024 one train loop | `sea_KeepSurface` / `sea_Kept` (`WaterShader.cs:90-94, 244, 292-327`) | yes; ShaderCheck: kept surface = surface alone within 8.9e-8 |

One thing those fixes did not reach: SE-0014 made the *shared* wave inputs safe across threads, but a single
`WavePlace` is still not, and vanilla boats' places are sampled from two threads (finding 2).

## Findings, most severe first

Severity: **high** (a release check is red, or a data race on a live path), **medium** (a real defect in an edge
case, or cost/maintenance that will bite), **low** (cleanness, small risks).

### 1. High: ShaderCheck is red against World Map 0.5.3, and the far-water contract is no longer checked

- **Evidence:** `ShaderCheck` fails 5 checks (both settings): "Wmap working tree: Sea's distant-sea sky reflection
  goes in", "Wmap distant terrain … anchors not found", and "far glint comparison program compiles"
  (`'seaWeather' : undeclared identifier`). `tests/ShaderCheck/Program.cs:114` decides that World Map is 0.5 or later
  by looking for `sea_NightFloor` in `farterrain.fsh`. World Map 0.5.3 (`Wmap@1c03c07`) declares the far-water
  uniforms in its includes (`wmapfaralbedo.ash`, `wmapfarshade.ash`) and under the names before SEA-D57
  (`seaNightFloor`, `seaWeather`, `seaWave`…). So the check takes the pre-0.5 path and tries to patch a shader Sea
  deliberately leaves alone; the 0.5 path (all far uniforms live in `farterrain` and `farcells`) never runs.
  `FarGlintMatchesNear` (`Program.cs:292-312`) pastes World Map's glint code after Sea's declarations, which now say
  `sea_Weather`. The same rename left `tests/SkyIntegrationCheck/Program.cs:136` asserting `seaNightFloor` in Sea's own
  patched output. The card and SEA-D75 say ShaderCheck passes; with today's World Map it does not.
- **Fix:** detect World Map 0.5 by its includes and either name (`sea_X` or `seaX`, as `WaterShader.Has` does); in the
  glint comparison declare the legacy names too (or `#define seaWeather sea_Weather`); assert `sea_NightFloor` in
  SkyIntegrationCheck. Then agree with World Map whether its far shaders move to the `sea_` names, so the legacy branch
  can go one day.
- **Risk:** test code only. Until it is fixed, a change that breaks World Map's far sea is not caught.
- **Item:** SE-0036.

### 2. High: vanilla boats' wave pose is worked out on two threads at once

- **Evidence:** the client's particle thread (`ModSystemAmbientParticles.AsyncParticleSpawnTick`, patched by
  `DryParticles`) asks `Hooks.ClientDryWithin` → `SeaSystem.IsDryWithin` → `NativeBoats.IsDry`
  (`NativeBoats.cs:87-92`; `IsDryWithin` defaults to it) → `Clip(b)` (`:54-64`) → `BoatWaves.ApplyToClip` →
  `TryGetPose` (`BoatWaves.cs:53-75`). `TryGetPose` writes the boat's cached `State` (time, position, fill, pose) and
  resamples its `WavePlace` (`sea.SampleWaves(s.Waves, …)`), the same object the render thread updates from
  `RenderBoat`, `ModelMatrix`, `SelectionTransform` and `AddWaterClips` every frame. `WavePlace` is not thread-safe:
  its `blended`, `order` and train arrays are rewritten in place, with an `Array.Sort` among them. A torn sample gives a
  wrong pose or dry cut for a frame; an exception is swallowed by `IsDryWithin`'s `catch` (finding 10), so it would
  never be seen. It needs a vanilla boat afloat near the player while ambient particles spawn, which is ordinary play.
- **Fix:** the particle path must not sample: publish each frame, on the render thread, an immutable snapshot of the
  boats' `WaterClipBox`es (a volatile array), and let `NativeBoats.IsDry` read only that; or make `TryGetPose` return the
  cached pose without resampling when it isn't on the main thread.
- **Risk:** low; the dry test then lags a frame, which the particle path's "dry within the next seconds" allows for.
- **Item:** SE-0037.

### 3. Medium: leaving a world does not switch World Map's far sea off

- **Evidence:** `WaterRenderer.SwitchOff` (`WaterRenderer.cs:426-459`) checks each uniform with `WaterShader.Has`,
  which also accepts the legacy name, but then sets it through `GL.GetUniformLocation(id, "sea_WaveCount")`,
  `"sea_NightFloor"`, `"sea_SkyReflect"` and so on, the new names only. World Map's `farterrain` and `farcells` declare
  `seaWaveCount`, `seaNightFloor`, `seaSkyReflect`: the location is -1 and `glUniform` ignores it. The game keeps its
  compiled programs between worlds, so a world joined next with World Map and without Sea draws the far sea with Sea's
  last waves, glints and night floor. `SwitchOffOnce` (`:397-399`) already handles the legacy wave count; `SwitchOff`
  was not brought along. SEA-D75's check ("a world without Sea after one with Sea draws vanilla water") looked at the
  near water.
- **Fix:** resolve every name with `WaterShader.N(prog, …)` in `SwitchOff`, through one helper used by both paths.
- **Risk:** low. Check on the bench with World Map installed: a world with Sea, then one without.
- **Item:** SE-0038.

### 4. Medium: some failure switches outlive the world they happened in

- **Evidence:** `CelestialRenderer` resets `ambientFailed` and `usesFailed` per renderer (`CelestialRenderer.cs:57`),
  but `twilightFailed` and `handOffFailed` (`:284`) are static and never reset: after one error, real twilight and the
  shadow/shading hand-off stay off in every later world of the session, with no word in that world's log. In `Hooks`
  (`Hooks.cs:337-344`) `WaterCodeLevel`, `CelestialDisabled`, `CelestialFarDisabled` and `FlowLookDisabled` also last
  for the process. That is right for a driver that can't compile Sea's code, but a failure caused by another mod's
  shader in one world also sticks after that mod is gone.
- **Fix:** reset the celestial flags in the constructor (or make them instance fields reached through `current`). Keep
  the shader step-down levels per process, but say so once in each world's log (`.sea` already shows them), or reset
  them when the set of shader mods changes.
- **Risk:** low.
- **Item:** SE-0039.

### 5. Medium: the coast map's bookkeeping grows and copies more than it needs

- **Evidence:**
  - `CoastField.wanted` gets every missed tile (`CellLocked`, `TileData`, `Ask`; `CoastField.cs:97-127`) and loses one
    only when it is solved or fails. Tiles whose chunks never load (where the camera was while travelling; negative
    chunk coordinates, for which `Scan` returns null) stay. Every 50 ms `Work` copies the whole set into a new array
    and checks up to 25 chunk columns per entry, taking the lock per column (`:153-186`); the set's hash order lets
    stale far tiles take the single solver slot before tiles in view.
  - Every coast lookup allocates a closure (`CoastMath.Bilinear(x, z, (i, j, into) => …)`, `:79-84`) under one global
    `gate` lock, called per sample by ships, boats, crest spray (up to 600 tries at 10 Hz) and surf spray from several
    threads. `Window` (`:242-252`) does about 25,600 tuple-keyed dictionary lookups under the same lock.
  - `CoastTexture.Update` (`CoastTexture.cs:36-55`) copies and uploads the whole 256 × 256 map (4 texels a column,
    about 4 MB) whenever `CoastField.Version` changes, and `Version` changes for any tile solved anywhere and when
    columns are forgotten; while a tile in view is missing it also re-uploads every 2 s.
- **Fix:** keep `wanted` with the time each key was last asked for and drop keys not asked for in a few seconds, and
  take the nearest to the last asker first; inline the four corners in `Lookup` (no delegate) and, since tile arrays are
  never changed after they are made, read them from a copy-on-write map without the lock; in `Window` look up the 25
  columns once under the lock and copy outside it; upload only the tiles that changed (per-tile version,
  `TexSubImage2D` of a 32 × 32-column rectangle) and don't bump the version when columns are forgotten.
- **Risk:** medium (locking changes); SeaTests' `CoastChecks` and the server/client probe equality (`/sea probe`,
  `.sea probe`) guard it.
- **Item:** SE-0040.

### 6. Medium: the CPU surface on a coast costs five times the open sea's, much of it discarded

- **Evidence:** benchmark above: `HeightAt` 20.3 µs on a coast against 3.8 µs at open sea; a contract `pose` is five
  such answers (shelter and four heights), about 100 µs; `ParameterAt` takes 14.4 µs of each. In
  `WavePlace.Displacement` (`WaveField.cs:801-816`), called by every one of up to 24 inverse iterations and three
  times per Newton step (`:774-797`), `CoastAt` runs `CoastMath.Breaking` and `CoastMath.LongshoreAt` and throws the
  results away (it needs only `cap` and `reach`). `SeaSystem.ShelteredHeight` (`SeaSystem.cs:613-617`) calls
  `SurfaceAt`, which works out the horizontal displacement only to take `.Y`. Boats, ships, spray, the contracts'
  `height`/`pose`/`surf` and `.sea drawn` all go through this path.
- **Fix:** a lean coast step for `Displacement` (lookup, `Reach`, the cap only); look the `CoastSample` up once per
  `ParameterAt` and again only when q leaves its column; let `ShelteredHeight` call `Elevation` directly. Add the
  benchmark to SeaTests as a report (no assertion).
- **Risk:** medium: the CPU must keep matching the GPU (ShaderCheck: surf positions within 2e-5 blocks, ships' heights
  at drawn points). Run ShaderCheck after.
- **Item:** SE-0041.

### 7. Medium: the coastal vertex stage costs 16 times the open sea's

- **Evidence:** `ShaderCheck -- --vertex-cost`: 10.26 ms per million coastal vertices against 0.63 ms at open sea,
  after SEA-D74/D75 roughly halved it. `sea_Differential` (`WaterShader.cs:260-337`) calls `sea_CoastAt` three times
  (here and two forward-difference points); each does 16 `texelFetch` and a loop over all trains with `sea_Reach` (an
  `atan`, a `pow`, several `smoothstep`) per train. Then `sea_TrainY` runs three times per train, with `sea_Reach` and
  `sea_Reflect` again each time. Coastal vertices are a minority of the water in view, so the frame cost is far below
  10 ms, but a long coast seen from a cliff is the worst case, and no measurement on a weak GPU exists.
- **Fix ideas, measured one at a time:** (a) take the bilinear coast's derivative from the four corners already
  fetched (one `sea_CoastAt` for the raw fields instead of three), recomputing only the nonlinear per-train loop at the
  side points; (b) work out `sea_Reach(K, i)` and `sea_Reflect(K, i)` once per train and point and pass them into
  `sea_TrainY`; (c) before going far, count how many water vertices take the coastal path in a typical coastal view.
- **Risk:** medium: normals near rips and cliffs (ShaderCheck's slope allowance) and the CPU mirror.
- **Item:** SE-0042.

### 8. Medium: dead wake code still in the repository

- **Evidence:** wakes were removed in 0.5.1 (SEA-D55, "including compiled implementation"), but `src/Water/WakeField.cs`
  (263 lines) and `src/Water/WakeRenderer.cs` (272) stay in `src/`, excluded only by `Sea.csproj:22`
  (`Exclude="src\Water\WakeField.cs;src\Water\WakeRenderer.cs"`). They no longer compile
  (`sea.Config.WakeEnabled`, `sea.TryWakeHull` are gone). `tests/SeaTests/WakeChecks.cs` and
  `tests/ShaderCheck/WakeGpu.cs` are not in their projects' compile lists. About 750 lines that read as live code. The
  only live copy of their code is `NativeBoats.Surface` (`NativeBoats.cs:166-182`), duplicated from `WakeRenderer`.
  (`SetWakeHull`/`RemoveWakeHull` stay as inert stubs for compiled companions, which is right.)
- **Fix:** delete the four files and the exclusion; the code stays in Git history (an `archive/wakes` tag if wanted).
  Keep ShaderCheck's "removed wake uniform absent" check.
- **Risk:** none at runtime.
- **Item:** SE-0043.

### 9. Medium: the open contracts' surface is not pinned by any test

- **Evidence:** Earth, Ships and Sea Monsters bind `seacontracts:*` by role name, member key and exact delegate type
  (`SeaContracts.cs:141-150, 247-256, 331-342`), and Sea follows `weather` by type tests (`:429, :435`). SeaTests
  compiles only `ContractMath.cs`; nothing checks the role names, the keys, the delegate signatures, `version`,
  `priority`, `recordLength` (179) or the claim and reclaim rules, and the server self-test doesn't read them either. A
  tuple or member type changed would compile and fail only in another mod, silently (a type test that doesn't match
  reads as "no member").
- **Fix:** compile `SeaContracts.cs` into SeaTests with a small fake `ICoreAPI` (an `ObjectCache` and a no-op event API)
  and snapshot each role: key → delegate type, plus the spec's section 3 rules (a higher-priority owner keeps the role;
  a withdrawn role is reclaimed). Optionally check `docs/open-sea-contracts.md`'s member tables against it.
- **Risk:** test code only.
- **Item:** SE-0044.

### 10. Medium: errors become plausible answers, or a log flood

- **Evidence:**
  - `SeaContracts.AtPlaceAt` (`SeaContracts.cs:188`) returns the "none" value for any exception (height 0, shelter 0,
    current 0) with no log. `SeaSystem.WaveFactorAt`/`MeshWaveFactorAt` (`SeaSystem.cs:560, 580-583`) return 0.2 on
    any exception, and `IsDry`/`IsDryWithin` (`:687, :699`) drop a throwing source silently. A bug here looks like calm
    or sheltered water, not like an error (finding 2 would hide behind one).
  - The reverse in `WaterRenderer.Prepare` (`WaterRenderer.cs:179`): a clip source that throws is logged with
    `Logger.Error` every frame, 60 lines a second. `SeaContracts.HullRecords` (`:389`) already logs once.
  - `CoastField`: a tile whose solve throws is asked for again at once and logs each time (`CoastField.cs:277-281`).
- **Fix:** one small "log once per site and source" helper used by all of these (keeping the safe answers); a failed
  tile waits before it is tried again.
- **Risk:** low.
- **Item:** SE-0045.

### 11. Medium: two files hold too much, and some code is written twice

- **Evidence:** `SeaSystem.cs` (1067 lines) is the typed API, the wind provider and change protocol, vanilla's wind, the
  coast's public face, wave probes, the server's model, anchors, save and packets, and the client's setup and frame
  start. `WaveField.cs` (1141 lines) holds six types: `WaveSpectrum`, `WavePlace`, `IWaveSource`, `SharedCorners`,
  `SampleHours`, `WaveClock`. Blocks of `SeaSystem` (`:549-641`) and `WavePlace` (`:523-527, :753-816`) are in a
  compressed style (`if(x){...}`, no spaces) unlike the rest. Duplicates: `CoastMath.Beach` and `Steepness`
  (`CoastMath.cs:251-318`) carry the same two-pass chamfer; `CoastMath.Hash` (`:386`) beside `Noise`; `SmoothStep` in
  `WaveField.cs:722` and `CoastMath.cs:430`; `CoastMath.Rays()` rebuilt on every `TileOf` call.
- **Fix:** split `SeaSystem` into partial classes by side and topic (`SeaSystem.Api.cs`, `.Wind.cs`, `.Server.cs`,
  `.Client.cs`, `.Coast.cs`) and give each `WaveField.cs` type its own file; reformat the compressed blocks; one chamfer
  helper, one hash, one `SmoothStep`; rays as a static field. Keep every public member and namespace: Ships builds its
  bridge against `Sea.dll` and Magelan builds `Sea.csproj`.
- **Risk:** low if done as pure moves (no logic change in the same commit; SeaTests, ShaderCheck and a Ships and
  Magelan build after).
- **Item:** SE-0046.

### 12. Low: per-frame allocations and GL queries

- **Evidence:**
  - `SwashRenderer.Frame` allocates a `HashSet` every frame and a `List` when pruning (`SwashRenderer.cs:76, 112`),
    and works out `CoastMath.Reach` for every beach cell and train each frame.
  - `BoatFloodRenderer` allocates a `HashSet` and a `List` every frame (`BoatFloodRenderer.cs:31, 72`), and each
    rebuild a new mesh and about 2,000 `Vec3d` (helper pass; not counted).
  - `WaterClipBox.DryTopsUniform()` allocates per dry hull per frame (`WaterRenderer.cs:229`).
  - `CelestialRenderer.cs:493` calls `GL.GetInteger(ActiveTexture)` on every water `Use`, and `WaterRenderer.cs:361`
    `GL.GetUniformLocation(…, "sea_WaveClock")` every frame. `glGet*` can force a driver sync on some drivers; on this
    AMD card SEA-D74 measured the whole `Use` postfix at 20–30 µs a frame, and a per-program name cache bought nothing,
    so this is about other drivers, not this PC.
- **Fix:** member collections, reused; copy the tops straight into `dryT`; cache the `sea_WaveClock` location per
  program id; keep track of the texture unit Sea sets instead of asking GL. Measure with `.sea cost` before and after.
- **Risk:** low.
- **Item:** SE-0047.

### 13. Low: uniform names and shader injection robustness

- **Evidence:**
  - `CelestialRenderer.cs:482`: `if (WaterShader.Has(p, "sea_FadeDistance")) p.Uniform("sea_FadeDistance", …)`. `Has`
    accepts the legacy name, but vanilla's `Uniform` indexes `uniformLocations[name]` and would throw for a program
    declaring only `seaFadeDistance`, which switches every celestial uniform off (`usesFailed`). Lines 470-474 set
    `sea_Sun`, `sea_Moon`, `sea_SunColor`, `sea_Weather` without `Has` whenever a program has `sea_Glint`: safe only
    while World Map declares all five.
  - Anchors are exact strings: `"vec4 cameraPos = modelViewMatrix * worldPos;"`,
    `"applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div)"`, `"void main("`
    (`WaterShader.cs:504-506`). A whitespace change in a game update turns Sea's waves off (logged, vanilla water: the
    designed fallback, SEA-D08). `CelestialShader.Fragment` and `Vertex` use `string.Replace` without checking that the
    anchor occurs once; a repeated line would inject twice and fail to compile (then step down).
  - The GLSL array sizes are literals (`sea_Wave[24]`, `sea_Clip*[8]`, `sea_ClipP[32]`, `sea_DryP[128]`, `sea_DryT[16]`)
    while C# has `MaxWaves`, `MaxClips`, `DryStations`…: a constant changed on one side only would cut uploads short
    without an error.
  - Not checked (helper pass): the shadow hand-off multiplies a held vanilla value by `keep` each frame
    (`CelestialRenderer.cs:324-326`), which may compound while vanilla fades shadows out.
- **Fix:** always `N()`; check the five far uniforms once; match anchors with whitespace-tolerant patterns; require one
  occurrence in every injector and return null otherwise; build the array sizes from the constants or assert them in
  SeaTests; add negative tests (anchor missing or doubled gives null) for `WaterShader.Inject`,
  `CelestialShader.Vertex`/`Fragment` and `FlowLook.Inject` (only `FarFragment` has them); look at the hand-off at dusk
  on the bench.
- **Risk:** low.
- **Item:** SE-0048.

### 14. Low: boats keep drifting at full current while vanilla's speed decays

- **Evidence:** `NativeBoats.Drift` (`NativeBoats.cs:142`): `if (b.ForwardSpeed != 0) { m.X += vx; m.Z += vz; return; }`.
  Vanilla eases `ForwardSpeed` toward its target exponentially, so it stays a tiny non-zero number long after the boat
  is let go, and the eased branch below (`:143-144`) is almost never reached: the current is added on top each tick
  instead of the boat easing to it.
- **Fix:** `Math.Abs(b.ForwardSpeed) > 1e-3` (or vanilla's own idle test); check on the bench in a longshore current.
- **Risk:** low; the drift speed of an idle boat changes, which is the point.
- **Item:** SE-0049.

### 15. Low: test harness fragility

- **Evidence (helper pass, spot-checked):** SeaTests needs the game installed and a .NET 10 runtime;
  `Assembly.LoadFrom(VintagestoryLib.dll)` and `File.ReadAllText(chunkliquid.*)` without existence checks crash rather
  than fail; a missing game file is "skip" in some checks and FAIL in others; timing assertions (`Program.cs:152,155`,
  `SelfTest.cs:271,274`) can fail on a busy machine; `SelfTest.DryPlaces` finishes only from `OnLoaded` and
  `tools/run_selftest.sh` has no timeout, so a chunk that never loads hangs the run. Not tested at all: the celestial
  renderer's runtime paths, client-only Harmony targets against the installed `VintagestoryLib`, dispose and rejoin
  (static state), `.sea` command parsing, and the fragment stage's cost (ShaderCheck times only the vertex stage).
- **Fix:** one "game root" helper that fails clearly; report timings without tight bounds; a fallback callback that
  ends the self-test after N seconds and `timeout` in the script; a member-existence check for every client patch
  target, as `Program.cs:229` does for the hand-off.
- **Risk:** test code only.
- **Item:** SE-0050.

### 16. Low: docs that drifted from the code

- The card (`overview/mods/sea.md`) and SEA-D75 say ShaderCheck passes; it fails against World Map 0.5.3 (finding 1).
- `docs/open-issues.md` still lists SE-0013 to SE-0026 as this mod's open issues; they are done. The contract spec
  (`docs/open-sea-contracts.md:134`) and `README.md:213` point to it for O1 and the review's leftovers; O1 has no item
  of its own (it is in the card's "Not checked").
- **Fix:** turn `open-issues.md` into a pointer to the backlog; file O1 as an item and point the spec and README at it;
  correct the card's test line once finding 1 is fixed.
- **Item:** SE-0051.

### Seen and judged fine

- **Threading:** wave sampling under the change protocol; `SharedCorners` computes outside its lock and doesn't keep an
  answer worked out across a change; `SampleHours` copies on write; the dry-space and clip-source lists are copied on
  change and read lock-free; contract calls share one lock per side; server map regions are a `ConcurrentDictionary`;
  the celestial and water postfixes run on the render thread.
- **Network sync:** the wave clock slews by at most 2% and snaps past a second; anchors are sent ahead of use and with
  every clock packet; NaN times are refused; a late joiner gets the parameters and the clock in `PlayerJoin`.
- **Harmony:** two ids (`sea`, `sea.client`), counted users, `UnpatchAll` by id only; each patch fails alone with a named
  warning; the SheyderMod prefix is found by type name and inert without it.
- **The water path's allocations:** reused arrays and comparer; nothing per frame apart from finding 12's small items.

## What is good

- **One truth for the waves.** The same `WavePlace` maths run on the server, every client and (mirrored) in the shader,
  driven by a shared clock and server anchors; the tests compare CPU and GPU surfaces to 2e-5 blocks and the per-place
  answers on both sides. That is the hard part of this mod, and it is solid.
- **Fallbacks everywhere, by design.** Shader code steps down (all → no dry cut → vanilla); passes that disagree drop
  to vanilla's waves together; uniform errors switch Sea's numbers off rather than crash the frame; each Harmony patch
  fails alone with a named warning.
- **The coast is pure and shared.** `CoastMath` cites its sources (Green's law, Komar, Stockdon, Seelig and Ahrens) and
  is compiled into the mod, SeaTests and ShaderCheck alike; solved tiles are immutable arrays, solved on one background
  task, with bounded caches.
- **Documented intent.** Comments and test messages name the item or decision they answer (SE-00xx, SEA-Dxx), and the
  decision log records measurements, including ideas that were measured and dropped.
- **Contracts in base types.** `seacontracts:*` lets other mods ride and cut Sea's water without compiling against it,
  with priorities and reclaiming, and the typed API kept alongside.
- **Foreign callbacks are guarded.** `WindProvider` wraps a provider against exceptions and NaN and counts refusals;
  `DryParticles`' transpilers keep labels and exception blocks and count what they replaced.

## Plan, in order

1. **Before the 0.6.0 release:** make ShaderCheck green again (SE-0036); stop the boat pose race (SE-0037); fix
   `SwitchOff`'s legacy names (SE-0038); reset per-world failure switches (SE-0039); log errors once (SE-0045); the
   drift test (SE-0049). All small; SE-0036 gates every later shader change.
2. **Pin the contracts** with a snapshot test (SE-0044), since three other mods bind them.
3. **Tidy, after the release, each in its own commit as pure moves:** delete the wake files (SE-0043); split
   `SeaSystem` and `WaveField` and remove the duplicates (SE-0046).
4. **Performance, each measured before and after, with ShaderCheck's CPU = GPU checks as the guard:** the coast map's
   bookkeeping and uploads (SE-0040), the CPU coast surface (SE-0041), the GPU coast vertex stage (SE-0042), the small
   per-frame items (SE-0047).
5. The rest (SE-0048, SE-0050, SE-0051) as they come up; SE-0051's card line goes with SE-0036.

## Fable pass

A second, independent review, asked for by the author on 14 November 2026: the review above and every re-review of its
fixes ran on Opus 5.5 (the session's model setting had been ignored); this pass ran on Fable 5.2. It reads Sea `main` at
`600bcb5` (the SE-0036 to SE-0051 fixes, SEA-D77, and the `WaveField.cs` split) without taking the findings above as
given. No code was changed. New findings are filed as SE-0053 to SE-0071 (status `new`).

**Counts:** critical 0, high 4, medium 8, low 7 groups of small points, refactor 9.

### How it was checked

- Read in full by me: `SeaSystem*.cs`, `Game/Hooks.cs`, `Water/WaterRenderer.cs`, `Coast/CoastField.cs`,
  `Coast/CoastTexture.cs`, `Boats/*.cs`, `Water/SwashRenderer.cs`, `CrestSpray.cs`, `SurfSpray.cs`,
  `Game/DryParticles.cs`, `Contracts/*.cs`, `LogOnce.cs`, and the tests' contract snapshot. Three helper passes (also
  Fable 5.2, with no knowledge of the earlier findings) read the GLSL against its C# mirror, the sky and flow code, and
  the wind, sync, config, command and self-test code; the findings taken from them were checked against the lines, and
  those I did not check myself say "helper".
- Vanilla's side of every claim against `devtools/vs-ref/decompiled/1.22.7` (file and line given where it carries the
  finding).
- Runs at `600bcb5` on this PC (AMD RX 6600; other sessions were building at the same time): Release build 0 warnings;
  SeaTests **ALL OK** (396 checks); ShaderCheck `--vertex-cost` **ALL OK** (coast 5.78 ms per million vertices, open
  sea 0.62); SkyIntegrationCheck **ALL OK** with `--vanilla` and against AstraTerra and World Map; the server self-test
  (`tools/run_selftest.sh`, own data folder and port) **SELFTEST ALL OK**.
- Scratch benchmarks on the linked sources (Release, outside the repo), quoted in the findings: the crest-spray tick,
  the swash frame, a coast tile solve with its allocations.

### The fixes for SE-0036 to SE-0051

| Item | Verdict |
| --- | --- |
| SE-0036 ShaderCheck against World Map | holds: ALL OK against World Map's current tree (which still uses the names before SEA-D57) |
| SE-0037 boat pose on two threads | holds for the boat's own place (`BoatWaves.cs:65-80`); a narrower race remains through `CameraWaves` (M3 below) |
| SE-0038 `SwitchOff` and legacy names | the code is as described, but **the failure it was filed for cannot happen** on 1.22.7 (R1): vanilla reloads every shader when a world is joined. The finding above (number 3) was wrong; its bench check passes with or without the fix |
| SE-0039 failure switches per world | holds (`CelestialRenderer.cs:59`, `Hooks.StepDowns`); two status flags still go stale (L1) |
| SE-0040 coast map bookkeeping | holds as read: asks age out, no closure, windows copied outside the lock, tiles uploaded singly. New in the same code: H4, M1, L5 |
| SE-0041 CPU coast surface | holds (`ElevationAt`); boats still take the product of two inverses (M3) |
| SE-0042 lee factors from the CPU | holds: `CoastMath.TrainLee` is the removed GLSL, CPU = GPU checks pass, 5.78 ms |
| SE-0043 wake code | holds: gone from `src/` and the tests |
| SE-0044 contracts pinned | holds: keys, delegate types, header, claim and reclaim checked against the built `Sea.dll` |
| SE-0045 errors logged once | holds at the sites named; `DryParticles.cs:83` and `SeaContracts.cs:262` still swallow silently, and `LogOnce` is shared by both sides of a singleplayer game (L7) |
| SE-0046 split | holds: `SeaSystem` (pure move, checked line by line in the re-review) and `WaveField.cs` (SeaTests and ShaderCheck pass) |
| SE-0047 per-frame allocations and GL queries | partly: the sets and locations are done; the swash still works out every train's lee per beach cell per frame, which the item named (M2, measured) |
| SE-0048 names and anchors | holds (`Once`, `N`, array sizes checked in SeaTests); nine names after `sea_Effects` are still set without `Has` (L7) |
| SE-0049 idle boats | holds; the snap to 0 is right against `EntityBoat.cs:237-245` |
| SE-0050 test harness | holds: the self-test ran and stopped by itself here |
| SE-0051 docs | holds |

### High

#### H1. The coast is switched off on every Earth world

- **Where:** `src/SeaSystem.Coast.cs:30`: `SurfOn => coast != null && Config?.Surf != false && !(Spectrum?.Circumference > 0)`.
- **What happens:** Earth requires Cylinder World (`Earth/modinfo.json:15`), so every Earth world has a ring. The
  author's own server log of 13 November reads "Sea ready, waves wrapping every 4007936 blocks from x=16384". With a ring
  `SurfOn` is false: no coast map, no shoaling or breakers, no lee of land, no longshore or rip current, no swash, no
  surf spray, and `seastate.surf`, `current` and `lee` answer "open sea" to Ships and Sea Monsters. Near land the sea
  is only scaled by the game's ocean map, as before 0.6.0. The README's config table says "Off on a Cylinder World
  ring" (`README.md:403`); the card lists coasts under what Sea owns and does not say they are off on the suite's own
  world. Every coast finding below (H3, H4, M1, M2, M8, L5) therefore applies to worlds without a ring only, until this
  is done.
- **Fix:** make the coast map ring-aware: tile and column keys taken modulo the ring (4007936 is 125248 tiles of 32,
  so tiles line up), `CoastTexture`'s window and `CoastMath.Bilinear` wrapping across the seam. Until then say it in
  the card and in `.sea coast`.
- **Risk:** medium (the seam is the only new case; SeaTests can run the coast checks at a wrapped origin).
- **Item:** SE-0053.

#### H2. Crest spray costs about 5 ms on the client's main thread, ten times a second

- **Where:** `src/Water/CrestSpray.cs:78-94`, ticked every 100 ms (`src/SeaSystem.Client.cs:46`).
- **What happens:** each tick tries 600 random points. Each try calls `sys.WaveFactorAt` (`:83`), which on a client
  inverts the surface on the camera's trains (`src/SeaSystem.Waves.cs:122`: up to 24 iterations over 24 trains), then
  `place.CrestAt` (`:85`), which inverts it again. Measured with the same calls on the linked sources: **4.5 to 5.2 ms a
  tick** (22 to 24 trains; 2.0 to 2.5 ms with one inverse), on the thread that draws the frame, whenever the wind's
  whitecap coverage reaches 0.002 (from about 8 m/s). At 60 fps one frame in six is 5 ms longer. On a ring world (H1)
  nothing stops the tries over land either: each still pays the first inverse before the ocean map says 0 (about 2 ms a
  tick anywhere within 60 blocks of sea level). `.sea cost` does not see it (it times the `Use` postfixes only).
- **Fix:** try random mesh coordinates, not world positions: the crest test needs no inverse at a mesh coordinate, the
  wave factor is read at that same coordinate, and the spawn point is the forward map (`SurfaceAt`). Both inverses go
  (600 loops of 24 sines: about 0.4 ms). Read the ocean map first, and skip the tick where the camera's own factor is 0
  and no sea is in reach.
- **Risk:** low; the spray's look is statistical. Add the tick to SeaTests' benchmark (report only).
- **Item:** SE-0054.

#### H3. The drawn coast ends at a 256-block window that jumps in 32-block steps (helper; code checked)

- **Where:** `src/Water/WaterShader.cs:163-166` (`sea_CoastAt` returns the open sea outside the map),
  `src/Coast/CoastTexture.cs:38-39` (the window is the camera's tile − 4, 8 tiles wide), `:146`. The CPU has no such edge
  (`CoastField.Lookup` solves whatever is asked for).
- **What happens:** the shader has the coast for 96 to 160 blocks around the camera, depending on where the camera
  stands in its tile, and the open sea beyond: past that line the drawn swell runs uncapped through beaches, lee and surf
  end on a straight edge, and a boat there rides the CPU's capped surface, off the drawn water. Crossing a tile line
  moves the window 32 blocks: one strip gains its coast and another loses it in a frame. ShaderCheck's coast check
  samples ±55 blocks on a map whose lookup always answers, so it cannot see it.
- **Fix:** centre the window on the camera (round, not floor) and, in `sea_CoastAt` and in the client's lookup alike,
  fade the coast's numbers toward the open sea over the outer 16 blocks by distance from the camera; or a larger map.
  Say in the contract spec that heights beyond the drawn map differ from the drawn water.
- **Risk:** medium (shader and CPU mirror; ShaderCheck's surf checks, plus one new check at the window's edge).
- **Item:** SE-0055.

#### H4. The coast caches are sized for one player (multiplayer)

- **Where:** `src/Coast/CoastField.cs:23` (`MaxTiles = 512, MaxColumns = 1024`), `:332-340` (`Forget` drops the least
  recently used quarter), `src/SeaSystem.cs:103-109, 125` (each second the server asks for the tiles within 128 blocks
  of every player: 9 × 9 = 81 tiles, which need 13 × 13 = 169 chunk columns).
- **What happens:** with seven players apart from each other the server wants 567 tiles and 1183 columns. Each solve
  past 512 drops 128 tiles that are asked for again within the second, so the server solves a tile every tick for good
  (1.1 MB each, M1), rescans up to 3 chunk columns every 50 ms on its main thread, and at any moment up to 128 of the
  tiles around players are missing: a boat there gets the open sea on the server and the coast on its client. Players
  inland count too (their all-land tiles take the same room). Not run (it needs seven clients); the numbers are the
  code's constants.
- **Fix:** size the caches by the players online, or evict by distance to the nearest player, not by use; keep all-land
  and all-deep tiles as one shared marker instead of 64 KB each (M1); ask only around players within reach of
  sea-level water.
- **Risk:** low.
- **Item:** SE-0056.

### Medium

#### M1. Every coast tile solve allocates 1.1 MB on the large object heap

- **Where:** `src/Coast/CoastField.cs:269, 304-305` and `src/Coast/CoastMath.cs:105-106, 171-173, 254-255, 277-278`: ten
  arrays of 160 × 160 floats (102 KB each, over the 85 KB large-object limit) and a priority queue per tile.
- **What happens:** measured: **1090 KB allocated per tile**, and a full (generation 2) collection about every three
  solves in a loop. A tile that is all land or all deep water costs the same 1.1 MB and 1.2 to 1.8 ms to conclude that
  nothing is there (a beach tile 8.7 ms). A client's first coastal view solves 64 tiles (70 MB); sailing adds 8 tiles
  every 32 blocks; a server does it for 81 tiles per player.
- **Fix:** look at the window first: no water, or no column shallower than 32 blocks, gives the shared open tile without
  a solve; keep one set of scratch arrays for the one solver task.
- **Risk:** low; tiles must stay identical on both sides (SeaTests' coast checks, the probe lines).
- **Item:** SE-0057.

#### M2. The swash works out every train's lee for every beach cell every frame

- **Where:** `src/Water/SwashRenderer.cs:80-90` (per frame), `:173-193` (`Collect`).
- **What happens:** per beach cell and frame, `CoastMath.Reach` (an `atan2`, a `pow`, smoothsteps) and a sine for each
  of up to 24 trains. Measured: **0.36 to 0.50 ms a frame for 200 beach cells, 1.3 to 1.8 ms for 800** (a beach 80
  blocks long and 10 deep inside the 40-block reach). `Collect` takes the coast map's lock 6561 times (81 × 81 cells,
  16 floats copied each) when the camera has moved 4 blocks, at most twice a second, and allocates an 8-float array per
  beach cell. SE-0047 named the per-frame `Reach`; its fix reused the sets only.
- **Fix:** the per-train part once a frame (`CoastMath.TrainLee`, as the shader gets it since SE-0042), leaving two lee
  reads and a blend per cell and train; or a cell's reach only when the trains are resampled (once a second). Read
  whole tiles (`TileData`) in `Collect`.
- **Risk:** low. `.sea coast` prints the frame's milliseconds.
- **Item:** SE-0058.

#### M3. Boats take height and shelter from two inverses, not the same ones on both sides

- **Where:** `src/Boats/BoatWaves.cs:29` (`Waves.HeightAt(x, z) * Sea.WaveFactorAt(x, z)`), `src/Boats/NativeBoats.cs:165`.
- **What happens:** each hull point is inverted twice (in `HeightAt` on the boat's place, then in `WaveFactorAt`). On a
  client `WaveFactorAt` inverts on the camera's trains (`src/SeaSystem.Waves.cs:122`), on the server on a probe of the
  place, so the two sides read the ocean map at slightly different mesh coordinates wherever shelter changes (bays,
  estuaries). It is also what remains of SE-0037: the lock per boat covers the boat's own place, but when the particle
  thread is the first to pose a boat in a frame (`BoatWaves.cs:67-78`) it reads `CameraWaves` through `WaveFactorAt`
  while the render thread may be resampling it, and the pose it caches is the one drawn that frame.
- **Fix:** `sea.HeightOn(place, x, z)` in both places (one inverse on the place's own trains, the same on every side;
  it exists since SE-0041), and let the particle path only read a pose already made.
- **Risk:** low; boats at shelter gradients move by millimetres.
- **Item:** SE-0059.

#### M4. A typo in `sea.json` overwrites the file with defaults (helper; code checked)

- **Where:** `src/SeaSystem.Server.cs:35-37`.
- **What happens:** `LoadModConfig` is a bare JSON parse (`VintagestoryLib/Vintagestory.Common/APIBase.cs:94-102`) and
  throws on a hand-edit's typo. The catch takes defaults, and the next line stores them over the file: the user's
  settings are gone, with one error line in the server log.
- **Fix:** store the config only when it was read or the file was missing; on a parse error leave the file alone (or
  copy it to `sea.json.bad`) and run on defaults.
- **Risk:** none.
- **Item:** SE-0060.

#### M5. After joining, a client's wave clock is behind by its loading time, then the sea jumps (helper; code order checked)

- **Where:** `src/SeaSystem.Server.cs:42-46, 52-53`, `src/SeaSystem.Client.cs:24-30, 92`, `src/Water/WaveClock.cs:26-35`.
- **What happens:** the only clock packet before play goes out at `PlayerJoin`, inside the join request
  (`Vintagestory.Server/ServerMain.cs:477`). The client handles it while its in-world stopwatch still reads 0; the
  stopwatch starts at LevelFinalize (`Vintagestory.Client.NoObf/ClientSystemStartup.cs:1031`). So the client's wave
  clock runs behind by the time between the two (the loading time). Later packets go only to clients in the `Playing`
  state, every 5 s: for up to 5 s in the world the waves are out of step with the server's (ships it moves), then the
  whole sea snaps. Each autosave does a smaller version: the server's clock stops while it is suspended
  (`ServerMain.cs:392, 1618`; `ServerSystemAutoSaveGame.cs:64`), the clients' do not (size not measured). Derived from
  the code's order, not seen in game.
- **Fix:** also send the clock on `PlayerNowPlaying`; ignore clock packets until LevelFinalize, or take the local time
  from a stopwatch of Sea's own. For the autosave: Sea's own server stopwatch, stopped only for a real pause.
- **Risk:** low. Check with `.sea` (the clock's error) right after joining.
- **Item:** SE-0061.

#### M6. `/sea setwind sw 0,3` raises a hurricane (helper; parse checked)

- **Where:** `src/SeaCommands.cs:31, 37-39`.
- **What happens:** the game's number parser allows thousands separators
  (`VintagestoryAPI/Vintagestory.API.Util/StringUtil.cs:150`, `NumberStyles.Any`), so "0,3" is 3 (checked:
  `double.TryParse("0,3", Any, Invariant)` gives 3), clamped to the maximum 1.5 for everyone on the server; hours "2,5"
  become 25. With a decimal comma on the keyboard this is one keystroke away. The same root command has
  `RequiresPlayer()` (`:26`), a precondition that runs before any sub-command
  (`Vintagestory.Common/ChatCommandImpl.cs:288-303`), so `setwind` and `clearwind` cannot be given from a server console;
  and `/sea setwind N 0.5 built` fails because the optional hours take "built".
- **Fix:** take the strength and hours as words and parse them as `Tune` does (`SeaCommands.cs:221`), refusing a comma;
  `RequiresPlayer` only on the root's own handler and `probe`; parse the tail by hand.
- **Risk:** none.
- **Item:** SE-0062.

#### M7. The water's surface seen from below draws as an opaque sheet of sky (helper; shader checked)

- **Where:** `src/Water/CelestialShader.cs:109-117`.
- **What happens:** the reflection block asks for an up face, sky exposure and `cameraUnderwater < 0.01`, but not which
  side of the surface the eye is on. Vanilla draws liquids without culling, and `cameraUnderwater` comes from the block
  at the eye, not from Sea's displaced surface. With the eye in the air block at the still-water line and a crest
  passing over it (a swimmer, a low boat in a storm), or looking up at a raised tank, `dot(-eye, n)` is negative,
  Fresnel is 1, and `sea_SkyReflect` replaces the water with the horizon's colour at full opacity. How often it shows
  in play is not measured.
- **Fix:** add `dot(eye, n) < 0.0` to the condition; `max(ray.y, 1e-3)` in `sea_SkyColor` (`:279` normalises a zero
  vector for a ray straight down).
- **Risk:** low; ShaderCheck's reflection checks.
- **Item:** SE-0063.

#### M8. The surf's phase tears along the midline between two shores (helper; measured by its probe)

- **Where:** `src/Coast/CoastMath.cs:211-222` (the away-from-shore direction is a normalised gradient sum, its trust
  from distance only), used by the surf phase at `src/Water/WaterShader.cs:129-132, 223`.
- **What happens:** in a channel 40 blocks wide and 4 deep the direction flips from +1 to −1 across the middle while
  the trust is high on both sides: the largest height step between neighbouring vertices there was 0.49 blocks against
  0.03 to 0.08 elsewhere (one train, 45 blocks long, amplitude 0.5). A crease down the middle of straits, coves and
  harbour mouths whose two shores are both within reach. CPU and GPU agree there, so the tests count it under "folds".
- **Fix:** let disagreement cost trust: scale it by the length of the gradient sum before normalising (data only; surf
  share, reflection and the longshore current all scale with it).
- **Risk:** low to medium (every coast tile changes a little; the probe lines stay equal on both sides).
- **Item:** SE-0064.

### Low

- **L1. Shader loading and step-down** (`src/Game/Hooks.cs`; SE-0065). A failed water program is blamed in a fixed order
  (sky code, then even flow, then the dry cut, then everything; `:371-390`), though vanilla never compiles the fragment
  stage when the vertex stage failed: a driver that refuses one part loses the others too, after up to four full
  reloads, for the session (read the failing stage's compile status instead). `FlowLookInjected`, `DryHullsInjected`
  and `CelestialRenderer.WaterPatched` are not cleared after a step-down, so `.sea` reports what is no longer in
  (`:270, 275-287`). World Map's own compile failure is logged as Sea's (`:363-369`: its 0.5 shaders declare
  `seaNightFloor` themselves). The rain patch checks the original IL, not what was replaced (`:150-159`). The two
  shader-load postfixes have no try/catch (`:225, 360`). (Helper.)
- **L2. Config and wind** (SE-0066). A world made in the GUI carries `seaStormHeight` "1", which replaces whatever
  `sea.json` says (`src/SeaConfig.cs:54-60`; the json field still documents 0 to 4). A wind provider's failures are
  counted but never logged (`src/Wind/WindModel.cs:33-37, 54-59`). Provider delegates run under Sea's place locks
  (`src/SeaSystem.Waves.cs:123, 161`; `SeaContracts.AtPlaceAt`): a first sample in a cell is 8 corner ends of up to 17
  provider calls each, with other callers waiting. Ended fixed winds are pruned only by the next change
  (`src/SeaConfig.cs:169`): up to 64 stay in the save, in every parameters packet and in each sea-state sum. No upper
  bound on `WaveScale`; the client takes the packet's ring and calendar numbers unchecked (`src/SeaSystem.Client.cs:71,
  88`). `WindAt(x, z)` pairs the shared sample with the local calendar (`src/SeaSystem.Wind.cs:23`). (Helper.)
- **L3. The self-test ships in the release** (SE-0067). `SeaSelfTest` is enabled by `ModConfig/sea-selftest.json`, a file
  per data folder: copied into a player's folder it leaves a water tank over spawn, keeps a chunk column loaded, erases
  the world's fixed wind and shuts the server down (`src/SelfTest.cs:37-44, 83, 97-104, 221-239`). For the same test
  every dedicated server applies the client's patch set (`src/SeaSystem.cs:94`, `src/Game/Hooks.cs:22`). Debug builds
  only, or an environment variable.
- **L4. Commands** (with SE-0062). `/sea probe` needs only chat rights and takes any time: a huge one stops the corner
  cache's pruning for the session (`src/SeaCommands.cs:61-65`, `src/Water/WaveSource.cs:81`). `.sea night` accepts 4
  where the server's cap is 2. `WaterRenderer.FailNextUpload` is a static that can carry into the next world. (Helper.)
- **L5. Coast map** (SE-0068). A re-solve after building is asked for once, sorted behind fresh tiles and dropped after
  10 s (`src/Coast/CoastField.cs:176-185, 244`); a chunk changed while the client had it unloaded comes back as
  `NewlyLoaded` and keeps its scanned depths (`:152-158`). The texture is copied and uploaded whole (4 MB) at every
  32-block line (`src/Coast/CoastTexture.cs:38-44, 102`). A solved tile switches from open sea to coast in one frame
  (`CoastTexture.cs:52-64`; blend it in over a second in the field's own copy).
- **L6. Boats, hulls and contracts** (SE-0069). After an upload error Sea no longer cuts the hull but still skips
  vanilla's water mask (`src/Boats/NativeBoats.cs:96-97`): the sea shows inside the sailboat. `InDryHull(x, y, z)`
  assumes the detailed outline, which only the four nearest dry hulls draw (`src/Water/WaterShader.cs:699`,
  `WaterRenderer.cs:223`). The contracts allocate on every answer (a closure per `height`, `shelter`, `current`, `surf`,
  `lee`; five answers and two delegates per `pose`: `src/Contracts/SeaContracts.cs:156-170, 312, 326`), and hulls given
  through `hulls.register` get three new float arrays per hull per frame (`src/Contracts/ContractMath.cs:82-94`).
  `src/Boats/BoatFloodRenderer.cs:33` reads `api.World.Player.Entity` without the null checks the other renderers make.
- **L7. Wave maths and sky code, small** (SE-0070; helper). `CrestAt` has no pixel-footprint fade, so spray is thrown on
  crests the shader does not whiten (`WaveField.cs:464-481` against `WaterShader.cs:278`). Between inland and open
  water the shader blends the horizontal displacement and the CPU does not (`WaterShader.cs:349`). `pow(x, 2.0)` on
  values that `SetTrains` may make negative (`WaterShader.cs:305-314`: write `x*x`). A NaN wind given to the public
  `WavePlace.Update` reaches `Split` as an array index (`WaveSpectrum.cs:155-158, 183`). Nine uniform names and two
  samplers after `sea_Effects` are set without `Has` (`CelestialRenderer.cs:484-499`). The shadow hand-off multiplies a
  value vanilla is itself decaying (`:326-328`), and runs without real twilight if that patch failed (`:62, 308-312`).
  `LogOnce` is one set for the server and the client of a singleplayer game (`LogOnce.cs:14-24`). The foam's worst
  pixel takes about 86 sine hashes with two noises worked out before its cheap gates (`CelestialShader.cs:185-215`; not
  timed: ShaderCheck times the vertex stage only). `GL.GetInteger(ActiveTexture)` twice per water program use
  (`CelestialRenderer.cs:497`, `CoastTexture.cs:143`).

### Refactor (SE-0071)

- **R1. `WaterRenderer.SwitchOff` is not needed on 1.22.7.** Vanilla reloads every shader after the mods of a world
  have started (`ClientSystemStartup.cs:878-879`: `StartModsFully(); Shader.ReloadShaders()`), and `ReloadShaders`
  disposes every program first (`ShaderRegistry.cs:156-163`). A world joined next never sees the last world's programs
  or uniforms. Sixty lines of GL at world teardown (which once crashed the game, 0.4.4) can go after one bench check
  with it disabled. The comment at `src/SeaSystem.cs:170-171` is wrong for the same reason, and `onLevelFinalize`
  (`src/SeaSystem.Client.cs:59-65`) only acts if that vanilla reload was suppressed.
- **R2. Per-world shader state lives in statics** spread over `Hooks`, `WaterRenderer` and `CelestialRenderer`
  (`Injected`, `WavesInjected`, `DryHullsInjected`, `FlowLookInjected`, `passWaves`, `WavesOff`, `current`, the failure
  flags), reset by hand in `Hooks.Remove` (`src/Game/Hooks.cs:198-206`). SE-0039 and L1's stale flags are missed resets.
  One object owned by the client's `SeaSystem` (new with each world), and a small per-process record of what the
  driver refused.
- **R3. The 32-place cache twice:** `src/SeaSystem.Waves.cs:188-209` and `SeaContracts.TrainPlace`
  (`src/Contracts/SeaContracts.cs:266-281`). One class.
- **R4. The legacy uniform names and the World Map 0.4 path.** World Map is in the suite: rename its eleven `sea…`
  uniforms to `sea_…` and drop `Legacy`, `N`, `Loc` and the double lookups in `Has`. `CelestialShader.FarFragment`,
  `FarSkyReflection`, `sea_NightEnabled` and `CelestialFarDisabled` serve World Map 0.4.x only and are dead with 0.5.3.
- **R5. Public API types sit in the shader file:** `WaterClipBox`, `IDrySpaces`, `IWaterClipSource` at the end of
  `src/Water/WaterShader.cs`, after 500 lines of GLSL. Their own file (a pure move; `HansaTests` compiles Sea sources
  by path, so check it as for SE-0046).
- **R6. One formula five times:** the developed sea's height `(0.1 + 2.4 s²) · WaveScale` and its inverse in
  `src/Wind/WindModel.cs:163, 205, 226, 232` and `src/SeaSystem.Wind.cs:103-106`.
- **R7. Dead or unused:** the `DrySpaceSources == 0` guards (`src/Game/Hooks.cs:437, 459, 475, 482`: `NativeBoats`
  always registers itself, `NativeBoats.cs:32`); `WorldShape.RingX`/`DeltaX`, `Wind.Rotated`, `SampleHours.InForce`/
  `Anchored`, `SeaSystem.Controls` (no users in Sea, Ships, World Map, Earth or Cylinder World).
- **R8. Texture units 10, 11 and 12** are literals in two files; name them in one place.
- **R9. Stale comments:** the section headers left at the ends of the split files (`SeaSystem.Server.cs:245`,
  `SeaSystem.Wind.cs:215`, `SeaSystem.cs:178`); `WindModel.cs:194` (the change can land between the three reads; it is
  safe because writers set `History` before `Override` and readers read `Override` first: say that);
  `NightSkyMath.cs:5` ("never alters calendar light", while `Twilight` writes it); `SelfTest.cs:207` (a method long
  gone); `WaterShader.cs:52-55` (`Has`'s summary sits on `Once`).

### Checked and found sound

- GLSL against C#, function by function (helper): no formula, constant, clamp or branch differs given the same inputs;
  all `out` parameters set on every path; `inverse(jac)` never singular; array indices and `texelFetch` in bounds;
  continuous at depth 32, across the 8 lee sectors, at the 24-train cut and at the 64-block cells.
- Harmony targets and signatures against 1.22.7; every anchor once in the installed water shaders; texture units 10 to
  12 clear of vanilla's; AstraTerra's eight reflected members present in 0.15.0-dev.25 (helper).
- Protobuf members, retired numbers, a server restart's fixed winds, singleplayer pause, `/time set` and sleeping, the
  change protocol (no foreign code under its lock), command privileges for `setwind` and `clearwind` (helper).
- `uniformLocations` holds every declared uniform (vanilla collects them from the shader text,
  `ShaderProgram.cs:39-58`), so `Has` is true for declared names even where the driver dropped them; the server's and
  the client's `ObjectCache` are separate; `Ping` is in seconds; `SeatPosition` is rebuilt on each read, so the heave
  added to it does not pile up.

### Top five, and the order

1. **H2** crest spray (SE-0054): the largest frame cost found, in ordinary weather, with a small fix.
2. **H1** coast on Earth worlds (SE-0053): decide whether 0.6.0 ships with coasts off on the suite's world, and say so
   until the ring is handled.
3. **M4** `sea.json` overwritten (SE-0060) and **M6** the decimal comma (SE-0062): small, before any release.
4. **H3** the drawn coast's window (SE-0055) and **M5** the clock after joining (SE-0061): visible, medium work.
5. **H4** and **M1** (SE-0056, SE-0057): before Sea is run on a server with many players.

Then M2, M3, M7, M8, the low groups, and the refactors (R1 and R4 remove the most code).
