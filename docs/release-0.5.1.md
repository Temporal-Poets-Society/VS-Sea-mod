# Sea 0.5.1 — development release for Earth

**Installation follow-up, 31 October, 23:03 Europe/Riga:** the author requested installation and confirmed the game/server
closed. Sea 0.5.1 is now installed with a verified pre.15/settings backup. Ships dev.23 and settings are unchanged;
no player save or game launch. See `overview/verification/sea-0.5.1-install.json`. Publication remains pending.

Author direction, 31 October 2026: the wake implementation looks bad and is removed from the release. Also explain
Sea's primary purpose for Earth and the plan to merge with Rough Seas following discussion with hoblin.

## Current behavior

Wakes, splash rings and Sea bow spray are not compiled into the release DLL. Their renderer, contact patch, atlas,
GL bindings, shader samplers/branches and live command paths are removed. Wake fields are absent from SeaConfig;
protobuf numbers 16/17 stay retired. The Earth tab declares only storm height; old wake keys cannot enable anything.
The old SetWakeHull/RemoveWakeHull methods are inert compatibility stubs for already-compiled companions.
Historical source/test files remain recoverable but are excluded from the current projects.

Breaking-wave crest foam, ordinary native contact effects, wave sampling, reflections and native boat sailing,
flooding/bailing/buoyancy remain. Sea's manifest credits Andris and links its listing. 0.5.0 remains a preserved
snapshot; use **sea-0.5.1.zip** for publication preparation.

## Publication

[HTML description](moddb-description.html) and [publishing information](moddb-publishing-info.txt) now describe Earth
as the primary purpose, with Earth remaining optional. The future Rough Seas merge is an author-reported plan;
it is not included here or presented as current co-install compatibility. Earth keeps ownership of weather.
The root Sea docs hold matching publication drafts so the older main checkout cannot cause the wrong text to be used.

## Verification

Release build: no errors or warnings. Offline: 241 checks pass, including old wake-on world/packet values and the
remaining storm settings. Hidden GPU: production water and foam programs pass, including 16 assertions that removed
wake uniforms are absent. Native disposable worlds: 77 Sea-only and 99 Sea+Ships checks pass. The actual shipped DLL
has no wake renderer/field/map types, no wake config fields or contact patch, and refuses wake declarations.
Native boat motion, trim frame generation, breathing, flood save/bail/sinking and rowing-boat dry boundaries pass.

Exact package, hashes, inputs and logs: `overview/verification/sea-release-0.5.1.json` (D47/SEA-D55).
This action does not install, commit, tag or upload the package. Full client appearance/multiplayer acceptance
remains pending. Older packages, the player's configuration and saves are preserved.
