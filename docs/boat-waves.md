# Native boats with Sea — 0.4.7-pre.15

The author requests standalone raft/sailboat wave following, Ships rowing-boat corrections, wind-driven native sails,
visible sail trim, and storm flooding. Earth keeps Sea strongly recommended and optional (Earth pre.16); no Earth
code or package is changed by this boat follow-up.

## Motion and sailing

Sea fits nine samples of the displaced wave surface to each hull. The native hull, seats/camera and individual
interaction boxes share heave and pitch/roll (capped at 30°). Dry/grounded boats and other dimensions are excluded.
The native raft and Ships rowboat respond immediately; the heavier native sailboat samples 0.65 seconds earlier.
That deterministic response delay lets a rising crest overtake the gunwale instead of making the hull perfectly
match every crest. Both sides use Sea's shared wave clock, without a second integration clock or network stream.
Native turning/forward lean is retained; native unrelated sine rocking is replaced without accumulating angles.

Sails keep the furled/half/full controls. Sea automatically braces the square yard and all three cloth/bundle
variants about the same mast pivot, smoothly up to 65° either way. This uses client-only JSON patches to the native
shape and animation metadata, without copying/replacing the model or moving its mast and stays. Downwind the yard
is athwart; reaching it braces toward the windward side. No manual trimming controls are added.

The gameplay polar has a 40° upwind no-go cone and useful close-hauled drive at 45° on either tack. Beam wind 0.3
has native sail thrust; strength scales drive, capped at twice native thrust. Best drive is 90–120° off the wind,
75% downwind. Furled-sail rowing and native rudder controls remain. There is no leeway or capsizing model.
Wind comes from `SeaSystem.WindAt`: admin override, provider (normally Earth), otherwise native strength blowing east.
Without Sea, native propulsion remains unchanged. Sea never references a Ships assembly.

## Water aboard

Only `game:boat-sailed-*` gains flooding. Every 0.2 seconds the server compares the current waves with six points
around the transformed gunwale. The deepest overtopping point controls intake (fill rate 0.04 × depth^1.5 per second,
depth capped at 4 blocks). Sheltered water uses Sea's actual shelter factor. No overtopping means no new water.

`seaBoatFlood` is a clamped float in the boat's native watched attributes, saved and synchronized by the game.
Fill lowers the native flotation offset by up to 0.8 blocks and raises density from 100 to 1150. At full flooding
native passive physics sinks the boat; wave lift fades over the final 10% of fill. The boat/cargo are not deleted.
Stored water is a level textured surface inside the hull, using the same volume as the dry cut. Its level follows
fill; water below that plane remains wet for the view and breathing. Flooding is a gameplay model of reserve
buoyancy, not a per-compartment volume/structural-damage model.

Sneak + right-click the hull with an empty open liquid container (the native wooden bucket works) to bail 4% of fill,
with a server-enforced one-second cooldown. The bucket stays empty: this represents throwing the water overboard.
The target info shows water percentage and the bailing instruction. Native pickup is blocked until bailed dry,
because native pickup creates a fresh item and would otherwise erase the stored water. Water does not drain by
itself. Removing Sea ignores its attribute and restores native behavior; re-adding Sea resumes saved flooding.

The mounted camera's depth and tint use the displaced surface instead of flat liquid blocks. A trough therefore
does not create a false underwater view. A real covering crest or stored floodwater still does. The server breathing check uses the same surface,
so a dry rider in a trough does not lose air and a covering wave is still wet; native creative/spectator breathing
exemptions remain. Pre.15 adds this server consistency correction; pre.14 remains a preserved candidate snapshot. Other cameras,
liquids and dimensions retain native checks. A native sailboat uses Sea's fitted, height-dependent dry volume;
the duplicate native flat-depth mask is suppressed while Sea's dry shader is available, retained as a driver fallback.

## Contracts

`BoatWaves.TryGetPose` gives the vertical translation and native local angles. `BoatWaves.ApplyToClip` rotates a
mod's source outline about the native pivot, including offsets, renderer turn/forward lean and the resulting keel
direction. Ships dev.23 requires Sea pre.14 for this corrected contract. Only vanilla rafts/sailboats and
`ships:rowboat-*` receive this adapter; custom Ships vessels retain their own mechanics.

The rowboat cut is baked from the existing oriented wooden hull, with its actual off-centre beam and four height
sections. Its cut stops above the floor and below the gunwale. Ships removes the duplicate flat native depth mask.
The hull/oars, boarding, collision and custom-ship geometry remain unchanged.

## Verification and limits

The offline suite checks planar/crest/trough fits, boundary angles, symmetric close-hauled drive, wind strength,
trim on both tacks and calm/flood buoyancy. Hidden-GPU checks compare both fitted boat cuts with the production
CPU test at level, pitched/rolled and flooded poses. The geometry audit samples 192,421 rowing-hull sections;
no cut extends outside the exterior or leaves free interior uncovered at that resolution.

`tests/BoatProbe` runs disposable native worlds with Sea alone and Sea + Ships. It exercises actual native physics,
frame/matrix/seat/selection methods, both rowing controls, wind propulsion, shape animation-frame generation,
the real camera Harmony callbacks with crest/trough inputs, actual entity-save serialization, bucket cooldown,
legitimate floodwater, and native sinking. Rowing-boat checks include 328 rendered boundary points with combined
pitch, roll and native lean. Storm intake uses a controlled open-ocean shelter factor and strength 1.5 over four
simulated minutes; it is not a claim that every sheltered lake floods at that wind.

See `overview/verification/boat-storm-fixes.json` for exact packages and checks. Earlier pre.13 centre-only tests
were insufficient to accept the hull boundary; the author's screenshot is the regression evidence. Neither server
nor hidden-GPU checks prove the full graphical-client appearance, multiplayer seat timing or final sailing balance.
These remain in-game acceptance checks. Previous packages and player saves are preserved.
