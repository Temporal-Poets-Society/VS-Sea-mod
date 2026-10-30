# Storm waves — issue #28, SEA-D46 (30 October 2026)

Implemented in 0.4.7-pre.8; uncommitted work for the coordinator. No game client, installation or save was used.
The issue is the force-12 sea off Sri Lanka (6.6°N, 82.7°E): small regular waves and glassy reflections over a few
swells, without breaking crests or wind foam. Appearance and gameplay acceptance still require an in-game look.

## Cause and shared surface

`WindModel.SeaAt` already integrates 16 past game hours without gusts, using fast (3-hour) and slow (10-hour)
exponential weights. Significant height is `max(fast, 0.75 slow)`, based on `(0.1 + 2.4 strength²) * WaveScale`.
This gives deliberately smaller heights than a physical fully developed storm: 5.5 blocks at strength 1.5 by default.
`StormHeightScale` (default 1, range 0–4, appended protobuf member 15) multiplies developed height by
`1 + (StormHeightScale - 1) * smoothstep(0.4, 0.8, strength)` before the history integration. Thus a tall storm's
swell still builds and decays through the same history; the default breeze and instantaneous short texture stay as before.
The server sends this setting including zero, old configs default to 1, and a changed setting rebuilds the client's model.
There is **no separate hard total-height/ship-safety cap** in Sea. Keep this gameplay scale and the existing
`0.05 * wavelength` per-band amplitude/steepness ceiling. Hull calming/cutting is a separate local operation.

Previously `SeaAt` inferred period as `2.5 + 3.2 sqrt(height / max(0.1, WaveScale))`, and `WaveSpectrum.Amplitudes`
clamped its wavelength to 10–150 blocks. The library had 12 swell lengths (4–181 blocks), each in 18 directions,
with log-band amplitude roughly proportional to wavelength below the peak: similar slope per short band.
The two extra 6.5/4.3-block chop bands grew as `0.03 + 0.12 strength`, even after the swell peak hit its clamp.
At 1.5 their primary amplitude was 0.21 blocks. Their short wavelengths make them disproportionately strong in
the analytic reflection normals (`amplitude * wave number`); white lines in the report are highlights, not a
storm-breaking simulation. Vanilla's depth/contact foam could not provide the missing open-sea whitecaps.

The production route remains `SeaSystem.SampleWaves` → `WavePlace.Sample` → `WaveSpectrum.Amplitudes` → continuous
strongest-24 selection. `HeightAt` sums those sine trains on the CPU; `FillUniforms` reduces their phases at the
camera in doubles; `WaterShader.seaWaves` sums exactly those trains for both water passes. Both apply the same
oceanity factor (1 in open sea, 0.2 in sheltered water). Wmap receives those same wave uniforms for normals.
No render-only displacement, CPU/GPU height divergence or different ship spectrum was introduced.
The pre-existing spatial limitation remains: all client water uses the camera's trains; a faraway server ship
samples its own location in a spatially varying wind field (open issue O1).

Directions still split energy between the two nearest 20° directions on a staggered per-band grid; shorter
swell bands spread up to ±40°. Fixed random phases, ring-compatible wave vectors, deep-water dispersion,
the anchored wave clock, one-second amplitude blending and the 64-block input grid remain unchanged.

## Physics and deliberate approximations

The [WMO Guide to Wave Analysis and Forecasting, section 1.3.3](https://www.imgw.pl/sites/default/files/2021-06/guidetowave-analysisandforecasting.pdf)
describes PM for unlimited duration/fetch, with `fp = 0.877 g / (2π U)` and frequency spectrum
`S(f) ∝ f^-5 exp[-1.25 (fp/f)^4]`. Thus `Tp = 2π U / (0.877 g)` and deep-water
`Lp = g Tp² / (2π)`, proportional to wind speed squared. This is an angular-frequency relation; `0.8 U/g`
alone is not the peak period in seconds. PM is sufficient here; no JONSWAP peak-enhancement factor is added.
The guide also gives fully grown `Hm0 = 0.0246 U²`; together these imply Lp/Hm0 about 34. Choosing 30 for Sea's
compressed spectrum retains similar steepness instead of combining full-scale wavelength with reduced height.

**Review correction:** the first pre.8 attempt used that physical wind-to-period conversion alongside Sea's compressed
5.5-block height. It produced a 1024-block modal wave (Hs/L about 1/186), with short slope reduced to 0.010: too flat
and mirror-like. The coordinator rejected it. Compressing height requires compressing wavelength consistently.
The revised built-storm peak is **`Lp = 30 * gameplay Hs`**, the coordinator's requested 25–35 range. Height still
grows approximately with wind squared, and increasing `StormHeightScale` now increases wavelength in proportion.
The blend from the old breeze period uses the **history's normalized reference height before StormHeightScale**,
not today's wind or the selected multiplier, retaining swell
after a lull. This is a PM-shaped gameplay sea, not a claim that 5.5-block waves physically have a hurricane period.
Sea's existing duration handling remains; it does not yet trace coastlines or compute geographic fetch (SEA-P01).

Convert `S(f) df` to variance per logarithmic wavelength bin: amplitude weight
`w(r) = r exp[-5 r² / 8]`, `r = wavelength / Lp`. Normalize to the existing significant-height scale before the
unchanged steepness cap. The strongest logarithmic band is near `0.894 Lp`, not exactly the frequency-density peak.
Use a smooth blend from the old breeze shape at strength 0.4 to PM at 0.8. Extend the library to 20 swell bins,
4–2896 blocks; defensive peak clamp 1600 (above the 660-block peak at strength 1.5 and StormHeightScale 4, WaveScale 1).
Taper the tail below Lp/8 and above about 2.8 Lp to fit the 24-train budget. This is a sparse PM approximation,
not an ocean FFT or full breaking-water solver. The equilibrium tail no longer grows at fixed short lengths;
the remaining 6.5/4.3/9.7-block texture bands saturate at fixed amplitude, independent of storm height and peak length.
The combined slope variance of all trains shorter than 16 blocks (including short swell bands) is capped smoothly
at RMS slope 0.065 × WaveScale in storms. Thus texture stays approximately as rough as the breeze (0.0646), without
growing with wind or vanishing. The first two retain their old seeds and phases for calm/moderate seas.

[Monahan and Ó Muircheartaigh (1980)](https://journals.ametsoc.org/abstract/journals/phoc/10/12/1520-0485_1980_010_2094_opldoo_2_0_co_2.xml)
give the robust fit `W = 3.84e-6 U10^3.41`. Sea smooths its onset over 5.5–10.8 m/s and bounds it at 0.95.
It is an appearance control, not an exact foam fraction or a validated hurricane extrapolation. The
[US National Weather Service Beaufort table](https://www.weather.gov/boi/beaufort) describes progressively stronger
foam streaks, and a sea whitened by foam/driving spray at force 12. Those descriptions guide the storm endpoint.

## Wind foam ownership and implementation

Vanilla retains its depth/contact/shore foam, flow, water alpha setup, lighting, underwater and fog paths. Sea adds
wind-driven open-sea foam in `CelestialShader`, after reflection shading and before fog. The crest signal is the
normalized height of the retained long trains, computed per vertex in the existing normal loop. An absolute
steepness threshold would incorrectly eliminate all breakers on the long, height-compressed storm swell.
Noise breaks up crest patches and produces irregular streaks elongated downwind, with a faster advected surface
spume component above strength 0.9. These are **surface cues**, not airborne spray particles or overturning geometry.

Oceanity suppresses it toward sheltered water and removes it for weak-wave liquids. Upward, sky-exposed,
animated water and an above-water camera are required. Non-dry hull calming also damps wind foam; dry-hull discard
still runs first. WaveScale 0 and failed
or disabled wave uploads send zero coverage; world exit clears the new uniform too. Foam uses the water's existing
vertex lighting/shadows, raises OIT alpha, then takes ordinary fog. Reflection strengths 0 and low graphics keep
wind foam active; the celestial shader compile fallback removes it together with the added reflection code.

The cosmetic foam frame tracks camera translation in doubles and eases toward the local wind over three seconds.
Rotation occurs about the camera, avoiding huge noise shifts from tiny gusts far from the world origin. Its
procedural coordinates wrap continuously; spatial noise and fine grain use derivative filtering. Foam is not
part of ship physics and does not claim a deterministic persisted foam history. Cost: an additional sine/variance
calculation in the existing ≤24-train vertex loop and five value-noise samples per eligible water pixel. No new
draw pass, texture, particle pool or per-pixel wave loop. Actual frame time remains unmeasured.

## Before/after evidence

`tests/SeaTests/StormEvidence.cs` freezes the pre.7 spectrum equations and evaluates both versions using the same
key 4242, fully developed east wind, WaveScale 1, wave time 100, at StormHeightScale 1 and 2.2. Metrics include the
retained trains and chop; pre.7's comparison always uses its original height, not the new optional multiplier.
Lp is the spectrum's peak parameter; the modal band is the band carrying most variance. Short slope is RMS slope
from wavelengths below 16 blocks. Values are open sea (no shelter/hull). Generated CSV: `obj/storm-spectrum.csv`.

| Strength | Peak parameter, blocks (before → after) | Modal band, blocks | Significant height, blocks | Short RMS slope | Foam target after |
|---|---:|---:|---:|---:|---:|
| 0.3 | 28.9 → 28.9 | 32 → 32 | 0.384 → 0.384 | 0.0646 → 0.0646 | 0.118% |
| 0.8 | 67.9 → 49.1 | 64 → 45.3 | 1.688 → 1.621 | 0.1180 → 0.0650 | 10.492% |
| 1.2 | 113.7 → 106.7 | 128 → 90.5 | 3.602 → 3.564 | 0.1606 → 0.0624 | 41.813% |
| 1.5 | 150.0 → 165.0 | 128 → 128 | 5.543 → 5.505 | 0.1938 → 0.0624 | 89.491% |

Taller option, `StormHeightScale: 2.2` (same foam target and short texture):

| Strength | Peak parameter, blocks | Modal band, blocks | Actual Hs, blocks | Lp / actual Hs | Short RMS slope |
|---|---:|---:|---:|---:|---:|
| 0.3 | 28.9 | 32 | 0.384 | 75.2 (unchanged breeze) | 0.0646 |
| 0.8 | 108.0 | 90.5 | 3.607 | 29.94 | 0.0624 |
| 1.2 | 234.7 | 181.0 | 7.827 | 29.99 | 0.0624 |
| 1.5 | 363.0 | 362.0 | 12.102 | 29.99 | 0.0624 |

Default breeze surface/phases agree with pre.7 within 1e-10 blocks in 200 samples at both height settings. Storm
long-wave energy (wavelength ≥ Lp/4) exceeds 92% at both scales. Default storm Lp / actual Hs is 30.28, 29.94, 29.97,
and short texture remains 0.0624–0.0650. At the issue's large world coordinates the uploaded float uniforms match
the CPU samples within 0.001 blocks for every strength at both scales. A heading/transition sweep retains at least
96% of the height budget; no stronger amplitude rescaling is applied on 24-train selection.

Debug build: zero warnings/errors. SeaTests passes 150 checks, including wind/provider/history, sampling/clock/ring,
default preservation, storm spectrum, CPU/uniform agreement, 24-train transitions and existing hull regressions.
Both table settings and storm-height settings 0/0.5/1/2.2/4 retain the equilibrium texture; serialization preserves
zero and 2.2 and defaults old configs to 1. The public four-argument SeaState constructor remains intact for ship
binaries; production samples carry the unscaled history's shape blend internally, so reducing storm height does
not accidentally switch a hurricane back to the breeze spectrum.
ShaderCheck passes 81 checks on AMD Radeon RX 6600, OpenGL 3.3, driver 26.10.1.261008: low/high graphics, both water passes,
dry cut on/off, all new uniforms live, and the six CPU/GPU dry-hull pixel comparisons unchanged.
Three further cases check the actual caravel profile for [issue #30](hold-water.md).
Its additional foam diagnostic calls the actual GLSL function on a synthetic crest/trough field; it checks ramp,
crest preference, zero coverage, downwind anisotropy and coordinate-wrap continuity. Diagnostic masks are written
to `obj/storm-foam-*.ppm` (blue underlay with white foam). These are not game renders or measured ocean coverage.

| Strength | GPU mean foam mask | GPU area with mask > 0.5 |
|---|---:|---:|
| 0.3 | 0.0010 | 0.0% |
| 0.8 | 0.1013 | 8.0% |
| 1.2 | 0.4394 | 47.1% |
| 1.5 | 0.8740 | 98.9% |

The field covers two synthetic crest/trough cycles across 128 × 128 blocks, sampled at half-block pixels.
Downwind streaks' cross/along gradient ratio is 7.4; wrapping the world anchor by 65536 blocks changes no pixels.

## Remaining in-game checks / coordinator handoff

Use an isolated test world, identical camera/light and `/sea setwind W <strength> 0 built`, for all four strengths.
At 0.3 retain the familiar breeze; at 0.8/1.2/1.5 inspect dominance of long swell, broken crests, irregular foam
streaks, drift and spume. Compare both default and StormHeightScale 2.2; the latter moves the whole ocean and ships.
Repeat in overcast/day/night, wind turns and calm-after-storm, lakes/coast/weak-wave liquids, underwater, dry and
flooded ship holds. Check ship heave/pitch/roll, shore intersections, paused/reloaded worlds, multiplayer agreement
and frame time. No shader-only test establishes those outcomes.

**Ship safety:** Hs is four standard deviations, not a maximum crest/trough bound; individual and superposed waves
can be larger. Opting into 12-block Hs changes heave, pitch/roll and wave speed as well as height. Sea does not cap
per-ship accelerations, keep decks above crests, or guarantee dry-space/collision margins for taller waves. Ship mods
own those responses and their hull bounds. Test in a disposable world before choosing the taller setting; inspect
shallow coasts, moored vessels, crew/cargo motion, dry holds and wave/hull intersections. Default 1 preserves the
previous height scale. WaveScale also multiplies heights/texture; large combinations can reach the defensive
1600-block peak clamp and are outside the documented calibration. No live save or ship was tested here.

Wmap receives the corrected spectrum through its existing ≤24-train contract. It has **no wind-foam consumer** in
this change. Near foam fades with the existing 0.76–0.80 view-distance water edge; inspect that handoff at force 12.
If a visible smooth far-sea band remains, Wmap needs a matching foam implementation/contract in its own worktree.
Airborne spume/spray also remains outside this surface-shader change. No Wmap/AstraTerra binary integration or
dedicated-server self-test was rerun; they are not claimed by the water GPU tests.

Manifest is 0.4.7-pre.8. Coordinator: build/package without replacing an existing release snapshot, run the suite
inventory update after the manifest/package change, and update suite ROADMAP row #28 to implemented/offline checked,
not installed or seen in game. Those suite files lie outside this authorized working copy and were left untouched.
