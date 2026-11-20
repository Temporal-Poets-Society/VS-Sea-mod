using System;
using System.Reflection;
using Vintagestory.API.Common;

namespace Sea
{
    /// <summary>
    /// (SEA-D84, SE-0079) Wind Vanes &amp; Randomized Winds (mod id <c>windvane</c>, Skyforger; it carries a fork of Pelagus
    /// Winds) turns the game's wind only on clients: its server drifts the angle and sends it, but only the client subscribes
    /// <c>OnGetWindSpeed</c>. The server's wind stays vanilla's (east), so whatever sails on the server (Ships' ships) and
    /// what players see disagreed. On the server Sea turns vanilla's vector by the same angle, as Wind Vanes' client
    /// handler does: X = sin(angle)·s, Z = cos(angle)·s, the length kept. Read in Wind Vanes 1.2.8 (decompiled in
    /// <c>devtools/vs-ref/mods/windvane</c>): <c>WindVaneMod.WindVaneModSystem.CurrentWindAngle</c> (public static float,
    /// the server's own angle) and <c>Config.EnableWindRandomizer</c> (public static config). Its fields are read, never
    /// written. A version without them leaves the wind as it is (logged once).
    /// </summary>
    sealed class WindVanesAdapter
    {
        public const string ModId = "windvane";
        const string SystemName = "WindVaneMod.WindVaneModSystem";

        readonly FieldInfo angle, config;
        FieldInfo randomizer;
        readonly ILogger log;

        WindVanesAdapter(FieldInfo angle, FieldInfo config, FieldInfo randomizer, ILogger log)
        {
            this.angle = angle; this.config = config; this.randomizer = randomizer; this.log = log;
        }

        /// <summary>The adapter when Wind Vanes is loaded with the fields it was written against; else null.</summary>
        public static WindVanesAdapter Find(ICoreAPI api, ILogger log)
        {
            if (api?.ModLoader == null || !api.ModLoader.IsModEnabled(ModId)) return null;
            var type = api.ModLoader.GetModSystem(SystemName)?.GetType();
            const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
            var angle = type?.GetField("CurrentWindAngle", Static);
            var config = type?.GetField("Config", Static);
            var randomizer = config?.FieldType.GetField("EnableWindRandomizer", BindingFlags.Public | BindingFlags.Instance);
            if (angle?.FieldType != typeof(float) || randomizer?.FieldType != typeof(bool))
            {
                log?.Warning("Wind Vanes is loaded, but not a version Sea knows ({0} has no CurrentWindAngle or Config.EnableWindRandomizer): ships sailed on the server keep the game's own wind", SystemName);
                return null;
            }
            log?.Notification("Wind Vanes: ships and boats sailed on the server take its wind direction");
            return new WindVanesAdapter(angle, config, randomizer, log);
        }

        /// <summary>Wind Vanes' direction on the game's wind, as its client handler sets it; the wind unchanged while its randomizer is off or on a failure.</summary>
        public Wind Turn(Wind w)
        {
            if (randomizer == null) return w;
            try
            {
                var cfg = config.GetValue(null);
                if (cfg == null || !(bool)randomizer.GetValue(cfg)) return w;
                double a = (float)angle.GetValue(null), s = w.Strength;
                return double.IsFinite(a) ? new Wind(Math.Sin(a) * s, Math.Cos(a) * s) : w;
            }
            catch (Exception e)
            {
                randomizer = null;
                LogOnce.Error(log, "reading Wind Vanes' wind direction", e);
                return w;
            }
        }

        /// <summary>For the self-test: Wind Vanes' angle now (radians), or NaN.</summary>
        public double Angle { get { try { return (float)angle.GetValue(null); } catch (Exception) { return double.NaN; } } }
    }
}
