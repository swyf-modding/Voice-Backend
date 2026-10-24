using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using BepInEx.Configuration;
using ScamWYF.Modding.Core.Ui;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// The library's own startup and its single per-frame loop.
    /// </summary>
    /// <remarks>
    /// Two things need a heartbeat: config files being watched for edits, and the UI. Rather than each
    /// service finding or creating its own GameObject - which is how two mods end up with two update
    /// loops and an unexplained frame cost - there is exactly one runner here for everything.
    ///
    /// The library also has a config file of its own, for the menu hotkey. It cannot live in any mod's
    /// file, because that mod might not be installed: the menu has to work with any combination of
    /// mods, including only one, and including a mod that knows nothing about menus.
    ///
    /// Everything here is idempotent and runs before the first mod's OnModLoad returns, so a mod can
    /// assume the menu, its hotkey and the diagnostics pages already exist.
    /// </remarks>
    internal static class LibraryRuntime
    {
        /// <summary>The library's own id, used where something must be owned but no mod owns it.</summary>
        internal const string LibraryId = "scamwyf.modding.core";

        private static bool _singleCopyWarned;

        /// <summary>
        /// Fail loudly if the library was loaded more than once.
        /// </summary>
        /// <remarks>
        /// The library must be one referenced assembly shared by every mod. If it is compiled into each
        /// mod's dll instead, every static in it is duplicated: two Hotkey registries, two menus, two
        /// overlays, and one keypress that opens two windows. Nothing can fix that at runtime, so the
        /// only useful thing to do is say clearly what is wrong and why.
        ///
        /// Detected by counting loaded assemblies that define a type with this library's name. A single
        /// copy is the correct arrangement.
        /// </remarks>
        internal static void CheckSingleCopy()
        {
            if (_singleCopyWarned) return;

            var mine = typeof(LibraryRuntime).Assembly.GetName().Name;
            var copies = 0;
            string first = null;

            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != mine) continue;
                    copies++;
                    if (first == null) first = assembly.Location;
                }
            }
            catch (Exception)
            {
                // If the assemblies cannot be enumerated there is nothing useful to conclude.
                return;
            }

            if (copies <= 1) return;

            _singleCopyWarned = true;
            Log.LogError(
                "ScamWYF.Modding.Core is loaded " + copies + " times (" + first + " and others). " +
                "Each copy has its own hotkey registry, its own menu and its own overlay, so one hotkey " +
                "will open several windows and mods cannot see each other. This happens when the library " +
                "is compiled into each mod instead of referenced as one dll. Build the library once with " +
                "'build.ps1 -LibraryOnly' and install it in BepInEx\\core; the build script now does " +
                "that automatically. Remove any copy of ScamWYF.Modding.Core.dll sitting beside a mod " +
                "dll in BepInEx\\plugins.");
        }

        private sealed class Runner : MonoBehaviour
        {
            private void Update()
            {
                LibraryRuntime.Tick();
            }
        }

        private static readonly List<ConfigWatcher> Watchers = new List<ConfigWatcher>();

        private static Runner _runner;
        private static ConfigFile _config;
        private static bool _started;

        /// <summary>
        /// The library's own settings. Separate from any mod's file on purpose, so the menu hotkey is
        /// the same whichever mods are installed.
        /// </summary>
        public static ConfigFile Config
        {
            get
            {
                if (_config == null)
                {
                    _config = new ConfigFile(Path.Combine(
                        BepInEx.Paths.ConfigPath, "scamwyf-modding.cfg"), false);
                }
                return _config;
            }
        }

        /// <summary>The log everything in the library that is not a mod logs through.</summary>
        internal static BepInEx.Logging.ManualLogSource Log
        {
            get { return LibraryLog.Value; }
        }

        private static readonly Lazy<BepInEx.Logging.ManualLogSource> LibraryLog =
            new Lazy<BepInEx.Logging.ManualLogSource>(delegate
            {
                return BepInEx.Logging.Logger.CreateLogSource("ScamWYF.Modding");
            });

        /// <summary>
        /// Bring the library up. Called by ScamMod before the first mod's load completes, so nothing
        /// a mod does in OnModLoad can race it.
        /// </summary>
        internal static void Start()
        {
            if (_started) return;
            _started = true;

            CheckSingleCopy();

            try
            {
                var settings = new ModSettings(Config, Log, LibraryId);
                _menuKey = settings.BindKey("Menu", "Key", Key.F1,
                    "Opens the mod menu, where every mod sharing this library has a tab.\n" +
                    "Set to None to bind nothing and use a mod's own hotkey instead.");

                var bound = settings.EnumValue(_menuKey, Key.F1);
                if (bound != Key.None)
                {
                    _menuBinding = Hotkeys.Register(LibraryId, Log, bound, "Open the mod menu",
                        ModMenu.Toggle);
                }
                else
                {
                    Log.LogInfo("Menu hotkey is set to None, so nothing opens the menu by default.");
                }

                // The menu's own hotkey is the one hotkey a person will want to change, so it gets the
                // in-game editor as well. Cheap: nothing is drawn until somebody opens the About tab.
                MenuPage = new ConfigEditor(Config, Log, LibraryId);

                // The menu itself is the library's, not a mod's: it has to exist with any combination
                // of mods installed, and its tab strip is filled in by whichever mods load.
                ModMenu.Start();

                WatchSelf();

                // Proves-then-skips workarounds for game-build defects. Each one is a no-op on a
                // build without the defect, and says what it suppressed when there is one.
                try
                {
                    GameWorkarounds.EnsureHudHintBindingGuard();
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Game workarounds did not install: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                // A library that cannot start still leaves every mod loadable. Say why and carry on.
                Log.LogError("The shared library could not start: " + ex);
            }

            EnsureRunner();
        }

        /// <summary>The editor for the library's own settings, drawn on the About tab.</summary>
        internal static ConfigEditor MenuPage { get; private set; }

        /// <summary>
        /// Watch the library's own config too, so editing the menu key in a text editor takes effect
        /// without a restart - the same promise every mod's config makes.
        /// </summary>
        private static void WatchSelf()
        {
            var watcher = new ConfigWatcher(Config, Log, LibraryId);
            watcher.Reloaded += OnSelfReloaded;
            Register(watcher);
        }

        private static void OnSelfReloaded()
        {
            // The same ConfigEntry instance is reused rather than re-bound, because the file has already
            // been re-read into it; there is only the key value to look at.
            if (_menuKey == null || _menuBinding == null) return;

            try
            {
                var rebound = new ModSettings(Config, Log, LibraryId).EnumValue(_menuKey, Key.F1);
                if (rebound == _menuBinding.Key) return;

                // Rebinding rather than re-registering keeps the binding's place in the list, so the
                // Mods tab still reads correctly.
                Hotkeys.Rebind(_menuBinding, rebound);
                Log.LogInfo("Menu hotkey is now " + _menuBinding.Signature + ".");
                ModMenu.Refresh();
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not re-read the menu hotkey: " + ex.Message);
            }
        }

        private static ConfigEntry<Key> _menuKey;
        private static Hotkeys.Binding _menuBinding;

        /// <summary>Add a config file to the set polled for edits.</summary>
        internal static void Register(ConfigWatcher watcher)
        {
            if (watcher == null) return;
            lock (Watchers) { Watchers.Add(watcher); }
            EnsureRunner();
        }

        /// <summary>Drop a config watcher belonging to a mod that is unloading.</summary>
        internal static void Forget(ConfigWatcher watcher)
        {
            if (watcher == null) return;
            lock (Watchers) { Watchers.Remove(watcher); }
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return;

            var go = new GameObject("ScamWYF.Modding.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _runner = go.AddComponent<Runner>();
        }

        private static void Tick()
        {
            // One keyboard poll and one UI pass for the whole library. Both tolerate the UI not existing
            // yet: UiPanel retries, so a menu opened before the first scene loaded still turns up.
            Hotkeys.Pump();
            UiPanel.Tick();

            ConfigWatcher[] snapshot;
            lock (Watchers) { snapshot = Watchers.ToArray(); }

            foreach (var watcher in snapshot)
            {
                try
                {
                    watcher.Tick();
                }
                catch (Exception ex)
                {
                    UiLog.Error("A config watcher threw: " + ex);
                }
            }
        }
    }
}