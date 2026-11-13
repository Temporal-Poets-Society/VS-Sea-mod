using System;
using System.Text.RegularExpressions;

namespace Sea
{
    /// <summary>
    /// (EA-0050, SEA-D73) Flowing water reads as one water body whichever way it flows. Vanilla's water fragment shader
    /// (chunkliquid.fsh) takes the flow vector the chunk tesselator gives each liquid face: the label's direction
    /// (<c>Cardinal.Normali</c>, both components ±1 for a diagonal) times half the push speed over 0.00075, and the push of a
    /// diagonal label is √2 times a straight one's. So a diagonal label's vector is exactly twice as long as a straight one's
    /// (ordinary water: 2 against 1), a still source's is 0, and the shader multiplies the texture's opacity by
    /// <c>1.2 × length</c> for any flow: still 1, straight 1.2, diagonal 2.4. Earth labels the top source of every river
    /// column with its flow (pool layout 11), so a river drew as blocky patches of lighter and darker water wherever the
    /// label turned between n and nw (the Daugava, 13 November 2026), and its still cells along the banks as a third shade.
    ///
    /// Sea's edit, only in the drawn water's fragment stage:
    ///  - <c>sea_EvenFlow</c>: the flow vector is measured along its label's own axis, so every direction has the same speed
    ///    (a diagonal vector is halved, a straight one kept). The texture and vanilla's foam noise then drift with the
    ///    current at one speed whichever way it runs: the cue of the current stays, the same everywhere.
    ///  - <c>sea_FlowAlpha</c>: on up faces the opacity grows continuously with the flow's speed from still water's,
    ///    max(1, 1.2 × speed − 0.2): ordinary flowing water (speed 1) is as clear as a lake, still and labelled cells
    ///    match, and rapid water (speed 5.3) stays the white, opaque water vanilla draws (6.2 against 6.4; both saturate).
    ///    Side and bottom faces (falls, the sides of steps) keep vanilla's 1.2 × speed.
    /// Nothing else in vanilla's shader changes; if its anchors move in a game update, the edit stays out (vanilla water).
    /// </summary>
    public static class FlowLook
    {
        public const string Marker = "sea_EvenFlow";

        /// <summary>The input every flow use in vanilla's fragment shader reads; Sea's evened vector takes its place in main.</summary>
        public const string FlowInput = "flowVectorf";

        /// <summary>Vanilla's opacity for flowing water, the line Sea's continuous opacity replaces.</summary>
        public const string AlphaLine = "texColor.a *= 1.2 * flowSpeed;";

        const string MainAnchor = "void main(";

        /// <summary>The GLSL functions Sea adds before main (public for the GPU check).</summary>
        public const string Functions = @"
// (EA-0050) Sea: a flow vector measured along its label's own axis, so a diagonal label (twice a straight one's length in
// vanilla) flows as fast as a straight one; and flowing water's opacity growing continuously from still water's.
vec2 sea_EvenFlow(vec2 v) {
	vec2 a = abs(v);
	float s = a.x + a.y;
	return s > 0.000001 ? v * (max(a.x, a.y) / s) : v;
}

float sea_FlowAlpha(float speed, float up) {
	return up > 0.5 ? max(1.0, 1.2 * speed - 0.2) : 1.2 * speed;
}

";

        static readonly Regex FlowWord = new(@"\b" + FlowInput + @"\b", RegexOptions.CultureInvariant);

        /// <summary>
        /// The water fragment shader with Sea's even flow, or null when it is already in or vanilla's anchors weren't found
        /// (main, the flow input used in main, the opacity line, the face normal).
        /// </summary>
        public static string Inject(string code)
        {
            if (code == null || code.Contains(Marker)) return null;
            int m = code.IndexOf(MainAnchor, StringComparison.Ordinal);
            if (m < 0) return null;
            int brace = code.IndexOf('{', m);
            if (brace < 0) return null;
            string head = code.Substring(0, brace + 1), body = code.Substring(brace + 1);
            if (!body.Contains(AlphaLine) || !FlowWord.IsMatch(body) || !code.Contains("fragNormal")) return null;
            body = FlowWord.Replace(body, "sea_Flow");
            body = body.Replace(AlphaLine, "texColor.a *= sea_FlowAlpha(flowSpeed, fragNormal.y);");
            code = head + "\n\tvec2 sea_Flow = " + Marker + "(" + FlowInput + ");" + body;
            return code.Insert(m, Functions);
        }

        /// <summary>The shader's <c>sea_EvenFlow</c> (for the tests).</summary>
        public static (double X, double Z) EvenFlow(double x, double z)
        {
            double ax = Math.Abs(x), az = Math.Abs(z), s = ax + az;
            if (s <= 0.000001) return (x, z);
            double k = Math.Max(ax, az) / s;
            return (x * k, z * k);
        }

        /// <summary>The shader's <c>sea_FlowAlpha</c>: what the texture's opacity is multiplied by for flowing water.</summary>
        public static double FlowAlpha(double speed, double up) => up > 0.5 ? Math.Max(1.0, 1.2 * speed - 0.2) : 1.2 * speed;

        /// <summary>Vanilla's: 1.2 × the flow vector's length.</summary>
        public static double VanillaFlowAlpha(double speed) => 1.2 * speed;

        /// <summary>
        /// The flow vector vanilla's chunk tesselator gives a level-7 liquid's up face (LiquidTesselator): the label's
        /// Cardinal normal (components -1, 0, 1, times the block's flowSpeed attribute) / 2 × push speed / 0.00075.
        /// </summary>
        public static (double X, double Z) TesselatorFlow(int nx, int nz, double push, int flowSpeed = 1)
        {
            double pushLength = push * Math.Sqrt(nx * nx + nz * nz);
            double k = pushLength / 0.00075;
            return (nx * flowSpeed / 2.0 * k, nz * flowSpeed / 2.0 * k);
        }
    }
}
