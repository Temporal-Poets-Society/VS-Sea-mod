using System;
using System.Collections.Generic;
using Sea;

namespace SeaTests
{
    /// <summary>
    /// The 0.4.2 review's open findings (backlog SE-0014, SE-0015, SE-0016, SE-0020, SE-0021, SE-0025, SE-0026), fixed
    /// 13 November 2026: wave samples that a change on another thread can't leave mixed, ends kept across a new anchor,
    /// the blend kept for a place asked again in the same moment, Sea and Wind blended by position, fixed winds judged
    /// by sample "now", identical fixed winds joined before any is dropped, and water passes kept in agreement.
    /// </summary>
    static class ReviewChecks
    {
        static Action<bool, string> Check;

        /// <summary>A sea that changes from place to place (as Program's TestSource), with its inputs apart from its revision.</summary>
        sealed class Source : IWaveSource
        {
            public int Asked;
            public long Revision { get; set; }
            public long InputsValue;
            public long Inputs => InputsValue;
            public double Shift;
            /// <summary>Game hours added from this sample on (a new anchor that moves it).</summary>
            public long MovedFrom = long.MaxValue;
            public double Moved;
            /// <summary>Run at the given ask (counting from 1), once: another thread changing the sea meanwhile.</summary>
            public int ChangeAtAsk;
            public Action Change;

            public double HoursOf(long n, double t) => 1000 + n * 0.01 + (n >= MovedFrom ? Moved : 0);

            public SeaState SeaAt(double x, double z, double hours, long n)
            {
                Asked++;
                if (Change != null && Asked == ChangeAtAsk) { var c = Change; Change = null; c(); }
                double s = Math.Clamp(0.2 + x / 4000 + 0.05 * Math.Sin(hours * 3) + Shift, 0, 1.4);
                double hgt = 0.1 + 2.4 * s * s, dir = 0.3 + z / 9000 + Shift;
                return new SeaState(hgt, Math.Cos(dir), Math.Sin(dir), 2.5 + 3.2 * Math.Sqrt(hgt));
            }

            public Wind WindAt(double x, double z, double hours, long n) => Wind.FromCompass(250 + x / 100, Math.Clamp(0.3 + x / 5000 + Shift, 0, 1.5));
        }

        public static void Run(Action<bool, string> check)
        {
            Check = check;
            Console.WriteLine("0.4.2 review findings (SE-0014 to SE-0026, 13 November 2026)");
            MixedSample();
            KeptEnds();
            SameMoment();
            BlendedSeaAndWind();
            FixedByNowSample();
            JoinedFixedWinds();
            WavesOffInject();
        }

        /// <summary>(SE-0014) A change from another thread in the middle of a sample: the sample comes out as after it, at once.</summary>
        static void MixedSample()
        {
            var spec = new WaveSpectrum(4242, 0);
            var src = new Source();
            var place = new WavePlace();
            place.Sample(spec, 700.2, 140, 90, 0, src);
            // half-way through the corners of the next second, the sea changes (and its revision with it)
            src.Asked = 0;
            src.ChangeAtAsk = 3;
            src.Change = () => { src.Shift = 0.25; src.Revision++; src.InputsValue++; };
            place.Sample(spec, 701.3, 140, 90, 0, src);
            var fresh = new WavePlace();
            fresh.Sample(spec, 701.3, 140, 90, 0, src);
            double off = 0;
            for (int i = 0; i < 20; i++) off = Math.Max(off, Math.Abs(place.HeightAt(130 + i * 2.5, 70 - i) - fresh.HeightAt(130 + i * 2.5, 70 - i)));
            Check(off < 1e-12, $"a change on another thread during a sample is worked out again in the same call (off by {off:e1})");
        }

        /// <summary>(SE-0025) A new anchor that leaves this second's hours alone keeps both ends; one that moves only the newest end redoes that end.</summary>
        static void KeptEnds()
        {
            var spec = new WaveSpectrum(99, 0);
            var src = new Source();
            var place = new WavePlace();
            place.Sample(spec, 800.4, 300, 300, 0, src);
            src.Revision++;   // (a new anchor, three samples ahead)
            src.Asked = 0;
            place.Sample(spec, 800.6, 300, 300, 0, src);
            int none = src.Asked;
            var fresh = new WavePlace();
            fresh.Sample(spec, 800.6, 300, 300, 0, src);
            bool same = Math.Abs(place.HeightAt(310, 290) - fresh.HeightAt(310, 290)) < 1e-12;
            // an anchor that moves sample 800 (this second's end) but not 799
            src.MovedFrom = 800;
            src.Moved = 0.5;
            src.Revision++;
            src.Asked = 0;
            place.Sample(spec, 800.7, 300, 300, 0, src);
            int oneEnd = src.Asked;
            var fresh2 = new WavePlace();
            fresh2.Sample(spec, 800.7, 300, 300, 0, src);
            bool same2 = Math.Abs(place.HeightAt(310, 290) - fresh2.HeightAt(310, 290)) < 1e-12;
            Check(none == 0 && same && oneEnd == 4 && same2,
                $"a new anchor keeps the ends whose hours it leaves alone ({none} asks), redoes the one it moves ({oneEnd} asks), and the surface is a new place's");
            // the model's inputs changing still starts over
            src.InputsValue++;
            src.Revision++;
            src.Asked = 0;
            place.Sample(spec, 800.8, 300, 300, 0, src);
            Check(src.Asked == 8, $"a change to the sea's inputs still starts both ends over ({src.Asked} asks)");
        }

        /// <summary>(SE-0026) Asked again at the same place in the same moment: the trains are kept as they are; elsewhere or later, worked out.</summary>
        static void SameMoment()
        {
            var spec = new WaveSpectrum(7, 0);
            var src = new Source();
            var place = new WavePlace();
            place.Sample(spec, 900.25, 50, 60, 0, src);
            var amp = (double[])place.Amp.Clone();
            src.Asked = 0;
            place.Sample(spec, 900.25, 50, 60, 0, src);
            bool kept = src.Asked == 0 && Same(amp, place.Amp, place.Count);
            place.Sample(spec, 900.5, 52, 61, 0, src);
            var fresh = new WavePlace();
            fresh.Sample(spec, 900.5, 52, 61, 0, src);
            bool moved = Same(fresh.Amp, place.Amp, place.Count) && fresh.Count == place.Count;
            // after a one-place Update the grid's blend isn't reused
            place.Update(spec, 900.5, new SeaState(1, 1, 0, 6), Wind.FromCompass(0, 0.5));
            place.Sample(spec, 900.5, 52, 61, 0, src);
            bool again = Same(fresh.Amp, place.Amp, place.Count) && fresh.Count == place.Count;
            Check(kept && moved && again, "a place asked again at the same spot and moment keeps its trains; moved, later or after an Update it gets a new place's");
        }

        static bool Same(double[] a, double[] b, int n)
        {
            for (int i = 0; i < n; i++) if (Math.Abs(a[i] - b[i]) > 1e-15) return false;
            return true;
        }

        /// <summary>(SE-0021) A place's Sea and Wind are its corners' blended by position, so they don't jump half-way across a cell.</summary>
        static void BlendedSeaAndWind()
        {
            var spec = new WaveSpectrum(5, 0);
            var src = new Source();
            var a = new WavePlace();
            var b = new WavePlace();
            // the cell [0, 64) on x, just either side of its middle
            a.Sample(spec, 1000.5, 31.99, 10, 0, src);
            b.Sample(spec, 1000.5, 32.01, 10, 0, src);
            double jumpH = Math.Abs(a.Sea.Height - b.Sea.Height), jumpW = Math.Abs(a.Wind.Strength - b.Wind.Strength);
            // at a corner it's that corner's
            var c = new WavePlace();
            c.Sample(spec, 1000.5, 64, 128, 0, src);
            double h1 = src.HoursOf(WavePlace.SampleIndex(1000.5), 1000.5);
            var corner = src.SeaAt(64, 128, h1, 1000);
            var cw = src.WindAt(64, 128, h1, 1000);
            // half-way along: the mean of the two corners' heights
            var m = new WavePlace();
            m.Sample(spec, 1000.5, 32, 0, 0, src);
            double mean = (src.SeaAt(0, 0, h1, 1000).Height + src.SeaAt(64, 0, h1, 1000).Height) / 2;
            Check(jumpH < 1e-3 && jumpW < 1e-3 && Math.Abs(c.Sea.Height - corner.Height) < 1e-12 && Math.Abs(c.Wind.X - cw.X) < 1e-12
                && Math.Abs(m.Sea.Height - mean) < 1e-12 && Math.Abs(Math.Sqrt(m.Sea.DirX * m.Sea.DirX + m.Sea.DirZ * m.Sea.DirZ) - 1) < 1e-12,
                $"a place's sea state and wind blend its corners' (step across the cell's middle {jumpH:e1} blocks, {jumpW:e1} strength)");
        }

        /// <summary>(SE-0015) The wind model judges a fixed wind by a wave sample when given one; without, as known now.</summary>
        static void FixedByNowSample()
        {
            var m = new WindModel(new WorldShape { Latitude = z => 0, DaysPerYear = 12, HoursPerDay = 24 }, new SeaConfig { SeaWindStrength = 0.1f });
            m.Override = new WindOverride { FromDegrees = 0, Strength = 1.0, StartHours = 100, ActiveSample = 5000 };
            var before = m.At(0, 0, 101, true, 4999);
            var after = m.At(0, 0, 101, true, 5000);
            var known = m.At(0, 0, 101);
            Check(!m.Decides(101, 4999) && m.Decides(101, 5000) && before.Strength < 1e-9 && Math.Abs(after.Strength - 1) < 1e-9 && Math.Abs(known.Strength - 1) < 1e-9,
                "a fixed wind counts from its sample when the wind is asked for at a sample, at once when asked as known now");
        }

        /// <summary>(SE-0020) Identical fixed winds that run on into each other are joined (reading the same everywhere) before any is dropped.</summary>
        static void JoinedFixedWinds()
        {
            // every pair that joins reads the same at every hour and sample, alone or under older and newer ones
            var rng = new Random(20261113);
            int pairs = 0, joined = 0, wrong = 0;
            for (int k = 0; k < 4000; k++)
            {
                double from = rng.Next(3) * 90, strength = rng.Next(2) == 0 ? 0.6 : 1.0;
                long aStart = 1000 + rng.Next(5), aEnd = aStart + rng.Next(1, 6);
                double aHours = 10 + rng.Next(4), aEndHours = aHours + rng.Next(0, 3) + (rng.Next(2) == 0 ? 0.5 : 0);
                var a = new WindOverride { FromDegrees = from, Strength = strength, StartHours = aHours, UntilHours = rng.Next(4) == 0 ? aHours + 2 : 0, ActiveSample = aStart, Ended = true, EndSample = aEnd, EndHours = aEndHours };
                long bStart = rng.Next(3) == 0 ? aEnd + 1 : aEnd;
                var b = new WindOverride
                {
                    FromDegrees = rng.Next(4) == 0 ? (from + 90) % 360 : from, Strength = strength, StartHours = aEndHours - rng.Next(0, 3) * 0.5 + (rng.Next(5) == 0 ? 1 : 0),
                    UntilHours = rng.Next(4) == 0 ? aEndHours + 1 : 0, ActiveSample = bStart, Ended = rng.Next(2) == 0, EndSample = bStart + rng.Next(0, 4), EndHours = aEndHours + rng.Next(-1, 3)
                };
                var older = new WindOverride { FromDegrees = 180, Strength = 0.3, StartHours = 0, ActiveSample = 0, Ended = true, EndSample = aStart, EndHours = aHours + 1 };
                var newer = new WindOverride { FromDegrees = 45, Strength = 0.2, StartHours = aEndHours + 1, ActiveSample = aEnd + 2 };
                pairs++;
                if (!WindOverride.Joins(a, b)) continue;
                joined++;
                var j = WindOverride.Joined(a, b);
                foreach (var (cur, hist, merged) in new[]
                {
                    ((WindOverride)null, new[] { a, b }, new[] { j }),
                    (newer, new[] { older, a, b }, new[] { older, j }),
                })
                {
                    var m1 = new WindModel(new WorldShape { Latitude = z => 0, DaysPerYear = 12, HoursPerDay = 24 }, new SeaConfig()) { Override = cur, History = hist };
                    var m2 = new WindModel(new WorldShape { Latitude = z => 0, DaysPerYear = 12, HoursPerDay = 24 }, new SeaConfig()) { Override = cur, History = merged };
                    for (long s = aStart - 2; s <= aEnd + 8; s++)
                        for (double h = aHours - 2; h <= aEndHours + 4; h += 0.25)
                        {
                            var f1 = m1.FixedAt(h, s);
                            var f2 = m2.FixedAt(h, s);
                            if ((f1 == null) != (f2 == null) || f1 != null && (f1.FromDegrees != f2.FromDegrees || f1.Strength != f2.Strength)) wrong++;
                        }
                }
            }
            Check(joined > 100 && wrong == 0, $"joined fixed winds read the same at every hour and sample ({joined} of {pairs} random pairs joined, {wrong} differences)");

            // an admin setting the same wind 100 times within an hour: nothing still needed is dropped
            WindOverride current = null;
            var history = Array.Empty<WindOverride>();
            int dropped = 0;
            for (int i = 0; i < 100; i++)
            {
                long sample = 200000 + i * 3;
                double hours = 2000 + i * 0.01;
                dropped += WindOverride.Change(ref current, ref history, new WindOverride { FromDegrees = 270, Strength = 0.8, StartHours = hours }, sample, hours, hours);
            }
            Check(dropped == 0 && history.Length <= WindOverride.MaxHistory && history.Length >= 1,
                $"the same fixed wind set 100 times within an hour drops none ({history.Length} kept)");
            // different winds each time: the oldest still go, as before (and Sea logs it)
            current = null;
            history = Array.Empty<WindOverride>();
            dropped = 0;
            for (int i = 0; i < 100; i++)
            {
                long sample = 300000 + i * 3;
                double hours = 3000 + i * 0.01;
                dropped += WindOverride.Change(ref current, ref history, new WindOverride { FromDegrees = i * 7 % 360, Strength = 0.8, StartHours = hours }, sample, hours, hours);
            }
            Check(dropped == 100 - 1 - WindOverride.MaxHistory && history.Length == WindOverride.MaxHistory, $"100 different fixed winds within an hour still drop the oldest ({dropped})");
        }

        /// <summary>(SE-0016) With Sea's waves off (the water passes disagreed), Sea's code goes in and vanilla's wave call stays.</summary>
        static void WavesOffInject()
        {
            string shaders = System.IO.Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? System.IO.Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "Vintagestory"), "assets", "game", "shaders");
            foreach (string pass in WaterShader.Passes)
            {
                string file = System.IO.Path.Combine(shaders, pass + ".vsh");
                if (!System.IO.File.Exists(file)) { Console.WriteLine($"  skip  {pass}.vsh not found"); continue; }
                string code = System.IO.File.ReadAllText(file);
                string kept = WaterShader.Inject(code, out bool waves, true, false);
                Check(kept != null && !waves && kept.Contains("worldPos = sea_Water(worldPos, xyz + origin);") && !kept.Contains("worldPos = sea_Waves(")
                    && kept.Contains("applyLiquidWarping("), $"{pass}.vsh: with Sea's waves off, its code goes in and vanilla's waves stay");
            }
        }
    }
}
