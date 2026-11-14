using System;
using System.Collections.Generic;

namespace Sea
{
    // (SE-0046) This file holds WavePlace; its name stays WaveField.cs because other repositories' tests compile it by path
    // (Ships' HansaTests, Magelan's MagelanTests, the Issue30 check). The other wave types are in WaveSpectrum.cs,
    // WaveSource.cs (IWaveSource, SharedCorners), SampleHours.cs and WaveClock.cs beside it.
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
        // Gerstner horizontal Jacobian stays positive: sum(Q_i A |k|) <= 0.65 < 1.
        // Also a contraction bound for the inverse world-position sample. Finch, GPU Gems ch. 1.
        public const double MaxCompression = 0.65;
        public const int InverseIterations = 24;
        /// <summary>Concentrate horizontal crest shaping in the swell; keep the equilibrium chop as texture.</summary>
        public static double ShapeWeight(double k) => 0.15 + 0.85 / (1 + k * k / 0.09);
        /// <summary>Orbit multiplier, before ShapeWeight. The current spectrum supplies steepness; never loops.</summary>
        public double Shape
        {
            get
            {
                double sum = 0;
                for (int i = 0; i < Count; i++)
                {
                    double k = Math.Sqrt(Kx[i] * Kx[i] + Kz[i] * Kz[i]);
                    sum += Math.Abs(Amp[i]) * k * ShapeWeight(k);
                }
                return Math.Min(4.0, MaxCompression / Math.Max(sum, 1e-12));
            }
        }

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
            blendValid = false;
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
            blendValid = false;
        }

        void Advance(WaveSpectrum spec, double t, in SeaState sea, in Wind wind, bool bothEnds, in SeaState prevSea, in Wind prevWind)
        {
            Time = t;
            if (spec == null) { Count = 0; return; }
            long n = SampleIndex(t);
            if (spec != spectrum) Attach(spec);
            gridSample = long.MinValue;   // (the grid's buffers are left as they are; Sample starts them over)
            blendValid = false;
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
        // gridSample: the sample both ends are worked out for (long.MinValue: nothing usable), at these hours, from these
        // inputs (IWaveSource.Inputs), as of this revision (IWaveSource.Revision)
        long gridSample = long.MinValue, gridCx, gridCz, gridRevision, gridInputs;
        double gridHours0 = double.NaN, gridHours1 = double.NaN;
        // (SE-0021) the sea state and wind at the corners at the sample's end, blended by position into Sea and Wind
        readonly SeaState[] cornerSea = new SeaState[4];
        readonly Wind[] cornerWind = new Wind[4];
        // (SE-0026) the blend last made from the grid: Kx..Amp hold it while nothing changed
        bool blendValid;
        double blendU, blendV, blendF;

        /// <summary>(SE-0014) How many times a sample is worked out again at once when another thread changed the sea meanwhile.</summary>
        public const int SampleRetries = 3;

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
            bool changed = false;
            for (int attempt = 0; attempt < SampleRetries; attempt++)
            {
                long revision = source.Revision;
                if (n == gridSample && cx == gridCx && cz == gridCz && revision == gridRevision) break;
                changed = true;
                long inputs = source.Inputs;
                double h0 = source.HoursOf(n - 1, t), h1 = source.HoursOf(n, t);
                // (SE-0025) an end already worked out here for the same hour, from the same inputs, is kept: the second's
                // start is the last second's end, and a new anchor that leaves this second's hours alone changes nothing
                bool same = gridSample != long.MinValue && gridCx == cx && gridCz == cz && gridInputs == inputs;
                bool keepNext = same && gridSample == n && gridHours1 == h1;
                bool keepPrev = same && gridSample == n && gridHours0 == h0;
                bool shift = !keepPrev && same && gridSample == n - 1 && gridHours1 == h0;
                gridSample = long.MinValue;
                if (shift) (gridPrev, gridNext) = (gridNext, gridPrev);
                double x0 = spec.Circumference > 0 ? ringStart : 0;
                for (int c = 0; c < 4; c++)
                {
                    double px = x0 + (fx + (c & 1)) * cell, pz = (fz + (c >> 1)) * cell;
                    if (!keepPrev && !shift) spec.Amplitudes(source.SeaAt(px, pz, h0, n - 1), source.WindAt(px, pz, h0, n - 1), gridPrev[c]);
                    if (keepNext) continue;
                    var sea = source.SeaAt(px, pz, h1, n);
                    var wind = source.WindAt(px, pz, h1, n);
                    spec.Amplitudes(sea, wind, gridNext[c]);
                    cornerSea[c] = sea;
                    cornerWind[c] = wind;
                }
                gridCx = cx;
                gridCz = cz;
                gridInputs = inputs;
                gridHours0 = h0;
                gridHours1 = h1;
                gridRevision = revision;
                // (SE-0014) something changed while this was worked out, from another thread: what was read may mix the old
                // and the new, so it's worked out again at once (and, should changes keep coming, at the next call)
                if (source.Revision == revision) { gridSample = n; break; }
            }
            double f = Math.Clamp(t / SampleSeconds - n, 0, 1);
            // (SE-0026) the same place in the same moment as last time, nothing new from the grid: the trains are as they are
            if (!changed && blendValid && u == blendU && v == blendV && f == blendF) return;
            double w0 = (1 - u) * (1 - v), w1 = u * (1 - v), w2 = (1 - u) * v, w3 = u * v;
            BlendCorners(w0, w1, w2, w3);
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
            blendValid = gridSample == n;
            blendU = u;
            blendV = v;
            blendF = f;
        }

        /// <summary>
        /// (SE-0021) <see cref="Sea"/> and <see cref="Wind"/> at the place: the corners' (at the sample's end) blended by
        /// position like the amplitudes, not the nearest corner's, so they don't jump half-way across a cell.
        /// </summary>
        void BlendCorners(double w0, double w1, double w2, double w3)
        {
            Span<double> w = stackalloc double[] { w0, w1, w2, w3 };
            double h = 0, dx = 0, dz = 0, period = 0, blend = 0, wx = 0, wz = 0;
            bool shaped = true;
            int nearest = 0;
            for (int c = 0; c < 4; c++)
            {
                var s = cornerSea[c];
                h += w[c] * s.Height;
                dx += w[c] * s.DirX;
                dz += w[c] * s.DirZ;
                period += w[c] * s.Period;
                if (s.SpectrumBlend < 0) shaped = false;
                else blend += w[c] * s.SpectrumBlend;
                wx += w[c] * cornerWind[c].X;
                wz += w[c] * cornerWind[c].Z;
                if (w[c] > w[nearest]) nearest = c;
            }
            double dl = Math.Sqrt(dx * dx + dz * dz);
            if (dl < 1e-9) { dx = cornerSea[nearest].DirX; dz = cornerSea[nearest].DirZ; dl = 1; }
            Sea = new SeaState(h, dx / dl, dz / dl, period, shaped ? blend : -1);
            Wind = new Wind(wx, wz);
        }

        /// <summary>The trains blended[order[0..c]] go to the shader's list (the strongest, when there are too many).</summary>
        void Keep(WaveSpectrum spec, int c)
        {
            // more trains than the shader takes (broad spectra or while the sea turns): keep the strongest, each less the strongest
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
            blendValid = false;
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
            var q = ParameterAt(x, z, t);
            return Elevation(q.X, q.Z, t);
        }

        /// <summary>
        /// (0.6.0) The coast under this place's water (null: open sea everywhere, as before 0.6.0). With one, the land
        /// upwave keeps its lee, and near a shore the trains hand over to surf trains that follow the bottom, shoal, break
        /// and take a breaker's shape, held back over rip channels, exactly as the shader draws them from sea_CoastTex
        /// (<see cref="CoastMath"/>).
        /// </summary>
        public CoastLookup Coast;

        /// <summary>
        /// (0.6.0) The wind (m/s, x east, z south) the coast's wind sea follows: set for tests or a mod's own sea; null
        /// takes the newest sample's (<see cref="Wind"/>). The shader gets the same (<see cref="FillCoastSea"/>).
        /// </summary>
        public (double X, double Z)? CoastWindOverride;

        /// <summary>(0.6.0) The wind the coast's wind sea follows now, m/s.</summary>
        public (double X, double Z) CoastWind => CoastWindOverride ?? (Wind.X * Beaufort.MetersPerSecondPerUnit, Wind.Z * Beaufort.MetersPerSecondPerUnit);

        /// <summary>
        /// (0.6.0) The coast's numbers at a mesh coordinate, and its breaking there (false: open sea). Its Time is the surf's
        /// own travel time there (<see cref="CoastMath.SurfTime"/>: longer over a rip).
        /// </summary>
        public bool CoastAt(double qx, double qz, out CoastSample c, out double cap, out double breaking)
        {
            Span<double> reach = stackalloc double[MaxWaves];
            return CoastAt(qx, qz, out c, out cap, out breaking, out _, out _, reach);
        }

        /// <summary>
        /// (0.6.0) As above, with every train's share that gets past the land to here (<see cref="CoastMath.Reach"/>) and the
        /// longshore current (m/s) that turns the surf's crests: what the shader's sea_CoastAt works out.
        /// </summary>
        public bool CoastAt(double qx, double qz, out CoastSample c, out double cap, out double breaking, out double vx, out double vz, Span<double> reach)
        {
            cap = 1; breaking = 0; vx = vz = 0;
            if (Coast == null || Count == 0 || !Coast(qx, qz, out c) || !c.Affects) { c = CoastSample.Open; return false; }
            var (wx, wz) = CoastWind;
            CoastMath.Reach(c, Count, Kx, Kz, Omega, wx, wz, reach);
            (cap, breaking) = CoastMath.Breaking(c, Count, Kx, Kz, Omega, Amp, reach);
            (vx, vz) = CoastMath.LongshoreAt(c, Count, Kx, Kz, Omega, Amp, reach);
            if (c.Rip > 0) c.Time = CoastMath.SurfTime(c, SignificantHeight);
            return true;
        }

        /// <summary>(SE-0041) The height drawn from mesh coordinate q now, times factor: <see cref="SurfaceAt"/>'s Y without its
        /// horizontal displacement.</summary>
        public double ElevationAt(double qx, double qz, double factor = 1) => Elevation(qx, qz, Time) * factor;

        /// <summary>
        /// (0.6.0) Train i's height at mesh coordinate q on a coast (WaterShader's sea_TrainY): its open part (the share that
        /// isn't surf), its surf part, which meets the shore where the sea's own crests do and turns in the longshore
        /// current, and the part the shore throws back. Gives the open part's amplitude and the surf part's amplitude
        /// and phase.
        /// </summary>
        double TrainOnCoast(int i, double qx, double qz, double t, in CoastSample c, double cap, double b, double vx, double vz,
            double hs, double reach, out double open, out double surfAmp, out double surfPhase)
        {
            double th = Kx[i] * qx + Kz[i] * qz - Omega[i] * t + Phase[i];
            double s = CoastMath.SurfShare(Math.Sqrt(Kx[i] * Kx[i] + Kz[i] * Kz[i]), c), a = Amp[i] * cap * reach;
            open = a * (1 - s);
            double y = open * Math.Sin(th);
            surfAmp = 0; surfPhase = 0;
            if (s > 0)
            {
                surfPhase = th + CoastMath.ShoreOffset(c, Kx[i], Kz[i]) - (Omega[i] - CoastMath.Bend(Kx[i], Kz[i], Omega[i], vx, vz)) * c.Time;
                surfAmp = a * s * CoastMath.Shoaling(Omega[i], c.Depth);
                y += surfAmp * CoastMath.Breaker(surfPhase, b).Value;
            }
            double kr = CoastMath.Reflection(c, Kx[i], Kz[i], Omega[i], hs);
            if (kr > 0) y += open * kr * Math.Sin(th + 2 * CoastMath.ShoreOffset(c, Kx[i], Kz[i]));
            return y;
        }

        /// <summary>Height above rest of the surface point drawn from mesh coordinate q at wave time t.</summary>
        double Elevation(double qx, double qz, double t)
        {
            Span<double> reach = stackalloc double[MaxWaves];
            bool coast = CoastAt(qx, qz, out var c, out double cap, out double b, out double vx, out double vz, reach);
            double y = 0, hs = coast ? SignificantHeight : 0;
            for (int i = 0; i < Count; i++)
            {
                if (!coast) { y += Amp[i] * Math.Sin(Kx[i] * qx + Kz[i] * qz - Omega[i] * t + Phase[i]); continue; }
                y += TrainOnCoast(i, qx, qz, t, c, cap, b, vx, vz, hs, reach[i], out _, out _, out _);
            }
            return y;
        }

        /// <summary>
        /// (0.6.0) The surf at a world position now: how far the waves break there (0 to 1), the whitewater the shader
        /// draws (0 to 1), and the depth (blocks; <see cref="CoastMath.MaxDepth"/> off the coast).
        /// </summary>
        public (double Breaking, double Foam, double Depth) SurfAt(double x, double z)
        {
            var q = ParameterAt(x, z, Time);
            Span<double> reach = stackalloc double[MaxWaves];
            if (!CoastAt(q.X, q.Z, out var c, out double cap, out double b, out double vx, out double vz, reach)) return (0, 0, c.Depth);
            double sum = 0, crest = 0, hs = SignificantHeight;
            for (int i = 0; i < Count; i++)
            {
                TrainOnCoast(i, q.X, q.Z, Time, c, cap, b, vx, vz, hs, reach[i], out _, out double a, out double phase);
                if (a <= 0) continue;
                double u = CoastMath.Breaker(phase, b).Crest;
                sum += a; crest += a * u * u * u;
            }
            return (b, CoastMath.SurfFoam(b, sum, crest), c.Depth);
        }

        /// <summary>
        /// (0.6.0) The open sea's crest at a world position now, as the water shader works it out for its whitecaps
        /// (WaterShader's <c>sea_Differential</c>, its <c>crest</c> output, with the whole of every train): the long
        /// waves' height over their spread, the crest's compression (1 less the smallest horizontal eigenvalue of the
        /// surface's Jacobian; before the place's height factor, which the caller multiplies in, as the shader does), and
        /// the long waves' falling speed over its spread. Open sea only: on a coast the shader's crest includes the
        /// orbits' change of size, which this leaves out (the surf has its own measure, <see cref="SurfAt"/>).
        /// </summary>
        public (double Height, double Compression, double Fall) CrestAt(double x, double z)
        {
            var q = ParameterAt(x, z, Time);
            return CrestAtMesh(q.X, q.Z);
        }

        /// <summary>(SE-0054) <see cref="CrestAt"/> at a mesh coordinate (no inverse): the crest the surface draws from q.</summary>
        public (double Height, double Compression, double Fall) CrestAtMesh(double qx, double qz)
        {
            var q = (X: qx, Z: qz);
            double shape = Shape, j00 = 1, j01 = 0, j11 = 1, height = 0, variance = 0, fall = 0, fallVariance = 0;
            for (int i = 0; i < Count; i++)
            {
                double k = Math.Sqrt(Kx[i] * Kx[i] + Kz[i] * Kz[i]);
                if (k < 1e-7) continue;
                double phase = Kx[i] * q.X + Kz[i] * q.Z - Omega[i] * Time + Phase[i], sn = Math.Sin(phase), cs = Math.Cos(phase);
                double ux = Kx[i] / k, uz = Kz[i] / k, c = shape * ShapeWeight(k) * Amp[i] * k * sn;
                j00 -= c * ux * ux; j01 -= c * ux * uz; j11 -= c * uz * uz;
                double longWave = 1 - CoastMath.SmoothStep(0.3, 0.6, k), a = longWave * Amp[i], omega = Math.Sqrt(9.81 * k);
                height += a * sn; variance += 0.5 * a * a;
                fall += a * omega * cs; fallVariance += 0.5 * a * a * omega * omega;
            }
            double least = 0.5 * (j00 + j11 - Math.Sqrt((j00 - j11) * (j00 - j11) + 4 * j01 * j01));
            return (height / Math.Max(Math.Sqrt(variance), 1e-4), Math.Max(0, 1 - least), fall / Math.Max(Math.Sqrt(fallVariance), 1e-4));
        }

        /// <summary>
        /// (0.6.0) How far a crest breaks into whitecap foam, 0 to 1, by the shader's gate (CelestialShader's
        /// <c>sea_CrestFoam</c> without its noise patches): a sharpened crest (compression, with the place's height
        /// factor, from 0.22) standing well above the long waves' mean (from 0.8 of their spread). In a storm (the wind's
        /// whitecap coverage from 0.1 to 0.9) the gate eases to compression from 0.14 and height from 0.35, never low
        /// enough for sheltered water (at most 0.65 x 0.2 = 0.13).
        /// </summary>
        public static double Whitecap(double height, double compression, double coverage = 0)
        {
            double s = CoastMath.SmoothStep(0.1, 0.9, coverage);
            return CoastMath.SmoothStep(Lerp(0.22, 0.14, s), Lerp(0.40, 0.30, s), compression) * CoastMath.SmoothStep(Lerp(0.8, 0.35, s), Lerp(1.65, 1.2, s), height);
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>
        /// (0.6.0) How far a crest throws spray, 0 to 1: the whitecap's gate (<see cref="Whitecap"/>) widened to the
        /// sharpened upper part of a wave, since wind tears spray off a crest before it whitens. Thresholds
        /// <see cref="SprayCompression"/> and <see cref="SprayHeight"/>.
        /// </summary>
        public static double SprayCrest(double height, double compression) =>
            CoastMath.SmoothStep(SprayCompression, SprayCompression + 0.18, compression) * CoastMath.SmoothStep(SprayHeight, SprayHeight + 0.85, height);

        /// <summary>Measured (SeaTests): about 10% of the open sea from Beaufort 6 to 12, 1.5% at 4; the crests' sharpness
        /// hardly grows past force 6, so how much of it throws spray is left to the wind's whitecap coverage.</summary>
        public const double SprayCompression = 0.08, SprayHeight = 0.4;

        /// <summary>
        /// (0.6.0) The strongest long train here (wavelength over about 10 blocks): the way it runs and its phase speed
        /// in blocks per second, the speed the crests travel at. False with no trains.
        /// </summary>
        public bool LeadingTrain(out double ux, out double uz, out double speed)
        {
            ux = uz = speed = 0;
            double best = 0;
            for (int i = 0; i < Count; i++)
            {
                double k = Math.Sqrt(Kx[i] * Kx[i] + Kz[i] * Kz[i]);
                if (k < 1e-7 || k > 0.6 || Amp[i] <= best) continue;
                best = Amp[i];
                double sign = Omega[i] >= 0 ? 1 : -1;
                ux = sign * Kx[i] / k; uz = sign * Kz[i] / k; speed = Math.Abs(Omega[i]) / k;
            }
            return best > 0;
        }

        /// <summary>
        /// (0.6.0) Moves (x, z) uphill along the line (ux, uz) to the top of the crest there (half-block steps, halved
        /// down to an eighth; at most a few blocks) and returns that height above rest.
        /// </summary>
        public double ClimbCrest(ref double x, ref double z, double ux, double uz)
        {
            double h = HeightAt(x, z), step = 0.5;
            for (int it = 0; it < 12 && step > 0.1; it++)
            {
                double ahead = HeightAt(x + ux * step, z + uz * step);
                if (ahead > h) { x += ux * step; z += uz * step; h = ahead; continue; }
                double behind = HeightAt(x - ux * step, z - uz * step);
                if (behind > h) { x -= ux * step; z -= uz * step; h = behind; continue; }
                step *= 0.5;
            }
            return h;
        }

        /// <summary>
        /// (0.6.0) The shader's sea_CoastSea uniform: the significant height, and the wind the coast's wind sea follows
        /// (m/s; <see cref="CoastWind"/>).
        /// </summary>
        public void FillCoastSea(float[] u)
        {
            var (wx, wz) = CoastWind;
            u[0] = (float)SignificantHeight; u[1] = (float)wx; u[2] = (float)wz; u[3] = 0;
        }

        /// <summary>
        /// (0.6.0) The shader's sea_WaveClock[] uniform: per train its angular speed and (unused since the surf meets the
        /// shore where the sea's own crests do) its phase less ω t, mod 2π.
        /// </summary>
        /// <summary>(SE-0042) Each train's lee sector, fraction, wind-sea share and fetch power (<see cref="CoastMath.TrainLee"/>),
        /// 4 floats each, for the shader's sea_WaveLee.</summary>
        public int FillLee(float[] u)
        {
            int n = Math.Min(Count, u.Length / 4);
            var (wx, wz) = CoastWind;
            for (int i = 0; i < n; i++) CoastMath.TrainLee(Kx[i], Kz[i], Omega[i], wx, wz, u, i * 4);
            return n;
        }

        public int FillClock(float[] u)
        {
            int n = Math.Min(Count, u.Length / 2);
            for (int i = 0; i < n; i++)
            {
                u[i * 2] = (float)Omega[i];
                u[i * 2 + 1] = (float)WorldShape.Mod(Phase[i] - Omega[i] * Time, 2 * Math.PI);
            }
            return n;
        }

        /// <summary>Undisplaced mesh coordinate at world X/Z. Shelter is packed at this coordinate by the game.</summary>
        public (double X, double Z) ParameterAt(double x, double z) => ParameterAt(x, z, Time);

        public (double X, double Z) ParameterAt(double x, double z, double t)
        {
            // Invert X = q + sum(Q A direction cos(theta(q))). A height at q is NOT a height at world X.
            // Horizontal orbits deliberately do not depend on the liquid's vertical divisor: legacy callers that
            // multiply HeightAt by WaveFactorAt still sample precisely the shader's sheltered surface.
            double qx = x, qz = z, shape = Shape;
            for (int j = 0; j < InverseIterations; j++)
            {
                Displacement(qx, qz, t, shape, out double dx, out double dz);
                double nx = x - dx, nz = z - dz;
                bool converged = Math.Abs(nx - qx) + Math.Abs(nz - qz) < 1e-7;
                qx = nx; qz = nz;
                if (converged) return (qx, qz);
            }
            // (0.6.0) On a coast the orbits' size changes from place to place (the lee of land, a rip, the depth cap), and
            // where it changes fast the plain iteration no longer contracts: finish with Newton's method on
            // q + D(q) = x (its Jacobian by differences), each step halved until it brings the point closer.
            if (Coast == null) return (qx, qz);
            double Miss(double px, double pz, out double ex, out double ez)
            {
                Displacement(px, pz, t, shape, out double ddx, out double ddz);
                ex = px + ddx - x; ez = pz + ddz - z;
                return Math.Abs(ex) + Math.Abs(ez);
            }
            double miss = Miss(qx, qz, out double fx, out double fz);
            const double h = 1e-4;
            for (int j = 0; j < InverseIterations && miss > 1e-7; j++)
            {
                Miss(qx + h, qz, out double ax, out double az);
                Miss(qx, qz + h, out double bx, out double bz);
                double j11 = (ax - fx) / h, j21 = (az - fz) / h, j12 = (bx - fx) / h, j22 = (bz - fz) / h;
                double det = j11 * j22 - j12 * j21;
                if (Math.Abs(det) < 1e-9) break;
                double sx = (j22 * fx - j12 * fz) / det, sz = (j11 * fz - j21 * fx) / det;
                bool better = false;
                for (double step = 1; step > 1e-3; step *= 0.5)
                {
                    double px = qx - step * sx, pz = qz - step * sz, m = Miss(px, pz, out double gx, out double gz);
                    if (m < miss) { qx = px; qz = pz; miss = m; fx = gx; fz = gz; better = true; break; }
                }
                if (!better) break;
            }
            return (qx, qz);
        }

        void Displacement(double qx, double qz, double t, double shape, out double dx, out double dz)
        {
            dx = dz = 0;
            // (0.6.0) on a coast the orbits shrink with the cap and what gets past the land, and the surf trains and the
            // shore's reflection (shaped vertically) have none
            Span<double> reach = stackalloc double[MaxWaves];
            bool coast = CoastAt(qx, qz, out var c, out double cap, out _, out _, out _, reach);
            for (int i = 0; i < Count; i++)
            {
                double k = Math.Sqrt(Kx[i] * Kx[i] + Kz[i] * Kz[i]);
                if (k < 1e-12) continue;
                double a = coast ? Amp[i] * cap * reach[i] * (1 - CoastMath.SurfShare(k, c)) : Amp[i];
                double d = shape * ShapeWeight(k) * a / k * Math.Cos(Kx[i] * qx + Kz[i] * qz - Omega[i] * t + Phase[i]);
                dx += Kx[i] * d; dz += Kz[i] * d;
            }
        }

        /// <summary>Parametric surface point, as the vertex shader draws it. Mainly useful for diagnostics.</summary>
        public (double X, double Y, double Z) SurfaceAt(double qx, double qz, double factor = 1)
        {
            Displacement(qx, qz, Time, Shape, out double dx, out double dz);
            return (qx + dx, Elevation(qx, qz, Time) * factor, qz + dz);
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
        /// The shader's sea_Wave[] uniform for a camera at (camX, camZ): per train (kx, kz, phase at the camera,
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

        /// <summary>The shader's surface height at camera-relative world X/Z (inverse map, evaluated in doubles).
        /// The GPU vertex shader itself evaluates the forward parametric map; tests check both directions.</summary>
        public static double ShaderHeight(float[] u, int n, double rx, double rz)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) { double k = Math.Sqrt(u[i * 4] * u[i * 4] + u[i * 4 + 1] * u[i * 4 + 1]); sum += Math.Abs(u[i * 4 + 3]) * k * ShapeWeight(k); }
            double shape = Math.Min(4.0, MaxCompression / Math.Max(sum, 1e-12)), qx = rx, qz = rz;
            for (int j = 0; j < InverseIterations; j++)
            {
                double dx = 0, dz = 0;
                for (int i = 0; i < n; i++)
                {
                    double k = Math.Sqrt(u[i * 4] * u[i * 4] + u[i * 4 + 1] * u[i * 4 + 1]);
                    if (k < 1e-12) continue;
                    double d = shape * ShapeWeight(k) * u[i * 4 + 3] / k * Math.Cos(u[i * 4] * qx + u[i * 4 + 1] * qz + u[i * 4 + 2]);
                    dx += u[i * 4] * d; dz += u[i * 4 + 1] * d;
                }
                qx = rx - dx; qz = rz - dz;
            }
            double y = 0;
            for (int i = 0; i < n; i++) y += u[i * 4 + 3] * Math.Sin(u[i * 4] * qx + u[i * 4 + 1] * qz + u[i * 4 + 2]);
            return y;
        }
    }
}
