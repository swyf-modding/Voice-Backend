using System;
using System.Collections.Generic;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The built-in Config tab: every mod's settings, editable in game. Each mod that binds config
    /// entries through the library already has a <see cref="ConfigEditor"/>; this tab lists them and
    /// lets the player change a setting and save, without opening a file.
    /// </summary>
    internal static class BuiltInConfigTab
    {
        private static readonly List<ModConfigEntry> Mods = new List<ModConfigEntry>();
        private static string Status = "";
        private static VisualElement _scroll;
        private static ModConfigEntry _scrollTo;

        /// <summary>Draw the tab. Refreshed each time it is shown so a hot reload is picked up.</summary>
        internal static void Build(VisualElement host)
        {
            if (Mods.Count == 0) Refresh();

            _scroll = host;

            var headerRow = Widgets.Row(host);
            var title = Widgets.Label(headerRow, "Mod Config Editor", false);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            Widgets.Filler(headerRow);
            Widgets.DescribedButton(headerRow, "Refresh", "Re-scan all mods for config settings", Refresh);
            Widgets.DescribedButton(headerRow, "Save all", "Write every mod's config to disk", SaveAll);

            Widgets.Spacer(host, 4f);

            if (Mods.Count == 0)
            {
                Widgets.Note(host, "No mods with config settings found.", false, false);
            }
            else
            {
                foreach (var mod in Mods) BuildMod(host, mod);
            }

            if (!string.IsNullOrEmpty(Status)) Widgets.Error(host, Status);

            if (_scrollTo != null)
            {
                var entry = _scrollTo;
                _scrollTo = null;
                ScrollToMod(entry);
            }
        }

        /// <summary>Open the Config tab and scroll to the given mod's section.</summary>
        internal static void OpenMod(ScamMod mod)
        {
            var entry = Mods.Find(e => e.Mod == mod);
            if (entry == null) return;

            ModMenu.ShowBuiltIn("config");
            _scrollTo = entry;
        }

        private static void BuildMod(VisualElement parent, ModConfigEntry mod)
        {
            var fold = Widgets.Section(parent, mod.Mod.DisplayName + "  (" + mod.Mod.ModId + ")", true);
            var body = Widgets.SectionBody(fold) ?? parent;

            mod.Editor.Build(body);

            if (!string.IsNullOrEmpty(mod.Editor.Status))
            {
                Widgets.Error(body, mod.Editor.Status);
            }
        }

        /// <summary>Refresh the list of mods with config settings.</summary>
        internal static void Refresh()
        {
            Mods.Clear();
            Status = "";

            try
            {
                foreach (var mod in ModRegistry.Mods)
                {
                    var editor = mod.ConfigEditor;
                    if (editor != null && editor.SettingCount > 0)
                    {
                        Mods.Add(new ModConfigEntry { Mod = mod, Editor = editor });
                    }
                }
                Mods.Sort(delegate (ModConfigEntry a, ModConfigEntry b)
                {
                    return string.Compare(a.Mod.DisplayName, b.Mod.DisplayName, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception ex)
            {
                Status = "Could not scan configs: " + ex.Message;
                UiLog.Error("Could not scan mods for config settings: " + ex);
            }
        }

        private static void SaveAll()
        {
            try
            {
                foreach (var mod in Mods)
                {
                    mod.Editor.Save();
                }
                Status = "All configs saved.";
            }
            catch (Exception ex)
            {
                Status = "Could not save all: " + ex.Message;
            }
            ModMenu.Refresh();
        }

        private static void ScrollToMod(ModConfigEntry entry)
        {
            if (_scroll == null) return;

            var foldout = FindFoldoutForMod(_scroll, entry.Mod);
            if (foldout == null) return;

            _scroll.schedule.Execute((Action)delegate
            {
                var scroller = _scroll.GetFirstAncestorOfType<ScrollView>();
                if (scroller != null) scroller.ScrollTo(foldout);
            }).ExecuteLater(50L);
        }

        private static Foldout FindFoldoutForMod(VisualElement root, ScamMod mod)
        {
            foreach (var child in root.Children())
            {
                var fold = child as Foldout;
                if (fold != null && fold.text != null && fold.text.Contains(mod.DisplayName))
                {
                    return fold;
                }

                var found = FindFoldoutForMod(child, mod);
                if (found != null) return found;
            }
            return null;
        }

        private sealed class ModConfigEntry
        {
            public ScamMod Mod;
            public ConfigEditor Editor;
        }
    }
}