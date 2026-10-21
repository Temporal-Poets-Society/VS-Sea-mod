using System;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>
    /// The sea's waves as a fixed library of travelling sine waves ("trains") that every side knows: each train has
    /// its own direction, wavelength and starting phase, made from the world key. The sea state and the wind only set
    /// how strong each train is, so when they change the waves grow and fade but never jump, and the surface at a
    /// place and time is the same on the server and every client (see <see cref="WavePlace"/> and <see cref="WaveClock"/>).
    ///
    ///  - Swell: 12 wavelengths, half an octave apart (4 … 181 blocks), in 18 directions 20° apart. A sea state lights
    ///    up the wavelengths from about 1.5× its peak wavelength down to a seventh of it (shorter ones smaller, same
    ///    steepness), each split between the two directions next to where it runs, shorter ones spread up to ±40°.
    ///  - Chop: 6.5 and 4.3 blocks, from the wind where you are.
    ///  - Deep-water speeds: long waves run fast, short ones slow.
    ///  - On a Cylinder World ring every train fits a whole number of waves around it, so the sea is seamless.
    /// </summary>
    public sealed class WaveSpectrum
    {
        public const int DirBins = 18;
        public const int SwellBins = 12;
        public static readonly double[] ChopLengths = { 6.5, 4.3 };
        const double DirStep = 2 * Math.PI / DirBins;
        const double G = 9.81;   // blocks/s² (1 block = 1 m)

        public readonly long Key;
        public readonly double Circumference;
        /// <summary>(0.3.7) The config's WaveScale for the chop (the swell gets it through the sea state's height).</summary>
        public readonly double ChopScale;
        public int Bins => SwellBins + ChopLengths.Length;
        public int TrainCount => Bins * DirBins;

        /// <summary>Per train: wave vector (radians per block), angular speed (radians per second), starting phase.</summary>
        public readonly double[] Kx, Kz, Omega, Phase;
        /// <summary>Per bin: how far its direction grid is turned (in steps), so bins don't all line up at once.</summary>
        readonly double[] grid;

        public WaveSpectrum(long key, double circumference, double chopScale = 1)
        {
            Key = key;
            Circumference = circumference;
            ChopScale = double.IsFinite(chopScale) ? Math.Max(0, chopScale) : 1;
            int n = TrainCount;
            Kx = new double[n]; Kz = new double[n]; Omega = new double[n]; Phase = new double[n];
            grid = new double[Bins];
            for (int b = 0; b < Bins; b++)
            {
                grid[b] = WorldShape.Mod(0.37 + b * 0.6180339887, 1);
                double k = 2 * Math.PI / Length(b);
                for (int d = 0; d < DirBins; d++)
                {
                    int id = b * DirBins + d;
                    double dir = (d + grid[b]) * DirStep;
                    double kx = Math.Cos(dir) * k, kz = Math.Sin(dir) * k;
                    // a whole number of waves around the ring: no seam
                    if (circumference > 0) kx = Math.Round(kx * circumference / (2 * Math.PI)) * 2 * Math.PI / circumference;
                    Kx[id] = kx;
                    Kz[id] = kz;
                    Omega[id] = Math.Sqrt(G * Math.Sqrt(kx * kx + kz * kz));
                    Phase[id] = Noise.Unit(Noise.Hash(key, 0x5EA, b, d)) * 2 * Math.PI;
                }
            }
        }

        /// <summary>Wavelength of a bin: swell 4·2^(b/2) blocks, then the chop.</summary>
        public static double Length(int bin) => bin < SwellBins ? 4 * Math.Pow(2, bin * 0.5) : ChopLengths[bin - SwellBins];

        /// <summary>Relative amplitude of a swell wavelength u octaves from the peak (u &gt; 0 longer).</summary>
        public static double SwellShape(double u)
        {
            if (u >= 0.6 || u <= -2.9) return 0;
            if (u >= 0)
            {
                double c = Math.Cos(Math.PI / 2 * u / 0.6);
                return c * c;
            }
            double s = Math.Pow(2, u);   // shorter waves, same steepness
            if (u < -2.4)
            {
                double c = Math.Cos(Math.PI / 2 * (-2.4 - u) / 0.5);
                s *= c * c;
            }
            return s;
        }

        /// <summary>A fixed side (-1..1) each swell bin is spread to, the shorter the wave the further.</summary>
        static double SpreadSide(int bin) => Math.Sin(bin * 2.39996 + 0.5);

        /// <summary>
        /// How strong every train is for a sea state and the wind (dense, one entry per train, mostly zero). The swell's
        /// significant height (4σ) is the sea state's height.
        /// </summary>
        public void Amplitudes(in SeaState sea, in Wind wind, double[] a)
        {
            Array.Clear(a, 0, a.Length);
            double lp = Math.Clamp(sea.Wavelength, 10, 150);
            double theta0 = Math.Atan2(sea.DirZ, sea.DirX);
            Span<double> w = stackalloc double[SwellBins];
            double sum = 0;
            for (int m = 0; m < SwellBins; m++)
            {
                w[m] = SwellShape(Math.Log2(Length(m) / lp));
                sum += w[m] * w[m];
            }
            if (sum > 0 && sea.Height > 0)
            {
                // significant height = 4σ, σ² = Σ a²/2  →  a_m = w_m · H / sqrt(8 Σ w²)
                double scale = sea.Height / Math.Sqrt(8 * sum);
                for (int m = 0; m < SwellBins; m++)
                {
                    if (w[m] <= 0) continue;
                    double len = Length(m), u = Math.Log2(len / lp);
                    double spread = 40 * Math.PI / 180 * Math.Clamp(-u / 2.4, 0, 1) * SpreadSide(m);
                    Split(m, theta0 + spread, Math.Min(w[m] * scale, 0.05 * len), a);
                }
            }
            // chop from the wind right here
            double ws = wind.Strength, chop = (0.03 + 0.12 * Math.Min(1.5, ws)) * ChopScale;
            double tc = ws > 1e-6 ? Math.Atan2(wind.Z, wind.X) : theta0;
            Split(SwellBins, tc, chop, a);
            Split(SwellBins + 1, tc - 35 * Math.PI / 180, chop * 0.6, a);
        }

        /// <summary>A wave running toward theta, shared between the bin's two nearest directions (keeping its energy).</summary>
        void Split(int bin, double theta, double amp, double[] a)
        {
            double p = theta / DirStep - grid[bin];
            double d0 = Math.Floor(p), f = p - d0;
            int i0 = (int)WorldShape.Mod(d0, DirBins), i1 = (i0 + 1) % DirBins;
            a[bin * DirBins + i0] += amp * Math.Cos(f * Math.PI / 2);
            a[bin * DirBins + i1] += amp * Math.Sin(f * Math.PI / 2);
        }
    }

    /// <summary>
    /// The waves at one place (the camera, a ship): over each second of <see cref="WaveClock"/> time the trains'
    /// amplitudes blend from the sea state and wind a second before its start to those at its start. Every
    /// side that looks at the same place at the same wave time gets the same surface, however long it has been
    /// looking there and wherever it was before (<see cref="SeaSystem.SampleWaves"/> uses <see cref="Sample"/>: both
    /// ends each second, on a grid around the place).
    /// </summary>
    public sealed class WavePlace
    {
        public const double SampleSeconds = 1;
        public const int MaxWaves = 24;

        WaveSpectrum spectrum;
        double[] prev, next, blended;
        int[] order;
        long sample = long.MinValue;

        /// <summary>Wave clock time of the last update.</summary>
        public double Time { get; private set; }
        /// <summary>The trains running here now: wave vector, angular speed, starting phase, blended amplitude.</summary>
        public int Count { get; private set; }
        public readonly double[] Kx = new double[MaxWaves], Kz = new double[MaxWaves], Omega = new double[MaxWaves],
            Phase = new double[MaxWaves], Amp = new double[MaxWaves];
        /// <summary>The sea state and wind of the newest sample.</summary>
        public SeaState Sea { get; private set; }
        public Wind Wind { get; private set; }
        public WaveSpectrum Spectrum => spectrum;

        /// <summary>Forget what was sampled (the place moved far, or it's reused for another). Keeps its buffers.</summary>
        public void Reset()
        {
            sample = long.MinValue;
            gridSample = long.MinValue;
            Count = 0;
        }

        /// <summary>Does <see cref="Update"/> at wave time t need a fresh sea state and wind (compute them only then)?</summary>
        public bool Due(WaveSpectrum spec, double t) => spec != spectrum || SampleIndex(t) != sample;

        /// <summary>The sample a wave time falls in: the amplitudes blend from sample n - 1's to sample n's over it.</summary>
        public static long SampleIndex(double t) => (long)Math.Floor(t / SampleSeconds);

        /// <summary>
        /// Bring the place to wave time t. sea and wind (what's there now) are only read when <see cref="Due"/>. The
        /// blend starts from this place's own previous sample, so two places only agree if they sampled alike before:
        /// use the overload with both ends (as <see cref="SeaSystem.SampleWaves"/> does) for a surface that doesn't
        /// depend on when, or since when, a place was updated.
        /// </summary>
        public void Update(WaveSpectrum spec, double t, in SeaState sea, in Wind wind) => Advance(spec, t, sea, wind, false, default, default);

        /// <summary>
        /// Bring the place to wave time t, blending over sample n (n = <see cref="SampleIndex"/>(t)) from (prevSea,
        /// prevWind), the state at the start of sample n - 1, to (sea, wind), the state at the start of sample n. Both are only read when <see cref="Due"/>, and
        /// nothing of the place's history is used, so every place given the same ends has the same surface.
        /// </summary>
        public void Update(WaveSpectrum spec, double t, in SeaState prevSea, in Wind prevWind, in SeaState sea, in Wind wind) =>
            Advance(spec, t, sea, wind, true, prevSea, prevWind);

        /// <summary>Use this library of trains (new buffers; nothing sampled yet).</summary>
        void Attach(WaveSpectrum spec)
        {
            spectrum = spec;
            int n = spec.TrainCount;
            prev = new double[n];
            next = new double[n];
            blended = new double[n];
            order = new int[n];
            gridPrev = new double[4][];
            gridNext = new double[4][];
            for (int c = 0; c < 4; c++) { gridPrev[c] = new double[n]; gridNext[c] = new double[n]; }
            sample = long.MinValue;
            gridSample = long.MinValue;
        }

        void Advance(WaveSpectrum spec, double t, in SeaState sea, in Wind wind, bool bothEnds, in SeaState prevSea, in Wind prevWind)
        {
            Time = t;
            if (spec == null) { Count = 0; return; }
            long n = SampleIndex(t);
            if (spec != spectrum) Attach(spec);
            gridSample = long.MinValue;   // (the grid's buffers are left as they are; Sample starts them over)
            if (n != sample)
            {
                if (bothEnds)
                {
                    spec.Amplitudes(prevSea, prevWind, prev);
                    spec.Amplitudes(sea, wind, next);
                }
                else
                {
                    // only the previous sample itself is a start to blend from; after a gap, hold the new one
                    bool follows = sample != long.MinValue && n == sample + 1;
                    (prev, next) = (next, prev);
                    spec.Amplitudes(sea, wind, next);
                    if (!follows) Array.Copy(next, prev, next.Length);
                }
                sample = n;
                Sea = sea;
                Wind = wind;
            }
            double f = Math.Clamp(t / SampleSeconds - n, 0, 1);
            int c = 0;
            for (int i = 0; i < next.Length; i++)
            {
                double a = prev[i] + (next[i] - prev[i]) * f;
                if (a <= 1e-5) continue;
                blended[i] = a;
                order[c++] = i;
            }
            Keep(spec, c);
        }

        // ---------------------------------------------------------------- (0.4.2) sampled on a grid

        /// <summary>
        /// (0.4.2) Blocks between the points the sea state is sampled at by <see cref="Sample"/> (on a Cylinder World ring,
        /// the nearest spacing that fits it a whole number of times).
        /// </summary>
        public const double GridCell = 64;

        // the trains' amplitudes at the four corners of the cell, at both ends of the sample: 0 (x0, z0), 1 (x1, z0), 2 (x0, z1), 3 (x1, z1)
        double[][] gridPrev, gridNext;
        long gridSample = long.MinValue, gridCx, gridCz, gridRevision;
        double gridHours = double.NaN;

        /// <summary>
        /// (0.4.2) Bring the place at (x, z) to wave time t: the sea state and wind are sampled at the corners of the
        /// <see cref="GridCell"/> cell it's in (for both ends of the second, at the game hours <paramref name="source"/>
        /// gives every side), and the trains' amplitudes are blended between the corners by where it is. So the surface
        /// at a place and time is the same for everyone, however (and from wherever) they got there; a moving ship's waves
        /// change smoothly. Only when the second or the cell changes is <paramref name="source"/> asked, and a second's
        /// start is the last one's end when nothing changed in between (<see cref="IWaveSource.Revision"/>).
        /// </summary>
        public void Sample(WaveSpectrum spec, double t, double x, double z, double ringStart, IWaveSource source)
        {
            Time = t;
            if (spec == null || source == null || !double.IsFinite(t) || !double.IsFinite(x) || !double.IsFinite(z)) { Count = 0; return; }
            if (spec != spectrum) Attach(spec);
            sample = long.MinValue;   // (the one-place Update starts over after this)
            double cell = GridCell, gx = x;
            if (spec.Circumference > 0)
            {
                cell = spec.Circumference / Math.Max(1, Math.Round(spec.Circumference / GridCell));
                gx = WorldShape.Mod(x - ringStart, spec.Circumference);
            }
            else gx = x;
            double fx = Math.Floor(gx / cell), fz = Math.Floor(z / cell);
            long cx = (long)fx, cz = (long)fz, n = SampleIndex(t);
            double u = Math.Clamp(gx / cell - fx, 0, 1), v = Math.Clamp(z / cell - fz, 0, 1);
            // (a new second or cell, or the sea's past or the samples' hours may read differently now: even mid-second,
            // so a place sampled before the change and one new to it agree)
            long revision = source.Revision;
            if (n != gridSample || cx != gridCx || cz != gridCz || revision != gridRevision)
            {
                double h0 = source.HoursOf(n - 1, t), h1 = source.HoursOf(n, t);
                // the second's start is the last second's end: the same cell, nothing changed since, at the same hour
                bool kept = gridSample == n - 1 && gridCx == cx && gridCz == cz && gridRevision == revision && gridHours == h0;
                gridSample = long.MinValue;
                if (kept) (gridPrev, gridNext) = (gridNext, gridPrev);
                int nearest = (u >= 0.5 ? 1 : 0) | (v >= 0.5 ? 2 : 0);
                double x0 = spec.Circumference > 0 ? ringStart : 0;
                for (int c = 0; c < 4; c++)
                {
                    double px = x0 + (fx + (c & 1)) * cell, pz = (fz + (c >> 1)) * cell;
                    if (!kept) spec.Amplitudes(source.SeaAt(px, pz, h0, n - 1), source.WindAt(px, pz, h0, n - 1), gridPrev[c]);
                    var sea = source.SeaAt(px, pz, h1, n);
                    var wind = source.WindAt(px, pz, h1, n);
                    spec.Amplitudes(sea, wind, gridNext[c]);
                    if (c == nearest) { Sea = sea; Wind = wind; }
                }
                gridCx = cx;
                gridCz = cz;
                gridRevision = revision;
                gridHours = h1;
                // (something changed while this was worked out, from another thread: work it out again next time)
                gridSample = source.Revision == revision ? n : long.MinValue;
            }
            double f = Math.Clamp(t / SampleSeconds - n, 0, 1);
            double w0 = (1 - u) * (1 - v), w1 = u * (1 - v), w2 = (1 - u) * v, w3 = u * v;
            double[] p0 = gridPrev[0], p1 = gridPrev[1], p2 = gridPrev[2], p3 = gridPrev[3];
            double[] n0 = gridNext[0], n1 = gridNext[1], n2 = gridNext[2], n3 = gridNext[3];
            int count = 0;
            for (int i = 0; i < n0.Length; i++)
            {
                double a0 = w0 * p0[i] + w1 * p1[i] + w2 * p2[i] + w3 * p3[i];
                double a1 = w0 * n0[i] + w1 * n1[i] + w2 * n2[i] + w3 * n3[i];
                double a = a0 + (a1 - a0) * f;
                if (a <= 1e-5) continue;
                blended[i] = a;
                order[count++] = i;
            }
            Keep(spec, count);
        }

        /// <summary>The trains blended[order[0..c]] go to the shader's list (the strongest, when there are too many).</summary>
        void Keep(WaveSpectrum spec, int c)
        {
            // more trains than the shader takes (only while the sea turns): keep the strongest, each less the strongest
            // one left out, so a train joins or leaves them at nothing as their order changes (0.3.7; cutting it off
            // made the surface jump by a few centimetres)
            double cut = 0;
            if (c > MaxWaves)
            {
                strongest ??= new Strongest();
                strongest.Amp = blended;
                Array.Sort(order, 0, c, strongest);
                cut = blended[order[MaxWaves]];
                c = MaxWaves;
            }
            for (int j = 0; j < c; j++)
            {
                int id = order[j];
                Kx[j] = spec.Kx[id];
                Kz[j] = spec.Kz[id];
                Omega[j] = spec.Omega[id];
                Phase[j] = spec.Phase[id];
                Amp[j] = blended[id] - cut;
            }
            Count = c;
        }

        /// <summary>Strongest first; ties by train, so every side keeps the same ones.</summary>
        sealed class Strongest : IComparer<int>
        {
            public double[] Amp;
            public int Compare(int p, int q) { int c = Amp[q].CompareTo(Amp[p]); return c != 0 ? c : p.CompareTo(q); }
        }

        Strongest strongest;

        /// <summary>
        /// Run exactly these trains at wave time t, with deep-water speeds (for tests, or a mod making its own sea).
        /// The next <see cref="Update"/> goes back to the sea state.
        /// </summary>
        public void SetTrains(double t, int count, double[] kx, double[] kz, double[] amp, double[] phase)
        {
            Time = t;
            spectrum = null;
            Count = Math.Min(count, MaxWaves);
            for (int i = 0; i < Count; i++)
            {
                Kx[i] = kx[i];
                Kz[i] = kz[i];
                Omega[i] = Math.Sqrt(9.81 * Math.Sqrt(kx[i] * kx[i] + kz[i] * kz[i]));
                Phase[i] = phase[i];
                Amp[i] = amp[i];
            }
        }

        /// <summary>Height of the open-sea surface above its rest level at a world position, at the last update's time.</summary>
        public double HeightAt(double x, double z) => HeightAt(x, z, Time);

        public double HeightAt(double x, double z, double t)
        {
            double y = 0;
            for (int i = 0; i < Count; i++) y += Amp[i] * Math.Sin(Kx[i] * x + Kz[i] * z - Omega[i] * t + Phase[i]);
            return y;
        }

        /// <summary>Significant height (4σ) of what's running here now.</summary>
        public double SignificantHeight
        {
            get
            {
                double s = 0;
                for (int i = 0; i < Count; i++) s += Amp[i] * Amp[i];
                return 4 * Math.Sqrt(s / 2);
            }
        }

        /// <summary>
        /// The shader's seaWave[] uniform for a camera at (camX, camZ): per train (kx, kz, phase at the camera,
        /// amplitude). Phases are worked out here in doubles, so the shader only sees small numbers.
        /// </summary>
        public int FillUniforms(float[] u, double camX, double camZ)
        {
            int n = Math.Min(Count, u.Length / 4);
            for (int i = 0; i < n; i++)
            {
                u[i * 4] = (float)Kx[i];
                u[i * 4 + 1] = (float)Kz[i];
                u[i * 4 + 2] = (float)WorldShape.Mod(Kx[i] * camX + Kz[i] * camZ - Omega[i] * Time + Phase[i], 2 * Math.PI);
                u[i * 4 + 3] = (float)Amp[i];
            }
            return n;
        }

        /// <summary>The height the shader computes from those uniforms, for a point (rx, rz) relative to the camera.</summary>
        public static double ShaderHeight(float[] u, int n, double rx, double rz)
        {
            double y = 0;
            for (int i = 0; i < n; i++) y += u[i * 4 + 3] * Math.Sin(u[i * 4] * rx + u[i * 4 + 1] * rz + u[i * 4 + 2]);
            return y;
        }
    }

    /// <summary>(0.4.2) What <see cref="WavePlace.Sample"/> asks for: the same answers on every side (<see cref="SeaSystem"/>'s).</summary>
    public interface IWaveSource
    {
        /// <summary>The game hour of wave sample n (wave time t is now: for a guess until the server's anchors arrive).</summary>
        double HoursOf(long n, double t);
        /// <summary>The sea state at game hour `hours`, as worked out for wave sample n (an admin's change of the fixed wind counts from its sample).</summary>
        SeaState SeaAt(double x, double z, double hours, long n);
        /// <summary>The wind the chop follows, likewise.</summary>
        Wind WindAt(double x, double z, double hours, long n);
        /// <summary>
        /// Changes whenever the answers may have changed (a new wind provider, a fixed wind, a new model, the samples'
        /// hours): places sampled before then sample again.
        /// </summary>
        long Revision { get; }
    }

    /// <summary>
    /// (0.3.6) The game hour of each wave sample (whole seconds of <see cref="WaveClock"/> time), the same for every
    /// caller and on every side. The sea state and wind are functions of game hours, but the calendar only moves in steps
    /// (every 200 ms on the server, faster while players sleep) and each client's is a little off, so "now" can't be
    /// used. The server instead publishes anchors: from a sample a few seconds ahead, hours advance at the calendar's
    /// current rate. Every side picks, for each sample, the latest anchor in force there. Until a client hears of one,
    /// it makes its own from its calendar, and drops it for the server's.
    /// </summary>
    public sealed class SampleHours
    {
        /// <summary>At most this many are kept, whatever else (they're pruned by use long before).</summary>
        public const int Cap = 64;
        /// <summary>Anchors no blend from this many samples back on can use are dropped.</summary>
        public const int Behind = 2;
        readonly object sync = new();
        // in the order the server made them (Seq); local: one made here before any from the server
        HoursAnchor[] anchors = Array.Empty<HoursAnchor>();
        bool local;
        long droppedBelow = long.MinValue;
        long version;

        /// <summary>(0.4.2) Counts up whenever the anchors change (a sample's hour may read differently from then on).</summary>
        public long Version { get { lock (sync) return version; } }

        /// <summary>Has an anchor from the server (or, on the server, its own).</summary>
        public bool Anchored { get { lock (sync) return anchors.Length > 0 && !local; } }

        /// <summary>
        /// Take the server's anchor (made there, or received), which replaces one made locally. now: this side's
        /// current sample; anchors that no blend from <see cref="Behind"/> samples back on can use are dropped. One
        /// already had, or older than one dropped, is ignored, so receiving the server's whole list again changes nothing.
        /// </summary>
        public void Add(HoursAnchor a, long now)
        {
            if (a == null || !(a.HoursPerSample >= 0) || !double.IsFinite(a.HoursPerSample) || !double.IsFinite(a.Hours)) return;
            lock (sync)
            {
                if (local) { anchors = Array.Empty<HoursAnchor>(); local = false; version++; }
                if (a.Seq < droppedBelow) return;
                foreach (var b in anchors) if (b.Seq == a.Seq) return;
                var list = new List<HoursAnchor>(anchors) { a };
                list.Sort((p, q) => p.Seq.CompareTo(q.Seq));
                // the one in force Behind samples back: nothing made before it is used from there on
                int keepFrom = 0;
                for (int i = 0; i < list.Count; i++) if (list[i].FromSample <= now - Behind) keepFrom = i;
                keepFrom = Math.Max(keepFrom, list.Count - Cap);
                if (keepFrom > 0)
                {
                    droppedBelow = Math.Max(droppedBelow, list[keepFrom].Seq);
                    list.RemoveRange(0, keepFrom);
                }
                anchors = list.ToArray();
                version++;
            }
        }

        /// <summary>
        /// The server's anchor at wave time t, number seq (counting up): from `ahead` samples on, hours run on from where
        /// the calendar reads hoursNow, at its present speed.
        /// </summary>
        public static HoursAnchor Make(double t, double hoursNow, double hoursPerSecond, int ahead, long seq)
        {
            long n = WavePlace.SampleIndex(t) + ahead;
            double rate = Math.Max(0, hoursPerSecond);
            return new HoursAnchor { Seq = seq, FromSample = n, Hours = hoursNow + (n * WavePlace.SampleSeconds - t) * rate, HoursPerSample = rate * WavePlace.SampleSeconds };
        }

        /// <summary>How far (game hours) the calendar may run from the newest anchor before the server makes another.</summary>
        public const double MaxDrift = 0.05;

        /// <summary>
        /// Server: has the calendar left the newest anchor's line (an admin's /time set, players starting or stopping to
        /// sleep)? Then a new anchor is due now rather than at the next clock packet.
        /// </summary>
        public static bool Drifted(HoursAnchor newest, double t, double hoursNow)
        {
            if (newest == null) return true;
            double predicted = newest.Hours + (t / WavePlace.SampleSeconds - newest.FromSample) * newest.HoursPerSample;
            return !(Math.Abs(predicted - hoursNow) <= MaxDrift);
        }

        /// <summary>The newest anchor kept (the server's), or null.</summary>
        public HoursAnchor Newest
        {
            get { lock (sync) return anchors.Length == 0 || local ? null : anchors[anchors.Length - 1]; }
        }

        /// <summary>Forget every anchor (a new world, a new clock).</summary>
        public void Clear()
        {
            lock (sync) { anchors = Array.Empty<HoursAnchor>(); local = false; droppedBelow = long.MinValue; version++; }
        }

        /// <summary>
        /// The game hour of a sample. With no anchor yet, one is made here from this estimate (the calendar now, worked
        /// back to the sample) and used until the server's arrive.
        /// </summary>
        public double At(long sample, double estimate, double hoursPerSample)
        {
            lock (sync)
            {
                if (anchors.Length == 0)
                {
                    anchors = new[] { new HoursAnchor { Seq = long.MinValue, FromSample = sample, Hours = estimate, HoursPerSample = Math.Max(0, hoursPerSample) } };
                    local = true;
                    version++;
                }
                return Pick(sample).At(sample);
            }
        }

        // the anchor in force at a sample: the last made that starts at or before it, else the earliest-starting
        HoursAnchor Pick(long sample)
        {
            HoursAnchor best = null, first = anchors[0];
            foreach (var a in anchors)
            {
                if (a.FromSample <= sample) best = a;
                if (a.FromSample < first.FromSample) first = a;
            }
            return best ?? first;
        }

        /// <summary>The anchor in force at a sample, or null.</summary>
        public HoursAnchor InForce(long sample)
        {
            lock (sync) return anchors.Length == 0 ? null : Pick(sample);
        }

        /// <summary>Every anchor kept, in the order they were made (for the clock packet; none made locally).</summary>
        public HoursAnchor[] All
        {
            get { lock (sync) return local ? Array.Empty<HoursAnchor>() : anchors; }
        }
    }

    /// <summary>
    /// Seconds on a clock the server and every client agree on, so they draw the same waves: the server's own
    /// running time, sent to each client every few seconds (plus half its ping). A client runs its own in-game clock
    /// and eases toward the server's by at most 2% so the waves never jump; it snaps only when it's more than a
    /// second off (joining, a long pause).
    /// </summary>
    public sealed class WaveClock
    {
        public const double SnapSeconds = 1;
        public const double MaxSlew = 0.02;

        double offset, target;
        public bool Synced { get; private set; }
        /// <summary>How far this clock still is from the server's last word (seconds, + = behind).</summary>
        public double Error => target - offset;

        /// <summary>The shared time for this side's local clock reading.</summary>
        public double Now(double local) => local + offset;

        /// <summary>The server said its clock reads serverSeconds when ours reads local.</summary>
        public void OnServerTime(double serverSeconds, double local)
        {
            // (0.4.2) a NaN or infinite time is refused: once in the offset, no later packet could snap it back
            if (!double.IsFinite(serverSeconds) || !double.IsFinite(local)) return;
            target = serverSeconds - local;
            if (!Synced || !double.IsFinite(offset) || Math.Abs(target - offset) > SnapSeconds)
            {
                offset = target;
                Synced = true;
            }
        }

        /// <summary>Ease toward the server's clock; call every frame with the local time that passed.</summary>
        public void Advance(double dt)
        {
            if (!double.IsFinite(dt)) return;
            double max = MaxSlew * Math.Max(0, dt);
            offset += Math.Clamp(target - offset, -max, max);
        }
    }
}
