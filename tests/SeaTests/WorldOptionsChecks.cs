using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sea;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace SeaTests
{
    static class WorldOptionsChecks
    {
        public static void Run(Action<bool, string> check)
        {
            Console.WriteLine("per-world Sea options (0.4.7-pre.12)");
            var fallback = new SeaConfig { StormHeightScale = 0.7f, WakeEnabled = false, WaveScale = 0.4f, WakeStrength = 0.6f }.Checked();
            var empty = new TreeAttribute();
            var old = fallback.ForWorld(empty);
            check(old.StormHeightScale == 0.7f && !old.WakeEnabled && old.WaveScale == 0.4f && old.WakeStrength == 0.6f
                && fallback.ForWorld(null).StormHeightScale == 0.7f && !empty.HasAttribute("seaStormHeight") && !empty.HasAttribute("seaWakes"),
                "old worlds retain non-default sea.json settings; absent world keys are not populated");
            var world = new TreeAttribute();
            world.SetString("seaStormHeight", "2.2");
            var heightOnly = fallback.ForWorld(world);
            world.RemoveAttribute("seaStormHeight");
            world.SetBool("seaWakes", true);
            var wakesOnly = fallback.ForWorld(world);
            check(heightOnly.StormHeightScale == 2.2f && !heightOnly.WakeEnabled
                && wakesOnly.StormHeightScale == 0.7f && wakesOnly.WakeEnabled,
                "each world key overrides independently; a missing key still uses sea.json");

            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                foreach (var pair in new[] { ("1", 1f), ("1.5", 1.5f), ("2.2", 2.2f) })
                {
                    world.SetString("seaStormHeight", pair.Item1);
                    foreach (bool wakes in new[] { false, true })
                    {
                        world.SetBool("seaWakes", wakes);
                        var effective = fallback.ForWorld(world);
                        var packet = new SeaParamsPacket { Config = effective };
                        var client = SerializerUtil.Deserialize<SeaParamsPacket>(SerializerUtil.Serialize(packet)).Config.Checked();
                        check(effective.StormHeightScale == pair.Item2 && effective.WakeEnabled == wakes
                            && client.StormHeightScale == pair.Item2 && client.WakeEnabled == wakes
                            && client.WaveScale == fallback.WaveScale && client.WakeStrength == fallback.WakeStrength,
                            $"world height {pair.Item1}, wakes {wakes}: override and existing packet agree, including false, under comma-decimal culture");
                    }
                }
            }
            finally { CultureInfo.CurrentCulture = culture; }

            check(fallback.StormHeightScale == 0.7f && !fallback.WakeEnabled,
                "world overrides never mutate the sea.json fallback used for other worlds");
            foreach (string invalid in new[] { "", "bogus", "NaN", "Infinity", "-1", "0", "4", "1,5" })
            {
                world.SetString("seaStormHeight", invalid);
                check(fallback.ForWorld(world).StormHeightScale == 0.7f, $"invalid world height '{invalid}' keeps sea.json fallback");
            }
            world.SetInt("seaStormHeight", 2);
            world.SetString("seaWakes", "false");
            var wrongTypes = new SeaConfig { StormHeightScale = 1.5f, WakeEnabled = true }.ForWorld(world);
            check(wrongTypes.StormHeightScale == 1.5f && wrongTypes.WakeEnabled,
                "wrong attribute types keep the existing settings");

            using var declaration = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "worldconfig.json")));
            using var english = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "game", "lang", "sea-worldconfig-en.json")));
            var root = declaration.RootElement;
            var attrs = root.GetProperty("worldConfigAttributes").EnumerateArray().ToArray();
            check(root.GetProperty("playStyles").GetArrayLength() == 0 && attrs.Length == 2
                && attrs.Select(a => a.GetProperty("code").GetString()).SequenceEqual(new[] { "seaStormHeight", "seaWakes" })
                && attrs.All(a => a.GetProperty("category").GetString() == "earth" && !a.GetProperty("onlyDuringWorldCreate").GetBoolean()),
                "declaration has an empty playStyles array and exactly two changeable Earth options");
            var storm = attrs[0];
            var wake = attrs[1];
            check(storm.GetProperty("dataType").GetString() == "DropDown" && storm.GetProperty("default").GetString() == "1"
                && storm.GetProperty("values").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "1", "1.5", "2.2" })
                && wake.GetProperty("dataType").GetString() == "Bool" && wake.GetProperty("default").GetString() == "true",
                "new-world defaults and dropdown choices match the agreed spec");
            var defaults = new TreeAttribute();
            defaults.SetString("seaStormHeight", storm.GetProperty("default").GetString());
            defaults.SetBool("seaWakes", bool.Parse(wake.GetProperty("default").GetString()));
            var newWorld = fallback.ForWorld(defaults);
            check(newWorld.StormHeightScale == 1 && newWorld.WakeEnabled,
                "new-world declared defaults override different sea.json settings");
            bool translated = english.RootElement.TryGetProperty("worldconfig-category-earth", out _);
            foreach (var attr in attrs)
            {
                string code = attr.GetProperty("code").GetString();
                translated &= english.RootElement.TryGetProperty("worldattribute-" + code, out _)
                    && english.RootElement.TryGetProperty("worldattribute-" + code + "-desc", out _);
                if (attr.TryGetProperty("names", out var names))
                    translated &= names.GetArrayLength() == attr.GetProperty("values").GetArrayLength()
                        && names.EnumerateArray().All(n => english.RootElement.TryGetProperty("worldconfig-" + code + "-" + n.GetString(), out _));
            }
            check(translated, "global English keys cover the Earth tab, both labels/descriptions and all dropdown value names");
        }
    }
}
