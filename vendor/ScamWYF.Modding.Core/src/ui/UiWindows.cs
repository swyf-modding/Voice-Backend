using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// Owns the shared windows: one per mod, created on demand, removed when the mod unloads.
    /// </summary>
    /// <remarks>
    /// Mods do not make their own GameObjects or their own OnGUI. They ask for a window by title and
    /// get one, and when they unload the window goes with them. That is the same contract the old
    /// IMGUI host had, so the difference is what it is drawn with rather than who owns it.
    /// </remarks>
    public static class UiWindows
    {
        private sealed class Entry
        {
            public string OwnerId;
            public UiWindow Window;
        }

        private static readonly List<Entry> Windows = new List<Entry>();
        private static float _cascade = 0f;

        /// <summary>Every live window, in creation order.</summary>
        public static UiWindow[] All
        {
            get
            {
                lock (Windows)
                {
                    var result = new UiWindow[Windows.Count];
                    for (int i = 0; i < Windows.Count; i++) result[i] = Windows[i].Window;
                    return result;
                }
            }
        }

        /// <summary>Number of windows currently registered.</summary>
        public static int Count
        {
            get { lock (Windows) { return Windows.Count; } }
        }

        /// <summary>Whether any window is showing, so the caller knows if a hotkey did anything.</summary>
        public static bool AnyVisible
        {
            get
            {
                foreach (var window in All)
                {
                    if (window != null && window.Visible) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// A window for this mod. Reusing an existing one with the same title, so a hotkey firing
        /// twice does not stack two copies on top of each other.
        /// </summary>
        public static UiWindow GetOrCreate(ScamMod owner, string title, float width, float height)
        {
            if (owner == null) throw new ArgumentNullException("owner");

            UiWindow found;
            if (TryGet(owner.ModId, title, out found)) return found;

            var overlay = Widgets.Overlay;
            if (overlay == null) return null;

            // Cascade so a second window does not land exactly on the first one.
            var x = 80f + _cascade;
            var y = 80f + _cascade;
            _cascade += 28f;
            if (_cascade > 140f) _cascade = 0f;

            var window = UiWindow.Create(owner.ModId, title, x, y, width, height);
            if (window.Element == null)
            {
                owner.ModLog.LogWarning("Could not open the '" + title + "' window: " +
                                        (window.UnavailableReason ?? "unknown reason"));
                return null;
            }

            lock (Windows)
            {
                Windows.Add(new Entry { OwnerId = owner.ModId, Window = window });
            }

            owner.ModLog.LogInfo("Opened the '" + title + "' window (" + UiTheme.Source + ").");
            return window;
        }

        /// <summary>The window a mod already has under this title, if any.</summary>
        public static bool TryGet(string ownerId, string title, out UiWindow window)
        {
            window = null;
            lock (Windows)
            {
                foreach (var entry in Windows)
                {
                    if (entry.OwnerId != ownerId) continue;
                    if (!string.Equals(entry.Window.Title, title, StringComparison.Ordinal)) continue;
                    window = entry.Window;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Hide every window a mod owns, then the overlay if nothing else is showing. For a mod that
        /// wants one key to put all its overlays away.
        /// </summary>
        public static void HideOwner(string ownerId)
        {
            foreach (var window in All)
            {
                if (window == null) continue;
                if (!string.IsNullOrEmpty(ownerId) && window.OwnerId != ownerId) continue;
                window.Visible = false;
            }

            if (!AnyVisible) UiPanel.HideOverlay();
        }

        /// <summary>Drop every window a mod owns. Called for you when a mod unloads.</summary>
        public static void RemoveOwner(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;

            List<Entry> doomed;
            lock (Windows)
            {
                doomed = new List<Entry>();
                foreach (var entry in Windows)
                {
                    if (entry.OwnerId == modId) doomed.Add(entry);
                }
                Windows.RemoveAll(entry => entry.OwnerId == modId);
            }

            foreach (var entry in doomed)
            {
                if (entry.Window != null) entry.Window.Destroy();
            }
        }

        /// <summary>A mod's own window titles, for the diagnostics page.</summary>
        public static string[] ForOwner(string ownerId)
        {
            var titles = new List<string>();
            if (string.IsNullOrEmpty(ownerId)) return titles.ToArray();

            lock (Windows)
            {
                foreach (var entry in Windows)
                {
                    if (entry.OwnerId != ownerId) continue;
                    if (entry.Window != null) titles.Add(entry.Window.Title);
                }
            }
            return titles.ToArray();
        }
    }
}