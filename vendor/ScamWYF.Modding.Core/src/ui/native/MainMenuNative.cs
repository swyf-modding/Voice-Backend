using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The native entry point on the game's title menu: a "MODS" home button next to SETTINGS,
    /// which toggles the mod menu over it.
    /// </summary>
    /// <remarks>
    /// The mod menu is its own screen dressed exactly like the settings pages
    /// (<see cref="SettingsMenu"/>), so this button has nothing to style and nothing to route
    /// through the game: it is the handle the player grabs, and clicking it behaves like every
    /// other menu button on this screen.
    ///
    /// The button is a plain Button wearing the SETTINGS button's classes, so the game's stylesheet
    /// makes it a native-looking word button. The game caches its title buttons for the idle
    /// floating animation after its UI initializes, so re-running that cache after inserting keeps
    /// the real buttons animating exactly as they did - a mod button that sits perfectly still next
    /// to five drifting ones would announce itself immediately.
    /// </remarks>
    internal static class MainMenuNative
    {
        private const string ControllerType = "MainMenuToolkitController";

        private static Injection _injection;

        /// <summary>True once a title menu with the expected elements is live.</summary>
        public static bool Alive
        {
            get { return _injection != null && _injection.Alive; }
        }

        // ---------------------------------------------------------------- polling

        /// <summary>
        /// Detect (or lose) the title menu and keep the injection current. Run every frame by
        /// <see cref="NativeUi"/>; the actual search is throttled because
        /// <c>Resources.FindObjectsOfTypeAll</c> is not cheap.
        /// </summary>
        public static void Tick()
        {
            if (_injection != null && _injection.Alive) return;

            if (_injection != null)
            {
                // Scene change or menu rebuild: forget the dead one now so the next search is clean.
                _injection = null;
                _nextLookup = 0f;
            }
            if (Time.unscaledTime < _nextLookup) return;
            _nextLookup = Time.unscaledTime + LookupInterval;

            try
            {
                var controllers = GameUi.FindControllers(ControllerType, "the title menu");
                for (int i = 0; i < controllers.Length; i++)
                {
                    var candidate = new Injection(controllers[i]);
                    if (!candidate.Alive) continue;

                    _injection = candidate;
                    UiLog.Info("Added the MODS button to the title menu.");
                    return;
                }
            }
            catch (Exception ex)
            {
                // Injection failure must leave the menu usable, and must not retry every frame.
                _nextLookup = Time.unscaledTime + 5f;
                UiLog.Warn("Could not add the MODS button to the title menu: " + ex.Message);
            }
        }

        private static float _nextLookup;

        /// <summary>Half a second: under a second of delay after a scene load, unnoticed in play.</summary>
        private const float LookupInterval = 0.5f;

        // ---------------------------------------------------------------- injection

        private sealed class Injection
        {
            private Component _controller;
            private VisualElement _root;
            private VisualElement _home;
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
                    Build(controller);
                }
                catch (Exception ex)
                {
                    // Anything built before the throw is discarded with this injection and nothing
                    // has been added to the game's tree, so the next tick retries cleanly instead of
                    // stacking injected buttons.
                    UiLog.Warn("Title-menu injection incomplete: " + ex.Message);
                }
            }

            /// <summary>
            /// The actual injection. Nothing here mutates the game's tree until the very end, so a
            /// failure leaves it exactly as it was found.
            /// </summary>
            private void Build(Component controller)
            {
                _home = _root.Q<VisualElement>("home-screen");
                var settings = _root.Q<Button>("settings-button");
                var quit = _root.Q<Button>("quit-button");
                if (_home == null || settings == null || quit == null) return;
                if (_home.parent == null) return;

                // The button, last of all. Named so that no game lookup ("settings-button",
                // "quit-button") can ever resolve to it, and dressed by copying the SETTINGS
                // button's classes, which is what makes it look like the game drew it.
                _modsButton = new Button { name = "swyf-mods-button", text = "MODS" };
                GameUi.CopyTraits(settings, _modsButton, null);
                _modsButton.clicked += delegate { GameSounds.Click(); ModMenu.Open(); };

                _home.Insert(_home.IndexOf(settings) + 1, _modsButton);

                // The game animates its title buttons; refreshed after insertion, so the whole set
                // - including this one - keeps the same idle motion it had before.
                ReflectionInvoke("CacheFloatingButtons");
            }

            internal bool Alive
            {
                get
                {
                    if (_controller == null) return false;
                    var behaviour = _controller as Behaviour;
                    if (behaviour != null && !behaviour.enabled) return false;
                    if (!_controller.gameObject.activeInHierarchy) return false;

                    return _root != null && _root.panel != null &&
                           _home != null && _home.panel != null &&
                           _modsButton != null && _modsButton.panel != null;
                }
            }

            /// <summary>
            /// Invoke a private instance method on the controller. Used for
            /// <c>CacheFloatingButtons</c>, which is game-private by design.
            /// </summary>
            private void ReflectionInvoke(string methodName)
            {
                try
                {
                    var method = AccessTools.Method(_controller.GetType(), methodName);
                    if (method == null) return;
                    method.Invoke(_controller, null);
                }
                catch (Exception)
                {
                    // The floating animation working or not working is cosmetic; dropping the
                    // invocation is strictly better than throwing out of injection.
                }
            }
        }
    }
}
