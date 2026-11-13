using System;
using System.IO;
using System.Text.RegularExpressions;
using Sea;

namespace SeaTests
{
    /// <summary>
    /// (EA-0050, SEA-D73) Flowing water drawn alike whichever way it flows: vanilla's flow vectors for the eight labels and
    /// still water, what Sea's shader makes of them, and the edit going into the game's own water fragment shader.
    /// </summary>
    static class FlowLookChecks
    {
        static readonly (string Label, int X, int Z)[] Labels =
        {
            ("n", 0, -1), ("ne", 1, -1), ("e", 1, 0), ("se", 1, 1), ("s", 0, 1), ("sw", -1, 1), ("w", -1, 0), ("nw", -1, -1)
        };

        public static void Run(Action<bool, string> check)
        {
            Console.WriteLine("flowing water drawn alike in every direction (EA-0050)");

            // vanilla: water's push is 0.0015 per axis (water.json), rapid water's 0.004 with flowSpeed 2 (rapidwater.json)
            double vMin = double.MaxValue, vMax = 0, sMin = double.MaxValue, sMax = 0, aMin = double.MaxValue, aMax = 0;
            double rMin = double.MaxValue, rMax = 0;
            foreach (var (label, x, z) in Labels)
            {
                var f = FlowLook.TesselatorFlow(x, z, 0.0015);
                double vanilla = Math.Sqrt(f.X * f.X + f.Z * f.Z);
                var e = FlowLook.EvenFlow(f.X, f.Z);
                double even = Math.Sqrt(e.X * e.X + e.Z * e.Z);
                double alpha = FlowLook.FlowAlpha(even, 1);
                vMin = Math.Min(vMin, vanilla); vMax = Math.Max(vMax, vanilla);
                sMin = Math.Min(sMin, even); sMax = Math.Max(sMax, even);
                aMin = Math.Min(aMin, alpha); aMax = Math.Max(aMax, alpha);
                // the direction is kept: the evened vector points the same way
                check(Math.Abs(Math.Atan2(e.Z, e.X) - Math.Atan2(f.Z, f.X)) < 1e-12, $"label {label}: Sea's flow points the same way as vanilla's");
                var r = FlowLook.TesselatorFlow(x, z, 0.004, 2);
                var re = FlowLook.EvenFlow(r.X, r.Z);
                double rs = Math.Sqrt(re.X * re.X + re.Z * re.Z);
                rMin = Math.Min(rMin, rs); rMax = Math.Max(rMax, rs);
            }
            check(Math.Abs(vMin - 1) < 1e-9 && Math.Abs(vMax - 2) < 1e-9 && Math.Abs(FlowLook.VanillaFlowAlpha(vMax) / FlowLook.VanillaFlowAlpha(vMin) - 2) < 1e-9,
                $"vanilla: a straight label's flow is {vMin:0.00} long, a diagonal one's {vMax:0.00}, so a diagonal cell's water is twice as opaque (the patches)");
            check(Math.Abs(sMin - 1) < 1e-9 && Math.Abs(sMax - 1) < 1e-9, $"Sea: every label flows at the same speed ({sMin:0.000} to {sMax:0.000})");
            check(Math.Abs(aMin - 1) < 1e-9 && Math.Abs(aMax - 1) < 1e-9 && FlowLook.FlowAlpha(0, 1) == 1,
                $"Sea: on the surface every label's water is as opaque as still water ({aMin:0.000} to {aMax:0.000}, still 1)");
            check(Math.Abs(rMin - rMax) < 1e-9 && FlowLook.FlowAlpha(rMin, 1) > 5,
                $"Sea: rapid water flows alike in every direction too ({rMin:0.00}) and stays opaque white water (× {FlowLook.FlowAlpha(rMin, 1):0.0}, vanilla × {FlowLook.VanillaFlowAlpha(rMin):0.0} straight)");

            // continuous: no step from still water as the flow starts, and none between neighbouring directions
            double worst = 0;
            for (int i = 0; i <= 1000; i++)
            {
                double s = i / 1000.0 * 6;
                worst = Math.Max(worst, Math.Abs(FlowLook.FlowAlpha(s + 1e-6, 1) - FlowLook.FlowAlpha(s, 1)));
            }
            check(worst < 1e-5 && Math.Abs(FlowLook.FlowAlpha(1e-9, 1) - 1) < 1e-9, $"Sea: the surface's opacity grows continuously with the speed from still water's (largest step {worst:e1})");
            double turn = 0;
            for (int i = 0; i < 3600; i++)
            {
                double a = i * Math.PI / 1800, b = (i + 1) * Math.PI / 1800;
                var p = FlowLook.EvenFlow(Math.Cos(a), Math.Sin(a));
                var q = FlowLook.EvenFlow(Math.Cos(b), Math.Sin(b));
                turn = Math.Max(turn, Math.Abs(Math.Sqrt(p.X * p.X + p.Z * p.Z) - Math.Sqrt(q.X * q.X + q.Z * q.Z)));
            }
            check(turn < 0.0018, $"Sea: the evened speed changes continuously with direction (largest step per 0.1° {turn:e1}, at most one per radian)");
            check(FlowLook.FlowAlpha(0.65, 0) == FlowLook.VanillaFlowAlpha(0.65) && FlowLook.FlowAlpha(5.33, -1) == FlowLook.VanillaFlowAlpha(5.33),
                "Sea: side and bottom faces keep vanilla's opacity (falls, the sides of steps)");

            // the edit in the game's own shader
            string vs = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (string.IsNullOrEmpty(vs)) vs = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "Vintagestory");
            string path = Path.Combine(vs, "assets", "game", "shaders", "chunkliquid.fsh");
            if (!File.Exists(path)) { check(false, "the game's chunkliquid.fsh is at " + path); return; }
            string vanillaCode = File.ReadAllText(path);
            string code = FlowLook.Inject(vanillaCode);
            check(code != null, "Sea's even flow goes into the game's water fragment shader");
            if (code == null) return;
            int main = code.IndexOf("void main(", StringComparison.Ordinal);
            string body = code.Substring(code.IndexOf('{', main) + 1);
            int uses = Regex.Matches(body, @"\b" + FlowLook.FlowInput + @"\b").Count;
            int vanillaUses = Regex.Matches(vanillaCode.Substring(vanillaCode.IndexOf("void main(", StringComparison.Ordinal)), @"\b" + FlowLook.FlowInput + @"\b").Count;
            check(uses == 1 && body.TrimStart().StartsWith("vec2 sea_Flow = sea_EvenFlow(flowVectorf);") && Regex.Matches(body, @"\bsea_Flow\b").Count == vanillaUses + 1,
                $"in main every one of vanilla's {vanillaUses} flow uses reads Sea's evened flow, and only Sea's line reads the input");
            check(!body.Contains(FlowLook.AlphaLine) && body.Contains("texColor.a *= sea_FlowAlpha(flowSpeed, fragNormal.y);"), "vanilla's flowing-water opacity line is replaced by Sea's");
            check(code.IndexOf(FlowLook.Marker + "(vec2 v)", StringComparison.Ordinal) < main && FlowLook.Inject(code) == null, "the functions go before main, and the edit goes in once");
            check(FlowLook.Inject(vanillaCode.Replace(FlowLook.AlphaLine, "texColor.a *= 1.3 * flowSpeed;")) == null && FlowLook.Inject(null) == null,
                "a game update that changes the opacity line leaves vanilla's flowing water alone");
            // with the other edits Sea makes to the same shader (the order the game loads them in)
            string celestial = CelestialShader.Fragment(vanillaCode);
            string both = celestial == null ? null : FlowLook.Inject(celestial);
            string all = both == null ? null : WaterShader.InjectFragment(both);
            check(all != null && all.Contains(CelestialShader.Marker) && all.Contains(WaterShader.DryMarker) && all.Contains(FlowLook.Marker),
                "it goes in beside the sky reflections and the dry-hull cut");
        }
    }
}
