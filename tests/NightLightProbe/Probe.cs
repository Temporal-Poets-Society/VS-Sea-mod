using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NightLightProbe
{
    /// <summary>
    /// Measures night light on a dedicated server with whatever mods are installed beside it (issue #7). `/nightprobe sweep
    /// &lt;file&gt;` writes a CSV over latitude, longitude, season and moon phase: the sun's and the moon's height, the game's
    /// GetDayLightStrength and the time-of-day light a spawn check sees outdoors (sunlight 24 times it). `/nightprobe block`
    /// reads GetLightLevel at the spawn's surface at a new-moon and a full-moon midnight. The calendar is put back after.
    /// </summary>
    public class ProbeSystem : ModSystem
    {
        ICoreServerAPI sapi;
        MethodInfo update, moonPhase;
        System.Func<double, IWorldAccessor, double> longitude;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            api.ChatCommands.Create("nightprobe").RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("sweep").WithArgs(api.ChatCommands.Parsers.Word("file")).HandleWith(a => Sweep((string)a[0])).EndSubCommand()
                .BeginSubCommand("block").HandleWith(_ => Block()).EndSubCommand();
        }

        void Bind()
        {
            var cal = sapi.World.Calendar;
            update ??= AccessTools.Method(cal.GetType(), "Update");
            moonPhase ??= AccessTools.Method(cal.GetType(), "GetMoonPhase", new[] { typeof(double) });
            var mapper = AccessTools.TypeByName("AstraTerra.Astronomy.LatitudeMapper");
            var m = mapper == null ? null : AccessTools.Method(mapper, "MapWorldLongitude", new[] { typeof(double), typeof(IWorldAccessor) });
            longitude = m == null ? null : (x, w) => (double)m.Invoke(null, new object[] { x, w });
        }

        void SetDays(double days)
        {
            var cal = sapi.World.Calendar;
            cal.Add((float)((days - cal.TotalDays) * cal.HoursPerDay));
            update.Invoke(cal, null);
        }

        double Lat(double z) => sapi.World.Calendar.OnGetLatitude(z) * 90;

        double FindZ(double lat)
        {
            double size = sapi.WorldManager.MapSizeZ, best = 0, err = double.MaxValue;
            for (double z = 0; z < size; z += 256) { double e = Math.Abs(Lat(z) - lat); if (e < err) { err = e; best = z; } }
            for (double z = Math.Max(0, best - 256); z < Math.Min(size, best + 256); z += 1) { double e = Math.Abs(Lat(z) - lat); if (e < err) { err = e; best = z; } }
            return best;
        }

        double FindX(double lon)
        {
            double size = sapi.WorldManager.MapSizeX, best = size / 2, err = double.MaxValue;
            if (longitude == null) return best;
            for (double x = 0; x < size; x += 512) { double e = Math.Abs(longitude(x, sapi.World) - lon); if (e < err) { err = e; best = x; } }
            return best;
        }

        float SunY(Vec3d p, double days) => sapi.World.Calendar.GetSunPosition(p, days).Normalize().Y;
        float MoonY(Vec3d p, double days) => sapi.World.Calendar.GetMoonPosition(p, days).Normalize().Y;
        double Phase(double days) => (double)moonPhase.Invoke(sapi.World.Calendar, new object[] { days });

        /// <summary>The time nearest `days` whose sun is lowest at p (local solar midnight).</summary>
        double Midnight(Vec3d p, double days)
        {
            double best = days, low = 2;
            for (double t = days - 0.6; t <= days + 0.6; t += 0.005) { float y = SunY(p, t); if (y < low) { low = y; best = t; } }
            return best;
        }

        (double Full, double New) Moons(double around)
        {
            double span = sapi.World.Calendar.DaysPerYear / 20.0, full = around, nu = around, ef = 9, en = 9;
            for (double t = around - span; t <= around + span; t += 0.02)
            {
                double ph = Phase(t);
                if (Math.Abs(ph - 4) < ef) { ef = Math.Abs(ph - 4); full = t; }
                double n = Math.Min(ph, 8 - ph);
                if (n < en) { en = n; nu = t; }
            }
            return (full, nu);
        }

        static string F(double v) => v.ToString("0.#####", Inv);

        TextCommandResult Sweep(string file)
        {
            Bind();
            var cal = sapi.World.Calendar;
            double start = cal.TotalDays;
            var sb = new StringBuilder("lat,lon,x,z,season,moon,days,hours_from_midnight,sun_y,sun_alt,vanilla_moon_y,vanilla_phase,phase_brightness,local_moon_y,local_phase,sunset_mod,daylight,tod_sunlight\n");
            var sunsetMod = AccessTools.Property(cal.GetType(), "SunsetMod");
            try
            {
                int dpy = cal.DaysPerYear;
                double yearStart = Math.Floor(start / dpy) * dpy;
                var seasons = new (string Name, double YearRel)[] { ("mar", 0.2226), ("jun", 0.4726), ("sep", 0.7226), ("dec", 0.9726) };
                var lats = new[] { 10.0, 45, 60, 63.3, -40 };
                var lons = longitude == null ? new[] { double.NaN } : new[] { 0.0, -169.0 };
                foreach (var (season, rel) in seasons)
                {
                    var (full, nu) = Moons(yearStart + rel * dpy);
                    foreach (var (moon, around) in new[] { ("full", full), ("new", nu) })
                    foreach (double lat in lats)
                    foreach (double lon in lons)
                    {
                        double z = FindZ(lat), x = double.IsNaN(lon) ? sapi.WorldManager.MapSizeX / 2.0 : FindX(lon);
                        var p = new Vec3d(x, sapi.World.SeaLevel, z);
                        double realLon = longitude == null ? double.NaN : longitude(x, sapi.World);
                        double mid = Midnight(p, around);
                        for (int k = -48; k <= 48; k++)
                        {
                            double t = mid + k / 96.0;
                            SetDays(t);
                            float sy = SunY(p, t), vy = MoonY(p, t);
                            double localDays = double.IsNaN(realLon) ? t : t + realLon / 360.0;
                            float ly = MoonY(p, localDays);
                            float dl = cal.GetDayLightStrength(x, z);
                            sb.Append(string.Join(",", F(Lat(z)), F(realLon), F(x), F(z), season, moon, F(t), F(k / 4.0), F(sy),
                                F(Math.Asin(Math.Clamp(sy, -1, 1)) * 180 / Math.PI), F(vy), F(cal.MoonPhaseExact), F(cal.MoonPhaseBrightness),
                                F(ly), F(Phase(localDays)), F(sunsetMod == null ? double.NaN : Convert.ToDouble(sunsetMod.GetValue(cal))),
                                F(dl), ((int)(24 * dl)).ToString(Inv))).Append('\n');
                        }
                    }
                }
            }
            finally { SetDays(start); }
            File.WriteAllText(file, sb.ToString());
            sapi.Logger.Notification("[nightprobe] sweep written to {0}", file);
            return TextCommandResult.Success("[nightprobe] sweep done");
        }

        TextCommandResult Block()
        {
            Bind();
            var spawn = sapi.World.DefaultSpawnPosition.AsBlockPos;
            sapi.WorldManager.LoadChunkColumnPriority(spawn.X / 32, spawn.Z / 32, new ChunkLoadOptions
            {
                OnLoaded = () =>
                {
                    var cal = sapi.World.Calendar;
                    double start = cal.TotalDays;
                    try
                    {
                        var ba = sapi.World.BlockAccessor;
                        var pos = new BlockPos(spawn.X, ba.GetRainMapHeightAt(spawn.X, spawn.Z) + 1, spawn.Z, 0);
                        var p = pos.ToVec3d();
                        var (full, nu) = Moons(start);
                        foreach (var (name, t) in new[] { ("now", start), ("newmoon-midnight", Midnight(p, nu)), ("fullmoon-midnight", Midnight(p, full)) })
                        {
                            SetDays(t);
                            sapi.Logger.Notification("[nightprobe] block {0} lat {1} at {2}: sunY {3} daylight {4} onlySun {5} onlyBlock {6} timeOfDaySun {7} maxTimeOfDay {8}",
                                name, F(Lat(pos.Z)), pos, F(SunY(p, t)), F(cal.GetDayLightStrength(pos.X, pos.Z)),
                                ba.GetLightLevel(pos, EnumLightLevelType.OnlySunLight), ba.GetLightLevel(pos, EnumLightLevelType.OnlyBlockLight),
                                ba.GetLightLevel(pos, EnumLightLevelType.TimeOfDaySunLight), ba.GetLightLevel(pos, EnumLightLevelType.MaxTimeOfDayLight));
                        }
                    }
                    finally { SetDays(start); }
                }
            });
            return TextCommandResult.Success("[nightprobe] block queued");
        }
    }
}
