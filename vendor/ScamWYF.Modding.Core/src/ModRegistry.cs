using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// The mods that came up on the shared library this session, plus a report of anything that
    /// collided.
    /// </summary>
    /// <remarks>
    /// The menu renders this; it is the reason collisions are visible instead of being something you
    /// have to read LogOutput.log to discover.
    /// </remarks>
    public static class ModRegistry
    {
        private static readonly List<ScamMod> Loaded = new List<ScamMod>();
        private static readonly List<string> Rejections = new List<string>();

        /// <summary>Live mods, in load order.</summary>
        public static ScamMod[] Mods
        {
            get { lock (Loaded) { return Loaded.ToArray(); } }
        }

        /// <summary>Add a mod. False means its id is already taken and it must not run.</summary>
        public static bool Register(ScamMod mod)
        {
            if (mod == null) return false;

            lock (Loaded)
            {
                foreach (var existing in Loaded)
                {
                    if (existing.ModId != mod.ModId) continue;

                    Rejections.Add(mod.DisplayName + " (" + mod.ModId + ") - already loaded as " +
                                   existing.DisplayName + " (" + existing.ModVersion + ")");
                    return false;
                }

                Loaded.Add(mod);
            }
            return true;
        }

        public static void Unregister(ScamMod mod)
        {
            if (mod == null) return;
            lock (Loaded) { Loaded.Remove(mod); }
        }

        public static bool TryGet(string modId, out ScamMod mod)
        {
            lock (Loaded)
            {
                foreach (var candidate in Loaded)
                {
                    if (candidate.ModId != modId) continue;
                    mod = candidate;
                    return true;
                }
            }

            mod = null;
            return false;
        }

        /// <summary>Number of mods currently sharing the library.</summary>
        public static int Count
        {
            get { lock (Loaded) { return Loaded.Count; } }
        }

        /// <summary>
        /// Everything that is wrong right now, one line each, or null when nothing is. This is what the
        /// menu's Mods tab shows under "collisions".
        /// </summary>
        public static string CollisionReport()
        {
            var report = new StringBuilder();

            lock (Loaded)
            {
                foreach (var rejection in Rejections)
                {
                    report.AppendLine("duplicate mod id: " + rejection);
                }
            }

            report.Append(PatchCoordinator.ConflictReport());
            report.Append(Hotkeys.ConflictReport());
            return report.Length == 0 ? null : report.ToString().TrimEnd();
        }
    }
}