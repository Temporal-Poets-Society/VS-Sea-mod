# Waves and wind

**Vanilla:** the water moves with the same small random bumps everywhere; they only get a little bigger with the wind,
and every player's game draws its own.

**Sea:** the sea has real waves. They run the way the wind has been blowing, grow with the wind of the past hours, and
are worked out the same way on the server and on every player's game.

## Where the wind comes from

Sea doesn't make weather. The wind its waves follow is, in this order:

1. **An admin's fixed wind** (`/sea setwind`), while it lasts.
2. **A weather mod's wind**, normally **Earth**: its real prevailing winds by month, travelling storms and gusts.
3. **Otherwise a steady wind from the config** (`SeaWindFrom`, `SeaWindStrength`): by default a medium breeze from the
   west. The game's own wind changes from moment to moment and isn't the same on every player's game, so the sea
   can't follow it. Boats and ships do: they sail by the game's wind, from the west or from wherever a wind direction
   mod (Pelagus Winds, Wind Vanes) turns it (since 0.6.3).

`/sea` says which one it is.

Wind strength is given in the game's units:

| Strength | Game name | Beaufort |
| --- | --- | --- |
| 0.15 | light breeze | 3 |
| 0.3 | medium breeze | 4 |
| 0.6 | strong breeze | 7 |
| 1 | storm | 10 |
| 1.5 (the most Sea takes) | — | 12 |

## How the sea builds and calms

- The waves follow the wind of the past hours at that place. At the default day length (a game hour is 2 real
  minutes) the sea takes about 6 real minutes to get up.
- In a storm (strength 1) the sea reaches about 1.3 blocks after an hour and 2.1 after four; fully built it is about
  2.5 blocks high. At strength 1.5 it is over 5 blocks, and Beaufort 12 waves are about 5.5 blocks high, 99 long, one
  every 8 seconds.
- Stronger winds make steeper, younger seas.
- After the wind drops, the swell goes on for hours.
- A calm sea is nearly flat. With the default steady breeze the sea runs east at about 0.3 blocks.
- Nothing jumps: when the wind changes, wave trains grow and fade; they never skip.

**Storm wave height:** an admin can make storm seas higher, up to about 12-block waves at a hurricane with
the world option (the config file allows more; see [Settings](Settings)). Breezes stay as they are.

## Rivers, lakes and bays

Rivers and lakes keep vanilla's ripples: Sea's waves run only on water the world's ocean map counts as sea. In bays and
estuaries they fade in, from about a quarter of the open sea's height to all of it. Lava and other liquids keep
vanilla's look.

## The same sea for everyone

Nothing about the waves is sent while you play, apart from a clock. When you join, the server sends a small packet (a
key for the wave pattern, its settings and the world's sizes); after that the server and every game work out the same
sea from the game time. A boat therefore rides the same wave on your screen as on the server.

- On a **Cylinder World** ring, the waves repeat exactly around it, so the seam is invisible on the water too.
- Latitude comes from the game, Cylinder World or Earth; `/sea` shows it.
