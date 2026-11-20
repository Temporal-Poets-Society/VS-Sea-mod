# Sea 0.6.4 (20 November 2026)

0.6.3 with Wind Vanes' wind direction on the server too.

- **Ships follow Wind Vanes** (SEA-D84, backlog SE-0079). Wind Vanes & Randomized Winds 1.2.8 (which carries Pelagus
  Winds) turns the game's wind only on clients; its server keeps vanilla's east wind. With 0.6.3 the vanilla sailboat
  (moved by its rider's game) followed it, but Ships' ships, sailed on the server, didn't, while their wind display did.
  With mod `windvane` loaded, Sea's server now reads Wind Vanes' angle and turns the game's wind the same way. The
  author chose this over asking Wind Vanes' author ("Yes, add adapter").

## The file

`release/sea-0.6.4.zip`, 210,263 bytes, SHA-256 `d90b5affbbab1cb8dbfa23e8db9ca217d19b049dc9bf08ca3610e773a2c37a91`,
built in the worktree from `8a5ac69` (0 warnings), 9 files, `modinfo.json` version 0.6.4.

## Verification

- `dotnet run --project tests\SeaTests`: ALL OK.
- `tools/run_selftest.sh` with this zip and `EXTRA_ZIPS` = Wind Vanes 1.2.8 (`windvanes_1_2_8.zip`, SHA-256
  `b1a70b3a…6d09e2`, from the Mod DB): SELFTEST ALL OK; "Wind Vanes: the server's game wind (0.450, 0.000) turned to
  its angle 348° for ships: 0.45 from NNE (12°) (-0.093, 0.440)". The game's own server wind stays east, as read in
  its code. An earlier run of the same build gave 252°, so the angle is Wind Vanes' own, which drifts.
- The same without Wind Vanes: SELFTEST ALL OK.
- Not seen in game, not run with Ships on that server, Pelagus Winds alone not read.

## Published

Nothing yet: the tag `v0.6.4` is local.
