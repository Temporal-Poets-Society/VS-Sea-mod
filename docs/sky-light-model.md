# How the sky is lit at sunset (25 October 2026)

Written after play-test issue 5 (sunset off Portugal, 37.05°N 8.97°W): the glitter path was orange but the sun was pale, the
sky had no warm glow around the sun, and there was a visible band where the near waves met the far sea. This note covers
the light model as it stands with Sea 0.4.7-pre.5, AstraTerra 0.15.0-dev.20 (Earth test build) and Wmap 0.5.0-pre.6, and
what those builds change. **None of it has been seen in game yet.**

## Four layers draw the sky, and each takes its light from somewhere else

1. **The game's sky dome** (`sky.fsh` → `skycolor.fsh getSkyColorAt`). Two lookup tables:
   - `sky.png`: the base colour, by the sun's height and how high you look. AstraTerra ships an override that keeps the
     dusk blue about 6° longer (`tools/brighten_twilight_sky.py`, D27).
   - `sunlight.png`: a glow around the sun, by the sun's height and the angle to the sun. The game shifts its lookup by
     twice a **random value it picks each day** (`SunsetMod`, about −0.1 to 0.3, near −0.08 on most days). The orange
     glow is in the table, but on most days the shift brings it only once the sun is 10° or more *below* the horizon. A sun on the sea
     therefore sits in a grey-blue sky with only a pale halo. That is what the screenshot shows.
   - Then the dome is fogged and, as daylight falls, faded into the fog colour.
   The same `sunlight.png` also sets the **calendar's** sun colour, reflect colour and daylight (one row, shifted by
   +SunsetMod for the sun and −SunsetMod for the reflection). On a given day the game's sun and the game's reflection can
   therefore differ in colour. That is one reason no mod here edits `sunlight.png`: it would change daylight in gameplay.
2. **The sun disc.** With `SunArt pixel` (the Earth look, D30) AstraTerra draws it, tinted by its own table
   (`Twilight.SunColour`: white above 25°, orange on the horizon). **Bug fixed in dev.20:** the game's light mix
   (`applyLight`) weighs the "ambient" light twice the "block" light. AstraTerra passed white as ambient and the orange as
   block light, so two thirds of the disc stayed white. The game's own sun has the same mix.
3. **Sea's water.** Near water reflects the sky by Fresnel, using Sea's copy of `getSkyColorAt` (it reads the same two
   tables). The sun's glitter path is a separate lobe in the sun's colour.
4. **Wmap's far terrain and far sea** (from 0.75 of the view distance). They fade into the colour `getSkyColorAt` gives
   along the view ray (haze), and the far sea reflects the same function along its wave normals.

## What changed

| Where | Change |
|---|---|
| AstraTerra dev.20 | The drawn sun takes its colour as both ambient and block light, so it is as orange as the table says. A **sunset glow** is patched into the game's sky shader (`SunsetScatter`). It follows the real sun, ignoring the daily shift: a halo around the sun plus a band along the horizon, strongest beneath the sun and faint opposite. It is in the sun's colour, grows below 20° altitude, is full from 3° to just after sunset, is gone by −9°, and cloud and fog mute it to a fifth. The clouds' forward sunlight takes the sun's colour too. |
| Sea 0.4.7-pre.5 | **One colour source:** while AstraTerra draws the sun, the glitter path takes AstraTerra's sun colour (pre.4 used a copy of the table, capped by the game's reflect colour, which on some days is redder than the disc). The sky reflection draws the same sunset glow (AstraTerra's `SunsetScatter.Shown`) and now includes the game's flat fog and the eye's height above the sea, as the dome and Wmap do. **Seam:** the near water fades each wave train once it is shorter than about four pixels, by Wmap's own rule. The slope it loses widens the glint lobe instead, so the glitter path keeps its width. |
| Wmap 0.5.0-pre.6 | Far wave slopes are divided as the near water's are. In open sea the near water divides by 1 (5 × max(0.2, 1 − oceanity)), while Wmap always divided by 5, so the far sea was a fifth as rough. Sea now passes the factor for the camera's place (`seaWaveFactor`). The far haze and far-sea reflection take the same sunset glow (`seaSunset`, passed by Sea). |

Without AstraTerra nothing changes: no glow, and the glint uses Sea's own table as in pre.4. Without Sea, Wmap's far
sea keeps its previous look: its waves are Sea's anyway, and the glow reaches it through Sea.

## The seam, cause by cause

The screenshot's band had three causes. All three are addressed, but **none is confirmed in game**:
- **Waves.** Sea drew every wave train right to its edge (aliased, high-contrast crests 300–400 blocks out). Wmap
  dropped trains shorter than a few pixels. Both now drop them by the same rule.
- **Slope scale.** In open sea Wmap's far normals were 5× too gentle (above).
- **Sky colour.** Sea's reflected sky left out the game's flat fog, so the near water reflected a clearer, darker sky
  than the far sea beyond it.

Not addressed: the base water colour under the reflection (the game's water texture near, Wmap's albedo far). At a
grazing angle it shows only about 10%. Also: Wmap's far sea has no glitter path (Sea fades the glint out between 0.5 and
0.74 of the view distance).

## What to look at in game

At sunset at sea, with Earth + AstraTerra dev.20 + Sea pre.5 + Wmap pre.6:
- The sun disc and its glitter path should be the same orange.
- A warm halo should surround the sun, with a warm band along the horizon beneath it, fading toward the opposite side.
- Where the near waves end (~0.75 of the view distance), the crests should already have smoothed out before the edge,
  and the far sea should be no lighter or flatter than the water in front of it.
- The glitter path should keep its width toward the horizon.

Possible tuning afterwards: the glow's strength (`SunsetScatter`, 0.7 cap, and its altitude ramp), the band's height
(the `10.0` in the shape) and the glint widening (`min(2 × roughness, 0.02)`).
