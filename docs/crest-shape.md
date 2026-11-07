# Crest geometry — issue #37, SEA-D50

Sea 0.4.7-pre.11, on accepted pre.10 (`94201e0`). No game client, installation or save used.
The play-test image at St Lawrence Island shows downwind dotted rows in shallow water and rounded crests.
The rows are the pre.10 windrow layer, not evidence of an axis swap. That layer is deleted, along with the
independent surface-spume mask and its ramp/status code. Vanilla contact/depth foam and client wakes remain.

## Cause and change

Pre.10 displaced water vertices only vertically, by a sum of sines. Its foam could appear independently of crest
breaking: windrows and spume had no local geometric breaking requirement. Oceanity attenuated them but did not
make them follow actual crest shape. A visible seabed alone does not establish low oceanity or weak local wind;
Earth supplies wind in this world. Read the **Foam wind** line in `.sea` / `.sea foam`, including its Beaufort
and whitecap input, rather than the fallback `SeaWindStrength`. The input is not measured rendered coverage.

The same 24 retained trains now form a parametric Gerstner-style surface. For parameter position `q`,
`theta_i = dot(k_i,q) - omega_i t + phase_i`, unit travel direction `d_i`, and existing vertical factor `f`:

```
X(q) = q + sum(Q_i A_i d_i cos(theta_i))
Y(q) = restY + f sum(A_i sin(theta_i))
Q_i = shape * weight(|k_i|)
weight(k) = 0.15 + 0.85 / (1 + k² / 0.09)
shape = min(4, 0.65 / sum(|A_i| |k_i| weight(|k_i|)))
```

Horizontal motion brings vertices toward crest tops and separates them in troughs. The current retained
amplitudes and wavelengths determine steepness, including partially developed seas and StormHeightScale.
Long waves receive most shaping; the existing saturated short texture retains its vertical amplitudes.
There is no new wind threshold or change to the spectrum, periods, directions, phases, train selection,
history, grid interpolation or anchored clock. All limits are computed from the current blended trains,
not `WavePlace.Sea`/`.Wind` (nearest-corner reports). No new config or network member is needed.

`J = dX/dq = I - sum(Q_i A_i |k_i| sin(theta_i) d_i d_i^T)` is symmetric. Its eigenvalues are at least
`1 - sum(|Q_i A_i k_i|) >= 0.35`: the map never folds or loops, even with all crests aligned. The same bound
makes `q <- worldXZ - displacement(q)` a contraction. `WavePlace.HeightAt` uses at most 24 iterations,
stopping at a parameter change below 1e-7 blocks, then evaluates height at that parameter. It does not evaluate
the sine height directly at the displaced world coordinate. It allocates no sampling buffers.

The horizontal map deliberately has **no vertical oceanity factor**. Thus existing ship callers using
`place.HeightAt(x,z) * sea.WaveFactorAt(x,z)` remain correct; reducing both horizontal and vertical orbits with
the shader divisor would break that API. `SeaSystem.WaveFactorAt` now looks up oceanity at the inverse mesh
coordinate, since that is where the game packed the shader's factor. This also matters across shelter gradients:
sampling the factor at displaced world X/Z would be wrong. The existing bounded server probe cache serves both
height and inverse-shelter queries. Sheltered heights/slopes still scale down exactly as before.
Both colour and depth passes use `WaterShader.Geometry`; CPU `SurfaceAt` exposes the forward map for diagnostics.
`seaWaveShape` accompanies the existing wave uniforms. Normals use `inverse(J) * dY/dq`, with the existing
pixel-footprint slope filtering and roughness retention. No per-pixel wave loop is added in production.

Dry clips still discard the **displaced fragment position**, including its new X/Z; wake atlas sampling also uses
that position and retains its original-rest-height check. Non-dry hull calming now locates its footprint at the
displaced X/Z. Hull calming, flooded-water exceptions and the visual perception warp remain existing local
exceptions to the undisturbed ship-sampled sea.

## Breaking foam, shelter and ownership

The vertex differential supplies normalized long-wave height, local compression
`max(0, 1 - smallestEigenvalue(J)) * f * hullCalming`, and normalized falling velocity. Wind foam requires:

- A high crest: normalized height above 0.8, fully enabled by 1.65.
- Local compression above 0.22, fully enabled by 0.40. A sheltered factor of 0.2 cannot pass this gate:
  even the global worst case is `0.65 * 0.2 = 0.13`. Covered/lava/underwater surfaces retain their exclusions.
- Independent metre-scale island/hole masks. Local Monahan-style wind input increases patch density **within**
  eligible crest tops; no wind-only overlay remains.
- A short trailing shoulder: positive falling phase opens bubble holes into lace, and fades the patch out
  between normalized fall 0.35 and 1.8. It cannot become a long downwind trail.

**0.6.0 (SEA-D70): storm-eased gate.** With `storm = smoothstep(0.1, 0.9, coverage)` the compression gate moves
from 0.22–0.40 to 0.14–0.30, the height gate from 0.8–1.65 to 0.35–1.2, and the shoulder's fade end from 1.8 to
2.6. Sheltered water still cannot pass (0.13 < 0.14). At force 12 the eased gate whitens 25 of 400 test places,
the calm one 4. `WavePlace.Whitecap(height, compression, coverage)` is the CPU copy; crest spray uses it.

These are tunable **incipient-breaking presentation thresholds**, not a hydrodynamic overturning solver.
The no-loop surface intentionally never actually overturns. Foam age here is a phase proxy; wake film keeps its
real node age, world coordinates, atlas, lighting, lifetime and accepted 250,000-sample raster budget.
The existing water light/alpha/fog path and vanilla-owned contact/depth foam stay in place. `.sea foam
all|wind|wakes|off` still isolates Sea's overlays without losing histories.

## Physics references and deliberate limits

[Mark Finch, GPU Gems chapter 1, “Gerstner Waves”](https://developer.nvidia.com/gpugems/gpugems/part-i-natural-effects/chapter-1-effective-water-simulation-physical-models)
describes lateral motion concentrating vertices at crests, differentiable surface normals, and the bound on
the summed steepness needed to avoid loops. Sea uses a stricter bound to leave a safe inverse-sampling margin.
The long-wave weighting and maximum orbit multiplier are presentation choices for Sea's compressed gameplay heights.

[Jerry Tessendorf, Simulating Ocean Water, section 3.6 “Choppy Waves”](https://people.computing.clemson.edu/~jtessen/reports/papers_files/coursenotes2002.pdf)
uses horizontal displacement and its Jacobian/eigenvalues to locate folding regions and motivate breaking/foam
indicators. Sea triggers before a fold and forbids folding, so this does not claim physically simulated breakers.
The PM spectrum, gameplay height/wavelength compression and wind input references remain in [storm-waves.md](storm-waves.md).

**Shoaling is a proposal.** Oceanity is a coarse shelter field, not bathymetry. A depth-aware wavelength,
refraction and depth-limited breaking model needs a cheap deterministic depth field shared by both sides,
including unloaded shores. A shader-only depth trick would make ships disagree with the water; it is not added.
The existing Wmap consumer still receives the same wave spectrum and filtered far normals. Sea offers the
new optional shape uniform if a future Wmap consumer declares it; its current flat far geometry is not changed.
Check the near/far join in game rather than claiming crest geometry throughout Wmap's far sea.

## Offline and GPU evidence

Run `tests/SeaTests` and `tests/ShaderCheck` with `VINTAGE_STORY` pointing to a development reference directory.
The shader check compiles the actual game water/depth shaders with Sea's patches at low/high settings and with
the dry cut on/off. Its hidden OpenGL context does not launch the game. Generated evidence is ignored under
`obj/crests37/`: `index.html`, twelve PNG views, `geometry.csv` and `foam.csv`. No game assets are embedded.

All comparisons use key 4242, fully developed sea, wave time 300 s, wind from W270° travelling east (+X).
Before uses pre.10's vertical-only geometry (`Q=0`); after uses the production Gerstner functions and unchanged
trains. Side cross-sections label their enlarged vertical axis; low-angle views use physical proportions and a
one-block mesh. Lighting is a schematic sunset. Before foam is hidden to isolate shape, so these are geometry
comparisons, not full pre.10 game frames. After uses production crest foam, with wakes off. Arrows show travel.

| Strength | Spectral Hs, before = after (blocks) | Target Lp, before = after (blocks) | Shape multiplier | Sum steepness bound | 1-block mesh max / RMS height error (blocks) |
|---|---:|---:|---:|---:|---:|
| 0.3 | 0.383731 | 28.853 | 4.000 | 0.3432 | 0.01787 / 0.00621 |
| 0.8 | 1.572700 | 49.080 | 2.364 | 0.6500 | 0.01752 / 0.00520 |
| 1.5 | 5.504948 | 165.000 | 1.420 | 0.6500 | 0.01702 / 0.00608 |

Hs above is the retained spectral `4 sqrt(sum(A²)/2)`; it does not promise identical Eulerian height statistics
after the nonlinear horizontal transformation. The one-block triangle approximation already existed for sine
waves; its error is measured separately from agreement with the analytic surface. No height scale was changed.

| Strength | Wind input (%) | Mean wind-foam opacity (%) | Area with foam > 0.1 (%) | Sheltered mean (%) |
|---|---:|---:|---:|---:|
| 0.3 | 0.11832 | 0.00001 | 0 | 0 |
| 0.8 | 10.49164 | 0.04439 | 0.13046 | 0 |
| 1.5 | 89.49069 | 0.08295 | 0.25253 | 0 |

Foam diagnostics cover at least four target wavelengths (128 / 196.32 / 660 blocks per side) at 512² pixels.
These are one-time, fixed-seed patch statistics, not a claim of Monahan coverage across the entire surface.
Only the steepest tops whiten; pre.10's wind-only rows no longer contribute. Fine bubble holes filter to their
mean when unresolved, and changing resolution changes the thresholded area.

Validation: warning-free Debug build; **183 offline checks and 126 GPU checks passed** on the AMD Radeon RX 6600.
Logs are `obj/pre11-build.txt`, `obj/pre11-offline.txt` and `obj/pre11-gpu.txt`.

- 18,432 actual GPU vertex samples across strengths 0.3/0.8/1.5, scales 1/2.2, factors 1/0.2/1:90,
  bearings 270°/315°, and camera origins 0 and (812345.5, -233007.25): forward position error below 0.000022 blocks;
  ship height at the GPU's displaced world X/Z differs by less than 0.000005 blocks. GPU normals differ from
  CPU world-height finite differences by less than 0.000004 in slope.
- A further 256 GPU vertices cross a shelter gradient: inverse-coordinate factor lookup differs by less than
  0.000002 blocks. Using displaced world coordinates for the factor would miss by 0.0249 blocks in this fixture.
- 8,640 CPU round trips include zero wind, scale 4 and a third bearing; error below 1e-8 blocks. A deliberately
  aligned 24-train custom spectrum at the full compression bound has inverse error below 0.000007 blocks.
  Single-train crest/trough curvature ratio is 15.11, with unchanged vertical extrema.
- GPU checks require zero sheltered foam at all three strengths, zero wind-only/uncompressed foam, finite patch
  lengths, fading behind crests, actual bubble holes and filtered subpixel cells. Wake age/fades and production
  dry clips remain checked; an added GPU test composes horizontal Gerstner displacement with a pitched/heeled dry hull.
- Existing shared-grid, clock, ring, continuity, provider-precedence, wind, dry-space and wake-budget checks remain.

## In-game look and coordinator handoff

Repeat the supplied shallow-coast sunset view and capture `.sea` alongside it. Check that wind foam is absent
between nonbreaking crests and that any remaining pale shore patterns belong to vanilla contact foam or reflections.
Compare `.sea foam wind|wakes|off|all`. Inspect crest shape at 0.3, 0.8 and 1.5, plus scale 2.2 storms; ship pitch,
heel and deck wetness can change because the same wave heights now have sharper local slopes. Inspect the
horizontal mesh at land edges, non-dry hull calming and dry holds, and the Wmap handoff. Frame time and multiplayer
appearance have not been measured in game. No added shoaling/refraction or airborne storm spray is claimed.

Changes are left uncommitted; `obj/COMMIT_MESSAGE.txt` is the coordinator's message. Outside-worktree suite inventory
and ROADMAP #37 updates belong to the coordinator under the task's write restriction.

**0.6.0-pre.1:** the shoaling left open here now exists over a deterministic coast map (SEA-D58): near beaches the
surf trains steepen into a breaker shape on top of these Gerstner crests, and breaking surf throws spray.
