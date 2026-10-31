# Foam direction and texture — issue #35, SEA-D49

**Superseded in part by pre.11 / SEA-D50:** [issue #37](crest-shape.md) deletes the downwind and independent
surface-spume layers and adds actual Gerstner crest geometry plus a local compression requirement for wind foam.
The wind-axis/provider audit and wake-age/texture findings below still apply; windrow tests/renders are historical.

Sea 0.4.7-pre.10, based on accepted/installed pre.9 `b47b9f4`. This corrects foam morphology and makes its source
diagnosable. Wave heights, phases, CPU/ship samples, hull clipping and the accepted 250,000-sample wake raster cap
remain unchanged. No Vintage Story client was launched, install performed or sibling project changed.

## What the screenshot establishes

The reported image shows broad smooth ribbons over crests and a long ribbon toward the viewer. It contains no
compass, provider status or layer isolation. Neither the actual wind strength nor that individual ribbon's
wind-versus-wake ownership can be recovered reliably from the still image. The reproduction proves **both**
pre.9 wind foam and wake foam could form smooth ribbons; it does not pretend to reconstruct the exact camera or
Earth weather state. Vanilla depth/contact foam and reflections are additional independent paths.

With Earth as wind provider, `SeaWindStrength: 0.3`/`SeaWindFrom: 270` are fallback settings, not the world's actual
wind. Pre.9 already took provider wind through the sampled wave place; pre.10 explicitly asks `Model.SeaWind` at
the camera/current game hour for this cosmetic effect, independently of the grid/clock wave sample. Admin fixed
wind still wins; without a provider the configured steady sea wind still applies. No wave sampling rule changes.

Read `.sea`: its first line identifies the provider, `Wind here:` shows local wind, and the new **Foam wind:** line
reports exactly the uploaded strength/from/toward/Beaufort, the smoothed windrow bearing, whitecap input and streak
ramp. Example for an eastward strength-1.2 wind:

```text
Foam wind: 1.20 from W (270°) (toward E, Beaufort 11); rows toward E (3s smoothing);
whitecap input 41.813%, streak ramp 100.0%; view All.
```

The whitecap input is the existing bounded Monahan-style drive, **not a measurement of rendered area**. WaveScale 0,
no active waves, shelter, dry hulls, camera underwater and distance can suppress its visible result.

Use the same camera and existing histories to identify the ribbon:

```text
.sea foam wind   # only Sea's wind foam
.sea foam wakes  # only Sea's wake foam
.sea foam off    # neither Sea overlay; vanilla contact foam/reflections remain
.sea foam all    # restore both
```

Unlike `.sea wakes off`, these views do not erase wake histories or change particle/physics state. A ribbon that
appears only in the wakes view is a wake; one in wind-only is wind foam. If it survives off, investigate the vanilla
contact/reflection paths. `.sea reset` also restores view All. No new persistent config setting is required.

## Axis and sign audit

Sea uses +X east, +Z south; compass bearings are clockwise from north. `Wind.FromCompass` converts a **from** bearing
to a **toward** vector. The shader uses the same shared `seaFoamCoordinates` function as the GPU diagnostics:

```text
along  = dot(worldRelativeXZ, towardXZ) + alongAnchor
across = dot(worldRelativeXZ, (-towardZ, towardX)) + acrossAnchor
```

| Wind from | Wind travels toward | Along coordinate | Across coordinate |
|---|---|---|---|
| W, 270° | E, +X | X | Z |
| N, 0° | S, +Z | Z | -X |
| E, 90° | W, -X | -X | -Z |
| NW, 315° | SE | (X+Z)/sqrt(2) | (-X+Z)/sqrt(2) |

**No 90° swap or reversed travel vector was found.** The old cross/along gradient ratio 7.4 was valid for its
isolated streak-noise term. Its test used a constant trough (`crest=-2`), disabling crest foam, and bypassed the
world-to-wind transform. It therefore proved neither the combined appearance nor compass sign/drift. Pre.10 checks
the actual transform for four game bearings and signed translation: reducing the along anchor by four metres
moves eastward windrows four metres east, with zero diagnostic error. Row bearings retain the existing three-second
turn smoothing; foam drifts 0.8 blocks/second downwind, with faster surface spume. Wave crests can belong to an older,
differently directed swell, so their bearing need not be perpendicular to the current wind at every instant.

## Cause and changes

1. **Crests became ribbons.** A height threshold extended along the whole wave front; a broad low-frequency noise
   gate did not reliably break it into short pieces. New independent metre-scale island/hole masks keep it patchy
   even at high wind. The constant-crest GPU test's longest cross-crest run is 8.88 m over a 64 m domain.
2. **Windrows widened into sheets.** Pre.9 lowered the streak threshold as coverage increased. At strength 1.5,
   almost every noise value passed, giving 98.4% of the diagnostic opacity above 0.1. New windrows have bounded
   widths, roughly four-metre spacing and irregular breaks. Above strength 1.04 storms add thinner interleaved rows
   rather than flooding the image. Their long direction is explicitly the along-wind axis; onset is strength
   0.68 (17 m/s, about Beaufort 8), fully developed by 1.04.
3. **Grain could not make holes.** Old wind foam multiplied by 0.72..1 and wake foam by 0.30..1, preserving an opaque
   floor. New jittered cellular films contain real holes, varied bubble sizes and mottle, with about 12.5 cm cell
   spacing as a visual scale. Unresolved cells filter to a mean instead of shimmering. The receding side of a
   crest opens into lace using normalised falling velocity computed in the existing vertex wave loop; this is a
   visual phase proxy, not a persistent wind-foam simulation.
4. **Wakes resembled wind foam.** Wake grain used the wind coordinate/drift frame. It now uses world coordinates
   independent of wind, and the existing spare atlas channel stores premultiplied freshness from actual node age.
   Young bow/wash is denser; old stern foam develops larger/more holes and torn lace. Shape, lifetime, budgets and
   the shared light/alpha/fog/dry-cut path stay intact. Windrows are thin and wind-aligned; wakes retain the hull
   bow/V/wash and broad curving travel trail. No extra texture, render pass, sample budget or ship force is added.

Primary references: the [NOAA/NWS Beaufort table](https://www.weather.gov/mfl/beaufort) describes isolated white
horses before gale conditions and well-marked/dense foam streaks **along the wind** at forces 8–10. It describes much
more extensive foam/spray at forces 11–12; these diagnostics are a bounded surface appearance model, not a hurricane
visibility forecast. Foam formation/decay depends on plume degassing and bubble-film stability, rather than a
uniform white sheet: [Callaghan, Deane & Stokes (2013)](https://journals.ametsoc.org/abstract/journals/phoc/43/6/jpo-d-12-0148.1.xml),
and [Callaghan et al. (2012)](https://agupubs.onlinelibrary.wiley.com/doi/10.1029/2012JC008147). Patch sizes, cell
scales, phase-based lace and windrow ramps above are Sea's visual choices, not fits to those studies.

## GPU evidence

`dotnet run --project tests/ShaderCheck` with a configured `VINTAGE_STORY` reference root generates
`obj/foam35/index.html`, PNGs and `coverage.csv` (all ignored). It uses the production GLSL projection/morphology,
the exact pre.9 equation as a before fixture, the actual 24-train spectrum (fully developed, key 4242) and the same
wake atlas for before/after isolation. Overview size 64×64 blocks, close-ups 8×8. **Orange arrows are wind travel;
north is up, east right, south down.** Four strengths have before/after images; separate pages/tiles show rotated
bearings, wind-only/wake-only/combined and fresh/old cellular film.

These are GPU function renders over schematic blue water. They do not reproduce the screenshot, perspective,
intra-block vertex interpolation, vanilla depth/contact foam, scene lighting or in-game spray. Actual low/high
water/depth/fallback shaders are also compiled/linked independently on AMD RX 6600/OpenGL 3.3. Thus the evidence
proves equations, coordinates and composition, not in-game visual acceptance.

| Actual strength | Whitecap input | Mean mask pre.9 → pre.10 | Area opacity >0.1 pre.9 → pre.10 |
|---|---|---|---|
| 0.3 | 0.118% | 0.063% → 0.055% | 0.00% → 0.00% |
| 0.8 | 10.492% | 6.069% → 1.490% | 13.65% → 4.17% |
| 1.2 | 41.813% | 31.069% → 2.969% | 39.52% → 9.18% |
| 1.5 | 89.491% | 84.123% → 5.894% | 98.43% → 21.56% |

Mean opacity and thresholded area are different quantities; neither equals the empirical whitecap input. The new
high-wind pattern contains more/denser fine windrows while retaining water gaps. Absolute storm whiteness remains
an in-game tuning question. A true 0.3 breeze cannot produce the extensive wind foam in this screenshot through
either tested equation alone; Earth weather, wakes and the independent vanilla paths must be distinguished with
status/layer captures, not guessed from fallback config.

Additional checks cover provider precedence, wind-from conversion, wake freshness, the unchanged raster cap,
finite crest runs, cellular holes and older lace, subpixel filtering, four rotated bearings (cross/along gradient
ratios about 8.5), signed drift, periodic anchoring, wind-independent wake texture, lossless layer isolation and
existing atlas expiry/dry-hull/far-distance behavior. Shared wave code is untouched.

Final result: warning-free Debug build, **179 offline checks and 118 GPU checks pass**. Logs are ignored
`obj/foam35-build.txt`, `obj/foam35-tests.txt` and `obj/foam35-gpu.txt`. Renders and CSV are regenerated by the
checked-in test; no game assets or external image library are embedded in these artifacts or new source files.

## In-game follow-up

Capture `.sea` plus the compass at the reported scene, then toggle all/wind/wakes/off without moving. Compare the
same ribbon in every view. Check close foreground bubble texture, patches breaking along crests, thin downwind
windrows at forces 8+, and old ship trails fading into lace. Pan/turn and check drift/sign at a steady wind. Test
low graphics, dry holds, shelter, camera underwater, Wmap handoff and shader/world reload as before. At actual
strengths 1.2–1.5 judge density and frame time as well as texture; tune only after those views identify each source.

The coordinator must update suite ROADMAP #35/overview/inventory outside this authorized working copy. Mark
implemented with offline/GPU evidence, **play-test pending**, rather than claiming the still image's ownership solved.
