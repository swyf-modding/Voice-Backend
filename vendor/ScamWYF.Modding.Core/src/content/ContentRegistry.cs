using System;
using System.Collections.Generic;
using System.Text;

namespace ScamWYF.Modding.Core.Content
{
    /// <summary>
    /// Every custom scam, app, store product and sentinel registered by any mod sharing this
    /// library, with ownership attached so two mods adding the same id is a clear message
    /// rather than a silent overwrite.
    /// </summary>
    /// <remarks>
    /// Register during <see cref="ScamMod"/> load (OnModLoad), before the office scene builds the
    /// OS. Entries are merged into the game's own catalogs by Harmony patches installed on first
    /// registration, so the game lists, sells, prompts and pays out custom content exactly like
    /// its own. A mod that registers after the catalogs already loaded still works: the entry is
    /// injected into the live catalogs immediately, and desktop icons appear on scene reload.
    ///
    /// Ids are unique across ALL mods, case-insensitively: the second registration of an id
    /// fails with both owners named, and the first keeps working. Prefix ids with something
    /// yours (e.g. "moar-jury-duty") rather than claiming a generic word.
    ///
    /// Unloading removes the registrations so a later load starts clean; entries already merged
    /// into a running session stay until the scene reloads, which is why <see cref="ScamMod"/>
    /// calls <see cref="UnregisterOwner"/> for you on unload.
    /// </remarks>
    public static class ContentRegistry
    {
        /// <summary>One registered thing, with its owner attached.</summary>
        public sealed class Entry
        {
            public string Kind;
            public string Id;
            public string OwnerId;
            public string OwnerName;
            public object Spec;
            public string Json;

            public override string ToString()
            {
                return Kind + " '" + Id + "' by " + OwnerName + " [" + OwnerId + "]";
            }
        }

        private static readonly List<Entry> ConversationScams = new List<Entry>();
        private static readonly List<Entry> ScamTypes = new List<Entry>();
        private static readonly List<Entry> Apps = new List<Entry>();
        private static readonly List<Entry> Products = new List<Entry>();
        private static readonly List<Entry> Sentinels = new List<Entry>();
        private static readonly List<string> Rejections = new List<string>();

        /// <summary>Every registered conversation scam, in registration order.</summary>
        public static Entry[] RegisteredConversationScams
        {
            get { lock (ConversationScams) { return ConversationScams.ToArray(); } }
        }

        public static Entry[] RegisteredScamTypes
        {
            get { lock (ScamTypes) { return ScamTypes.ToArray(); } }
        }

        public static Entry[] RegisteredApps
        {
            get { lock (Apps) { return Apps.ToArray(); } }
        }

        public static Entry[] RegisteredProducts
        {
            get { lock (Products) { return Products.ToArray(); } }
        }

        public static Entry[] RegisteredSentinels
        {
            get { lock (Sentinels) { return Sentinels.ToArray(); } }
        }

        /// <summary>
        /// Register a conversation scam (objectives that check off as you talk). The scam's AppId
        /// must have a matching app registered (contentType "conversationScam") and its ProductId
        /// a matching product, or the scam has no window and no way to unlock.
        /// </summary>
        public static bool RegisterConversationScam(ScamMod owner, ConversationScamSpec spec)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (spec == null)
            {
                owner.ModLog.LogWarning("Ignoring a null conversation scam from " + owner.DisplayName + ".");
                return false;
            }

            var problem = ContentValidation.CheckConversationScam(spec);
            if (problem != null)
            {
                Reject(owner, "conversation scam", spec.Id, problem);
                return false;
            }

            lock (ConversationScams)
            {
                if (!Claim(ConversationScams, owner, "conversation scam", spec.Id)) return false;
                ConversationScams.Add(NewEntry(owner, "conversation scam", spec.Id, spec, null));
            }

            AfterRegistered(owner, "conversation scam '" + spec.Id + "'");
            return true;
        }

        /// <summary>Register a scam type (a script entry pointing at a form app).</summary>
        public static bool RegisterScamType(ScamMod owner, ScamTypeSpec spec)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (spec == null)
            {
                owner.ModLog.LogWarning("Ignoring a null scam type from " + owner.DisplayName + ".");
                return false;
            }

            var problem = ContentValidation.CheckScamType(spec);
            if (problem != null)
            {
                Reject(owner, "scam type", spec.Id, problem);
                return false;
            }

            lock (ScamTypes)
            {
                if (!Claim(ScamTypes, owner, "scam type", spec.Id)) return false;
                ScamTypes.Add(NewEntry(owner, "scam type", spec.Id, spec, null));
            }

            AfterRegistered(owner, "scam type '" + spec.Id + "'");
            return true;
        }

        /// <summary>Register an OS app. Scam apps should use the OsAppSpec factories.</summary>
        public static bool RegisterApp(ScamMod owner, OsAppSpec spec)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (spec == null)
            {
                owner.ModLog.LogWarning("Ignoring a null app from " + owner.DisplayName + ".");
                return false;
            }

            var problem = ContentValidation.CheckApp(spec);
            if (problem != null)
            {
                Reject(owner, "app", spec.Id, problem);
                return false;
            }

            lock (Apps)
            {
                if (!Claim(Apps, owner, "app", spec.Id)) return false;
                Apps.Add(NewEntry(owner, "app", spec.Id, spec, null));
            }

            AfterRegistered(owner, "app '" + spec.Id + "'");
            return true;
        }

        /// <summary>
        /// Register an OS app from raw JSON, for shapes the spec factories do not cover. The JSON
        /// must be a single app definition object; appId names it for duplicate detection.
        /// </summary>
        public static bool RegisterAppJson(ScamMod owner, string appId, string json)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            var problem = ContentValidation.CheckId(appId, "app");
            if (problem != null)
            {
                Reject(owner, "app", appId, problem);
                return false;
            }
            if (string.IsNullOrEmpty(json) || json.Trim().Length == 0)
            {
                Reject(owner, "app", appId, "empty JSON.");
                return false;
            }

            lock (Apps)
            {
                if (!Claim(Apps, owner, "app", appId)) return false;
                Apps.Add(NewEntry(owner, "app", appId, null, json));
            }

            AfterRegistered(owner, "app '" + appId + "' (raw JSON)");
            return true;
        }

        /// <summary>Register a store product (what gates a catalog app and what Scamazon sells).</summary>
        public static bool RegisterProduct(ScamMod owner, StoreProductSpec spec)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (spec == null)
            {
                owner.ModLog.LogWarning("Ignoring a null product from " + owner.DisplayName + ".");
                return false;
            }

            var problem = ContentValidation.CheckProduct(spec);
            if (problem != null)
            {
                Reject(owner, "product", spec.Id, problem);
                return false;
            }

            lock (Products)
            {
                if (!Claim(Products, owner, "product", spec.Id)) return false;
                Products.Add(NewEntry(owner, "product", spec.Id, spec, null));
            }

            AfterRegistered(owner, "product '" + spec.Id + "'");
            return true;
        }

        /// <summary>Register a product from raw JSON. productId names it for duplicate detection.</summary>
        public static bool RegisterProductJson(ScamMod owner, string productId, string json)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            var problem = ContentValidation.CheckId(productId, "product");
            if (problem != null)
            {
                Reject(owner, "product", productId, problem);
                return false;
            }
            if (string.IsNullOrEmpty(json) || json.Trim().Length == 0)
            {
                Reject(owner, "product", productId, "empty JSON.");
                return false;
            }

            lock (Products)
            {
                if (!Claim(Products, owner, "product", productId)) return false;
                Products.Add(NewEntry(owner, "product", productId, null, json));
            }

            AfterRegistered(owner, "product '" + productId + "' (raw JSON)");
            return true;
        }

        /// <summary>
        /// Register a caller value. Prefer reusing an existing tag; add one only for genuinely new
        /// information, with an appCompletionSource matching the app that collects it.
        /// </summary>
        public static bool RegisterSentinel(ScamMod owner, SentinelSpec spec)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (spec == null)
            {
                owner.ModLog.LogWarning("Ignoring a null sentinel from " + owner.DisplayName + ".");
                return false;
            }

            var problem = ContentValidation.CheckSentinel(spec);
            if (problem != null)
            {
                Reject(owner, "sentinel", spec.Tag, problem);
                return false;
            }

            lock (Sentinels)
            {
                if (!Claim(Sentinels, owner, "sentinel", spec.Tag)) return false;
                Sentinels.Add(NewEntry(owner, "sentinel", spec.Tag, spec, null));
            }

            AfterRegistered(owner, "sentinel '" + spec.Tag + "'");
            return true;
        }

        /// <summary>One mod's entries, for its own menu page.</summary>
        public static Entry[] ForOwner(string ownerId)
        {
            var mine = new List<Entry>();
            if (string.IsNullOrEmpty(ownerId)) return mine.ToArray();
            lock (ConversationScams) { Collect(ConversationScams, ownerId, mine); }
            lock (ScamTypes) { Collect(ScamTypes, ownerId, mine); }
            lock (Apps) { Collect(Apps, ownerId, mine); }
            lock (Products) { Collect(Products, ownerId, mine); }
            lock (Sentinels) { Collect(Sentinels, ownerId, mine); }
            return mine.ToArray();
        }

        /// <summary>Drop everything one mod registered. Called for you when a mod unloads.</summary>
        public static void UnregisterOwner(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return;
            int removed = 0;
            lock (ConversationScams) { removed += RemoveOwned(ConversationScams, ownerId); }
            lock (ScamTypes) { removed += RemoveOwned(ScamTypes, ownerId); }
            lock (Apps) { removed += RemoveOwned(Apps, ownerId); }
            lock (Products) { removed += RemoveOwned(Products, ownerId); }
            lock (Sentinels) { removed += RemoveOwned(Sentinels, ownerId); }
            if (removed > 0)
            {
                LibraryLog.Info("ContentRegistry dropped " + removed + " entr" +
                                (removed == 1 ? "y" : "ies") + " from " + ownerId +
                                ". Already-built catalogs keep them until the scene reloads.");
            }
        }

        /// <summary>Rejected registrations, one line each, or null when there are none.</summary>
        public static string ConflictReport()
        {
            lock (Rejections)
            {
                if (Rejections.Count == 0) return null;
                var report = new StringBuilder();
                foreach (var rejection in Rejections) report.AppendLine(rejection);
                return report.ToString();
            }
        }

        // ---------------------------------------------------------------- internals

        private static Entry NewEntry(ScamMod owner, string kind, string id, object spec, string json)
        {
            var entry = new Entry();
            entry.Kind = kind;
            entry.Id = id;
            entry.OwnerId = owner.ModId;
            entry.OwnerName = owner.DisplayName;
            entry.Spec = spec;
            entry.Json = json;
            return entry;
        }

        /// <summary>
        /// Claim an id across every owner. Must hold the kind's lock. False means someone else has
        /// it; the rejection names both mods.
        /// </summary>
        private static bool Claim(List<Entry> entries, ScamMod owner, string kind, string id)
        {
            foreach (var existing in entries)
            {
                if (!string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(existing.OwnerId, owner.ModId, StringComparison.Ordinal))
                {
                    Reject(owner, kind, id, "already registered by this mod.");
                    return false;
                }
                var message = kind + " '" + id + "' from " + owner.DisplayName + " [" + owner.ModId +
                              "] is already registered by " + existing.OwnerName + " [" +
                              existing.OwnerId + "]. Rename yours; the first registration keeps working.";
                lock (Rejections) { Rejections.Add("content collision: " + message); }
                owner.ModLog.LogWarning(message);
                return false;
            }
            return true;
        }

        private static void Reject(ScamMod owner, string kind, string id, string reason)
        {
            var message = "Ignoring " + kind + " '" + (id != null ? id : "?") + "' from " +
                          owner.DisplayName + ": " + reason;
            lock (Rejections) { Rejections.Add(message); }
            owner.ModLog.LogWarning(message);
        }

        private static void AfterRegistered(ScamMod owner, string what)
        {
            owner.ModLog.LogInfo("Registered " + what + " for " + owner.DisplayName + ".");
            ContentPatches.EnsureInstalled(owner);
            ContentPatches.ApplyToLiveGame(owner);
        }

        private static void Collect(List<Entry> entries, string ownerId, List<Entry> mine)
        {
            foreach (var entry in entries)
            {
                if (entry.OwnerId == ownerId) mine.Add(entry);
            }
        }

        private static int RemoveOwned(List<Entry> entries, string ownerId)
        {
            int before = entries.Count;
            entries.RemoveAll(delegate (Entry entry) { return entry.OwnerId == ownerId; });
            return before - entries.Count;
        }

        private static class LibraryLog
        {
            internal static void Info(string message)
            {
                try
                {
                    BepInEx.Logging.Logger.CreateLogSource("ScamWYF.Modding").LogInfo(message);
                }
                catch (Exception)
                {
                    // Logging must never break unloading.
                }
            }
        }
    }
}
