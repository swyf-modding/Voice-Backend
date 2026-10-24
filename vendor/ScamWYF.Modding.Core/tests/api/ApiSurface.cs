using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Content;
using ScamWYF.Modding.Core.Ui;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamWYF.Modding.Core.Tests
{
    /// <summary>
    /// Compile-time and shape checks for the library's public surface.
    /// </summary>
    /// <remarks>
    /// This file is not part of the shipped library. It exists so that the API a mod is expected to use
    /// is written out at least once, in a form that has to compile against the real game assemblies.
    ///
    /// That matters more than it looks. The build compiles with -nostdlib+ against the game's own
    /// mscorlib, so anything managed stripping removed is a compile error here rather than a
    /// MissingMethodException in somebody's session. Every UI Toolkit call, every BepInEx config call and
    /// every Unity type touched below has been checked against the shipped build.
    ///
    /// Build it with:
    ///   vendor\ScamWYF.Modding.Core\build.ps1 -Project ScamWYF.Modding.Tests `
    ///       -Sources .\tests -OutDir .\bin\tests -NoCopy
    /// </remarks>
    internal static class ApiSurface
    {
        // ---- ScamMod: the three things a mod actually calls -------------------------

        internal static void ModBasics(ScamMod mod, Plugin_ plugin)
        {
            // Identity comes from [BepInPlugin]; a mod never restates it.
            var id = mod.ModId;
            var name = mod.DisplayName;
            var version = mod.ModVersion;
            var state = mod.State;
            var failure = mod.FailureReason;
            var log = mod.ModLog;

            // Config, with the in-game editor attached.
            var settings = mod.Settings;
            var editor = mod.ConfigEditor;
            var configFile = mod.Config;
            var watcher = mod.Watcher;
            var hasSettings = mod.HasSettings;
            var hasDefaultPage = mod.HasDefaultMenuPage;

            // Range-checked reads.
            var timeout = settings.Int(settings.Bind("S", "I", 5, 1, 60, "Seconds."), 1, 3600);
            var ratio = settings.Float(settings.Bind("S", "F", 0.5f, 0f, 1f, "Ratio."), 0f, 1f);
            var mode = settings.EnumValue(settings.Bind("S", "E", DayOfWeek.Friday, "Day."), DayOfWeek.Friday);
            var text = settings.Text(settings.Bind("S", "T", "x", "Text."), "fallback");
            var raw = settings.Raw(settings.Bind("S", "R", "x", "Raw."));
            var flag = settings.Bool(settings.Bind("S", "B", true, "Flag."), true);
            var path = settings.FilePath;

            // A declared range is what turns a setting into a slider in the editor.
            var ranged = settings.Bind("S", "N", 90, 1, 3600, "Timeout in seconds.");
            var key = settings.BindKey("Menu", "Key", UnityEngine.InputSystem.Key.F1, "Opens the menu.");
            var choice = settings.BindChoice("S", "Mode", DayOfWeek.Friday,
                new[] { DayOfWeek.Friday, DayOfWeek.Saturday }, "Which day.");

            // Hot reload. OnConfigReloaded is the override point.
            mod.WatchConfig();
            mod.ReloadConfig();
            var reloaded = watcher != null && watcher.ReloadCount > 0;
        }

        // ---- Menu, hotkeys, patches ------------------------------------------------

        internal static void SharedServices(ScamMod mod)
        {
            // One menu, one hotkey. A mod contributes a tab and nothing else.
            var page = ModMenu.AddPage(mod, "Status", BuildPage);
            page.Available = true;

            ModMenu.AddPage(mod, "Second", BuildPage, 5);
            ModMenu.Show(mod, "Status");
            ModMenu.Refresh();
            ModMenu.Open();
            ModMenu.Close();
            ModMenu.Toggle();
            var open = ModMenu.IsOpen;
            var pages = ModMenu.RegisteredPages;

            // A built-in page is keyed, so registering the same key twice replaces it. That is how the
            // mod handler takes over the Plugins tab the library reserves for it.
            ModMenu.SetBuiltInPage("mods", "Mods", BuildPage);
            ModMenu.SetBuiltInPage("plugins", "Plugins", BuildPage, () => true);

            // Hotkeys: one poller for the whole library, clashes reported not hidden.
            var binding = Hotkeys.Register(mod, UnityEngine.InputSystem.Key.F7, "Toggle", ModMenu.Toggle,
                UnityEngine.InputSystem.Key.LeftCtrl);
            Hotkeys.Rebind(binding, UnityEngine.InputSystem.Key.F8);
            var all = Hotkeys.Bindings;
            var mine = Hotkeys.ForOwner(mod.ModId);
            var conflicts = Hotkeys.ConflictReport();
            var menuBound = Hotkeys.MenuBound;

            // Patches, with collisions made visible.
            var patch = new HarmonyMethod(AccessTools.Method(typeof(ApiSurface), nameof(Prefix)));
            var ok = PatchCoordinator.TryPatch(mod, typeof(UnityEngine.MonoBehaviour), "Update",
                new[] { typeof(UnityEngine.MonoBehaviour) }, "count updates", patch);
            var records = PatchCoordinator.Installed;
            var minePatches = PatchCoordinator.ForOwner(mod.ModId);
            var clashing = PatchCoordinator.ConflictingMethods;

            // Registry: who else is here, and what did they collide on.
            var mods = ModRegistry.Mods;
            var report = ModRegistry.CollisionReport();
            var count = ModRegistry.Count;
            ScamMod found;
            var has = ModRegistry.TryGet(mod.ModId, out found);

            // What build is this, and does the thing a mod hooks still exist.
            var described = GameBuild.Describe();
            var unity = GameBuild.UnityVersion;
            var gameVersion = GameBuild.GameVersion;
            GameBuild.CheckUnityVersion(mod.ModLog, mod.ModId, "6000.3.10f1");

            MethodBase method;
            var resolved = GameBuild.TryResolveMethod(mod, typeof(UnityEngine.MonoBehaviour), "Update",
                new[] { typeof(UnityEngine.MonoBehaviour) }, out method);

            Type type;
            var gotType = GameBuild.TryResolveType(mod, "MainMenuConnectionStatus", out type);

            FieldInfo field;
            var gotField = GameBuild.TryResolveField(mod, type, "backendLabel", out field);
        }

        // ---- Windows, for overlays that have to stay up while the game is played ---

        internal static UiWindow Overlay(ScamMod mod)
        {
            var window = UiWindows.GetOrCreate(mod, "Overlay", 420f, 300f);
            if (window == null) return null;

            window.Visible = true;
            window.Focus();
            window.SetTitle("Overlay");
            window.ClearBody();

            var reason = window.UnavailableReason;
            var onClosed = window.OnClosed;
            window.OnClosed = delegate { };
            window.Close();

            UiWindows.RemoveOwner(mod.ModId);

            var overlay = Widgets.Overlay;
            var theme = UiTheme.Source;
            var themed = UiTheme.IsThemed;
            var panel = UiPanel.PanelSource;
            var unavailable = UiPanel.UnavailableReason;
            var typing = UiPanel.TextEntryFocused;
            return window;
        }

        private static void Prefix() { }

        // ---- Game workarounds: prove-then-skip guards for game-build defects ----

        internal static void WorkaroundSurface()
        {
            // Installed by the library itself; a mod only reads back what engaged.
            GameWorkarounds.EnsureHudHintBindingGuard();
            var suppressed = GameWorkarounds.HudHintBindingSuppressed;
        }

        // ---- Custom scams and apps: one registry every mod shares ----------------

        internal static void ContentSurface(ScamMod mod)
        {
            // Specs are plain data; validation is pure and never touches the game.
            var objective = new ScamObjectiveSpec
            {
                Id = "confirm",
                Text = "Confirm the date.",
                Prerequisites = new[] { "summons" },
                Final = true,
            };

            var scam = new ConversationScamSpec
            {
                Id = "moar-jury-duty",
                ProductId = "moar-scam-jury",
                AppId = "scam-moar-jury",
                Title = "Jury Duty",
                Reward = 475,
                Objectives = new[]
                {
                    new ScamObjectiveSpec { Id = "summons", Text = "Get them to acknowledge the summons." },
                    objective,
                },
            };
            var scamProblem = ContentValidation.CheckConversationScam(scam);

            var scamType = new ScamTypeSpec
            {
                Id = "moar_tax",
                DisplayName = "Tax Rebate",
                AppId = "moar-tax-office",
                DisplayOrder = 20,
                RelevantSentinels = new[] { "FICTIONAL_MOAR_TAX_ID" },
                AppCompletionSource = "moar_tax_submit",
                CallerPrompt = "Share [[FICTIONAL_MOAR_TAX_ID]] when persuaded.",
                TestOpeningPlayerLine = "I am calling about your tax rebate.",
            };
            var typeProblem = ContentValidation.CheckScamType(scamType);

            // Factories cover the two scam app shapes; anything else is a plain spec.
            var conversationApp = OsAppSpec.ConversationScamApp("scam-moar-jury", "Jury Duty", "moar-scam-jury");
            var formApp = OsAppSpec.FormScamApp("moar-tax-office", "Tax Office", "moar-scam-tax",
                "moar_tax_submit", "Verify",
                new[] { new OsFormFieldSpec { Id = "tax-id", Label = "Tax ID", ItemId = "FICTIONAL_MOAR_TAX_ID" } });
            var appProblem = ContentValidation.CheckApp(formApp);

            var product = StoreProductSpec.FreeScam("moar-scam-jury", "scam-moar-jury",
                "moar-jury-duty", "Jury Duty", "Serve justice.", 3);
            var productProblem = ContentValidation.CheckProduct(product);

            var sentinel = SentinelSpec.Digits("FICTIONAL_MOAR_TAX_ID", "Tax ID", 250,
                "moar_tax_submit", new[] { 2, 2 }, "-");
            var sentinelProblem = ContentValidation.CheckSentinel(sentinel);

            // Registration merges into the game's own catalogs. The first mod's call installs
            // the patches; every mod after that just adds entries. Duplicate ids fail with
            // both owners named instead of overwriting.
            var scamOk = ContentRegistry.RegisterConversationScam(mod, scam);
            var typeOk = ContentRegistry.RegisterScamType(mod, scamType);
            var appOk = ContentRegistry.RegisterApp(mod, conversationApp);
            var rawAppOk = ContentRegistry.RegisterAppJson(mod, "moar-extra", "{\"id\":\"moar-extra\"}");
            var productOk = ContentRegistry.RegisterProduct(mod, product);
            var rawProductOk = ContentRegistry.RegisterProductJson(mod, "moar-extra-product", "{\"id\":\"x\"}");
            var sentinelOk = ContentRegistry.RegisterSentinel(mod, sentinel);

            var mine = ContentRegistry.ForOwner(mod.ModId);
            var allScams = ContentRegistry.RegisteredConversationScams;
            var allTypes = ContentRegistry.RegisteredScamTypes;
            var allApps = ContentRegistry.RegisteredApps;
            var allProducts = ContentRegistry.RegisteredProducts;
            var allSentinels = ContentRegistry.RegisteredSentinels;
            var conflicts = ContentRegistry.ConflictReport();
            ContentRegistry.UnregisterOwner(mod.ModId);
        }

        // ---- Widgets a page can be built from --------------------------------------

        private static void BuildPage(VisualElement page)
        {
            var scroll = Widgets.Scroll(page);

            Widgets.Heading(scroll, "Heading");
            Widgets.Paragraph(scroll, "Some explanation.");
            Widgets.Label(scroll, "A label", true);

            Widgets.FieldRow(scroll, "Endpoint", "http://127.0.0.1:11434/v1");
            Widgets.Note(scroll, "Something to note.", true, false);
            Widgets.Note(scroll, "Something wrong.", false, true);

            var row = Widgets.Row(scroll);
            Widgets.Button(row, "Click", delegate { });
            Widgets.PrimaryButton(row, "Apply", delegate { });
            Widgets.DescribedButton(row, "Why", "Explains itself", delegate { });
            Widgets.TabButton(row, "Tab", delegate { });
            Widgets.SetSelected(row, true);
            Widgets.Filler(row);
            Widgets.Spacer(scroll, 6f);

            var wrap = Widgets.WrapRow(scroll);
            Widgets.Column(wrap);

            Widgets.Toggle(scroll, "Enabled", true, delegate { });
            Widgets.TextField(scroll, "Name", "value", delegate { }, false);
            Widgets.TextField(scroll, "Notes", "value", delegate { }, true);
            Widgets.SecretField(scroll, "ApiKey", "", delegate { });
            Widgets.IntSlider(scroll, "Count", 3, 0, 10, delegate { });

            var choices = new List<string> { "One", "Two" };
            Widgets.Dropdown(scroll, "Pick", choices, 0, delegate { });

            var fold = Widgets.Foldout(scroll, "Advanced", false);
            fold.Add(new Label("hidden until opened"));

            // Colours, for a mod drawing something the widgets do not cover.
            var colour = UiTheme.Accent;
            var muted = UiTheme.TextMuted;
            var rowHeight = UiTheme.Row;
            var lighter = UiTheme.Lighten(UiTheme.Button, 0.1f);
            var darker = UiTheme.Darken(UiTheme.Button, 0.1f);
            var blended = UiTheme.Shade(UiTheme.Text, UiTheme.TextMuted);
        }

        /// <summary>Stand-in for a real plugin type, so the surface above has a host to hang off.</summary>
        internal sealed class Plugin_ : ScamMod
        {
            protected override void OnModLoad() { }
        }
    }
}