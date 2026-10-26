using ProtoBuf;

namespace Sea
{
    /// <summary>
    /// ModConfig/sea.json. The server's copy is sent to every client, so everyone computes the same sea. (0.3.7) Every
    /// member is sent even when it's 0: protobuf leaves zeros out, and a client would then keep the default.
    /// (0.4.0) Members 1-8 (WindModel, PrevailingStrength, Variability, Gusts, StormFrequency, StormRadius, StormSpeed,
    /// SeasonShift) served Sea's own weather, which moved to Earth (SEA-D30). Their numbers are retired: never reuse
    /// them. Old sea.json files keep them until the server writes the file back without them.
    /// </summary>
    [ProtoContract]
    public class SeaConfig
    {
        /// <summary>Multiplies wave heights.</summary>
        [ProtoMember(9, IsRequired = true)] public float WaveScale = 1;

        /// <summary>0 restores vanilla highlights, 1 enables Sea's sun/moon trails.</summary>
        [ProtoMember(10, IsRequired = true)] public float CelestialReflections = 1;
        /// <summary>(0.4.4) The sky reflected by Fresnel (weak looking down, the sky's own colour at the horizon), near and on
        /// Wmap's distant sea; 0 disables, 1 is physical. No star reflections since 0.4.4.</summary>
        [ProtoMember(11, IsRequired = true)] public float SkyReflections = 1;
        /// <summary>Outdoor night visibility floor on the existing sunlight channel; 0 disables it and the real twilight. (0.4.7-night)
        /// 1 = clear moonless 0.10, high full moon 0.40 (0.4.4: 0.15 / 0.45, now 1.5).</summary>
        [ProtoMember(12, IsRequired = true)] public float NightVisibility = 1;

        /// <summary>
        /// (0.4.0) Without a wind provider (no Earth): the compass bearing the steady wind the sea follows blows from.
        /// Vanilla's wind has no direction and isn't the same on every side, so the sea can't follow it.
        /// </summary>
        [ProtoMember(13, IsRequired = true)] public float SeaWindFrom = 270;
        /// <summary>(0.4.0) Without a wind provider: the strength of that steady wind (0.3 medium breeze; 0 = a calm sea).</summary>
        [ProtoMember(14, IsRequired = true)] public float SeaWindStrength = 0.3f;

        /// <summary>
        /// (0.3.7) Numbers a hand-edited sea.json can't use (negative, NaN, infinite) back to their defaults.
        /// </summary>
        public SeaConfig Checked()
        {
            var d = new SeaConfig();
            static float Ok(float v, float fallback) => float.IsFinite(v) && v >= 0 ? v : fallback;
            WaveScale = Ok(WaveScale, d.WaveScale);
            CelestialReflections = System.Math.Clamp(Ok(CelestialReflections, d.CelestialReflections), 0, 2);
            SkyReflections = System.Math.Clamp(Ok(SkyReflections, d.SkyReflections), 0, 2);
            NightVisibility = System.Math.Clamp(Ok(NightVisibility, d.NightVisibility), 0, 2);
            SeaWindFrom = float.IsFinite(SeaWindFrom) ? (float)WorldShape.Mod(SeaWindFrom, 360) : d.SeaWindFrom;
            SeaWindStrength = (float)System.Math.Clamp(Ok(SeaWindStrength, d.SeaWindStrength), 0, WindModel.MaxStrength);
            return this;
        }
    }

    /// <summary>(0.4.0) Who decides the wind: a provider mod (<see cref="SeaSystem.SetWindProvider"/>) or nobody (vanilla's wind).</summary>
    public enum WindMode { Off, Provider }

    /// <summary>An admin's fixed wind (/sea setwind), everywhere, from StartHours until UntilHours (0 = until cleared).</summary>
    [ProtoContract]
    public class WindOverride
    {
        [ProtoMember(1)] public double FromDegrees;
        [ProtoMember(2)] public double Strength;
        [ProtoMember(3)] public double StartHours;
        [ProtoMember(4)] public double UntilHours;
        /// <summary>
        /// (0.4.2) The wave sample from which it's in force when the waves are worked out (0 = always). Every side
        /// samples the waves at the same samples, so a fixed wind set now takes over at the same one on the server and
        /// every client (they hear of it a moment later than the server makes it), whatever the calendar does.
        /// </summary>
        [ProtoMember(5)] public long ActiveSample;
        /// <summary>
        /// (0.4.2) Replaced (an admin's setwind over it, or clearwind): from wave sample <see cref="EndSample"/> on it only
        /// counts for game hours before <see cref="EndHours"/> (that sample's hour), so the sea it raised dies down
        /// instead of vanishing; before that sample it counts as it did.
        /// </summary>
        [ProtoMember(6)] public bool Ended;
        [ProtoMember(7)] public long EndSample;
        [ProtoMember(8)] public double EndHours;

        public bool Covers(double hours) => hours >= StartHours && (UntilHours <= 0 || hours < UntilHours);

        /// <summary>
        /// (0.4.2) Does it decide the wind at game hour `hours`, as worked out for wave sample `sample`
        /// (<see cref="long.MaxValue"/>: as known now, for anything that isn't a wave sample)?
        /// </summary>
        public bool AppliesAt(double hours, long sample) =>
            sample >= ActiveSample && Covers(hours) && (!Ended || sample < EndSample || hours < EndHours);

        /// <summary>
        /// (0.3.7) This fixed wind if every number in it is finite, the strength clamped to 0..<see cref="WindModel.MaxStrength"/>;
        /// otherwise null (no fixed wind). A NaN here would reach every side's wave directions and train indices.
        /// (0.4.2) The bearing is wrapped to 0..360 (a huge finite one overflowed to infinity in radians).
        /// </summary>
        public static WindOverride Checked(WindOverride ov)
        {
            if (ov == null || !double.IsFinite(ov.FromDegrees) || !double.IsFinite(ov.Strength) || !double.IsFinite(ov.StartHours) || !double.IsFinite(ov.UntilHours)
                || ov.Ended && !double.IsFinite(ov.EndHours)) return null;
            double from = ov.FromDegrees % 360;   // (exact for any finite number, unlike a - m·floor(a/m))
            if (from < 0) from += 360;
            return new WindOverride
            {
                FromDegrees = from, Strength = System.Math.Clamp(ov.Strength, 0, WindModel.MaxStrength), StartHours = ov.StartHours, UntilHours = ov.UntilHours,
                ActiveSample = ov.ActiveSample, Ended = ov.Ended, EndSample = ov.EndSample, EndHours = ov.EndHours
            };
        }

        /// <summary>
        /// (0.4.2) As loaded from a save: wave samples count from the server's start, so after a restart it's in force
        /// from the first, and one that ended has ended for good.
        /// </summary>
        public WindOverride Restarted()
        {
            var ov = Checked(this);
            if (ov == null) return null;
            ov.ActiveSample = 0;
            if (ov.Ended) ov.EndSample = long.MinValue;
            return ov;
        }

        /// <summary>
        /// The most ended fixed winds kept (the newest). Only a flood of changes within 17 game hours reaches it (each
        /// is dropped 17 hours after it ended); beyond it the oldest go, and Sea logs it.
        /// </summary>
        public const int MaxHistory = 64;

        /// <summary>
        /// (0.4.2) An admin changing the fixed wind (/sea setwind, /sea clearwind): `next` (or null) takes over at wave
        /// sample `activeSample`, whose game hour is `activeHours`; what was fixed before ends there and joins
        /// <paramref name="history"/> (the ended ones, oldest first), so the sea it raised dies down over the next hours
        /// instead of vanishing, and before that sample everything reads as it did. Ended ones no sample can need any
        /// more (over 16 game hours before `hoursNow`: give the hour of the oldest sample still being worked out, not
        /// the calendar, which an admin's /time set moves ahead of the samples for a few seconds) are dropped.
        /// Returns how many still-needed ones were dropped for room (<see cref="MaxHistory"/>).
        /// </summary>
        public static int Change(ref WindOverride current, ref WindOverride[] history, WindOverride next, long activeSample, double activeHours, double hoursNow)
        {
            next = Checked(next);
            if (next != null) next.ActiveSample = activeSample;
            var kept = new System.Collections.Generic.List<WindOverride>();
            foreach (var h in history ?? System.Array.Empty<WindOverride>())
                if (h != null && !(h.Ended && h.EndHours < hoursNow - WindModel.HistoryHours - 1)) kept.Add(h);
            var old = Checked(current);
            if (old != null)
            {
                old.Ended = true;
                old.EndSample = activeSample;
                old.EndHours = activeHours;
                kept.Add(old);
            }
            int overflow = System.Math.Max(0, kept.Count - MaxHistory);
            if (overflow > 0) kept.RemoveRange(0, overflow);
            history = kept.ToArray();
            current = next;
            return overflow;
        }
    }

    /// <summary>(0.4.2) The ended fixed winds, as saved with the world.</summary>
    [ProtoContract]
    public class WindOverrideHistory
    {
        [ProtoMember(1)] public WindOverride[] Items;
    }

    /// <summary>The server's wave clock (seconds), sent to each client every few seconds so everyone draws the same waves.</summary>
    [ProtoContract]
    public class SeaClockPacket
    {
        [ProtoMember(1)] public double Seconds;
        /// <summary>
        /// (0.3.6) The game hours of the wave samples: every anchor the server keeps, in the order it made them (the one in
        /// force a second ago too, which a client joining now needs for its first blend; see <see cref="SampleHours"/>).
        /// </summary>
        [ProtoMember(2)] public HoursAnchor[] Anchors;
    }

    /// <summary>
    /// (0.3.6) From wave sample <see cref="FromSample"/> on, sample n is at game hour
    /// <see cref="Hours"/> + (n - FromSample) · <see cref="HoursPerSample"/>. Made by the server from its calendar.
    /// </summary>
    [ProtoContract]
    public class HoursAnchor
    {
        [ProtoMember(1)] public long FromSample;
        [ProtoMember(2)] public double Hours;
        [ProtoMember(3)] public double HoursPerSample;
        /// <summary>Its number: the server counts them up, and the later one wins where both are in force.</summary>
        [ProtoMember(4)] public long Seq;

        public double At(long sample) => Hours + (sample - FromSample) * HoursPerSample;
    }

    /// <summary>Everything a client needs to compute the same sea as the server.</summary>
    [ProtoContract]
    public class SeaParamsPacket
    {
        [ProtoMember(1)] public long Key;
        [ProtoMember(2)] public SeaConfig Config;
        [ProtoMember(3)] public double RingStart;
        [ProtoMember(4)] public double Circumference;
        [ProtoMember(5)] public double DaysPerYear;
        [ProtoMember(6)] public double HoursPerDay;
        [ProtoMember(7)] public WindOverride Override;
        /// <summary>(0.4.2) The fixed winds before it, ended (the sea they raised still dying down), oldest first.</summary>
        [ProtoMember(8)] public WindOverride[] History;
    }
}
