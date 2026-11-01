using System;
using Sea;

namespace SeaTests
{
    static class BoatChecks
    {
        public static void Run(Action<bool, string> check)
        {
            var raft = new BoatWaterplane(1.5, 0.75);
            var sailboat = new BoatWaterplane(4.4, 1.4, 1.5);
            foreach (var hull in new[] { raft, sailboat, new BoatWaterplane(3.9, 1.4, 2) })
                for (int i = 0; i < 12; i++)
                {
                    double yaw = i * Math.PI / 6;
                    var p = BoatWaveMath.Sample((x, z) => 0.4 + 0.08 * (x - 100) - 0.12 * (z - 200), 100, 200, yaw, hull);
                    double along = 0.08 * Math.Sin(yaw) - 0.12 * Math.Cos(yaw);
                    double across = 0.08 * Math.Cos(yaw) + 0.12 * Math.Sin(yaw);
                    check(Math.Abs(p.Heave - 0.4) < 1e-12 && Math.Abs(-Math.Tan(p.Pitch) / Math.Cos(p.Roll) - along) < 1e-12
                        && Math.Abs(-Math.Tan(p.Roll) - across) < 1e-12, "all boat waterplanes fit a sloping sea at heading " + i);
                }
            var waves = new WavePlace();
            double k = 2 * Math.PI / 40;
            waves.SetTrains(0, 1, new[] { 0.0 }, new[] { k }, new[] { 0.8 }, new[] { Math.PI / 2 });
            var crest = BoatWaveMath.Sample((x, z) => waves.HeightAt(x, z), 0, 0, 0, raft);
            waves.SetTrains(Math.PI / Math.Sqrt(9.81 * k), 1, new[] { 0.0 }, new[] { k }, new[] { 0.8 }, new[] { Math.PI / 2 });
            var trough = BoatWaveMath.Sample((x, z) => waves.HeightAt(x, z), 0, 0, 0, raft);
            check(crest.Heave > 0.75 && trough.Heave < -0.75, "a travelling wave lifts the raft at its crest and lowers it in its trough");
            var flat = BoatWaveMath.Sample((x, z) => 0, 0, 0, 0, sailboat);
            check(flat.Heave == 0 && flat.Pitch == 0 && flat.Roll == 0, "calm water gives no invented rocking");
            var bad = BoatWaveMath.Sample((x, z) => double.NaN, 0, 0, 0, raft);
            check(bad.Heave == 0 && bad.Pitch == 0 && bad.Roll == 0, "invalid samples cannot poison boat transforms");
            var steep = BoatWaveMath.Sample((x, z) => x * 100 + z * 100, 0, 0, 0, raft);
            check(Math.Abs(steep.Roll) <= Math.PI / 6 && Math.Abs(steep.Pitch) <= Math.PI / 6, "steep waves bound pitch and roll to 30 degrees");
            check(BoatWaveMath.SailDrive(0, 0, 0) == 0 && BoatWaveMath.SailDrive(double.NaN, 0, 0) == 0, "calm or invalid wind gives no sail thrust");
            check(BoatWaveMath.SailDrive(0, -0.3, 0) == 0 && BoatWaveMath.SailDrive(Math.Sin(39 * Math.PI / 180) * .3, -Math.Cos(39 * Math.PI / 180) * .3, 0) == 0,
                "headwind and the 40-degree no-go cone give no sail thrust");
            check(BoatWaveMath.SailDrive(.3, 0, Math.PI * 1.25) > .25, "45-degree close-hauled sailing has useful drive");
            check(Math.Abs(BoatWaveMath.SailDrive(0.3, 0, 0) - 1) < 1e-12 && BoatWaveMath.SailDrive(0, 0.3, 0) > 0,
                "a medium beam wind gives native sail speed, a following wind still drives her");
            check(BoatWaveMath.SailDrive(0.15, 0, 0) < BoatWaveMath.SailDrive(0.3, 0, 0)
                && BoatWaveMath.SailDrive(10, 0, 0) == 2, "wind strength changes drive with a bounded storm speed");
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6;
                check(Math.Abs(BoatWaveMath.SailDrive(0.3 * Math.Cos(a), -0.3 * Math.Sin(a), a) - 1) < 1e-12,
                    "sailing response rotates with wind and heading " + i);
            }
            check(BoatWaveMath.SailTrim(0, .3, 0, 0) == 0, "running sail stays athwart the hull");
            check(BoatWaveMath.SailTrim(.3, 0, 0, 0) == 45 && BoatWaveMath.SailTrim(-.3, 0, 0, 0) == -45,
                "beam-wind yard trim mirrors on both tacks");
            check(BoatWaveMath.SailTrim(.1, -.3, 0, 0) == 65 && BoatWaveMath.SailTrim(-.1, -.3, 0, 0) == -65,
                "close-hauled yard braces to its limit");
            check(BoatWaveMath.SailTrim(0, -.3, 0, -1) == -65 && BoatWaveMath.SailTrim(0, 0, 0, 0) == 0,
                "the yard holds its tack through headwind and centres in calm");
            double fill = 0;
            for (int i = 0; i < 600; i++) fill = BoatWaveMath.FloodStep(fill, 0, .2);
            check(fill == 0, "calm water never invents flooding");
            for (int i = 0; i < 600; i++) fill = BoatWaveMath.FloodStep(fill, .75, .2);
            check(fill == 1 && BoatWaveMath.FloodDensity(fill) > 1000, "sustained green water fills a boat and defeats water buoyancy");
            check(BoatWaveMath.FloodDensity(0) == 100 && BoatWaveMath.FloodDraft(0) == 0
                && BoatWaveMath.FloodDraft(.5) > 0, "dry boats keep native buoyancy; shipped water deepens their draft");
        }
    }
}
