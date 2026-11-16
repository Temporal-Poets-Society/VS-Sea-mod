# Boats

**Vanilla:** a boat sits flat on the water and rocks by a small motion of its own that has nothing to do with the
water under it; the sailboat's sail drives it the same in every wind.

**Sea:** the game's raft and sailboat ride Sea's waves, the sailboat sails by the wind, and it can take on water in a
storm. No ship mod is needed.

## On the waves

- The hull rises, falls, pitches and rolls with the waves under it (up to 30°). The raft responds at once; the heavier
  sailboat a little later, so a rising crest can overtake its side.
- With coasts on (`Coasts` in [Settings](Settings)), boats drift with the surf's current near beaches.
- The view from a boat follows the real wave surface: a trough doesn't put you underwater, but a crest over you does,
  and so does water in the boat.

## Sailing by the wind

- The sailboat's yard and sail turn to the wind by themselves (up to 65° either way). You keep the game's furled, half
  and full sail controls and the rudder.
- You can't sail closer than 40° to the wind. From 45° off the wind you make way, best between 90° and 120° off the
  wind, and with the wind straight behind at three quarters of the best.
- The drive follows the wind: the game's normal sail power in a medium breeze, half in a light one, none in a calm,
  and up to twice from a strong breeze on. Water in the boat slows it.
- The wind is Earth's on Earth worlds, or an admin's fixed wind. Without a weather mod the sail takes the game's own
  wind strength at that place, always from the west; `SeaWindFrom` and `SeaWindStrength` drive the waves only.
- There is no leeway and no capsizing.

## Water aboard (sailboat)

- When waves come over the sailboat's sides, water collects in the boat. It lowers the boat in the water, and a full
  boat sinks. The boat and cargo stay, sunk.
- **Bailing:** sneak and right-click the hull with an empty bucket (or another open liquid container) to throw out some
  of the water, once a second. Looking at the boat shows how full it is.
- You can't pick a boat up while it has water in it: bail it dry first.
- Water doesn't drain by itself.
- The water is saved with the boat. If Sea is removed, the boat behaves as in vanilla; add Sea back and the water is
  still there.

## Ship mods

Ship mods ask Sea to keep the sea out of their hulls, so no water shows inside a ship, and their ships ride Sea's
waves. See [Compatibility](Compatibility).
