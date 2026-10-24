<#
    Starts the companion server for ScamWYF.VoiceBackend.

        ./start.ps1                 # Parakeet + Chatterbox
        ./start.ps1 -NoTts          # Parakeet only (no torch, no Chatterbox models)
        ./start.ps1 -Port 8011

    `uv` is the only prerequisite; it downloads Python 3.11 and the dependencies on first run.
#>
[CmdletBinding()]
param(
    [switch]$NoTts,
    [int]$Port = 8010,
    [string]$HostName = "127.0.0.1"
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    throw "uv is not on PATH. Install it from https://docs.astral.sh/uv/ or run: winget install astral-sh.uv"
}

$env:VOICE_PORT = "$Port"
$env:VOICE_HOST = $HostName
if ($NoTts) { $env:VOICE_TTS = "0" }

Push-Location $PSScriptRoot
try {
    Write-Host "Starting the voice backend on http://${HostName}:${Port}" -ForegroundColor Green
    Write-Host "The first request downloads the selected model; /health shows what is loaded." -ForegroundColor DarkGray
    if ($NoTts) {
        uv run python app.py
    } else {
        uv run --extra tts python app.py
    }
}
finally {
    Pop-Location
}
