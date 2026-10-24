using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine.InputSystem;
using ScamWYF.Modding.Core.Ui;

namespace ScamWYF.Modding.Core
{
    /// <summary>
    /// One keyboard poller for every mod.
    /// </summary>
    /// <remarks>
    /// Each mod polling Keyboard.current itself works until two mods want the same key, and then
    /// neither one gets it cleanly and there is nothing in the log saying why. Bindings are
    /// registered here instead, duplicates are reported, and the poll happens in one place.
    ///
    /// Both fire if two mods bind the same key: silently ignoring the second would just move the
    /// confusion somewhere less obvious. The conflict is reported in the log and on the menu's Mods
    /// tab.
    ///
    /// The library binds its own hotkey against its own id rather than any mod's, so the menu has a key
    /// whether or not any particular mod is installed.
    /// </remarks>
    public static class Hotkeys
    {
        /// <summary>One key, optionally with modifiers, owned by one thing.</summary>
        public sealed class Binding
        {
            public string OwnerId;
            public string OwnerName;
            public string Description;
            public Key Key;
            public readonly List<Key> Modifiers = new List<Key>();
            public Action OnPressed;

            public string Signature
            {
                get { return Humanize() + (Modifiers.Count == 0 ? "" : " + " + Join(Modifiers)); }
            }

            /// <summary>What a person would call this binding, for logs and the mod list.</summary>
            public string Humanize()
            {
                return Key.ToString();
            }

            private string Join(List<Key> keys)
            {
                var parts = new string[keys.Count];
                for (int i = 0; i < keys.Count; i++) parts[i] = keys[i].ToString();
                return string.Join("+", parts);
            }

            public override string ToString()
            {
                return OwnerName + ": " + Signature + " - " + Description;
            }
        }

        private static readonly List<Binding> Registry = new List<Binding>();

        /// <summary>Every live binding, in registration order.</summary>
        public static Binding[] Bindings
        {
            get { lock (Registry) { return Registry.ToArray(); } }
        }

        /// <summary>One owner's bindings, for its own menu page.</summary>
        public static Binding[] ForOwner(string ownerId)
        {
            var mine = new List<Binding>();
            if (string.IsNullOrEmpty(ownerId)) return mine.ToArray();

            lock (Registry)
            {
                foreach (var binding in Registry)
                {
                    if (binding.OwnerId == ownerId) mine.Add(binding);
                }
            }
            return mine.ToArray();
        }

        /// <summary>Whether the library's own menu hotkey has been bound.</summary>
        public static bool MenuBound
        {
            get
            {
                foreach (var binding in Bindings)
                {
                    if (binding.OwnerId != LibraryRuntime.LibraryId) continue;
                    if (IsMenuDescription(binding.Description)) return true;
                }
                return false;
            }
        }

        internal static bool IsMenuDescription(string description)
        {
            return description != null &&
                   description.IndexOf("mod menu", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Claim a key. <paramref name="modifiers"/> must all be held for the binding to fire;
        /// pass none for a bare key.
        /// </summary>
        public static Binding Register(ScamMod owner, Key key, string description, Action onPressed,
            params Key[] modifiers)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            return Register(owner.ModId, owner.ModLog, key, description, onPressed, modifiers);
        }

        /// <summary>
        /// Claim a key on behalf of something that is not a mod. Used by the library for its own menu
        /// hotkey, which has to exist regardless of which mods are installed.
        /// </summary>
        internal static Binding Register(string ownerId, ManualLogSource log, Key key, string description,
            Action onPressed, params Key[] modifiers)
        {
            if (onPressed == null) throw new ArgumentNullException("onPressed");

            var binding = new Binding
            {
                OwnerId = ownerId ?? LibraryRuntime.LibraryId,
                OwnerName = ownerId == LibraryRuntime.LibraryId ? "Scam WYF Modding" : ownerId,
                Description = description ?? "",
                Key = key,
                OnPressed = onPressed
            };

            if (modifiers != null)
            {
                foreach (var modifier in modifiers)
                {
                    if (modifier != Key.None && !binding.Modifiers.Contains(modifier))
                    {
                        binding.Modifiers.Add(modifier);
                    }
                }
            }

            List<Binding> clashes;
            lock (Registry)
            {
                clashes = Clashes(binding);
                Registry.Add(binding);
            }

            // The library's runner polls these each frame; it is created once, on demand.
            LibraryRuntime.Start();

            if (log != null)
            {
                log.LogInfo("Hotkey " + binding.Signature + " - " + binding.Description + ".");

                if (clashes.Count > 0)
                {
                    log.LogWarning(binding.Signature + " is already bound by " + Join(clashes) +
                                   ". Both will fire; give one of them a different key or a modifier.");
                }
            }

            return binding;
        }

        /// <summary>Drop every binding something owns. Called for you when a mod unloads.</summary>
        public static void RemoveOwner(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return;
            lock (Registry)
            {
                Registry.RemoveAll(binding => binding.OwnerId == ownerId);
            }
        }

        /// <summary>
        /// Re-point a binding at a different key, for a hotkey the user has just changed in the menu.
        /// </summary>
        public static bool Rebind(Binding binding, Key key)
        {
            if (binding == null) return false;

            lock (Registry)
            {
                binding.Key = key;
            }
            return true;
        }

        /// <summary>Keys claimed by more than one owner, as text, or null when there are none.</summary>
        public static string ConflictReport()
        {
            var report = new System.Text.StringBuilder();

            foreach (var group in ClashGroups())
            {
                var owners = new List<string>();
                foreach (var binding in group) owners.Add(binding.OwnerName);
                report.AppendLine("hotkey collision on " + group[0].Signature + ": " +
                                  string.Join(" + ", owners.ToArray()));
            }

            return report.Length == 0 ? null : report.ToString();
        }

        // ---------------------------------------------------------------- internals

        /// <summary>Poll every binding. Called once a frame by the library's runner.</summary>
        internal static void Pump()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            Binding[] snapshot;
            lock (Registry) { snapshot = Registry.ToArray(); }

            foreach (var binding in snapshot)
            {
                // Skipped while the menu has focus, so typing "f1" into a text field does not toggle it.
                if (UiPanel.MenuHasFocus) continue;

                var control = keyboard[binding.Key];
                if (control == null || !control.wasPressedThisFrame) continue;
                if (!ModifiersHeld(keyboard, binding)) continue;

                try
                {
                    if (binding.OnPressed != null) binding.OnPressed();
                }
                catch (Exception ex)
                {
                    UiLog.Error("Hotkey " + binding.Signature + " (" + binding.OwnerName + ") threw: " + ex);
                }
            }
        }

        private static bool ModifiersHeld(Keyboard keyboard, Binding binding)
        {
            for (int i = 0; i < binding.Modifiers.Count; i++)
            {
                var control = keyboard[binding.Modifiers[i]];
                if (control == null || !control.isPressed) return false;
            }
            return true;
        }

        private static List<Binding> Clashes(Binding candidate)
        {
            var clashes = new List<Binding>();
            foreach (var existing in Registry)
            {
                if (existing.OwnerId == candidate.OwnerId) continue;
                if (existing.Key != candidate.Key) continue;
                if (existing.Modifiers.Count != candidate.Modifiers.Count) continue;

                var same = true;
                for (int i = 0; i < existing.Modifiers.Count && same; i++)
                {
                    same = existing.Modifiers.Contains(candidate.Modifiers[i]) &&
                           candidate.Modifiers.Contains(existing.Modifiers[i]);
                }
                if (same) clashes.Add(existing);
            }
            return clashes;
        }

        /// <summary>
        /// Bindings sharing a key with a binding from a different owner, one list per key. One owner
        /// binding the same key twice is its own business and is not a collision.
        /// </summary>
        private static List<List<Binding>> ClashGroups()
        {
            var groups = new List<List<Binding>>();

            lock (Registry)
            {
                var seen = new List<Binding>();
                foreach (var binding in Registry)
                {
                    if (seen.Contains(binding)) continue;

                    var group = new List<Binding>();
                    foreach (var candidate in Registry)
                    {
                        if (candidate.Key != binding.Key) continue;
                        if (candidate.Modifiers.Count != binding.Modifiers.Count) continue;
                        if (Clashes(candidate).Count == 0) continue;
                        group.Add(candidate);
                    }

                    if (group.Count == 0) continue;
                    foreach (var member in group) seen.Add(member);
                    groups.Add(group);
                }
            }

            return groups;
        }

        private static string Join(List<Binding> bindings)
        {
            var names = new List<string>();
            foreach (var binding in bindings) names.Add(binding.OwnerName + " (" + binding.OwnerId + ")");
            return string.Join(", ", names.ToArray());
        }
    }
}