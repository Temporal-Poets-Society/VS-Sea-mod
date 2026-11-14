extern alias SeaDll;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Common;
using Contracts = SeaDll::Sea.SeaContracts;

namespace SeaTests
{
    /// <summary>
    /// (SE-0044) The open sea contracts as other mods bind them: Earth, Ships and Sea Monsters look each role up by name and
    /// each member by key and exact delegate type, so a changed tuple or parameter compiles here and fails silently there.
    /// This publishes Sea's real roles (from the built Sea.dll) into a fake ObjectCache and pins every key and type, the
    /// header fields, and the claim and reclaim rules of the spec's section 3. A deliberate change of the contract changes
    /// this snapshot and the spec (docs/open-sea-contracts.md) together.
    /// </summary>
    static class ContractSnapshot
    {
        // key → the delegate or value type, as the contract spec gives it (docs/open-sea-contracts.md)
        static readonly Dictionary<string, Dictionary<string, string>> Expected = new()
        {
            ["seastate"] = new()
            {
                ["at"] = "Func<Double, Double, Double, ValueTuple<Double, Double, Double, Double, Double, Double, Double>>",
                ["shelter"] = "Func<Double, Double, Double>",
                ["current"] = "Func<Double, Double, ValueTuple<Double, Double>>",
                ["surf"] = "Func<Double, Double, ValueTuple<Double, Double, Double>>",
                ["lee"] = "Func<Double, Double, Double>",
            },
            ["surface"] = new()
            {
                ["seconds"] = "Func<Double>",
                ["clock"] = "String",
                ["trains"] = "Func<Double, Double, Double, Double[], Double[], Double[], Double[], Double[], Int32>",
                ["height"] = "Func<Double, Double, Double>",
                ["pose"] = "Func<Double, Double, Double, Double, Double, ValueTuple<Double, Double, Double, Double>>",
            },
            ["hulls"] = new()
            {
                ["register"] = "Action<String, Func<Double[][]>>",
                ["unregister"] = "Action<String>",
                ["registerDry"] = "Action<String, Func<Double, Double, Double, Boolean>>",
                ["unregisterDry"] = "Action<String>",
                ["isDry"] = "Func<Double, Double, Double, Boolean>",
                ["recordLength"] = "Int32",
            },
        };
        static readonly Dictionary<string, string> Header = new()
        {
            ["owner"] = "String", ["version"] = "Int32", ["priority"] = "Int32", ["revision"] = "Func<Int64>",
        };

        public static void Run(Action<bool, string> check)
        {
            var cache = new Dictionary<string, object>();
            var api = FakeApi.Make(cache, out var ticks);
            var log = FakeApi.Logger();
            var c = new Contracts(null, api, log);
            c.Publish();
            foreach (var role in Expected)
            {
                var d = cache.TryGetValue(Contracts.Prefix + role.Key, out var o) ? o as IDictionary<string, object> : null;
                check(d != null, "contracts: Sea publishes seacontracts:" + role.Key);
                if (d == null) continue;
                var want = new Dictionary<string, string>(Header);
                foreach (var kv in role.Value) want[kv.Key] = kv.Value;
                var got = d.ToDictionary(kv => kv.Key, kv => Name(kv.Value?.GetType()));
                var missing = want.Where(kv => !got.TryGetValue(kv.Key, out var t) || t != kv.Value).Select(kv => kv.Key + " (" + (got.TryGetValue(kv.Key, out var t2) ? t2 : "absent") + ")").ToList();
                var extra = got.Keys.Except(want.Keys).ToList();
                check(missing.Count == 0 && extra.Count == 0, "contracts: seacontracts:" + role.Key + " has exactly the spec's members and types"
                    + (missing.Count > 0 ? "; differs: " + string.Join(", ", missing) : "") + (extra.Count > 0 ? "; not in the spec: " + string.Join(", ", extra) : ""));
                check(d["owner"] as string == "sea" && d["version"] is 1 && d["priority"] is 50, "contracts: " + role.Key + " is owner sea, version 1, priority 50");
            }
            check(cache.TryGetValue(Contracts.Prefix + "hulls", out var h) && ((IDictionary<string, object>)h)["recordLength"] is 179,
                "contracts: hulls.recordLength is 179 (the detailed record with dry tops)");
            c.Dispose();
            check(!cache.Keys.Any(k => k.StartsWith(Contracts.Prefix)), "contracts: Dispose withdraws Sea's own entries");

            // section 3: a holder at the same or a higher priority keeps the role; a lower one gives way; a withdrawn role is reclaimed
            cache.Clear();
            var other = new Dictionary<string, object> { ["owner"] = "othermod", ["version"] = 1, ["priority"] = 50 };
            var lower = new Dictionary<string, object> { ["owner"] = "lowmod", ["version"] = 1, ["priority"] = 0 };
            cache[Contracts.Prefix + "surface"] = other;
            cache[Contracts.Prefix + "hulls"] = lower;
            var c2 = new Contracts(null, api, log);
            c2.Publish();
            check(ReferenceEquals(cache[Contracts.Prefix + "surface"], other), "contracts: a mod holding surface at priority 50 keeps it");
            check(Owner(cache, "hulls") == "sea", "contracts: a mod holding hulls at priority 0 gives way to Sea's 50");
            cache.Remove(Contracts.Prefix + "surface");
            foreach (var tick in ticks.ToArray()) tick(1f);
            check(Owner(cache, "surface") == "sea", "contracts: a withdrawn role is Sea's again within a tick (reclaim)");
            c2.Dispose();
            check(!cache.ContainsKey(Contracts.Prefix + "surface") && !cache.ContainsKey(Contracts.Prefix + "hulls"), "contracts: Dispose after a reclaim withdraws them too");
        }

        static string Owner(Dictionary<string, object> cache, string role)
            => cache.TryGetValue(Contracts.Prefix + role, out var o) && o is IDictionary<string, object> d && d.TryGetValue("owner", out var w) ? w as string : null;

        /// <summary>A type as the spec writes it: short names, generic arguments in brackets.</summary>
        static string Name(Type t)
        {
            if (t == null) return "null";
            if (t.IsArray) return Name(t.GetElementType()) + "[]";
            if (!t.IsGenericType) return t.Name;
            string n = t.Name.Substring(0, t.Name.IndexOf('`'));
            return n + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
        }
    }

    /// <summary>The few members of ICoreAPI the contracts use: the ObjectCache, a logger and a tick listener.</summary>
    public class FakeApi : DispatchProxy
    {
        Dictionary<string, object> cache;
        object events;

        public static ICoreAPI Make(Dictionary<string, object> cache, out List<Action<float>> ticks)
        {
            var api = Create<ICoreAPI, FakeApi>();
            var self = (FakeApi)(object)api;
            self.cache = cache;
            var ev = Create<IEventAPI, FakeEvents>();
            ticks = ((FakeEvents)(object)ev).Ticks;
            self.events = ev;
            return api;
        }

        public static ILogger Logger() => Create<ILogger, FakeLog>();

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "get_ObjectCache") return cache;
            if (method.Name == "get_Event") return events;
            return method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    public class FakeEvents : DispatchProxy
    {
        public readonly List<Action<float>> Ticks = new();
        readonly Dictionary<long, Action<float>> byId = new();
        long next;
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "RegisterGameTickListener" && args?.Length > 0 && args[0] is Action<float> tick) { Ticks.Add(tick); byId[++next] = tick; return next; }
            if (method.Name == "UnregisterGameTickListener" && args?.Length > 0 && args[0] is long id && byId.Remove(id, out var gone)) { Ticks.Remove(gone); return null; }
            return method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    public class FakeLog : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
            => method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
