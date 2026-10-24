using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// Every Harmony patch made through here, so that two mods patching the same game method is
    /// something you find out about immediately rather than by wondering why a prefix stopped
    /// firing.
    /// </summary>
    /// <remarks>
    /// One Harmony instance per mod, keyed by the mod's id. That keeps UnpatchMod exact: a mod
    /// going away takes its own patches with it and nobody else's.
    /// </remarks>
    public static class PatchCoordinator
    {
        /// <summary>One mod's patch on one game method.</summary>
        public sealed class PatchRecord
        {
            public string OwnerId;
            public string OwnerName;
            public string Method;
            public string Kinds;

            public override string ToString()
            {
                return OwnerName + " [" + OwnerId + "] -> " + Method + " (" + Kinds + ")";
            }
        }

        /// <summary>Two or more mods patching the same method.</summary>
        public sealed class PatchConflict
        {
            public string Method;
            public readonly List<string> Owners = new List<string>();
        }

        private static readonly Dictionary<string, Harmony> Instances = new Dictionary<string, Harmony>();
        private static readonly List<PatchRecord> Records = new List<PatchRecord>();
        private static readonly List<PatchConflict> Conflicts = new List<PatchConflict>();

        /// <summary>All patches currently installed through the coordinator.</summary>
        public static PatchRecord[] Installed
        {
            get { lock (Records) { return Records.ToArray(); } }
        }

        /// <summary>One mod's patches, for its own menu page.</summary>
        public static PatchRecord[] ForOwner(string ownerId)
        {
            var mine = new List<PatchRecord>();
            if (string.IsNullOrEmpty(ownerId)) return mine.ToArray();

            lock (Records)
            {
                foreach (var record in Records)
                {
                    if (record.OwnerId == ownerId) mine.Add(record);
                }
            }
            return mine.ToArray();
        }

        /// <summary>Methods more than one mod has patched.</summary>
        public static PatchConflict[] ConflictingMethods
        {
            get { lock (Records) { return Conflicts.ToArray(); } }
        }

        /// <summary>
        /// Patch a resolved method. Returns false if Harmony refused, in which case the game is
        /// left exactly as it was.
        /// </summary>
        /// <param name="owner">The mod asking for the patch.</param>
        /// <param name="target">The game method to patch.</param>
        /// <param name="what">What the patch does, for the log and the mod list.</param>
        public static bool TryPatch(ScamMod owner, MethodBase target, string what,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (target == null) throw new ArgumentNullException("target");
            if (prefix == null && postfix == null && transpiler == null && finalizer == null)
                throw new ArgumentException("nothing to apply", "prefix");

            var harmony = InstanceFor(owner.ModId);
            var previous = OtherOwners(target, owner.ModId);

            try
            {
                harmony.Patch(target, prefix, postfix, transpiler, finalizer, null);
            }
            catch (Exception ex)
            {
                owner.ModLog.LogError("Could not patch " + Describe(target) + " (" + what + "): " + ex);
                return false;
            }

            lock (Records)
            {
                Records.Add(new PatchRecord
                {
                    OwnerId = owner.ModId,
                    OwnerName = owner.DisplayName,
                    Method = Describe(target),
                    Kinds = Kinds(prefix, postfix, transpiler, finalizer)
                });

                if (previous.Count > 0) RecordConflict(target, owner, previous);
            }

            if (previous.Count > 0)
            {
                owner.ModLog.LogWarning(
                    Describe(target) + " is already patched by " + string.Join(", ", previous.ToArray()) +
                    ". Patches compose, but if behaviour looks wrong, check this first.");
            }

            owner.ModLog.LogInfo("Patched " + Describe(target) + " (" + what + ").");
            return true;
        }

        /// <summary>
        /// Resolve a method on a game type and patch it in one step. When the method has moved or
        /// changed shape - the usual sign the game was updated - this logs what it expected and
        /// returns false instead of letting AccessTools throw.
        /// </summary>
        public static bool TryPatch(ScamMod owner, Type declaringType, string methodName, Type[] parameterTypes,
            string what, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
        {
            MethodBase target;
            if (!GameBuild.TryResolveMethod(owner, declaringType, methodName, parameterTypes, out target))
                return false;

            return TryPatch(owner, target, what, prefix, postfix, transpiler, finalizer);
        }

        /// <summary>Remove everything a mod patched. Called for you when a mod unloads.</summary>
        public static void UnpatchOwner(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;

            lock (Records)
            {
                Records.RemoveAll(record => record.OwnerId == modId);
                foreach (var conflict in Conflicts)
                    conflict.Owners.RemoveAll(id => id == modId);
                Conflicts.RemoveAll(conflict => conflict.Owners.Count < 2);
            }

            if (Harmony.HasAnyPatches(modId))
                Harmony.UnpatchID(modId);
        }

        /// <summary>Conflicts as text, or null when there are none.</summary>
        public static string ConflictReport()
        {
            var report = new StringBuilder();
            lock (Records)
            {
                foreach (var conflict in Conflicts)
                {
                    report.AppendLine("patch collision on " + conflict.Method + ": " +
                                      string.Join(" + ", conflict.Owners.ToArray()));
                }
            }
            return report.Length == 0 ? null : report.ToString();
        }

        // ---------------------------------------------------------------- internals

        private static Harmony InstanceFor(string modId)
        {
            lock (Instances)
            {
                Harmony harmony;
                if (Instances.TryGetValue(modId, out harmony)) return harmony;
                harmony = new Harmony(modId);
                Instances.Add(modId, harmony);
                return harmony;
            }
        }

        /// <summary>Ids of other mods already patching this method.</summary>
        private static List<string> OtherOwners(MethodBase target, string modId)
        {
            var owners = new List<string>();
            var info = Harmony.GetPatchInfo(target);
            if (info == null || info.Owners == null) return owners;
            foreach (var ownerId in info.Owners)
            {
                if (ownerId != modId) owners.Add(ownerId);
            }
            return owners;
        }

        private static void RecordConflict(MethodBase target, ScamMod owner, List<string> previous)
        {
            var method = Describe(target);
            foreach (var conflict in Conflicts)
            {
                if (conflict.Method != method) continue;
                AddOwner(conflict, owner.ModId);
                return;
            }

            var fresh = new PatchConflict { Method = method };
            foreach (var ownerId in previous) AddOwner(fresh, ownerId);
            AddOwner(fresh, owner.ModId);
            Conflicts.Add(fresh);
        }

        private static void AddOwner(PatchConflict conflict, string modId)
        {
            if (!conflict.Owners.Contains(modId)) conflict.Owners.Add(modId);
        }

        private static string Kinds(HarmonyMethod prefix, HarmonyMethod postfix,
            HarmonyMethod transpiler, HarmonyMethod finalizer)
        {
            var kinds = new List<string>();
            if (prefix != null) kinds.Add("prefix");
            if (postfix != null) kinds.Add("postfix");
            if (transpiler != null) kinds.Add("transpiler");
            if (finalizer != null) kinds.Add("finalizer");
            return string.Join("+", kinds.ToArray());
        }

        private static string Describe(MethodBase target)
        {
            return target.DeclaringType != null ? target.DeclaringType.FullName + "." + target.Name : target.Name;
        }
    }
}