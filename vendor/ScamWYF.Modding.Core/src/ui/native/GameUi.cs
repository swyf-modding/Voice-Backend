using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// Reflection helpers for touching the game's UI Toolkit screens without referencing any game
    /// assembly.
    /// </summary>
    /// <remarks>
    /// The library compiles against Unity's own modules only - the game types
    /// (<c>MainMenuToolkitController</c>, <c>PauseMenuToolkitController</c>, <c>KolkataMonitorInputState</c>,
    /// <c>AudioSingleton</c>) are reached through <see cref="AccessTools"/> here, exactly like
    /// <see cref="GameWorkarounds"/> does for its patches. A game update that renames any of them
    /// fails to find and degrades to "no native entry point", never to a load failure.
    ///
    /// One of these helpers deserves its own explanation: <see cref="CopyTraits"/>. Cloning a game
    /// screen element is done by hand, because this Unity version has no
    /// <c>VisualElement.CloneHierarchy</c> and its class list is a private field with no public
    /// accessor. Styling in the game's sheets matches on <c>name</c> and classes, so copying those -
    /// plus focusable/tabIndex, which the game's focus ring consults - is all a stand-in element
    /// needs to look native.
    /// </remarks>
    internal static class GameUi
    {
        private static FieldInfo _classListField;
        private static bool _classListFailed;

        /// <summary>
        /// Read the class list of an element. Public API does not exist in this Unity version, so
        /// this is the private <c>m_ClassList</c> field; returns null rather than throwing.
        /// </summary>
        public static List<string> ReadClasses(VisualElement element)
        {
            if (element == null) return null;

            var list = _classListField;
            if (list == null && !_classListFailed)
            {
                list = AccessTools.Field(typeof(VisualElement), "m_ClassList");
                if (list == null)
                {
                    _classListFailed = true;
                    return null;
                }
                _classListField = list;
            }
            if (list == null) return null;

            try
            {
                // The field holds a List<string> that may be a shared, pooled empty list; it is read
                // only here and never written back.
                var snapshot = list.GetValue(element) as List<string>;
                return snapshot;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Make <paramref name="target"/> wear <paramref name="source"/>'s classes, so the same USS
        /// rules style both. Unity's own <c>unity-</c> classes are skipped - they are per-control
        /// type noise whose visuals come from the theme - and anything named in
        /// <paramref name="skip"/> is dropped, for state classes such as
        /// <c>menu-screen--active</c> that describe the source.
        /// </summary>
        /// <returns>True when the classes could be read at all; false means the game build's
        /// internals moved and callers should apply their own minimal styling.</returns>
        public static bool CopyTraits(VisualElement source, VisualElement target, string[] skip)
        {
            if (source == null || target == null) return false;

            var classes = ReadClasses(source);
            if (classes == null) return false;

            int read = 0;
            foreach (var className in classes)
            {
                if (string.IsNullOrEmpty(className)) continue;
                if (className.StartsWith("unity-", StringComparison.Ordinal)) continue;
                if (skip != null && Array.IndexOf(skip, className) >= 0) continue;
                target.AddToClassList(className);
                read++;
            }

            // Whatever keyboard behaviour the source had is worth keeping: modded screens and buttons
            // participate in the game's focus ring the same way real ones do.
            if (read > 0)
            {
                target.focusable = source.focusable;
                target.tabIndex = source.tabIndex;
            }
            return true;
        }

        /// <summary>
        /// Every live component of a game type that is in an actual scene. Does not throw and does
        /// not match prefab assets, whose lives the library must never touch.
        /// </summary>
        public static Component[] FindControllers(string typeName, string logLabel)
        {
            try
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) return Empty;

                var found = Resources.FindObjectsOfTypeAll(type);
                var live = new List<Component>();
                for (int i = 0; i < found.Length; i++)
                {
                    if (!(found[i] is Component component)) continue;
                    if (component == null) continue;
                    if (!component.gameObject.scene.IsValid()) continue;
                    live.Add(component);
                }
                return live.ToArray();
            }
            catch (Exception ex)
            {
                UiLog.Warn("Could not look up " + (logLabel ?? typeName) + ": " + ex.Message);
                return Empty;
            }
        }

        private static readonly Component[] Empty = new Component[0];

        /// <summary>
        /// The controllers' cached <c>root</c> VisualElement, or one straight from the UIDocument when
        /// the field has not been assigned yet. Null out of a build without the field.
        /// </summary>
        public static VisualElement RootOf(Component controller, string fieldName)
        {
            if (controller == null) return null;

            try
            {
                var field = AccessTools.Field(controller.GetType(), fieldName);
                if (field == null) return null;
                return field.GetValue(controller) as VisualElement;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
