using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using BepInEx.Logging;
using ScamWYF.Modding.Core.Ui;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// Builds an in-game editor for a mod's config file, straight from what BepInEx already knows about
    /// it.
    /// </summary>
    /// <remarks>
    /// Every setting a mod binds is registered with a section, a key, a description and a default. That
    /// is everything needed to draw an editor, so no mod has to write one: the mod menu renders a mod's
    /// whole config as a form, with the mod's own comments as help text.
    ///
    /// The type of each entry picks the widget - a toggle for a bool, a dropdown for an enum, a slider
    /// for a number with a range, a masked field for anything that looks like a secret, a text field
    /// otherwise. Editing writes straight back to the entry and saves the file, so the text file on disk
    /// and what the mod is using cannot drift apart.
    ///
    /// Reading is defensive throughout. This file exists to be hand-edited, so a value may be missing,
    /// misspelled or out of range, and the editor's job is to show what is actually in effect rather than
    /// to refuse to open.
    /// </remarks>
    public sealed class ConfigEditor
    {
        private readonly ConfigFile _file;
        private readonly ManualLogSource _log;
        private readonly string _modId;

        private List<Row> _rows;

        /// <summary>One configurable setting, with everything needed to draw it.</summary>
        private sealed class Row
        {
            public string Section;
            public string Key;
            public string Description;
            public Type Type;
            public object Minimum;
            public object Maximum;
            public bool Multiline;
            public bool Secret;

            public ConfigDefinition Definition { get { return new ConfigDefinition(Section, Key); } }
            public string Label { get { return Section + " / " + Key; } }
        }

        public ConfigEditor(ConfigFile file, ManualLogSource log, string modId)
        {
            if (file == null) throw new ArgumentNullException("file");

            _file = file;
            _log = log;
            _modId = modId;
            Describe();
        }

        /// <summary>The file being edited, for the header line.</summary>
        public string FilePath
        {
            get { return _file.ConfigFilePath; }
        }

        /// <summary>Every setting in the file, in the order BepInEx reports them.</summary>
        public int SettingCount
        {
            get { return _rows == null ? 0 : _rows.Count; }
        }

        /// <summary>Rebuild the widget list. Called after a hot reload so new bindings are picked up.</summary>
        public void Rescan()
        {
            Describe();
        }

        /// <summary>Write the current values to the config file on disk.</summary>
        public void Save()
        {
            try
            {
                _file.Save();
            }
            catch (Exception ex)
            {
                Status = _file.ConfigFilePath + ": could not save - " + ex.Message;
            }
        }

        /// <summary>Re-read the file from disk, discarding unsaved edits.</summary>
        public void Reload()
        {
            try
            {
                _file.Reload();
                Status = null;
            }
            catch (Exception ex)
            {
                Status = _file.ConfigFilePath + ": could not reload - " + ex.Message;
            }
        }

        /// <summary>
        /// Draw the whole config into a container, one collapsible group per config section.
        /// </summary>
        /// <remarks>
        /// No scroll view of its own: the caller decides. The menu's page host is already scrollable, and
        /// a nested one takes the wheel first, which reads as a menu that has stopped responding.
        /// </remarks>
        /// <returns>The container it drew into, for chaining.</returns>
        public VisualElement Build(VisualElement parent)
        {
            if (parent == null) return null;

            if (_rows == null) Describe();

            if (_rows.Count == 0)
            {
                Widgets.Note(parent, "This mod has no settings bound yet.");
                return null;
            }

            // Each config section becomes a collapsible group, open to begin with. A config with six
            // sections and fifty settings is unreadable as one flat list, and collapsing the parts you are
            // not working on is the obvious thing to want.
            VisualElement body = parent;
            var lastSection = "";

            foreach (var row in _rows)
            {
                if (!string.Equals(row.Section, lastSection, StringComparison.Ordinal))
                {
                    if (lastSection.Length > 0) Widgets.Spacer(parent, 4f);

                    var fold = Widgets.Section(parent, row.Section, true);
                    body = Widgets.SectionBody(fold) ?? parent;
                    lastSection = row.Section;
                }

                BuildRow(body, row);
            }

            Widgets.Spacer(parent, 4f);

            if (!string.IsNullOrEmpty(Status)) Widgets.Error(parent, Status);

            BuildFooter(parent);
            return parent;
        }

        // ---------------------------------------------------------------- widgets

        private void BuildRow(VisualElement parent, Row row)
        {
            try
            {
                var entry = _file[row.Definition];
                if (entry == null)
                {
                    Widgets.Note(parent, row.Label + " - in the file but not bound, so nothing to edit.",
                        true, false);
                    return;
                }

                if (row.Type == typeof(bool)) BuildToggle(parent, row, entry);
                else if (row.Type != null && row.Type.IsEnum) BuildEnum(parent, row, entry);
                else if (row.Type == typeof(int) && row.Minimum != null && row.Maximum != null)
                {
                    BuildIntSlider(parent, row, entry);
                }
                else if (row.Type == typeof(int) || row.Type == typeof(float)) BuildNumber(parent, row, entry);
                else if (row.Secret) BuildSecret(parent, row, entry);
                else BuildText(parent, row, entry);
            }
            catch (Exception ex)
            {
                // One odd setting must not cost a mod its whole settings page.
                Widgets.Error(parent, row.Label + " - could not be edited: " + ex.Message);
            }
        }

        private void BuildToggle(VisualElement parent, Row row, ConfigEntryBase entry)
        {
            var toggle = Widgets.Toggle(parent, row.Key, ReadBool(entry), delegate { Write(entry, true); });
            toggle.tooltip = row.Description;

            // The callback only sees "it changed", not which way, so the field is read back rather than
            // captured from the closure. That also keeps the two in step if something else writes the
            // setting while the menu is open.
            toggle.RegisterValueChangedCallback(delegate (ChangeEvent<bool> evt)
            {
                Write(entry, evt.newValue);
            });
        }

        private void BuildEnum(VisualElement parent, Row row, ConfigEntryBase entry)
        {
            var names = Enum.GetNames(row.Type);
            var choices = new List<string>(names);

            Widgets.Dropdown(parent, row.Key, choices, ReadEnumIndex(entry, names), delegate (int index)
            {
                if (index < 0 || index >= names.Length) return;

                try
                {
                    Write(entry, Enum.Parse(row.Type, names[index]));
                }
                catch (Exception ex)
                {
                    Widgets.Error(parent, row.Key + " could not be set: " + ex.Message);
                }
            });

            AddHelp(parent, row);
        }

        private void BuildIntSlider(VisualElement parent, Row row, ConfigEntryBase entry)
        {
            var min = ToInt(row.Minimum);
            var max = ToInt(row.Maximum);
            if (max <= min) max = min + 1;

            var slider = Widgets.IntSlider(parent, row.Key, ReadInt(entry, min, max), min, max,
                delegate (int value) { Write(entry, value); });
            slider.tooltip = row.Description;

            // The slider's own callback already writes; this one only re-syncs when something else wrote,
            // such as the config being hot-reloaded while the menu is open.
            slider.RegisterValueChangedCallback(delegate (ChangeEvent<int> evt)
            {
                var current = ReadInt(entry, min, max);
                if (current == evt.newValue) return;
                slider.SetValueWithoutNotify(current);
            });
        }

        /// <summary>
        /// A number with no usable range, or a float: a text field validated on the way out rather than a
        /// slider, because a float range the mod did not declare would be a guess.
        /// </summary>
        private void BuildNumber(VisualElement parent, Row row, ConfigEntryBase entry)
        {
            Widgets.TextField(parent, row.Key, ReadText(entry), delegate (string value)
            {
                object parsed;
                if (!TryParseNumber(row.Type, value, out parsed))
                {
                    Widgets.Note(parent, row.Key + " needs a number; '" + value + "' is not one. " +
                                    "It has not been saved.", true, false);
                    return;
                }

                Write(entry, parsed);
            }, false).tooltip = row.Description;
        }

        private void BuildSecret(VisualElement parent, Row row, ConfigEntryBase entry)
        {
            var field = Widgets.SecretField(parent, row.Key, ReadText(entry),
                delegate (string value) { Write(entry, value); });

            field.tooltip = "Stored in plain text in the config file. Masked here so it is not on screen, " +
                            "not so it is encrypted.";
            Widgets.Note(parent, "Stored in plain text in the config file. Masked here so it is not on " +
                                "screen, not so it is encrypted.", false, false);
        }

        private void BuildText(VisualElement parent, Row row, ConfigEntryBase entry)
        {
            Widgets.TextField(parent, row.Key, ReadText(entry),
                delegate (string value) { Write(entry, value); }, row.Multiline).tooltip = row.Description;

            AddHelp(parent, row);
        }

        private static void AddHelp(VisualElement parent, Row row)
        {
            if (string.IsNullOrEmpty(row.Description)) return;
            Widgets.Note(parent, row.Description);
        }

        private void BuildFooter(VisualElement parent)
        {
            var row = Widgets.WrapRow(parent);
            row.style.marginLeft = 2f;

            Widgets.DescribedButton(row, "Save now", "Write the current values to the config file",
                delegate
                {
                    try
                    {
                        _file.Save();
                        if (_log != null) _log.LogInfo("Saved " + _file.ConfigFilePath + ".");
                    }
                    catch (Exception ex)
                    {
                        Widgets.Error(parent, "Could not save: " + ex.Message);
                    }
                });

            Widgets.DescribedButton(row, "Reload from disk", "Re-read the file, discarding unsaved edits",
                delegate
                {
                    try
                    {
                        _file.Reload();
                        Rescan();
                        if (_log != null) _log.LogInfo("Reloaded " + _file.ConfigFilePath + ".");
                    }
                    catch (Exception ex)
                    {
                        Widgets.Error(parent, "Could not reload: " + ex.Message);
                    }
                });

            Widgets.DescribedButton(row, "Open file", "Open the config file in a text editor",
                delegate
                {
                    try
                    {
                        Application.OpenURL("file://" + _file.ConfigFilePath.Replace("\\", "/"));
                    }
                    catch (Exception ex)
                    {
                        Widgets.Warning(parent, "Could not open the file: " + ex.Message);
                    }
                });

            Widgets.Spacer(row, 2f);
            Widgets.Label(row, _file.ConfigFilePath, true);
        }

        // ---------------------------------------------------------------- writing

        /// <summary>
        /// Put the entry's value into the file and save. Every widget funnels through here, so there is
        /// exactly one place a write can fail and exactly one place it gets reported.
        /// </summary>
        private void Write(ConfigEntryBase entry, object value)
        {
            try
            {
                entry.BoxedValue = value;
                _file.Save();
                Status = null;
            }
            catch (Exception ex)
            {
                if (_log != null)
                {
                    _log.LogWarning(_modId + ": could not write a setting: " + ex.Message);
                }

                // Showing the failure matters more than the write: a value that looks set but was
                // rejected is worse than one that visibly was not. The next Build puts this under the
                // form, so a mod does not have to wire up an error channel of its own.
                Status = _file.ConfigFilePath + ": could not save - " + ex.Message;
            }
        }

        /// <summary>
        /// Why the last edit failed, or null when the last one worked. Rendered under the form on the
        /// next Build, which happens every time the tab is selected or the menu is refreshed.
        /// </summary>
        public string Status { get; private set; }

        // ---------------------------------------------------------------- reading

        private bool ReadBool(ConfigEntryBase entry)
        {
            try
            {
                var value = entry.BoxedValue;
                if (value is bool) return (bool)value;
            }
            catch (Exception)
            {
            }

            var fallback = entry.DefaultValue;
            return fallback is bool ? (bool)fallback : false;
        }

        /// <summary>Which of the enum's names is in effect, as an index into the choices list.</summary>
        private int ReadEnumIndex(ConfigEntryBase entry, string[] names)
        {
            var index = IndexOfName(entry.BoxedValue, names);
            if (index >= 0) return index;
            return Math.Max(0, IndexOfName(entry.DefaultValue, names));
        }

        private static int IndexOfName(object value, string[] names)
        {
            if (value == null) return -1;

            var name = value.ToString();
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], name, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>The stored value if it is in range, otherwise the default if that is, otherwise the min.</summary>
        private int ReadInt(ConfigEntryBase entry, int min, int max)
        {
            try
            {
                var value = entry.BoxedValue;
                if (value is int)
                {
                    var number = (int)value;
                    if (number >= min && number <= max) return number;
                }
            }
            catch (Exception)
            {
            }

            var fallback = entry.DefaultValue;
            if (fallback is int)
            {
                var number = (int)fallback;
                if (number >= min && number <= max) return number;
            }
            return min;
        }

        private string ReadText(ConfigEntryBase entry)
        {
            try
            {
                var value = entry.BoxedValue;
                if (value != null) return value.ToString();
            }
            catch (Exception)
            {
            }

            var fallback = entry.DefaultValue;
            return fallback == null ? "" : fallback.ToString();
        }

        private static int ToInt(object value)
        {
            if (value == null) return 0;
            try { return System.Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        private static bool TryParseNumber(Type type, string text, out object value)
        {
            value = null;
            if (string.IsNullOrEmpty(text)) return false;

            if (type == typeof(int))
            {
                int parsed;
                if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return false;
                }
                value = parsed;
                return true;
            }

            float single;
            if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out single))
            {
                return false;
            }
            value = single;
            return true;
        }

        // ---------------------------------------------------------------- describing

        /// <summary>
        /// Read what BepInEx knows about every entry: type, default, acceptable values and the
        /// description the mod supplied. That is the whole input to the editor, which is why a mod does
        /// not have to declare anything extra for its settings to be editable in game.
        ///
        /// Anything unreadable is skipped rather than fatal.
        /// </summary>
        private void Describe()
        {
            var rows = new List<Row>();

            foreach (var definition in _file.Keys)
            {
                try
                {
                    var entry = _file[definition];
                    if (entry == null) continue;

                    var description = entry.Description;
                    var text = description == null ? null : description.Description;
                    if (text != null) text = text.Trim();

                    var key = definition.Key ?? "";
                    var type = entry.SettingType;

                    rows.Add(new Row
                    {
                        Section = definition.Section,
                        Key = definition.Key,
                        Description = text,
                        Type = type,
                        Minimum = RangeBound(description, true),
                        Maximum = RangeBound(description, false),

                        // A description with line breaks is documentation, and documentation this long
                        // describes prose - so the value is probably prose too.
                        Multiline = text != null && text.IndexOf('\n') >= 0,
                        Secret = LooksSecret(key, type)
                    });
                }
                catch (Exception ex)
                {
                    if (_log != null)
                    {
                        _log.LogWarning(_modId + ": could not read the setting " + definition +
                                        ": " + ex.Message);
                    }
                }
            }

            _rows = rows;
        }

        /// <summary>
        /// The min or max of an AcceptableValueRange, when the mod supplied one. That is what makes a
        /// number render as a slider rather than a free-text field.
        /// </summary>
        private static object RangeBound(ConfigDescription description, bool minimum)
        {
            var acceptable = description == null ? null : description.AcceptableValues;
            if (acceptable == null) return null;

            // An AcceptableValueList also arrives here; it has no bounds, so the range checks below
            // simply do not match and the caller falls back to a text field.
            var range = acceptable as AcceptableValueBase;
            if (range == null) return null;

            try
            {
                if (acceptable is AcceptableValueRange<int>)
                {
                    var typed = (AcceptableValueRange<int>)acceptable;
                    return minimum ? (object)typed.MinValue : typed.MaxValue;
                }

                if (acceptable is AcceptableValueRange<float>)
                {
                    var typed = (AcceptableValueRange<float>)acceptable;
                    return minimum ? (object)typed.MinValue : typed.MaxValue;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>
        /// Whether a value should be masked on screen. A guess from the key's name, and only for strings:
        /// it is better to mask something that is not secret than to put an API key on a projector.
        /// </summary>
        private static bool LooksSecret(string key, Type type)
        {
            if (type != typeof(string)) return false;

            var lower = (key ?? "").ToLowerInvariant();
            return lower.Contains("apikey") || lower.Contains("api_key") || lower.Contains("token") ||
                   lower.Contains("secret") || lower.Contains("password");
        }
    }
}