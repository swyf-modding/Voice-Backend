using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Ui
{
    /// <summary>One tab in the universal menu.</summary>
    public sealed class MenuPage
    {
        internal MenuPage(string ownerId, string ownerName, string title, Action<VisualElement> build,
            int order)
        {
            OwnerId = ownerId;
            OwnerName = ownerName;
            Title = title;
            Build = build;
            Order = order;
        }

        /// <summary>The mod that registered this page.</summary>
        public string OwnerId { get; private set; }

        /// <summary>That mod's display name.</summary>
        public string OwnerName { get; private set; }

        public string Title { get; private set; }

        /// <summary>Fills the page. Called each time the tab is selected, so a page may rebuild freely.</summary>
        public Action<VisualElement> Build { get; private set; }

        /// <summary>Lower sorts first.</summary>
        public int Order { get; private set; }

        /// <summary>
        /// When false the tab is hidden but kept, for a mod that is loaded and has nothing to show right
        /// now. Call <see cref="ModMenu.Refresh"/> after changing it.
        /// </summary>
        public bool Available { get; set; }

        public override string ToString()
        {
            return OwnerName + " - " + Title;
        }
    }

    /// <summary>
    /// The one menu every mod shares.
    /// </summary>
    /// <remarks>
    /// Previously each mod drew its own window: one hotkey per mod, several windows to mis-click, and
    /// no way to see what else was installed. This is the opposite. One menu, one hotkey, one tab per
    /// thing a mod wants to show.
    ///
    /// A mod contributes a page and nothing else. The menu owns the screen, the side strip, the hotkey
    /// and the layout, so pages cannot fight over any of it - the same argument this library makes about
    /// Harmony patches and hotkeys, applied to interfaces.
    ///
    /// The menu belongs to the library rather than to the mod handler. A user who only wants the AI
    /// backend should not have to install a plugin manager to reach its settings, and a user with no
    /// mods at all should still get somewhere to see what went wrong.
    ///
    /// Two kinds of tab exist. A mod page is registered by a mod and disappears when that mod unloads.
    /// A built-in page is registered by key, so registering a key twice replaces it rather than
    /// stacking: that is how the mod handler takes over the "Plugins" tab the library reserves for it.
    ///
    /// The menu draws itself as its own fullscreen screen dressed exactly like the game's settings
    /// pages - that is <see cref="SettingsMenu"/>'s job. This class is the page model and the public
    /// API in front of it.
    /// </remarks>
    public static class ModMenu
    {
        /// <summary>Built-in tab key for the list of loaded mods.</summary>
        public const string ModsTab = "mods";

        /// <summary>Built-in tab key describing this session and this library.</summary>
        public const string AboutTab = "about";

        /// <summary>
        /// Reserved for whoever wants to show installed plugins - in practice the mod handler. The
        /// library registers a placeholder so the side strip has a sensible shape without it.
        /// </summary>
        public const string PluginsTab = "plugins";

        private sealed class BuiltInPage
        {
            public string Key;
            public string Title;
            public Action<VisualElement> Build;
            public Func<bool> Available;
        }

        private static readonly List<MenuPage> Pages = new List<MenuPage>();
        private static readonly List<BuiltInPage> BuiltIn = new List<BuiltInPage>();

        private static int _nextOrder = 0;
        private static bool _refreshing;

        /// <summary>
        /// One thing a tab strip can show, in a form any surface of the menu can consume: a
        /// built-in page by key, or a mod page.
        /// </summary>
        internal sealed class TabEntry
        {
            internal string BuiltInKey;
            internal MenuPage Page;
            internal string Title;
        }

        /// <summary>
        /// Whether the menu is showing anywhere: its own settings-page-shaped screen.
        /// </summary>
        public static bool IsOpen
        {
            get { return SettingsMenu.IsOpen; }
        }

        /// <summary>Every registered mod page, including ones currently hidden.</summary>
        public static MenuPage[] RegisteredPages
        {
            get { lock (Pages) { return Pages.ToArray(); } }
        }

        // ---------------------------------------------------------------- mod pages

        /// <summary>
        /// Contribute a tab. The build callback is handed a fresh container each time the tab is
        /// selected, so a page can be as simple as "make some labels" or as elaborate as it likes.
        /// </summary>
        public static MenuPage AddPage(ScamMod owner, string title, Action<VisualElement> build)
        {
            return AddPage(owner, title, build, _nextOrder++);
        }

        /// <summary>
        /// Contribute a tab at a chosen position, for a mod that wants to sit at the top or the bottom
        /// rather than wherever it happened to load.
        /// </summary>
        public static MenuPage AddPage(ScamMod owner, string title, Action<VisualElement> build, int order)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (string.IsNullOrEmpty(title)) throw new ArgumentNullException("title");
            if (build == null) throw new ArgumentNullException("build");

            var page = new MenuPage(owner.ModId, owner.DisplayName, title, build, order) { Available = true };

            lock (Pages) { Pages.Add(page); }

            owner.ModLog.LogInfo("Added the '" + title + "' tab to the mod menu.");
            Refresh();
            return page;
        }

        /// <summary>Take a mod's tabs away. Called for you when a mod unloads.</summary>
        public static void RemoveOwner(string modId)
        {
            if (string.IsNullOrEmpty(modId)) return;

            lock (Pages)
            {
                Pages.RemoveAll(page => page.OwnerId == modId);
            }

            Refresh();
        }

        /// <summary>True when this mod currently has at least one tab showing.</summary>
        public static bool HasPage(ScamMod owner)
        {
            if (owner == null) return false;

            foreach (var page in RegisteredPages)
            {
                if (page.OwnerId == owner.ModId && page.Available) return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- built-in pages

        /// <summary>
        /// Register or replace a tab the library draws. Keyed, so a second call with the same key
        /// replaces the first rather than adding a duplicate.
        /// </summary>
        public static void SetBuiltInPage(string key, string title, Action<VisualElement> build)
        {
            SetBuiltInPage(key, title, build, null);
        }

        /// <summary>
        /// Register or replace a tab the library draws, showing it only while
        /// <paramref name="available"/> returns true. A hidden tab keeps its place in the order.
        /// </summary>
        public static void SetBuiltInPage(string key, string title, Action<VisualElement> build,
            Func<bool> available)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException("key");

            lock (BuiltIn)
            {
                BuiltIn.RemoveAll(page => page.Key == key);
                if (build != null)
                {
                    BuiltIn.Add(new BuiltInPage
                    {
                        Key = key,
                        Title = title,
                        Build = build,
                        Available = available
                    });
                }
            }

            Refresh();
        }

        // ---------------------------------------------------------------- opening

        /// <summary>
        /// Open the menu: its own screen, dressed like the game's settings pages, over whatever the
        /// game is showing. False when the overlay cannot be built yet.
        /// </summary>
        public static bool Open()
        {
            SettingsMenu.Open();
            return SettingsMenu.IsOpen;
        }

        public static void Close()
        {
            SettingsMenu.Close();
        }

        public static void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        /// <summary>
        /// Open the menu on one of a mod's tabs. For a hotkey that means "open the AI backend's tab"
        /// rather than merely "open the menu".
        /// </summary>
        public static bool Show(ScamMod owner, string title)
        {
            if (owner == null) return false;

            MenuPage target = null;
            foreach (var page in OrderedPages())
            {
                if (page.OwnerId != owner.ModId) continue;
                if (!string.Equals(page.Title, title, StringComparison.Ordinal)) continue;
                if (!page.Available) continue;
                target = page;
                break;
            }
            if (target == null) return false;

            SettingsMenu.Open();
            SettingsMenu.SelectPage(target);
            return SettingsMenu.IsOpen;
        }

        /// <summary>
        /// Open the menu on one of the library's built-in tabs, identified by key - for example
        /// <see cref="PluginsTab"/> or "config". For a hotkey that means "open the Plugins tab"
        /// rather than merely "open the menu".
        /// </summary>
        public static bool ShowBuiltIn(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;

            if (!SettingsMenu.IsOpen) SettingsMenu.Open();
            SettingsMenu.SelectBuiltIn(key);
            return SettingsMenu.IsOpen;
        }

        /// <summary>
        /// Rebuild the side strip and redraw the current page. A mod calls this after changing
        /// whether its pages are available, or when it has new things to show.
        /// </summary>
        public static void Refresh()
        {
            // Refresh rebuilds the strip and then selects a tab, which selects again. Guard against a
            // page whose build calls Refresh turning that into unbounded recursion.
            if (_refreshing) return;
            _refreshing = true;

            try
            {
                SettingsMenu.Rebuild();
            }
            finally
            {
                _refreshing = false;
            }
        }

        // ---------------------------------------------------------------- startup

        /// <summary>
        /// Register the tabs the library draws itself and hook the menu's entry points into the
        /// game's menus. Called once during the library's startup, before any mod has finished
        /// loading.
        /// </summary>
        internal static void Start()
        {
            DefaultPages.Register();

            // The MODS buttons on the title menu and beside PRESS KIT on the settings page are
            // attached before any mod loads, so the game puts them up whether or not any mod
            // wants one.
            NativeUi.Install();
        }

        // ---------------------------------------------------------------- internals

        // ---- shared page model, consumed by the menu screen

        /// <summary>
        /// Every tab currently showing, in strip order: library built-ins first, then mod pages.
        /// Shared by the menu screen, which is the whole point of the shared model - a page
        /// registered at runtime has to appear there at once.
        /// </summary>
        internal static TabEntry[] ListTabs()
        {
            var tabs = new List<TabEntry>();

            foreach (var builtIn in OrderedBuiltIn())
            {
                if (!IsShowing(builtIn)) continue;
                tabs.Add(new TabEntry { BuiltInKey = builtIn.Key, Title = builtIn.Title });
            }

            foreach (var page in OrderedPages())
            {
                if (!page.Available) continue;
                tabs.Add(new TabEntry { Page = page, Title = page.Title });
            }

            return tabs.ToArray();
        }

        /// <summary>
        /// Render one tab's contents into an arbitrary host. Same contract whichever surface calls:
        /// a mod's build callback runs, and if it throws the host shows the error instead of the
        /// throw reaching the game's UI loop.
        /// </summary>
        internal static void RenderTab(VisualElement host, string builtInKey, MenuPage page)
        {
            if (host == null) return;

            // Each selection is a fresh render; a page that expects to keep state across selections
            // is one that will show stale content from the last visit.
            host.Clear();

            try
            {
                if (page != null)
                {
                    page.Build?.Invoke(host);
                }
                else if (builtInKey != null)
                {
                    foreach (var builtIn in OrderedBuiltIn())
                    {
                        if (builtIn.Key != builtInKey) continue;
                        builtIn.Build?.Invoke(host);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                UiLog.Error("The '" + (page != null ? page.ToString() : builtInKey) +
                            "' tab threw while building: " + ex);
                Widgets.Error(host, "This tab failed to draw: " + ex.Message);
            }

            host.MarkDirtyRepaint();
        }

        private static bool IsShowing(BuiltInPage page)
        {
            if (page.Available == null) return true;

            try
            {
                return page.Available();
            }
            catch (Exception)
            {
                // A tab that cannot say whether it is available is better hidden than crashing the
                // whole menu on every refresh.
                return false;
            }
        }

        /// <summary>Built-in tabs keep registration order, which is how "Mods" ends up first.</summary>
        private static List<BuiltInPage> OrderedBuiltIn()
        {
            lock (BuiltIn) { return new List<BuiltInPage>(BuiltIn); }
        }

        private static List<MenuPage> OrderedPages()
        {
            lock (Pages)
            {
                var copy = new List<MenuPage>(Pages);
                copy.Sort(delegate (MenuPage a, MenuPage b)
                {
                    if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                    return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
                });
                return copy;
            }
        }
    }
}
