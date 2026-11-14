using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Sea
{
    /// <summary>(SE-0046) Hulls and dry places that ship mods register: water clip sources and dry spaces.</summary>
    public partial class SeaSystem
    {
        /// <summary>Client: register something with hulls in the water; Sea keeps the waves out of them. Any time (from any thread), even before Sea started.</summary>
        public void AddWaterClipSource(IWaterClipSource source)
        {
            if (source == null) return;
            lock (drySync) { if (Array.IndexOf(waterSources, source) < 0) waterSources = waterSources.Append(source).ToArray(); }
        }

        public void RemoveWaterClipSource(IWaterClipSource source)
        {
            lock (drySync) { waterSources = waterSources.Where(s => s != source).ToArray(); }
        }

        /// <summary>(0.4.2) The registered hull sources, as of now (a copy is made on every change, so drawing never sees one change).</summary>
        internal IWaterClipSource[] WaterSources => waterSources;
        volatile IWaterClipSource[] waterSources = Array.Empty<IWaterClipSource>();

        /// <summary>
        /// Both sides: register something with dry places where the world has water (a ship mod's hulls, rooms below
        /// the waterline). There the camera isn't under water (no fog, tint or muffled sound) and creatures can breathe.
        /// Any time, even before Sea started. For the water not to be drawn there, also mark its clips
        /// <see cref="WaterClipBox.Dry"/>.
        /// </summary>
        public void AddDrySpaces(IDrySpaces source)
        {
            lock (drySync) { if (Array.IndexOf(drySpaces, source) < 0) drySpaces = drySpaces.Append(source).ToArray(); }
        }

        public void RemoveDrySpaces(IDrySpaces source)
        {
            lock (drySync) { drySpaces = drySpaces.Where(s => s != source).ToArray(); }
        }

        /// <summary>Is this world position dry though the world has water there (inside a hull)? Safe from any thread.</summary>
        public bool IsDry(double x, double y, double z)
        {
            var list = drySpaces;
            for (int i = 0; i < list.Length; i++)
            {
                try { if (list[i].IsDry(x, y, z)) return true; }
                catch (Exception e) { LogOnce.Error(api?.Logger, "dry places of " + list[i].GetType().Name, e); }
            }
            return false;
        }

        /// <summary>(0.3.5) Will this world position be dry at any moment in the next seconds (<see cref="IDrySpaces.IsDryWithin"/>)? Safe from any thread.</summary>
        public bool IsDryWithin(double x, double y, double z, double seconds)
        {
            var list = drySpaces;
            for (int i = 0; i < list.Length; i++)
            {
                try { if (list[i].IsDryWithin(x, y, z, seconds)) return true; }
                catch (Exception e) { LogOnce.Error(api?.Logger, "dry places (within seconds) of " + list[i].GetType().Name, e); }
            }
            return false;
        }

        public int DrySpaceSources => drySpaces.Length;

        volatile IDrySpaces[] drySpaces = Array.Empty<IDrySpaces>();
        readonly object drySync = new();
    }
}
