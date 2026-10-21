# Wmap 0.4.3 far-terrain anchors — 21 October 2026 (branch `wmap-043-far-anchors`)

**ShaderCheck reported 2 failures** on `main` (db0ab6a): "Wmap distant terrain with Sea night floor: Sea's code
couldn't be put in (anchors not found)". Wmap 0.4.3 (Wmap commit a09af28) lights far terrain by the game's ambient
colour, `albedo * clamp(wmapAmbient, 0.0, 1.0) * nb`, instead of the sun colour, `albedo * max(vec3(0.05), sun) *
(...)`, the only anchor Sea's adapter knew. In game Wmap 0.4.3's distant terrain loaded unpatched (no warning; the
safe fallback), so it went without Sea's explicit night floor.

Fix: `CelestialShader.FarFragment` accepts either light term and wraps it as `max(<term>, seaNightFloor)`. The other
checks are unchanged: `void main()` must be present, the haze limit is added only to 0.4.1-style shaders that lack it
(0.4.2 and 0.4.3 have their own), a shader already holding `seaNightFloor` is left alone, and any missing anchor
returns null (Wmap keeps its own shader). Wmap reads `wmapAmbient` from `api.Render.AmbientColor`, which probably
already carries Sea's floor (Sea raises `BlendedAmbientColor` in its ambient postfix); the explicit `max` holds the
floor either way and changes nothing when it is already there. Not checked in game.

- `tests/SeaTests`: ALL OK, with two new checks on inline 0.4.1-style and 0.4.3-style snippets: the floor applies
  once to each, and missing anchors (light term, `void main()`, 0.4.1's haze mix) or null return null.
- `tests/ShaderCheck` run from `C:\VS mods\Sea` (RX 6600, OpenGL 3.3.0 Core, driver 26.8.1.260806): ALL OK. Wmap
  working tree (0.4.3, a09af28) and installed `wmap_0.4.1.zip` both compile and link with the adapter, at low and
  high settings.
- `tests/SkyIntegrationCheck` run from `C:\VS mods\Sea`: ALL OK, including "installed workspace Wmap distant lighting
  adapter applies once" against 0.4.3.
- No Wmap file was edited. No package was built. In-game: not run.

# Sea 0.3.8-pre.3 — 17 October 2026 (reflections invisible in game)

**User report on pre.2: no reflections visible in game.** pre.2 loaded without Sea warnings (the water patch applied).
Cause, from the vanilla shaders: the water's OIT pass shows `rgb × alpha` (`oit.fsh`: `colour.rgb *= colour.a`).
pre.1/pre.2 only *added* RGB, weighted by Fresnel (≈0.16 at usual angles) and left alpha alone, so a sun glint
added ≈0.05 and a full-moon trail ≈0.01 to the screen. Enabling them also removes vanilla's glint, which mixes toward
the sun colour and raises alpha. An offline test even asserted "unchanged reflection alpha".

Fix: sun glitter and moon trail (`seaGlints` in `CelestialShader`) now mix toward the light colour and raise alpha
to the glint weight, as vanilla does; Fresnel shades them (0.55–1) instead of scaling them to nothing. Strengths:
sun 1.6 (core saturates), moon 0.75 × illuminated fraction × night (was 0.8 and 0.18). Faint sky/star light keeps
the water's alpha and is divided by it (min 0.25) so it survives the premultiply. Water transparency changes only
on glint pixels.

- New GPU check (RX 6600) of what reaches the screen for dark water (b 0.04, alpha 0.4, 0.016 shown): full moon
  0.19, crescent (15 %) 0.039, overcast (clear 0.08) 0.028, sun 0.92; effects off leaves the pixel unchanged. PASS.
- Release build 0 warnings/errors → `release/sea-0.3.8-pre.3.zip`; pre.1/pre.2/0.3.5 ZIPs kept. SeaTests, ShaderCheck,
  SkyIntegrationCheck: ALL OK.
- In-game: **pre.3 not yet launched.** Strengths are first estimates from the numbers above, not tuned by eye.

# Sea 0.3.8-pre.2 — 17 October 2026 (crash fix)

**First in-game launch of 0.3.8-pre.1 crashed** (user report, Vintage Story 1.22.7 with Cworld, Earth, Wmap 0.4.2,
Magelan, Sea Monsters): `NullReferenceException` in `Mat4d.Invert`, from `CelestialRenderer.Frame` called by the
`AmbientManager.UpdateAmbient` postfix. That postfix runs in the Before stage, before the camera's first update, when
`CameraMatrixOrigin` is still null. pre.1 should not be used.

Fix in pre.2: the camera matrix is null-checked; the ambient, shader-`Use` and star-cube Harmony postfixes and the
renderer's per-frame sky update now catch errors, log once and switch their effect off (night floor to 0, water
`seaEffects` and Wmap floor uniforms to 0) instead of ending the game.

- `dotnet build -c Release`: PASS, 0 warnings/errors → `release/sea-0.3.8-pre.2.zip`. `sea-0.3.8-pre.1.zip` and
  `sea-0.3.5.zip` kept.
- `tests/SeaTests`: ALL OK. `tests/ShaderCheck` (RX 6600): ALL OK. `tests/SkyIntegrationCheck` (AstraTerra 0.15.0 DLL)
  and `-- --vanilla`: ALL OK. None of these exercises the client render loop, which is where the pre.1 crash was;
  no automated check covers it.
- Wmap: the installed `wmap_0.4.2.zip` `farterrain.fsh` is identical to the Wmap working tree. It already has the
  night haze limit, so Sea's adapter adds only the ambient floor (no second haze limit).
- In-game: **pre.2 not yet launched.** Everything under "In-game" below is still unverified.

# Sea 0.3.8-pre.1 verification — 17 October 2026

Package: `C:\VS mods\Sea\release\sea-0.3.8-pre.1.zip` (79,659 bytes).
Release DLL: `C:\VS mods\Sea\bin\Release\mod\Sea.dll`.
Package SHA-256: `51578aaa7f4d1ed8c0c479b22aeabf4b95e0fed22e9b217bcf8a699f8bd465d4`.
Contents inspected: `Sea.dll`, `Sea.pdb`, `modinfo.json`; no bundled astronomy/geography/ship assemblies.
Nothing installed into the regular game data folder. Player saves were not used.

## Automated

- `dotnet build -c Release`: **PASS**, zero warnings/errors. Existing release ZIPs are preserved; packaging now
  refuses to overwrite an existing destination. `sea-0.3.5.zip` retains SHA-256
  `7ed1eaafe0a3ffc7d7b916221e9e6417fe82e08ab676e46347cd349cd8da1dae`.
- `dotnet run --project tests/SeaTests`: **104/104 PASS**. Includes existing wave/wind/dry-hull checks and new
  night ordering, weather/phase/disable behavior, local-moon ambient correction, shader anchors and all-zero config
  serialization. Log: `obj/sky-inspection/offline-results.txt`.
- `dotnet run --project tests/SkyIntegrationCheck`: **PASS** against the DLL extracted from the exact installed
  AstraTerra-0.15.0.zip. Adapter initialization, real star record reader, longitude moon-time function, atlas
  determinism/seam/subpixel energy and Wmap working-tree shader anchors. Log: `obj/sky-inspection/adapter-results.txt`.
- `dotnet run --project tests/SkyIntegrationCheck -- --vanilla`: **PASS**, no AstraTerra assembly loaded.
  Log: `obj/sky-inspection/vanilla-adapter-results.txt`.
- Existing dedicated-server self-test: **PASS** in a new vanilla-only Debug test profile,
  `testserver/celestial-20261017-210957-8e83a164` (`Logs/server-main.log`: `SELFTEST ALL OK`).
- Same dedicated-server self-test using the final Release ZIP plus **AstraTerra 0.15.0**: **PASS**,
  `testserver/celestial-20261017-212021-665247a3` (`SELFTEST ALL OK`). No patch/runtime errors found;
  game startup warned that the remote blocked-mod list could not be fetched (DNS/network unavailable).
- Shared `tools/Update-ModInventory.ps1`: run successfully after the new manifest/package; 10 mods recorded,
  no unknown declared sibling dependencies. Sea overview card, roadmap, scope/decision/source references updated.

Server checks exercise existing wind/waves/dry spaces and assembly coexistence, not client sky rendering.

## GPU

`dotnet run --project tests/ShaderCheck`: **68/68 PASS** on AMD Radeon RX 6600,
OpenGL 3.3.0 Core Profile, driver `26.8.1.260806`.

Water/particle baseline and Sea shader variants compile/link at low/high settings. Celestial variants also compile
with the dry cut disabled. Required new uniforms survive optimization. Both the Wmap working-tree shader and the
shader extracted from the installed `wmap_0.4.1.zip` compile/link with the optional Sea adapter. GPU equations check
reflected-direction alignment, rough-lobe broadening and below-horizon rejection. Resource check confirms cube pixel
copy/mipmaps, framebuffer/binding restoration and unchanged source filtering. Six existing dry-hull pixel-grid
comparisons pass, including tilt, detailed outline, rake and flooding. Log: `obj/sky-inspection/gpu-results.txt`.

These are compile/link, numerical and resource checks. They do not establish appearance in the game render pipeline.

## In-game

**NOT RUN.** No graphical game client was launched for this feature. Clear-night navigation brightness, star shimmer,
sun/moon trail appearance, both camera visibility cases, caves/rooms/ships/underwater and Wmap boundary continuity
remain unverified. No screenshots or visual acceptance are claimed.

Eight fresh, unlaunched effects-on/off comparison profiles were created: vanilla, installed AstraTerra settings,
AstraTerra pixel moon, and AstraTerra + Cworld + Earth + Wmap + Magelan. Paths are in
[celestial-comparison-profiles.json](celestial-comparison-profiles.json). Only the self-test profiles above have
generated worlds. The [comparison matrix and commands](sky-water.md#reproducible-in-game-comparison-pending) cover
day/twilight/moonlit/moonless/overcast/storm, calm/rough, bodies on/off screen, open sea/shore/deck/enclosures/cave/
underwater and distant sea. The fresh-profile script is [New-CelestialTestProfile.ps1](../tools/New-CelestialTestProfile.ps1).

Performance remains a measurement task in game. Added resources are bounded at about 9.33 MiB GPU plus 2 MiB CPU
for both star sources; there are no sky capture passes. A synthetic Debug 6000-star CPU splat took 4.56–4.58 ms per
rebuild here, excluding source enumeration, GPU upload/mips and actual frame time; atlas rebuilds are capped at 4 Hz.

Known scope limits: star atlas is filtered grayscale source data; sky background is a faint approximation; no exact
cloud occlusion/captured Milky Way/planet/meteor reflections or arbitrary near-body replacement moon reflection.
See [the source audit and behavior boundaries](sky-water.md).
