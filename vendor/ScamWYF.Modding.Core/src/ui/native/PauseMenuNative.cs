using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The native entry point on the settings page: a "MODS" button beside PRESS KIT in its top
    /// row, which toggles the mod menu over it.
    /// </summary>
    /// <remarks>
    /// There is deliberately no MODS tab in the settings page's own side strip - the user asked for
    /// the mod menu to be a separate thing, not a page among the game's settings. What remains
    /// attached to the settings page is just a handle: one more small button dressed exactly like
    /// PRESS KIT (a copy of its classes), sitting right beside it, opening the menu the same way the
    /// title menu's MODS button does.
    ///
    /// The settings page rebuilds its whole UI Toolkit tree every time it opens (the game's
    /// controller runs InitializeUi from OnEnable), so this injection lives and dies with it: the
    /// poll in <see cref="Tick"/> re-detects each opening, the same pattern the title menu's button
    /// uses on a slower cadence.
    /// </remarks>
    internal static class PauseMenuNative
    {
        private const string ControllerType = "PauseMenuToolkitController";

        private static Injection _injection;

        public static void Tick()
        {
            if (_injection != null && _injection.Alive) return;

            if (_injection != null)
            {
                _injection = null;
                _nextLookup = 0f;
            }
            if (Time.unscaledTime < _nextLookup) return;
            _nextLookup = Time.unscaledTime + LookupInterval;

            try
            {
                var controllers = GameUi.FindControllers(ControllerType, "the pause menu");
                for (int i = 0; i < controllers.Length; i++)
                {
                    var candidate = new Injection(controllers[i]);
                    if (!candidate.Alive) continue;

                    _injection = candidate;
                    UiLog.Info("Added the MODS button to the settings page.");
                    return;
                }
            }
            catch (Exception ex)
            {
                _nextLookup = Time.unscaledTime + 5f;
                UiLog.Warn("Could not add the MODS button to the settings page: " + ex.Message);
            }
        }

        private static float _nextLookup;

        /// <summary>Shorter than the title menu's: the settings page opens and closes often.</summary>
        private const float LookupInterval = 0.35f;

        // ---------------------------------------------------------------- injection

        private sealed class Injection
        {
            private Component _controller;
            private VisualElement _root;
            private VisualElement _origin;
            private Button _modsButton;

            internal Injection(Component controller)
            {
                _controller = controller;

                var document = controller.GetComponent<UIDocument>();
                _root = document != null
                    ? document.rootVisualElement
                    : GameUi.RootOf(controller, "root");
                if (_root == null) return;

                try
                {
                    Build();
                }
                catch (Exception ex)
                {
                    UiLog.Warn("Settings-page injection incomplete: " + ex.Message);
                }
            }

            /// <summary>
            /// The actual injection. Nothing touches the game tree until the button is complete, so
            /// a failure leaves the settings page untouched for the next tick to retry.
            /// </summary>
            private void Build()
            {
                // PRESS KIT is the template: same size, same corner, same look, one over.
                var pressKit = _root.Q<Button>("press-kit-button");
                if (pressKit == null || pressKit.parent == null) return;
                _origin = pressKit;

                _modsButton = new Button { name = "swyf-mods-open-button", text = "MODS" };
                GameUi.CopyTraits(pressKit, _modsButton, null);
                _modsButton.clicked += delegate { GameSounds.Click(); ModMenu.Open(); };

                var row = pressKit.parent;
                row.Insert(row.IndexOf(pressKit) + 1, _modsButton);
            }

            internal bool Alive
            {
                get
                {
                    // The settings controller is disabled while its page is closed, and rebuilds
                    // its tree at each opening; enabled-ness is what makes this injection die
                    // between openings.
                    var behaviour = _controller as Behaviour;
                    if (behaviour != null && !behaviour.enabled) return false;
                    if (_controller != null && !_controller.gameObject.activeInHierarchy) return false;

                    return _controller != null &&
                           _root != null && _root.panel != null &&
                           _origin != null && _origin.panel != null &&
                           _modsButton != null && _modsButton.panel != null;
                }
            }
        }
    }
}
