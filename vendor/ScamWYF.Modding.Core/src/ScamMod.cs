using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;
using ScamWYF.Modding.Core.Ui;

namespace ScamWYF.Modding.Core
{
    /// <summary>Where a mod got to during load.</summary>
    public enum ModState
    {
        /// <summary>Registered, but OnModLoad has not finished.</summary>
        Loading,

        /// <summary>Loaded and running.</summary>
        Active,

        /// <summary>OnModLoad threw. The mod is inert and says why.</summary>
        Failed,

        /// <summary>ModRegistry rejected it, usually a duplicate id.</summary>
        Rejected
    }

    /// <summary>
    /// Base class for a Scam With Your Friends mod. Gives every mod the same identity, the same
    /// failure handling and the same shared services, so two mods cannot quietly fight over the
    /// same Harmony patch, the same hotkey or the same menu tab.
    /// </summary>
    /// <remarks>
    /// Identity is read from the [BepInPlugin] attribute rather than restated in code, so the name
    /// the mod list shows and the name the rest of the session uses can never drift apart.
    ///
    /// Do not declare Awake/OnDestroy/Update: they are sealed here. Use OnModLoad and OnModUnload.
    ///
    /// Beyond the hooks, a mod gets three things without asking:
    ///
    ///  - a tab in the universal menu, showing its patches and its settings, unless it registered a
    ///    better one itself;
    ///  - a hotkey that opens that menu, owned by the library rather than by any one mod;
    ///  - hot reload of its own config file, if it asked for it with <see cref="WatchConfig"/>.
    /// </remarks>
    public abstract class ScamMod : BaseUnityPlugin
    {
        /// <summary>The [BepInPlugin] GUID. Unique per mod, enforced at load.</summary>
        public string ModId { get; private set; }

        public string DisplayName { get; private set; }

        public string ModVersion { get; private set; }

        public ModState State { get; private set; }

        /// <summary>Why the mod failed, or null. Shown in the mod list.</summary>
        public string FailureReason { get; private set; }

        /// <summary>This mod's own logger. The shared services log through it too.</summary>
        public ManualLogSource ModLog { get; private set; }

        private ModSettings _settings;
        private ConfigEditor _configEditor;
        private ConfigWatcher _watcher;
        private bool _hasDefaultPage;

        /// <summary>
        /// This mod's settings, with range checking that survives a hand-edited file. Touching this
        /// also builds the in-game editor for the same file.
        /// </summary>
        public ModSettings Settings
        {
            get
            {
                if (_settings == null) _settings = new ModSettings(Config, ModLog, ModId);
                return _settings;
            }
        }

        /// <summary>
        /// An editor for this mod's config file, drawn from what BepInEx already knows about it. Built
        /// on first use; <see cref="ScamMod.Settings"/> builds it too.
        /// </summary>
        public ConfigEditor ConfigEditor
        {
            get
            {
                if (_configEditor == null) _configEditor = new ConfigEditor(Config, ModLog, ModId);
                return _configEditor;
            }
        }

        /// <summary>Watches this mod's config file, or null when it is not watching.</summary>
        public ConfigWatcher Watcher
        {
            get { return _watcher; }
        }

        /// <summary>Whether this mod has any settings bound. False until <see cref="Settings"/> is used.</summary>
        public bool HasSettings
        {
            get { return _settings != null; }
        }

        /// <summary>
        /// Whether the library gave this mod a menu tab automatically, rather than the mod registering
        /// one itself. True means the default page is showing its patches and settings.
        /// </summary>
        public bool HasDefaultMenuPage
        {
            get { return _hasDefaultPage; }
        }

        private void Awake()
        {
            ModLog = Logger;
            State = ModState.Loading;

            var attribute = (BepInPlugin)Attribute.GetCustomAttribute(GetType(), typeof(BepInPlugin));
            if (attribute == null)
            {
                Fail("no [BepInPlugin] attribute on " + GetType().FullName +
                     ". Every mod needs one so the mod list can identify it.");
                return;
            }

            ModId = attribute.GUID ?? "";
            DisplayName = attribute.Name ?? "";
            ModVersion = attribute.Version != null ? attribute.Version.ToString() : "";

            if (ModId.Length == 0)
            {
                Fail("[BepInPlugin] has an empty GUID.");
                return;
            }

            if (!ModRegistry.Register(this))
            {
                State = ModState.Rejected;
                FailureReason = "another plugin already registered as " + ModId;
                ModLog.LogError(FailureReason + ". This copy is disabled; change the GUID in its [BepInPlugin].");
                enabled = false;
                return;
            }

            // From here on, everything this mod registered has to come back off on unload.
            _hooksRan = true;

            // Idempotent, and called before OnModLoad so a mod can assume the menu, its hotkey and the
            // diagnostics tabs already exist. Cheap after the first mod: it returns immediately.
            LibraryRuntime.Start();

            try
            {
                ModLog.LogInfo("Starting " + DisplayName + " " + ModVersion + " on " + GameBuild.Describe() + ".");
                OnModLoad();
                State = ModState.Active;

                GiveMenuDefaults();
            }
            catch (Exception ex)
            {
                // A mod that throws during load must not take the session with it.
                Fail(ex);
                TearDown();
            }
        }

        private void OnDestroy()
        {
            if (!_hooksRan) return;
            _hooksRan = false;
            TearDown();
        }

        private void TearDown()
        {
            try
            {
                OnModUnload();
            }
            catch (Exception ex)
            {
                ModLog.LogError("OnModUnload threw: " + ex);
            }

            ModRegistry.Unregister(this);
            PatchCoordinator.UnpatchOwner(ModId);
            Content.ContentRegistry.UnregisterOwner(ModId);
            Content.ContentPatches.MaybeUninstall();
            Hotkeys.RemoveOwner(ModId);
            UiWindows.RemoveOwner(ModId);
            ModMenu.RemoveOwner(ModId);

            if (_watcher != null)
            {
                LibraryRuntime.Forget(_watcher);
                _watcher.Dispose();
                _watcher = null;
            }
        }

        /// <summary>
        /// Do the work. Anything thrown here is logged and turns the mod inert instead of
        /// breaking the game.
        /// </summary>
        protected virtual void OnModLoad()
        {
        }

        /// <summary>Undo anything OnModLoad did. Shared services are cleaned up for you.</summary>
        protected virtual void OnModUnload()
        {
        }

        /// <summary>
        /// Re-read the config file now and re-apply anything cached from it. Call this after writing
        /// settings yourself; a change made in a text editor does it for you.
        /// </summary>
        public void ReloadConfig()
        {
            try
            {
                Config.Reload();
            }
            catch (Exception ex)
            {
                ModLog.LogWarning("Could not reload " + Config.ConfigFilePath + ": " + ex.Message +
                                  ". The values in use are the ones from before.");
                return;
            }

            OnConfigReloaded();
        }

        /// <summary>
        /// Called after the config file has been re-read, whether from a text-editor change or from
        /// <see cref="ReloadConfig"/>. Override to re-apply anything cached at load.
        /// </summary>
        protected virtual void OnConfigReloaded()
        {
        }

        /// <summary>
        /// Start watching this mod's config file, so an edit in a text editor takes effect within about
        /// half a second instead of needing a restart. Optional: a mod that never calls this still
        /// works, it just does not reload.
        /// </summary>
        public ConfigWatcher WatchConfig()
        {
            if (_watcher != null) return _watcher;

            try
            {
                _watcher = new ConfigWatcher(Config, ModLog, ModId);
                _watcher.Reloaded += OnConfigReloaded;
                LibraryRuntime.Register(_watcher);
                ModLog.LogInfo("Watching " + Config.ConfigFilePath +
                               " for changes; edits there take effect without a restart.");
            }
            catch (Exception ex)
            {
                // Hot reload is a convenience. A mod that cannot have it still works.
                ModLog.LogWarning("Could not watch the config file for changes: " + ex.Message +
                                  ". Edits will need a restart to take effect.");
            }

            return _watcher;
        }

        /// <summary>Give up cleanly: log why, record it for the mod list, and switch the mod off.</summary>
        protected void Fail(string reason)
        {
            FailureReason = reason;
            State = ModState.Failed;
            ModLog.LogError(DisplayName.Length > 0 ? DisplayName + ": " + reason : reason);
            enabled = false;
        }

        /// <summary>Give up cleanly because of an exception.</summary>
        protected void Fail(Exception ex)
        {
            Fail(ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
        }

        private bool _hooksRan;

        /// <summary>
        /// Make sure this mod is reachable. A mod that registered no menu page of its own gets a default
        /// one showing its patches, its hotkeys and its settings, so a mod nothing can reach is a mod
        /// nobody can configure.
        /// </summary>
        private void GiveMenuDefaults()
        {
            if (ModMenu.HasPage(this)) return;

            _hasDefaultPage = true;
            ModMenu.AddPage(this, DisplayName, delegate (UnityEngine.UIElements.VisualElement page)
            {
                BuildOwnPage(page);
            });
        }

        private void BuildOwnPage(UnityEngine.UIElements.VisualElement page)
        {
            // No Scroll here: the menu's page host is already one, and nesting a second scroll view inside
            // it makes the wheel scroll the inner one first, which reads as a stuck menu.
            var scroll = page;

            Widgets.FieldRow(scroll, "Mod", DisplayName + " " + ModVersion);
            Widgets.FieldRow(scroll, "Id", ModId);
            Widgets.FieldRow(scroll, "State", State.ToString());
            Widgets.FieldRow(scroll, "Config", Config.ConfigFilePath);

            if (_watcher != null)
            {
                var when = _watcher.LastChangeUtc.HasValue
                    ? _watcher.LastChangeUtc.Value.ToLocalTime().ToString("HH:mm:ss")
                    : "never";
                Widgets.FieldRow(scroll, "Config reloaded",
                    _watcher.ReloadCount + " time(s), last at " + when);
            }

            var patches = PatchCoordinator.ForOwner(ModId);
            Widgets.Spacer(scroll, 4f);
            if (patches.Length == 0)
            {
                Widgets.Note(scroll, "This mod has not patched anything through the coordinator.");
            }
            else
            {
                Widgets.Heading(scroll, "Patches");
                foreach (var patch in patches)
                {
                    Widgets.FieldRow(scroll, patch.Method, patch.Kinds);
                }
            }

            var hotkeys = Hotkeys.ForOwner(ModId);
            if (hotkeys.Length > 0)
            {
                Widgets.Spacer(scroll, 4f);
                Widgets.Heading(scroll, "Hotkeys");
                foreach (var binding in hotkeys)
                {
                    Widgets.FieldRow(scroll, binding.Signature, binding.Description);
                }
            }

            // The config editor is the point of a menu, so build it even if no setting has been read
            // yet: a mod can have settings on disk that nothing has touched this session. Its sections
            // are collapsible, so a long config does not bury the status above it.
            var editor = ConfigEditor;
            if (editor.SettingCount > 0)
            {
                Widgets.Spacer(scroll, 8f);
                var fold = Widgets.Section(scroll, "Settings", true);
                var body = Widgets.SectionBody(fold);
                if (body != null) editor.Build(body);
                else editor.Build(scroll);
            }
            else
            {
                Widgets.Spacer(scroll, 8f);
                Widgets.Note(scroll, "This mod has no settings.");
            }
        }
    }
}