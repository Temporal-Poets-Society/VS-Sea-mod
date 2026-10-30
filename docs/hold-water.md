# Water in the caravel hold — issue #30, SEA-D47

30 October 2026, Sea 0.4.7-pre.8. **Hansa handoff; no Sea clip change and no Hansa files changed.**
Two different water paths matter. A small Hansa clip-origin error is reproduced at strong pitch/heel, with a
specific correction tested offline. Hansa can also intentionally fill an intact caravel in heavy seas and draws
that water with its own mesh. The screenshot does not include the vessel's stored water or shader status, so it
cannot by itself prove which path produced every visible patch. Do not mark the pictured case visually fixed.

## Reproduction and isolation

The report shows `hansa:caravel`, entity 1938, a translucent sheet over the hold floor and between ribs at force 12,
SeaWindStrength 1.5, near 8.7°N 84.6°E. Expected: the outside ocean must be absent inside the dry hull, while genuine
simulated floodwater should remain visible. No client, live world, saved ship or regular game-data directory was used.

Read-only source trace:

- Hansa `src/Client/ShipRenderer.cs:2860` supplies clips from the same `RenderOrigin` as the drawn vessel and passes
  the drawn pitch/roll including heel. It skips sinking ships intentionally. `ShipWater.Clip` registers a dry
  volume, not just a waterline footprint, with a detailed 4 × 32 outline fitted by `WaterClipDef.Build`.
- Sea `WaterShader.Inject` assigns `seaFragPos` **after** wave displacement and local hull calming. `seaDry` runs
  first in each water fragment pass and tests the drawn point against the inverse rigid hull frame. Both depth
  and visible water use this path. High waves do not bypass the test or use the undisplaced waterline position.
- Caravel clip: waterline offset 0.1, fitted half-length 13.5625, centre −2.8125, inset 1, dry depth 3.25, dry height
  2.125; four rows begin −0.85 relative to the waterline, step 1, with end rows held outside that range. Keel is
  −2.75, hold floor −1.875, deck 1.125 in ship coordinates. The height band already includes the hold floor.
- Hansa `src/Ship/ShipDrySpaces.cs:23` owns gameplay dry-space queries using its voxel mask. Sea's shader uses the
  registered outline, not that mask; checking both matters.

## Reproduced registration error: Hansa's clip pivot

`ShipWater.Clip` currently obtains `y0 = height / (cos(pitch) cos(roll))` from `ShipFrame.WaterlinePlane` and sends
`X = shipOrigin.X`, `Z = shipOrigin.Z`, `WaterY = shipOrigin.Y + y0`. This is the **vertical intersection** with the
tilted local waterline plane. Sea reconstructs a rigid frame about the supplied point; its detailed widths are
local coordinates relative to a waterline point. The vertical intersection is not the rotated local point
`ShipFrame.ToWorld(yaw, pitch, roll, 0, height, 0)` when height is nonzero and the hull tilts.

At −12° pitch/−35° roll, this shifts the interpreted across-hull coordinate by about 0.070 blocks and along-hull
coordinate by about 0.026. The outline's small fitting allowance does not always cover it. The caravel's generated
bottom mesh has **232 vertices outside the registered cut** in the inspected below-deck region; maximum width
shortfall is **0.0593 blocks**. This is a registration error, not evidence that the fitted outline needs widening.
Sea cannot infer the missing ship-origin/waterline offset from the generic clip alone.

The needed Hansa change is confined to `src/Ship/ShipWater.cs`:

```csharp
ShipFrame.WaterlinePlane(pitch, roll, wc.Height, out _, out double along, out double across);
ShipFrame.ToWorld(yaw, pitch, roll, 0, wc.Height, 0, out double hx, out double hy, out double hz);
// Preserve existing slopes, profiles, height band and absolute flood plane.
X = ox + hx; Z = oz + hz; WaterY = oy + hy;
```

This passes the exact transformed waterline pivot to Sea. The probe simulates those three changed fields without
editing Hansa. **All 232 misses disappear**, with no enlarged outline or blanket water suppression. Level placement
is unchanged. A ready-to-review patch is left in ignored `obj/hansa-clip-origin.patch`; it has not been applied.

| Mesh region (−12 ≤ local X ≤ 7, −2.7 ≤ local Y ≤ −0.3) | Points per pose | Current, level | Current, +12°/+35° | Current, −12°/−35° | Proposed pivot, all three poses |
|---|---:|---:|---:|---:|---:|
| `interior` vertices | 53,208 | 0 misses | 0 | 0 | 0 |
| `bottom` vertices | 9,344 | 0 misses | 0 | 232 | 0 |

A separate dry-mask interior sweep, inset at least 0.06 from the authored outline, tests 102,480 points in each of
12 poses: pitch −12/0/+12°, roll −20/0/+20/+35°, yaw 0.37. It finds **0 misses in 1,229,760 points**, including the
hold bottom. Thus the reproduced registration defect is a thin boundary gap, not a demonstrated broad sheet through
the middle of a dry hold. The sampled poses/region are explicit limits, not a proof for every ship and angle.

## Independent Hansa floodwater path

Hansa `src/Ship/ShipFlooding.cs:95` defines `Flooded => Water > 0.5` cubic blocks. Its `DrawFlood` in
`src/Client/ShipRenderer.cs:794` then draws a separate translucent saltwater-textured mesh, tinted
`(0.36, 0.5, 0.52, 0.85)`, in the hold's light. It is approximately level in world space, with slosh and small
vanilla bobbing. **It does not pass through Sea's chunkliquid shader**, so changing Sea's dry cut cannot remove it.
Hansa deliberately sends a flood plane to Sea, leaving the outside water uncut below that plane too.

`ShipFlood.Downflooding` (`ShipDamage.cs:1625`) lets a share of water overtopping deck edges/scuppers reach the hold
even with all hatches closed (`DeckShare = 0.05` by default). `ShipFlooding.cs:689` integrates that into `Water`.
Intact hulls do not imply permanently dry holds under this model.

The read-only probe calls Hansa's actual `ShipMotion.Waves` and `ShipFlood.Downflooding` for an intact caravel,
all openings closed, no pumps, 120 seconds at 0.25-second steps; key 4242, starting world X/Z 382876/65229, heading
358°, speed 13.3 blocks/s (as shown in the report), full sail steadiness, open sea. It uses Sea's frozen pre.7
equations as well as corrected pre.8, with a chosen steady east wind. The course is a diagnostic input, not a
reconstruction of the screenshot's camera heading or the vessel's unknown true course. This is an isolated intake
diagnostic: no damage, sinkage/list feedback,
weather changes or full entity tick, and the original world key/ship water are unknown.

| Spectrum / strength | Water admitted in 120 s | Peak intake, m³/s | First crosses Hansa's drawing threshold |
|---|---:|---:|---:|
| pre.7 / 0.3 | 0 | 0 | never |
| pre.7 / 1.5 | 3.4275 m³ | 0.9260 | 22.25 s |
| pre.8 / 0.3 | 0 | 0 | never |
| pre.8 / 1.5 | 3.7699 m³ | 1.0365 | 22.00 s |

The actual caravel hydrostatics put only 0.6 m³ at local centre height −2.357. At 20° heel, `ShipWater.Surface`
reaches local Y −1.341 on the low side, above the hold floor at −1.875: a visible water strip is possible with
very little stored water. This reproduces a plausible cause of the screenshot's sheet, but is not a measurement
of that live ship. Raising Sea's clip height or widening its outline would not address this independent mesh.

## Verification and reproducibility

`tests/ShipClipCheck` reads Hansa without modifying/building it or starting any world. It invokes its existing
Debug assembly's pure model/frame/flood routines and compares current registration with the proposed pivot. Set
`HANSA_ROOT` to the Hansa checkout and `VINTAGE_STORY` to a readable dependency root, then from Sea run:

```powershell
dotnet run --project tests/ShipClipCheck -p:VintageStoryPath="$env:VINTAGE_STORY"
```

`HansaAssembly` can override the existing assembly path; all probe outputs are in Sea's ignored build directories.
This invocation checks the existing Hansa binary plus current authored data, not the exact installed package.
The relevant source formulas were cross-checked explicitly. Provenance for the recorded run:

| Input | SHA-256 |
|---|---|
| Hansa Debug DLL | `5a4ac9a66c517b999fd0397cd0cfbb9b0cd85836b579c6f0713a60781f973fb0` |
| `config/ships/caravel.json` | `8025b332a13e78ba723a150999d9415800c93d749c7fda282323c24598b129e5` |
| `config/shipmeshes/caravel.json` | `09bf8af77233d748182cf66ceb1c613dc5120da713c2871862fd2ef1fd4e2847` |
| `ShipWater.cs` | `968c1c5c6af6e43d18abaa891d876c614785537e1e0cc1c3051d28db80a5bb3a` |

The fitted Sea clip is frozen in `tests/fixtures/caravel-dry-clip.json`; no Hansa/game binary or complete asset is
committed. ShaderCheck adds three GPU cases using this actual profile: level, 12° pitch/35° heel, and flooded.
Each compares 131,072 pixels across eight world-height planes with Sea's CPU twin: **zero mismatches**, including
1,674 intentionally visible points below the flood plane in the flooded case. These checks cover Sea's clip
equation and upload layout; they do not establish a live client's shader activation or reproduce its frame.

## Hansa/coordinator actions and remaining in-game check

1. Apply/review the proposed Hansa pivot patch in Hansa's own worktree, build there and run its frame/dry-hull tests.
   Re-check both tilt signs and ensure no outside water gap appears around bow/stern or above the rail.
2. At the reported caravel, inspect the pump's `WaterText`/stored `Water`, `WaterRate`, `FloodLevel`, `Flooded` and
   whether `DrawFlood` is active. A freshly repaired/pumped-dry disposable test distinguishes the two render paths.
   If unwanted intake persists with an intact closed hull, review Hansa's `DeckShare`/downflood-entry policy; do not
   hide its floodwater by changing Sea. The snapshot does not justify disabling intentional downflooding here.
3. If water persists while `Water <= 0.5`, capture `.sea` shader/dry-cut status and the supplied clip for that frame;
   investigate a missing registration/fallback or different installed caravel geometry. That runtime evidence is
   still needed before claiming every part of #30 resolved.

Version stays pre.8. SEA-D47 records this handoff separately from storm spectrum SEA-D46. Suite ROADMAP #30 should
say: Hansa origin-registration correction prepared/tested, separate simulated floodwater identified, live case
pending; no Sea blanket-clip fix and no Hansa patch applied in this restricted working copy.
