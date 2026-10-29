using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using ScamWYF.Modding.Core;

namespace ScamWYF.VoiceBackend
{
    /// <summary>Which Parakeet checkpoint to use for a language.</summary>
    public enum ParakeetModel
    {
        /// <summary>parakeet-tdt-0.6b-v2, English only. The fastest and most accurate for English.</summary>
        V2,
        /// <summary>parakeet-tdt-0.6b-v3, 25 European languages plus English.</summary>
        V3
    }

    /// <summary>What to do with a language Parakeet v3 does not cover.</summary>
    public enum OtherLanguageAsr
    {
        /// <summary>Leave the request to the game's own Whisper model.</summary>
        Whisper,
        /// <summary>Ask Parakeet v3 anyway. Better than nothing for closely related languages, poor for others.</summary>
        ParakeetV3
    }



    /// <summary>Where the companion server runs inference.</summary>
    public enum DeviceMode
    {
        /// <summary>GPU when the runtime sees one, CPU otherwise.</summary>
        Auto,
        /// <summary>Force CPU.</summary>
        Cpu,
        /// <summary>Prefer CUDA, ROCm, XPU or MPS; fall back to CPU with a warning if none is usable.</summary>
        Gpu
    }

    internal sealed class Config
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<bool> Debug;

        public readonly ConfigEntry<Api.SttService> AsrService;
        public readonly ConfigEntry<Api.TtsService> TtsService;
        public readonly ConfigEntry<bool> AutoDownload;

        public readonly ConfigEntry<string> DeepgramUrl;
        public readonly ConfigEntry<string> DeepgramApiKey;
        public readonly ConfigEntry<string> DeepgramSttModel;
        public readonly ConfigEntry<string> DeepgramTtsVoice;
        public readonly ConfigEntry<string> GladiaUrl;
        public readonly ConfigEntry<string> GladiaApiKey;
        public readonly ConfigEntry<string> ElevenLabsUrl;
        public readonly ConfigEntry<string> ElevenLabsApiKey;
        public readonly ConfigEntry<string> ElevenLabsVoiceId;
        public readonly ConfigEntry<string> ElevenLabsModel;
        public readonly ConfigEntry<string> OpenAiUrl;
        public readonly ConfigEntry<string> OpenAiApiKey;
        public readonly ConfigEntry<string> OpenAiSttModel;
        public readonly ConfigEntry<string> OpenAiTtsModel;
        public readonly ConfigEntry<string> OpenAiTtsVoice;
        public readonly ConfigEntry<string> BaseUrl;
        public readonly ConfigEntry<DeviceMode> Mode;
        public readonly ConfigEntry<string> AsrPath;
        public readonly ConfigEntry<string> TtsPath;
        public readonly ConfigEntry<string> ApiKey;
        public readonly ConfigEntry<int> AsrTimeoutSeconds;
        public readonly ConfigEntry<int> TtsTimeoutSeconds;
        public readonly ConfigEntry<bool> WarmupOnLoad;

        public readonly ConfigEntry<bool> AsrEnabled;
        public readonly ConfigEntry<ParakeetModel> EnglishModel;
        public readonly ConfigEntry<ParakeetModel> EuropeanModel;
        public readonly ConfigEntry<OtherLanguageAsr> OtherLanguages;
        public readonly ConfigEntry<bool> AsrFallbackToWhisper;

        public readonly ConfigEntry<bool> TtsEnabled;
        public readonly ConfigEntry<bool> SkipPocketModel;
        public readonly ConfigEntry<bool> TtsFallbackToPocket;
        public readonly ConfigEntry<bool> SendVoiceAudio;

        public Config(ModSettings settings)
        {
            Enabled = settings.Bind("1 - General", "Enabled", true,
                "Master switch. When false the mod installs no patches at all and the game uses its\n" +
                "own Whisper and Pocket TTS exactly as shipped.");

            Debug = settings.Bind("1 - General", "Debug", false,
                "Log every request to the companion server: URL, model, audio length, HTTP status and\n" +
                "timing. Useful when a turn is slow or a reply looks wrong.");

            AsrService = settings.BindChoice("2 - Backend", "AsrService", Api.SttService.Local,
                new[]
                {
                    Api.SttService.Local, Api.SttService.Server, Api.SttService.Deepgram,
                    Api.SttService.Gladia, Api.SttService.OpenAi
                },
                "Who transcribes the microphone.\n" +
                "Local    = Parakeet in this process, on the game's own ONNX Runtime. No account, no\n" +
                "           network, downloaded on first use.\n" +
                "Server   = a server speaking this mod's own /asr endpoint (BaseUrl below).\n" +
                "Deepgram = api.deepgram.com pre-recorded listen, model DeepgramSttModel.\n" +
                "Gladia   = api.gladia.io pre-recorded, uploaded and polled.\n" +
                "OpenAi   = /v1/audio/transcriptions, which also covers any self-hosted server that\n" +
                "           speaks that shape (faster-whisper-server, LM Studio and friends).");

            TtsService = settings.BindChoice("2 - Backend", "TtsService", Api.TtsService.Local,
                new[]
                {
                    Api.TtsService.Local, Api.TtsService.Server, Api.TtsService.ElevenLabs,
                    Api.TtsService.Deepgram, Api.TtsService.OpenAi
                },
                "Who speaks the caller's lines.\n" +
                "Local      = Kokoro in this process, voice tuned from the caller's reference clip.\n" +
                "Server     = a server speaking this mod's own /tts endpoint (BaseUrl below).\n" +
                "ElevenLabs = api.elevenlabs.io text-to-speech, voice ElevenLabsVoiceId.\n" +
                "Deepgram   = api.deepgram.com Aura speak, voice DeepgramTtsVoice.\n" +
                "OpenAi     = /v1/audio/speech, which also covers any server that speaks that shape.\n" +
                "The remote services do not clone the game's reference clips: they speak in the voice\n" +
                "configured here, for every caller.");

            AutoDownload = settings.Bind("2 - Backend", "AutoDownload", true,
                "Fetch missing model files from their public release URLs on first use. When off, the\n" +
                "files have to be placed in BepInEx/plugins/VoiceBackend/models by hand and a missing\n" +
                "file is an error with the path in it.");

            BaseUrl = settings.Bind("2 - Server", "BaseUrl", "http://127.0.0.1:8010",
                "Base URL of the server used when Backend = Remote. No trailing slash and no endpoint\n" +
                "path: AsrPath and TtsPath are appended. Example: http://127.0.0.1:8010");

            Mode = settings.BindChoice("2 - Backend", "Mode", DeviceMode.Cpu,
                new[] { DeviceMode.Auto, DeviceMode.Cpu, DeviceMode.Gpu },
                "Which execution provider ONNX Runtime is asked for.\n" +
                "Cpu  = the CPU provider. Measured on this machine: Parakeet decodes 4 s of speech in\n" +
                "       about 180 ms and Kokoro renders 2.8 s in about 540 ms.\n" +
                "Gpu  = append the game's DirectML provider. Worth trying on a discrete GPU, but the\n" +
                "       runtime the game ships is a 1.17 build and DirectML refuses some of these\n" +
                "       graphs: Kokoro fails outright on its ConvTranspose node. The mod detects that,\n" +
                "       logs it, and reloads every model on the CPU, so Gpu is safe to try - just not\n" +
                "       the default.\n" +
                "Auto = same as Gpu.");

            AsrPath = settings.Bind("2 - Server", "AsrPath", "/asr",
                "Speech-recognition endpoint, appended to BaseUrl. It receives a WAV body and returns\n" +
                "{\"text\": \"...\", \"confidence\": 0.0}. The bundled server uses /asr.");

            TtsPath = settings.Bind("2 - Server", "TtsPath", "/tts",
                "Text-to-speech endpoint, appended to BaseUrl. It receives JSON and returns a 24 kHz\n" +
                "mono WAV. The bundled server uses /tts.");

            ApiKey = settings.Bind("2 - Server", "ApiKey", "",
                "Sent as Authorization: Bearer. Leave empty for the bundled local server.\n" +
                "Whatever you put here is stored in plain text in this file.");

            AsrTimeoutSeconds = settings.Bind("2 - Server", "AsrTimeoutSeconds", 10, 1, 120,
                "How long one speech-recognition request may take. The game's own pipeline gives Whisper\n" +
                "15 seconds; this should stay below that so the failure is ours and says why.");

            TtsTimeoutSeconds = settings.Bind("2 - Server", "TtsTimeoutSeconds", 90, 1, 600,
                "How long one text-to-speech request may take. Kokoro renders the whole clip\n" +
                "before answering, so this needs headroom on a CPU-only server.");

            WarmupOnLoad = settings.Bind("2 - Server", "WarmupOnLoad", true,
                "Load the Parakeet checkpoint (and Kokoro, if TTS is on) when the game starts, when the\n" +
                "model files are already on disk. The first load of a 600 MB encoder takes a couple of\n" +
                "seconds and a first caller turn would otherwise pay for it. Downloads are not started\n" +
                "here: they wait for a turn that needs them.");

            DeepgramUrl = settings.Bind("2 - Remote API", "DeepgramUrl", "https://api.deepgram.com",
                "Deepgram base URL. Point it at a proxy or a regional endpoint if your traffic has to\n" +
                "leave through one.");

            DeepgramApiKey = settings.Bind("2 - Remote API", "DeepgramApiKey", "",
                "Deepgram API key, sent as 'Authorization: Token ...'. Stored in plain text in this\n" +
                "file; the same key serves recognition and Aura speech.");

            DeepgramSttModel = settings.Bind("2 - Remote API", "DeepgramSttModel", "nova-3",
                "Deepgram recognition model, for example nova-3 or nova-2. Language is passed from the\n" +
                "game's own language for that turn.");

            DeepgramTtsVoice = settings.Bind("2 - Remote API", "DeepgramTtsVoice", "aura-2-thalia-en",
                "Deepgram Aura voice. The voice decides the language: aura-2-thalia-en is English,\n" +
                "aura-2-celeste-es Spanish, and so on.");

            GladiaUrl = settings.Bind("2 - Remote API", "GladiaUrl", "https://api.gladia.io",
                "Gladia base URL.");

            GladiaApiKey = settings.Bind("2 - Remote API", "GladiaApiKey", "",
                "Gladia API key, sent as 'x-gladia-key'. Stored in plain text in this file.");

            ElevenLabsUrl = settings.Bind("2 - Remote API", "ElevenLabsUrl", "https://api.elevenlabs.io",
                "ElevenLabs base URL. A proxy in front of it works as long as it passes the same paths.");

            ElevenLabsApiKey = settings.Bind("2 - Remote API", "ElevenLabsApiKey", "",
                "ElevenLabs API key, sent as 'xi-api-key'. Stored in plain text in this file.");

            ElevenLabsVoiceId = settings.Bind("2 - Remote API", "ElevenLabsVoiceId",
                "21m00Tcm4TlvDq8ikWAM",
                "ElevenLabs voice id - the id from the voice's page, not its name.");

            ElevenLabsModel = settings.Bind("2 - Remote API", "ElevenLabsModel", "eleven_multilingual_v2",
                "ElevenLabs model id. eleven_multilingual_v2 covers 29 languages; eleven_turbo_v2_5 is\n" +
                "cheaper and faster and covers 32, at a lower quality.");

            OpenAiUrl = settings.Bind("2 - Remote API", "OpenAiUrl", "https://api.openai.com",
                "Base URL for the OpenAI-shaped services. Point it at a local or self-hosted server to\n" +
                "keep audio on your own machine.");

            OpenAiApiKey = settings.Bind("2 - Remote API", "OpenAiApiKey", "",
                "Bearer token for the OpenAI-shaped services. Stored in plain text in this file.");

            OpenAiSttModel = settings.Bind("2 - Remote API", "OpenAiSttModel", "whisper-1",
                "Transcription model name, for example whisper-1 or gpt-4o-mini-transcribe.");

            OpenAiTtsModel = settings.Bind("2 - Remote API", "OpenAiTtsModel", "gpt-4o-mini-tts",
                "Speech model name, for example gpt-4o-mini-tts or tts-1.");

            OpenAiTtsVoice = settings.Bind("2 - Remote API", "OpenAiTtsVoice", "alloy",
                "Voice name for the OpenAI-shaped speech service.");

            AsrEnabled = settings.Bind("3 - Speech recognition", "AsrEnabled", true,
                "Replace Whisper with Parakeet. English uses EnglishModel, the languages Parakeet v3\n" +
                "covers use EuropeanModel, and everything else follows OtherLanguages.\n" +
                "This also turns off the game's Moonshine recognizer, which otherwise bypasses Whisper\n" +
                "on machines where the game prefers it.");

            EnglishModel = settings.BindChoice("3 - Speech recognition", "EnglishModel", ParakeetModel.V2,
                new[] { ParakeetModel.V2, ParakeetModel.V3 },
                "Parakeet checkpoint for English. V2 is the English-only model and is the better choice:\n" +
                "V3 also understands English but is shared with 25 other languages.");

            EuropeanModel = settings.BindChoice("3 - Speech recognition", "EuropeanModel", ParakeetModel.V3,
                new[] { ParakeetModel.V2, ParakeetModel.V3 },
                "Parakeet checkpoint for the European languages V3 covers (Spanish, French, German,\n" +
                "Italian, Portuguese, Dutch, Polish, Czech, Russian and more). V2 only speaks English,\n" +
                "so leave this on V3.");

            OtherLanguages = settings.BindChoice("3 - Speech recognition", "OtherLanguages", OtherLanguageAsr.Whisper,
                new[] { OtherLanguageAsr.Whisper, OtherLanguageAsr.ParakeetV3 },
                "What to do for a language outside Parakeet v3's 25 (Chinese, Japanese, Korean, Arabic,\n" +
                "Hindi, Persian, Turkish, Indonesian, Hungarian and a few others).\n" +
                "Whisper      = let the game's own Whisper handle that language.\n" +
                "ParakeetV3   = send it to Parakeet v3 anyway. Fine for closely related languages,\n" +
                "               noticeably worse where the model was never trained.");

            AsrFallbackToWhisper = settings.Bind("3 - Speech recognition", "FallbackToWhisper", true,
                "If Parakeet fails, run the game's own Whisper for that request rather than\n" +
                "throwing the turn away. The failure is still counted and logged.");

            TtsEnabled = settings.Bind("4 - Text to speech", "TtsEnabled", true,
                "Replace the Pocket TTS provider with Kokoro. Languages the game routes to Qwen or\n" +
                "MOSS instead (Chinese, Japanese, Russian when configured that way) are left alone,\n" +
                "because this replaces Pocket, not every provider.");

            SkipPocketModel = settings.Bind("4 - Text to speech", "SkipPocketModel", false,
                "Do not load the game's Pocket TTS ONNX models at all. The game still creates its\n" +
                "PocketTTS component and emits frames through it, but the models never enter memory.\n" +
                "Off by default because Kokoro is English-only here: a German or Spanish line has to go\n" +
                "back to Pocket, and that needs the model loaded. Turn it on for an English-only group\n" +
                "to save the memory and the load time.");

            TtsFallbackToPocket = settings.Bind("4 - Text to speech", "FallbackToPocket", true,
                "If Kokoro cannot speak a line - wrong language, model missing, synthesis failed - let\n" +
                "the game's own Pocket TTS say it instead. Requires SkipPocketModel = false.");

            SendVoiceAudio = settings.Bind("4 - Text to speech", "SendVoiceAudio", false,
                "Send the reference voice WAV itself instead of its path. The bundled server runs on\n" +
                "the same machine and can read the path, which is smaller and faster. Turn this on when\n" +
                "the server is on another machine or inside a container that cannot see the game install.");

            ApplyLimits(settings, null);
        }

        public bool AsrActive
        {
            get { return Enabled.Value && AsrEnabled.Value; }
        }

        public bool TtsActive
        {
            get { return Enabled.Value && TtsEnabled.Value; }
        }

        public string ResolveUrl(string path)
        {
            var baseUrl = (BaseUrl.Value ?? "").Trim().TrimEnd('/');
            if (baseUrl.Length == 0)
                throw new InvalidOperationException("BaseUrl is empty; set it in the config file.");

            var suffix = (path ?? "").Trim();
            if (suffix.Length == 0) return baseUrl;
            if (!suffix.StartsWith("/", StringComparison.Ordinal)) suffix = "/" + suffix;
            return baseUrl.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + suffix;
        }

        public string ResolveUrl(ConfigEntry<string> path)
        {
            return ResolveUrl(path != null ? path.Value : null);
        }

        /// <summary>
        /// Pull every numeric setting back into a range that cannot wedge the game, and disable
        /// combinations that cannot work.
        /// </summary>
        public void ApplyLimits(ModSettings settings, ManualLogSource log)
        {
            Clamp(AsrTimeoutSeconds, settings.Int(AsrTimeoutSeconds, 1, 120));
            Clamp(TtsTimeoutSeconds, settings.Int(TtsTimeoutSeconds, 1, 600));

            if (TtsActive && SkipPocketModel.Value && log != null)
            {
                if (TtsService.Value == Api.TtsService.Local)
                {
                    log.LogWarning("SkipPocketModel is on, so every line has to come from Kokoro - and the " +
                                   "local backend only speaks English. A non-English line will have no " +
                                   "voice. Turn SkipPocketModel off to let the game's Pocket TTS cover it.");
                }
                else if (TtsService.Value != Api.TtsService.Server)
                {
                    log.LogWarning("SkipPocketModel is on, so a failed " + TtsService.Value +
                                   " request leaves the caller with no voice at all; turn it off if you want " +
                                   "the game's Pocket TTS to answer when the service is down.");
                }
            }

            if (SkipPocketModel.Value && TtsFallbackToPocket.Value)
            {
                TtsFallbackToPocket.Value = false;
                if (log != null)
                {
                    log.LogWarning("SkipPocketModel is on, so FallbackToPocket cannot work: the Pocket " +
                                   "models were never loaded. FallbackToPocket has been turned off in the " +
                                   "config file.");
                }
            }
        }

        private static void Clamp<T>(ConfigEntry<T> entry, T value) where T : IComparable
        {
            try
            {
                if (entry.Value.CompareTo(value) == 0) return;
                entry.Value = value;
            }
            catch (Exception)
            {
                // The settings object has already complained about an unreadable value.
            }
        }

        public void Describe(ManualLogSource log)
        {
            if (log == null) return;

            log.LogInfo(string.Format(
                "In effect: ASR {0} (English {1}, European {2}, other {3}), TTS {4}, device {5}, " +
                "skip Pocket model {6}, downloads {7}.",
                AsrActive ? AsrService.Value.ToString() : "off",
                EnglishModel.Value,
                EuropeanModel.Value,
                OtherLanguages.Value,
                TtsActive ? TtsService.Value.ToString() : "off",
                Mode.Value,
                SkipPocketModel.Value ? "yes" : "no",
                AutoDownload.Value ? "on" : "off"));

            if (AsrService.Value == Api.SttService.Server || TtsService.Value == Api.TtsService.Server)
            {
                log.LogInfo("  server base URL: " + SafeUrl());
            }
        }

        private string SafeUrl()
        {
            try
            {
                return string.IsNullOrEmpty(BaseUrl.Value.Trim()) ? "<not set>" : BaseUrl.Value.Trim();
            }
            catch (Exception ex)
            {
                return "unreadable: " + ex.Message;
            }
        }
    }
}
