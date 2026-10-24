# Voice Backend server

The local half of [ScamWYF.VoiceBackend](../README.md). It serves two endpoints:

| Endpoint | Body | Response |
|---|---|---|
| `POST /asr?language=en&model=v2` | 16 kHz mono WAV | `{"text": "...", "confidence": 0.0}` |
| `POST /tts` | JSON `{text, language, voice_path, voice_b64, model}` | 24 kHz mono WAV |
| `GET /health` | — | loaded models, device, quantization |
| `POST /warmup?asr_model=v3&do_tts=true` | — | loads models before the first call |

Speech recognition is **NVIDIA Parakeet** through [`onnx-asr`](https://github.com/istupakov/onnx-asr):
`parakeet-tdt-0.6b-v2` for English, `parakeet-tdt-0.6b-v3` for the 25 European languages it covers.
Speech synthesis is **Chatterbox**: Turbo for English, and (by default) Chatterbox Multilingual V3
for languages Turbo does not speak. Turbo is English-only; that is a property of the checkpoint, not
of this server.

> **Players do not run this by hand.** A release ships a self-contained
> `VoiceBackend.Server.exe` in `BepInEx/plugins/VoiceBackend/`, and the mod starts it with the game.
> The instructions below are for developing or rebuilding the server.

## Requirements

- Windows x64, Linux x64, or macOS for the server. The mod itself is a Windows/Proton DLL.
- For a normal install: nothing. The release contains the self-contained executable.
- For building from source: [`uv`](https://docs.astral.sh/uv/), which downloads Python and every
  dependency, and about 2 GB of free disk for the build.
- Disk for the models: Parakeet v2 and v3 are about 700 MB each on first download; Chatterbox is
  another ~1.5 GB. They are cached by Hugging Face under your user profile.
- NVIDIA GPU is optional. Parakeet runs well on CPU in int8; Chatterbox is much faster on a GPU.

## Build the standalone server

```powershell
./build-server.ps1              # server/dist/VoiceBackend.Server/
./build-server.ps1 -Install     # ...and copy it into BepInEx/plugins/VoiceBackend/
```

PyInstaller bundles Python, onnxruntime, torch and the Chatterbox package into a one-folder
application. The result is about **1.7 GB** and needs no Python on the target machine. Model weights
are still downloaded on first use.

The release layout puts that folder at `BepInEx/plugins/VoiceBackend/`, one level below the plugin
scan. `ServerLauncher` finds it there and starts it with the game. See [`../RELEASING.md`](../RELEASING.md).

## Install and run from source

From this directory:

```powershell
uv sync --extra tts          # Python 3.11, FastAPI, onnx-asr, torch, chatterbox
./start.ps1
```

`start.ps1` runs `uv run --extra tts python app.py`, so the first start also creates the virtual
environment. Nothing is installed globally.

ASR only, no torch and no Chatterbox download:

```powershell
uv sync
./start.ps1 -NoTts
```

The server listens on `127.0.0.1:8010`. The mod's `BaseUrl` default is exactly that. To use another
port, pass `-Port 8011` and change `BaseUrl` in
`BepInEx/config/com.community.scamwyf.voicebackend.cfg`.

### NVIDIA GPU

`torch` from PyPI is the CPU build. If you want CUDA, install the wheel for your driver first, then
sync the rest:

```powershell
uv pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124
uv sync --extra tts
```

Parakeet can use ONNX Runtime's CUDA provider too, after installing `onnxruntime-gpu` and its CUDA
dependencies:

```powershell
uv pip uninstall onnxruntime
uv pip install "onnxruntime-gpu[cuda,cudnn]>=1.20"
$env:PARAKEET_PROVIDERS = "CUDAExecutionProvider"
```

On a machine without the right CUDA libraries, leave `PARAKEET_PROVIDERS` unset. `onnxruntime` falls
back to CPU, and `CHATTERBOX_DEVICE=cpu` makes the TTS choice explicit.

## Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `VOICE_HOST` | `127.0.0.1` | Bind address. Keep it on loopback unless you mean to expose it. |
| `VOICE_PORT` | `8010` | Port. |
| `PARAKEET_QUANTIZATION` | `int8` | `int8`, `fp16`, `fp32`/`none`. int8 is the CPU-friendly default. |
| `PARAKEET_PROVIDERS` | *(unset)* | Comma-separated ONNX Runtime providers, e.g. `CUDAExecutionProvider`. |
| `CHATTERBOX_MODEL` | `auto` | `auto`, `turbo`, or `multilingual`. |
| `CHATTERBOX_DEVICE` | `auto` | `auto`, `cuda`, `cpu`, `mps`. |
| `CHATTERBOX_REFERENCE_SECONDS` | `10` | How much of the reference voice to keep. Longer is not better. |
| `CHATTERBOX_DEFAULT_VOICE` | *(unset)* | WAV used when the game sends a voice path this machine cannot read. |
| `LOG_LEVEL` | `INFO` | `DEBUG` shows every request. |

## Calling it by hand

```powershell
# health
curl.exe http://127.0.0.1:8010/health

# speech recognition, from a WAV file
curl.exe -X POST "http://127.0.0.1:8010/asr?language=en&model=v2" `
  -H "Content-Type: audio/wav" --data-binary "@test.wav"

# speech synthesis
curl.exe -X POST http://127.0.0.1:8010/tts `
  -H "Content-Type: application/json" `
  -d '{\"text\":\"Hi, this is a test.\",\"language\":\"en\",\"model\":\"turbo\",\"voice_path\":\"C:/path/to/voice.wav\"}' `
  --output out.wav
```

## Notes

- **Models are downloaded on first use**, not at install time. The first English line therefore
  takes as long as the download plus model load; the game's own timeout may fire. Run
  `POST /warmup?asr_model=v2&do_tts=true` once after starting the server, or start the game with a
  little patience on the first call.
- **Voice cloning** uses the game's own reference WAVs (`StreamingAssets/kolkata-voices`). The mod
  sends the resolved path by default; `SendVoiceAudio = true` uploads the file itself for a remote
  server.
- **Turbo is English only.** `CHATTERBOX_MODEL=auto` uses Multilingual V3 everywhere else, because
  Turbo reading Spanish or German is an English voice reading Spanish or German.
- **No telemetry, no credentials, no network egress except model downloads.** The server binds to
  loopback, and all audio stays on the machine.
