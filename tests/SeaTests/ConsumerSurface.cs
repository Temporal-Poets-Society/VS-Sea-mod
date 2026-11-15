extern alias SeaDll;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Contracts = SeaDll::Sea.SeaContracts;
using SeaSys = SeaDll::Sea.SeaSystem;
using Func3 = System.Func<double, double, double, (double X, double Z)>;
using Func4 = System.Func<double, double, double, bool, (double X, double Z)>;

namespace SeaTests
{
    /// <summary>
    /// Sea's typed surface as the mods that read it without a build reference look it up, on the built Sea.dll (the same
    /// Debug build ContractSnapshot publishes from). A rename, a new overload or a changed type compiles everywhere and
    /// fails only in those mods' worlds, mostly silently; here it fails with the consumer's name. Keep this in step with
    /// docs/open-sea-contracts.md ("Who reads what").
    ///  - World Map 0.5.x (Wmap src/Integrations/SeaWinds.cs:28-92, its wind layer and barometer panel): the mod system by
    ///    full name, SeaSystem.Model, WindModel.Prevailing/At/BeltName by name and parameter types, Mode by name compared
    ///    with "Off", Shape.DaysPerYear/HoursPerDay as doubles, Wind.X/Z as fields (Expression.Field), Sea.Beaufort.Name.
    ///  - Earth 2.0 (Earth src/RequiredMods.cs:96-105, EarthWeather.Role.cs LinkSea/UnlinkSea): SetWindProvider and
    ///    ClearWindProvider by exact signature among the public instance methods, provider name "earth".
    ///  - Sea's own reader of seacontracts:weather (Earth's role; SeaContracts.FollowWeather): owner, wind, prevailing.
    /// Cylinder World reads none of these (it reads the weather role itself, docs/open-sea-contracts.md).
    /// </summary>
    static class ConsumerSurface
    {
        const string Wmap = "World Map", Earth = "Earth";

        public static void Run(Action<bool, string> check)
        {
            WorldMap(check);
            EarthBinding(check);
            WeatherRole(check);
        }

        // ------------------------------------------------------------------ World Map 0.5.x (SeaWinds)

        /// <summary>World Map's WorldSource.Member: a field, else a property, by name with the default flags.</summary>
        static object Member(object o, string name) => o == null ? null : o.GetType().GetField(name)?.GetValue(o) ?? o.GetType().GetProperty(name)?.GetValue(o);

        /// <summary>World Map's SeaWinds.Bind: model.method(x, z, hours[, false]) compiled into a tuple of the result's X and Z fields.</summary>
        static Func3 Bind(object m, string name, params Type[] signature)
        {
            var method = m.GetType().GetMethod(name, signature);
            if (method == null) return null;
            ParameterExpression x = Expression.Parameter(typeof(double)), z = Expression.Parameter(typeof(double)), h = Expression.Parameter(typeof(double));
            var args = signature.Length == 4 ? new Expression[] { x, z, h, Expression.Constant(false) } : new Expression[] { x, z, h };
            var wind = Expression.Variable(method.ReturnType);
            var body = Expression.Block(new[] { wind }, Expression.Assign(wind, Expression.Call(Expression.Constant(m), method, args)),
                Expression.New(typeof((double, double)).GetConstructor(new[] { typeof(double), typeof(double) }), Expression.Field(wind, "X"), Expression.Field(wind, "Z")));
            return Expression.Lambda<Func3>(body, x, z, h).Compile();
        }

        /// <summary>One of World Map's bindings: any exception (an ambiguous overload, a property where it wants a field) fails it.</summary>
        static T Try<T>(Action<bool, string> check, string what, Func<T> bind)
        {
            try { return bind(); }
            catch (Exception e) { check(false, $"{Wmap} binds {what}: {e.GetType().Name}: {e.Message}"); return default; }
        }

        static void WorldMap(Action<bool, string> check)
        {
            var assembly = typeof(Contracts).Assembly;
            // SeaWinds.Find: the mod system whose type's full name is exactly "Sea.SeaSystem", with a Model property
            var type = assembly.GetType("Sea.SeaSystem");
            check(type != null && type.FullName == "Sea.SeaSystem" && typeof(Vintagestory.API.Common.ModSystem).IsAssignableFrom(type),
                $"{Wmap}: Sea.SeaSystem is a mod system found by its full name");
            var modelProperty = Try(check, "SeaSystem.Model", () => type?.GetProperty("Model"));
            check(modelProperty?.GetMethod?.IsPublic == true && !modelProperty.GetMethod.IsStatic, $"{Wmap}: SeaSystem.Model, a public instance property");
            var beaufortName = Try(check, "Sea.Beaufort.Name(double)", () => assembly.GetType("Sea.Beaufort")?.GetMethod("Name", new[] { typeof(double) }));
            check(beaufortName != null && beaufortName.IsStatic && beaufortName.ReturnType == typeof(string),
                $"{Wmap}: Sea.Beaufort.Name(double), static, returning string (invoked without an instance)");
            if (modelProperty == null) return;

            // A real model as Sea makes it, with a provider: World Map's compiled bindings must call through to it
            var shape = new SeaDll::Sea.WorldShape { DaysPerYear = 96, HoursPerDay = 20 };
            var model = new SeaDll::Sea.WindModel(shape, new SeaDll::Sea.SeaConfig())
            {
                Provider = new SeaDll::Sea.WindProvider("test", (x, z, h, gusts) => gusts ? (0.4, -0.2) : (0.3, 0.1), (x, z, h) => (0.1, 0.2)),
            };
            var sys = new SeaSys();
            modelProperty.SetValue(sys, model);   // (its setter is private; Sea sets it when the world's shape is known)
            object m = Try(check, "SeaSystem.Model's value", () => modelProperty.GetValue(sys));
            check(ReferenceEquals(m, model), $"{Wmap}: SeaSystem.Model gives the model");
            if (m == null) return;

            var prevailing = Try(check, "WindModel.Prevailing(double, double, double) into Wind.X, Wind.Z", () => Bind(m, "Prevailing", typeof(double), typeof(double), typeof(double)));
            check(prevailing != null && prevailing(100, 200, 3000) == (0.1, 0.2), $"{Wmap}: WindModel.Prevailing(double, double, double) gives the provider's prevailing wind as Wind.X, Wind.Z (its wind layer)");
            var weather = Try(check, "WindModel.At(double, double, double, bool) into Wind.X, Wind.Z", () => Bind(m, "At", typeof(double), typeof(double), typeof(double), typeof(bool)));
            check(weather != null && weather(100, 200, 3000) == (0.3, 0.1), $"{Wmap}: WindModel.At(double, double, double, bool) with gusts false gives the steady wind (its weather layer)");
            var beltName = Try(check, "WindModel.BeltName(double, double) as Func<double, double, string>", () =>
            {
                var belt = m.GetType().GetMethod("BeltName", new[] { typeof(double), typeof(double) });
                return belt == null ? null : (Func<double, double, string>)belt.CreateDelegate(typeof(Func<double, double, string>), m);
            });
            check(beltName != null, $"{Wmap}: WindModel.BeltName(double, double), an instance method returning string (null since 0.4.0: no belt shown)");

            // Mode: Member(model, "Mode")?.ToString() == "Off" means Sea leaves the wind to vanilla (WindLayer.cs:88)
            var modeType = (MemberInfo)m.GetType().GetField("Mode") ?? m.GetType().GetProperty("Mode");
            var mt = (modeType as FieldInfo)?.FieldType ?? (modeType as PropertyInfo)?.PropertyType;
            check(mt != null && (mt == typeof(string) || mt.IsEnum && Enum.GetNames(mt).Contains("Off")), $"{Wmap}: WindModel.Mode, read by name, can say \"Off\" (found {mt?.Name ?? "nothing"})");
            check(Try(check, "WindModel.Mode", () => Member(m, "Mode")?.ToString()) == "Provider", $"{Wmap}: WindModel.Mode is not \"Off\" while a provider decides the wind");
            model.Provider = null;
            check(Try(check, "WindModel.Mode", () => Member(m, "Mode")?.ToString()) == "Off", $"{Wmap}: WindModel.Mode reads \"Off\" without a provider (vanilla's wind)");

            // Shape: Member(model, "Shape") then Member(shape, name) is double v && v > 0, else 108 days and 24 hours silently
            object s = Try(check, "WindModel.Shape", () => Member(m, "Shape"));
            check(Member(s, "DaysPerYear") is double days && days == 96, $"{Wmap}: WindModel.Shape.DaysPerYear, a double");
            check(Member(s, "HoursPerDay") is double hours && hours == 20, $"{Wmap}: WindModel.Shape.HoursPerDay, a double");

            // Beaufort: Invoke(null, strength) as string; 0.6 vanilla units are 15 m/s
            check(Try(check, "Sea.Beaufort.Name", () => beaufortName?.Invoke(null, new object[] { 0.6 }) as string) == "near gale", $"{Wmap}: Beaufort.Name(0.6) names the force (\"near gale\")");
        }

        // ------------------------------------------------------------------ Earth 2.0 (RequiredMods.SeaWindSetter/Clearer)

        /// <summary>Earth's RequiredMods.Method: among the public instance methods, the name, no generics, the exact return and parameter types.</summary>
        static MethodInfo EarthMethod(object provider, string name, Type result, params Type[] arguments)
            => provider?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m =>
                m.Name == name && !m.ContainsGenericParameters && m.ReturnType == result
                && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(arguments));

        static void EarthBinding(Action<bool, string> check)
        {
            // Earth finds it with ModLoader.GetModSystem("Sea.SeaSystem"): the full name, ignoring case
            check(typeof(Contracts).Assembly.GetTypes().Count(t => string.Equals(t.FullName, "Sea.SeaSystem", StringComparison.InvariantCultureIgnoreCase)) == 1,
                $"{Earth}: one type is named Sea.SeaSystem (GetModSystem by full name, any case)");
            var sys = new SeaSys();
            var set = EarthMethod(sys, "SetWindProvider", typeof(bool), typeof(string), typeof(Func4), typeof(Func3));
            var clear = EarthMethod(sys, "ClearWindProvider", typeof(void), typeof(string));
            check(set != null, $"{Earth}: SeaSystem.SetWindProvider(string, Func<double, double, double, bool, (double, double)>, Func<double, double, double, (double, double)>) returning bool");
            check(clear != null, $"{Earth}: SeaSystem.ClearWindProvider(string) returning void");
            if (set == null || clear == null) return;
            // LinkSea: Invoke(sea, name, windAt, prevailing) is bool b && b; UnlinkSea: Invoke(sea, name)
            Func4 windAt = (x, z, h, gusts) => (0.5, 0);
            Func3 prevailing = (x, z, h) => (0.2, 0);
            check(set.Invoke(sys, new object[] { "earth", windAt, prevailing }) is bool ok && ok && sys.WindProviderName == "earth",
                $"{Earth}: SetWindProvider(\"earth\", ...) answers true and Sea's wind comes from \"earth\"");
            clear.Invoke(sys, new object[] { "earth" });
            check(sys.WindProviderName == null, $"{Earth}: ClearWindProvider(\"earth\") hands the wind back to vanilla");
        }

        // ------------------------------------------------------------------ Sea reads seacontracts:weather (section 4)

        /// <summary>
        /// Sea's reader of the weather role (SeaContracts.FollowWeather, once a second): an owner other than Sea that publishes a
        /// wind of the contract's type in m/s becomes Sea's wind provider under its own name, in vanilla units (÷ 25), unless a
        /// mod already decides Sea's wind (Earth's SetWindProvider: the same name, nothing changes). Sea reads only owner, wind
        /// and prevailing; version, priority and revision are not read (any version is followed).
        /// </summary>
        static void WeatherRole(Action<bool, string> check)
        {
            const string Key = Contracts.Prefix + "weather";
            var cache = new Dictionary<string, object>();
            var api = FakeApi.Make(cache, out var ticks);
            var sys = new SeaSys();
            var c = new Contracts(sys, api, FakeApi.Logger());
            c.Publish();
            void Tick() { foreach (var t in ticks.ToArray()) t(1f); }
            // (below Sea's cap of 1.5 vanilla units, 37.5 m/s)
            Func4 wind = (x, z, h, gusts) => gusts ? (30.0, -15.0) : (25.0, 0.0);
            Func3 prevailingMps = (x, z, h) => (12.5, 12.5);
            Dictionary<string, object> Role(string owner, object w, object p = null)
            {
                var d = new Dictionary<string, object> { ["owner"] = owner, ["version"] = 1, ["priority"] = 100, ["revision"] = new Func<long>(() => 0), ["writesVanilla"] = true, ["wind"] = w };
                if (p != null) d["prevailing"] = p;
                return d;
            }
            static bool Near(double a, double b) => Math.Abs(a - b) < 1e-12;

            Tick();
            check(sys.WindProviderName == null && c.WeatherOwner == null, "weather role: without a weather owner Sea has no provider (vanilla's wind)");

            cache[Key] = Role("earth", wind, prevailingMps);
            Tick();
            var p = sys.WindProvider;
            check(sys.WindProviderName == "earth" && c.WeatherOwner == "earth", "weather role: Sea follows the owner's wind under the owner's name (\"earth\")");
            check(p != null && Near(p.At(1, 2, 3, true).X, 1.2) && Near(p.At(1, 2, 3, true).Z, -0.6) && Near(p.At(1, 2, 3, false).X, 1) && Near(p.At(1, 2, 3, false).Z, 0),
                "weather role: wind (m/s, gusts on and off) becomes vanilla units, ÷ 25");
            check(p != null && Near(p.Prevailing(1, 2, 3).X, 0.5) && Near(p.Prevailing(1, 2, 3).Z, 0.5), "weather role: prevailing (m/s) becomes vanilla units, ÷ 25");

            cache[Key] = Role("rival", wind);
            Tick();
            p = sys.WindProvider;
            check(sys.WindProviderName == "rival" && c.WeatherOwner == "rival", "weather role: a new owner of the role replaces the one Sea followed");
            check(p != null && Near(p.Prevailing(1, 2, 3).X, 1) && Near(p.Prevailing(1, 2, 3).Z, 0), "weather role: without prevailing, the steady wind (gusts off) stands for it");

            cache.Remove(Key);
            Tick();
            check(sys.WindProviderName == null && c.WeatherOwner == null, "weather role: a withdrawn role takes Sea's provider away again");

            cache[Key] = new Dictionary<string, object> { ["owner"] = "bare", ["wind"] = wind };
            Tick();
            check(sys.WindProviderName == "bare", "weather role: owner and wind are all Sea needs (version, priority and revision are not read)");
            cache.Remove(Key);
            Tick();

            // the same mod already gives Sea its wind directly (Earth calls SetWindProvider("earth") and publishes the role)
            Func4 direct = (x, z, h, gusts) => (0.7, 0);
            sys.SetWindProvider("earth", direct, null);
            cache[Key] = Role("earth", wind, prevailingMps);
            Tick();
            check(sys.WindProviderName == "earth" && c.WeatherOwner == null && Near(sys.WindProvider.At(0, 0, 0, true).X, 0.7),
                "weather role: an owner that already gives Sea its wind by SetWindProvider keeps that one; nothing changes");
            sys.ClearWindProvider("earth");
            cache.Remove(Key);
            Tick();

            // another mod decides Sea's wind: kept
            sys.SetWindProvider("othermod", direct, null);
            cache[Key] = Role("earth", wind, prevailingMps);
            Tick();
            check(sys.WindProviderName == "othermod" && c.WeatherOwner == null, "weather role: a provider of another name keeps Sea's wind");
            sys.ClearWindProvider("othermod");
            cache.Remove(Key);
            Tick();

            cache[Key] = Role("sea", wind);
            Tick();
            check(sys.WindProviderName == null && c.WeatherOwner == null, "weather role: Sea does not follow a role published under its own name");
            cache[Key] = Role("earth", (Func3)((x, z, h) => (25.0, 0.0)));
            Tick();
            check(sys.WindProviderName == null && c.WeatherOwner == null, "weather role: a wind of another delegate type is not followed");
            c.Dispose();
            check(ticks.Count == 0, "weather role: Dispose stops watching");
        }
    }
}
