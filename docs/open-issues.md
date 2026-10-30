# Sea open issues (after the 0.4.2 review round)

What the Claude + Codex review of 21 October 2026 (three Codex passes and two of Claude's) found and 0.4.2 did **not**
fix, with why, and what to look at in the next rounds. Fixed items are in [DECISIONS.md](DECISIONS.md) (SEA-D31–D34)
and the README's 0.4.2 note. Severity is the reviewers' estimate; nothing here has been reproduced in game.

## Not verified at all

0.4.2 passed `tests/SeaTests`, `tests/ShaderCheck` (GPU), `tests/SkyIntegrationCheck` and the dedicated-server
self-test. None of these runs a client, so these 0.4.2 paths have no automated check and need a play test:

- `WaterRenderer.SwitchOff` on leaving a world, then joining a world **without** Sea in the same session (no frozen
  waves, hull holes or glints) and one **with** Sea (Sea's shader code back: `.sea` says "Sea code in").
- The water uniforms uploaded from the `ShaderProgramBase.Use` postfix (patch "water uniforms"), prepared at Before
  0.99: the liquid depth pass (Before 0.995) and the drawn water agree while the camera and ships move.
- `/sea setwind` and `/sea clearwind` in multiplayer: the change shows at the same moment on every client and the
  server's ships; the cleared storm's swell dies down over game hours.
- A ship sailing across the 64-block sampling grid: no visible step in its motion or the drawn waves.
- The AstraTerra adapter's typed delegates in a live client (only bound and called offline against the DLL).
- (Review round 1, not released) Leaving a singleplayer world and joining another: the client's patches come off
  and go back on, on the main thread (SEA-D38); `.sea` lists them again. After a water uniform upload error the water
  shows vanilla waves and no hull cuts instead of frozen ones.

## Open, by design or deferred

| # | Severity | Issue | Why it's open | Next step |
|---|---|---|---|---|
| O1 | Medium | **On a client `WaveHeightAt` is the drawn surface** (the camera's trains at the asked place); the server samples the place itself. Away from the camera, with a provider's wind varying in space, the two differ slightly | The water shader draws all water from the camera's trains; matching the server would put the client's answer off the visible water. Magelan's physics asks on the server only | Spatial amplitude interpolation in the shader (corner amplitudes as uniforms), then sample the place on both sides |
| O2 | Low | **A sample worked out across a change on another thread** (a provider swapped, `SetOverride`, a new model) can mix old and new state for that one call; it's redone at the place's next call | `Revision` is bumped before and after every change and re-checked after sampling; a fully atomic snapshot (anchors, model, provider, fixed winds) wasn't built | One immutable "sea inputs" object swapped as a whole, read once per sample |
| O3 | Low | **Hour-only questions** (`WindAt`, `SeaAt(x, z)`, `WindModel.At`, `SeaWind(x, z, h)`) answer "as known now": a fixed wind changed by an admin counts on the server at once and on a client when its packet arrives | Only the wave samples (what ships ride and the water shows) need to agree exactly; these answer for the present | Pass the current sample where a caller needs agreement |
| O4 | Low | **Partial water-shader injection is tracked globally**: if a game update changes `chunkliquiddepth`'s wave call but not `chunkliquid`'s, the depth pass keeps vanilla waves while the drawn water has Sea's, and `.sea` still says "Sea waves in" | Both passes come from the same vanilla code today; Sea logs a warning when an anchor is missing (Codex, first pass) | Track `Injected`/`WavesInjected` per pass and fall back for both together |
| O5 | Low | **`SeaSystem.WindAt` without a provider or fixed wind reads vanilla's wind through `World.BlockAccessor`**, which isn't meant for other threads, though `WindAt` is documented as callable from anywhere | Vanilla's wind is a single global value; no failure seen | Cache vanilla's wind on the main thread (a tick listener) and return the cached value |
| O6 | Low | **Listeners and events aren't unregistered on dispose** (the server's tick listeners, `PlayerJoin`, `GameWorldSave`, the client's `LevelFinalize` lambda and wind tick) | The game drops them with the world's API objects; only the static `Hooks.RequestShaderReload` outlived a world, and 0.4.2 clears it (Codex, first pass: a hypothesis, not a failure) | Keep the listener ids and handlers; unregister them in `Dispose` if a world ever reloads in place |
| O7 | Low | **A client keeps its model when only `DaysPerYear`/`HoursPerDay` change** (`OnParams` compares ring, key and three config values) | Those only feed `WorldShape.YearRel` (Wmap); a server changes them only with a new world | Compare them too |
| O8 | Low | **More than 64 fixed-wind changes within 17 game hours** drop the oldest still-needed ones (logged) | A flood of admin changes is unlikely; unbounded history would grow the packet and save | Coalesce adjacent identical fixed winds before dropping any |
| O9 | Info | **With the calendar stopped**, a cleared storm's past hours stay stormy until the calendar moves (only the current hour stops being fixed) | Correct for the model: no time passes for the sea to calm | None, unless a stopped calendar should calm the sea by wave time |
| O10 | Info | **After a server restart** a fixed wind that was still waiting for its sample is in force at once, and ended ones count as ended for every sample | Wave samples count from the server's start (`ElapsedMilliseconds`), so saved sample numbers mean nothing after it (`WindOverride.Restarted`) | None needed |
| O11 | Info | **`WavePlace.Sea` and `.Wind` are the nearest grid corner's**, as of the last time the place was sampled, not the place's own | Only reports read them (`.sea`); the trains themselves are blended by position | Blend them like the amplitudes if a mod needs them exact |
| O12 | Low | **Around a hull that isn't dry the reflections' normal ignores the calming**: the water there is drawn flat or damped, but glints and the sky reflection still follow the full waves (review S1-3) | A visible change to the water's look; Magelan and Hansa use dry hulls, which aren't calmed | Have `seaWater` return its damping and scale `seaSurfaceSlope` by it (plus the clip plane's slope); judge it in game |

## Performance left on the table

- **`CelestialRenderer`'s `Use` postfix** still asks two `HasUniform` lookups of every program the game uses and
  uploads its celestial uniforms on every use of a water program (not once per frame like the water uniforms); the
  water uniforms' postfix compares each program's pass name (review S1-7, left open: tens of µs a frame). Its two
  textures (units 10 and 11) must still be bound on every use, since other programs rebind those units between uses.
- **The vertex stage runs the 24 trains twice** (`seaWaves` for the height, `seaSurfaceSlope` for the reflections'
  normal) while reflections are on (review S1-8; since then the slope is skipped while glints and sky are both off).
  One loop could give both.
- **Every wave place samples both ends again when a new anchor arrives** (every 5 s, and on each `/time set`). Since
  the shared corners (SEA-D39) the provider isn't asked again for hours it already answered, but the place still
  blends both ends anew.
- **`WavePlace.Sample` blends 4 × 414 amplitudes (pre.8; formerly 252) per call** even when the place hasn't moved and the time is in the
  same second; caching by (u, v, f) would skip it for stationary samplers called several times a frame.

## Earlier, still open

- Issue #30 / SEA-D47: [caravel hold-water investigation](hold-water.md) reproduces a thin boundary gap caused by
  Hansa's clip pivot (232 bottom-mesh vertices at −12° pitch/−35° heel; proposed Hansa registration correction removes
  every miss). Hansa's independent floodwater mesh is also triggered by storm downflooding with closed hatches.
  Hansa patch is prepared but not applied; live vessel water/shader state is needed to attribute the pictured sheet.
  Sea's CPU/GPU clip agrees for the actual caravel profile; its bounds were not widened to suppress genuine flooding.

- Issue #28 / SEA-D46: pre.8 corrects the storm spectrum and adds near-water wind foam; offline evidence and the
  remaining [storm playtest matrix](storm-waves.md) are recorded. Wmap receives the longer waves but has no wind-foam
  consumer: inspect the near/far handoff, and add it in Wmap if the smooth far band is visible. Surface spume is not
  airborne spray. No client appearance, ship handling or frame-time acceptance yet.

- The stern dry-outline failure reported by the ship test history (see `overview/ROADMAP.md`); 0.3.6's frame fix may
  bear on it, unverified.
- SEA-P01 (coastal and shelter behaviour) and SEA-P03 (Terra Prety and Watersheds run-time check) in
  [DECISIONS.md](DECISIONS.md).

## Review tooling notes

- Codex's sandbox can't read the game's installation, so it can neither build Sea nor run the tests that read the
  installed shaders; give it the verification results and have it review only.
- The Codex plugin's rescue agent may start a background Codex job and return before it finishes; collect it with
  `node <plugin>/scripts/codex-companion.mjs status|result <job id>`.
