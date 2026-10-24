<#
    Builds the self-contained companion server.

        ./build-server.ps1                  # detect the best backend for this machine
        ./build-server.ps1 -Backend cuda    # NVIDIA CUDA + CPU fallback
        ./build-server.ps1 -Backend rocm    # AMD ROCm (Linux)
        ./build-server.ps1 -Backend directml # Windows DirectML (ASR; Chatterbox stays CPU)
        ./build-server.ps1 -Backend cpu     # smallest
        ./build-server.ps1 -Install         # also copy it next to the mod

    The result needs no Python on the player's machine. Model weights are still downloaded on first
    use into the Hugging Face cache, not bundled here.

    The runtime picks the device from the mod's Mode setting:
        VOICE_DEVICE_MODE=auto|cpu|gpu
    A GPU build still runs on CPU when no GPU provider is available.
#>
[CmdletBinding()]
param(
    [ValidateSet("auto", "cpu", "cuda", "rocm", "directml")]
    [string]$Backend = "auto",
    [switch]$Install,
    [switch]$Clean,
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    throw "uv is not on PATH. Install it from https://docs.astral.sh/uv/"
}

function Resolve-Backend {
    if ($Backend -ne 'auto') { return $Backend }

    if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { return 'cuda' }
    if ((Get-Command rocminfo -ErrorAction SilentlyContinue) -or (Test-Path '/opt/rocm')) { return 'rocm' }
    return 'cpu'
}

function Uninstall-Package([string]$Name) {
    # uv writes "Uninstalled ..." to stderr; with $ErrorActionPreference=Stop that is a terminating
    # NativeCommandError even though the exit code is zero.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { uv pip uninstall $Name *> $null } catch { }
    finally { $ErrorActionPreference = $previous }
}

Push-Location $PSScriptRoot
try {
    $resolved = Resolve-Backend
    Write-Host "Server backend: $resolved" -ForegroundColor Cyan

    if ($Clean) {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue build, dist
    }

    # The base environment: Python 3.11, FastAPI, onnx-asr, torch, chatterbox.
    uv sync --extra tts
    uv pip install pyinstaller

    switch ($resolved) {
        'cuda' {
            # CUDA 12.4 wheels match the current PyTorch stable line and work on a CPU-only machine
            # too, so one build covers both. --reinstall is required: the lock's CPU wheel otherwise
            # satisfies the request and nothing is replaced.
            uv pip install --index-url https://download.pytorch.org/whl/cu124 --reinstall torch torchaudio
            Uninstall-Package onnxruntime
            uv pip install "onnxruntime-gpu[cuda,cudnn]>=1.20"
        }
        'rocm' {
            uv pip install --index-url https://download.pytorch.org/whl/rocm6.2 --reinstall torch torchaudio
            Uninstall-Package onnxruntime
            uv pip install onnxruntime-rocm
        }
        'directml' {
            # torch has no stable DirectML backend for Chatterbox; Parakeet uses DML, TTS stays CPU.
            Uninstall-Package onnxruntime
            uv pip install onnxruntime-directml
        }
        default {
            uv pip install onnxruntime
        }
    }

    # --no-sync is important: uv run would otherwise restore the lock file's CPU wheels over the
    # GPU packages just installed.
    uv run --no-sync pyinstaller --clean --noconfirm server.spec

    $exe = Join-Path $PSScriptRoot 'dist/VoiceBackend.Server/VoiceBackend.Server.exe'
    if (-not (Test-Path $exe)) { throw "PyInstaller did not produce $exe" }
    Write-Host "Built $exe ($resolved)" -ForegroundColor Green

    if ($Install) {
        . (Join-Path $PSScriptRoot '../vendor/ScamWYF.Modding.Core/GameDir.ps1')
        $GameDir = Resolve-GameDir -Explicit $GameDir -Require BepInEx
        $target = Join-Path $GameDir 'BepInEx/plugins/VoiceBackend'

        New-Item -ItemType Directory -Force -Path $target | Out-Null
        # Copy over the top; do not delete files the user may have put there.
        Copy-Item -Recurse -Force (Join-Path $PSScriptRoot 'dist/VoiceBackend.Server/*') $target
        Write-Host "Installed the $resolved server to $target" -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
