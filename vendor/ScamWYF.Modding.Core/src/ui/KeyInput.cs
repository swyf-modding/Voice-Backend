using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// Polls whether a key is down, across both of Unity's input systems.
    /// </summary>
    /// <remarks>
    /// The game may use the new Input System only, in which case legacy <c>UnityEngine.Input</c> can
    /// throw, and the library does not reference <c>UnityEngine.InputLegacyModule</c> because its build
    /// does not include it. Both paths are therefore reached by reflection: legacy first, and
    /// <c>Keyboard.current</c> from <c>Unity.InputSystem</c> as the fallback.
    /// </remarks>
    internal static class KeyInput
    {
        private static bool _legacyBroken;
        private static bool _legacyInit;
        private static Type _inputType;
        private static MethodInfo _getKeyDown;

        private static bool _newInit;
        private static Type _kbType;
        private static PropertyInfo _current;
        private static PropertyInfo _wasPressed;
        private static readonly Dictionary<string, PropertyInfo> KeyProps =
            new Dictionary<string, PropertyInfo>();

        /// <summary>True when the key fired this frame.</summary>
        /// <param name="legacy">The legacy <see cref="KeyCode"/>.</param>
        /// <param name="newInputName">The same key's name on the new input system.</param>
        public static bool Pressed(KeyCode legacy, string newInputName)
        {
            if (!_legacyBroken)
            {
                try
                {
                    if (!_legacyInit)
                    {
                        _legacyInit = true;
                        _inputType = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")
                            ?? Type.GetType("UnityEngine.Input, UnityEngine");
                        if (_inputType != null)
                        {
                            _getKeyDown = _inputType.GetMethod("GetKeyDown", new[] { typeof(KeyCode) });
                        }
                    }

                    if (_getKeyDown != null)
                    {
                        var result = _getKeyDown.Invoke(null, new object[] { legacy });
                        if (result is bool && (bool)result) return true;
                    }
                }
                catch
                {
                    _legacyBroken = true;
                }
            }

            return NewInputPressed(newInputName);
        }

        private static bool NewInputPressed(string name)
        {
            try
            {
                if (!_newInit)
                {
                    _newInit = true;
                    _kbType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                    if (_kbType != null)
                    {
                        _current = _kbType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                    }
                }
                if (_current == null) return false;

                var kb = _current.GetValue(null, null);
                if (kb == null) return false;

                PropertyInfo keyProp;
                if (!KeyProps.TryGetValue(name, out keyProp))
                {
                    keyProp = _kbType.GetProperty(name);
                    KeyProps[name] = keyProp;
                }
                if (keyProp == null) return false;

                var key = keyProp.GetValue(kb, null);
                if (key == null) return false;

                if (_wasPressed == null)
                {
                    _wasPressed = key.GetType().GetProperty("wasPressedThisFrame");
                }
                return _wasPressed != null && (bool)_wasPressed.GetValue(key, null);
            }
            catch
            {
                return false;
            }
        }
    }
}