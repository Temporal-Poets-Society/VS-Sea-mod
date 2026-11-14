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
