using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sea;

namespace SeaTests
{
    /// <summary>
    /// Offline checks: the wind model is deterministic and seamless around the ring, the belts blow the right way in
    /// both hemispheres, storms turn the right way and the cache finds all of them, the sea builds up and calms down,
    /// it's fast, the waves are the same wherever they're worked out and never jump, and the water shader code goes in
    /// where it should.
    ///   dotnet run --project tests/SeaTests
    /// </summary>
    static class Program
    {
        static int failures;

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok) failures++;
        }

        // a Cylinder World planet: 256000 wide with a 1000-block inset, 128000 long, walls 4000 deep
        const double RingStart = 1000, Circumference = 254000, EquatorZ = 64000, PoleDistance = 60000;

        static WorldShape Planet() => new WorldShape
        {
            RingStart = RingStart,
            Circumference = Circumference,
            Latitude = z => Math.Clamp((EquatorZ - z) / PoleDistance, -1, 1),
            DaysPerYear = 108,
            HoursPerDay = 24
        };

        static double ZAt(double latDeg) => EquatorZ - latDeg / 90 * PoleDistance;

        static int Main()
        {
            var cfg = new SeaConfig();
            var m = new WindModel(Planet(), cfg, 12345);
            var m2 = new WindModel(Planet(), cfg, 12345);
            var other = new WindModel(Planet(), cfg, 999);
            var rng = new Random(7);

            Console.WriteLine("determinism and the ring");
            {
                bool same = true, differs = false, ring = true;
                double worstSeam = 0;
                for (int i = 0; i < 2000; i++)
                {
                    double x = rng.NextDouble() * 256000, z = rng.NextDouble() * 128000, h = rng.NextDouble() * 24 * 400;
                    var a = m.At(x, z, h);
                    var b = m2.At(x, z, h);
                    same &= a.X == b.X && a.Z == b.Z;
                    var o = other.At(x, z, h);
                    differs |= Math.Abs(o.X - a.X) > 0.02;
                    var c = m.At(x + Circumference, z, h);
                    ring &= Math.Abs(c.X - a.X) < 1e-9 && Math.Abs(c.Z - a.Z) < 1e-9;
                    var s0 = m.At(RingStart + Circumference - 0.25, z, h);
                    var s1 = m.At(RingStart + 0.25, z, h);
                    worstSeam = Math.Max(worstSeam, Math.Abs(s0.X - s1.X) + Math.Abs(s0.Z - s1.Z));
                }
                Check(same, "same key, same place and time: same wind");
                Check(differs, "another key gives other weather");
                Check(ring, "the wind repeats exactly around the ring");
                Check(worstSeam < 0.05, $"no jump across the seam (worst {worstSeam:0.0000})");
            }

            Console.WriteLine("prevailing winds (no season shift)");
            {
                var flat = new WindModel(Planet(), new SeaConfig { SeasonShift = 0 }, 1);
                void Belt(double lat, double lo, double hi, double minS, string name)
                {
                    var w = flat.Belt(ZAt(lat), 0);
                    double f = w.FromDegrees;
                    bool inRange = lo <= hi ? f >= lo && f <= hi : f >= lo || f <= hi;
                    Check(inRange && w.Strength >= minS, $"{name} at {Math.Abs(lat)}°{(lat >= 0 ? "N" : "S")}: from {Compass.Name(f)} ({f:0}°), {w.Strength:0.00}");
                }
                Belt(15, 30, 80, 0.3, "NE trades");
                Belt(-15, 100, 150, 0.3, "SE trades");
                Belt(47, 230, 280, 0.3, "westerlies");
                Belt(-47, 260, 310, 0.35, "roaring forties");
                Belt(78, 30, 90, 0.15, "polar easterlies");
                Check(flat.Belt(ZAt(32), 0).Strength < 0.03 && flat.Belt(ZAt(-32), 0).Strength < 0.03, "horse latitudes are calm");
                Check(flat.Belt(ZAt(0), 0).Strength < 0.15, $"doldrums are weak ({flat.Belt(ZAt(0), 0).Strength:0.00})");
                Check(flat.Belt(ZAt(-47), 0).Strength > flat.Belt(ZAt(47), 0).Strength, "southern westerlies are stronger");
            }

            Console.WriteLine("seasons");
            {
                double july = 0.55 * 108 * 24, january = 0.05 * 108 * 24;
                Check(m.BeltShift(july) > 7 && m.BeltShift(january) < -7, $"belts move north in July ({m.BeltShift(july):0.0}°) and south in January ({m.BeltShift(january):0.0}°)");
                // at 5°N the NE trades blow in January, but in July the SE trades cross the equator to reach it
                var jan = m.Belt(ZAt(5), january);
                var jul = m.Belt(ZAt(5), july);
                Check(jan.Z > 0 && jul.Z < 0, $"at 5°N: January wind heads south ({Compass.Name(jan.ToDegrees)}), July wind heads north ({Compass.Name(jul.ToDegrees)})");
            }

            Console.WriteLine("storms");
            {
                var shape = Planet();
                var field = new StormField(shape, cfg, 5);
                foreach (double lat in new[] { 45.0, -45.0 })
                {
                    var s = new Storm(1, 100000, ZAt(lat), 0, 48, 350, 0, 1, 2000, false);
                    s.Centre(shape, 24, out double cx, out double cz);
                    var east = field.WindOf(s, cx + 800, cz, 24);
                    bool ok = lat > 0 ? east.Z < 0 && -east.Z > Math.Abs(east.X) : east.Z > 0 && east.Z > Math.Abs(east.X);
                    Check(ok, $"{(lat > 0 ? "north" : "south")}: east of the centre the wind heads {(lat > 0 ? "north (anticlockwise)" : "south (clockwise)")}: {Compass.Name(east.ToDegrees)}, {east.Strength:0.00}");
                    // back to the wind, the low is on your left in the north, on your right in the south
                    int wrong = 0;
                    for (int i = 0; i < 16; i++)
                    {
                        double ang = i * Math.PI / 8, r = 1500;
                        double px = cx + Math.Cos(ang) * r, pz = cz + Math.Sin(ang) * r;
                        var w = field.WindOf(s, px, pz, 24);
                        var side = w.Rotated(lat > 0 ? -Math.PI / 2 : Math.PI / 2);
                        double toCentre = Compass.Bearing(cx - px, cz - pz), sideB = side.ToDegrees;
                        double diff = Math.Abs(((toCentre - sideB) % 360 + 540) % 360 - 180);
                        if (diff > 45) wrong++;
                    }
                    Check(wrong == 0, $"{(lat > 0 ? "north" : "south")}: back to the wind, the centre is on your {(lat > 0 ? "left" : "right")} all round");
                    var eye = field.WindOf(s, cx + 50, cz, 24).Strength;
                    var wall = field.WindOf(s, cx + 600, cz, 24).Strength;
                    Check(eye < 0.3 * wall && wall > 0.9, $"calm eye ({eye:0.00}), strong eyewall ({wall:0.00})");
                    Check(field.WindOf(s, cx + 5000, cz, 24).Strength == 0, "no storm wind beyond its reach");
                    Check(field.WindOf(s, cx + 600, cz, 1).Strength < 0.3 * wall, "young storms are weak");
                }

                // the per-area cache must find every storm that blows there
                double worst = 0;
                int stormy = 0;
                for (int i = 0; i < 60; i++)
                {
                    double x = rng.NextDouble() * 256000, z = ZAt(rng.NextDouble() * 120 - 60), h = 24 * 30 + rng.NextDouble() * 24 * 60;
                    var cached = m.Storms.At(x, z, h);
                    Wind brute = default;
                    foreach (var s in m.Storms.ActiveWithin(x, z, h, 30000)) brute += m.Storms.WindOf(s, x, z, h);
                    worst = Math.Max(worst, Math.Abs(cached.X - brute.X) + Math.Abs(cached.Z - brute.Z));
                    if (brute.Strength > 0.01) stormy++;
                }
                Check(worst < 1e-9, $"the storm cache misses nothing (worst {worst:0.000000}, {stormy}/60 samples near a storm)");

                // (0.3.6) also on a small ring, where a storm's track can come near an area as another copy of it (a whole
                // circumference over) later in the day than the copy nearest at the day's start
                {
                    var small = new WorldShape { RingStart = 1024, Circumference = 24576, Latitude = _ => 0.5 };
                    var sf = new StormField(small, cfg, 5);
                    var r2 = new Random(17);
                    double worstSmall = 0;
                    int near = 0;
                    for (int i = 0; i < 4000; i++)
                    {
                        double x = 1024 + r2.NextDouble() * 24576, z = r2.NextDouble() * 40000, h = 24 + r2.NextDouble() * 24 * 20;
                        var cached = sf.At(x, z, h);
                        Wind brute = default;
                        foreach (var s in sf.ActiveWithin(x, z, h, 30000)) brute += sf.WindOf(s, x, z, h);
                        worstSmall = Math.Max(worstSmall, Math.Abs(cached.X - brute.X) + Math.Abs(cached.Z - brute.Z));
                        if (brute.Strength > 0.01) near++;
                    }
                    // the case the review found: a storm another copy of whose track reaches here later that day
                    {
                        double x = 17461.014, z = 18755.051, h = 114.9;
                        Wind brute = default;
                        foreach (var s in sf.ActiveWithin(x, z, h, 30000)) brute += sf.WindOf(s, x, z, h);
                        var cached = sf.At(x, z, h);
                        worstSmall = Math.Max(worstSmall, Math.Abs(cached.X - brute.X) + Math.Abs(cached.Z - brute.Z));
                        Console.WriteLine($"         small ring at ({x}, {z}), hour {h}: storm wind {brute.Strength:0.000}, cached {cached.Strength:0.000}");
                    }
                    Check(worstSmall < 1e-9 && near > 100, $"… also on a small ring, across catalog borders (worst {worstSmall:0.000000}, {near}/4000 near a storm)");
                }

                // (0.3.6) a client's latitudes arriving after its model was built: the same function, new data behind it
                // (as vanilla does it). Forgetting the storms rolls them again with the new latitudes, and a lookup that
                // was still working out the old ones when they were forgotten can't put them back
                {
                    var planet = Planet();
                    bool arrived = false;
                    var gate = new System.Threading.ManualResetEventSlim(true);
                    var entered = new System.Threading.ManualResetEventSlim(false);
                    var oldHere = new System.Threading.ThreadLocal<bool>();   // a lookup that started before they arrived
                    var lateShape = new WorldShape
                    {
                        RingStart = planet.RingStart, Circumference = planet.Circumference, DaysPerYear = planet.DaysPerYear, HoursPerDay = planet.HoursPerDay,
                        Latitude = z => { entered.Set(); gate.Wait(); return arrived && !oldHere.Value ? planet.Latitude(z) : 0.5; }   // 45°N until they arrive
                    };
                    var lateField = new StormField(lateShape, cfg, 5);
                    var refField = new StormField(planet, cfg, 5);
                    double x = 50000, z = ZAt(15), hh = 24 * 200 + 7;
                    int[] Ids(StormField f, double hours) => f.Near(x, z, hours).Select(s => s.Id).ToArray();
                    var stale = Ids(lateField, hh);   // rolled with the wrong latitudes
                    var want = Ids(refField, hh);
                    arrived = true;
                    bool kept = Ids(lateField, hh).SequenceEqual(stale);
                    lateField.Forget();
                    Check(kept && Ids(lateField, hh).SequenceEqual(want) && !stale.SequenceEqual(want),
                        $"latitudes that arrive late roll the storms again once forgotten ({want.Length} storms near 15°N, {stale.Length} before)");

                    // a lookup of another day, held inside its work with the old latitudes while they're replaced
                    arrived = false;
                    lateField.Forget();
                    double nextDay = hh + 24;
                    entered.Reset();
                    gate.Reset();
                    var slow = System.Threading.Tasks.Task.Run(() => { oldHere.Value = true; return Ids(lateField, nextDay); });
                    entered.Wait();
                    arrived = true;
                    lateField.Forget();
                    gate.Set();
                    var slowIds = slow.Result;
                    Check(Ids(lateField, nextDay).SequenceEqual(Ids(refField, nextDay)),
                        $"a lookup still working with the old latitudes when they're forgotten doesn't put its storms back ({slowIds.Length} it saw)");
                }

                // how stormy is it: fraction of time with gales (≥ 0.6) by belt, over a year
                double Gales(double latLo, double latHi, out double mean)
                {
                    int gales = 0, n = 3000;
                    mean = 0;
                    var r = new Random(11);
                    for (int i = 0; i < n; i++)
                    {
                        double lat = latLo + r.NextDouble() * (latHi - latLo);
                        if (r.Next(2) == 0) lat = -lat;
                        double s = m.At(r.NextDouble() * 256000, ZAt(lat), r.NextDouble() * 108 * 24 + 108 * 24).Strength;
                        mean += s;
                        if (s >= 0.6) gales++;
                    }
                    mean /= n;
                    return (double)gales / n;
                }
                double gTrade = Gales(8, 22, out double mTrade), gHorse = Gales(30, 34, out double mHorse), gWest = Gales(40, 58, out double mWest);
                Console.WriteLine($"         gales: trades {gTrade:P1} (mean {mTrade:0.00}), horse latitudes {gHorse:P1} (mean {mHorse:0.00}), westerlies {gWest:P1} (mean {mWest:0.00})");
                Check(gWest > 0.02 && gWest < 0.25, "westerlies have gales a few percent of the time, not all the time");
                Check(gTrade < gWest && mTrade > mHorse && mWest > mHorse, "trades steadier than westerlies; horse latitudes calmest");
                Check(mTrade > 0.2 && mTrade < 0.55, "trade winds are a good sailing breeze");

                // trade winds keep their direction
                int fromEastish = 0;
                for (int i = 0; i < 1000; i++)
                {
                    var w = m.At(rng.NextDouble() * 256000, ZAt(12 + rng.NextDouble() * 8), rng.NextDouble() * 108 * 24);
                    double f = w.FromDegrees;
                    if (f >= 0 && f <= 135 || f >= 345) fromEastish++;
                }
                Check(fromEastish > 800, $"trade winds blow from N..E..SE {fromEastish / 10.0:0}% of the time");
                var peak = Enumerable.Range(0, 20000).Select(i => m.At(rng.NextDouble() * 256000, rng.NextDouble() * 128000, rng.NextDouble() * 108 * 24 * 3).Strength).Max();
                Check(peak <= WindModel.MaxStrength + 1e-9, $"never above {WindModel.MaxStrength} (max seen {peak:0.00})");
            }

            Console.WriteLine("sea state");
            {
                // (0.3.6) WindModel "Off": vanilla's wind for everything else, but the sea follows Sea's steady winds (the
                // same on every side), not nothing and not vanilla's
                var off = new WindModel(Planet(), new SeaConfig { WindModel = "Off" }, 42);
                var steady = new WindModel(Planet(), new SeaConfig { WindModel = "Steady" }, 42);
                double x = 50000, z = ZAt(15), h = 3000;
                var so = off.SeaAt(x, z, h);
                var ss = steady.SeaAt(x, z, h);
                Check(off.At(x, z, h).Strength == 0 && so.Height == ss.Height && so.DirX == ss.DirX && off.SeaWind(x, z, h).X == steady.At(x, z, h).X && so.Height > 0.3,
                    $"with WindModel Off the sea follows Sea's steady winds ({so.Height:0.00} blocks in the trades), Sea leaves the wind itself to vanilla");
            }
            {
                var sm = new WindModel(Planet(), cfg, 42);
                double x = 50000, z = ZAt(45), t0 = 2000;
                double before = sm.SeaAt(x, z, t0 - 0.5).Height;
                sm.Override = new WindOverride { FromDegrees = 270, Strength = 1.0, StartHours = t0, UntilHours = t0 + 16 };
                var s1 = sm.SeaAt(x, z, t0 + 1);
                var s4 = sm.SeaAt(x, z, t0 + 4);
                var s15 = sm.SeaAt(x, z, t0 + 15.5);
                var after2 = sm.SeaAt(x, z, t0 + 18);
                var after12 = sm.SeaAt(x, z, t0 + 30);
                Console.WriteLine($"         heights: before {before:0.00}, +1h {s1.Height:0.00}, +4h {s4.Height:0.00}, +15h {s15.Height:0.00}, 2h after {after2.Height:0.00}, 14h after {after12.Height:0.00}");
                Check(s1.Height < s4.Height && s4.Height < s15.Height, "a storm's sea builds up over hours");
                Check(Math.Abs(s15.Height - sm.DevelopedHeight(1.0)) < 0.05, $"and reaches the developed height ({sm.DevelopedHeight(1.0):0.0} blocks)");
                Check(after2.Height > 0.4 * s15.Height && after12.Height < after2.Height, "swell lingers after the wind drops, then fades");
                Check(s15.DirX > 0.99 && s15.Wavelength > 8, $"waves run downwind (east) and are long enough for the water mesh ({s15.Wavelength:0} blocks, {s15.Period:0.0} s)");
            }

            Console.WriteLine("speed");
            {
                var sw = Stopwatch.StartNew();
                int n = 200000;
                double sum = 0;
                for (int i = 0; i < n; i++) sum += m.At(i * 13.7 % 256000, 20000 + i * 0.37 % 90000, 5000 + i * 0.0002).Strength;
                double us = sw.Elapsed.TotalMilliseconds * 1000 / n;
                Check(us < 10, $"wind lookup {us:0.00} µs (mean {sum / n:0.00})");
                sw.Restart();
                for (int i = 0; i < 2000; i++) m.SeaAt(i * 97.0, 60000, 7000 + i * 0.01);
                Check(sw.Elapsed.TotalMilliseconds / 2000 < 0.5, $"sea state {sw.Elapsed.TotalMilliseconds * 1000 / 2000:0} µs");
            }

            Console.WriteLine("compass");
            {
                Check(Math.Abs(Wind.FromCompass(0, 1).Z - 1) < 1e-9, "a north wind blows south (+Z)");
                Check(Math.Abs(Wind.FromCompass(270, 1).X - 1) < 1e-9, "a west wind blows east (+X)");
                Check(Math.Abs(Wind.FromCompass(225, 0.5).FromDegrees - 225) < 1e-9, "from-direction round-trips");
                Check(Compass.Parse("sw") == 225 && Compass.Parse("90") == 90 && Compass.Parse("x") == null, "compass parsing");
                Check(Beaufort.Force(1.0) == 10 && Beaufort.Force(0.3) == 4 && Beaufort.Force(0) == 0, "Beaufort scale");
            }

            Waves();

            Console.WriteLine("water shader");
            string shaders = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "Vintagestory", "assets", "game", "shaders");
            foreach (string pass in WaterShader.Passes)
            {
                string file = Path.Combine(shaders, pass + ".vsh");
                if (!File.Exists(file)) { Console.WriteLine($"  skip  {pass}.vsh not found"); continue; }
                string code = File.ReadAllText(file);
                string injected = WaterShader.Inject(code, out bool waves);
                int main = injected?.IndexOf("void main(") ?? -1;
                int warp = injected?.IndexOf("worldPos = seaWaves(", main) ?? -1;
                int clip = injected?.IndexOf("worldPos = seaWater(worldPos, xyz + origin);") ?? -1;
                bool ok = injected != null && waves
                    && injected.IndexOf("uniform vec4 seaWave[24];") < main && injected.IndexOf("vec4 seaWaves(") < main
                    && warp > main && clip > warp
                    && clip < injected.IndexOf("vec4 cameraPos = modelViewMatrix * worldPos;")
                    && injected.IndexOf("applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div)") < 0
                    && WaterShader.Inject(injected) == null;
                Check(ok, $"{pass}.vsh: Sea's waves replace vanilla's, then the hull clip, once");

                // dry hulls: the vertex stage hands each water vertex's position (as drawn) to the fragment stage, which
                // discards the pixels inside a dry hull's outline before anything else
                int fragPos = injected?.IndexOf("seaFragPos = worldPos.xyz;") ?? -1;
                bool vok = injected != null && injected.IndexOf("out vec3 seaFragPos;") >= 0 && injected.IndexOf("out vec3 seaFragPos;") < main
                    && fragPos > clip && fragPos < injected.IndexOf("vec4 cameraPos = modelViewMatrix * worldPos;")
                    && injected.IndexOf("if (seaClipD[h].w > 0.5) continue;") > 0;
                string ffile = Path.Combine(shaders, pass + ".fsh");
                if (!File.Exists(ffile)) { Console.WriteLine($"  skip  {pass}.fsh not found"); continue; }
                string fcode = File.ReadAllText(ffile);
                string finjected = WaterShader.InjectFragment(fcode);
                int fmain = finjected?.IndexOf("void main(") ?? -1;
                int body = fmain < 0 ? -1 : finjected.IndexOf('{', fmain);
                string first = body < 0 ? "" : finjected.Substring(body + 1).TrimStart();
                bool fok = finjected != null && finjected.IndexOf("in vec3 seaFragPos;") < fmain && finjected.IndexOf("bool seaDry(vec3 p)") < fmain
                    && finjected.IndexOf("uniform vec4 seaClipD[8];") < fmain
                    && first.StartsWith("if (seaDry(seaFragPos)) discard;")
                    && finjected.StartsWith(fcode.Substring(0, fcode.IndexOf('\n')))   // #version stays first
                    && WaterShader.InjectFragment(finjected) == null;
                Check(vok && fok, $"{pass}: dry hulls are cut out per pixel ({pass}.vsh passes the position, {pass}.fsh discards first thing), once");
            }

            Console.WriteLine("dry hulls");
            {
                // a hull heading east (yaw π/2: the keel along +X), footprint 30 x 10 with a 1-block margin, waterline at y 100
                var clip = new WaterClipBox
                {
                    X = 1000, Z = 2000, WaterY = 100, Yaw = Math.PI / 2, HalfLength = 15, CentreOffset = 0, HalfWidth = 5,
                    Dry = true, DryInset = 1, DryDepth = 3, DryHeight = 4
                };
                Check(clip.InDryHull(1010, 99, 2003.5) && clip.InDryHull(990, 97.5, 1996.5) && clip.InDryHull(1000, 103.9, 2000),
                    "inside the hull's outline, from its keel to its decks, it's dry");
                Check(!clip.InDryHull(1010, 99, 2004.5) && !clip.InDryHull(1014.5, 99, 2000) && !clip.InDryHull(1000, 96.5, 2000) && !clip.InDryHull(1000, 104.5, 2000),
                    "the margin around the hull (the footprint less the inset), below its keel and above its decks are not");
                var turned = clip;
                turned.Yaw = 0;   // now the keel runs along +Z
                Check(turned.InDryHull(1000, 99, 2010) && !turned.InDryHull(1010, 99, 2000), "the outline turns with the hull's heading");
                var shaped = clip;
                shaped.Profile = new float[16];
                for (int i = 0; i < 16; i++) shaped.Profile[i] = i < 8 ? 5 : 2;   // broad aft, narrow forward
                Check(shaped.InDryHull(990, 99, 2003.5) && !shaped.InDryHull(1012, 99, 2003.5) && shaped.InDryHull(1012, 99, 2000.5),
                    "the outline follows the hull's width profile along the keel");
                var wet = clip;
                wet.Dry = false;
                Check(!wet.InDryHull(1010, 99, 2003.5), "a hull that isn't dry keeps the old clip (and no water is cut)");
                Check(Math.Abs(shaped.HalfWidthAt(-1) - 5) < 1e-9 && Math.Abs(shaped.HalfWidthAt(1) - 2) < 1e-9 && Math.Abs(clip.HalfWidthAt(0.3) - 5) < 1e-9,
                    "the width profile is read the way the shader reads it");
                var rolled = clip;
                rolled.SlopeAcross = 0.2f;   // her waterline rises toward +Z: she's rolled, her masts lean toward -Z
                Check(!rolled.InDryHull(1000, 103, 2003.5) && rolled.InDryHull(1000, 103, 1995.6) && clip.InDryHull(1000, 103, 2003.5) && !clip.InDryHull(1000, 103, 1995.6),
                    "the outline leans with her roll: high up it shifts toward the side she leans to");
                var pitched = clip;
                pitched.SlopeAlong = 0.1f;   // bow up
                Check(!pitched.InDryHull(1013.9, 103, 2000) && pitched.InDryHull(1013.5, 103, 2000) && pitched.InDryHull(1013, 105.2, 2000) && !pitched.InDryHull(987, 103, 2000) &&
                      clip.InDryHull(1013.9, 103, 2000) && !clip.InDryHull(1013, 105.2, 2000) && clip.InDryHull(987, 103, 2000),
                    "and with her pitch (her ends and her deck heights move with it)");
                var detailed = clip;
                detailed.DryProfile = new float[WaterShader.DryStations * WaterShader.DryLevels];
                detailed.DryLevel0 = -2;
                detailed.DryLevelStep = 2;
                for (int j = 0; j < WaterShader.DryLevels; j++)
                    for (int i = 0; i < WaterShader.DryStations; i++) detailed.DryProfile[j * WaterShader.DryStations + i] = j < 2 ? 2 : 4;
                Check(detailed.HasDryProfile && !detailed.InDryHull(1000, 100, 2003) && detailed.InDryHull(1000, 102, 2003) &&
                      detailed.InDryHull(1000, 101, 2002.9) && !detailed.InDryHull(1000, 101, 2003.1) && detailed.InDryHull(1000, 97.1, 2001.9),
                    "a detailed outline gives the hull's width at each height (between rows it's interpolated, beyond them the nearest row holds)");
                Check(detailed.InDryHull(1013.9, 102, 2000) && !detailed.InDryHull(1014.1, 102, 2000) && detailed.InDryHull(1000, 100, 2003, false),
                    "its stations run from stern to bow (the footprint less the inset); the shader's fallback for far hulls is the footprint's outline");
                var raked = detailed;
                raked.DryRake = -0.5f;   // a transom leaning out aft: its stern end 0.5 further aft per block up
                Check(raked.InDryHull(986.1, 100, 2000) && !raked.InDryHull(985.9, 100, 2000) && raked.InDryHull(985.1, 102, 2000) &&
                      !raked.InDryHull(984.9, 102, 2000) && !raked.InDryHull(986.9, 98, 2000) && raked.InDryHull(987.1, 98, 2000) &&
                      raked.InDryHull(1013.9, 102, 2000) && !raked.InDryHull(1014.1, 102, 2000),
                    "a raked stern end: the cut ends at the transom's plane, further aft higher up (the bow end stays)");
                var flooded = raked;
                flooded.Flooded = true;
                flooded.FloodY = 99.2;   // water in her hold, 0.8 under her waterline
                Check(!flooded.InDryHull(1000, 99.1, 2001.5) && !flooded.InDryHull(1000, 97.5, 2001.9) && flooded.InDryHull(1000, 99.3, 2001.5) &&
                      flooded.InDryHull(1000, 102, 2003) && raked.InDryHull(1000, 99.1, 2001.5) && !flooded.InDryHull(1000, 99.3, 2004.5),
                    "flooded: under the water in her hold the sea isn't cut away, over it the hull is dry as before (the outline unchanged)");
                var floodedRolled = flooded;
                floodedRolled.SlopeAcross = 0.2f;   // rolled: her outline leans, the water in her stays level
                Check(!floodedRolled.InDryHull(1000, 99.15, 2001.5) && !floodedRolled.InDryHull(1000, 99.15, 1998.5) &&
                      floodedRolled.InDryHull(1000, 99.25, 2001.5) && floodedRolled.InDryHull(1000, 99.25, 1998.5),
                    "the flood's level is level in the world however she leans (the same height both sides of her when she's rolled)");
                Check(clip.FloodUniform == WaterClipBox.NoFlood && Math.Abs(flooded.FloodUniform - (-0.8f)) < 1e-5,
                    "the shader gets the flood's height over her waterline (seaClipB.y), and far below it when she's dry");

                // (0.3.6) her own frame exactly: points of her outline tilted the way a ship mod draws her (roll about the
                // keel, then pitch, then her heading: here +X) are inside however far she leans, and just beyond it aren't
                double pitch = 12 * Math.PI / 180, roll = 20 * Math.PI / 180;
                var tilted = clip;
                tilted.SlopeAlong = (float)Math.Tan(pitch);
                tilted.SlopeAcross = (float)(-Math.Tan(roll) / Math.Cos(pitch));
                bool In(double lx, double ly, double lz)
                {
                    double cr = Math.Cos(roll), sr = Math.Sin(roll), cp = Math.Cos(pitch), sp = Math.Sin(pitch);
                    double y1 = ly * cr - lz * sr, z1 = ly * sr + lz * cr;
                    return tilted.InDryHull(tilted.X + lx * cp - y1 * sp, tilted.WaterY + lx * sp + y1 * cp, tilted.Z + z1);
                }
                Check(In(13.9, 0.5, 3.9) && In(-13.9, -2.9, -3.9) && In(13.9, 3.9, -3.9) && In(0, -2.9, 0) &&
                      !In(14.1, 0.5, 0) && !In(-14.1, 0.5, 0) && !In(0, 0.5, 4.1) && !In(0, 0.5, -4.1) && !In(5, 4.1, 0) && !In(5, -3.1, 0),
                    "the outline is her own however she's pitched (12°) and rolled (20°): no strip of her inside left out, nothing outside cut");
                WaterClipBox.HullFrame(tilted.SlopeAlong, tilted.SlopeAcross, 0, 0, 0, out double ox, out double oy, out double oz);
                Check(ox == 0 && oy == 0 && oz == 0, "her frame's origin is the waterline above (X, Z)");
            }
            {
                // the fragment stage leaves the sea under a flooded hull's water alone before anything else
                string fcode = File.Exists(Path.Combine(shaders, "chunkliquid.fsh")) ? WaterShader.InjectFragment(File.ReadAllText(Path.Combine(shaders, "chunkliquid.fsh"))) : null;
                int dyAt = fcode?.IndexOf("float dy = p.y - b.z;") ?? -1, floodAt = fcode?.IndexOf("if (dy < b.y) continue;") ?? -1, hlAt = fcode?.IndexOf("float hl = ") ?? -1;
                Check(fcode != null && dyAt > 0 && floodAt > dyAt && hlAt > floodAt, "the dry cut skips what's under a flooded hull's water (seaClipB.y) first");
            }
            {
                // rain streaks: vanilla turns every streak about world Z (so they all lean east); Sea lays them along the drop's path
                string file = Path.Combine(shaders, RainShader.Pass + ".vsh");
                if (!File.Exists(file)) Console.WriteLine($"  skip  {RainShader.Pass}.vsh not found");
                else
                {
                    string code = File.ReadAllText(file);
                    string injected = RainShader.Inject(code);
                    int rainBranch = injected?.IndexOf("if (rainParticle) {") ?? -1;
                    int normalise = injected?.IndexOf("u = normalize(u);", rainBranch) ?? -1;
                    int streak = injected?.IndexOf("mvmat[1] = modelViewMatrix * vec4(-u, 0.0);") ?? -1;
                    int billboard = injected?.IndexOf("mvmat[0].xyz = vec3(1.0, 0.0, 0.0);") ?? -1;
                    bool ok = injected != null && rainBranch >= 0 && normalise > rainBranch && streak > normalise && billboard > streak
                        && injected.IndexOf("mvmat = mvmat * rotationZ(zangle);") < 0
                        // vanilla's billboarding keeps a rain drop's y axis (it only resets it for other particles)
                        && injected.IndexOf("mvmat[1].xyz", billboard) > injected.IndexOf("if (!rainParticle) {", billboard)
                        && injected.IndexOf("if (!rainParticle) {", billboard) > billboard
                        && RainShader.Inject(injected) == null;
                    Check(ok, $"{RainShader.Pass}.vsh: rain streaks follow the drops' paths instead of all leaning east, once");
                }
            }

            DryWaterParticles();
            BadInputs();
            WaveBudget();

            Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        sealed class CountingParticles : Vintagestory.API.Client.IAsyncParticleManager
        {
            public int Spawned;
            public Vintagestory.API.Common.IBlockAccessor BlockAccess => null;
            public int Spawn(Vintagestory.API.Common.IParticlePropertiesProvider p) { Spawned++; return 1; }
            public int ParticlesAlive(Vintagestory.API.Common.EnumParticleModel model) => 0;
        }

        /// <summary>
        /// The particles vanilla makes in and on the world's water (DryParticles): the game's own methods have the spawns
        /// the patches replace (read from the game's code, as Harmony reads it), and a spawn in a dry place is left out.
        /// </summary>
        static void DryWaterParticles()
        {
            Console.WriteLine("water particles in dry places");
            int Replaced(Type t, string method, Func<System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction>, System.Collections.Generic.IEnumerable<HarmonyLib.CodeInstruction>> transpiler, string with)
            {
                var m = HarmonyLib.AccessTools.Method(t, method);
                if (m == null) return -1;
                var spawn = HarmonyLib.AccessTools.Method(typeof(Vintagestory.API.Client.IAsyncParticleManager), "Spawn");
                var target = HarmonyLib.AccessTools.Method(typeof(DryParticles), with);
                var outIl = transpiler(HarmonyLib.PatchProcessor.GetOriginalInstructions(m)).ToList();
                return outIl.Any(ci => ci.Calls(spawn)) ? -2 : outIl.Count(ci => ci.Calls(target));
            }
            int ambient = Replaced(typeof(Vintagestory.GameContent.ModSystemAmbientParticles), "AsyncParticleSpawnTick", DryParticles.InWaterTranspiler, nameof(DryParticles.SpawnInWater));
            int rain = Replaced(typeof(Vintagestory.GameContent.WeatherSimulationParticles), "SpawnRainParticles", DryParticles.OnWaterTranspiler, nameof(DryParticles.SpawnOnWater));
            int dust = Replaced(typeof(Vintagestory.GameContent.WeatherSimulationParticles), "SpawnDustParticles", DryParticles.OnWaterTranspiler, nameof(DryParticles.SpawnOnWater));
            // ambient: summer air motes, the water specks, fireflies; rain: the drops and their splashes; storm: dust, spray, splashes
            Check(ambient == 3 && rain == 2 && dust == 3, $"vanilla's spawns found and replaced: ambient particles {ambient} (3), rain {rain} (2), storm {dust} (3)");

            var keep = DryParticles.IsDry;
            DryParticles.IsDry = (x, y, z) => x >= 0 && x <= 4 && y >= 0 && y <= 3 && z >= -2 && z <= 2;   // a ship's hold
            try
            {
                var m = new CountingParticles();
                int Spawns(Action a) { int b = m.Spawned; a(); return m.Spawned - b; }
                Vintagestory.API.Common.SimpleParticleProperties P(double x, double y, double z, bool dieInAir, double add = 0) => new()
                {
                    MinPos = new Vintagestory.API.MathTools.Vec3d(x, y, z), AddPos = new Vintagestory.API.MathTools.Vec3d(add, 0, add), MinQuantity = 1, ShouldDieInAir = dieInAir
                };
                int specInHold = Spawns(() => DryParticles.SpawnInWater(m, P(2, 1, 0, true)));
                int specOutside = Spawns(() => DryParticles.SpawnInWater(m, P(6, 1, 0, true)));
                int moteInHold = Spawns(() => DryParticles.SpawnInWater(m, P(2, 1, 0, false)));
                int splashInHold = Spawns(() => DryParticles.SpawnOnWater(m, P(1.5, 2.5, 0, false, 1)));
                int splashOutside = Spawns(() => DryParticles.SpawnOnWater(m, P(7, 2.5, 0, false, 1)));
                int rainOver = Spawns(() => DryParticles.SpawnOnWater(m, P(2 - 30, 2.5, -30, false, 60)));
                Check(specInHold == 0 && specOutside == 1 && moteInHold == 1, $"a water speck isn't spawned in a dry hold ({specInHold}), outside it is ({specOutside}); a mote of air is ({moteInHold})");
                Check(splashInHold == 0 && splashOutside == 1 && rainOver == 1, $"a rain splash isn't spawned on the water in a dry hold ({splashInHold}), outside it is ({splashOutside}); the rain over a wide box is ({rainOver})");
                // (0.3.5) judged over its life: the hold sailing on at 3 a second toward +x, a speck living two seconds
                // just ahead of her is inside her a moment later; one far ahead isn't
                double asked = -1;
                DryParticles.IsDryWithin = (x, y, z, s) =>
                {
                    asked = s;
                    for (double t = 0; t <= s + 1e-9; t += 0.25)
                        if (x >= 3 * t && x <= 4 + 3 * t && y >= 0 && y <= 3 && z >= -2 && z <= 2) return true;
                    return false;
                };
                var ahead = P(6, 1, 0, true); ahead.LifeLength = 2;
                var farAhead = P(20, 1, 0, true); farAhead.LifeLength = 2;
                int sweptIn = Spawns(() => DryParticles.SpawnInWater(m, ahead));
                double life = asked;
                int clear = Spawns(() => DryParticles.SpawnInWater(m, farAhead));
                Check(sweptIn == 0 && clear == 1 && Math.Abs(life - 2) < 1e-6,
                    $"a speck just ahead of a sailing hull isn't spawned ({sweptIn}: she'd be over it within its {life:0.0} s), one far ahead is ({clear})");
                DryParticles.IsDryWithin = null;
                DryParticles.IsDry = null;
                Check(Spawns(() => DryParticles.SpawnInWater(m, P(2, 1, 0, true))) == 1, "with no dry places known, everything is spawned");
            }
            finally { DryParticles.IsDry = keep; DryParticles.IsDryWithin = null; }
        }

        /// <summary>The drawn waves: the same everywhere they're computed, the right size, moving, and never jumping.</summary>
        static void Waves()
        {
            Console.WriteLine("waves");
            var calm = new SeaState(0.1, 1, 0, 2.6);
            var storm = new SeaState(2.5, 1, 0, 7.6);   // running east (+X)
            var gale = Wind.FromCompass(270, 1.0);
            var spec = new WaveSpectrum(4242, 0);

            // a place updated every frame at wave time t (the sea state only read when it's due, like SeaSystem does)
            void Run(WavePlace p, WaveSpectrum s, double t, SeaState sea, Wind wind) => p.Update(s, t, sea, wind);

            {
                var p = new WavePlace();
                Run(p, spec, 100, calm, default);
                double calmMax = 0;
                for (int i = 0; i < 400; i++) calmMax = Math.Max(calmMax, Math.Abs(p.HeightAt(1000 + i * 1.7, 1000 + i * 0.9)));
                Check(calmMax < 0.15, $"no wind, calm sea: the water is nearly flat (highest {calmMax:0.000})");

                for (double t = 100; t < 105; t += 0.1) Run(p, spec, t, storm, gale);
                double sum2 = 0; int n = 0;
                for (int ix = 0; ix < 80; ix++)
                    for (int iz = 0; iz < 80; iz++) { double h = p.HeightAt(1000 + ix * 3.1, 1000 + iz * 2.9); sum2 += h * h; n++; }
                double hs = 4 * Math.Sqrt(sum2 / n);
                Check(Math.Abs(hs - storm.Height) < 0.7, $"storm sea: the waves have about the sea state's height (4σ = {hs:0.00} for {storm.Height:0.00}, {p.Count} trains)");
                Check(p.Count <= WavePlace.MaxWaves && p.Count >= 10, $"a storm sea runs {p.Count} wave trains (the shader takes {WavePlace.MaxWaves})");

                // the shader's numbers give the same surface as the CPU
                var u = new float[WavePlace.MaxWaves * 4];
                int un = p.FillUniforms(u, 1000, 1000);
                double worst = 0;
                for (int i = 0; i < 200; i++)
                {
                    double rx = (i % 20 - 10) * 7.3, rz = (i / 20 - 5) * 5.1;
                    worst = Math.Max(worst, Math.Abs(WavePlace.ShaderHeight(u, un, rx, rz) - p.HeightAt(1000 + rx, 1000 + rz)));
                }
                Check(worst < 1e-3, $"shader uniforms and CPU agree on the surface (worst {worst:0.00000})");
                un = p.FillUniforms(u, 812345.5, -233007.25);
                worst = 0;
                for (int i = 0; i < 200; i++)
                {
                    double rx = (i % 20 - 10) * 7.3, rz = (i / 20 - 5) * 5.1;
                    worst = Math.Max(worst, Math.Abs(WavePlace.ShaderHeight(u, un, rx, rz) - p.HeightAt(812345.5 + rx, -233007.25 + rz)));
                }
                Check(worst < 2e-3, $"… also far from the world's origin (worst {worst:0.00000})");

                // they travel downwind (east): the pattern 2 s later matches the earlier one shifted east
                double[] Line(double t) { var a = new double[400]; for (int i = 0; i < a.Length; i++) a[i] = p.HeightAt(900 + i * 0.5, 1000, t); return a; }
                var before = Line(105);
                var after = Line(107);
                int best = 0; double bestC = double.MinValue;
                for (int sft = -60; sft <= 60; sft++)
                {
                    double c = 0;
                    for (int i = 60; i < 340; i++) c += before[i] * after[i + sft];
                    if (c > bestC) { bestC = c; best = sft; }
                }
                Check(best > 4, $"the waves move downwind, not stand still ({best * 0.5:0.0} blocks in 2 s)");

                // the main direction of the drawn swell follows the sea state's
                foreach (double deg in new[] { 0.0, 37.0, 135.0, 250.0 })
                {
                    double r = deg * Math.PI / 180;
                    var q = new WavePlace();
                    Run(q, spec, 10, new SeaState(2.0, Math.Cos(r), Math.Sin(r), 7.0), Wind.FromCompass(0, 0.0));
                    double sx = 0, sz = 0;
                    for (int i = 0; i < q.Count; i++)
                    {
                        double k = Math.Sqrt(q.Kx[i] * q.Kx[i] + q.Kz[i] * q.Kz[i]), e = q.Amp[i] * q.Amp[i];
                        if (2 * Math.PI / k < 20) continue;   // the long swell
                        sx += e * q.Kx[i] / k; sz += e * q.Kz[i] / k;
                    }
                    double got = Math.Atan2(sz, sx) * 180 / Math.PI, diff = Math.Abs(WorldShape.Mod(got - deg + 180, 360) - 180);
                    Check(diff < 12, $"swell sent toward {deg:0}° runs toward {WorldShape.Mod(got, 360):0}°");
                }
            }

            {
                // the same waves wherever they're worked out: another copy of the library, updated at another rate
                var specB = new WaveSpectrum(4242, 0);
                var a = new WavePlace();
                var b = new WavePlace();
                double worst = 0;
                double tb = 50;
                for (double t = 50; t < 70; t += 1 / 60.0)
                {
                    var sea = t < 58 ? calm : storm;
                    var wind = t < 60 ? default : gale;
                    Run(a, spec, t, sea, wind);
                    while (tb + 1 / 7.0 <= t) { tb += 1 / 7.0; Run(b, specB, tb, tb < 58 ? calm : storm, tb < 60 ? default : gale); }
                    Run(b, specB, t, sea, wind);
                    worst = Math.Max(worst, Math.Abs(a.HeightAt(3000.5, -2000.25) - b.HeightAt(3000.5, -2000.25)));
                }
                Check(worst < 1e-9, $"the same inputs give the same surface on every side, whatever the frame rate (worst {worst:e1})");
                var other = new WavePlace();
                Run(other, new WaveSpectrum(99, 0), 70, storm, gale);
                Run(a, spec, 70, storm, gale);
                Check(Math.Abs(other.HeightAt(10, 10) - a.HeightAt(10, 10)) > 1e-3 || Math.Abs(other.HeightAt(40, 70) - a.HeightAt(40, 70)) > 1e-3,
                    "another world has other waves");
            }

            {
                // a sudden change (calm to storm, and the wind swinging round) grows and turns the waves, never jumps them
                var p = new WavePlace();
                double prev = double.NaN, worstStep = 0;
                for (double t = 200; t < 212; t += 1 / 60.0)
                {
                    var sea = t < 203 ? calm : t < 207 ? storm : new SeaState(2.5, 0, 1, 7.6);
                    var wind = t < 203 ? default : t < 207 ? gale : Wind.FromCompass(0, 1.0);
                    Run(p, spec, t, sea, wind);
                    double h = p.HeightAt(500, 500);
                    if (!double.IsNaN(prev)) worstStep = Math.Max(worstStep, Math.Abs(h - prev));
                    prev = h;
                }
                Check(worstStep < 0.2, $"no jumps when the sea changes at once: largest change in one frame {worstStep:0.000} blocks");
                var q = new WavePlace();
                Run(q, spec, 300, calm, default);
                Run(q, spec, 301, storm, gale);
                double h0 = q.SignificantHeight;
                Run(q, spec, 301.5, storm, gale);
                double h1 = q.SignificantHeight;
                Run(q, spec, 302, storm, gale);
                Check(h0 < 0.3 && h1 > 0.5 && h1 < 2.2 && q.SignificantHeight > 2.2, $"a jump in the sea state is eased in over a second ({h0:0.00} → {h1:0.00} → {q.SignificantHeight:0.00})");

                // (0.3.6) after a gap the old one-end update holds the new sample instead of blending from a stale one
                var held = new WavePlace();
                Run(held, spec, 500, storm, gale);
                Run(held, spec, 510.5, calm, default);
                Check(held.SignificantHeight < 0.3, $"a place that missed seconds doesn't blend from what it saw before them ({held.SignificantHeight:0.00})");
            }

            {
                // (0.3.6) both ends of each second given (SeaSystem.SampleWaves): a place that kept up, one that missed
                // seconds, and one new every frame draw the same surface while the sea changes
                SeaState SeaN(long n) => n < 403 ? calm : storm;   // the sea at each second's start
                Wind WindN(long n) => n < 404 ? default : gale;
                void Both(WavePlace p, double t)
                {
                    long n = WavePlace.SampleIndex(t);
                    if (p.Due(spec, t)) p.Update(spec, t, SeaN(n - 1), WindN(n - 1), SeaN(n), WindN(n));
                    else p.Update(spec, t, default, default);
                }
                var kept = new WavePlace();
                var gappy = new WavePlace();
                double worst = 0;
                for (double t = 400; t < 410; t += 1 / 60.0)
                {
                    Both(kept, t);
                    var late = new WavePlace();
                    Both(late, t);
                    worst = Math.Max(worst, Math.Abs(late.HeightAt(700, 300) - kept.HeightAt(700, 300)));
                    if (t < 402 || t > 405.5)
                    {
                        Both(gappy, t);
                        worst = Math.Max(worst, Math.Abs(gappy.HeightAt(700, 300) - kept.HeightAt(700, 300)));
                    }
                }
                Check(worst < 1e-9, $"the surface doesn't depend on a place's history: new, kept up or after a gap (worst {worst:e1})");
            }

            {
                // (0.3.6) the game hour of each wave sample: the server's calendar moves in 200 ms steps, runs 20 times
                // faster while players sleep and jumps 12 hours at an admin's /time set; a client's lags a little. With
                // the server's anchors every caller on either side gets the same hours for both ends of a sample's blend
                // (n and n - 1), whenever in the second it asks, also a client that joins on an anchor's first sample
                const double perSecond = 30 / 3600.0;
                double Rate(double t) => t >= 40 && t < 60 ? 20 * perSecond : perSecond;
                // /time set: four jumps a second apart (a new anchor each time, the older ones still needed), then one more
                double[] jumps = { 6.9, 7.9, 8.9, 9.9, 26.9 };
                double Exact(double t) => 1000 + perSecond * (Math.Min(t, 40) + 20 * Math.Clamp(t - 40, 0, 20) + Math.Max(0, t - 60)) + 12 * jumps.Count(j => t >= j);
                long seq = 0;
                double Stepped(double t) => Exact(Math.Floor(t / 0.2) * 0.2);   // the calendar as the game has it
                var server = new SampleHours();
                server.Add(SampleHours.Make(0, Stepped(0), Rate(0), 0, ++seq), 0);
                // clients: when they join, and their packets in flight (sent 0.35 s before they arrive)
                var joins = new[] { 12.0, 28.25 };   // the second one joins on the first sample of the anchor made at 25 s
                var clients = joins.Select(_ => new SampleHours()).ToArray();
                var heard = new bool[joins.Length];
                var inFlight = new System.Collections.Generic.List<(int c, double at, HoursAnchor[] all)>();
                var seen = new System.Collections.Generic.Dictionary<(int side, long n), double>();
                double worstSide = 0, worstBetween = 0, worstCalendar = 0;
                void Seen(int side, long n, double h)
                {
                    if (seen.TryGetValue((side, n), out double was)) worstSide = Math.Max(worstSide, Math.Abs(h - was));
                    else seen[(side, n)] = h;
                }
                for (int f = 0; f < 90 * 60; f++)
                {
                    double t = f / 60.0;
                    bool tick = f % (5 * 60) == 0 && f > 0;
                    long now = WavePlace.SampleIndex(t);
                    if (tick) server.Add(SampleHours.Make(t, Stepped(t), Rate(t), 3, ++seq), now);
                    // and once a second: the calendar jumped or changed speed, a new anchor at once (sent like a tick's)
                    else if (f % 60 == 30 && SampleHours.Drifted(server.Newest, t, Stepped(t)))
                    {
                        server.Add(SampleHours.Make(t, Stepped(t), Rate(t), 3, ++seq), now);
                        tick = true;
                    }
                    for (int c = 0; c < joins.Length; c++)
                        if (tick && t >= joins[c] || Math.Abs(t - joins[c]) < 1e-9) inFlight.Add((c, t + 0.35, server.All));   // and one on joining
                    foreach (var p in inFlight.Where(p => p.at <= t + 1e-9).ToList())
                    {
                        foreach (var a in p.all) clients[p.c].Add(a, now);
                        heard[p.c] = true;
                        inFlight.Remove(p);
                    }
                    long n = WavePlace.SampleIndex(t);
                    for (int k = 0; k < 3; k++)   // a few callers per frame, each with its own idea of "now"
                    {
                        double estimate = Stepped(t + k * 0.07) - (t - n) * Rate(t);
                        double s1 = server.At(n, estimate, Rate(t)), s0 = server.At(n - 1, estimate - Rate(t), Rate(t));
                        Seen(-1, n, s1);
                        Seen(-1, n - 1, s0);
                        if (!jumps.Any(j => t >= j && t < j + 5)) worstCalendar = Math.Max(worstCalendar, Math.Abs(s1 - Exact(n)));   // (≤ 5 s behind a jump)
                        for (int c = 0; c < joins.Length; c++)
                        {
                            if (t < joins[c]) continue;
                            double ce = Stepped(t - 0.3) - (t - n) * Rate(t);
                            double c1 = clients[c].At(n, ce, Rate(t)), c0 = clients[c].At(n - 1, ce - Rate(t), Rate(t));
                            if (!heard[c]) continue;
                            Seen(c, n, c1);
                            Seen(c, n - 1, c0);
                            worstBetween = Math.Max(worstBetween, Math.Max(Math.Abs(c1 - s1), Math.Abs(c0 - s0)));
                        }
                    }
                }
                Check(worstSide == 0 && worstBetween == 0 && heard.All(h => h),
                    $"every caller, on the server and on clients (one joining on an anchor's first sample), gets the same game hours for both ends of a sample's blend (worst within a side {worstSide:e1}, between them {worstBetween:e1})");
                Check(worstCalendar < 5 * 20 * perSecond, $"… and they follow the calendar, sleeping included, and catch up with each /time set within 5 s (worst {worstCalendar * 60:0.0} game minutes off)");
            }

            {
                // on a Cylinder World ring the sea repeats exactly around it: no seam
                const double C = 254000;
                var ring = new WaveSpectrum(7, C);
                var p = new WavePlace();
                Run(p, ring, 1234.5, storm, gale);
                double worst = 0;
                var r = new Random(3);
                for (int i = 0; i < 200; i++)
                {
                    double x = RingStart + r.NextDouble() * 400, z = r.NextDouble() * 128000;
                    worst = Math.Max(worst, Math.Abs(p.HeightAt(x, z) - p.HeightAt(x + C, z)));
                }
                Check(worst < 1e-6, $"the sea is seamless around the ring (worst {worst:e1})");
            }

            {
                // the wave clock: snaps when it first hears from the server, then only eases (≤ 2%), and snaps when far off
                var c = new WaveClock();
                c.OnServerTime(5000, 12);
                bool snapped = Math.Abs(c.Now(12) - 5000) < 1e-9;
                c.OnServerTime(5000.3 + 1, 13);   // the server is 0.3 s ahead of us now
                double t0 = c.Now(13), worstRate = 0;
                double local = 13;
                for (int i = 0; i < 60 * 20; i++)
                {
                    double before = c.Now(local);
                    local += 1 / 60.0;
                    c.Advance(1 / 60.0);
                    worstRate = Math.Max(worstRate, Math.Abs((c.Now(local) - before) * 60 - 1));
                }
                bool caughtUp = Math.Abs(c.Now(local) - (5001.3 + local - 13)) < 1e-6;
                c.OnServerTime(9000, local);
                Check(snapped && worstRate <= WaveClock.MaxSlew + 1e-9 && caughtUp && Math.Abs(c.Now(local) - 9000) < 1e-9,
                    $"the wave clock follows the server's: snaps on joining, eases {worstRate:P1} at most, snaps when far off");
            }
        }

        /// <summary>(0.3.7) Numbers Sea can't use (a hand-edited config, an admin's typo, a mod's degenerate hull) are refused or come back to defaults.</summary>
        static void BadInputs()
        {
            Console.WriteLine("bad inputs");
            bool parse = Compass.Parse("NaN") == null && Compass.Parse("Infinity") == null && Compass.Parse("-Infinity") == null
                && Compass.Parse("270") == 270 && Compass.Parse("sw") == 225;
            var nan = new WindOverride { FromDegrees = double.NaN, Strength = 1 };
            var inf = new WindOverride { FromDegrees = 90, Strength = 1, UntilHours = double.PositiveInfinity };
            var strong = WindOverride.Checked(new WindOverride { FromDegrees = 90, Strength = 9, StartHours = 10 });
            var weak = WindOverride.Checked(new WindOverride { FromDegrees = 90, Strength = double.NaN });
            Check(parse && WindOverride.Checked(nan) == null && WindOverride.Checked(inf) == null && weak == null && WindOverride.Checked(null) == null
                && strong != null && strong.Strength == WindModel.MaxStrength && strong.StartHours == 10,
                "a fixed wind with NaN or infinite numbers is refused (the direction, the strength, the hours), a too strong one clamped");

            var bad = new SeaConfig { StormSpeed = -350, StormRadius = float.NaN, WaveScale = float.PositiveInfinity, Gusts = -1, SeasonShift = 400, StormFrequency = 0 }.Checked();
            var d = new SeaConfig();
            Check(bad.StormSpeed == d.StormSpeed && bad.StormRadius == d.StormRadius && bad.WaveScale == d.WaveScale && bad.Gusts == d.Gusts
                && bad.SeasonShift == 45 && bad.StormFrequency == 0,
                "a config's negative, NaN or infinite numbers go back to the defaults (0 stays 0: no storms)");

            // the server's settings reach clients as they are, zeros included (protobuf leaves zeros out, and the
            // client's object starts from the defaults)
            {
                var sent = new SeaParamsPacket
                {
                    Key = 7, Circumference = 0, DaysPerYear = 108, HoursPerDay = 24,
                    Config = new SeaConfig { WindModel = "Steady", PrevailingStrength = 0, Variability = 0, Gusts = 0, StormFrequency = 0, StormRadius = 0, StormSpeed = 0, SeasonShift = 0, WaveScale = 0 }
                };
                // (the game's own serializer, as its network channels use it)
                var got = Vintagestory.API.Util.SerializerUtil.Deserialize<SeaParamsPacket>(Vintagestory.API.Util.SerializerUtil.Serialize(sent)).Config;
                Check(got.WindModel == "Steady" && got.PrevailingStrength == 0 && got.Variability == 0 && got.Gusts == 0 && got.StormFrequency == 0
                    && got.StormRadius == 0 && got.StormSpeed == 0 && got.SeasonShift == 0 && got.WaveScale == 0,
                    $"a config of zeros arrives as zeros (gusts {got.Gusts}, storms {got.StormFrequency}, wave scale {got.WaveScale})");
            }

            // WaveScale 0 flattens the whole sea, the chop too
            var spec0 = new WaveSpectrum(4242, 0, 0);
            var amps = new double[spec0.TrainCount];
            spec0.Amplitudes(new SeaState(0, 1, 0, 2.5), Wind.FromCompass(270, 1), amps);
            double most = 0;
            foreach (var x in amps) most = Math.Max(most, Math.Abs(x));
            var spec1 = new WaveSpectrum(4242, 0, 1);
            spec1.Amplitudes(new SeaState(0, 1, 0, 2.5), Wind.FromCompass(270, 1), amps);
            double chop1 = 0;
            foreach (var x in amps) chop1 = Math.Max(chop1, x);
            Check(most == 0 && chop1 > 0.1, $"WaveScale 0: no waves at all, chop included ({most:0.000}; at 1 the chop is {chop1:0.00})");

            // a hull with no outline left: nothing dry, and no exception
            bool threw = false, dry = false;
            try
            {
                foreach (bool detailed in new[] { false, true })
                {
                    var noLength = new WaterClipBox { Dry = true, HalfLength = 0, HalfWidth = 2, DryDepth = 5, DryHeight = 5, WaterY = 110 };
                    var insetAll = new WaterClipBox { Dry = true, HalfLength = 4, DryInset = 4, HalfWidth = 2, DryDepth = 5, DryHeight = 5, WaterY = 110,
                        DryProfile = new float[WaterShader.DryStations * WaterShader.DryLevels], DryLevelStep = 1 };
                    dry |= noLength.InDryHull(0, 109, 0, detailed) | insetAll.InDryHull(0, 109, 0, detailed);
                }
            }
            catch (Exception) { threw = true; }
            Check(!threw && !dry, "a dry hull with no length, or inset by all of it, has nothing dry and doesn't throw");
        }

        /// <summary>
        /// (0.3.7) While the sea turns, more trains run than the shader takes: those kept are each less the strongest left
        /// out, so the surface doesn't jump when their order changes (Codex's case: key 4242, a 2.5-block sea turning from
        /// east to south; cutting the weakest off made it jump about 0.06 blocks).
        /// </summary>
        static void WaveBudget()
        {
            Console.WriteLine("wave budget");
            var spec = new WaveSpectrum(4242, 0);
            var east = new SeaState(2.5, 1, 0, 7.6);
            var south = new SeaState(2.5, 0, 1, 7.6);
            var wind0 = Wind.FromCompass(270, 1.0);
            var wind1 = Wind.FromCompass(0, 1.0);
            var p = new WavePlace();
            const double dt = 1e-4;
            double worst = 0, worstMotion = 0;
            int most = 0;
            bool swapped = false;
            var before = new System.Collections.Generic.HashSet<(double, double)>();
            var now = new System.Collections.Generic.HashSet<(double, double)>();
            double[] prevH = new double[16];
            for (int i = 0; i <= 10000; i++)
            {
                double t = 300 + i * dt;
                p.Update(spec, Math.Min(t, 300.99999), east, wind0, south, wind1);
                most = Math.Max(most, p.Count);
                now.Clear();
                for (int j = 0; j < p.Count; j++) now.Add((p.Kx[j], p.Kz[j]));
                if (i > 0 && !now.SetEquals(before)) swapped = true;
                (before, now) = (now, before);
                // how far the surface may move in dt from the trains' own running and growing
                double motion = 0;
                for (int j = 0; j < p.Count; j++) motion += p.Amp[j] * p.Omega[j] * dt + 2.5 * dt;
                for (int k = 0; k < prevH.Length; k++)
                {
                    double h = p.HeightAt(1000 + k * 13.7, 2000 + k * 7.3);
                    if (i > 0) worst = Math.Max(worst, Math.Abs(h - prevH[k]));
                    prevH[k] = h;
                }
                worstMotion = Math.Max(worstMotion, motion);
            }
            Check(most == WavePlace.MaxWaves && swapped && worst < 0.01,
                $"the surface doesn't jump when the strongest {WavePlace.MaxWaves} trains change (over budget: {most == WavePlace.MaxWaves}, kept ones changed: {swapped}; biggest step {worst:0.0000} in {dt * 1000:0.0} ms, running alone moves up to {worstMotion:0.0000})");
        }
    }
}
