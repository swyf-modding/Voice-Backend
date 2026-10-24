<div align="center">

# ScamWYF.Modding.Core

![license](https://img.shields.io/badge/license-MIT-blue)
![game build](https://img.shields.io/badge/game-0.1.71--swyf-blue)
![Unity](https://img.shields.io/badge/Unity-6000.3.10f1-blue)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-blue)

*Built with:*

![C#](https://img.shields.io/badge/C%23-512BD4?style=for-the-badge&logo=csharp&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Harmony](https://img.shields.io/badge/Harmony-51796aa?style=for-the-badge)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-14172c?style=for-the-badge)

> ⚠️ Linux build support (`build.ps1` under `pwsh`, for the Setup/Proton flow) is **very experimental**;
> it is validated on one Linux machine only. Windows remains the reference platform.

</div>

---

## Table of Contents

- [Overview](#overview)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Installation](#installation)
  - [Developer Setup](#developer-setup)
  - [Usage](#usage)
  - [Testing](#testing)
- [Compatibility](#compatibility)
- [What the Library Provides](#what-the-library-provides)
- [Project Structure](#project-structure)
- [Version Stamping](#version-stamping)
- [Continuous Builds](#continuous-builds)
- [Security](#security)
- [License](#license)
- [Related Projects](#related-projects)

---

## Overview

**ScamWYF.Modding.Core** is an unofficial, community-built C# modding library for **Scam With Your
Friends**. It provides the things every BepInEx mod for this game ends up writing anyway: a base class,
a Harmony coordinator that reports collisions, one keyboard poller, range-checked settings, config hot
reload, and one in-game menu that all mods share.

**Key Features:**

- **One shared menu.** A single window on a single hotkey, with a tab per mod, drawn with UI Toolkit so
  it inherits the base game's own theme, fonts and scaling rather than imitating them
- **An in-game config editor.** A form generated from your `.cfg` file itself — bounded numbers become
  sliders, enums become dropdowns, and the descriptions you already wrote become the help text
- **Config hot reload.** A hand-edited `.cfg` takes effect while the game is running, including values
  that decide whether your patch is installed at all
- **Collisions made visible.** Two mods patching the same method, or binding the same key, is reported
  with both owners rather than being a mystery
- **No redistributed game DLLs, decompiled game source, telemetry, or credential collection**

> [!IMPORTANT]
> The library builds to its own dll and mods **reference** it rather than embedding it. This is
> deliberate. The library owns singletons — the hotkey table, the menu, the panel — so when it was
> compiled into each mod, every mod had a private copy, and two mods meant two F1 bindings and two
> windows opening on top of each other.

> [!NOTE]
> BepInEx reads the config while the game runs, so the library **polls** for changes rather than using
> `FileSystemWatcher`. Managed stripping left only `Path` and `Created` on that type in this game
> build; polling notices a change within about half a second either way.

This project is not affiliated with or endorsed by the developers or publisher of Scam With Your Friends.

---

## Getting Started

### Prerequisites

- A legally installed copy of **Scam With Your Friends**, with the build listed under
  [Compatibility](#compatibility)
- [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx) installed, along with the corlib override —
  [Setup](https://github.com/swyf-modding/Setup) installs both, because this game ships a stripped `mscorlib` that BepInEx
  cannot start without
- The .NET SDK, for the behaviour tests only. Building does not need it
- **PowerShell.** Windows PowerShell 5.1 or PowerShell 7 on Windows. On Linux and macOS it is
  [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell)
  (`pwsh`), since there is no Windows PowerShell there; the scripts are the same either way

### Installation

1. Install BepInEx and the corlib override with [Setup](https://github.com/swyf-modding/Setup).
2. Add this repo as a submodule, so the library and the mods are pinned to the same commit:

   ```powershell
   git submodule add https://github.com/swyf-modding/mod-lib.git vendor/ScamWYF.Modding.Core
   ```

3. Build. `build.ps1` builds the library first, then your mod against the dll it just produced:

   ```powershell
   & "./vendor/ScamWYF.Modding.Core/build.ps1" -Project YourMod -Sources "./src"
   ```

4. Copy `YourMod.dll` into `BepInEx/plugins`.

The library is **not** installed into `plugins`. `BepInEx/core` is where it belongs, and `build.ps1`
puts it there — BepInEx loads `core` first so every mod can resolve it, and anything in `plugins` would
be loaded as though it were a plugin.

### Developer Setup

```powershell
git clone --recurse-submodules https://github.com/swyf-modding/mod-lib.git
cd mod-lib
./build.ps1 -LibraryOnly          # does it compile
./build.ps1 -LibraryOnly -Test    # does it compile, and do the tests pass
```

On Linux and macOS run those with `pwsh` — `pwsh ./build.ps1 -LibraryOnly`. There is no Windows
PowerShell there. Everything else is the same, and the game is found by itself: Steam's libraries under
`~/.steam` and `~/.local/share`, or whatever `-GameDir` or `SWYG_GAME_DIR` says.

The library's own `build.ps1` needs the game's assemblies to compile against. It finds the install
itself; pass `-GameDir`, or set `SWYG_GAME_DIR`, if it cannot.

Start a mod from [`template/Plugin.cs`](template/Plugin.cs): rename the namespace and the guid, and you
have a working mod that already has a menu tab, settings and hot reload.

### Usage

```csharp
using BepInEx;
using ScamWYF.Modding.Core;

// PluginBuildInfo.Version is generated into obj/ by build.ps1 from your repo's git tag. See
// "Version Stamping" below - do not replace it with a literal.
[BepInPlugin(PluginGuid, "My Mod", PluginBuildInfo.Version)]
public sealed class Plugin : ScamMod
{
    private const string PluginGuid = "com.example.mymod";
    private const string BuiltAgainst = "6000.3.10f1";

    protected override void OnModLoad()
    {
        GameBuild.CheckUnityVersion(ModLog, ModId, BuiltAgainst);

        // Range-checked, and the description becomes help text on the generated settings form.
        var greetingLength = Settings.Bind("General", "GreetingLength", 24, 1, 200,
            "How long the greeting is.");

        // Pick up an edit to the .cfg while the game is running.
        WatchConfig();

        PatchCoordinator.TryPatch(this, typeof(SomeType), "SomeMethod", new[] { typeof(int) },
            "what this does", new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(Prefix))));
    }

    protected override void OnConfigReloaded()
    {
        // The file was re-read, so re-derive anything clamped rather than trusting the old values.
        ModLog.LogInfo("Greeting length is now " + Settings.Int(greetingLength, 1, 200));
    }
}
```

Contributing a tab to the shared menu, if you want one beyond the automatic default:

```csharp
ModMenu.AddPage(this, "Status", page =>
{
    // The page host is already a scroll view. Do not add another: a nested one takes the wheel
    // first, which reads as a menu that has stopped responding.
    Widgets.FieldRow(page, "Endpoint", Config.BaseUrl.Value);

    var fold = Widgets.Section(page, "All settings", false);
    ConfigEditor.Build(Widgets.SectionBody(fold) ?? page);
});
```

The important part is what is *not* there: no `Awake`, no `Update`, no `OnGUI`, no window. `ScamMod`
seals the Unity callbacks and handles them once for every mod, so two mods cannot fight over them.

### Testing

```powershell
./build.ps1 -LibraryOnly -Test
```

`-Test` does two things:

1. **Compiles [`tests/api`](tests/api)** together with the library against the game's own assemblies.
   That file is the public surface written out once, so every UI Toolkit and BepInEx call a mod is
   expected to make is checked against what actually shipped.
2. **Runs the behaviour tests**, each compiling the real source file it covers rather than a copy:

   | Suite | Covers |
   |---|---|
   | [`tests/watcher`](tests/watcher) | config polling, waiting for a write to settle, surviving a file that does not parse |
   | [`tests/window`](tests/window) | window size and position clamping: minimums, screen limits, idempotence |

The behaviour tests need the .NET SDK and are skipped with a warning if `dotnet` is not on `PATH`. They
use nothing but the BCL, so they run on Linux too — `pwsh ./build.ps1 -LibraryOnly -Test` — which is what
the CI workflow checks on every push.

No game DLL belongs in this repository or in a GitHub release.

---

## Compatibility

| Component | Verified Version |
|---|---|
| Scam With Your Friends | `0.1.71-swyf` |
| Unity | `6000.3.10f1` |
| BepInEx | `5.4.23.5`, Mono preloader |
| Doorstop | `4.5.0` |
| C# language level | `7.3` |
| Backend | Mono, with managed stripping **on** |
| Platform | Windows x64, including under Proton on Linux |

Other game builds are untested. `GameBuild.CheckUnityVersion` reports a mismatch as a warning naming
both versions rather than assuming support.

The build compiles with `-nostdlib+` against **the game's own `mscorlib.dll`**, not a reference
assembly. The compiler therefore sees exactly the API surface Unity shipped, so a call that managed
stripping removed is a **build error** rather than a `MissingMethodException` in somebody's session.

---

## What the Library Provides

### `ScamMod` — the base class

Identity read from `[BepInPlugin]`, duplicate guids rejected, load failures logged and made inert
instead of taking the session down, and everything registered with the shared services undone on unload.

### `ModMenu` — one window, one hotkey, every mod

Press **F1**. One window with a tab strip: a tab per mod, plus **Mods** (what is loaded, what is
colliding) and **About** (this session, where the files are). A keyed tab mechanism lets the mod handler
take over **Plugins**. Rebuilding the strip keeps the current tab, so editing a setting does not throw
the user back to page one.

### `UiPanel` and `UiTheme` — the base game's own UI, not a lookalike

The shared panel clones the game's own `PanelSettings` and reads its live theme, so a mod window
inherits the game's theme, fonts and scaling. Windows are movable and resizable, and remember where
they were left in `scamwyf-modding.cfg`. The clamping rules that keep a window on screen and above a
minimum size live in [`src/ui/WindowGeometry.cs`](src/ui/WindowGeometry.cs) and are covered by tests.

### `ConfigEditor` — a settings form from your `.cfg`

Reads the `ConfigFile` you already have and draws it: booleans as toggles, bounded numbers as sliders,
enums as dropdowns, secrets as masked fields. Writes go straight to disk. Sections become collapsible
groups, because a config with fifty settings is unreadable as one flat list.

### `ConfigWatcher` — hot reload

Polls for mtime and length changes, debounces, and re-reads. Survives a file that does not parse by
reporting it rather than throwing out of the tick.

### `PatchCoordinator` — Harmony, with collisions made visible

One Harmony instance per mod. Target resolution that reports a game update rather than throwing, and
same-target patches reported with both owners.

### `Hotkeys` — one keyboard poller

One poller for all mods, duplicate bindings reported instead of silently misfiring, rebindable in game,
and suppressed while a text field has focus.

### `ContentRegistry` — custom scams and apps, without conflicts

Every mod registers conversation scams, scam types, OS apps, store products and sentinels through one
registry. Entries are validated against the game's own catalog rules and merged into the game's catalogs,
so custom content is listed, sold, prompted and paid out like the game's own. Duplicate ids across mods
are rejected with both owners named rather than overwriting.

### `ModSettings` — range-checked config reads

These `.cfg` files get hand-edited, so a value out of range is clamped and reported rather than passed
on to a request path that will not survive it.

### `GameWorkarounds` — prove-then-skip guards for game-build defects

Defects in the shipped build that would otherwise throw every frame, such as the HUD menu-hint bind
referencing an InputSystem type the build cannot resolve. Each guard lets the method fail once, arms
a skip only on that specific failure, and does nothing on a build without the defect.

---

## Project Structure

```text
src/
|-- ScamMod.cs             Base class: identity, lifecycle, sealed Unity callbacks
|-- LibraryRuntime.cs      Startup, menu hotkey binding, single-copy warning
|-- ModRegistry.cs         Which mods are loaded
|-- ModSettings.cs         Range-checked config reads
|-- Hotkeys.cs             One keyboard poller for all mods
|-- PatchCoordinator.cs    Harmony, with collisions reported
|-- GameBuild.cs           What game and Unity build is this
|-- config/
|   |-- ConfigWatcher.cs   Config hot reload
|   `-- ConfigEditor.cs    Settings form generated from a .cfg
|-- content/
|   |-- ContentSpecs.cs      Scam, app, product and sentinel shapes mods register
|   |-- ContentValidation.cs Catalog rules, dependency-free and covered by tests
|   |-- ContentRegistry.cs   Multi-mod registration with duplicate ids rejected
|   `-- ContentPatches.cs    Merges registrations into the game's own catalogs
`-- ui/
    |-- UiPanel.cs         The shared UIDocument
    |-- UiTheme.cs         Colours read from the game's live theme
    |-- Widgets.cs         Themed controls
    |-- ModMenu.cs         The one menu
    |-- UiWindow.cs        Window frame, geometry persistence
    |-- UiWindows.cs       Window bookkeeping per owner
    |-- ResizeGrip.cs      Drag to resize
    |-- WindowGeometry.cs  The clamping arithmetic, deliberately dependency-free
    `-- WindowPlacement.cs Saved size and position
template/Plugin.cs         A working mod to copy
Version.ps1                Version stamping, shared with every mod built through this library
GameDir.ps1                Finding the game install, shared with every mod built through this library
tools/                     The [BepInPlugin] version check, compiled and run after every build
tests/                     API surface compile check, and behaviour tests
```

---

## Version Stamping

`Version.ps1` is dot-sourced by `build.ps1` and by each mod's own `build.ps1`. It reads the nearest git
tag and stamps the result into the assembly, so a released dll reports the tag it was cut from without
anyone editing a number in a source file.

| | |
|---|---|
| `AssemblyVersion`, `AssemblyFileVersion` | `1.2.3` — numeric, because the CLR rejects a prerelease here |
| `AssemblyInformationalVersion` | `1.2.3+g0a1b2c3` — what Explorer and Programs and Features show |
| `PluginBuildInfo.Version` | the same string, as a `const` a mod can pass to `[BepInPlugin]` |

A commit past the tag adds `+3.g0a1b2c3`, a prerelease tag keeps its name (`v1.2.3-rc1` →
`1.2.3-rc1+g0a1b2c3`), an uncommitted tree is marked `.dirty` and warned about, and an untagged tree
reports `0.0.0+untagged.g0a1b2c3` — which says so rather than claiming to be 1.0.0. The generated
`obj/BuildInfo.g.cs` is not committed.

**A mod resolves its own version and passes it down.** This file lives in a submodule, so resolving it
here would report *mod-lib's* tags rather than the mod's — the two are released independently and are
not always at the same commit. A mod therefore calls `Resolve-BuildVersion -Path $PSScriptRoot` and
hands the result to `build.ps1 -BuildVersion`.

Two fallbacks, so a build works outside a checkout: no `git` or no `.git\` warns and stamps `0.0.0`,
and `-Version` sets it outright for a source archive.

The reason this exists rather than a version literal in each mod: Switchboard's Mods tab and this
library's Plugins tab both read the version back out of `[BepInPlugin]` with Cecil and show it to the
player. A hardcoded `"1.0.0"` is right on the first release and silently wrong on every one after it.

---

## Continuous Builds

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs the behaviour tests on every push and pull
request, on a hosted runner, and checks the build script itself. That is all it runs, and the limit is
deliberate.

The build compiles with `-nostdlib+` against the game's own `mscorlib`, and those are not ours to
redistribute, so a hosted runner cannot do it. It could be given them as an artefact or a secret, but a
self-hosted runner is simpler than either and there is no reason to pretend otherwise. So:

| | Where | What |
|---|---|---|
| behaviour tests | CI, hosted | pure BCL, no game — logic a compile cannot catch |
| the build script | CI, hosted | every `.ps1` parses under `pwsh`, and `GameDir.ps1` resolves a fabricated install |
| the build | your machine | the real compile, and the API surface check |

The API surface check is the tier that matters most here, because it is the one a hosted runner cannot
do and the one that catches a stripped API the build calls. It runs before every release:

```powershell
./build.ps1 -LibraryOnly -Test -NoCopy
```

The machine that has the game can be Linux: `pwsh ./build.ps1 -LibraryOnly -Test -NoCopy`. The compile
reads the game's Windows assemblies and BepInEx's as metadata references, which is all it needs from
them, and a Proton install is found under the Steam libraries in `~/.steam` and `~/.local/share`.

There was a self-hosted CI job for this once. It is gone because a self-hosted job with no runner
registered does not fail — it queues, and sits there until GitHub cancels it 24 hours later, so every
push to `main` left a stuck red job and the "Run workflow" button was a trap that did the same on demand.

Releases are published by hand; see [RELEASING.md](RELEASING.md).

---

## Security

Please do not publish suspected vulnerabilities, credentials, authentication tickets, private game data,
or sensitive logs in a public issue — including the contents of `BepInEx/LogOutput.log`, which contains
the API keys mods configure.

The project deliberately does not redistribute proprietary assemblies: the game's own assemblies stay in
your install, and `vendor\` carries only what is ours to give away. Because this is game-mod software,
install only releases you trust.

---

## License

MIT — Copyright © 2026 Ras_rap. See [LICENSE](LICENSE).

The mods that use this library are separate works under their own licences. Nothing here grants any
right to the game's own assemblies, which are not redistributed.

---

## Related Projects

| Project | What it is |
|---|---|
| [Setup](https://github.com/swyf-modding/Setup) | Installs BepInEx, the corlib override, and the vtable patches this game needs |
| [Switchboard](https://github.com/swyf-modding/Switchboard) | Installs, launches, and manages mods from outside the game |
| [Mod-Handler](https://github.com/swyf-modding/Mod-Handler) | The in-game **Plugins** tab — turn mods off without leaving a session |
| [AI-Backend](https://github.com/swyf-modding/AI-Backend) | Routes the game's AI calls to your own LLM |
