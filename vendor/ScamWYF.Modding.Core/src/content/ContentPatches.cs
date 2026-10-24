using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace ScamWYF.Modding.Core.Content
{
    /// <summary>
    /// Merges <see cref="ContentRegistry"/> entries into the game's own catalogs. One Harmony
    /// patch per catalog, installed once for the whole session no matter how many mods register.
    /// </summary>
    /// <remarks>
    /// Everything here resolves game types by name at runtime rather than referencing them, so a
    /// game update that renames something is a clear log message ("expected X, this build has Y")
    /// instead of a MissingMethodException. A patch that cannot install only disables custom
    /// content of that kind; the mods themselves keep running.
    ///
    /// Patch methods take game values as <see cref="object"/> or <see cref="IList"/> so this file
    /// compiles without the game's assemblies: several game types are internal and could not be
    /// named here even with a reference.
    /// </remarks>
    internal static class ContentPatches
    {
        private static readonly object Gate = new object();
        private static bool _installed;
        private static string _installerId;

        /// <summary>
        /// Game types resolved once and reused. Each AccessTools.TypeByName call walks every
        /// loaded assembly, and one of them (ScriptsAssDef) logs a warning on every walk because
        /// of a broken InputSystem reference the game ships with. Resolving once keeps a session
        /// with custom content from flooding the log.
        /// </summary>
        private static readonly Dictionary<string, Type> TypeCache =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        private static Type CachedType(string name)
        {
            lock (TypeCache)
            {
                Type cached;
                if (TypeCache.TryGetValue(name, out cached)) return cached;
                try
                {
                    cached = AccessTools.TypeByName(name);
                }
                catch (Exception)
                {
                    cached = null;
                }
                TypeCache[name] = cached;
                return cached;
            }
        }

        private static Type CachedType(string first, string second)
        {
            return CachedType(first) ?? CachedType(second);
        }

        /// <summary>Install every catalog patch. Idempotent; the first mod registers them.</summary>
        internal static void EnsureInstalled(ScamMod owner)
        {
            if (owner == null) return;
            lock (Gate)
            {
                if (_installed) return;
                _installed = true;
                _installerId = owner.ModId;
            }

            int ok = 0;
            if (PatchScamTypes(owner)) ok++;
            if (PatchConversationScams(owner)) ok++;
            if (PatchSentinels(owner)) ok++;
            if (PatchStore(owner)) ok++;
            if (PatchApps(owner)) ok++;
            owner.ModLog.LogInfo("Content patches installed (" + ok + " of 5 catalogs hooked).");
        }

        /// <summary>Remove the patches when the last content mod unloads.</summary>
        internal static void MaybeUninstall()
        {
            lock (Gate)
            {
                if (!_installed) return;
                if (ContentRegistry.RegisteredConversationScams.Length > 0 ||
                    ContentRegistry.RegisteredScamTypes.Length > 0 ||
                    ContentRegistry.RegisteredApps.Length > 0 ||
                    ContentRegistry.RegisteredProducts.Length > 0 ||
                    ContentRegistry.RegisteredSentinels.Length > 0) return;
                _installed = false;
            }

            if (!string.IsNullOrEmpty(_installerId)) PatchCoordinator.UnpatchOwner(_installerId);
            _installerId = null;
        }

        /// <summary>
        /// Push registered entries into catalogs that already loaded (a mod registering late).
        /// Load-time patches cover the normal path; this only fills in what is already live.
        /// </summary>
        internal static void ApplyToLiveGame(ScamMod owner)
        {
            if (owner == null) return;
            try { MergeScamTypesLive(); }
            catch (Exception ex) { owner.ModLog.LogWarning("Live scam-type merge skipped: " + ex.Message); }
            try { MergeConversationScamsLive(); }
            catch (Exception ex) { owner.ModLog.LogWarning("Live conversation-scam merge skipped: " + ex.Message); }
            try { MergeSentinelsLive(); }
            catch (Exception ex) { owner.ModLog.LogWarning("Live sentinel merge skipped: " + ex.Message); }
            try { MergeStoreLive(); }
            catch (Exception ex) { owner.ModLog.LogWarning("Live store merge skipped: " + ex.Message); }
        }

        // ---------------------------------------------------------------- catalog: scam types

        private static bool PatchScamTypes(ScamMod owner)
        {
            var target = FindStaticMethod(owner, "ScamTypeLibrary", "LoadAll", 0);
            if (target == null) return false;
            return PatchCoordinator.TryPatch(owner, target, "merge custom scam types",
                new HarmonyMethod(AccessTools.Method(typeof(ContentPatches), "MergeScamTypes")));
        }

        /// <summary>Runs after ScamTypeLibrary.LoadAll: appends registered scam types once.</summary>
        public static void MergeScamTypes(object __result)
        {
            var list = __result as IList;
            if (list == null) return;
            var entries = ContentRegistry.RegisteredScamTypes;
            if (entries.Length == 0) return;
            var definitionType = CachedType("ScamTypeDefinition");
            if (definitionType == null) return;

            foreach (var entry in entries)
            {
                try
                {
                    var spec = entry.Spec as ScamTypeSpec;
                    if (spec == null || HasId(list, spec.Id)) continue;
                    var json = BuildScamTypeJson(spec).ToString();
                    var definition = JsonDeserialize(json, definitionType);
                    if (definition == null) continue;
                    MarkCallerAiValid(definition);
                    list.Add(definition);
                }
                catch (Exception)
                {
                    // One bad entry must not poison the whole catalog.
                }
            }

            SortByDisplayOrder(list);
        }

        private static void MergeScamTypesLive()
        {
            var libraryType = CachedType("ScamTypeLibrary");
            if (libraryType == null) return;
            var cached = GetStaticField(libraryType, "cachedTypes") as IList;
            if (cached == null) return;
            MergeScamTypes(cached);
        }

        // ------------------------------------------------------- catalog: conversation scams

        private static bool PatchConversationScams(ScamMod owner)
        {
            var target = FindStaticMethod(owner, "ConversationScamCatalog", "Load", 0);
            if (target == null) return false;
            return PatchCoordinator.TryPatch(owner, target, "merge custom conversation scams",
                new HarmonyMethod(AccessTools.Method(typeof(ContentPatches), "MergeConversationScams")));
        }

        /// <summary>
        /// Runs after ConversationScamCatalog.Load: replaces the cached array with base + custom.
        /// </summary>
        public static void MergeConversationScams(ref object __result)
        {
            var entries = ContentRegistry.RegisteredConversationScams;
            if (entries.Length == 0) return;

            var catalogType = CachedType("ConversationScamCatalog");
            var definitionType = CachedType("ConversationScamDefinition");
            if (catalogType == null || definitionType == null) return;

            try
            {
                Array current = __result as Array;
                var fresh = new List<object>();
                if (current != null)
                {
                    foreach (var item in current) fresh.Add(item);
                }

                foreach (var entry in entries)
                {
                    var spec = entry.Spec as ConversationScamSpec;
                    if (spec == null || HasAppId(fresh, spec.AppId)) continue;
                    var json = BuildConversationScamJson(spec).ToString();
                    var definition = JsonDeserialize(json, definitionType);
                    if (definition == null) continue;
                    fresh.Add(definition);
                }

                if (current != null && fresh.Count == current.Length) return;

                var merged = Array.CreateInstance(definitionType, fresh.Count);
                for (int i = 0; i < fresh.Count; i++) merged.SetValue(fresh[i], i);
                SetStaticField(catalogType, "cached", merged);
                __result = merged;
            }
            catch (Exception)
            {
                // Leave the base catalog alone rather than half-merged.
            }
        }

        private static void MergeConversationScamsLive()
        {
            var catalogType = CachedType("ConversationScamCatalog");
            if (catalogType == null) return;
            var cached = GetStaticField(catalogType, "cached") as Array;
            if (cached == null) return;
            object result = cached;
            MergeConversationScams(ref result);
        }

        // ---------------------------------------------------------------- catalog: sentinels

        private static bool PatchSentinels(ScamMod owner)
        {
            var target = FindStaticMethod(owner, "SentinelValueFactory", "EnsureLoaded", 0);
            if (target == null) return false;
            return PatchCoordinator.TryPatch(owner, target, "merge custom sentinels",
                new HarmonyMethod(AccessTools.Method(typeof(ContentPatches), "MergeSentinels")));
        }

        /// <summary>Runs after SentinelValueFactory.EnsureLoaded: adds registered tags once.</summary>
        public static void MergeSentinels()
        {
            var entries = ContentRegistry.RegisteredSentinels;
            if (entries.Length == 0) return;
            var factoryType = CachedType("SentinelValueFactory");
            var definitionType = CachedType("SentinelDefinition");
            if (factoryType == null || definitionType == null) return;

            var byTag = GetStaticField(factoryType, "definitionsByTag") as IDictionary;
            var all = GetStaticField(factoryType, "definitions") as IList;
            if (byTag == null) return;

            foreach (var entry in entries)
            {
                try
                {
                    var spec = entry.Spec as SentinelSpec;
                    if (spec == null || byTag.Contains(spec.Tag)) continue;
                    var json = BuildSentinelJson(spec).ToString();
                    var definition = JsonDeserialize(json, definitionType);
                    if (definition == null) continue;
                    byTag[spec.Tag] = definition;
                    if (all != null) all.Add(definition);
                }
                catch (Exception)
                {
                    // One bad tag must not poison the factory.
                }
            }
        }

        private static void MergeSentinelsLive()
        {
            var factoryType = CachedType("SentinelValueFactory");
            if (factoryType == null) return;
            var loaded = GetStaticField(factoryType, "isLoaded");
            if (!(loaded is bool) || !(bool)loaded) return;
            MergeSentinels();
        }

        // ---------------------------------------------------------------- catalog: store

        private static bool PatchStore(ScamMod owner)
        {
            var catalogType = CachedType("KolkataOsApplicationStoreCatalog",
                "Kolkata.UI.KolkataOsApplicationStoreCatalog");
            if (catalogType == null)
            {
                owner.ModLog.LogWarning("This build has no KolkataOsApplicationStoreCatalog (" +
                                        GameBuild.Describe() + "). Custom store products are off.");
                return false;
            }
            var target = FindMethod(catalogType, "SetManifest", true, 1);
            if (target == null)
            {
                owner.ModLog.LogWarning("KolkataOsApplicationStoreCatalog.SetManifest is gone (" +
                                        GameBuild.Describe() + "). Custom store products are off.");
                return false;
            }
            return PatchCoordinator.TryPatch(owner, target, "merge custom store products",
                new HarmonyMethod(AccessTools.Method(typeof(ContentPatches), "MergeProducts")));
        }

        /// <summary>Runs before SetManifest indexes the file: appends registered products.</summary>
        public static void MergeProducts(object manifest)
        {
            if (manifest == null) return;
            var entries = ContentRegistry.RegisteredProducts;
            if (entries.Length == 0) return;

            try
            {
                var manifestType = manifest.GetType();
                var productsField = manifestType.GetField("products",
                    BindingFlags.Public | BindingFlags.Instance);
                if (productsField == null) return;
                var current = productsField.GetValue(manifest) as Array;
                Type elementType = current != null
                    ? current.GetType().GetElementType()
                    : CachedType("KolkataOsApplicationProductDefinition",
                        "Kolkata.UI.KolkataOsApplicationProductDefinition");
                if (elementType == null) return;

                var fresh = new List<object>();
                if (current != null)
                {
                    foreach (var item in current) fresh.Add(item);
                }

                foreach (var entry in entries)
                {
                    if (HasProductId(fresh, entry.Id)) continue;
                    object product = entry.Json != null
                        ? JsonDeserialize(entry.Json, elementType)
                        : JsonDeserialize(BuildProductJson(entry.Spec as StoreProductSpec).ToString(),
                            elementType);
                    if (product == null) continue;
                    fresh.Add(product);
                }

                if (current != null && fresh.Count == current.Length) return;
                var merged = Array.CreateInstance(elementType, fresh.Count);
                for (int i = 0; i < fresh.Count; i++) merged.SetValue(fresh[i], i);
                productsField.SetValue(manifest, merged);
            }
            catch (Exception)
            {
                // Leave the store file alone rather than half-merged.
            }
        }

        private static void MergeStoreLive()
        {
            var catalogType = CachedType("KolkataOsApplicationStoreCatalog",
                "Kolkata.UI.KolkataOsApplicationStoreCatalog");
            if (catalogType == null) return;
            var manifest = GetStaticField(catalogType, "s_manifest");
            if (manifest == null) return;
            // Re-running the prefix logic against the live manifest merges into the arrays, then
            // re-index by re-setting the manifest through the game's own setter.
            MergeProducts(manifest);
            var setter = FindMethod(catalogType, "SetManifest", true, 1);
            if (setter != null)
            {
                try { setter.Invoke(null, new object[] { manifest }); }
                catch (Exception) { }
            }
        }

        // ---------------------------------------------------------------- catalog: OS apps

        private static bool PatchApps(ScamMod owner)
        {
            var controllerType = CachedType("KolkataOsController",
                "Kolkata.UI.KolkataOsController");
            if (controllerType == null)
            {
                owner.ModLog.LogWarning("This build has no KolkataOsController (" +
                                        GameBuild.Describe() + "). Custom apps are off.");
                return false;
            }

            bool ok = true;

            var desktop = FindMethod(controllerType, "BuildDesktop", false, -1);
            if (desktop == null)
            {
                owner.ModLog.LogWarning("KolkataOsController.BuildDesktop is gone (" +
                                        GameBuild.Describe() + "). Custom apps load without desktop icons.");
            }
            else if (!PatchCoordinator.TryPatch(owner, desktop, "merge custom OS apps",
                new HarmonyMethod(AccessTools.Method(typeof(ContentPatches), "MergeApps"))))
            {
                ok = false;
            }

            var lookup = FindMethod(controllerType, "GetAppDefinitionById", false, 1);
            if (lookup == null)
            {
                owner.ModLog.LogWarning("KolkataOsController.GetAppDefinitionById is gone (" +
                                        GameBuild.Describe() + "). Late-registered apps may not resolve.");
            }
            else if (!PatchCoordinator.TryPatch(owner, lookup, "resolve custom OS apps",
                new HarmonyMethod(AccessTools.Method(typeof(ContentPatches), "LookupCustomApp"))))
            {
                ok = false;
            }

            return ok;
        }

        /// <summary>Runs before BuildDesktop creates runtimes: appends registered app definitions.</summary>
        public static void MergeApps(object __instance)
        {
            if (__instance == null) return;
            var entries = ContentRegistry.RegisteredApps;
            if (entries.Length == 0) return;

            try
            {
                var instanceType = __instance.GetType();
                var listField = FirstFieldOfListType(instanceType, "m_appDefinitions");
                if (listField == null) return;
                var list = listField.GetValue(__instance) as IList;
                if (list == null) return;

                var appType = AppDefinitionType();
                if (appType == null) return;

                foreach (var entry in entries)
                {
                    if (HasId(list, entry.Id)) continue;
                    object definition = entry.Json != null
                        ? JsonDeserialize(entry.Json, appType)
                        : JsonDeserialize(BuildAppJson(entry.Spec as OsAppSpec).ToString(), appType);
                    if (definition == null) continue;
                    NormalizeApp(__instance, definition);
                    list.Add(definition);
                }
            }
            catch (Exception)
            {
                // Leave the desktop alone rather than half-merged.
            }
        }

        /// <summary>
        /// Falls back to the registry when the game has no app by that id (late registration,
        /// before the next desktop rebuild).
        /// </summary>
        public static void LookupCustomApp(string appId, ref object __result, object __instance)
        {
            if (__result != null || string.IsNullOrEmpty(appId)) return;
            var entries = ContentRegistry.RegisteredApps;
            if (entries.Length == 0) return;

            try
            {
                foreach (var entry in entries)
                {
                    if (!string.Equals(entry.Id, appId.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;
                    var appType = AppDefinitionType();
                    if (appType == null) return;
                    object definition = entry.Json != null
                        ? JsonDeserialize(entry.Json, appType)
                        : JsonDeserialize(BuildAppJson(entry.Spec as OsAppSpec).ToString(), appType);
                    if (definition == null) return;
                    if (__instance != null)
                    {
                        NormalizeApp(__instance, definition);
                        var listField = FirstFieldOfListType(__instance.GetType(), "m_appDefinitions");
                        var list = listField != null ? listField.GetValue(__instance) as IList : null;
                        if (list != null && !HasId(list, entry.Id)) list.Add(definition);
                    }
                    __result = definition;
                    return;
                }
            }
            catch (Exception)
            {
                // A failed lookup simply stays failed.
            }
        }

        // ---------------------------------------------------------------- JSON builders

        internal static JObject BuildScamTypeJson(ScamTypeSpec spec)
        {
            var json = new JObject();
            json["id"] = spec.Id;
            json["displayName"] = spec.DisplayName;
            json["iconName"] = spec.IconName != null ? spec.IconName : "shopping-bag";
            json["appId"] = spec.AppId;
            json["displayOrder"] = spec.DisplayOrder;
            var sentinels = new JArray();
            if (spec.RelevantSentinels != null)
            {
                foreach (var sentinel in spec.RelevantSentinels) sentinels.Add(sentinel);
            }
            json["relevantSentinels"] = sentinels;
            json["appCompletionSource"] = spec.AppCompletionSource;
            json["callerPrompt"] = spec.CallerPrompt;
            json["testOpeningPlayerLine"] = spec.TestOpeningPlayerLine;
            return json;
        }

        internal static JObject BuildConversationScamJson(ConversationScamSpec spec)
        {
            var json = new JObject();
            json["id"] = spec.Id;
            json["productId"] = spec.ProductId;
            json["appId"] = spec.AppId;
            json["title"] = spec.Title;
            if (!string.IsNullOrEmpty(spec.CallerPrompt)) json["callerPrompt"] = spec.CallerPrompt;
            if (!string.IsNullOrEmpty(spec.Sentinel)) json["sentinel"] = spec.Sentinel;
            json["reward"] = spec.Reward;
            if (spec.RewardTiers != null && spec.RewardTiers.Length > 0)
            {
                var tiers = new JArray();
                foreach (int tier in spec.RewardTiers) tiers.Add(tier);
                json["rewardTiers"] = tiers;
            }
            json["endCallOnCompletion"] = spec.EndCallOnCompletion;
            var objectives = new JArray();
            if (spec.Objectives != null)
            {
                foreach (var objective in spec.Objectives)
                {
                    var item = new JObject();
                    item["id"] = objective.Id;
                    item["text"] = objective.Text;
                    if (!string.IsNullOrEmpty(objective.Hint)) item["hint"] = objective.Hint;
                    if (!string.IsNullOrEmpty(objective.Criteria))
                        item["criteria"] = objective.Criteria;
                    else
                        item["criteria"] = objective.Text;
                    if (!string.IsNullOrEmpty(objective.RequiredSentinel))
                        item["requiredSentinel"] = objective.RequiredSentinel;
                    var prereqs = new JArray();
                    if (objective.Prerequisites != null)
                    {
                        foreach (var prereq in objective.Prerequisites) prereqs.Add(prereq);
                    }
                    item["prerequisites"] = prereqs;
                    item["optional"] = objective.Optional;
                    item["final"] = objective.Final;
                    item["requiresSubmission"] = objective.RequiresSubmission;
                    if (objective.RequiresSubmission) item["requiresTrustIncrease"] = false;
                    objectives.Add(item);
                }
            }
            json["objectives"] = objectives;
            return json;
        }

        internal static JObject BuildAppJson(OsAppSpec spec)
        {
            var json = new JObject();
            json["id"] = spec.Id;
            json["title"] = spec.Title;
            if (!string.IsNullOrEmpty(spec.IconResource)) json["iconResource"] = spec.IconResource;
            if (!string.IsNullOrEmpty(spec.IconSvg)) json["iconSvg"] = spec.IconSvg;
            if (!string.IsNullOrEmpty(spec.IconPath)) json["iconPath"] = spec.IconPath;
            json["contentType"] = spec.ContentType;
            json["pinned"] = spec.Pinned;
            json["pinnedOrder"] = spec.PinnedOrder;
            json["showOnDesktop"] = spec.ShowOnDesktop;
            json["desktopHiddenByDefault"] = spec.DesktopHiddenByDefault;
            json["desktopOrder"] = spec.DesktopOrder;
            if (!string.IsNullOrEmpty(spec.DesktopAnchor)) json["desktopAnchor"] = spec.DesktopAnchor;
            json["lobbyOnly"] = spec.LobbyOnly;
            json["windowLeft"] = spec.WindowLeft != 0f ? spec.WindowLeft : 300f;
            json["windowTop"] = spec.WindowTop != 0f ? spec.WindowTop : 120f;
            json["windowWidth"] = spec.WindowWidth != 0f ? spec.WindowWidth : 640f;
            json["windowHeight"] = spec.WindowHeight != 0f ? spec.WindowHeight : 480f;
            if (spec.MinWindowWidth > 0f) json["minWindowWidth"] = spec.MinWindowWidth;
            if (spec.MinWindowHeight > 0f) json["minWindowHeight"] = spec.MinWindowHeight;
            json["accentColor"] = string.IsNullOrEmpty(spec.AccentColor) ? "#0078D7" : spec.AccentColor;
            json["initialState"] = string.IsNullOrEmpty(spec.InitialState) ? "closed" : spec.InitialState;
            if (!string.IsNullOrEmpty(spec.PlayerPrefsKey)) json["playerPrefsKey"] = spec.PlayerPrefsKey;

            var access = new JObject();
            var specAccess = spec.Access;
            string mode = specAccess != null && !string.IsNullOrEmpty(specAccess.Mode)
                ? specAccess.Mode
                : "default";
            access["mode"] = mode;
            if (specAccess != null && !string.IsNullOrEmpty(specAccess.ProductId))
                access["productId"] = specAccess.ProductId;
            access["unlockDay"] = specAccess != null ? Math.Max(1, specAccess.UnlockDay) : 1;
            json["access"] = access;

            var content = new JObject();
            var specContent = spec.Content;
            if (specContent != null)
            {
                if (!string.IsNullOrEmpty(specContent.Title)) content["title"] = specContent.Title;
                if (!string.IsNullOrEmpty(specContent.Subtitle)) content["subtitle"] = specContent.Subtitle;
                if (!string.IsNullOrEmpty(specContent.Body)) content["body"] = specContent.Body;
                if (specContent.Binding != null)
                {
                    var binding = new JObject();
                    binding["kind"] = string.IsNullOrEmpty(specContent.Binding.Kind)
                        ? "scenarioClaim"
                        : specContent.Binding.Kind;
                    if (!string.IsNullOrEmpty(specContent.Binding.CompletionSource))
                        binding["completionSource"] = specContent.Binding.CompletionSource;
                    if (!string.IsNullOrEmpty(specContent.Binding.SubmitLabel))
                        binding["submitLabel"] = specContent.Binding.SubmitLabel;
                    content["binding"] = binding;
                }
                if (specContent.Fields != null && specContent.Fields.Length > 0)
                {
                    var fields = new JArray();
                    foreach (var field in specContent.Fields)
                    {
                        var item = new JObject();
                        if (!string.IsNullOrEmpty(field.Id)) item["id"] = field.Id;
                        if (!string.IsNullOrEmpty(field.Label)) item["label"] = field.Label;
                        if (!string.IsNullOrEmpty(field.Placeholder))
                            item["placeholder"] = field.Placeholder;
                        if (!string.IsNullOrEmpty(field.ItemId)) item["itemId"] = field.ItemId;
                        item["multiline"] = field.Multiline;
                        item["password"] = field.Password;
                        item["required"] = field.Required;
                        item["persistLocal"] = field.PersistLocal;
                        fields.Add(item);
                    }
                    content["fields"] = fields;
                }
            }
            json["content"] = content;

            string callerPrompt = spec.CallerPrompt;
            JObject promptObjectives = null;
            JObject promptDescriptions = null;
            if (string.Equals(spec.ContentType, "conversationScam", StringComparison.Ordinal))
            {
                var scam = FindConversationScam(spec.Id);
                if (scam != null)
                {
                    if (string.IsNullOrEmpty(callerPrompt)) callerPrompt = scam.CallerPrompt;
                    promptObjectives = new JObject();
                    promptDescriptions = new JObject();
                    if (scam.Objectives != null)
                    {
                        foreach (var objective in scam.Objectives)
                        {
                            promptObjectives[objective.Id] = objective.Text;
                            promptDescriptions[objective.Id] = objective.Text;
                        }
                    }
                }
            }
            if (!string.IsNullOrEmpty(callerPrompt) || promptObjectives != null)
            {
                var prompts = new JObject();
                if (!string.IsNullOrEmpty(callerPrompt)) prompts["callerPrompt"] = callerPrompt;
                if (promptObjectives != null) prompts["objectives"] = promptObjectives;
                if (promptDescriptions != null) prompts["objectiveDescriptions"] = promptDescriptions;
                json["prompts"] = prompts;
            }

            return json;
        }

        internal static JObject BuildProductJson(StoreProductSpec spec)
        {
            var json = new JObject();
            json["id"] = spec.Id;
            json["appId"] = spec.AppId;
            if (!string.IsNullOrEmpty(spec.ScamId)) json["scamId"] = spec.ScamId;
            json["title"] = spec.Title;
            json["description"] = spec.Description != null ? spec.Description : spec.Title;
            if (!string.IsNullOrEmpty(spec.IconResource)) json["iconResource"] = spec.IconResource;
            if (!string.IsNullOrEmpty(spec.IconSvg)) json["iconSvg"] = spec.IconSvg;
            if (!string.IsNullOrEmpty(spec.IconPath)) json["iconPath"] = spec.IconPath;
            if (!string.IsNullOrEmpty(spec.IconText)) json["iconText"] = spec.IconText;
            json["price"] = Math.Max(0, spec.Price);
            json["sellPrice"] = Math.Max(0, spec.SellPrice);
            json["ownership"] = string.IsNullOrEmpty(spec.Ownership) ? "run" : spec.Ownership;
            json["sellable"] = spec.Sellable;
            if (!string.IsNullOrEmpty(spec.Category)) json["category"] = spec.Category;
            if (!string.IsNullOrEmpty(spec.AppCategory)) json["appCategory"] = spec.AppCategory;
            json["accessMode"] = string.IsNullOrEmpty(spec.AccessMode) ? "purchase" : spec.AccessMode;
            json["unlockDay"] = Math.Max(0, spec.UnlockDay);
            if (!string.IsNullOrEmpty(spec.AccentColor)) json["accentColor"] = spec.AccentColor;
            return json;
        }

        internal static JObject BuildSentinelJson(SentinelSpec spec)
        {
            var json = new JObject();
            json["tag"] = spec.Tag;
            json["label"] = spec.Label;
            json["valueEach"] = Math.Max(0, spec.ValueEach);
            json["appCompletionSource"] = spec.AppCompletionSource;
            var generator = new JObject();
            generator["type"] = string.IsNullOrEmpty(spec.GeneratorType)
                ? "alphanumeric_groups"
                : spec.GeneratorType;
            if (spec.GeneratorGroups != null && spec.GeneratorGroups.Length > 0)
            {
                var groups = new JArray();
                foreach (int group in spec.GeneratorGroups) groups.Add(group);
                generator["groups"] = groups;
            }
            if (!string.IsNullOrEmpty(spec.GeneratorSeparator))
                generator["separator"] = spec.GeneratorSeparator;
            if (spec.GeneratorDigits > 0) generator["digits"] = spec.GeneratorDigits;
            json["generator"] = generator;
            json["spokenFormat"] = string.IsNullOrEmpty(spec.SpokenFormat)
                ? "char_groups"
                : spec.SpokenFormat;
            json["rdpFileNames"] = new JArray();
            return json;
        }

        // ---------------------------------------------------------------- reflection helpers

        private static MethodBase FindStaticMethod(ScamMod owner, string typeName, string methodName,
            int paramCount)
        {
            var type = CachedType(typeName);
            if (type == null)
            {
                owner.ModLog.LogWarning("This build has no " + typeName + " (" +
                                        GameBuild.Describe() + "). Custom content of that kind is off.");
                return null;
            }
            var found = FindMethod(type, methodName, true, paramCount);
            if (found == null)
            {
                owner.ModLog.LogWarning(typeName + "." + methodName + " is gone (" +
                                        GameBuild.Describe() + "). Custom content of that kind is off.");
            }
            return found;
        }

        private static MethodBase FindMethod(Type type, string methodName, bool wantStatic,
            int paramCount)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static;
            foreach (var candidate in type.GetMethods(flags))
            {
                if (candidate.Name != methodName) continue;
                if (candidate.IsStatic != wantStatic) continue;
                if (paramCount >= 0 && candidate.GetParameters().Length != paramCount) continue;
                return candidate;
            }
            return null;
        }

        private static Type AppDefinitionType()
        {
            return CachedType("KolkataOsAppDefinition",
                "Kolkata.UI.KolkataOsAppDefinition");
        }

        private static FieldInfo FirstFieldOfListType(Type instanceType, string fieldName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance;
            var field = instanceType.GetField(fieldName, flags);
            if (field != null && typeof(IList).IsAssignableFrom(field.FieldType)) return field;
            return null;
        }

        private static void NormalizeApp(object controller, object definition)
        {
            try
            {
                var normalize = controller.GetType().GetMethod("NormalizeDefinition",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (normalize == null) return;
                var parameters = normalize.GetParameters();
                if (parameters.Length != 1) return;
                if (!parameters[0].ParameterType.IsInstanceOfType(definition)) return;
                normalize.Invoke(controller, new object[] { definition });
            }
            catch (Exception)
            {
                // Defaults are already in the JSON; normalization is a nicety.
            }
        }

        private static object JsonDeserialize(string json, Type type)
        {
            try
            {
                var method = typeof(Newtonsoft.Json.JsonConvert).GetMethod("DeserializeObject",
                    new Type[] { typeof(string), typeof(Type) });
                if (method == null) return null;
                return method.Invoke(null, new object[] { json, type });
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static object GetStaticField(Type type, string name)
        {
            try
            {
                var field = type.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null) return null;
                return field.GetValue(null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SetStaticField(Type type, string name, object value)
        {
            try
            {
                var field = type.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null) return;
                field.SetValue(null, value);
            }
            catch (Exception)
            {
                // The caller decides whether a missed write matters.
            }
        }

        private static string ReadString(object item, string fieldName)
        {
            if (item == null) return null;
            try
            {
                var field = item.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) return null;
                var value = field.GetValue(item);
                return value != null ? value.ToString() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool HasId(IList list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return false;
            foreach (var item in list)
            {
                if (string.Equals(ReadString(item, "id"), id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool HasAppId(List<object> items, string appId)
        {
            if (string.IsNullOrEmpty(appId)) return false;
            foreach (var item in items)
            {
                if (string.Equals(ReadString(item, "appId"), appId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool HasProductId(List<object> items, string productId)
        {
            if (string.IsNullOrEmpty(productId)) return false;
            foreach (var item in items)
            {
                if (string.Equals(ReadString(item, "id"), productId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static ConversationScamSpec FindConversationScam(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return null;
            foreach (var entry in ContentRegistry.RegisteredConversationScams)
            {
                var spec = entry.Spec as ConversationScamSpec;
                if (spec != null &&
                    string.Equals(spec.AppId, appId, StringComparison.OrdinalIgnoreCase)) return spec;
            }
            return null;
        }

        private static void MarkCallerAiValid(object definition)
        {
            try
            {
                var setter = definition.GetType().GetMethod("SetCallerAiConfigurationValidity",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (setter != null) setter.Invoke(definition, new object[] { true });
            }
            catch (Exception)
            {
                // Validity is advisory; the scam still loads.
            }
        }

        private static void SortByDisplayOrder(IList list)
        {
            try
            {
                var items = new List<object>();
                foreach (var item in list) items.Add(item);
                items.Sort(delegate (object left, object right)
                {
                    int leftOrder = ReadInt(left, "displayOrder");
                    int rightOrder = ReadInt(right, "displayOrder");
                    int compared = leftOrder.CompareTo(rightOrder);
                    if (compared != 0) return compared;
                    return string.Compare(ReadString(left, "displayName"),
                        ReadString(right, "displayName"), StringComparison.OrdinalIgnoreCase);
                });
                list.Clear();
                foreach (var item in items) list.Add(item);
            }
            catch (Exception)
            {
                // Order is cosmetic; the entries are already added.
            }
        }

        private static int ReadInt(object item, string fieldName)
        {
            if (item == null) return 0;
            try
            {
                var field = item.GetType().GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) return 0;
                return Convert.ToInt32(field.GetValue(item));
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
