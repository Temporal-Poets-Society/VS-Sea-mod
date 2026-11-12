# Sea open issues (moved to the suite backlog)

Since 12 November 2026 the issues that were listed here are tracked as one file each in the suite backlog,
`C:\VS mods\backlog\items\`, with their current status and the evidence for it. Each was rechecked against
`main` that day. The index of everything open is [backlog/BACKLOG.md](../../backlog/BACKLOG.md); for this mod's
open items run `tools\Update-Backlog.ps1 -Mod sea` from `C:\VS mods`.

| Item | Issue |
| --- | --- |
| [SE-0005](../../backlog/items/SE-0005-storm-sea-small-glassy-waves.md) | Hurricane sea shows small glassy waves, no whitecaps |
| [SE-0006](../../backlog/items/SE-0006-ships-should-leave-wakes.md) | Ships should leave wakes |
| [SE-0007](../../backlog/items/SE-0007-wind-foam-smooth-ribbons.md) | Wind foam drawn as smooth white ribbons along the crests |
| [SE-0008](../../backlog/items/SE-0008-white-stripes-and-poor-crests.md) | White stripes across the waves and poor crest shape |
| [SE-0012](../../backlog/items/SE-0012-sea-0-4-2-client-paths-untested.md) | Sea 0.4.2 client paths never play-tested |
| [SE-0013](../../backlog/items/SE-0013-client-wave-height-differs-from-server.md) | Client wave height is the drawn surface, server samples the place |
| [SE-0014](../../backlog/items/SE-0014-sample-mixes-state-across-thread-change.md) | A wave sample can mix old and new state across a change on another thread |
| [SE-0015](../../backlog/items/SE-0015-hour-only-questions-answer-as-known-now.md) | Hour-only wind questions answer "as known now" |
| [SE-0016](../../backlog/items/SE-0016-partial-shader-injection-tracked-globally.md) | Partial water-shader injection is tracked for both passes at once |
| [SE-0017](../../backlog/items/SE-0017-windat-reads-vanilla-wind-off-thread.md) | WindAt reads vanilla's wind through a main-thread accessor |
| [SE-0018](../../backlog/items/SE-0018-listeners-not-unregistered-on-dispose.md) | Listeners and events not unregistered on dispose |
| [SE-0019](../../backlog/items/SE-0019-client-model-ignores-calendar-change.md) | Client keeps its wave model when only days per year or hours per day change |
| [SE-0020](../../backlog/items/SE-0020-fixed-wind-history-drops-oldest.md) | More than 64 fixed-wind changes drop still-needed ones |
| [SE-0021](../../backlog/items/SE-0021-waveplace-sea-wind-from-nearest-corner.md) | WavePlace.Sea and .Wind come from the nearest grid corner |
| [SE-0022](../../backlog/items/SE-0022-reflections-ignore-calming-near-hull.md) | Reflections ignore the calming around a hull that isn't dry |
| [SE-0023](../../backlog/items/SE-0023-celestial-use-postfix-cost.md) | CelestialRenderer's Use postfix costs tens of µs a frame |
| [SE-0024](../../backlog/items/SE-0024-vertex-stage-runs-trains-twice.md) | Vertex stage runs the 24 wave trains twice |
| [SE-0025](../../backlog/items/SE-0025-wave-places-resample-on-new-anchor.md) | Every wave place re-blends both ends when a new anchor arrives |
| [SE-0026](../../backlog/items/SE-0026-waveplace-sample-blends-every-call.md) | WavePlace.Sample blends 4 × 414 amplitudes on every call |
| [SE-0027](../../backlog/items/SE-0027-stern-dry-outline-failure.md) | Stern dry-outline failure from the ship test history |
| [SE-0028](../../backlog/items/SE-0028-coast-aware-waves.md) | Waves should be smaller and break in sheltered water near land |
| [SE-0029](../../backlog/items/SE-0029-terra-prety-watersheds-runtime-check.md) | Run-time check with Terra Prety and Watersheds |
| [SH-0001](../../backlog/items/SH-0001-water-in-caravel-hold-in-high-waves.md) | Water inside the caravel's hold in high waves |

Not carried over: O9 and O10 (by design, nothing to do) and the notes about the review tooling.

The text this page held before is in this repository's history: `git show 1d6ad8f:docs/open-issues.md`.
Add new issues to the backlog, not here.
