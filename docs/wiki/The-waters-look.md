# The water's look

Sea redraws the sea's surface with its waves. The settings below are in `sea.json`; each player can also try other
strengths on their own game with the `.sea` commands (see [Commands](Commands)).

## Sun and moon on the water

A glitter path from the sun and a trail from the moon that break up on the waves, instead of vanilla's one highlight.
The moon's trail and the sun's colour follow AstraTerra's sky when it is installed. Setting: `CelestialReflections`
(0 gives vanilla's highlight back).

## The sky in the water

The sea reflects the sky more at a low angle and less when you look straight down, as real water does, near you and on
World Map's far sea. Stars aren't reflected. Setting: `SkyReflections`.

## Foam and spray

- **Whitecaps:** foam on breaking crests, opening into lace and fading behind the crest, more as the wind rises.
- **Spray:** from about Beaufort 5 the wind tears spray off breaking crests near you; from a gale it also blows
  spindrift low and fast downwind.
- **Surf:** with coasts on, breaking waves on beaches draw whitewater and throw spray (see [Coasts](Coasts)).
- `.sea foam all|off` shows or hides Sea's foam on your own game: whitecaps, surf whitewater and spray.

## Night

A clear moonless night outdoors has a little light, a full moon lights the land, and twilight lasts as long as it
really does. Setting: `NightVisibility` (0 keeps the game's own night and twilight).

## Rivers

Flowing water is drawn the same whichever way it flows, so rivers show no lighter or darker patches by direction.

## Far away

Glitter and foam fade out by the view distance set in your game's graphics settings.

## When the water code can't go in

If another mod changes the water shader so that Sea's code can't go in, Sea falls back to vanilla's waves (and ships
show water inside), with a warning in the log; `.sea` says what is missing.
