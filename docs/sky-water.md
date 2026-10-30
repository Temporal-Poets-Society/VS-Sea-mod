# Celestial water and night visibility — 0.4.4

**0.4.7-night (25 October 2026):** night levels, twilight, the moon's light and weather are recalibrated; see
[night light](night-light.md). The night numbers below are 0.4.4's.

**0.4.4 (21 October 2026), after the first night play-tests (SEA-D35, SEA-D36).** Star reflections are removed: on
small waves they showed as blotches ("leopard skin"), and at the default strength they were invisible. In their place
the water reflects **the sky the game draws**, by Fresnel through Sea's wave normals: barely anything looking down, the
sky's own colour toward the horizon, so the sea meets the sky there instead of lying black under it. The colour is the
game's own sky function (`skycolor.fsh`: `sky.png` by sun and view height, `sunlight.png` glow, its fog). At night that
function returns the fog colour with alpha = its fog amount (`mix` to `vec4(rgbaFog.rgb, fogAmount)` by `1 - dayLight`),
and the dome shows rgb × alpha over dark space; since 0.4.6 the reflection takes that, not the bare fog colour, which
0.4.4 and 0.4.5 reflected as a red-brown band on the horizon (reported in game). AstraTerra's
Milky Way and stars are not reflected (the user's choice). Wmap's distant sea gets the same reflection through Sea's
optional adapter, so it does not stop at half the view distance. Fog no longer dims the glints or the night floor (the
game fogs the scene itself). Night visibility at strength 1 is now 0.15 clear-moonless and 0.45 under a high full moon
(the user found 0.4.3's strength 2–3 right in game; existing `sea.json` files at 1 get the new level). Sections below
that mention star reflections describe 0.3.8–0.4.3.

pre.3: sun/moon glints were invisible in pre.1/pre.2 (added RGB without alpha in a premultiplied OIT pass); they now
mix toward the light and raise alpha like vanilla's highlight. 0.3.8-pre.1 crashed on its first in-game frame (null camera matrix); pre.2 fixes it. See [verification](verification-celestial.md).

Implemented 17 October 2026. **Pre-release: no graphical in-game comparison has been run.** Shader compilation,
GPU equation/resource tests, adapter checks and a disposable vanilla server self-test are separate evidence below.
Comfortable navigation, visual quality, actual ship interiors and the Wmap seam still require the comparison.

## Inspection and sources

Primary evidence was the locally installed Vintage Story **1.22.7** shaders and decompiled assemblies, the exact
installed **AstraTerra-0.15.0.zip**, Sea production source and Wmap 0.4.1 source. No astronomy/geography/ship assembly
is a new build or manifest dependency. Inspection outputs and test logs are in `obj/sky-inspection/` (not packaged).

| Mechanism | Verified source behavior | Implementation choice |
|---|---|---|
| Vanilla water (`chunkliquid.fsh`, `ChunkRenderer.RenderOIT`) | One procedural sun highlight; `sunPosRel` is the sun, not the moon. `SunSpecularIntensity` comes from a screen occlusion query in `SystemRenderSunMoon.OnRenderFrame3DPost` | Retain the original highlight behind `CelestialReflections = 0`; otherwise replace it, with no second sun highlight. Use source directions and local sunlight/shadow exposure, without the screen query |
| Water normals (`chunkliquid.vsh`, Sea `WaterShader`) | Sea previously displaced vertices but left flat packed normals; vanilla's highlight invented unrelated noise normals | Analytic gradients of the same uploaded Sea trains, divided by the same oceanity/weak-wave factor, interpolated between vertices |
| Vanilla stars (`SystemRenderNightSky`, `nightsky.vsh/fsh`) | A complete six-face star cubemap rotated by a model matrix. It exists independently of the view frustum. `ClientPlatformWindows.Load3DTextureCube` uploads only level 0 with linear filters | Copy all faces to a private, at-most-512px cube and generate mipmaps; sample with the inverse source rotation. No modification to the sky renderer's texture |
| Engine `sky`/`glow` textures (`SystemRenderSkyColor`, `skycolor.fsh`) | Colour lookup images, **not captures of rendered sky objects**. They contain neither the rendered moon nor AstraTerra stars. At night the sky dome is drawn as the fog colour | (0.4.4) Sampled for the Fresnel sky reflection, like `getSkyColorAt` without its dither; bound to units 10 and 11 when the water program is used |
| Vanilla moon (`ClientGameCalendar.Update`, `SystemRenderSunMoon`, `celestialobject.fsh`) | Position from the calendar; displayed phase shading uses sun/moon directions. Calendar moon ambient brightness has its own phase table | Vanilla trail uses the source moon direction and illuminated fraction from the displayed sun/moon separation |
| AstraTerra stars (`SkyStarSunMoonRenderer`) | `DrawableStars` contains the projected sky, filtered by horizon/brightness, not camera frustum. Meshes add a residual sidereal rotation below the 0.05-degree projection threshold | A smooth 1024×512 atlas of these stars, refreshed only when source cache inputs change and at most four times a second. Grayscale brightness, not an exact rendering of each sprite; cache/residual/throttle error is below the reflection blur during ordinary time progression |
| AstraTerra moon (`MoonDiscRenderer.Draw`, `LocalMoonTime`, `VanillaMoonPhase`, `MoonDiscMeshBuilder`) | With `MoonArt` pixel/photo, position uses `TotalDays + ObserverLongitude / 360`; terminator uses local `ExactAt`. With vanilla art the vanilla moon remains | Reflect the same local moon and phase when replaced. Check actual renderer replacement flags each frame; no forced change to AstraTerra settings |
| AstraTerra solar/extra light (`LongitudeAwareSunController`, `NearBodyLightPatchHost`) | Solar delegate can change longitude/tilt. Near-body lighting adjusts calendar light for planetshine/eclipses | Read the resulting calendar sun; never add a separate AstraTerra light source |
| Night ambient (`AmbientManager.UpdateDaylight/UpdateAmbient`, `fogandlight.vsh`) | Vanilla already combines sun/moon light, dims scene brightness at night, and multiplies ambient by each surface's propagated sunlight. Block and point lights are separate | A client presentation floor on **ambient sunlight**, not gamma, exposure, `MINBRIGHT`, block lighting or server light levels |
| Weather | Blended cloud/fog modifiers and actual `WeatherSystemClient.GetPrecipitation`, including the rain-cloud overlay. Overcast and haze patterns set cloud thickness 1 | Cloud and rain attenuate the glints and the night floor (overcast keeps 8 %); (0.4.4) fog does not. The sky reflection needs no attenuation: the game's sky colour already darkens under cloud. With Earth's weather, overcast is common at sea |
| Wmap (`FarTerrain.LoadShader`, `farterrain.fsh`) | Separate opaque distant surface; no celestial specular sampler; starts at 0.75× view distance | New near-water effects fade from 0.50–0.74× view distance. Optional shader adapter applies the same ambient floor to Wmap land/water; no Wmap source edit or hard dependency |

The installed `wmap_0.4.1.zip` shader was also inspected: unlike the current Wmap working tree, it lacks the
nighttime haze limit. Sea's optional adapter supplies that limit when `NightVisibility > 0`, without changing
Wmap source or its package. Setting that control to 0 restores the installed shader's original haze behavior. Wmap 0.4.2 has its own night
haze limit; with it Sea adds only the ambient floor. Wmap 0.4.3 lights far terrain by the game's ambient colour (`wmapAmbient`)
instead of the sun colour; the adapter floors whichever of the two light terms it finds and otherwise leaves the shader alone.

Installed AstraTerra package SHA-256:
`2757eb941f602f69fa192c6dcbfe4ac3a6e978c31a227237bffa66e1ebcc03ec`.
The inspected installed config uses `StarfieldMode: astraterra`, `MoonArt: vanilla`, `LongitudeAwareSun: true`,
`NearBodyLighting: true`, `GeneratedWorldTilt: true`. Comparison profiles copy that config into their own directory.

## Behavior and scope

Sun glitter and moon trails follow the reflected source direction, with continuous wave slope movement, a wider
lobe as seas grow, horizon fade, weather attenuation and angle-dependent Fresnel weighting. Sheltered water scales
both slope and roughness. (0.4.4) The sky reflection is described at the top; it replaced 0.3.8's faint stars and
blue background. This is a low-frequency approximation, not a textured moon-disc
reflection or a capture of clouds, planets, Milky Way, meteors or constellation overlays.

The added shader writes before the existing fog application; glints and the sky reflection raise alpha by their
weight (the OIT pass shows rgb × alpha), as vanilla's highlight does. Water alpha, underwater effects, depth pass,
contact foam and dry-hull discard remain owned by their existing paths. Since 0.4.7-pre.8 Sea adds **open-sea wind
foam** separately, after reflections and before fog: crest patches, downwind streaks and surface spume (SEA-D46,
[storm evidence](storm-waves.md)). It uses the water's existing light, raises alpha for OIT and leaves vanilla's
depth/contact foam intact. It requires animated, upward, sky-exposed water above the camera's underwater threshold;
oceanity suppresses it in sheltered/weak-wave water and dry-hull discard still runs first. It remains on with glints
and sky reflection disabled and with vanilla's foam graphics option off, as Sea's waves do with waving-stuff off.
Since pre.9, moving-source wake foam shares this same light/alpha/fog path (SEA-D48, [wake notes](wakes.md)).
Its mask samples the displaced triangles, tests their original water level and runs after the existing dry-hull
discard. Wakes also work on sheltered/covered water; only wind foam requires oceanity and sky exposure. Wake foam
fades out by min(120 blocks, 0.68 view distance), before Wmap starts at 0.75. Wakes add no displacement, depth-pass
work or far-water consumer. The optional bow-spray particles also veto dry-space births/predicted endpoints.
It shares the celestial shader compile fallback, so that fallback also removes wind foam and wakes. Added reflections require upward, sky-exposed
water and a camera above water. The eye ray comes from the inverse camera matrix, including third-person offsets.
Directional sun shadows are retained; moon and star occlusion use the water's existing skylight exposure flag,
not a new terrain ray trace. Weather attenuation is a local average, not exact occlusion by each visible cloud.

Night visibility extends Sea's water-only responsibility by one small client environment hook. At default strength,
the clear, fully dark ambient target is 0.15 for a moonless sky and up to 0.45 with a high full moon (0.4.4; it was
0.075 and 0.225) (blue channel;
red ×0.78, green ×0.88). Twilight fades it out smoothly. These are **deliberate gameplay values**, brighter than
physical starlight. They aim to make shores and decks readable while leaving lanterns useful for colour, detail and
enclosed spaces. On 21 October the user judged the moonless level in game: 0.15 right in a dark room, 0.225 with a light near
the monitor (`.sea night 1.5`). The full-moon level is not yet judged.

The floor takes a maximum with existing lighting. For AstraTerra's pixel/photo moon only, a known vanilla calendar
moon contribution can be scaled down when that moon disagrees with the displayed local moon. This scaling is
limited to the unmodified sun/moon calendar maximum; stronger extra light and eclipse changes are preserved.
Calendar fields, exposure/gamma and light propagation are unchanged. Every rendered surface still attenuates sky
light by its sunlight channel. A sealed block room/cave with no sunlight receives essentially none of this floor;
openings retain ordinary light spill. The floor/correction are disabled underwater and outside dimension 0.
Moving ships still supply their own interior lighting: Hansa/Magelan's existing interior light overrides are outside
Sea's authority and require the ship comparison. No new enclosure, room, save or ship API is introduced.

If AstraTerra's near-body catalogue hides the vanilla moon, Sea suppresses the vanilla moon trail. Reflections of
arbitrary near-body planets/replacement satellites are outside this implementation. If optional adapter members
change or a texture fails, Sea logs the failure and suppresses unsupported star/moon trails. A water compile failure
first retries without celestial effects, then retains the existing no-dry-cut/vanilla-water fallbacks. Wmap's optional
floor has its own compile fallback. None of these fallbacks establishes visual compatibility with other shader mods.

## Configuration

Server `ModConfig/sea.json`, sent to clients (matching Sea version required). Existing files gain defaults on load.

| Key | Default | Meaning |
|---|---|---|
| `CelestialReflections` | 1 | Sun/moon highlight strength. 0 restores vanilla's highlight |
| `SkyReflections` | 1 | (0.4.4) The sky reflected by Fresnel, near and on Wmap's distant sea. 1 is physical; 0 disables |
| `NightVisibility` | 1 | Outdoor ambient floor, real twilight and local-moon correction (0.4.7-night: 1 = moonless 0.10, full moon 0.40; 0.4.4: 0.15 / 0.45). 0 keeps the game's own night and twilight |

Tuning by eye (0.4.3): on a client, `.sea night <0–4>`, `.sea sky <0–4>` and `.sea glint <0–4>` change these three
strengths on that client at once, until you leave the world; `.sea night` alone shows the value, `.sea night default`
and `.sea reset` go back to `sea.json`, which they never change.

Values are limited to 0–2; invalid/negative values return to defaults. All three at 0 disable the celestial appearance;
wind foam follows the wind and WaveScale independently.
Zero values are explicitly serialized. Restart the server/client after config edits, or use the `.sea` tuning commands above.
`.sea` reports shader activation, the moon's source, clear-sky factor, sun/moon altitudes, illuminated fraction, sky
daylight, the three strengths in effect, ambient target and total days. A target is not proof of visible brightness or an active shader.

## Performance

No new sky render passes or per-pixel wave loop. Wave gradients add at most 24 cosines per water vertex. Since
pre.8 the foam crest signal adds up to 24 sines in that same loop and five procedural value-noise samples per
eligible water pixel; [storm evidence and remaining frame-time check](storm-waves.md). Water
pixels add two smooth lobes and two reads of the game's own small sky textures (units 10 and 11); Wmap's distant sea
adds a few arithmetic operations to the sky colour it already computes. (0.4.4) No textures, copies or CPU work of
Sea's own remain: the star cube (≤ 8 MiB), the star atlas (≈ 1.33 MiB GPU + 2 MiB CPU) and its rebuilds (≈ 4.5 ms of
CPU at up to 4 Hz) are gone. No in-game FPS/frame-time impact has been measured. Weather sampling is limited to 4 Hz.

## Verification performed

| Category | Result and practical limit |
|---|---|
| Build | Debug build passed with zero warnings/errors; Release/package result recorded in `verification-celestial.md` |
| Automated offline | Existing suite plus night ordering, weather attenuation, disable, local-moon correction, source anchors and config serialization. Final result recorded in `verification-celestial.md` |
| Exact optional adapter | `tests/SkyIntegrationCheck` initializes the actual adapter against the DLL extracted from the installed ZIP, checks the real star struct, longitude correction and Wmap source anchor; deterministic atlas/seam/subpixel-energy checks pass. Separate process checks absence of AstraTerra. No client rendering exercised |
| GPU | AMD Radeon RX 6600, OpenGL 3.3, driver 26.8.1. Water/particle shaders and new celestial variants compile/link at low/high settings, dry cut enabled/disabled; Wmap shader adapter also compiles/links. GPU lobe alignment/broadening/horizon and cube-copy/mipmap/state checks pass. Existing six dry-hull pixel comparisons pass |
| Server | Existing self-test passed in a newly created vanilla-only directory, `testserver/celestial-20261017-210957-8e83a164`. Wind/wave/dry-space regression check only; no rendering |
| In-game | **Not run.** No claim of visual quality, navigability, cave/interior darkness, no shimmer or Wmap seam continuity yet |

## Reproducible in-game comparison (pending)

Build once, then create isolated data profiles from the Sea directory:

```powershell
$vanilla = .\tools\New-CelestialTestProfile.ps1
$vanillaOff = .\tools\New-CelestialTestProfile.ps1 -Baseline
$astra = .\tools\New-CelestialTestProfile.ps1 -AstraTerra
$astraOff = .\tools\New-CelestialTestProfile.ps1 -AstraTerra -Baseline
$mapped = .\tools\New-CelestialTestProfile.ps1 -AstraTerra -Geography -Wmap -Ship
$mappedOff = .\tools\New-CelestialTestProfile.ps1 -AstraTerra -Geography -Wmap -Ship -Baseline
# Launch exactly one profile at a time:
& "$env:APPDATA\Vintagestory\Vintagestory.exe" --dataPath $vanilla
```

The script copies packages into new directories; it never installs into the regular Mods directory or copies player
saves. It requires the exact named installed packages for each selected option and refuses missing ones. Seed is
4242; world size 8192×8192×256. Start a creative world only in the selected data directory. Establish the scene once,
quit fully, and copy that **disposable** closed world's save to an effects-off sibling profile of the same geography
if exact geometry is needed. Do not copy an open SQLite save or use a player world. Fully quit between package or
profile changes. Keep identical graphics, gamma, view distance and camera coordinates for each pair.

Use an open-sea location with a shoreline, terrain markers at 16/32/64 blocks, a ship when included, a closed
stone room and a covered cave/tunnel with no skylight. Place one lantern/torch outside and one inside; compare lit
and unlit versions. Also test underwater and a dry ship hold below the waterline. Record coordinates, dimensions,
total days and `.sea` output; capture fixed-camera pairs and a slow pan/30-second stationary clip.

Time/weather controls verified from the installed command source:

- `/time set day`, `/time set sunset`, `/time set midnight`; `/time stop` holds the test moment. To seek another
  lunar phase, `/time add 1 days` at the same hour and inspect `.sea`. Record the resulting total days, rather than
  assuming a phase from time of day. For moonlit night require fraction ≥0.8 and moon altitude ≥20°; for moonless
  require fraction ≤0.05 or moon below the horizon. Include the horizon crossing and both twilight directions.
- `/weather acp off`, `/weather seti clearsky`, `/weather setprecip -1` for clear conditions; check the visible sky
  and `.sea` clear factor after modifiers settle. `/weather seti overcast`, `/weather setprecip 0` for overcast;
  `/weather setprecip 1` for heavy precipitation. `/weather lp` lists installed pattern codes. Restore with
  `/weather acp on` and `/weather setprecipa` in test worlds if continuing normal play.
- `/sea setwind E 0 0 built` for calm water; `/sea setwind E 1 0 built` for rough water. Hold wind fixed between
  on/off pairs. Sea wind is independent of rain/clouds; test both clear rough seas and cloudy calm water.
- Repeat AstraTerra with the copied installed `MoonArt: vanilla`, then change **only the disposable profile's**
  `astraterra.json` to `MoonArt: pixel` (or `photo`), fully restart, and repeat. Test a nonzero world longitude in
  the geography profile, plus a second longitude; record positions/time because the sky is intentionally local.

For every sky state in the table, test calm and rough water, the sun/moon on screen and above/behind the camera
while the reflecting water stays visible, and repeat a slow pan across the horizon. Brightness/trails should not
switch merely because a celestial body leaves the screen. At low view distance, face the same direction from shore
and ship and scan the 0.50–0.75× view-distance handoff with Wmap, checking brightness as well as the disappearing trail.

| Sky state | Open sea / shore | Ship deck / dry hold | Closed room / cave / underwater | Wmap boundary |
|---|---|---|---|---|
| Day | pending | pending | pending | pending |
| Sunrise/sunset twilight | pending | pending | pending | pending |
| Clear moonlit night | pending | pending | pending | pending |
| Clear moonless night | pending | pending | pending | pending |
| Overcast / heavy precipitation night | pending | pending | pending | pending |

Run the table for vanilla and AstraTerra 0.15.0 (installed vanilla moon and pixel/photo moon). Judge gameplay
separately: can the player navigate, identify the 16–64-block shoreline/ship markers, and work on deck without
losing the lantern's advantage? Overcast/storm must be noticeably darker. Reject/tune defaults if those comparisons
fail; automated/GPU tests do not settle them.
