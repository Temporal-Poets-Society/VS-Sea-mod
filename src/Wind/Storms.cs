using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>
    /// One travelling storm: where and when it forms, how it moves, how strong and big it gets. Its wind turns around
    /// the centre (anticlockwise in the north, clockwise in the south) and spirals a little inward.
    /// </summary>
    public readonly struct Storm
    {
        public readonly int Id;
        public readonly double X0, Z0, T0, Life, VX, VZ, Peak, Radius;
        public readonly bool Tropical;

        public Storm(int id, double x0, double z0, double t0, double life, double vx, double vz, double peak, double radius, bool tropical)
        {
            Id = id; X0 = x0; Z0 = z0; T0 = t0; Life = life; VX = vx; VZ = vz; Peak = peak; Radius = radius; Tropical = tropical;
        }

        /// <summary>Winds stop this far from the centre.</summary>
        public double OuterRadius => Radius * StormField.OuterFactor;
        /// <summary>The strongest winds blow this far from the centre; inside it's calmer (the eye).</summary>
        public double MaxWindRadius => Radius * 0.3;
        public double Speed => Math.Sqrt(VX * VX + VZ * VZ);

        public bool ActiveAt(double hours) => hours > T0 && hours < T0 + Life;

        /// <summary>Peak strength at the eyewall right now: builds up, then dies away over the storm's life.</summary>
        public double Intensity(double hours)
        {
            double a = (hours - T0) / Life;
            if (a <= 0 || a >= 1) return 0;
            return Peak * Math.Pow(Math.Sin(Math.PI * a), 1.2);
        }

        public void Centre(WorldShape shape, double hours, out double x, out double z)
        {
            double dt = hours - T0;
            x = shape.RingX(X0 + VX * dt);
            z = Z0 + VZ * dt;
        }
    }

    /// <summary>
    /// Where storms are, as a pure function of the key and the world: every game day each 6000-block cell of the
    /// map rolls whether a storm forms in it. The odds depend on latitude and season (tropical storms in late summer,
    /// more westerly storms in winter). Storms near a place are cached per 8192-block area and game day.
    /// </summary>
    public sealed class StormField
    {
        public const double CellSize = 6000, SlotHours = 24, MinLife = 36, MaxLife = 84, CatalogSize = 8192;
        public const double OuterFactor = 2.4, MaxRadiusFactor = 1.35, MaxSpeedFactor = 1.25;

        readonly WorldShape shape;
        readonly SeaConfig cfg;
        readonly long key;
        // the storms near each area and day, in generations: forgetting them (new latitudes) starts a new one, so a
        // lookup still working out the old ones can only put them into a generation nobody reads any more
        sealed class Catalog { public readonly ConcurrentDictionary<(long, long, long), Storm[]> Areas = new(); }
        volatile Catalog catalog = new();

        public StormField(WorldShape shape, SeaConfig cfg, long key)
        {
            this.shape = shape;
            this.cfg = cfg;
            this.key = key ^ 0x5707A11L;
        }

        public bool Enabled => cfg.StormFrequency > 0;

        /// <summary>The storm part of the wind at a place.</summary>
        public Wind At(double x, double z, double hours)
        {
            if (!Enabled) return default;
            Wind w = default;
            foreach (var s in Near(x, z, hours)) w += WindOf(s, x, z, hours);
            return w;
        }

        public Wind WindOf(in Storm s, double x, double z, double hours)
        {
            double inten = s.Intensity(hours);
            if (inten <= 0) return default;
            s.Centre(shape, hours, out double cx, out double cz);
            double dx = shape.DeltaX(cx, x), dz = z - cz;
            double r = Math.Sqrt(dx * dx + dz * dz), outer = s.OuterRadius, rm = s.MaxWindRadius;
            if (r >= outer || r < 1e-6) return default;

            double fade = 1 - Noise.SmoothStep(0.45 * outer, outer, r);
            double f = (r < rm ? r / rm : Math.Pow(rm / r, 0.65)) * fade;

            // anticlockwise seen from above in the north (+X east, +Z south), clockwise in the south
            double hs = shape.Latitude(cz) >= 0 ? 1 : -1;
            double tx = dz / r * hs, tz = -dx / r * hs;
            double ix = -dx / r * 0.35, iz = -dz / r * 0.35;
            double ux = tx + ix, uz = tz + iz, ul = Math.Sqrt(ux * ux + uz * uz);
            ux /= ul; uz /= ul;

            // on the side where the storm's own motion adds to its winds they're stronger (the "dangerous semicircle")
            double sp = s.Speed, mx = sp > 0 ? s.VX / sp : 0, mz = sp > 0 ? s.VZ / sp : 0;
            double carry = 0.12 * inten * fade * Math.Min(1, r / rm);
            return new Wind(ux * inten * f + mx * carry, uz * inten * f + mz * carry);
        }

        /// <summary>
        /// (0.3.6) Forget the storms worked out so far: the latitudes changed (a client's arrive after its model was
        /// built), and storms are rolled by latitude. Safe from any thread.
        /// </summary>
        public void Forget() => catalog = new Catalog();

        /// <summary>Storms that may blow at (x, z) during the game day containing `hours`.</summary>
        public Storm[] Near(double x, double z, double hours)
        {
            var cache = catalog.Areas;
            long cx = shape.Wraps ? (long)Math.Floor((shape.RingX(x) - shape.RingStart) / CatalogSize) : (long)Math.Floor(x / CatalogSize);
            long cz = (long)Math.Floor(z / CatalogSize);
            long slot = (long)Math.Floor(hours / SlotHours);
            var k = (cx, cz, slot);
            if (cache.TryGetValue(k, out var list)) return list;
            if (cache.Count > 1024) cache.Clear();
            // (a static factory: a lambda capturing these would be allocated on every lookup, cached or not)
            return cache.GetOrAdd(k, static (key, self) => self.Build(key.Item1, key.Item2, key.Item3), this);
        }

        Storm[] Build(long catX, long catZ, long slot)
        {
            double xLo = (shape.Wraps ? shape.RingStart : 0) + catX * CatalogSize;
            double xHi = xLo + CatalogSize;
            if (shape.Wraps) xHi = Math.Min(xHi, shape.RingStart + shape.Circumference);
            double zLo = catZ * CatalogSize;
            double acx = (xLo + xHi) / 2, acz = zLo + CatalogSize / 2;
            double half = Math.Sqrt((xHi - xLo) * (xHi - xLo) + CatalogSize * CatalogSize) / 2;
            double winLo = slot * SlotHours, winHi = winLo + SlotHours;
            var found = new List<Storm>();
            foreach (var s in Spawned(acx, acz, half, winLo, winHi))
            {
                double tA = Math.Max(s.T0, winLo), tB = Math.Min(s.T0 + s.Life, winHi);
                if (tA > tB) continue;
                // closest approach of its track (during this day) to the area; on a ring, of every copy of the track a
                // whole circumference over that could come near it (the nearest at the day's start needn't be later on)
                double ax = shape.DeltaX(acx, s.X0 + s.VX * (tA - s.T0)), az = s.Z0 + s.VZ * (tA - s.T0) - acz;
                double ex = s.VX * (tB - tA), ez = s.VZ * (tB - tA), reach = s.OuterRadius + half;
                long copies = shape.Wraps ? (long)Math.Ceiling((Math.Abs(ex) + reach) / shape.Circumference) + 1 : 0;
                for (long k = -copies; k <= copies; k++)
                {
                    double kx = ax + k * shape.Circumference;
                    if (SegmentDistance(kx, az, kx + ex, az + ez) > reach) continue;
                    found.Add(s);
                    break;
                }
            }
            return found.ToArray();
        }

        /// <summary>Storms alive at `hours` whose centre is within `range` blocks of (x, z).</summary>
        public List<Storm> ActiveWithin(double x, double z, double hours, double range)
        {
            var list = new List<Storm>();
            if (!Enabled) return list;
            foreach (var s in Spawned(x, z, range, hours, hours))
            {
                if (!s.ActiveAt(hours)) continue;
                s.Centre(shape, hours, out double cx, out double cz);
                double dx = shape.DeltaX(x, cx), dz = cz - z;
                if (dx * dx + dz * dz <= range * range) list.Add(s);
            }
            return list;
        }

        /// <summary>Every storm formed before `toHours` that could be alive after `fromHours` and could come within `extra` blocks (plus its own winds) of (x, z).</summary>
        IEnumerable<Storm> Spawned(double x, double z, double extra, double fromHours, double toHours)
        {
            double reach = cfg.StormSpeed * MaxSpeedFactor * MaxLife + cfg.StormRadius * MaxRadiusFactor * OuterFactor + extra;
            long slot0 = (long)Math.Floor((fromHours - MaxLife) / SlotHours), slot1 = (long)Math.Floor(toHours / SlotHours);
            long cz0 = (long)Math.Floor((z - reach) / CellSize), cz1 = (long)Math.Floor((z + reach) / CellSize);

            var cellsX = new List<long>();
            if (shape.Wraps)
            {
                long around = Math.Max(1, (long)Math.Round(shape.Circumference / CellSize));
                double cellW = shape.Circumference / around;
                long ci = (long)Math.Floor((shape.RingX(x) - shape.RingStart) / cellW);
                long n = (long)Math.Ceiling(reach / cellW);
                if (2 * n + 1 >= around) for (long i = 0; i < around; i++) cellsX.Add(i);
                else for (long i = ci - n; i <= ci + n; i++) cellsX.Add(Noise.Wrap(i, around));
            }
            else
            {
                for (long i = (long)Math.Floor((x - reach) / CellSize); i <= (long)Math.Floor((x + reach) / CellSize); i++) cellsX.Add(i);
            }

            for (long sl = slot0; sl <= slot1; sl++)
                for (long czi = cz0; czi <= cz1; czi++)
                    foreach (long cxi in cellsX)
                        if (TrySpawn(cxi, czi, sl, out var s) && s.T0 < toHours && s.T0 + s.Life > fromHours) yield return s;
        }

        static double SegmentDistance(double ax, double az, double bx, double bz)
        {
            double ex = bx - ax, ez = bz - az, len2 = ex * ex + ez * ez;
            double t = len2 > 0 ? Math.Clamp(-(ax * ex + az * ez) / len2, 0, 1) : 0;
            double px = ax + ex * t, pz = az + ez * t;
            return Math.Sqrt(px * px + pz * pz);
        }

        /// <summary>Does a storm form in this cell on this game day, and if so, which.</summary>
        public bool TrySpawn(long cellX, long cellZ, long slot, out Storm storm)
        {
            storm = default;
            ulong h = Noise.Hash(key, cellX, cellZ, slot);
            double z = (cellZ + Noise.Unit(h, 1)) * CellSize;
            double x;
            if (shape.Wraps)
            {
                long around = Math.Max(1, (long)Math.Round(shape.Circumference / CellSize));
                x = shape.RingStart + (cellX + Noise.Unit(h, 2)) * (shape.Circumference / around);
            }
            else x = (cellX + Noise.Unit(h, 2)) * CellSize;
            double t0 = (slot + Noise.Unit(h, 3)) * SlotHours;

            double lat = shape.LatitudeDeg(z), a = Math.Abs(lat), hs = lat >= 0 ? 1 : -1;
            double yr = shape.YearRel(t0);
            double trop = Noise.Band(a, 5, 10, 24, 30), west = Noise.Band(a, 32, 40, 60, 67), polar = Noise.SmoothStep(62, 72, a);
            double tropSeason = 1 + 0.9 * Math.Sin(2 * Math.PI * (yr - 0.38)) * hs;   // late summer
            double winter = 1 - 0.35 * Math.Sin(2 * Math.PI * (yr - 0.30)) * hs;
            double p = (0.01 + 0.09 * trop * tropSeason + 0.17 * west * winter + 0.10 * polar) * cfg.StormFrequency;
            if (Noise.Unit(h, 0) >= p) return false;

            bool tropical = trop > west + polar;
            double life = Noise.Lerp(MinLife, MaxLife, Noise.Unit(h, 4));
            double peak = Noise.Lerp(0.75, 1.2, Noise.Unit(h, 5));
            double radius = cfg.StormRadius * Noise.Lerp(0.75, MaxRadiusFactor, Noise.Unit(h, 6));
            if (tropical) { radius *= 0.75; peak *= 1.12; }

            // tropical storms drift west and toward the pole, the rest are carried east by the westerlies
            double east = tropical ? -0.85 : 1, north = (tropical ? 0.4 : 0.2) * hs;
            double l = Math.Sqrt(east * east + north * north);
            double speed = cfg.StormSpeed * Noise.Lerp(0.75, MaxSpeedFactor, Noise.Unit(h, 7));
            storm = new Storm((int)(h & 0x7FFFFFFF), x, z, t0, life, east / l * speed, -north / l * speed, Math.Min(peak, 1.4), radius, tropical);
            return true;
        }
    }
}
