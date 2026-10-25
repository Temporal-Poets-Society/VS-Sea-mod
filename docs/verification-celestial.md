# Sea 0.4.7-pre.5 — 25 October 2026 (sunset colour, sky glow, near/far seam)

Branch `playtest/sunset-seam-2026-10-25` from pre.4 (`a9aa45d`); `release/sea-0.4.7-pre.5.zip`. With AstraTerra
0.15.0-dev.20 (branch `feat/sunset-sky-glow`) and Wmap 0.5.0-pre.6 (branch `playtest/sunset-seam-2026-10-25`).
[What and why](sky-light-model.md).

- `tests/SeaTests`: ALL OK, new: the near water's footprint fade, flat fog, eye height and glow in the shader text (the
  "no star reflections" check now ignores the game's own `flatFogStart`).
- `tests/ShaderCheck` (RX 6600, `WMAP_ROOT` = the Wmap branch): ALL OK. Water variants compile and link; Wmap's
  `farterrain` and `farcells` compile with `seaWaveFactor` and `seaSunset` live; new GPU check: the glow warms the
  grazing reflection beneath a low sun (r 0.27 → 0.72) and not behind it, and flat fog greys the reflected horizon.
- `tests/SkyIntegrationCheck`: `--vanilla` OK; against the installed dev.18 DLL the sun pieces are absent and the glint
  keeps Sea's table, the moon adapter intact; against the dev.20 DLL AstraTerra's sun colour (horizon 1.00 0.57 0.28)
  and glow are bound.
- Not verified: anything in game (the seam's continuity, the glint's width far off, the glow's strength), other GPUs.

# Sea 0.4.6 — 22 October 2026 (no fog-coloured horizon at night)

**Reported in game** (0.4.5 with Wmap 0.4.5, clear night at sea): a red-brown band where the sea meets the sky.

Cause: `getSkyColorAt` (vanilla `skycolor.fsh`) ends with `mix(skyColor, vec4(rgbaFog.rgb, fogAmount), 1 - light)`:
at night its rgb is the fog colour and its alpha the fog amount, and the sky dome shows rgb × alpha over dark space.
0.4.4's reflection took the rgb alone (`seaSkyColor`; `FarSkyReflection` mixed toward `sky.rgb`), and at a grazing
angle Fresnel is near 1, so the horizon of the sea turned the fog colour. The 0.4.4 GPU check asserted that.

Fix: `seaSkyColor` returns its colour times `mix(1, fog, 1 - light)`; `FarSkyReflection` reflects `sky.rgb × sky.a`.
By day (alpha 1) nothing changes.

- Release build 0 warnings/errors → `release/sea-0.4.6.zip` (SHA-256
  `48f0314946426fc3dbbcf8c71066a1d16a9e5e31781422bc56ab2d2b9713d7fe`), installed in place of 0.4.5. Older ZIPs kept.
- SeaTests ALL OK. ShaderCheck (RX 6600) ALL OK; the sky-reflection check now requires the night dome over dark space:
  grazing at night 0.00 clear, 0.09 with fog 0.5 (0.4.5 gave the fog colour, 0.16). Day values unchanged (looking
  down 0.057, grazing 0.82, alpha 0.91).
- In-game: **not run.**

# Sea 0.4.4 — 21 October 2026 (Fresnel sky reflection instead of stars; night level chosen in game)

**User play-test of 0.4.3** (same setup as below): with `.sea sky` raised, small waves showed blotches ("leopard skin")
from the star reflections. The user judged `.sea night 2` right in a dark room and `3` with a light near the monitor,
and chose: remove the star reflections, reflect the sky realistically (option B), no Milky Way on the water (SEA-D35,
SEA-D36).

Changes: the water reflects the game's sky colour by Fresnel through Sea's wave normals (`seaSkyReflect`/`seaSkyColor`
in `CelestialShader`); Wmap's distant sea gets the same reflection through Sea's adapter (`FarSkyReflection`, uniform
`seaSkyReflect`). Star cube, star atlas, AstraTerra star reads and `StarCubemap.cs` are removed; the AstraTerra adapter
now reads only its moon. Night floor at 1: 0.15 / 0.45 (was 0.075 / 0.225). Fog no longer attenuates glints or the
floor. `.sea` shows sky daylight and the three strengths in effect.

- Release build 0 warnings/errors → `release/sea-0.4.4.zip` (SHA-256 `32688d06e45ebf04c6b587fd03e0deac4e92672cf1b922e7159e9bd698b6f2f0`).
  Older ZIPs kept.
- SeaTests ALL OK, including the night levels, sky daylight, shader text and the Wmap adapter (applied with its water
  flag, floor only without).
- ShaderCheck (RX 6600) ALL OK: water variants compile with `seaSky`, `seaSkyTex`, `seaGlowTex` live; Wmap working
  tree (= installed 0.4.4) and installed 0.4.1 compile with the adapter, `seaSkyReflect` live. New GPU check: water
  b 0.04 / alpha 0.4 → looking down 0.057, grazing 0.82 with alpha 0.91, at night the fog colour; off unchanged.
- SkyIntegrationCheck (AstraTerra 0.15.0 DLL) and `--vanilla`: ALL OK.
- In-game: **not run.** The look by day and night, rough water, the Wmap seam and the full-moon night level are unjudged.

# Sea 0.4.3 — 21 October 2026 (live night/reflection tuning; first night play-test of 0.4.2)

**Play-test of 0.4.2 by the user** (Earth 0.15.2, AstraTerra 0.15.0-dev.1-earthtest with `MoonArt: vanilla`,
`LongitudeAwareSun: true`, Wmap 0.4.4, Hansa dev.3), open sea at x 116028, z 46982:

- Overcast moonless night: `.sea` clear 0.26, moon −65.8° (fraction 0.13), floor 0.020; Earth `/earthweather`: clouds
  100 %. Dark, no reflections: the designed overcast/moonless result.
- Clear moonless night: clear 1.00, moon −46.6° (fraction 0.59), floor 0.075. Screenshot: AstraTerra's sky and Milky Way
  bright, water and ground near black, no visible star or sky reflection. **Findings:** the moonless floor does not meet
  the user's "some visibility" target; water does not reflect the sky's brightness (fixed faint tint, stars at ≈4.5 %,
  fade-out from 0.50 of view distance, no Milky Way in Sea's data). With `MoonArt: vanilla` the moon keeps the global
  clock while AstraTerra's sun follows Earth's longitude.

0.4.3 adds client commands `.sea night|sky|glint <0–4>` (and `default`, `.sea reset`) that override the three strengths
on that client only, at once, until the world is left, so defaults can be chosen from in-game comparisons. No default,
shader or `sea.json` behaviour changed. Release build 0 warnings/errors → `release/sea-0.4.3.zip`
(SHA-256 `f73e17dc44354af748b674bb80b9dd8a09fa67e0eba217f09026abea38e9ec4d`); 0.4.2 and older ZIPs kept. SeaTests, ShaderCheck (GPU), SkyIntegrationCheck: ALL OK. The command
dispatch was checked against the game's `ChatCommandImpl.Execute` (bare `.sea` still reports). The commands have not
been run in game.

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
