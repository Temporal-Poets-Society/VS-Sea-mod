# Visual wakes — issue #29, SEA-D48

**Pre.10 update (31 October, SEA-D49):** [issue #35](foam-direction.md) gives wake foam its own wind-independent
porous film, opening into lace with node age. The atlas's spare green channel now stores premultiplied freshness;
texture allocation, shape, lifetime, dry cuts and the accepted 250,000-sample raster cap stay unchanged.
`.sea foam all|wind|wakes|off` isolates surface foam layers without clearing histories.

Sea 0.4.7-pre.9 adds client-only moving-source foam and spray on accepted pre.8 (`d11e6ba`). Pre.8 had no source
histories: ships, swimmers and thrown objects could produce vanilla contact particles but no persistent Sea-aware
wake. Sea now observes nearby water contacts, keeps bounded visual histories and samples their foam on the
**existing water triangles**. Their Sea wave displacement, hull calming, dry-space discard, lighting, OIT alpha and
fog are shared. Wakes never enter `WaveSpectrum`, `WavePlace`, the wave clock, ship forces or server simulation.
Viewers may observe different cosmetic histories without changing their shared ship samples.

Feature reference/credit: Bobrik00's [Immersive Wakes 0.9.1](https://github.com/Bobrik00/ImmersiveWakes), a port of
Goby56's Minecraft Wakes. Sea is GPL-3.0. This is an original implementation from physics and the requested feature
list; no upstream source or assets were fetched/copied. The wake source headers carry the same credit.

## Physics and visual approximations

Kelvin's deep-water envelope has half-angle `asin(1/3) = 19.47122063°`. The largest visible waves narrow at high
`Fr = U/sqrt(gL)`, while the weak outer envelope remains Kelvin's. Sea draws the bright arms, using
`atan(tan(KelvinAngle) * min(1, 0.5/Fr))`. The transition and inverse-Froude high-speed scaling follow
[Rabaud & Moisy, 2013](https://arxiv.org/abs/1304.2653); the distinction between the brightest angle and the outer
envelope follows [Darmon, Benzaquen & Raphaël, 2014](https://arxiv.org/abs/1309.6751). This interpolation is Sea's
rendering choice, not a fitted hydrodynamics model. Hull aspect ratio also changes the real transition and
intermediate scaling ([Moisy & Rabaud, 2014](https://arxiv.org/abs/1404.2049)); beam/draft here size the foam and
wash, without claiming that complete solution or shallow-water/planing accuracy.

For the low-Fr cusp packets, forward group speed is U/3 and relative aft speed 2U/3. Advancing both forward and
lateral packet positions gives the Kelvin arm relative to the moving bow; a sideways-only expanding trail would
give the wrong cone. Transverse crest hints use the deep-water stationary wavelength `2πU²/g`, bounded to 2..4L
blocks for a readable finite wake. These visual crests add no surface height or physical force.

Each moving contact contributes a hull-sized bow arc, stern wash and two propagating bright arms. A ragged stern
trail slowly spreads and meanders; anchored noise in the shared foam shader breaks its texture up. Its quadratic
fade lasts `clamp(25 + 4L, 25, 180)` seconds: about 28 seconds for a swimmer, 41 for a four-block boat, 129 for a
26-block caravel, up to three minutes for larger hulls. These lifetimes are visual tuning, not a foam chemistry
claim. Pressure-arm foam lasts 12 seconds. Entry rings expand with a deep-water group-speed scale and fade over
eight seconds. The liquid-collision hook and observed dry-to-wet transition deduplicate the same impact.

Bow spray uses speed and local sheltered significant wave height. At rest there is none. It emits small white
quads near the bow, samples the camera's actual Sea trains at birth, dies in liquid and vetoes dry-space
births/predicted endpoints. No server entity is spawned.

## Sources and size API

`WakeRenderer` queries the game's nearby-entity spatial partition at 10 Hz, retaining at most 64 contacts, vessels
first then nearest. Speed comes from observed client positions in blocks/second. First observations, stationary
contacts (below 0.25 blocks/s) and teleport-sized steps do not leave joining stripes. Mounted agents are excluded,
so passengers do not emit swimmer wakes. Agents, animals, items and projectiles use the same wet/contact path.
Entry impacts additionally use the client liquid-collision callback, recovering speed from motion/fall distance
and the preceding vertical observation. Fast impacts missed between polls can therefore still produce rings.
Deeply submerged sources and flying entities do not draw surface trails.

Vessels are detected by a declared hull, entity class names containing Boat/Ship, or raft/sailboat code paths.
The game's `Vintagestory.GameContent.EntityBoat`, Hansa's `hansa.EntityShip` and Magelan's EntityShip class are
covered without loader dependencies or renamed-ID assumptions. Dimensions are chosen in order:

1. A ship mod's explicit client declaration.
2. Its existing Sea water clip whose origin is within two blocks of the entity. Length/beam exclude `DryInset`;
   draft/centre offset come from the clip. This avoids using Hansa's square collision box as its caravel outline.
3. The entity collision box. Vessels get length at least 1.5× beam; non-vessels retain their own size.

Call on the **client**, after Sea's client startup and entity creation:

```csharp
bool accepted = sea.SetWakeHull(entity.EntityId, length, beam, draft, centreOffset);
// Remove on despawn; reissue if the waterline hull changes.
sea.RemoveWakeHull(entity.EntityId);
```

Arguments are BCL types, suitable for optional reflection without a Sea reference. Dimensions are blocks:
length 0.1..200, beam 0.1..50, draft 0.05..20, finite centre offset with magnitude <= length. Offset runs along
`(sin(entity.Pos.Yaw), cos(entity.Pos.Yaw))`. The API returns false for invalid values, a server call or a full
512-entry registry. It never edits collision boxes, saves or network state. Wakes follow travel direction,
including reverse travel; the declared centre offset follows hull yaw. Unusual pivots should use the API rather
than rely on approximate collision-box/nearby-clip fallbacks.

Contact checks use loaded non-lava liquid columns near the entity, not a global sea level. Eighth-block rest
heights match the engine's liquid-level table; a small shader tolerance allows interpolated corners/flowing water.
A premultiplied rest-height channel rejects foam on another stacked water surface, independently of wave height.
Agents in registered dry spaces are skipped. Final wake fragments use the existing Sea dry-hull cut, including
pitch, heel and flooding rules; its existing clip budget still applies. With dry providers but no working GPU
dry-cut path, wakes disable. Pre.8's #30 Hansa pivot/flood investigation remains separate; wakes do not hide
legitimate floodwater.

## Cost, configuration and lifecycle

| Resource | Bound / behavior |
|---|---|
| Updates | At most 10 Hz on the render thread; paused wave time makes no new observations |
| Contacts | 64 candidates per spatial query, vessels first/nearest; 128 observation records |
| Histories | 64 tracks, 1,024 nodes total, two nodes/source/second; oldest nodes/tracks evicted under load |
| Drawn trails | Nearest 32 tracks; old trails persist after source unload/despawn until TTL/eviction |
| Splash contacts / rings | 64 queued contacts, 64 rings; same-source entries within 0.7 seconds deduplicated |
| Atlas | 512² RGBA16F over 256² blocks, 0.5-block texels; 2 MiB GPU, 4 MiB upload buffer |
| CPU raster | At most 250,000 sample visits/update (coordinator review); far segments clipped before subdivision |
| Upload | At most 4 MiB/update (40 MiB/s at 10 Hz), one texture, no extra offscreen render pass; idle uploads skipped |
| Spray | Four births/update maximum (40/s), lifetime 0.5 seconds, sources within 64 blocks |
| Fade | Starts min(96, 0.50 view distance), ends min(120, 0.68 view distance); Wmap starts at 0.75 |

The engine's spatial-query traversal still depends on loaded entity density; these caps bound Sea's own retained,
contact and raster work. Under load distant/old trails may expire early; rings/arms may be omitted once the shared
raster budget is spent. The finite near field also hides old trails once they are far behind the camera.

`WakeEnabled: true`, `WakeStrength: 1` are server-provided **presentation defaults**, config members 16/17.
Strength is 0..2; disabled/zero values serialize explicitly. Old configs default on. `.sea wakes off`,
`.sea wakes 0..2`, `.sea wakes default` tune this client without writing config; `.sea reset` also clears that
override. `.sea` exposes counts/failure state. Glints, sky reflection, vanilla waving-stuff and vanilla foam
graphics switches do not control wakes. The optional celestial shader compile fallback removes wind foam/wakes.

Texture unit 12 is rebound on every water-program use, even if that frame's uniforms are already uploaded. Atlas
upload/bind restores the previous active unit. An atlas exception disables only wakes, preserving the shared
wave/hull uploader. World exit disposes the texture/histories; shader shutdown zeros wake strength in retained
game programs. Histories are never saved or synchronized.

## Offline evidence

With a configured game reference root in `VINTAGE_STORY`, run:

```powershell
dotnet build Sea.csproj
dotnet run --project tests/SeaTests
dotnet run --project tests/ShaderCheck
```

Build: zero warnings/errors; **173 offline checks and 102 GPU checks pass**. Offline checks cover dimensions, Kelvin geometry/high-Fr narrowing, speed/sea-state
response, lifetimes, source/node/ring/raster budgets, teleports, backwards clock, zero config serialization,
rest-height encoding, large-coordinate precision and bit-identical ship wave samples after wake work.
GPU checks on AMD Radeon RX 6600, OpenGL 3.3 compile actual water/depth shaders at low/high settings and fallback
variants; the wake uniforms remain live. Production atlas-sampler/`seaDry` diagnostics verify off = zero,
stacked-water rejection, vertical camera anchoring, Wmap fade, persistence after stopping, expiry and zero
dry-hold foam leaks with five-block displaced water and pitch/heel.

Measured debug-build raster evidence: the full 1,024-node/32-drawn-source overload case visited 93,932 samples
in 7.1 ms; a deliberately dense fleet of oversized 180×45-block hulls stopped exactly at the then 1,500,000-sample cap in
45.2 ms. These CPU-only timings are machine-specific, exclude entity queries/upload/game rendering, and are not
a frame-time promise. The latter demonstrates a hard work bound, with visible quality sacrificed at exhaustion.
Coordinator review (30 October): the cap was lowered to 250,000 samples so a worst-case repaint (every 0.1 s on
the render thread) stays near the normal 7 ms overload case; frame time with wakes remains an in-game check.

The GPU check writes ignored `obj/wakes-before.ppm`, `wakes-after.ppm`, `wakes-dry-cut.ppm` and
`wakes-after-60s.ppm`: top-down GPU mask diagnostics over schematic blue water, **not game renders**. The scene
has a 26×7-block caravel at 4 blocks/s, a turning four-block boat, swimmer and impact ring. Before has no wake;
after has the hull-sized bow/V/trail. After 60 seconds, pressure arms are gone but caravel foam persists/fades.
No client was launched. These checks establish geometry/composition, not live contact detection, appearance
acceptance or in-game frame time.

## In-game checklist

- Sail a vanilla raft/boat and Hansa/Magelan ship straight, reverse, turn and stop. Compare `.sea wakes off`/`1`;
  check hull size and bow/stern placement at wind strengths 0.3, 0.8, 1.2 and 1.5.
- Swim, jump in, let an animal swim, throw items and shoot arrows into water. Rings should occur on entry rather
  than repeatedly while floating. Mounted agents must not add wakes; deep animals must not foam a distant surface.
  Check sheltered/covered water, flowing water and stacked ponds.
- Revisit a trail after 30/60/120 seconds; move/turn the camera and cross a world seam. No camera-attached pattern
  or teleport stripe. Look for thin-line aliasing, especially swimmer wakes/rings on half-block texels.
- Check a tilted dry hold for wake foam/spray; compare legitimate flooding separately. Watch bow spray against
  the hull at high heel and large storm heave.
- With Wmap at low/high view distance, pan across near/far water. Wakes must disappear before far sea; pre.8's
  wind-whitecap/far-sea limitations remain separate.
- Stress many ships/animals and watch `.sea` counts/frame time. Toggle off/on, pause/resume, reload shaders,
  leave/join worlds (including one without Sea): no stale texture, foam, GL errors or changed ship motion.

The coordinator must update suite overview/ROADMAP/inventory: this task permits writes only in the Sea working
copy. Mark #29 implemented/offline checks passing, **play-test pending**, not accepted in game.
