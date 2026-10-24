using System;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// Config binding that survives a hand-edited file.
    /// </summary>
    /// <remarks>
    /// Config files for this game are meant to be edited in a text editor, so "MinMaxTokens = lots" has
    /// to mean something other than a mod that throws during Awake. Every read here is range-checked and
    /// falls back to a known-good value, complaining in the log when it does.
    ///
    /// BepInEx already gives each plugin its own .cfg named after its GUID, so keys cannot collide
    /// across mods. What this adds is not letting a bad value take the mod down with it.
    ///
    /// Ranges declared here are also what make the in-game config editor useful: a setting bound with a
    /// known range is drawn as a slider, and the mod's own description becomes the help text. Nothing
    /// extra to declare for the editor to be worth having.
    /// </remarks>
    public sealed class ModSettings
    {
        private readonly ConfigFile _file;
        private readonly ManualLogSource _log;
        private readonly string _modId;

        /// <summary>
        /// ConfigEntry does not expose its own definition, so the section/key each entry was bound under
        /// is remembered here. Only used to name a setting in a warning.
        /// </summary>
        private readonly System.Collections.Generic.Dictionary<object, string> _labels =
            new System.Collections.Generic.Dictionary<object, string>();

        public ModSettings(ConfigFile file, ManualLogSource log, string modId)
        {
            if (file == null) throw new ArgumentNullException("file");
            _file = file;
            _log = log;
            _modId = modId;
        }

        public ConfigFile File
        {
            get { return _file; }
        }

        /// <summary>Where this mod's config file lives.</summary>
        public string FilePath
        {
            get { return _file.ConfigFilePath; }
        }

        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            var entry = _file.Bind(section, key, defaultValue, description);
            Remember(entry, section, key);
            return entry;
        }

        /// <summary>
        /// Bind a number with a range. The range is enforced on every read, and the config editor uses
        /// it to draw a slider instead of a free-text field.
        /// </summary>
        public ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, T min, T max,
            string description) where T : IComparable
        {
            var entry = _file.Bind(section, key, defaultValue,
                new ConfigDescription(description, new AcceptableValueRange<T>(min, max)));
            Remember(entry, section, key);
            return entry;
        }

        /// <summary>Bind a key, for use with the shared hotkey registry.</summary>
        public ConfigEntry<Key> BindKey(string section, string key, Key defaultValue, string description)
        {
            var entry = _file.Bind(section, key, defaultValue, description);
            Remember(entry, section, key);
            return entry;
        }

        /// <summary>
        /// Bind one of a fixed set of values. Used for enums, which the config editor renders as a
        /// dropdown by reading the type rather than needing the choices declared.
        /// </summary>
        /// <remarks>
        /// No AcceptableValueList here: BepInEx's requires IEquatable, which enums do not implement, and
        /// the editor gets the same dropdown either way from the type. The choices are still accepted so
        /// a caller can be explicit about which members are valid - a "None" member the mod does not
        /// handle is better left out.
        /// </remarks>
        public ConfigEntry<T> BindChoice<T>(string section, string key, T defaultValue, T[] choices,
            string description) where T : struct
        {
            var entry = _file.Bind(section, key, defaultValue, description);
            Remember(entry, section, key);
            return entry;
        }

        /// <summary>
        /// Bind one of a fixed set of non-enum values, with BepInEx enforcing the list on every read.
        /// Named differently from BindChoice because two overloads differing only by constraint cannot
        /// coexist.
        /// </summary>
        public ConfigEntry<T> BindFromList<T>(string section, string key, T defaultValue, T[] choices,
            string description) where T : IEquatable<T>
        {
            var entry = _file.Bind(section, key, defaultValue,
                new ConfigDescription(description, new AcceptableValueList<T>(choices)));
            Remember(entry, section, key);
            return entry;
        }

        private void Remember(object entry, string section, string key)
        {
            _labels[entry] = section + " / " + key;
        }

        private string Label(object entry)
        {
            string label;
            return _labels.TryGetValue(entry, out label) ? label : "a setting";
        }

        /// <summary>An int clamped into range. Out-of-range means someone edited the file.</summary>
        public int Int(ConfigEntry<int> entry, int min, int max)
        {
            int value;
            if (!TryRead(entry, out value, min))
            {
                WarnUnreadable(entry, min);
                return min;
            }

            if (value >= min && value <= max) return value;

            var clamped = Clamp(value, min, max);
            WarnOutOfRange(entry, value, min, max, clamped);
            return clamped;
        }

        /// <summary>A float clamped into range.</summary>
        public float Float(ConfigEntry<float> entry, float min, float max)
        {
            float value;
            if (!TryRead(entry, out value, min))
            {
                WarnUnreadable(entry, min);
                return min;
            }

            if (value >= min && value <= max) return value;

            var clamped = Clamp(value, min, max);
            WarnOutOfRange(entry, value, min, max, clamped);
            return clamped;
        }

        /// <summary>An enum, falling back when the file names a value that does not exist.</summary>
        public T EnumValue<T>(ConfigEntry<T> entry, T fallback) where T : struct
        {
            try
            {
                return entry.Value;
            }
            catch (Exception ex)
            {
                if (_log != null)
                {
                    _log.LogWarning(_modId + ": " + Label(entry) + " is not a valid " + typeof(T).Name +
                                    " (" + ex.Message + "); using " + fallback + ".");
                }
                return fallback;
            }
        }

        /// <summary>A string that cannot be null, and is trimmed.</summary>
        public string Text(ConfigEntry<string> entry, string fallbackWhenBlank)
        {
            try
            {
                var value = entry.Value;
                if (string.IsNullOrEmpty(value) || value.Trim().Length == 0) return fallbackWhenBlank;
                return value.Trim();
            }
            catch (Exception)
            {
                return fallbackWhenBlank;
            }
        }

        /// <summary>A string left exactly as typed, for things like header blobs.</summary>
        public string Raw(ConfigEntry<string> entry)
        {
            try
            {
                return entry.Value ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        public bool Bool(ConfigEntry<bool> entry, bool fallback)
        {
            try
            {
                return entry.Value;
            }
            catch (Exception)
            {
                WarnUnreadable(entry, fallback);
                return fallback;
            }
        }

        private bool TryRead<T>(ConfigEntry<T> entry, out T value, T fallback)
        {
            try
            {
                value = entry.Value;
                return true;
            }
            catch (Exception)
            {
                value = fallback;
                return false;
            }
        }

        private void WarnUnreadable(object entry, object fallback)
        {
            if (_log == null) return;
            _log.LogWarning(_modId + ": " + Label(entry) + " could not be read from " + FilePath +
                            "; using " + fallback + ".");
        }

        private void WarnOutOfRange<T>(object entry, T value, T min, T max, T clamped)
        {
            if (_log == null) return;
            _log.LogWarning(_modId + ": " + Label(entry) + " is " + value + ", outside " + min + ".." + max +
                            "; using " + clamped + ".");
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }
    }
}