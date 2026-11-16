# FAQ

**The sea is almost flat. Is Sea working?**
Without a weather mod the sea follows a steady medium breeze and stays low (about 0.3 blocks). Type `/sea` to see the
wind. With Earth, the sea follows the real wind and can be calm for days in the tropics' quiet belts. An admin can try
a storm with `/sea setwind W 1 0 built`.

**I changed the wind with `/sea setwind` but the trees and rain didn't change.**
A fixed wind only drives the sea and boats. The weather's own effects stay the weather mod's (or vanilla's).

**My sailboat filled with water and sank.**
Waves came over its sides. Sneak and right-click the hull with an empty bucket to bail. A sunk boat is still there with
its cargo.

**I can't pick up my boat.**
It has water in it. Bail it dry first.

**Why can't I sail straight into the wind?**
Nobody can: within 40° of the wind a sail gives no drive. Tack at 45° or more off the wind.

**Storms are too tame (or too wild) for my server.**
Set the world's storm height: `/worldconfig seaStormHeight 2.2` for the biggest seas the world option gives, `1` for Sea's normal ones, then
restart. Or change `StormHeightScale` in `sea.json` for every world that uses `json`.

**The water inside my ship shows through.**
The ship mod asks Sea to keep its hull dry. If another mod changed the water shader so that Sea's code couldn't go in,
there are no dry hulls; `.sea` says so.

**The water looks too bright or too dark at night.**
Try `.sea night 0.5` (or another value from 0 to 2) on your own game; `.sea night default` goes back. A server owner
can set `NightVisibility` in `sea.json` for everyone.

**Does everyone see the same waves?**
Yes. The server and every player work out the same sea from the game time.
