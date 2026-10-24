<#
    Builds the self-contained companion server.

        ./build-server.ps1              # dist/VoiceBackend.Server/VoiceBackend.Server.exe
        ./build-server.ps1 -Install     # also copy it next to the mod
        ./build-server.ps1 -Clean       # remove build/ and dist/ first

    The result needs no Python on the player's machine. The model weights are still downloaded on
    first use into the Hugging Face cache, not bundled here.
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Clean,
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    throw "uv is not on PATH. Install it from https://docs.astral.sh/uv/"
}

Push-Location $PSScriptRoot
try {
    if ($Clean) {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue build, dist
    }

    uv sync --extra tts
    uv pip install pyinstaller
    uv run pyinstaller --clean --noconfirm server.spec

    $exe = Join-Path $PSScriptRoot 'dist/VoiceBackend.Server/VoiceBackend.Server.exe'
    if (-not (Test-Path $exe)) { throw "PyInstaller did not produce $exe" }
    Write-Host "Built $exe" -ForegroundColor Green

    if ($Install) {
        . (Join-Path $PSScriptRoot '../vendor/ScamWYF.Modding.Core/GameDir.ps1')
        $GameDir = Resolve-GameDir -Explicit $GameDir -Require BepInEx
        $target = Join-Path $GameDir 'BepInEx/plugins/VoiceBackend'

        New-Item -ItemType Directory -Force -Path $target | Out-Null
        # /MIR would delete files a user put there; copy over the top instead.
        Copy-Item -Recurse -Force (Join-Path $PSScriptRoot 'dist/VoiceBackend.Server/*') $target
        Write-Host "Installed the server to $target" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
