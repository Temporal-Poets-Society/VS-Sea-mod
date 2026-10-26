# Night light: twilight, moon, weather and spawning (issue #7, 25 October 2026)

**Status: proposal written first (this section), then implemented on branch `night-light-2026-10-25`
(Sea `0.4.7-night.1`) and AstraTerra branch `fix/local-moon-light`. Not seen in game.**

The user's play-test note: *"Overall light level at night — is it too bright for mobs to spawn? With and without moon.
Looks like without moon it is too light before rain or clouds. We need to recalibrate light levels on ground, sky and
far distance renders. Also consider latitudes for light levels. I have been told that in the south nights are more
pitch black than in the north."*

## What makes a night bright today (installed stack, measured)

Measured on a dedicated server with the installed Earth 0.16.14-playtest.1, Cylinder World 0.11.15-playtest.2,
AstraTerra 0.15.0-dev.18-earthtest, Sea 0.4.7-pre.4, Wmap 0.5.0-pre.5 and Hydrology 0.1.9 (probe:
`tests/NightLightProbe`, CSV per latitude, longitude, season and moon phase), and from the game's decompiled 1.22 code.

| Contributor | Side | What it does at night |
|---|---|---|
| Vanilla `GameCalendar.GetDayLightStrength` | server and client | max(moon term, sun term). The **sun term reads `sunlight.png` at (1.4 sin(altitude) + 1.2) / 2**: it reaches its night value (0.05–0.06) only once the sun is about **46° below** the horizon; at −18° (the real end of twilight) it is still 0.51, at −6° 0.62. The moon term is 0.33 × (0.66 + 0.33 y) at full moon (client 0.66 y). No weather term. |
| Vanilla `AmbientManager` | client | Ambient ≈ daylight × scene brightness (night scene 0.35; overcast patterns 0.9, storms 0.65). Deep moonless night ≈ 0.02; high full moon ≈ 0.115. |
| Vanilla clouds (`clouds.vsh`) | client | Colour × daylight; fogged out only when daylight < 0.25. With vanilla's long twilight they stay lit (the beige clouds of screenshot 6). |
| Sea night floor (SEA-D27, D36) | client | Ambient raised to at least 0.15 (moonless) to 0.45 (high full moon) × clear sky, full from sun −10°. Strength 1 = the level the user picked on 21 October. Clouds × (1 − 0.92 c), rain × (1 − 0.9 r); fog not counted. |
| Sea `MoonAmbientScale` | client | Scales vanilla ambient down where vanilla's moon is up but AstraTerra draws its own local moon. Ambient only. |
| AstraTerra longitude-aware sun (Earth decision 0010) | server and client | Moves the sun by longitude; **vanilla's moon light term still uses the moon at universal time**. |
| AstraTerra cloud light (`CloudLight`) | client | Forward scatter around the drawn moon and sun on clouds. |
| AstraTerra near-body light | both | Only for a parent giant; inert on Earth worlds. |
| AstraTerra `sky.png` override | client | Holds the dusk sky 6° longer; the sky dome and fog colour, not the light level. |
| Wmap far terrain | client | `albedo × max(game ambient, Sea's floor)`; haze ≤ the game's sky daylight (1.25 × (daylight − moon / 2)). Follows whatever the game's daylight and ambient are. |
| Earth, Cylinder World | both | Supply the latitude (`OnGetLatitude`) that vanilla's sun uses. No light of their own. |

**Measured server daylight at local midnight** (`GetDayLightStrength`; the time-of-day light a spawn check sees
outdoors is int(24 × daylight)), longitude 0°:

| | 10°N | 45°N | 60°N | 63.3°N | 40°S |
|---|---|---|---|---|---|
| March, new moon | 0.058 (1) | 0.061 (1) | 0.227 (5) | 0.295 (7) | 0.051 (1) |
| June, new moon | 0.054 (1) | **0.378 (9)** | **0.609 (14)** | **0.671 (16)** | 0.058 (1) |
| June, full moon | 0.310 (7) | 0.395 (9) | 0.614 (14) | 0.676 (16) | 0.288 (6) |
| December, new moon | 0.058 (1) | 0.058 (1) | 0.052 (1) | 0.050 (1) | 0.259 (6) |
| December, full moon | 0.266 (6) | 0.269 (6) | 0.253 (6) | 0.249 (5) | 0.272 (6) |

Without AstraTerra the values at 0° are identical: no installed mod raises the server's night light. At **169°W**
(the Bering Strait, screenshot 1) a full-moon midnight gives **0.058 (1) instead of 0.27–0.31 (6–7)** in March, June,
September and December at 10°N and 40°S: vanilla's moon is below the horizon there (y −0.8) while AstraTerra draws the
local full moon high (y 0.3–0.96). The light follows a moon the player cannot see, and not the one they can.

## Answers

**Too bright for mobs to spawn?** No hostile is held back by the night's light. Drifters, wolves, bears, shivers and
bowtorns use `lightLevelType: OnlyBlockLight` (≤ 7): sunlight and moonlight do not count at all, by day or night.
Sea's floor is client rendering only and never reaches the server. The creatures that do read the time of day are
bells and locusts (`MaxTimeOfDayLight` ≤ 5), night hares (≤ 8) and butterflies (≥ 15). For them vanilla's long
twilight matters: in June at 45°N the darkest moment is light 9, at 60°N 14, so bells and locusts cannot spawn there
all summer. That is vanilla's own behaviour with a real latitude, unchanged by this work. The phantom moon at far
longitudes (above) is a real defect and is fixed (AstraTerra, below).

**Darker nights in the south?** There is no hemisphere effect: the same latitude in the same season has the same
night. What differs is **how far the sun sinks**: twilight ends at −18°. Near the equator the sun drops steeply and
night falls about 70 minutes after sunset all year. At mid and high latitudes in summer it sinks slowly and not far
— above about 48.5° it never reaches −18° around the June solstice, and above 60° it stays above −6.6° (white
nights). So a northern summer (June–July) is far brighter than a southern night at the same time, which is in
winter. Six months later the roles swap. Most people travelling between hemispheres compare northern summer with
southern winter, or a lit northern city with a dark southern countryside; light pollution, moon height and airglow
differ much more than the hemisphere.

## Proposal (targets)

Ownership stays as recorded: **Sea owns the client's outdoor night presentation** (SEA-D27: client only, no gamma,
exposure, block light or server light), **AstraTerra owns the astronomy** (where the moon is, by longitude), and
**Earth only supplies latitude and longitude**. Two changes:

### 1. Sea (client, rendering only): real twilight, recalibrated floor, weather

All on the client's own rendered copy of the day's light (`ClientGameCalendar.Update` postfix: `SunLightStrength`,
`MoonLightStrength`, `DayLightStrength`, the sun and reflect colours). The server, spawning, crops and every gameplay
light query keep vanilla's numbers. One value drives ground, sky dome, clouds, fog brightness and Wmap's far terrain,
so they agree.

| | Target (rendered ambient, mean of rgb, open sky) | Implemented (model) | Before |
|---|---|---|---|
| Sun above the horizon | vanilla (untouched; the sunset session's area) | vanilla | vanilla |
| Sun −3° | a bright dusk | 0.43 | 0.56 |
| Sun −6° (end of civil twilight) | vanilla's colour at −15°, ≈ 0.35–0.4 | 0.39 | 0.45 |
| Sun −9° | dim | 0.13 | 0.42 |
| Sun −12° (end of nautical twilight) and lower, clear, no moon | night: **0.09** (floor 0.10) | 0.089 | 0.41 (−12°), 0.30 (−18°), 0.13 from about −25° |
| High full moon (≥ 35°), clear | ≈ 0.36 (floor 0.40) | 0.355 | 0.40 |
| Quarter moon, high | ≈ 0.18 | 0.183 | 0.27 |
| Full moon 10° up | ≈ 0.2–0.25 | 0.234 | 0.40 |
| Overcast (cloud thickness 0.8), no moon | ≈ 0.02 | 0.018 | 0.035 |
| Overcast, full moon | ≈ 0.07 | 0.070 | 0.105 |
| Rain under full cloud, any moon | vanilla's ≈ 0.02 | 0.018 | 0.025–0.10 |
| Thick fog (density 0.03) under light cloud (0.3), no moon | ≈ 0.03 | 0.027 | 0.096 |

- **Twilight by the sun's depression, not latitude.** Below the horizon the sun term is read at 2.5 × the depression
  angle (−18° reads as vanilla's −45°): vanilla's colours, real timing. Latitude then works by itself: white nights at
  60°N in June, short tropical dusk, long dark winter nights.
- **Floor**: 0.10 clear and moonless at strength 1 (was 0.15; the user now finds moonless nights too light).
  `.sea night 1.5` gives the old level. The floor fades in between 0° and −6°, under the twilight's own light, so the
  dusk darkens steadily into the night (a first fade from −6° to −18° dipped to 0.06 around −15° before the floor
  caught up; changed before release).
- **Moon**: + 0.30 × (lit fraction)^1.5 × height factor (0.35 at the horizon to 1 above 35°). Moon light in the
  rendered daylight is also cut by clouds.
- **Weather**: clear-sky factor (1 − 0.95 c^0.75) × (1 − 0.9 r) × (1 − 0.5 × fog), fog fading in from density 0.004
  to 0.03. Vanilla's scene brightness (overcast 0.9, storm 0.65) still applies on top.
- Off with `NightVisibility 0` (`.sea night 0`): everything vanilla again.

### 2. AstraTerra (both sides): the moon's light comes from the moon it draws

When the longitude-aware sun is on, `GetDayLightStrength` (server: spawning and every gameplay query) and the client's
rendered copy use the moon at the observer's longitude (`LocalMoonTime`: `TotalDays + longitude / 360`, the moon
AstraTerra draws), with vanilla's own formula and phase table. At longitude 0° nothing changes; at 169°W a full-moon
midnight goes back to vanilla's 0.27–0.31 (light 6–7) where the full moon is up. A scope change only in the sense
that AstraTerra already moves the sun; it is the same model applied to the moon.

### Not changed

Server twilight (vanilla's spawn calibration stays; render and spawn light are decoupled by design), block light,
gamma, exposure, Wmap's code (it follows the game's ambient and daylight), the sun's colour above the horizon,
glints, sky reflection and the near/far water hand-off (sunset session).

## Implementation and measurements

See [verification](verification-celestial.md#sea-047-night1--25-october-2026-night-light-issue-7).
