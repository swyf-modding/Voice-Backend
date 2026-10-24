using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The one UIDocument every mod window and menu tab draws into.
    /// </summary>
    /// <remarks>
    /// The base game builds its menus with UI Toolkit - UIDocument, PanelSettings, a theme stylesheet -
    /// so a mod that wants to look like it belongs inherits that stack rather than inventing a second
    /// one. This class is the whole of the shared part:
    ///
    ///  - one hidden GameObject and one UIDocument, created on first use and kept across scene loads;
    ///  - a PanelSettings cloned from the game's own, so the theme, fonts and scaling rules are the
    ///    game's rather than a lookalike;
    ///  - a root element that ignores the pointer when nothing is open, so an idle overlay can never
    ///    eat clicks meant for the game.
    ///
    /// Finding the game's PanelSettings is the fiddly part. BepInEx loads plugins before any scene
    /// does, so at Awake there may be no UIDocument in the world yet. The lookup therefore runs on
    /// demand and <em>retries</em> rather than giving up: a menu opened in the first second of a
    /// session is not permanently broken.
    ///
    /// A runtime-created PanelSettings needs a theme stylesheet and a font or UI Toolkit renders
    /// nothing at all - that is what the "No Theme Style Sheet set to PanelSettings" warning is about.
    /// Both are taken from the game's own assets when they can be found, and there is a last-resort
    /// path that finds any loaded ThemeStyleSheet and FontAsset, since the base game certainly has both
    /// in memory by the time a person presses a menu hotkey.
    /// </remarks>
    public static class UiPanel
    {
        /// <summary>
        /// Sorting order for the mod overlay. Above the game's own screen panels, so a mod menu is not
        /// hidden behind the pause menu it was opened from.
        /// </summary>
        private const float OverlaySortingOrder = 5000f;

        private static readonly List<Action> Tickers = new List<Action>();

        private static UIDocument _document;
        private static PanelSettings _panelSettings;
        private static VisualElement _root;
        private static VisualElement _overlay;
        private static string _panelSource;
        private static string _lastFailure;
        private static bool _wanted;
        private static bool _warnedFailure;

        /// <summary>
        /// The overlay root, created on first use. Null when UI Toolkit cannot be set up yet, which is
        /// logged once and retried rather than thrown: a mod with no menu is degraded, a mod that
        /// takes the session down is not acceptable.
        /// </summary>
        public static VisualElement Ensure()
        {
            _wanted = true;
            if (_overlay != null) return _overlay;

            if (!TryAdoptGamePanelSettings())
            {
                // Not fatal yet. A scene may not have loaded; the library's runner tries again.
                _lastFailure = "no PanelSettings with a theme is available yet";
                return null;
            }

            try
            {
                Build();
            }
            catch (Exception ex)
            {
                _lastFailure = "the UIDocument could not be created: " + ex.Message;
                Report(_lastFailure);
                _panelSettings = null;
                return null;
            }

            UiTheme.Sample(_root);
            return _overlay;
        }

        /// <summary>True once the overlay exists and can be drawn into.</summary>
        public static bool Available
        {
            get { return _overlay != null; }
        }

        /// <summary>Why the overlay could not be built, or null when it is fine.</summary>
        public static string UnavailableReason
        {
            get { return _overlay != null ? null : _lastFailure; }
        }

        /// <summary>How the PanelSettings was obtained, for the log and the About page.</summary>
        public static string PanelSource
        {
            get { return _panelSource ?? "not created yet"; }
        }

        /// <summary>The overlay root, or null. For drawing without asking for the panel to exist.</summary>
        public static VisualElement Root
        {
            get { return _overlay; }
        }

        /// <summary>Run something every frame. For a mod that needs per-frame work of its own.</summary>
        public static void AddTicker(Action tick)
        {
            if (tick == null) return;
            lock (Tickers) { Tickers.Add(tick); }
        }

        public static void RemoveTicker(Action tick)
        {
            if (tick == null) return;
            lock (Tickers) { Tickers.Remove(tick); }
        }

        /// <summary>
        /// True when a text field in the overlay has keyboard focus.
        /// </summary>
        /// <remarks>
        /// Hotkeys poll the raw keyboard, so without this a person typing "f1" into the config editor's
        /// "Menu key" field would toggle the menu mid-keystroke. Only text entry suppresses hotkeys:
        /// a toggle or a slider still wants F1 to work while it is being adjusted.
        /// </remarks>
        public static bool TextEntryFocused
        {
            get
            {
                if (_root == null) return false;
                if (_root.focusController == null) return false;

                // focusController hands back a Focusable, which is the base of more than VisualElement,
                // so the walk has to go through VisualElement explicitly.
                var element = _root.focusController.focusedElement as VisualElement;
                for (; element != null; element = element.parent)
                {
                    if (element is TextField) return true;
                }
                return false;
            }
        }

        /// <summary>Alias for <see cref="TextEntryFocused"/>, spelled for the hotkey poller.</summary>
        internal static bool MenuHasFocus
        {
            get { return TextEntryFocused; }
        }

        /// <summary>Hide the overlay entirely. Used when every window has closed.</summary>
        public static void HideOverlay()
        {
            if (_overlay != null) _overlay.style.display = DisplayStyle.None;
            if (_root != null) _root.style.display = DisplayStyle.None;
        }

        /// <summary>Show the overlay root. Windows decide for themselves whether they are visible.</summary>
        public static void ShowOverlay()
        {
            if (_root != null) _root.style.display = DisplayStyle.Flex;
            if (_overlay != null) _overlay.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Called once a frame by the library's runner. Runs any mod tickers and retries the panel, so
        /// a menu opened before the first scene loaded still turns up.
        /// </summary>
        internal static void Tick()
        {
            Action[] snapshot;
            lock (Tickers) { snapshot = Tickers.ToArray(); }

            foreach (var tick in snapshot)
            {
                try
                {
                    tick();
                }
                catch (Exception ex)
                {
                    UiLog.Error("A per-frame UI callback threw: " + ex);
                }
            }

            if (_overlay != null)
            {
                // USS custom properties only resolve after a style pass, so the first sample can land
                // before the theme has been read at all. UiTheme keeps its result once the theme really
                // is readable, so retrying here costs nothing and means the palette is the game's rather
                // than the fallback whenever it can be.
                UiTheme.Sample(_root);
                return;
            }

            if (_wanted) Ensure();
        }

        // ---------------------------------------------------------------- internals

        private static void Build()
        {
            var go = new GameObject("ScamWYF.Modding.Ui");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            _document = go.AddComponent<UIDocument>();
            _document.panelSettings = _panelSettings;
            _document.sortingOrder = 1f;

            _root = _document.rootVisualElement;
            _root.name = "scamwyf-mod-ui";

            // Full-screen and invisible; the overlay below is what actually draws.
            Stretch(_root);
            // Nothing open means nothing should catch the mouse.
            _root.pickingMode = PickingMode.Ignore;
            _root.style.display = DisplayStyle.None;

            _overlay = new VisualElement();
            _overlay.name = "overlay";
            Stretch(_overlay);
            _overlay.pickingMode = PickingMode.Ignore;
            _root.Add(_overlay);
        }

        private static void Stretch(VisualElement element)
        {
            element.style.position = Position.Absolute;
            element.style.left = 0f;
            element.style.top = 0f;
            element.style.right = 0f;
            element.style.bottom = 0f;
        }

        /// <summary>
        /// Clone the game's PanelSettings so the mod overlay inherits its theme, fonts and scaling.
        /// Falls back to a runtime-created panel carrying any theme and font already in memory.
        /// </summary>
        private static bool TryAdoptGamePanelSettings()
        {
            var existing = Resources.FindObjectsOfTypeAll<PanelSettings>();
            for (int i = 0; i < existing.Length; i++)
            {
                var candidate = existing[i];
                if (candidate == null) continue;

                // A theme is not optional: without one UI Toolkit draws unstyled controls and no text
                // at all, so a panel without one is no use here.
                if (candidate.themeStyleSheet == null) continue;

                var clone = UnityEngine.Object.Instantiate(candidate);
                if (clone == null) continue;

                clone.sortingOrder = OverlaySortingOrder;
                _panelSettings = clone;
                _panelSource = "cloned from the game's own PanelSettings (" + candidate.name + ")";
                return true;
            }

            var themes = Resources.FindObjectsOfTypeAll<ThemeStyleSheet>();
            if (themes.Length == 0)
            {
                // Worth one line: this is the difference between "no menu, and I know why" and
                // "no menu, and I have no idea".
                Report("No PanelSettings or ThemeStyleSheet is loaded yet; the mod menu will appear " +
                       "once a scene using UI Toolkit has loaded.");
                return false;
            }

            var own = ScriptableObject.CreateInstance<PanelSettings>();
            own.themeStyleSheet = themes[0];
            own.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            own.referenceResolution = new Vector2Int(1920, 1080);
            own.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            own.match = 0.5f;
            own.clearColor = false;
            own.sortingOrder = OverlaySortingOrder;

            // Text needs a font. The game has one loaded if any of its own text has rendered, and
            // without a font the menu draws as empty boxes.
            var fonts = Resources.FindObjectsOfTypeAll<FontAsset>();
            if (fonts.Length > 0)
            {
                var text = ScriptableObject.CreateInstance<PanelTextSettings>();
                text.defaultFontAsset = fonts[0];
                own.textSettings = text;
            }

            _panelSettings = own;
            _panelSource = fonts.Length > 0
                ? "built at runtime from the game's theme and font"
                : "built at runtime from the game's theme, but no FontAsset was loaded so text may not render";
            return true;
        }

        /// <summary>Log a problem once. Repeating it every frame would fill LogOutput.log.</summary>
        private static void Report(string message)
        {
            if (_warnedFailure) return;
            _warnedFailure = true;
            UiLog.Warn(message);
        }
    }

    /// <summary>Logging for the library's own UI, which has no mod to borrow a logger from.</summary>
    internal static class UiLog
    {
        private static BepInEx.Logging.ManualLogSource _log;

        private static BepInEx.Logging.ManualLogSource Log
        {
            get
            {
                if (_log == null) _log = BepInEx.Logging.Logger.CreateLogSource("ScamWYF.Modding.Ui");
                return _log;
            }
        }

        public static void Info(string message)
        {
            Log.LogInfo(message);
        }

        public static void Warn(string message)
        {
            Log.LogWarning(message);
        }

        public static void Error(string message)
        {
            Log.LogError(message);
        }
    }
}