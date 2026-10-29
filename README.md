<div align="center">

# ScamWYF.VoiceBackend

![license](https://img.shields.io/badge/license-MIT-blue)
![game build](https://img.shields.io/badge/game-0.1.92--swyf-blue)
![Unity](https://img.shields.io/badge/Unity-6000.3.26f1-blue)
![BepInEx](https://img.shields.io/badge/BepInEx-5.4.23.5-14172c?style=for-the-badge)

*Built with:* C# · Harmony · BepInEx · ONNX Runtime

</div>

---

## Overview

**ScamWYF.VoiceBackend** is an unofficial BepInEx mod for **Scam With Your Friends** that replaces
the two local speech models the game ships with:

- **Whisper is replaced by NVIDIA Parakeet** — `parakeet-tdt-0.6b-v2` for English and
  `parakeet-tdt-0.6b-v3` for the 25 European languages it covers. The rest of the game's speech
  pipeline (voice-activity detection, partial transcripts, timings, echo filtering) is untouched.
- **Pocket TTS is replaced by Kokoro** — Kokoro-82M for English, with the caller's voice tuned from
  the game's own per-caller reference WAV by the inno-kokoro clone tuner instead of a fixed voice.

Both models run **inside the game process**, on the ONNX Runtime the game already ships
(`Scam With Your Friends_Data/Plugins/x86_64/onnxruntime.dll`). There is no companion server, no
Python, and no second copy of a runtime: the mod is a Harmony layer that hands the same audio to the
same two models and feeds the answers back into the game's own structures, so the caller still gets
subtitles, cancellation, first-audio handling and speaker routing exactly as before.

**Key features**

- **One hook per feature.** Speech recognition joins at `Whisper.WhisperManager.GetTextAsync`;
  speech synthesis joins at `PocketTTS.PocketTTS.Prompt`. Everything around those calls is the
  game's own code.
- **The stock Pocket model can stay unloaded.** With `SkipPocketModel = true` the game's Pocket ONNX
  sessions never load: the component is faked into `Ready` and Kokoro supplies every sample through
  the same frame queue. Off by default, because Kokoro is English-only here and other languages need
  Pocket to answer; on for an English-only group.
- **Per-language model choice.** English uses Parakeet v2, the v3 language set uses v3, and a
  language outside that set follows `OtherLanguages` — either the game's Whisper or v3 anyway.
- **In-game settings and hot reload.** Every option is on the shared **F1** menu, and the `.cfg` is
  watched: turning a replacement off puts the original provider back without a restart.
- **No telemetry, no credentials, no cloud calls.** Model downloads on first use are the only
  network traffic; after that the mod never opens a socket.
- No redistributed game DLLs or decompiled game source.

This project is not affiliated with or endorsed by the developers or publisher of Scam With Your
Friends.

---

## Table of Contents

- [How It Hooks In](#how-it-hooks-in)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [Install the mod](#install-the-mod)
  - [Nothing to start](#nothing-to-start)
  - [Configure the mod](#configure-the-mod)
- [Language Coverage](#language-coverage)
- [Configuration Reference](#configuration-reference)
- [The Local Backend](#the-local-backend)
- [Remote Services](#remote-services)
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
it asks a `WhisperManager` for text. This mod replaces only that answer: the prefix resamples to
16 kHz mono, runs the selected Parakeet checkpoint in-process on a worker thread, and returns a
`WhisperResult` built from the text.

Every `GetTextAsync` the manager declares that takes `(float[] samples, int frequency, int channels)`
first is patched, and the prefix reads its arguments through the call's own signature rather than a
fixed one. A game build that adds parameters to the method therefore keeps working; one that removes
the method logs that recognition stays on Whisper. The native `WhisperWrapper` is deliberately left
alone: it carries no language of its own, so answering it would also answer a language the player
had left to Whisper.

The game also has a second recognizer, **Moonshine**, which on some machines it prefers and which
would bypass the Whisper method entirely. While Parakeet is active, `WindowsSpeechCompatibility.UseMoonshine`
is forced to `false`, so recognition stays on the path this mod answers. If Parakeet fails (a model
file missing, for instance) and `FallbackToWhisper` is on, the original Whisper call is used for that
request; the failure is still counted and shown on the menu tab.

### Speech synthesis

| Language | Local backend |
|---|---|
| English | Kokoro-82M, voice tuned from the caller's reference clip |
| Everything else | The game's own provider — see the limit below |

**Kokoro here is English-only, on purpose.** Kokoro's acoustic model covers eight languages, but its
other seven are phonemised with espeak-ng in the reference implementation, and this mod will not
bundle a GPL native library. Rather than speak German with English phonemes, the mod declines the
line and the game's Pocket TTS speaks it, which is why the shipping defaults are
`SkipPocketModel = false` and `FallbackToPocket = true`: whatever Kokoro declines, Pocket answers.

This is the only place the local backend is narrower than the old Chatterbox server, which covered
23 languages.

---

## The Local Backend

Everything runs in the game process, on the ONNX Runtime the game already has. The mod only supplies
code and a few small assets; the weights are downloaded once.

| Model | Files | Size | Source |
|---|---|---|---|
| Parakeet v2 (English ASR) | `nemo128.onnx`, `encoder-model.int8.onnx`, `decoder_joint-model.int8.onnx`, `vocab.txt` | 630 MB | `istupakov/parakeet-tdt-0.6b-v2-onnx` |
| Parakeet v3 (25 European ASR) | same four | 630 MB | `istupakov/parakeet-tdt-0.6b-v3-onnx` |
| Kokoro-82M (TTS) | `kokoro-v1.0.onnx` | 325 MB | kokoro-onnx release `model-files-v1.1` |
| inno-kokoro tuner | `encoder.onnx`, `tuner.bin`, `default-voice.bin` | 30 MB | `remsky/kokoro-inno-clone-tuner` v0.2.0, converted by `tools/export-inno.py` |
| English lexicon | `en-lexicon.bin` | 2.4 MB | the `misaki` package's IPA dictionaries, packed by `tools/build-lexicon.py` |

Weights go to `BepInEx/plugins/VoiceBackend/models/<model>/`; the last two rows are the mod's own
assets and go to `VoiceBackend/{inno,g2p}/`. Both are written as `.part` and renamed when the byte
length matches the catalog, and `AutoDownload = false` turns the fetching off for a hand-placed
folder.

The tuner and the lexicon are published as assets of this mod's own GitHub release rather than as
part of the release zip. That is not an accident of packaging: the installers this mod ships through
copy the assemblies an entry names and nothing else, so data inside the zip would never reach the
disk. They are fetched from the release matching this build's version, so a DLL never loads a newer
set of assets than the one it was compiled and tested against.

**Speech recognition** is Parakeet TDT, decoded the way the model wants: `nemo128.onnx` produces the
log-mel features, the conformer encoder runs once per clip, and the joint network then emits a token
plus a duration per frame, so the loop advances by the duration the model chose. Nothing is
reimplemented except the loop itself.

**Speech synthesis** is Kokoro. Text goes through the ported misaki lexicon and morphology, the
phonemes map to Kokoro's 115-symbol vocabulary, and the style vector comes from the inno-kokoro
tuner: it embeds the caller's reference clip, blends Kokoro's stock prosody packs by pitch and rate,
and writes the 510-row voice table Kokoro conditions on. With no reference clip the stock
`af_heart` voice is used.

`Backend = Remote` reverses who does the work: the mod posts the same requests the old companion
server answered —

| Endpoint | Body | Response |
|---|---|---|
| `POST /asr?language=en&model=v2` | 16 kHz mono WAV | `{"text": "...", "confidence": 0.0}` |
| `POST /tts` | JSON `{text, language, voice_path}` | 24 kHz mono WAV |
| `GET /health` | — | provider and device report |

---

## Remote Services

`AsrService` and `TtsService` are independent, so the four arrangements that matter all work:
everything local (default), local recognition with cloud speech, cloud recognition with Kokoro, or
both remote.

| Setting | Service | What the mod sends | What it asks for back |
|---|---|---|---|
| `Deepgram` | Deepgram pre-recorded listen | the utterance as `audio/wav`, `Authorization: Token <key>` | JSON, `results.channels[0].alternatives[0].transcript` |
| `Gladia` | Gladia pre-recorded | multipart upload, then a job, then polling every second | JSON, `result.transcription.utterances[]` joined |
| `OpenAi` | `/v1/audio/transcriptions` | multipart: `file`, `model`, `language`, `response_format=json` | JSON, `text` |
| `ElevenLabs` | ElevenLabs text to speech | JSON `{text, voice_settings}`, `xi-api-key` | **raw 16-bit PCM at 24 kHz** (`output_format=pcm_24000`) |
| `Deepgram` | Deepgram Aura speak | JSON `{text}`, `Authorization: Token <key>` | WAV (`container=wav`, `encoding=linear16`) |
| `OpenAi` | `/v1/audio/speech` | JSON `{model, voice, input, response_format=pcm}` | **raw 16-bit PCM** |
| `Server` | this mod's own `/asr` and `/tts` | sees below | 24 kHz mono WAV |

Every request asks for a format the mod can consume without a media decoder: raw linear16 or WAV.
MP3, Ogg/Opus and WebM bodies are refused by name rather than played as noise, so a service that
ignores the requested format fails loudly instead of sounding broken.

**Settings live in `[2 - Remote API]`:** one base URL per service (point it at a proxy or a
self-hosted server when audio must not leave your network), one key each, and the model or voice
names. `OpenAiUrl` also covers any server that speaks the OpenAI shape - faster-whisper-server,
LocalAI, LM Studio - which is the cheap way to move recognition off the game's process without an
account.

Keys are stored in plain text in the `.cfg`, the same file BepInEx writes everything else to. The
mod never logs a key: a URL is redacted before it appears in `LogOutput.log`, and the debug line for
a request prints the length of the body, not its contents.

**What remote speech does not do:** it does not clone the caller. ElevenLabs, Deepgram Aura and the
OpenAI voices speak in the one voice configured for that service, so every caller sounds the same
unless you keep `TtsService = Local` for the cloned voices or run one voice per service yourself.
Language is passed as a two-letter code from the game's own language for that turn; ElevenLabs'
multilingual models infer it from the text.

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

The inference code has a console harness that runs outside Unity against the game's own
`onnxruntime.dll`, which is the fastest way to check a change without a live call:

```powershell
$env:VOICEBACKEND_ORT_DIR = "C:\...\Scam With Your Friends_Data\Plugins\x86_64"
dotnet run --project tests/local -- probe
dotnet run --project tests/local -- asr .\models\parakeet-v2 tests/local/fixtures/asr/01.wav
dotnet run --project tests/local -- tts . <text-file> <reference.wav> out.wav
```

`tests/local/fixtures` holds reference transcripts and a reference Kokoro render, generated once by
the Python stack (see its README), so a change can be compared against something other than itself.

The remote paths have a stub: `tools/service-stub.ps1` answers with the payload shapes Deepgram,
Gladia, ElevenLabs and OpenAI document, logs every request's path, authorization header and
multipart framing, and lets the same in-game round trip run without an account or a cent of quota.

There is also an in-game round trip: start the game with `VOICEBACKEND_SELFTEST=1` set and
`-batchmode -nographics`, and the mod synthesises a known sentence with Kokoro, feeds the audio back
through Parakeet, and logs what it heard with both timings. It downloads whatever the configuration
needs first, which is how the download path gets exercised without a live call.

---

## Compatibility

| Component | Verified Version |
|---|---|
| Scam With Your Friends | `0.1.92-swyf` |
| Unity | `6000.3.26f1` |
| BepInEx | `5.4.23.5`, Mono preloader |
| ScamWYF.Modding.Core | vendored from the local library checkout |
| C# language level | `7.3` |
| Runtime | the game's own `onnxruntime.dll` (reports ABI 1.17, CPU + DirectML) |
| Models | `istupakov/parakeet-tdt-0.6b-v2-onnx` (int8), `istupakov/parakeet-tdt-0.6b-v3-onnx` (int8), `kokoro-v1.0.onnx` from the kokoro-onnx release, `remsky/kokoro-inno-clone-tuner` v0.2.0 |
| Platform | Windows x64 (Proton included); the local backend has no platform code beyond Win32 DLL loading |

Other game builds are untested. `GameBuild.CheckUnityVersion` reports a mismatch as a warning naming
both versions rather than assuming support.

---

## Known Limits

- **Kokoro speaks English here.** Its other seven languages need espeak-ng in the reference stack;
  the mod declines those lines instead of speaking them with English phonemes. Set
  `SkipPocketModel = false` and `FallbackToPocket = true` so the game's Pocket TTS covers them.
- **Only the Pocket provider is replaced.** Qwen TTS and MOSS TTS continue to serve the languages
  the game routes to them. That is game-side, not a setting.
- **Cloning is approximate.** The inno-kokoro tuner fits a Kokoro style vector to the reference clip:
  the model card measures about a third of the way to the speaker's identity, against 0.695 for a
  second real recording of that speaker. It is a voice that reads as the same person, not a copy.
- **The pronunciation lexicon is a port, not the reference.** English graphemes go through the
  misaki lexicon plus morphology and a letter-to-sound fallback, because misaki's neural fallback
  needs spaCy and BART. Dictionary words match the reference exactly; invented names are guesswork.
- **Recognition runs on the game's ONNX Runtime**, which is an older build than a current Python
  wheel. Parakeet is int8, and int8 kernels differ between versions: on the same clip this port and
  a current `onnx-asr` agree on the words but can differ in the last word of a long clip. That is the
  runtime, not the mod.
- **Windows ARM64 is not covered for recognition.** The game bypasses Whisper there and uses its
  own ARM speech worker; this mod patches the Whisper path.
- **The first request downloads models.** About 630 MB for Parakeet v2, 630 MB more for v3, 325 MB
  for Kokoro. `WarmupOnLoad` only preloads what is already on disk.
- **Reference voices come from the game.** The tuner reads the WAV the game already uses for that
  caller. With no reference clip it uses a stock Kokoro voice instead.
- **Remote speech is one voice.** ElevenLabs, Deepgram Aura and the OpenAI voices ignore the game's
  per-caller reference clips: every caller sounds like the configured voice. Only `TtsService = Local`
  clones.
- **A remote `Server` backend is expected to exist.** `AsrService`/`TtsService = Server` post the
  `/asr` and `/tts` requests the old companion server served; nothing here ships that server.
- **The remote services cost money per request.** The menu's **Check models** describes a service
  instead of calling it, precisely so a diagnostic click cannot spend quota.

---

## Security

The local backend opens no listening socket at all: it runs in the game's process. The only outbound
traffic is the model download from Hugging Face and the GitHub release asset, both on first use.
`Backend = Remote` is the only path that talks to a network address, and that address is whatever you
configure.

Do not paste `BepInEx/LogOutput.log` into a public issue without checking it: **this mod logs the
configured endpoint, the selected models and request timings**, and the server logs reference voice
paths.

---

## License

MIT — Copyright © 2026 Ras_rap. See [LICENSE](LICENSE).

The models are separate works under their own licences: Parakeet TDT v2/v3 are CC BY 4.0 from NVIDIA,
Kokoro-82M is Apache-2.0, and the inno-kokoro tuner is Apache-2.0 except its baked speaker encoder,
which is CC BY-SA 3.0 (see `tools/export-inno.py`). Nothing here grants any right to the game's own
assemblies.

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
