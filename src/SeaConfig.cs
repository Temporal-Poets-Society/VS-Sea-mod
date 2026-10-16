using ProtoBuf;

namespace Sea
{
    /// <summary>ModConfig/sea.json. The server's copy is sent to every client, so everyone computes the same wind.</summary>
    [ProtoContract]
    public class SeaConfig
    {
        /// <summary>"Earthlike": prevailing winds by latitude, drifting weather, travelling storms and gusts.
        /// "Steady": prevailing winds and gusts only, no storms. "Off": vanilla wind (strength only, no direction); the sea then follows "Steady".</summary>
        [ProtoMember(1)] public string WindModel = "Earthlike";

        /// <summary>Multiplies the prevailing winds (trade winds, westerlies, polar easterlies).</summary>
        [ProtoMember(2)] public float PrevailingStrength = 1;

        /// <summary>Multiplies the day-to-day drift of the weather on top of the prevailing wind.</summary>
        [ProtoMember(3)] public float Variability = 1;

        /// <summary>Multiplies gusts (short changes in strength and direction). 0 = none.</summary>
        [ProtoMember(4)] public float Gusts = 1;

        /// <summary>Multiplies how often storms form. 0 = no storms.</summary>
        [ProtoMember(5)] public float StormFrequency = 1;

        /// <summary>Typical storm size in blocks (gales reach about this far from the centre).</summary>
        [ProtoMember(6)] public float StormRadius = 2000;

        /// <summary>How fast storms travel, in blocks per game hour (350 ≈ 3 blocks/s at the default day length; the carrack does 7).</summary>
        [ProtoMember(7)] public float StormSpeed = 350;

        /// <summary>How far (degrees of latitude) the wind belts move north in northern summer and south in winter.</summary>
        [ProtoMember(8)] public float SeasonShift = 8;

        /// <summary>Multiplies wave heights.</summary>
        [ProtoMember(9)] public float WaveScale = 1;

        public WindMode Mode => WindModel?.ToLowerInvariant() switch
        {
            "off" => WindMode.Off,
            "steady" => WindMode.Steady,
            _ => WindMode.Earthlike
        };
    }

    public enum WindMode { Off, Steady, Earthlike }

    /// <summary>An admin's fixed wind (/sea wind), everywhere, from StartHours until UntilHours (0 = until cleared).</summary>
    [ProtoContract]
    public class WindOverride
    {
        [ProtoMember(1)] public double FromDegrees;
        [ProtoMember(2)] public double Strength;
        [ProtoMember(3)] public double StartHours;
        [ProtoMember(4)] public double UntilHours;

        public bool Covers(double hours) => hours >= StartHours && (UntilHours <= 0 || hours < UntilHours);
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

    /// <summary>Everything a client needs to compute the same wind as the server.</summary>
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
