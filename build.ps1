<#
    Builds ScamWYF.VoiceBackend and installs it into the game's BepInEx/plugins.

    The shared library's build script does the work; this file only says what this mod is and
    which extra references it needs.

        ./build.ps1
        ./build.ps1 -CscDll /path/to/roslyn/csc.dll
        ./build.ps1 -GameDir "/home/you/.steam/steam/steamapps/common/Scam With Your Friends"
        ./build.ps1 -NoCopy -Test

    -Test runs the library's tests as well: the behaviour tests and the API surface check.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$CscDll,
    # Stamp this version instead of reading the nearest tag.
    [string]$Version,
    [switch]$NoCopy,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'

$lib = Join-Path $PSScriptRoot 'vendor/ScamWYF.Modding.Core/build.ps1'
if (-not (Test-Path $lib)) {
    throw "The shared library is missing. Run: git submodule update --init --recursive"
}

# The library finds the game install itself; do the same here so the extra references below point
# at the same place.
. (Join-Path $PSScriptRoot 'vendor/ScamWYF.Modding.Core/GameDir.ps1')
$GameDir = Resolve-GameDir -Explicit $GameDir -Require Managed
$managed = Join-Path $GameDir 'Scam With Your Friends_Data/Managed'

# The version comes from this repository's tags, not the submodule's.
. (Join-Path $PSScriptRoot 'vendor/ScamWYF.Modding.Core/Version.ps1')

$buildArgs = @{
    Project = 'ScamWYF.VoiceBackend'
    Sources = @((Join-Path $PSScriptRoot 'src'))
    OutDir  = (Join-Path $PSScriptRoot 'bin')
    BuildVersion = (Resolve-BuildVersion -Path $PSScriptRoot -Explicit $Version)
    Refs    = @(
        (Join-Path $managed 'com.github.asus4.onnxruntime.dll')
        (Join-Path $managed 'UnityEngine.UnityWebRequestModule.dll')
        (Join-Path $managed 'UnityEngine.AudioModule.dll')
        (Join-Path $managed 'UnityEngine.UIElementsModule.dll')
        (Join-Path $managed 'Newtonsoft.Json.dll')
        (Join-Path $managed 'Assembly-CSharp.dll')
        (Join-Path $managed 'ScriptsAssDef.dll')
        (Join-Path $managed 'com.whisper.unity.dll')
        (Join-Path $managed 'PocketTTS.dll')
        (Join-Path $managed 'Kolkata.Speech.dll')
    )
}

if ($GameDir)   { $buildArgs.GameDir = $GameDir }
if ($CscDll)    { $buildArgs.CscDll = $CscDll }
if ($NoCopy)    { $buildArgs.NoCopy = $true }
if ($Test)      { $buildArgs.Test = $true }

& $lib @buildArgs

if (-not $NoCopy) {
    # The assets are part of the mod, not a download: the inno-kokoro tuner graph, its blend tables and
    # the English pronunciation lexicon. They live beside the plugin DLL, in the folder the release zip
    # already uses, so the mod finds them without knowing where the game is.
    $assets = Join-Path $PSScriptRoot 'assets'
    if (Test-Path $assets) {
        $target = Join-Path $GameDir 'BepInEx/plugins/VoiceBackend'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -Recurse -Force (Join-Path $assets '*') $target
        Write-Host ("Installed assets to " + $target) -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "Models download into BepInEx/plugins/VoiceBackend/models on first use;" -ForegroundColor Green
    Write-Host "Parakeet v2 is about 630 MB and Kokoro about 325 MB." -ForegroundColor Green
}
