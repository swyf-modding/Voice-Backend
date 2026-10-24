using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>
    /// The mod menu: a private copy of the game's own settings page, with its tabs and screens
    /// replaced by ours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The earlier iterations of this menu tried to look native by injecting a panel into the
    /// game's settings tree, or by dressing a separate window with copies of the settings page's
    /// classes. Neither ever read as the game, because the game's look is not a handful of
    /// classes - it is the whole tree: the UXML layout, the stylesheet that ships with it, and the
    /// way the controller drives selection. Copying pieces of it always left seams.
    /// </para>
    /// <para>
    /// The version that works is the one a working mock of this menu settled on: take the game's
    /// own settings page UXML and <see cref="PanelSettings"/> and build a second, inert copy of
    /// the whole thing. Unity instantiates a fresh copy of the layout with the game's real
    /// styling, and because the game's controller is not on our copy, nothing we do touches the
    /// real settings page. We then strip the game's tabs and screens out and put ours in their
    /// place, reusing the same classes (<c>tab-button</c>, <c>settings-screen</c>,
    /// <c>setting-row</c>, ...) so every pixel is drawn by the rules the game ships. The result is
    /// indistinguishable from the settings page, because it is one.
    /// </para>
    /// <para>
    /// The copy is created lazily on first open and kept alive (never deactivated - that would
    /// throw the tree away) with show and hide done through <c>root.style.display</c>, exactly as
    /// the mock does. Open, close and toggle are cheap after the first time.
    /// </para>
    /// </remarks>
    internal static class SettingsMenu
    {
        private const string TemplateName = "PauseMenu";
        private const string CloneName = "ModsMenuDocument";

        private static GameObject _go;
        private static UIDocument _doc;
        private static bool _customized;
        private static bool _wantOpen;
        private static bool _shown;

        private static VisualElement _tabBar;
        private static VisualElement _card;
        private static string _pendingSelect;

        private static readonly List<Button> _tabs = new List<Button>();
        private static readonly List<VisualElement> _screens = new List<VisualElement>();

        public static bool IsOpen
        {
            get { return _wantOpen; }
        }

        public static void Open()
        {
            _wantOpen = true;
            if (_go == null && !Create())
            {
                _wantOpen = false;
                return;
            }
            Tick();
        }

        public static void Close()
        {
            _wantOpen = false;
            Tick();
        }

        public static void Toggle()
        {
            if (_wantOpen) Close();
            else Open();
        }

        /// <summary>Activate the tab whose page matches, if it is currently shown.</summary>
        public static void SelectPage(MenuPage page)
        {
            if (page == null || !_customized) return;

            var tabs = ModMenu.ListTabs();
            if (tabs == null) return;

            for (int i = 0; i < tabs.Length; i++)
            {
                if (tabs[i].Page != null && tabs[i].Page == page)
                {
                    Select(i);
                    return;
                }
            }
        }

        /// <summary>Activate the tab for a built-in page key, if it is currently shown.</summary>
        public static void SelectBuiltIn(string key)
        {
            if (string.IsNullOrEmpty(key) || !_customized) return;

            var tabs = ModMenu.ListTabs();
            if (tabs == null) return;

            for (int i = 0; i < tabs.Length; i++)
            {
                if (tabs[i].BuiltInKey != null && tabs[i].BuiltInKey == key)
                {
                    Select(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Rebuild the tabs from the current page model. Called by <see cref="ModMenu.Refresh"/>
        /// so a mod that registers, hides or removes pages is represented without reopening.
        /// </summary>
        public static void Rebuild()
        {
            if (!_customized || _tabBar == null || _card == null) return;

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i] != null && _tabs[i].panel != null) _tabs[i].RemoveFromHierarchy();
            }
            for (int i = 0; i < _screens.Count; i++)
            {
                if (_screens[i] != null && _screens[i].panel != null) _screens[i].RemoveFromHierarchy();
            }
            _tabs.Clear();
            _screens.Clear();

            var tabs = ModMenu.ListTabs();
            if (tabs == null || tabs.Length == 0)
            {
                var empty = new Label("No mod has added anything to show yet.");
                empty.AddToClassList("setting-help");
                _card.Add(empty);
                return;
            }

            for (int i = 0; i < tabs.Length; i++)
            {
                AddTab(_tabBar, _card, tabs[i], i);
            }

            Select(0);
        }

        // ------------------------------------------------------------------ lifecycle

        public static void Tick()
        {
            if (_doc == null) return;

            var root = _doc.rootVisualElement;
            if (root == null) return;

            if (!_customized)
            {
                if (root.Q("pause-root") == null) return; // tree not built yet
                _customized = true;
                Customize(root);
                _shown = !_wantOpen; // force the first show/hide to apply
            }

            if (!string.IsNullOrEmpty(_pendingSelect))
            {
                var key = _pendingSelect;
                _pendingSelect = null;
                SelectBuiltIn(key);
            }

            if (_shown != _wantOpen)
            {
                _shown = _wantOpen;
                root.style.display = _wantOpen ? DisplayStyle.Flex : DisplayStyle.None;
            }

            // Escape closes the menu, the way the mock does it: poll the input state every frame and
            // call Close when it fires. The game may be on the new input system only, so the legacy
            // path is tried first and the reflection fallback covers the rest.
            if (_wantOpen && KeyInput.Pressed(KeyCode.Escape, "escape"))
            {
                Close();
            }
        }

        // ------------------------------------------------------------------ creation

        private static bool Create()
        {
            UIDocument template = null;
            foreach (var d in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (d.gameObject != null && d.gameObject.name == CloneName) continue;
                if (d.visualTreeAsset != null && d.visualTreeAsset.name == TemplateName)
                {
                    template = d;
                    break;
                }
            }

            if (template == null || template.panelSettings == null)
            {
                UiLog.Warn("The settings page template ('" + TemplateName + "') was not found yet; " +
                           "the mod menu cannot open until the game has built it once.");
                return false;
            }

            // Created inactive so the tree is built once, after everything is assigned.
            _go = new GameObject(CloneName);
            _go.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_go);

            _doc = _go.AddComponent<UIDocument>();
            _doc.panelSettings = template.panelSettings;
            _doc.visualTreeAsset = template.visualTreeAsset;
            _doc.sortingOrder = template.sortingOrder + 100f; // above the real settings + main menu

            _go.SetActive(true);
            return true;
        }

        // ------------------------------------------------------------------ customizing the copy

        private static void Customize(VisualElement root)
        {
            var tabBar = root.Q(className: "tab-bar");
            var card = root.Q(className: "settings-card");
            if (tabBar == null || card == null)
            {
                UiLog.Warn("The settings page layout changed (no .tab-bar / .settings-card); " +
                           "the mod menu cannot be drawn.");
                return;
            }

            _tabBar = tabBar;
            _card = card;

            // Heading.
            var title = root.Q<Label>("game-title");
            if (title != null) title.text = "MODS";

            Hide(root.Q("early-access-label"));

            var version = root.Q<Label>("version-label");
            if (version != null) version.text = "MODS";

            // Left rail: remove game actions and voice controls, turn "leave" into BACK.
            var actions = root.Q(className: "action-rail-actions");
            if (actions != null) actions.Clear();
            Hide(root.Q(className: "voice-controls"));

            var leave = root.Q<Button>("leave-button");
            if (leave != null)
            {
                leave.text = "BACK";
                leave.RemoveFromClassList("rail-button--danger");
                leave.clicked += Close;
            }

            // Bottom utility bar: RESUME -> CLOSE, hide the rest.
            var resume = root.Q<Button>("resume-utility-button");
            if (resume != null)
            {
                resume.text = "CLOSE";
                resume.clicked += Close;
            }
            Hide(root.Q("feedback-button"));
            Hide(root.Q("press-kit-button"));

            // Replace the settings tabs and screens with ours.
            tabBar.Clear();
            card.Clear();
            _tabs.Clear();
            _screens.Clear();

            var list = ModMenu.ListTabs();
            if (list == null || list.Length == 0)
            {
                var empty = new Label("No mod has added anything to show yet.");
                empty.AddToClassList("setting-help");
                card.Add(empty);
                return;
            }

            for (int i = 0; i < list.Length; i++)
            {
                AddTab(tabBar, card, list[i], i);
            }

            Select(0);
        }

        private static void AddTab(VisualElement tabBar, VisualElement card, ModMenu.TabEntry entry, int index)
        {
            var tab = new Button { text = entry.Title != null ? entry.Title.ToUpperInvariant() : "" };
            tab.AddToClassList("tab-button");

            var screen = new ScrollView(ScrollViewMode.Vertical);
            screen.AddToClassList("settings-screen");

            int captured = index;
            tab.clicked += delegate { Select(captured); };

            _tabs.Add(tab);
            _screens.Add(screen);
            tabBar.Add(tab);
            card.Add(screen);

            // Page content, drawn with the game's classes so it matches the rows above and below.
            Widgets.GameTree = true;
            try
            {
                ModMenu.RenderTab(screen, entry.BuiltInKey, entry.BuiltInKey != null ? null : entry.Page);
            }
            finally
            {
                Widgets.GameTree = false;
            }
        }

        private static void Select(int index)
        {
            for (int i = 0; i < _tabs.Count && i < _screens.Count; i++)
            {
                bool on = i == index;
                _tabs[i].EnableInClassList("tab-button--active", on);
                _screens[i].EnableInClassList("settings-screen--active", on);
                _screens[i].style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // ------------------------------------------------------------------ small helpers

        private static void Hide(VisualElement ve)
        {
            if (ve != null) ve.style.display = DisplayStyle.None;
        }
    }
}
