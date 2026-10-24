using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The built-in Plugins tab: a list of every BepInEx plugin file with enable/disable switches.
    /// This lives in the library (not a separate mod) so it is always available. The ModHandler mod
    /// can still be installed for its additional features; registering by the same key replaces this.
    /// </summary>
    internal static class BuiltInPluginsTab
    {
        private static readonly List<ModEntry> Mods = new List<ModEntry>();
        private static string Status = "";
        private static bool RestartNeeded;

        private static string PluginsDir
        {
            get { return BepInEx.Paths.PluginPath; }
        }

        private static string DisabledDir
        {
            get
            {
                return Path.Combine(
                    Path.GetDirectoryName(PluginsDir) ?? BepInEx.Paths.BepInExRootPath, "plugins_disabled");
            }
        }

        /// <summary>Draw the tab. Refreshed each time it is shown so newly installed files appear.</summary>
        internal static void Build(VisualElement host)
        {
            Refresh();
            BuildList(host);
        }

        /// <summary>Draw the list into the container, rebuilding it each time the tab is shown.</summary>
        private static void BuildList(VisualElement host)
        {
            var scroll = host;

            // Action bar: full-width row with buttons that stretch to fill.
            var actionBar = Widgets.Row(scroll);
            actionBar.AddToClassList("general-action-grid");
            actionBar.style.flexGrow = 1f;

            var btnRefresh = Widgets.Button(actionBar, "Refresh", Refresh);
            btnRefresh.AddToClassList("general-action-button");

            var btnOpenPlugins = Widgets.Button(actionBar, "Open plugins folder",
                delegate { Open(PluginsDir); });
            btnOpenPlugins.AddToClassList("general-action-button");

            var btnOpenDisabled = Widgets.Button(actionBar, "Open disabled folder",
                delegate { Open(DisabledDir); });
            btnOpenDisabled.AddToClassList("general-action-button");

            Widgets.Spacer(scroll, 4f);

            if (Mods.Count == 0)
            {
                Widgets.Note(scroll, "No plugin files found in BepInEx\\plugins or plugins_disabled.", false, false);
            }
            else
            {
                foreach (var mod in Mods) BuildRow(scroll, mod);
            }

            Widgets.Spacer(scroll, 6f);

            Widgets.Note(scroll,
                "Enabling or disabling moves the dll to the other folder. BepInEx loads every plugin " +
                "before any of them run, so a change cannot apply to the session already in progress - " +
                "it takes effect on the next launch.", RestartNeeded, false);

            if (!string.IsNullOrEmpty(Status)) Widgets.Label(scroll, Status, true);
        }

        private static void BuildRow(VisualElement parent, ModEntry mod)
        {
            var box = Widgets.Column(parent);
            box.AddToClassList("settings-row");
            box.AddToClassList("setting-row--compact");
            box.style.marginBottom = 3f;
            box.style.paddingLeft = 6f;
            box.style.paddingRight = 6f;
            box.style.paddingTop = 4f;
            box.style.paddingBottom = 4f;

            var header = Widgets.Row(box);

            // A file that is already loaded cannot be switched off without unloading it, so it is
            // labelled instead; a file that is only on disk gets the ON/OFF switch.
            if (mod.Live != null)
            {
                var tag = Widgets.Label(header, mod.Live.State == ModState.Active ? "running" : "loaded", true);
                tag.style.marginLeft = 6f;
            }
            else
            {
                Widgets.ToggleButton(header, "", mod.Enabled, delegate { Toggle(mod); });
            }

            var displayName = string.IsNullOrEmpty(mod.Version)
                ? mod.Name
                : mod.Name + "  " + mod.Version;

            var name = Widgets.Label(header, displayName, false);
            name.style.unityFontStyleAndWeight = FontStyle.Bold;

            Widgets.Filler(header);

            var detail = mod.Detail;
            if (!string.IsNullOrEmpty(detail))
            {
                var line = Widgets.Label(box, detail, true);
                line.style.marginLeft = 20f;
            }
        }

        private static void Toggle(ModEntry mod)
        {
            mod.Enabled = !mod.Enabled;
            try
            {
                var targetDir = mod.Enabled ? PluginsDir : DisabledDir;
                Directory.CreateDirectory(targetDir);
                var targetPath = Path.Combine(targetDir, mod.FileName);
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(mod.FilePath, targetPath);
                mod.FilePath = targetPath;
                RestartNeeded = true;
                ModMenu.Refresh();
            }
            catch (Exception ex)
            {
                Status = "Could not move " + mod.FileName + ": " + ex.Message;
                ModMenu.Refresh();
            }
        }

        private static void Refresh()
        {
            Mods.Clear();
            Status = "";
            RestartNeeded = false;

            // Live mods are keyed by the file their assembly came from, so a plugin file and the
            // mod it contains can be matched even when the file name and the mod id differ.
            var live = new Dictionary<string, ScamMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in ModRegistry.Mods)
            {
                if (mod == null) continue;

                try
                {
                    var location = mod.GetType().Assembly.Location;
                    if (!string.IsNullOrEmpty(location))
                    {
                        live[Path.GetFileName(location)] = mod;
                    }
                }
                catch (Exception)
                {
                    // A mod whose assembly location cannot be read is still listed by file name.
                }
            }

            try
            {
                Directory.CreateDirectory(PluginsDir);
                Directory.CreateDirectory(DisabledDir);
                Collect(PluginsDir, true, live);
                Collect(DisabledDir, false, live);
                Mods.Sort(delegate (ModEntry a, ModEntry b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception ex)
            {
                Status = "Could not read the plugin folders: " + ex.Message;
            }
        }

        private static void Collect(string directory, bool enabled, Dictionary<string, ScamMod> live)
        {
            foreach (var path in Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(path);

                var entry = new ModEntry
                {
                    FileName = fileName,
                    FilePath = path,
                    Name = Path.GetFileNameWithoutExtension(path),
                    Enabled = enabled
                };

                if (live.TryGetValue(fileName, out var liveMod))
                {
                    entry.Live = liveMod;
                    entry.Version = liveMod.ModVersion;
                }

                Mods.Add(entry);
            }
        }

        private static void Open(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                Application.OpenURL("file://" + path.Replace("\\", "/"));
            }
            catch (Exception ex)
            {
                UiLog.Warn("Could not open " + path + ": " + ex.Message);
            }
        }

        private sealed class ModEntry
        {
            public string FileName;
            public string FilePath;
            public string Name;
            public string Version;
            public ScamMod Live;
            public bool Enabled;

            public string Detail
            {
                get
                {
                    if (Live != null && Live.State == ModState.Failed) return "failed: " + Live.FailureReason;
                    if (Live != null) return Live.ModId;
                    return FileName;
                }
            }
        }
    }
}