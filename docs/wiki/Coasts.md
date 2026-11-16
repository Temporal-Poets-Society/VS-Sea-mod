# Coasts

**Vanilla:** waves look the same at the shore as in open water.

**Off by default since 0.6.2,** until the coasts are reworked: near coasts the waves showed borders where they turn
toward the shore. Without them the open sea's waves run up to every shore. To try them, an admin sets `Coasts` to
`true` in the server's `sea.json` (see [Settings](Settings)) and restarts. This page describes them as they are then.

**Sea:** near coasts the waves feel the sea floor (config `Coasts`). Sea reads the depth from the blocks around you, so
a channel you dig, a pier or a sea wall changes the surf too.

## Waves on beaches

- Over shallow water the swell turns until its crests run along the beach, grows as the water gets shallower, and
  steepens into breakers with a peaked crest and a steep face toward the shore.
- A wave can't be higher than the depth allows, so it breaks before it gets too high for the water.
- Breaking surf is drawn white and throws spray.
- Crests reach a beach one stretch after another, not all at once.

## Sheltered water

Islands and headlands keep waves off the water behind them. Swell and long waves bend in behind land partly; the wind's
own short waves come back further out, and longer ones only with more open water.

## Waves thrown back

A cliff, a sea wall or a steep rocky shore throws back the waves running at it; a gently sloping beach throws back almost none.
In front of a wall the incoming and reflected waves meet, and the water stands up and down in place with crests up to
twice as high.

## Currents and rips

- Waves breaking at an angle drive water along the beach.
- The water they pile up runs back out to sea through **rip channels**: fixed places along gently sloping beaches, about
  250 blocks apart on average. Over a rip the breakers have a gap and the waves bow seaward.
- Rafts and sailboats drift with the current.

## Swash

Off since 0.6.2, until it is reworked. In 0.6.0 and 0.6.1 each wave ran up the beach as a thin sheet with a white front
and left the sand dark for a few seconds, but it was drawn as whole pale squares, on grass as well as on sand.

## Seeing it

`.sea coast` shows the coast where you stand: the depth, how much of the sea gets past the land, how steep the shore is,
how much is thrown back, breaking, whitewater, the current and any rip.

## Limits

- The coast is worked out within about 110 blocks of you; farther out the water eases into the open sea.
- Sheltered gulfs, and lakes the world's ocean map counts as sea, still get the open sea's waves away from their
  shores, because the sea's size comes from the weather, not from how much open water is around.
- On a Cylinder World ring whose seam position or circumference isn't a multiple of 32 blocks there is no surf;
  `.sea coast` says so.
- Shallow banks far from land don't hold the sea down.
- A boat in front of a wall rises and falls but isn't pushed to and fro by the reflected waves.
- Where the waves change size within a block or two (the foot of a cliff, the edge of a rip in a storm) the water can
  look folded at a few points.
- Checked in game on one client.
