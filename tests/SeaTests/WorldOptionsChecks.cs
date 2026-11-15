using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProtoBuf;
using Sea;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace SeaTests
{
    static class WorldOptionsChecks
    {
        [ProtoContract]
        public class RetiredWakeConfig
        {
            [ProtoMember(9)] public float WaveScale = .4f;
            [ProtoMember(15)] public float StormHeightScale = .7f;
            [ProtoMember(16)] public bool WakeEnabled = true;
            [ProtoMember(17)] public float WakeStrength = 2;
        }
        public static void Run(Action<bool, string> check)
        {
            Console.WriteLine("per-world Sea storm option; wakes retired in 0.5.1");
            var fallback = new SeaConfig { StormHeightScale = .7f, WaveScale = .4f }.Checked();
            var empty = new TreeAttribute();
            var old = fallback.ForWorld(empty);
            check(old.StormHeightScale == .7f && old.WaveScale == .4f && fallback.ForWorld(null).StormHeightScale == .7f
                && !empty.HasAttribute("seaStormHeight"), "old worlds retain sea.json fallback without adding world keys");
            var legacy = SerializerUtil.Deserialize<SeaConfig>(SerializerUtil.Serialize(new RetiredWakeConfig())).Checked();
            check(legacy.WaveScale == .4f && legacy.StormHeightScale == .7f
                && typeof(SeaConfig).GetField("WakeEnabled") == null && typeof(SeaConfig).GetField("WakeStrength") == null,
                "legacy packets skip retired wake fields 16/17 and preserve wave/storm values");
            var world = new TreeAttribute(); world.SetBool("seaWakes", true);
            check(fallback.ForWorld(world).StormHeightScale == .7f && world.GetBool("seaWakes"),
                "legacy wake-on world key is ignored without rewriting the save");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                foreach (var pair in new[] { ("1", 1f), ("1.5", 1.5f), ("2.2", 2.2f) })
                {
                    world.SetString("seaStormHeight", pair.Item1);
                    var effective = fallback.ForWorld(world);
                    var packet = new SeaParamsPacket { Config = effective };
                    var client = SerializerUtil.Deserialize<SeaParamsPacket>(SerializerUtil.Serialize(packet)).Config.Checked();
                    check(effective.StormHeightScale == pair.Item2 && client.StormHeightScale == pair.Item2
                        && client.WaveScale == fallback.WaveScale,
                        $"world height {pair.Item1}: override and packet agree under comma-decimal culture");
                }
            }
            finally { CultureInfo.CurrentCulture = culture; }
            check(fallback.StormHeightScale == .7f, "world overrides never mutate the sea.json fallback");
            foreach (string invalid in new[] { "", "bogus", "NaN", "Infinity", "-1", "0", "4", "1,5" })
            {
                world.SetString("seaStormHeight", invalid);
                check(fallback.ForWorld(world).StormHeightScale == .7f, $"invalid world height '{invalid}' retains fallback");
            }
            world.SetInt("seaStormHeight", 2);
            check(new SeaConfig { StormHeightScale = 1.5f }.ForWorld(world).StormHeightScale == 1.5f,
                "wrong attribute type retains storm setting");
            using var declaration = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "worldconfig.json")));
            using var english = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "game", "lang", "worldconfig-en.json")));
            var root = declaration.RootElement;
            var attrs = root.GetProperty("worldConfigAttributes").EnumerateArray().ToArray();
            check(root.GetProperty("playStyles").GetArrayLength() == 0 && attrs.Length == 1
                && attrs[0].GetProperty("code").GetString() == "seaStormHeight"
                && attrs[0].GetProperty("category").GetString() == "earth" && !attrs[0].GetProperty("onlyDuringWorldCreate").GetBoolean(),
                "only the storm-height setting is declared, changeable after creation");
            // (SEA-D80) off the world-creation screen: vanilla still writes the default ("json") into every new world
            // (WorldConfig.allDefaultValues) and /worldconfig still sets it
            check(attrs[0].TryGetProperty("onCustomizeScreen", out var shown) && shown.ValueKind == JsonValueKind.False,
                "storm height is not on the world-creation screen (onCustomizeScreen false); set with /worldconfig");
            var storm = attrs[0];
            // (SE-0066) the default is the server's sea.json; the three heights stay as they were
            check(storm.GetProperty("dataType").GetString() == "DropDown" && storm.GetProperty("default").GetString() == "json"
                && storm.GetProperty("values").EnumerateArray().Select(v => v.GetString()).SequenceEqual(new[] { "json", "1", "1.5", "2.2" }),
                "storm choices: the server's sea.json (the default), 1, 1.5, 2.2");
            var defaults = new TreeAttribute(); defaults.SetString("seaStormHeight", storm.GetProperty("default").GetString());
            check(fallback.ForWorld(defaults).StormHeightScale == fallback.StormHeightScale, "a new world's default takes sea.json's StormHeightScale (SE-0066)");
            bool translated = english.RootElement.TryGetProperty("worldconfig-category-earth", out _)
                && english.RootElement.TryGetProperty("worldattribute-seaStormHeight", out _)
                && english.RootElement.TryGetProperty("worldattribute-seaStormHeight-desc", out _)
                && storm.GetProperty("names").EnumerateArray().All(n => english.RootElement.TryGetProperty("worldconfig-seaStormHeight-" + n.GetString(), out _));
            check(translated && !english.RootElement.EnumerateObject().Any(v => v.Name.Contains("seaWakes")),
                "remaining setting is translated and wake controls are absent");
        }
    }
}
