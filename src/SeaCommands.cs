using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Sea
{
    /// <summary>
    /// /sea                 wind and waves where you stand (what a sailor can see and feel), and where the wind comes from
    /// /sea setwind &lt;from&gt; &lt;strength&gt; [hours] [built]   fix Sea's wind everywhere (admins, for testing and events)
    /// /sea clearwind       back to the provider's wind (or vanilla's)
    /// (0.4.0) /sea storms went with Sea's weather; storms are Earth's now.
    /// </summary>
    public static class SeaCommands
    {
        public static void Register(ICoreServerAPI api, SeaSystem sys)
        {
            var parsers = api.ChatCommands.Parsers;
            api.ChatCommands.Create("sea")
                .WithDescription("Wind and waves where you are")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(args => TextCommandResult.Success(Report(sys, args.Caller.Player.Entity.Pos)))
                .BeginSubCommand("setwind")
                    .WithDescription("Fix the wind everywhere: from (N, SW, 270…), strength (0.15 light breeze, 0.3 medium, 0.6 strong, 1 storm, max 1.5), game hours (0 = until /sea clearwind), 'built' = the sea is already up (otherwise it builds over ~3 game hours)")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(parsers.Word("from"), parsers.Double("strength"), parsers.OptionalDouble("hours", 0), parsers.OptionalWordRange("sea", "built"))
                    .HandleWith(args =>
                    {
                        if (sys.Model == null) return TextCommandResult.Error("Sea isn't ready yet.");
                        double? from = Compass.Parse(args[0] as string);
                        if (from == null) return TextCommandResult.Error("Direction must be a compass point (N, NNE, NE … NW) or degrees.");
                        if (!double.IsFinite((double)args[1]) || !double.IsFinite((double)args[2])) return TextCommandResult.Error("Strength and hours must be numbers.");
                        double strength = Math.Clamp((double)args[1], 0, WindModel.MaxStrength);
                        double hours = Math.Max(0, (double)args[2]);
                        // (0.4.2) from a moment ahead, so the server and every client change at the same wave sample
                        double now = sys.NextActivation;
                        // "built": as if it had been blowing for a day already, so the sea is fully up at once
                        bool built = (args[3] as string) == "built";
                        sys.ChangeOverride(new WindOverride { FromDegrees = from.Value, Strength = strength, StartHours = built ? now - 24 : now, UntilHours = hours > 0 ? now + hours : 0 });
                        return TextCommandResult.Success($"Wind fixed: {strength:0.00} from the {Compass.Name(from.Value)} ({from.Value:0}°), Beaufort {Beaufort.Force(strength)}"
                            + (hours > 0 ? $", for {hours:0.#} game hours" : ", until /sea clearwind")
                            + (built ? ". The sea is already up." : ". The sea builds up over ~3 game hours (add 'built' to skip that)."));
                    })
                .EndSubCommand()
                .BeginSubCommand("clearwind")
                    .WithDescription("Let the weather (the wind provider, or vanilla) decide the wind again (admins)")
                    .RequiresPrivilege(Privilege.controlserver)
                    .HandleWith(args =>
                    {
                        if (sys.Model?.Override == null) return TextCommandResult.Success("The wind isn't fixed.");
                        // (0.4.2) the sea the fixed wind raised dies down over the next hours instead of vanishing
                        sys.ChangeOverride(null);
                        return TextCommandResult.Success($"The weather decides the wind again ({WindSource(sys)}); the sea it raised dies down over the next game hours.");
                    })
                .EndSubCommand();
        }

        /// <summary>.sea on a client: what Sea is doing there (for checking that the client side works).</summary>
        public static void RegisterClient(ICoreClientAPI capi, SeaSystem sys)
        {
            var parsers = capi.ChatCommands.Parsers;
            capi.ChatCommands.Create("sea")
                .WithDescription("What Sea is doing on your client: wind, waves, water shader, patches")
                .HandleWith(args => TextCommandResult.Success(ClientReport(capi, sys)))
                // (0.4.3) tuning by eye: this client only, at once, until you leave the world (sea.json is unchanged)
                .BeginSubCommand("night")
                    .WithDescription("Night visibility on your client now, 0-4 (sea.json's NightVisibility otherwise; 1 = clear moonless 0.10, high full moon 0.40, 1.5 = the 0.4.4 level; 0 = the game's own night and twilight)")
                    .WithArgs(parsers.OptionalWord("value"))
                    .HandleWith(args => Tune(sys, args[0] as string, "night", c => c.NightOverride, (c, v) => c.NightOverride = v, c => c.NightStrength))
                .EndSubCommand()
                .BeginSubCommand("sky")
                    .WithDescription("Sky reflection on the water (Fresnel) on your client now, 0-4 (sea.json's SkyReflections otherwise; 1 = physical)")
                    .WithArgs(parsers.OptionalWord("value"))
                    .HandleWith(args => Tune(sys, args[0] as string, "sky", c => c.SkyOverride, (c, v) => c.SkyOverride = v, c => c.SkyStrength))
                .EndSubCommand()
                .BeginSubCommand("glint")
                    .WithDescription("Sun glitter and moon trail on your client now, 0-4 (sea.json's CelestialReflections otherwise; 0 = vanilla's sun highlight)")
                    .WithArgs(parsers.OptionalWord("value"))
                    .HandleWith(args => Tune(sys, args[0] as string, "glint", c => c.GlintOverride, (c, v) => c.GlintOverride = v, c => c.GlintStrength))
                .EndSubCommand()
                .BeginSubCommand("foam")
                    .WithDescription("Breaking-wave foam on this client: all, wind, off")
                    .WithArgs(parsers.OptionalWord("view"))
                    .HandleWith(args =>
                    {
                        string value=args[0] as string;
                        if (!string.IsNullOrEmpty(value)) {
                            if (!Enum.TryParse<FoamView>(value,true,out var view) || !Enum.IsDefined(view) || char.IsDigit(value[0]))
                                return TextCommandResult.Error("Use .sea foam all|wind|off");
                            sys.FoamView=view;
                        }
                        return TextCommandResult.Success(sys.FoamStatus);
                    })
                .EndSubCommand()
                .BeginSubCommand("reset")
                    .WithDescription("Back to sea.json's night, sky and glint strengths")
                    .HandleWith(args =>
                    {
                        var c = sys.Celestial;
                        if (c == null) return TextCommandResult.Error("Sea's sky presentation isn't running on this client.");
                        c.NightOverride = c.SkyOverride = c.GlintOverride = null;
                        sys.FoamView = FoamView.All;
                        return TextCommandResult.Success($"Back to sea.json: night {c.NightStrength:0.##}, sky {c.SkyStrength:0.##}, glint {c.GlintStrength:0.##}.");
                    })
                .EndSubCommand();
        }

        /// <summary>Show or set one of the client's live strengths. No value: what it is now. "default": sea.json's again.</summary>
        static TextCommandResult Tune(SeaSystem sys, string value, string name, System.Func<CelestialRenderer, float?> get, System.Action<CelestialRenderer, float?> set, System.Func<CelestialRenderer, float> now)
        {
            var c = sys.Celestial;
            if (c == null) return TextCommandResult.Error("Sea's sky presentation isn't running on this client.");
            if (string.IsNullOrEmpty(value))
                return TextCommandResult.Success($"{name}: {now(c):0.##}{(get(c).HasValue ? " (set here; .sea " + name + " default for sea.json's)" : " (sea.json)")}.");
            if (value == "default")
            {
                set(c, null);
                return TextCommandResult.Success($"{name}: {now(c):0.##} (sea.json).");
            }
            if (!float.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) || !float.IsFinite(v))
                return TextCommandResult.Error($"Give a number from 0 to 4, or 'default': .sea {name} 1.5");
            set(c, Math.Clamp(v, 0, 4));
            return TextCommandResult.Success($"{name}: {now(c):0.##} on this client until you leave the world (sea.json unchanged). .sea shows the result.");
        }

        static string ClientReport(ICoreClientAPI capi, SeaSystem sys)
        {
            var sb = new StringBuilder();
            var m = sys.Model;
            if (m == null) return $"Sea: not ready on this client (the server's parameters haven't arrived); wind from {WindSource(sys)}.";
            var ov = sys.FixedWindNow;
            sb.Append($"Sea on this client: wind from {WindSource(sys)}{(ov != null ? $", fixed by an admin: {ov.Strength:0.00} from {Compass.Name(ov.FromDegrees)}" : "")}.");
            var p = sys.WindProvider;
            if (p != null && p.Failures > 0) sb.Append($" {p.Failures} of its answers were refused (calm instead); the last: {p.LastError}.");
            var w = sys.LocalWind;
            sb.Append($"\nWind here: {w} (Beaufort {Beaufort.Force(w.Strength)}); vanilla sees {GlobalConstants.CurrentWindSpeedClient.X:0.00} (surface {GlobalConstants.CurrentSurfaceWindSpeedClient.X:0.00}).");
            var cw = sys.CameraWaves;
            var t = cw.Sea;
            sb.Append($"\nSea state here: {t.Height:0.00} blocks toward the {Compass.Name(t.ToDegrees)}, {t.Wavelength:0} blocks long, every {t.Period:0.0} s (builds up over game hours).");
            var ep = capi.World.Player.Entity;
            sb.Append($"\nDrawn waves: {cw.SignificantHeight:0.00} blocks from {cw.Count} wave trains ({sys.DrawnWaveCount} in the shader), here × {sys.WaveFactorAt(ep.Pos.X, ep.Pos.Z):0.00} (open sea 1, sheltered 0.2, inland 0).");
            sb.Append($"\nWave clock: {sys.WaveTime:0.0} s, {(sys.WaveClockSynced ? $"{sys.WaveClockError * 1000:+0;-0} ms from the server's" : "not heard from the server yet")}.");
            sb.Append($"\nWater shader: Sea code {(WaterRenderer.Injected ? "in" : "MISSING")}, Sea waves {(WaterRenderer.WavesInjected ? "in" : "MISSING (vanilla waves)")}, hulls kept dry this frame: {sys.DrawnHullCount}.");
            var eye = capi.World.Player.Entity.CameraPos.AddCopy(capi.World.Player.Entity.LocalEyePos);
            if (Hooks.WaterCodeLevel > 0) sb.Append($"\nYour graphics driver didn't take all of Sea's water code: {(Hooks.WaterCodeLevel == 1 ? "no dry-hull cut" : "vanilla water")} (see the client log).");
            sb.Append($"\nDry hulls: water cut out {(Hooks.DryHullsInjected ? "in the shader" : "MISSING")}, {sys.DrawnDryHullCount} drawn this frame, {sys.DrySpaceSources} mod(s) with dry places; your eyes are {(sys.IsDry(eye.X, eye.Y, eye.Z) ? "in a dry place" : "not in a dry place")}; {DryParticles.Dropped} water specks, splashes and spray kept out of dry places so far.");
            sb.Append($"\nPatches in: {string.Join(", ", Hooks.Active)}.");
            sb.Append("\n" + sys.CelestialStatus);
            sb.Append("\n" + sys.FoamStatus);
            return sb.ToString();
        }

        static string LatText(double deg) => $"{Math.Abs(deg):0}°{(deg >= 0 ? "N" : "S")}";

        /// <summary>(0.4.0) Who decides the wind on this side: the provider's name, or vanilla with the sea's steady wind.</summary>
        static string WindSource(SeaSystem sys)
        {
            string name = sys.WindProviderName;
            if (name != null) return name;
            var m = sys.Model;
            return m == null ? "vanilla (no wind provider such as Earth; no direction)" : $"vanilla (no wind provider such as Earth; no direction; the sea follows a steady {m.SteadySeaWind})";
        }

        public static string Report(SeaSystem sys, EntityPos pos)
        {
            var m = sys.Model;
            double h = sys.Hours;
            var w = sys.WindAt(pos.X, pos.Z);
            var sea = sys.SeaAt(pos.X, pos.Z);
            var sb = new StringBuilder();
            double s = w.Strength;
            if (s < 0.02) sb.Append("Wind: calm.");
            else sb.Append($"Wind: from the {Compass.Name(w.FromDegrees)} ({w.FromDegrees:0}°), {s:0.00}: Beaufort {Beaufort.Force(s)}, {Beaufort.Name(s)}.");
            double here = sys.WaveFactorAt(pos.X, pos.Z);
            sb.Append($"\nSea: waves {sea.Height:0.0} blocks out at sea{(here < 0.99 ? $" ({sea.Height * here:0.0} here, sheltered)" : "")}, running toward the {Compass.Name(sea.ToDegrees)}, every {sea.Period:0} s.");
            if (m != null)
            {
                sb.Append($"\nYou're at {LatText(m.Shape.LatitudeDeg(pos.Z))}. Wind from {WindSource(sys)}.");
                var ov = m.FixedAt(h);
                if (ov != null) sb.Append(ov.UntilHours > 0 ? $"\n(An admin fixed the wind for another {ov.UntilHours - h:0.#} game hours.)" : "\n(An admin fixed the wind.)");
            }
            else sb.Append($"\n(Sea isn't ready yet; wind from {WindSource(sys)}.)");
            return sb.ToString();
        }
    }
}
