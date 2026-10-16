using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Sea
{
    /// <summary>
    /// Particles the game makes in and on the world's water, kept out of dry places (a ship's hold below the waterline,
    /// where the world still has water: the ship is an entity, not blocks). Vanilla spawns them from the client's particle
    /// thread through <see cref="IAsyncParticleManager.Spawn"/>; the patched methods call these instead, which leave out a
    /// spawn whose place is dry. Particles aren't cut by the water shader's dry clip, so these showed in the hold.
    /// <list type="bullet">
    /// <item>In water (<see cref="SpawnInWater"/>): vanilla's ambient specks (<c>ModSystemAmbientParticles</c>, its
    /// liquidParticles): light blue-white motes drifting for two seconds, spawned round the player wherever there's a
    /// full block of liquid, and dying in air. Only those that die in air are judged (its summer air motes and fireflies,
    /// spawned in air, aren't).</item>
    /// <item>On water (<see cref="SpawnOnWater"/>): rain's splashes and a storm's spray where the rain map's top block is
    /// water (<c>WeatherSimulationParticles</c>): inside a hull that's the sea's level, under her decks. Only small spawn
    /// boxes are judged (the rain and storm dust themselves are spawned over tens of blocks and kept).</item>
    /// </list>
    /// (0.3.5, "there are still air bubbles from water in the cargo deck") A spawn is judged over its whole life, not
    /// only where it starts: the specks stand still in the world's water for two seconds and the ship doesn't, so those
    /// spawned just outside her hull (ahead of her bow, beside her flaring sides, under her keel) were swept into her
    /// hold as she sailed, rolled and heaved through them. Those that would be in a dry place at any moment of their
    /// life (<see cref="IDrySpaces.IsDryWithin"/>) aren't spawned.
    /// </summary>
    public static class DryParticles
    {
        /// <summary>Is this world position dry? Set by Sea on the client (asked from the particle thread).</summary>
        public static System.Func<double, double, double, bool> IsDry;
        /// <summary>
        /// (0.3.5) Is this world position dry at any moment in the next seconds (a hull moving through the water there)? Set
        /// by Sea on the client; without it a spawn is judged by <see cref="IsDry"/> where it starts.
        /// </summary>
        public static System.Func<double, double, double, double, bool> IsDryWithin;

        static long dropped;
        /// <summary>How many spawns were left out (for .sea).</summary>
        public static long Dropped => Interlocked.Read(ref dropped);

        /// <summary>Particles that live in water: left out where their place is dry.</summary>
        public static int SpawnInWater(IAsyncParticleManager manager, IParticlePropertiesProvider p)
        {
            if (p is SimpleParticleProperties sp && sp.ShouldDieInAir && Dry(sp)) { Interlocked.Increment(ref dropped); return 0; }
            return manager.Spawn(p);
        }

        /// <summary>Splashes and spray on the water's surface: left out where their (small) spawn box's middle is dry.</summary>
        public static int SpawnOnWater(IAsyncParticleManager manager, IParticlePropertiesProvider p)
        {
            if (p is SimpleParticleProperties sp && Small(sp) && Dry(sp)) { Interlocked.Increment(ref dropped); return 0; }
            return manager.Spawn(p);
        }

        /// <summary>The largest spawn box (each way) judged by its middle.</summary>
        public const double SmallBox = 2.0;

        static bool Small(SimpleParticleProperties sp)
        {
            var a = sp.AddPos;
            return a == null || (Math.Abs(a.X) <= SmallBox && Math.Abs(a.Y) <= SmallBox && Math.Abs(a.Z) <= SmallBox);
        }

        static bool Dry(SimpleParticleProperties sp)
        {
            var check = IsDry;
            var within = IsDryWithin;
            var m = sp.MinPos;
            if (check == null && within == null || m == null) return false;
            var a = sp.AddPos;
            double x = m.X + (a?.X ?? 0) / 2, y = m.Y + (a?.Y ?? 0) / 2, z = m.Z + (a?.Z ?? 0) / 2;
            try
            {
                // (its whole life: the longest it may live)
                if (within != null) return within(x, y, z, Math.Max(0, sp.LifeLength + Math.Max(0, sp.addLifeLength)));
                return check(x, y, z);
            }
            catch (Exception) { return false; }
        }

        static readonly MethodInfo spawn = AccessTools.Method(typeof(IAsyncParticleManager), nameof(IAsyncParticleManager.Spawn));

        /// <summary>Every manager.Spawn(...) in the method becomes <see cref="SpawnInWater"/>.</summary>
        public static IEnumerable<CodeInstruction> InWaterTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Replace(instructions, AccessTools.Method(typeof(DryParticles), nameof(SpawnInWater)));

        /// <summary>Every manager.Spawn(...) in the method becomes <see cref="SpawnOnWater"/>.</summary>
        public static IEnumerable<CodeInstruction> OnWaterTranspiler(IEnumerable<CodeInstruction> instructions) =>
            Replace(instructions, AccessTools.Method(typeof(DryParticles), nameof(SpawnOnWater)));

        /// <summary>Calls replaced by the last transpiler run (a patch that found none is reported).</summary>
        public static int LastReplaced;

        /// <summary>The stack is the same before and after: (manager, provider) in, an int out.</summary>
        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, MethodInfo with)
        {
            int n = 0;
            foreach (var ci in instructions)
            {
                if (ci.Calls(spawn))
                {
                    n++;
                    yield return new CodeInstruction(OpCodes.Call, with).WithLabels(ci.labels).WithBlocks(ci.blocks);
                    continue;
                }
                yield return ci;
            }
            LastReplaced = n;
        }
    }
}
