using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sea;

namespace SeaTests
{
    // Frozen pre.7 equations for comparisons, evaluated with the same seeded phases/direction library.
    // This deliberately includes chop, directional energy splitting and the original per-band steepness cap.
    static class StormEvidence
    {
        internal static WavePlace Before(WaveSpectrum spec, double strength, double height, double time)
        {
            double lp = Math.Clamp(9.81 * Math.Pow(2.5 + 3.2 * Math.Sqrt(height), 2) / (2 * Math.PI), 10, 150);
            var amps = new double[spec.TrainCount];
            var w = Enumerable.Range(0, 12).Select(b => WaveSpectrum.SwellShape(Math.Log2(WaveSpectrum.Length(b) / lp))).ToArray();
            double scale = height / Math.Sqrt(8 * w.Sum(x => x * x));
            void Split(int bin, int seedBin, double theta, double a)
            {
                double p = theta / (2 * Math.PI / 18) - WorldShape.Mod(0.37 + seedBin * 0.6180339887, 1);
                double d = Math.Floor(p), f = p - d;
                int i = (int)WorldShape.Mod(d, 18);
                amps[bin * 18 + i] = a * Math.Cos(f * Math.PI / 2);
                amps[bin * 18 + (i + 1) % 18] = a * Math.Sin(f * Math.PI / 2);
            }
            for (int b = 0; b < 12; b++)
            {
                double len = WaveSpectrum.Length(b), u = Math.Log2(len / lp);
                double spread = 40 * Math.PI / 180 * Math.Clamp(-u / 2.4, 0, 1) * Math.Sin(b * 2.39996 + 0.5);
                Split(b, b, spread, Math.Min(w[b] * scale, 0.05 * len));
            }
            double chop = 0.03 + 0.12 * Math.Min(1.5, strength);
            Split(WaveSpectrum.SwellBins, 12, 0, chop);
            Split(WaveSpectrum.SwellBins + 1, 13, -35 * Math.PI / 180, chop * 0.6);
            int[] ids = Enumerable.Range(0, amps.Length).Where(i => amps[i] > 1e-5).ToArray();
            var p = new WavePlace();
            p.SetTrains(time, ids.Length, ids.Select(i => spec.Kx[i]).ToArray(), ids.Select(i => spec.Kz[i]).ToArray(),
                ids.Select(i => amps[i]).ToArray(), ids.Select(i => spec.Phase[i]).ToArray());
            return p;
        }

        static (double modal, double shortSlope, double longShare) Metrics(WavePlace p, double lp)
        {
            var bands = new Dictionary<double, double>();
            double total = 0, longEnergy = 0, shortSlope = 0;
            for (int i = 0; i < p.Count; i++)
            {
                double k = Math.Sqrt(p.Kx[i] * p.Kx[i] + p.Kz[i] * p.Kz[i]);
                double len = 2 * Math.PI / k, e = p.Amp[i] * p.Amp[i];
                double band = Math.Round(len, 3);
                bands[band] = bands.GetValueOrDefault(band) + e;
                total += e;
                if (len >= lp / 4) longEnergy += e;
                if (len < 16) shortSlope += 0.5 * e * k * k;
            }
            return (bands.MaxBy(x => x.Value).Key, Math.Sqrt(shortSlope), longEnergy / total);
        }

        public static void Run(Action<bool, string> check)
        {
            Console.WriteLine("storm spectrum, pre.7 -> pre.8 (fully developed, key 4242, east, WaveScale 1)");
            var rows = new List<string> { "strength,version,storm_height_scale,peak_parameter_m,modal_band_m,significant_height_m,short_slope_rms,long_energy_fraction,whitecap_target" };
            foreach (float stormScale in new[] { 1f, 2.2f })
            {
            foreach (double s in new[] { 0.3, 0.8, 1.2, 1.5 })
            {
                var cfg = new SeaConfig { SeaWindStrength = (float)s, StormHeightScale = stormScale };
                var model = new WindModel(new WorldShape(), cfg);
                var wind = new Wind(s, 0);
                // Provider avoids float-config rounding in the comparison.
                model.Provider = new WindProvider("constant", (x,z,h,g) => (s, 0.0), null);
                var sea = model.SeaAt(0, 0, 100);
                var spec = new WaveSpectrum(4242, 0);
                var after = new WavePlace();
                after.Update(spec, 100, sea, wind);
                double oldHeight = 0.1 + 2.4 * s * s;
                var before = Before(spec, s, oldHeight, 100);
                double oldLp = Math.Min(150, 9.81 * Math.Pow(2.5 + 3.2 * Math.Sqrt(oldHeight), 2) / (2 * Math.PI));
                var old = Metrics(before, oldLp);
                var now = Metrics(after, sea.Wavelength);
                double foam = WaveSpectrum.WhitecapCoverage(s);
                if (stormScale == 1) rows.Add(FormattableString.Invariant($"{s},pre.7,1,{oldLp:F3},{old.modal:F3},{before.SignificantHeight:F4},{old.shortSlope:F5},{old.longShare:F5},0"));
                rows.Add(FormattableString.Invariant($"{s},pre.8,{stormScale},{sea.Wavelength:F3},{now.modal:F3},{after.SignificantHeight:F4},{now.shortSlope:F5},{now.longShare:F5},{foam:F5}"));
                Console.WriteLine(FormattableString.Invariant($"  scale {stormScale:F1}, {s:F1}: Lp {oldLp:F1} -> {sea.Wavelength:F1} m; modal {old.modal:F0} -> {now.modal:F0} m; Hs {before.SignificantHeight:F3} -> {after.SignificantHeight:F3}; short slope {old.shortSlope:F4} -> {now.shortSlope:F4}; foam target {foam:P1}"));
                check(after.Count <= 24 && Math.Abs(after.SignificantHeight - sea.Height) < 0.13,
                    $"{s}: retained spectrum respects 24 trains and the existing significant-height scale");
                if (s == 0.3)
                {
                    double worst = 0;
                    for (int i = 0; i < 200; i++) worst = Math.Max(worst, Math.Abs(before.HeightAt(i * 3.7, i * 1.9) - after.HeightAt(i * 3.7, i * 1.9)));
                    check(worst < 1e-10, $"default breeze geometry/phases unchanged (worst {worst:E2})");
                }
                else check(now.shortSlope >= 0.055 && now.shortSlope <= 0.075 && now.longShare > 0.90
                    && Math.Abs(sea.Wavelength / after.SignificantHeight - WindModel.StormLengthRatio(oldHeight, 1)) < 1.5,
                    $"scale {stormScale}, {s}: Lp/Hs {sea.Wavelength / after.SignificantHeight:F1} as the storm steepness gives ({WindModel.StormLengthRatio(oldHeight, 1):F1}: 30 to a gale, 18 at 1.5), long waves >90% energy, breeze-like short RMS slope");
                // The actual renderer upload, including camera phase reduction, against the CPU ship sampler.
                var uniforms = new float[96];
                int count = after.FillUniforms(uniforms, 378606, 69939);
                double mismatch = 0;
                for (int i = 0; i < 300; i++) mismatch = Math.Max(mismatch, Math.Abs(
                    WavePlace.ShaderHeight(uniforms, count, i * 1.7, -i * 0.6) - after.HeightAt(378606 + i * 1.7, 69939 - i * 0.6)));
                check(mismatch < 0.001, $"{s}: drawn-water uniforms and ship samples agree ({mismatch:E2} blocks)");
            }
            }
            Directory.CreateDirectory("obj");
            File.WriteAllLines("obj/storm-spectrum.csv", rows);
            double lambda(double s) => new WindModel(new WorldShape(), new SeaConfig { SeaWindStrength = (float)s }).SeaAt(0,0,100).Wavelength;
            check(Math.Abs(lambda(1.5) / 5.5 - 18) < 1e-5 && Math.Abs(lambda(0.8) / 1.636 - 30) < 1e-5,
                "storm peak wavelength follows gameplay significant height: Lp/Hs = 30 at a gale (0.8), 18 at the strongest wind (1.5)");
            check(WaveSpectrum.WhitecapCoverage(0) == 0 && WaveSpectrum.WhitecapCoverage(0.3) < 0.005
                && WaveSpectrum.WhitecapCoverage(0.8) < WaveSpectrum.WhitecapCoverage(1.2)
                && WaveSpectrum.WhitecapCoverage(1.5) > 0.8, "whitecaps start faintly in a breeze and become dense in a hurricane");
            // Fixed-length tail amplitudes must not keep growing as the storm peak moves to longer waves.
            var spectrum = new WaveSpectrum(4242, 0);
            double shortMin = double.MaxValue, shortMax = 0;
            foreach (double s in new[] { 0.8, 1.2, 1.5 })
            {
                var model = new WindModel(new WorldShape(), new SeaConfig { SeaWindStrength = (float)s });
                var a = new double[spectrum.TrainCount];
                spectrum.Amplitudes(model.SeaAt(0,0,100), new Wind(s,0), a);
                double variance = Enumerable.Range(0,a.Length).Where(i => 2*Math.PI/Math.Sqrt(spectrum.Kx[i]*spectrum.Kx[i]+spectrum.Kz[i]*spectrum.Kz[i]) < 16)
                    .Sum(i => 0.5*a[i]*a[i]*(spectrum.Kx[i]*spectrum.Kx[i]+spectrum.Kz[i]*spectrum.Kz[i]));
                shortMin = Math.Min(shortMin,Math.Sqrt(variance)); shortMax = Math.Max(shortMax,Math.Sqrt(variance));
            }
            check(shortMin > 0.055 && shortMax <= 0.065001, "storm equilibrium texture saturates near breeze slope without vanishing");
            foreach (float scale in new[] {0f,0.5f,1f,2.2f,4f})
            {
                var model = new WindModel(new WorldShape(), new SeaConfig {SeaWindStrength=1.5f,StormHeightScale=scale});
                var sea = model.SeaAt(0,0,100);
                var p = new WavePlace(); p.Update(spectrum,100,sea,new Wind(1.5,0));
                var metric = Metrics(p,Math.Max(1,sea.Wavelength));
                check((scale == 0 || Math.Abs(sea.Wavelength/sea.Height-18) < 1e-10)
                    && metric.shortSlope >= 0.055 && metric.shortSlope <= 0.075,
                    $"StormHeightScale {scale}: wavelength scales with height (Lp/Hs 18 at 1.5 whatever the scale), independently saturated texture retained");
            }
            double seamWorst = 0, ratioMin = 1, ratioMax = 0;
            var ring = new WaveSpectrum(4242, 254000);
            foreach (double s in new[] {0.0,0.3,0.4,0.5,0.6,0.8,1.2,1.5})
            {
                var model = new WindModel(new WorldShape(), new SeaConfig {SeaWindStrength=(float)s});
                var sea = model.SeaAt(0,0,100);
                for (int bearing=0; bearing<360; bearing+=13)
                {
                    double angle = bearing*Math.PI/180;
                    var p = new WavePlace();
                    p.Update(ring,100,new SeaState(sea.Height,Math.Cos(angle),Math.Sin(angle),sea.Period),new Wind(s*Math.Cos(angle),s*Math.Sin(angle)));
                    double ratio = p.SignificantHeight / sea.Height;
                    if (s >= 0.4) { ratioMin = Math.Min(ratioMin,ratio); ratioMax = Math.Max(ratioMax,ratio); }
                    seamWorst=Math.Max(seamWorst,Math.Abs(p.HeightAt(378606,69939)-p.HeightAt(378606+254000,69939)));
                }
            }
            check(seamWorst < 1e-8 && ratioMin > 0.95 && ratioMax < 1.3,
                $"all headings through breeze/storm transition preserve the ring and storm height budget (seam {seamWorst:E2}, Hs ratio {ratioMin:F3}–{ratioMax:F3})");
        }
    }
}
