<#
    Finding a Scam With Your Friends install, for this repository's build script and for every mod that
    takes it as a submodule.

    Dot-source this and call Resolve-GameDir:

        . (Join-Path $PSScriptRoot 'vendor/ScamWYF.Modding.Core/GameDir.ps1')
        $game = Resolve-GameDir -Explicit $GameDir -Require Managed

    It is a separate file rather than a function inside build.ps1 because a mod's build.ps1 needs the
    answer before it can call the library's, and the alternative is the same Steam search copied into
    every mod - which is how two copies quietly stop agreeing about where the game is.

    Resolution order: an explicit path, then SWYG_GAME_DIR, then every Steam library.

    The files are searched across every library, and only the full release counts - a leftover Playtest
    install is ignored. The two live under different folder names, so matching the release by name, and
    not by whichever library Steam happens to list first, is what keeps the old Playtest from being
    picked when both are present.

    SWYG_GAME_DIR is the one a build agent wants: it has no Steam, so nothing else would resolve. The
    game is not redistributable, so an agent has to be given the Managed folder and BepInEx/core by
    whatever it is already set up to use - a cached artifact, a mounted copy, or a self-hosted runner
    where the game is installed.
#>

<#
    Every Steam library's 'common' folder, default install first, then whatever libraryfolders.vdf
    names. The game is installed to a library, not necessarily the one Steam itself lives in - it can
    be on D: while Steam is on C: - so reading the VDF is what makes this work on a machine whose
    libraries are somewhere unexpected, without hardcoding one drive letter.

    Both platforms' roots are tried. Which ones exist is decided by Test-Path rather than by a branch
    on the OS, and the child segments use forward slashes because .NET normalises those on Windows -
    one script, no separator variable.
#>
function Get-SteamGameRoots {
    $roots = @()

    # Windows: the three fixed install locations, when the environment actually has them. An unset
    # variable leaves the string starting with '/Steam', which is filtered rather than joined. On
    # Linux it also has to be filtered here: Join-Path cannot form a 'C:\...' path there at all.
    foreach ($steam in @("${env:ProgramFiles(x86)}/Steam", "${env:ProgramFiles}/Steam",
                         "${env:LOCALAPPDATA}/Steam")) {
        if ($steam -and -not $steam.StartsWith('/Steam')) { $roots += $steam }
    }

    # Linux and macOS. The same set the setup tool reads: Steam's own compatibility symlinks, the XDG
    # location, Flatpak and Snap.
    if ($env:HOME) {
        $roots += @(
            "$env:HOME/.steam/steam"
            "$env:HOME/.steam/root"
            "$env:HOME/.local/share/Steam"
            "$env:HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
            "$env:HOME/snap/steam/common/.local/share/Steam"
            "$env:HOME/Library/Application Support/Steam"
        )
    }
    if ($env:XDG_DATA_HOME) { $roots += "$env:XDG_DATA_HOME/Steam" }

    $common = @()
    foreach ($steam in ($roots | Select-Object -Unique)) {
        if (-not (Test-Path $steam)) { continue }
        $common += (Join-Path $steam 'steamapps/common')

        $vdf = Join-Path $steam 'steamapps/libraryfolders.vdf'
        if (-not (Test-Path $vdf)) { continue }
        try {
            $text = Get-Content $vdf -Raw
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $library = $m.Groups[1].Value -replace '\\\\', '\'
                $common += (Join-Path $library 'steamapps/common')
            }
        } catch {
            # A VDF that cannot be read is not worth failing over; the fixed roots are still tried.
        }
    }

    # Last resorts for a machine where nothing above resolved. Windows-only on purpose: a 'C:\...' path
    # is not a location on Linux, and PowerShell there cannot even form one with Join-Path - it reads
    # 'C' as a drive that does not exist.
    $onWindows = if ($null -ne $IsWindows) { $IsWindows } else { $env:OS -eq 'Windows_NT' }
    if ($onWindows) {
        $common += @(
            'C:\Program Files (x86)\Steam\steamapps\common'
            'D:\SteamLibrary\steamapps\common'
        )
    }

    return ($common | Where-Object { $_ } | Select-Object -Unique)
}

<#
    The game install to build against: an explicit path, then SWYG_GAME_DIR, then every Steam library.

    -Require says what the caller needs to find there, because the three callers do not need the same
    thing - the library needs Managed and BepInEx/core, a mod that only reads the game's own assemblies
    needs Managed, and one that only needs the loader's Copy of Cecil needs BepInEx/core. A candidate
    that has the game but not the piece asked for is skipped rather than returned and failing later.
#>
function Resolve-GameDir {
    param(
        [string]$Explicit,
        [ValidateSet('Managed', 'BepInEx', 'Both')][string]$Require = 'Both'
    )

    $candidates = @()
    if ($Explicit) { $candidates += $Explicit }
    if ($env:SWYG_GAME_DIR) { $candidates += $env:SWYG_GAME_DIR }

    foreach ($root in (Get-SteamGameRoots)) {
        $candidates += (Join-Path $root 'Scam With Your Friends')
    }

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }

        $managedOk = Test-Path (Join-Path $candidate 'Scam With Your Friends_Data/Managed/mscorlib.dll')
        $bepOk     = Test-Path (Join-Path $candidate 'BepInEx/core/BepInEx.dll')

        $ok = switch ($Require) {
            'Managed' { $managedOk }
            'BepInEx' { $bepOk }
            default   { $managedOk -and $bepOk }
        }

        if ($ok) { return $candidate }
    }

    $needs = switch ($Require) {
        'Managed' { "'Scam With Your Friends_Data/Managed'" }
        'BepInEx' { 'BepInEx/core' }
        default   { "'Scam With Your Friends_Data/Managed' and BepInEx/core" }
    }

    $hints = ($candidates | Where-Object { $_ } | Select-Object -Unique) -join "`n  "
    throw @"
Could not find the game install. It needs $needs.
Pass -GameDir, or set SWYG_GAME_DIR.

Looked in:
  $hints
"@
}
