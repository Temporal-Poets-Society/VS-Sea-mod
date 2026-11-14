using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Sea
{
    /// <summary>(SE-0046) The server: its model, the world key and ring, the clock packets, and the fixed wind with its save.</summary>
    public partial class SeaSystem
    {
        WorldShape MakeShape(double ringStart, double circumference, double daysPerYear, double hoursPerDay)
        {
            var world = api.World;
            return new WorldShape
            {
                RingStart = ringStart,
                Circumference = circumference,
                Latitude = z => world.Calendar.OnGetLatitude(z),
                DaysPerYear = daysPerYear,
                HoursPerDay = hoursPerDay
            };
        }

        // ---------------------------------------------------------------- server

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            serverInstance = this;
            Boats = new NativeBoats(this, api);
            // (SE-0060) stored back only when it was read or missing: a file that doesn't parse (a hand edit's typo) is left as
            // it is, so the player's settings aren't overwritten with defaults
            bool readable = true;
            try { Config = (api.LoadModConfig<SeaConfig>(ConfigFile) ?? new SeaConfig()).Checked(); }
            catch (Exception e)
            {
                readable = false;
                Mod.Logger.Error("ModConfig/{0} could not be read, so Sea runs on its defaults and leaves the file as it is (fix it and restart): {1}", ConfigFile, e.Message);
                Config = new SeaConfig();
            }
            if (readable) api.StoreModConfig(Config, ConfigFile);

            serverChannel = api.Network.GetChannel(ChannelName);
            // Cylinder World sets up its ring at WorldReady
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, BuildServerModel);
            onPlayerJoin = p =>
            {
                if (sentParams != null) serverChannel.SendPacket(sentParams, p);
                SendClock(p);
            };
            api.Event.PlayerJoin += onPlayerJoin;
            Listen(api.Event.RegisterGameTickListener(_ =>
            {
                // the calendar as it is now (its speed changes while players sleep), in force a few seconds from now
                if (Model != null) PublishAnchor(AnchorLead);
                foreach (IServerPlayer p in api.World.AllOnlinePlayers)
                    if (p.ConnectionState == EnumClientState.Playing) SendClock(p);
            }, ClockPacketMs));
            api.Event.GameWorldSave += SaveOverride;
            // the calendar jumped (/time set) or changed speed (sleeping): a new anchor now, sent at once
            Listen(api.Event.RegisterGameTickListener(_ =>
            {
                if (Model == null || !SampleHours.Drifted(sampleHours.Newest, WaveTime, Hours)) return;
                PublishAnchor(AnchorLead);
                foreach (IServerPlayer p in api.World.AllOnlinePlayers)
                    if (p.ConnectionState == EnumClientState.Playing) SendClock(p);
            }, 1000));

            SeaCommands.Register(api, this);
        }

        void BuildServerModel()
        {
            // World configuration is available now; sea.json was stored before these per-world overrides.
            // The effective Config goes to the model and through the existing SeaParamsPacket to every client.
            Config = Config.ForWorld(sapi.World.Config);
            RegionSize = sapi.World.BlockAccessor.RegionSize;
            var cal = sapi.World.Calendar;
            ReadRing(out double ringStart, out double circumference);
            long key = WindKey();
            var shape = MakeShape(ringStart, circumference, cal.DaysPerYear, cal.HoursPerDay);
            var model = new WindModel(shape, Config);

            model.Override = LoadOverride();
            model.History = LoadHistory();
            UseModel(model);
            BuildSpectrum(key, circumference, Config.WaveScale);
            sampleHours.Clear();
            PublishAnchor(0);
            sentParams = new SeaParamsPacket
            {
                Key = key, Config = Config, RingStart = ringStart, Circumference = circumference,
                DaysPerYear = cal.DaysPerYear, HoursPerDay = cal.HoursPerDay, Override = model.Override, History = model.History
            };
            serverChannel.BroadcastPacket(sentParams);

            Mod.Logger.Notification("Sea ready{0}; wind from {1}", circumference > 0 ? $", waves wrapping every {circumference:0} blocks from x={ringStart:0}" : "",
                WindProviderName ?? $"vanilla (no wind provider yet; the sea follows a steady {model.SteadySeaWind})");
        }

        /// <summary>
        /// (0.3.6) The world's wind key (since 0.4.0 it only picks the wave trains' phases): random, made once and kept in the save. It used to be a mix of the seed, which
        /// a client could undo (the mix is invertible) to learn the seed; the key is sent to every client.
        /// </summary>
        long WindKey()
        {
            var save = sapi.WorldManager.SaveGame;
            byte[] data = save.GetData(WindKeyKey);
            if (data != null && data.Length == 8) return BitConverter.ToInt64(data, 0);
            long key = BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8), 0);
            save.StoreData(WindKeyKey, BitConverter.GetBytes(key));
            return key;
        }

        /// <summary>The wave clock as it will read when the packet arrives (half the ping later).</summary>
        void SendClock(IServerPlayer p)
        {
            double t = WaveTime, ping = p.Ping;   // (NaN while the player has no connection)
            serverChannel.SendPacket(new SeaClockPacket
            {
                Seconds = t + (double.IsFinite(ping) ? Math.Clamp(ping, 0, 2) / 2 : 0),
                Anchors = sampleHours.All
            }, p);
        }

        /// <summary>Cylinder World's ring (read by reflection, so Sea doesn't depend on it).</summary>
        void ReadRing(out double ringStart, out double circumference)
        {
            ringStart = circumference = 0;
            try
            {
                var core = sapi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "CylinderWorld.CylinderWorldCore");
                var p = core?.GetType().GetProperty("Params", BindingFlags.Public | BindingFlags.Instance)?.GetValue(core);
                if (p == null) return;
                var t = p.GetType();
                circumference = Convert.ToDouble(t.GetProperty("Circumference")?.GetValue(p) ?? 0.0);
                ringStart = Convert.ToDouble(t.GetField("JumpInset")?.GetValue(p) ?? 0);
                if (circumference <= 0) ringStart = circumference = 0;
            }
            catch (Exception e)
            {
                Mod.Logger.Warning("Cylinder World is installed but its ring couldn't be read ({0}); the wind won't line up at the seam", e.Message);
                ringStart = circumference = 0;
            }
        }

        /// <summary>
        /// Fix the wind everywhere (null = back to the provider's or vanilla's), tell every client, remember it in the
        /// save. It decides Sea's wind and sea (ships, waves); vanilla's weather is the provider's (Earth's) to drive.
        /// It takes over on this side at once, and the sea's past is rewritten: no trace of the fixed winds before it is
        /// kept (for tests and mods that want a known sea now; an admin's change is <see cref="ChangeOverride"/>).
        /// </summary>
        public void SetOverride(WindOverride ov)
        {
            var m = Model;
            if (m == null || sentParams == null) return;
            ov = WindOverride.Checked(ov);
            if (ov != null) ov.ActiveSample = 0;
            Change(() =>
            {
                m.History = Array.Empty<WindOverride>();
                m.Override = ov;
            });
            Publish(m);
        }

        /// <summary>
        /// (0.4.2) Change the fixed wind as an admin does (/sea setwind, /sea clearwind): it takes over at the wave
        /// sample a few seconds ahead (<see cref="AnchorLead"/>), on the server and every client alike, whose game hour
        /// is <see cref="NextActivation"/>; the fixed wind before it ends there, and the sea it raised dies down over the
        /// next game hours rather than vanishing (<see cref="WindOverride.Change"/>). Give the new one's hours from
        /// <see cref="NextActivation"/>.
        /// </summary>
        public void ChangeOverride(WindOverride ov)
        {
            var m = Model;
            if (m == null || sentParams == null) return;
            double t = WaveTime;
            long now = WavePlace.SampleIndex(t), n = now + AnchorLead;
            WindOverride current = m.Override;
            WindOverride[] history = m.History;
            // (ended ones are dropped by the hour of the oldest sample still blended, not the calendar: after a /time set
            // the samples follow it a few seconds later)
            double oldest = Math.Min(Hours, waveSource.HoursOf(now - SampleHours.Behind, t));
            int dropped = WindOverride.Change(ref current, ref history, ov, n, waveSource.HoursOf(n, t), oldest);
            if (dropped > 0) Mod.Logger.Warning("More than {0} fixed-wind changes within 17 game hours: the oldest {1} no longer shape the sea", WindOverride.MaxHistory, dropped);
            Change(() =>
            {
                m.History = history;   // (first: until the new one's sample, the old one ended there reads as before)
                m.Override = current;
            });
            Publish(m);
        }

        /// <summary>
        /// (0.4.2) The game hour of the wave sample a fixed wind changed now takes over at (a few samples ahead, so
        /// every client hears of it first).
        /// </summary>
        public double NextActivation
        {
            get
            {
                double t = WaveTime;
                return waveSource.HoursOf(WavePlace.SampleIndex(t) + AnchorLead, t);
            }
        }

        void Publish(WindModel m)
        {
            sentParams.Override = m.Override;
            sentParams.History = m.History;
            serverChannel.BroadcastPacket(sentParams);
        }

        WindOverride LoadOverride()
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(OverrideKey);
            if (data == null || data.Length == 0) return null;
            try { return SerializerUtil.Deserialize<WindOverride>(data)?.Restarted(); }
            catch (Exception e) { Mod.Logger.Warning("Could not read the saved fixed wind: {0}", e.Message); return null; }
        }

        WindOverride[] LoadHistory()
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(HistoryKey);
            if (data == null || data.Length == 0) return Array.Empty<WindOverride>();
            try { return Checked(SerializerUtil.Deserialize<WindOverrideHistory>(data)?.Items, true); }
            catch (Exception e) { Mod.Logger.Warning("Could not read the saved ended fixed winds: {0}", e.Message); return Array.Empty<WindOverride>(); }
        }

        /// <summary>The usable ones (restarted: from a save), at most <see cref="WindOverride.MaxHistory"/>, the newest.</summary>
        static WindOverride[] Checked(WindOverride[] items, bool restarted)
        {
            if (items == null) return Array.Empty<WindOverride>();
            var ok = items.Select(o => restarted ? o?.Restarted() : WindOverride.Checked(o)).Where(o => o != null).ToArray();
            return ok.Length > WindOverride.MaxHistory ? ok[^WindOverride.MaxHistory..] : ok;
        }

        void SaveOverride()
        {
            var m = Model;
            if (m == null) return;
            var save = sapi.WorldManager.SaveGame;
            var ov = m.Override;
            save.StoreData(OverrideKey, ov == null ? Array.Empty<byte>() : SerializerUtil.Serialize(ov));
            save.StoreData(HistoryKey, SerializerUtil.Serialize(new WindOverrideHistory { Items = m.History }));
        }

        // ---------------------------------------------------------------- client
    }
}
