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
        /// <summary>Faint reflected sky and stars; 0 disables, 1 is the restrained default.</summary>
        [ProtoMember(11, IsRequired = true)] public float SkyReflections = 1;
        /// <summary>Outdoor night visibility floor on the existing sunlight channel; 0 disables.</summary>
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

        public bool Covers(double hours) => hours >= StartHours && (UntilHours <= 0 || hours < UntilHours);

        /// <summary>
        /// (0.3.7) This fixed wind if every number in it is finite, the strength clamped to 0..<see cref="WindModel.MaxStrength"/>;
        /// otherwise null (no fixed wind). A NaN here would reach every side's wave directions and train indices.
        /// </summary>
        public static WindOverride Checked(WindOverride ov)
        {
            if (ov == null || !double.IsFinite(ov.FromDegrees) || !double.IsFinite(ov.Strength) || !double.IsFinite(ov.StartHours) || !double.IsFinite(ov.UntilHours)) return null;
            return new WindOverride { FromDegrees = ov.FromDegrees, Strength = System.Math.Clamp(ov.Strength, 0, WindModel.MaxStrength), StartHours = ov.StartHours, UntilHours = ov.UntilHours };
        }
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
    }
}
