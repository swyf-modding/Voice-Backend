using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// Remembers where each window was and how big, so reopening it puts it back where the person left
    /// it.
    /// </summary>
    /// <remarks>
    /// A menu that snaps back to the same spot every time gets in the way after the first use: you move
    /// it off the HUD once and expect it to stay moved. These settings live in the library's own config
    /// file rather than any mod's, because they describe the shared windows and no single mod owns them.
    ///
    /// Written when the geometry changes rather than every frame: one write when the pointer is let go
    /// instead of hundreds while it moves.
    ///
    /// Everything here is best-effort. If the config cannot be read or written the windows still work;
    /// they just do not remember where they were.
    /// </remarks>
    internal static class WindowPlacement
    {
        private const string Section = "Windows";

        /// <summary>One window's four remembered numbers.</summary>
        private sealed class Entry
        {
            public ConfigEntry<float> X;
            public ConfigEntry<float> Y;
            public ConfigEntry<float> Width;
            public ConfigEntry<float> Height;

            // ConfigEntry has no ResetToDefault, so the defaults are kept here for Forget.
            public float DefaultX;
            public float DefaultY;
            public float DefaultWidth;
            public float DefaultHeight;
        }

        private static readonly Dictionary<string, Entry> Cache = new Dictionary<string, Entry>();
        private static ConfigFile _file;
        private static ManualLogSource _log;
        private static bool _probed;

        /// <summary>
        /// Open at a remembered size and position if there is one, otherwise as asked.
        /// </summary>
        /// <remarks>
        /// The values passed in are the window's own defaults, and are also used as the config defaults.
        /// That matters: binding with fixed defaults instead would make every window's first run open at
        /// whatever those fixed numbers were, ignoring the geometry it asked for.
        /// </remarks>
        public static void Load(string key, ref float x, ref float y, ref float width, ref float height)
        {
            if (string.IsNullOrEmpty(key)) return;

            try
            {
                var entry = Get(key, x, y, width, height);
                if (entry == null) return;

                // A stored value of zero for a size means the entry predates this, or was hand-edited;
                // the minimums in WindowGeometry will pull it back to something usable either way.
                width = entry.Width.Value;
                height = entry.Height.Value;
                x = entry.X.Value;
                y = entry.Y.Value;
            }
            catch (Exception)
            {
                // A hand-edited value that will not parse is not worth failing over: the defaults stand.
            }
        }

        /// <summary>Remember a window's geometry. Cheap enough to call whenever the geometry changes.</summary>
        public static void Save(string key, float x, float y, float width, float height)
        {
            if (string.IsNullOrEmpty(key)) return;

            try
            {
                // The cache only, deliberately. Binding here would bake the current geometry in as the
                // default and make Forget a no-op, and a window that was never bound has nothing to save.
                Entry entry;
                if (!Cache.TryGetValue(key, out entry)) return;

                entry.X.Value = x;
                entry.Y.Value = y;
                entry.Width.Value = width;
                entry.Height.Value = height;
                _file.Save();
            }
            catch (Exception ex)
            {
                // Not remembering where a window was is an annoyance, not a failure worth logging loudly
                // on every resize.
                if (_log != null) _log.LogDebug("Could not save the layout of '" + key + "': " + ex.Message);
            }
        }

        /// <summary>
        /// Forget a window's geometry, so it reopens at its default size. The defaults are captured when
        /// the entry is bound rather than assumed, so a default that is itself configured is respected.
        /// </summary>
        public static void Forget(string key)
        {
            if (string.IsNullOrEmpty(key)) return;

            try
            {
                Entry entry;
                if (!Cache.TryGetValue(key, out entry)) return;

                entry.X.Value = entry.DefaultX;
                entry.Y.Value = entry.DefaultY;
                entry.Width.Value = entry.DefaultWidth;
                entry.Height.Value = entry.DefaultHeight;
                _file.Save();
            }
            catch (Exception ex)
            {
                if (_log != null) _log.LogDebug("Could not reset the layout of '" + key + "': " + ex.Message);
            }
        }

        /// <summary>
        /// The window's numbers, bound to the config on first use.
        /// </summary>
        /// <param name="key">Which window. Used in the config key, so it has to be config-safe.</param>
        /// <param name="defaultX">Where to open if nothing is remembered.</param>
        /// <param name="defaultY">Where to open if nothing is remembered.</param>
        /// <param name="defaultWidth">How wide to open if nothing is remembered.</param>
        /// <param name="defaultHeight">How tall to open if nothing is remembered.</param>
        private static Entry Get(string key, float defaultX, float defaultY, float defaultWidth, float defaultHeight)
        {
            EnsureConfig();
            if (_file == null) return null;

            Entry existing;
            if (Cache.TryGetValue(key, out existing)) return existing;

            var entry = new Entry
            {
                DefaultX = defaultX,
                DefaultY = defaultY,
                DefaultWidth = defaultWidth,
                DefaultHeight = defaultHeight,
                X = _file.Bind(Section, key + ".X", defaultX,
                    "Left edge of the '" + key + "' window, in pixels from the left of the screen."),
                Y = _file.Bind(Section, key + ".Y", defaultY,
                    "Top edge of the '" + key + "' window, in pixels from the top of the screen."),
                Width = _file.Bind(Section, key + ".Width", defaultWidth,
                    "Width of the '" + key + "' window. Drag its bottom-right corner to resize."),
                Height = _file.Bind(Section, key + ".Height", defaultHeight,
                    "Height of the '" + key + "' window. Drag its bottom-right corner to resize.")
            };

            Cache[key] = entry;
            return entry;
        }

        /// <summary>
        /// Turn a window title into something usable as a config key.
        /// </summary>
        /// <remarks>
        /// BepInEx keys land in a TOML table, so a title with a dot or a quote in it would produce a file
        /// that will not parse back. Titles are chosen by mods and nobody would notice a bad key until
        /// their config stopped loading, so it is handled here rather than trusted.
        /// </remarks>
        internal static string SafeKey(string title)
        {
            if (string.IsNullOrEmpty(title)) return "window";

            var chars = title.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                var safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
                if (!safe) chars[i] = '_';
            }

            var key = new string(chars);
            return key.Length == 0 ? "window" : key;
        }

        private static void EnsureConfig()
        {
            if (_probed) return;
            _probed = true;

            try
            {
                _file = LibraryRuntime.Config;
                _log = LibraryRuntime.Log;
            }
            catch (Exception)
            {
                // No config means no persistence, which is a perfectly workable state to be in.
                _file = null;
                _log = null;
            }
        }
    }
}