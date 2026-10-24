<div align="center">

# ScamWYF.VoiceBackend

![license](https://img.shields.io/badge/license-MIT-blue)
![game build](https://img.shields.io/badge/game-0.1.89--swyf-blue)
![Unity](https://img.shields.io/badge/Unity-6000.3.26f1-blue)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-14172c?style=for-the-badge)

*Built with:* C# · Harmony · BepInEx · Python · ONNX Runtime

</div>

---

## Overview

**ScamWYF.VoiceBackend** is an unofficial BepInEx mod for **Scam With Your Friends** that replaces
the two local speech models the game ships with:

- **Whisper is replaced by NVIDIA Parakeet** — `parakeet-tdt-0.6b-v2` for English and
  `parakeet-tdt-0.6b-v3` for the 25 European languages it covers. The rest of the game's speech
  pipeline (voice-activity detection, partial transcripts, timings, echo filtering) is untouched.
- **Pocket TTS is replaced by Chatterbox** — Chatterbox Turbo for English, Chatterbox Multilingual
  V3 for the other languages when `TtsModel = Auto`, with zero-shot cloning from the game's own
  per-caller reference WAVs.

The models run in a small companion server (`server/`) on the same machine, over loopback. The mod
is a thin Harmony layer that sends audio to that server and feeds the answers back into the game's
own structures, so the caller still gets subtitles, cancellation, first-audio handling and speaker
routing exactly as before.

**Key features**

- **One hook per feature.** Speech recognition joins at `Whisper.WhisperManager.GetTextAsync`;
  speech synthesis joins at `PocketTTS.PocketTTS.Prompt`. Everything around those calls is the
  game's own code.
- **The stock Pocket model can stay unloaded.** With `SkipPocketModel = true` (the default), the
  game's Pocket ONNX sessions never load. The component is faked into `Ready`, and Chatterbox
  supplies every sample through the same frame queue.
- **Per-language model choice.** English uses Parakeet v2, the v3 language set uses v3, and a
  language outside that set follows `OtherLanguages` — either the game's Whisper or v3 anyway.
- **In-game settings and hot reload.** Every option is on the shared **F1** menu, and the `.cfg` is
  watched: turning a replacement off puts the original provider back without a restart.
- **No telemetry, no credentials, no cloud calls.** The server binds to loopback. Model downloads
  are the only network traffic.
- No redistributed game DLLs or decompiled game source.

This project is not affiliated with or endorsed by the developers or publisher of Scam With Your
Friends.

---

## Table of Contents

- [How It Hooks In](#how-it-hooks-in)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Install the mod](#install-the-mod)
  - [The server is started for you](#the-server-is-started-for-you)
  - [Configure the mod](#configure-the-mod)
- [Language Coverage](#language-coverage)
- [Configuration Reference](#configuration-reference)
- [The Companion Server](#the-companion-server)
- [Building and Testing](#building-and-testing)
- [Compatibility](#compatibility)
- [Known Limits](#known-limits)
- [Security](#security)
- [License](#license)
- [Related Projects](#related-projects)

---

## How It Hooks In

Everything the game does with speech funnels through two methods.

### Speech recognition

```csharp
Whisper.WhisperManager.GetTextAsync(float[] samples, int frequency, int channels)
    -> Task<WhisperResult>
```

`WhisperVcPipeline` owns the microphone, the voice-activity detector, the utterance buffer, the
partial/final split, the 15-second timeout and the `OnTranscriptUpdated` events. When it has audio,
it asks a `WhisperManager` for text. This mod replaces only that answer: the prefix encodes the
samples as 16 kHz mono WAV, asks the server for the selected Parakeet checkpoint, and returns a
`WhisperResult` built from the text.

The game also has a second recognizer, **Moonshine**, which on some machines it prefers and which
would bypass the Whisper method entirely. While Parakeet is active, `WindowsSpeechCompatibility.UseMoonshine`
is forced to `false`, so recognition stays on the path this mod answers. If the server fails and
`FallbackToWhisper` is on, the original Whisper call is used for that request; the failure is still
counted and shown on the menu tab.

### Speech synthesis

```csharp
PocketTTS.PocketTTS.Prompt(string text, string voicePath, string languageCode)
    -> bool
```

`PocketTtsSingleton` owns the request queue, caller/language bookkeeping, tracked-completion
reports, cancellation and the speaker routing. `PocketTTS` owns the model, the audio queue, the
frame pacer, sequence numbers and the word timings. This mod replaces the `Prompt` call and then
enqueues Chatterbox's WAV through the game's own `OnResponseGenerated`/frame path, so all of the
surrounding behaviour keeps working.

When `SkipPocketModel = true`, `PocketTTS.InitModel` is skipped and the component is put straight
into `Ready` — the state `PrepareLegacyProvider` waits for. `WarmUpVoice` becomes a no-op and
`IsVoiceReady` reports true, because Chatterbox clones from the same WAV rather than loading a
local voice embedding. The game's 30-second "no audio progress" cancel and its slow-non-English
transcript recovery are suspended only while a Chatterbox request is still generating.

### What is not replaced

The game has other speech providers. **Qwen TTS** and **MOSS TTS** are left exactly as they are.
Languages the game routes to those providers (Chinese, Japanese, Korean, Russian in some
configurations) keep using them. This mod replaces **Pocket**, which is what the game uses for
English and most of the languages Pocket supports.

Parakeet's speech recognition replacement covers every language that reaches the Whisper path.
On Windows ARM64 the game uses its ARM speech worker instead of Whisper; this mod does not replace
that path.

---

## Getting Started

### Prerequisites

- A legally installed copy of **Scam With Your Friends**. Verified against `0.1.89-swyf`, Unity
  `6000.3.26f1`.
- BepInEx and the unstripped corlib override, installed by
  [Setup](https://github.com/swyf-modding/Setup). The game ships a stripped `mscorlib` that
  BepInEx cannot start without.
- [ScamWYF.Modding.Core](https://github.com/swyf-modding/mod-lib), which owns the shared menu. It is
  vendored in `vendor/` here so the repository builds out of the box.
- The release includes a **self-contained server executable**. Python is only needed if you build
  that server from source; a player installing the mod does not install Python or `uv`.
- Disk for the server and the models: the CPU server folder is about **1.7 GB**; a CUDA build is
  about **7.5 GB** because it carries both torch's CUDA 12 runtime and ONNX Runtime's CUDA 13
  libraries. The models are downloaded on first use — two Parakeet checkpoints (~700 MB each) and
  one or both Chatterbox checkpoints (~1.5 GB each).
- An NVIDIA GPU is optional. Parakeet is comfortable on CPU in int8; Chatterbox is much faster on a
  GPU and is the part where a CPU-only machine feels slow.

### Install the mod

```powershell
git clone --recurse-submodules https://github.com/swyf-modding/Voice-Backend.git
cd Voice-Backend
./build.ps1                # builds and copies into the game
./build.ps1 -NoCopy -Test  # build, run the library tests, do not touch the game
```

`build.ps1` builds the shared library first, then this mod against the dll it just produced, then
installs:

- `ScamWYF.VoiceBackend.dll` → `BepInEx/plugins`
- `ScamWYF.Modding.Core.dll` → `BepInEx/core`

A release zip also contains the self-contained server in `BepInEx/plugins/VoiceBackend/`. When
building from source, create that folder once with `server/build-server.ps1 -Install`; `build.ps1`
does not compile it.

The library is deliberately **not** in `plugins`: BepInEx would try to load it as a plugin, and every
mod embedding its own copy would give the game two menus and two hotkey tables.

The game install is auto-detected, or set `SWYG_GAME_DIR` / pass `-GameDir`.

### The server is started for you

A release puts the self-contained server in `BepInEx/plugins/VoiceBackend/`:

```text
BepInEx/plugins/
|-- ScamWYF.VoiceBackend.dll
`-- VoiceBackend/
    |-- VoiceBackend.Server.exe
    `-- _internal/...
```

On game start the mod checks `/health`. If nothing is listening, it launches
`VoiceBackend.Server.exe`, waits for it to become healthy, and then asks it to warm its models. On a
normal game exit it stops the server again — but only if the mod started it. A server you had
already started yourself is never touched.

The server's stdout/stderr goes to `BepInEx/VoiceBackend.Server.log`, which is the first place to
look if it does not start. The menu's **Voice Backend** tab shows the process state and has
**Start server** / **Stop server** buttons.

There is nothing else to install or start for a normal player. Python, `uv` and `pip` are only for
building the server from source:

```powershell
cd server
./build-server.ps1 -Install
```

Running the Python server by hand is still useful while developing it:

```powershell
cd server
uv sync --extra tts
./start.ps1
```

The mod leaves an already-running server alone, so either workflow is fine.

### GPU or CPU: `Mode`

`Mode` decides where inference runs. The default is `Gpu`:

| Value | Behaviour |
|---|---|
| `Gpu` | Prefer the GPU. Chatterbox uses CUDA/ROCm (`cuda` in torch), Intel XPU, or Apple MPS. Parakeet uses TensorRT/CUDA/ROCm/DirectML/OpenVINO/CoreML/WebGPU if the ONNX Runtime has one. If nothing usable is present, the server logs a warning and runs on CPU. |
| `Auto` | Same as `Gpu`, but a CPU fallback is silent. |
| `Cpu` | Force CPU for both models. |

The hardware is detected at runtime, not at build time, so `Mode = Gpu` is the only setting a player
normally changes. The **server binary itself** has to contain the matching runtime:

| Build | Hardware |
|---|---|
| `-Backend cuda` | NVIDIA CUDA 12/13, with CPU fallback. ~7.5 GB. |
| `-Backend rocm` | AMD ROCm on Linux. |
| `-Backend directml` | Windows DirectML for Parakeet; Chatterbox stays CPU. |
| `-Backend cpu` | CPU only, smallest (~1.7 GB). |
| `-Backend auto` | Detects the build machine's GPU and picks one of the above. |

The menu's **Check server** button shows the selected torch device, backend and ONNX providers from
`/health`, which is the quickest way to confirm that `Gpu` really selected the GPU.

### Configure the mod

Launch the game once. BepInEx writes
`BepInEx/config/com.community.scamwyf.voicebackend.cfg`. Press **F1** and open **Voice Backend** for
the same settings as a form, or edit the file in a text editor; it is watched and takes effect
within about half a second.

The defaults are the intended setup:

```ini
[1 - General]
Enabled = true

[2 - Server]
BaseUrl = http://127.0.0.1:8010
Mode = Gpu
WarmupOnLoad = true
AutoStartServer = true
StopServerOnExit = true

[3 - Speech recognition]
AsrEnabled = true
EnglishModel = V2
EuropeanModel = V3
OtherLanguages = Whisper

[4 - Text to speech]
TtsEnabled = true
TtsModel = Auto
SkipPocketModel = true
```

---

## Language Coverage

### Speech recognition

| Game language | Model | Notes |
|---|---|---|
| English (`en`) | Parakeet v2 | English-only checkpoint; the best English accuracy. |
| Spanish, French, German, Italian, Portuguese, Dutch, Polish, Czech, Russian, Ukrainian, Danish, Swedish, Finnish, Greek, Hungarian, Romanian, Bulgarian, Croatian, Estonian, Latvian, Lithuanian, Maltese, Slovak, Slovenian | Parakeet v3 | The model's full 25-language set. |
| Chinese, Japanese, Korean, Arabic, Persian, Hindi, Turkish, Indonesian, and anything else | The game's Whisper | Default. `OtherLanguages = ParakeetV3` sends it to v3 instead. |

Parakeet v3 detects the language itself; no language prompt is sent.

### Speech synthesis

| Language | `TtsModel = Auto` |
|---|---|
| English | Chatterbox **Turbo** (350M, English only) |
| Arabic, Danish, German, Greek, Spanish, Finnish, French, Hebrew, Hindi, Italian, Japanese, Korean, Malay, Dutch, Norwegian, Polish, Portuguese, Russian, Swedish, Swahili, Turkish, Chinese | Chatterbox **Multilingual V3** |
| Czech, Estonian, Indonesian, Hungarian | Multilingual V3 with English (`en`) as the language tag; the script is Latin and the result is intelligible, not native. |
| Persian | Multilingual V3 with Arabic (`ar`) as the language tag. |
| Anything else | English. Set `TtsModel = Turbo` if you want Turbo for everything. |

Chatterbox Turbo is English-only. That is a property of the checkpoint: using it for Spanish or
German produces an English voice reading those words. `Auto` exists so the common case (English
callers) gets the fast model and other languages still sound like the language they are written in.

---

## Configuration Reference

`BepInEx/config/com.community.scamwyf.voicebackend.cfg`.

### General

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. Off installs no patches at all. |
| `Debug` | `false` | Log every request: URL, model, audio length, HTTP status. |

### Server

| Key | Default | Meaning |
|---|---|---|
| `BaseUrl` | `http://127.0.0.1:8010` | Server root. `AsrPath` and `TtsPath` are appended. |
| `Mode` | `Gpu` | `Gpu` prefers CUDA/ROCm/XPU/MPS and falls back to CPU; `Auto` does the same without warning; `Cpu` forces CPU. |
| `AsrPath` | `/asr` | Speech-recognition endpoint. |
| `TtsPath` | `/tts` | Text-to-speech endpoint. |
| `ApiKey` | *(empty)* | Sent as `Authorization: Bearer`. Stored in plain text. |
| `AsrTimeoutSeconds` | `10` | Per-request recognition timeout, below the game's 15 seconds. |
| `TtsTimeoutSeconds` | `90` | Per-request synthesis timeout. CPU-only Chatterbox needs the headroom. |
| `WarmupOnLoad` | `true` | Ask the server to load its models at game start. |
| `AutoStartServer` | `true` | Launch `VoiceBackend.Server.exe` if nothing is listening. |
| `ServerExecutable` | *(empty)* | Explicit server path. Empty searches `BepInEx/plugins/VoiceBackend`, `BepInEx/plugins`, the game root and nearby folders. |
| `ServerStartTimeoutSeconds` | `300` | How long to wait for the auto-started server to answer `/health`. A cold CUDA bundle can take a while on the first launch. |
| `StopServerOnExit` | `true` | Stop the server on game exit, but only if this mod started it. |

### Speech recognition

| Key | Default | Meaning |
|---|---|---|
| `AsrEnabled` | `true` | Replace Whisper with Parakeet and disable Moonshine. |
| `EnglishModel` | `V2` | `V2` or `V3` for English. |
| `EuropeanModel` | `V3` | `V2` or `V3` for the v3 language set. |
| `OtherLanguages` | `Whisper` | `Whisper` or `ParakeetV3` outside v3's set. |
| `FallbackToWhisper` | `true` | If the server fails, run the game's Whisper for that request. |

### Text to speech

| Key | Default | Meaning |
|---|---|---|
| `TtsEnabled` | `true` | Replace the Pocket provider with Chatterbox. |
| `TtsModel` | `Auto` | `Auto`, `Turbo` or `Multilingual`. |
| `SkipPocketModel` | `true` | Never load the game's Pocket ONNX models. |
| `FallbackToPocket` | `false` | On failure, use the stock Pocket model. Requires `SkipPocketModel = false`; the two cannot both be on. |
| `SendVoiceAudio` | `false` | Upload the reference WAV instead of sending its path. For a remote server. |

The menu tab also shows live request/failure counts, Whisper fallbacks, the last request, the last
error verbatim, and a **Check server** button that reads `/health`.

---

## The Companion Server

The server is described in [`server/README.md`](server/README.md). It is a self-contained
`VoiceBackend.Server.exe` built with PyInstaller: Python, torch, onnxruntime and the Chatterbox
package are inside its folder. The mod starts it automatically, and its output goes to
`BepInEx/VoiceBackend.Server.log`.

The model weights are **not** in the executable. They are downloaded on first use into the Hugging
Face cache, so the first English line on a fresh machine waits for Parakeet v2, and the first
non-English line waits for the checkpoint it needs.

| Endpoint | Purpose |
|---|---|
| `POST /asr?language=en&model=v2` | Parakeet transcription of a WAV body. |
| `POST /tts` | Chatterbox synthesis; returns 24 kHz mono WAV. |
| `GET /health` | Loaded models, device, quantization. |
| `POST /warmup` | Load models in the background. |

It uses [`onnx-asr`](https://github.com/istupakov/onnx-asr) for Parakeet (no NeMo toolkit, no CUDA
requirement) and the official `chatterbox-tts` package for Chatterbox. Both are optional halves:
without the `tts` extra, `/asr` still works and `/health` reports `tts.installed: false`.

The server is not bundled into the DLL. That is deliberate: the models are gigabytes, Python is
where their runtimes live, and a model crash should not take the game process with it.

---

## Building and Testing

```powershell
./build.ps1 -NoCopy           # compile only
./build.ps1 -NoCopy -Test     # compile, then run the library's API and behaviour tests
```

The mod compiles with `-nostdlib+` against **the game's own `mscorlib`**, so a method that managed
stripping removed is a build error rather than a `MissingMethodException` in a session.

A headless smoke test, without the game taking over the screen:

```powershell
& "C:\...\Scam With Your Friends.exe" -batchmode -nographics
```

BepInEx still loads plugins and applies Harmony patches, and writes
`BepInEx/LogOutput.log`. `-batchmode` loads no scene, so the call paths that need a live call cannot
be exercised, but patch installation and the model warm-up request can.

The server can be checked by hand:

```powershell
curl.exe http://127.0.0.1:8010/health
curl.exe -X POST "http://127.0.0.1:8010/asr?language=en&model=v2" `
  -H "Content-Type: audio/wav" --data-binary "@test.wav"
```

---

## Compatibility

| Component | Verified Version |
|---|---|
| Scam With Your Friends | `0.1.89-swyf` |
| Unity | `6000.3.26f1` |
| BepInEx | `5.4.23.5`, Mono preloader |
| ScamWYF.Modding.Core | vendored from the local library checkout |
| C# language level | `7.3` |
| Server | Python 3.11, `onnx-asr` 0.12, `chatterbox-tts` 0.1.7, torch 2.6 |
| Platform | Windows x64; the server also runs on Linux/macOS, the mod is a Windows DLL (Proton included) |

Other game builds are untested. `GameBuild.CheckUnityVersion` reports a mismatch as a warning naming
both versions rather than assuming support.

---

## Known Limits

- **Only the Pocket provider is replaced.** Qwen TTS and MOSS TTS continue to serve the languages
  the game routes to them. If you want Chatterbox for those too, `TtsModel` does not change the
  provider choice — that is game-side.
- **Chatterbox on CPU is slow.** It generates the whole clip before answering. A ten-second line
  can take tens of seconds on CPU. Use a CUDA build of torch for anything beyond testing. The mod
  suspends the game's 30-second stall cancel while synthesis is in flight, but the caller still
  takes that long to start speaking.
- **Windows ARM64 is not covered for recognition.** The game bypasses Whisper there and uses its
  own ARM speech worker; this mod patches the Whisper path.
- **The first request downloads models.** The server is started automatically, but a fresh install
  still downloads the selected checkpoints before the first answer. `WarmupOnLoad` starts that in
  the background at game load; a cold server plus an immediate first caller can still be slow.
- **The server folder is large.** It is about 1.7 GB because it contains Python, torch and the
  Chatterbox runtime. The model weights are downloaded separately on first use. This is the cost of
  not asking players to install Python.
- **Auto-start is local only.** If `BaseUrl` points at another machine, the mod leaves it alone and
  expects that machine to be running the server.
- **Reference voices come from the game.** Chatterbox clones the WAV the game already uses for that
  caller. A custom/community voice that is not on disk where the server can read it will fall back to
  the server's default voice or no reference; `SendVoiceAudio = true` fixes the remote-server case.

---

## Security

The companion server binds to `127.0.0.1` by default and has no authentication because it is meant
to be reachable only by the local game. If you change `VOICE_HOST`, put it behind something that
authenticates. It holds no credentials and sends no telemetry; the only outbound traffic is model
downloads from Hugging Face.

Do not paste `BepInEx/LogOutput.log` into a public issue without checking it: **this mod logs the
configured endpoint, the selected models and request timings**, and the server logs reference voice
paths.

---

## License

MIT — Copyright © 2026 Ras_rap. See [LICENSE](LICENSE).

The models are separate works under their own licences: Parakeet TDT v2/v3 are CC BY 4.0 from NVIDIA,
and Chatterbox is MIT from Resemble AI. Nothing here grants any right to the game's own assemblies.

`vendor/ScamWYF.Modding.Core` is the shared library, vendored so this repository builds out of the
box. To track upstream instead, replace it with the submodule:

```powershell
git submodule add https://github.com/swyf-modding/mod-lib.git vendor/ScamWYF.Modding.Core
```

---

## Related Projects

| Project | What it is |
|---|---|
| [mod-lib](https://github.com/swyf-modding/mod-lib) | Shared library: base class, menu, config editor, hot reload, patch coordinator |
| [Setup](https://github.com/swyf-modding/Setup) | Installs BepInEx, the corlib override, and the vtable patches this game needs |
| [Switchboard](https://github.com/swyf-modding/Switchboard) | Installs, launches, and manages mods from outside the game |
| [Mod-Handler](https://github.com/swyf-modding/Mod-Handler) | The in-game **Plugins** tab |
| [AI-Backend](https://github.com/swyf-modding/AI-Backend) | Routes the game's AI calls to your own LLM |
