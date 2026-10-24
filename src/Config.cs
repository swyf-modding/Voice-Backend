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

    /// <summary>Which Chatterbox checkpoint the companion server should use.</summary>
    public enum TtsModelMode
    {
        /// <summary>Turbo for English, Chatterbox Multilingual V3 for the languages Turbo does not speak.</summary>
        Auto,
        /// <summary>Always Chatterbox Turbo. English only; other languages inherit an English accent.</summary>
        Turbo,
        /// <summary>Always Chatterbox Multilingual V3.</summary>
        Multilingual
    }

    internal sealed class Config
    {
        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<bool> Debug;

        public readonly ConfigEntry<string> BaseUrl;
        public readonly ConfigEntry<string> AsrPath;
        public readonly ConfigEntry<string> TtsPath;
        public readonly ConfigEntry<string> ApiKey;
        public readonly ConfigEntry<int> AsrTimeoutSeconds;
        public readonly ConfigEntry<int> TtsTimeoutSeconds;
        public readonly ConfigEntry<bool> WarmupOnLoad;
        public readonly ConfigEntry<bool> AutoStartServer;
        public readonly ConfigEntry<string> ServerExecutable;
        public readonly ConfigEntry<int> ServerStartTimeoutSeconds;
        public readonly ConfigEntry<bool> StopServerOnExit;

        public readonly ConfigEntry<bool> AsrEnabled;
        public readonly ConfigEntry<ParakeetModel> EnglishModel;
        public readonly ConfigEntry<ParakeetModel> EuropeanModel;
        public readonly ConfigEntry<OtherLanguageAsr> OtherLanguages;
        public readonly ConfigEntry<bool> AsrFallbackToWhisper;

        public readonly ConfigEntry<bool> TtsEnabled;
        public readonly ConfigEntry<TtsModelMode> TtsModel;
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

            BaseUrl = settings.Bind("2 - Server", "BaseUrl", "http://127.0.0.1:8010",
                "Base URL of the companion server in server/ (or any server speaking the same two\n" +
                "endpoints). No trailing slash and no endpoint path: AsrPath and TtsPath are appended.\n" +
                "Example: http://127.0.0.1:8010");

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
                "How long one text-to-speech request may take. Chatterbox generates the whole clip\n" +
                "before answering, so this needs headroom on a CPU-only server.");

            WarmupOnLoad = settings.Bind("2 - Server", "WarmupOnLoad", true,
                "Ask the server to load both Parakeet checkpoints (and Chatterbox, if TTS is on) when\n" +
                "the game starts. The first model load can take minutes on a fresh install, and a\n" +
                "first caller turn would otherwise hit the game's 15-second recognition timeout.\n" +
                "Harmless when the server is not running: the failure is logged, not raised.");

            AutoStartServer = settings.Bind("2 - Server", "AutoStartServer", true,
                "Start the companion server with the game if it is not already running.\n" +
                "The server is looked for at BepInEx/plugins/VoiceBackend/VoiceBackend.Server.exe,\n" +
                "then a few nearby folders. It is a self-contained executable: Python is not needed\n" +
                "on the player's machine. A server already listening on BaseUrl is left alone.");

            ServerExecutable = settings.Bind("2 - Server", "ServerExecutable", "",
                "Explicit path to VoiceBackend.Server.exe. Empty searches the usual places.\n" +
                "A relative path is tried next to the plugins folder, then the game root.");

            ServerStartTimeoutSeconds = settings.Bind("2 - Server", "ServerStartTimeoutSeconds", 120, 5, 600,
                "How long to wait for the auto-started server to answer /health. The first launch of\n" +
                "a bundled Python runtime can take a while on a cold disk.");

            StopServerOnExit = settings.Bind("2 - Server", "StopServerOnExit", true,
                "Stop the server when the game exits, but only if this mod started it. A server that\n" +
                "was already running before the game is never touched.");

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
                "If the Parakeet server fails, run the game's own Whisper for that request rather than\n" +
                "throwing the turn away. The failure is still counted and logged.");

            TtsEnabled = settings.Bind("4 - Text to speech", "TtsEnabled", true,
                "Replace the Pocket TTS provider with Chatterbox. Languages the game routes to Qwen or\n" +
                "MOSS instead (Chinese, Japanese, Russian when configured that way) are left alone,\n" +
                "because this replaces Pocket, not every provider.");

            TtsModel = settings.BindChoice("4 - Text to speech", "TtsModel", TtsModelMode.Auto,
                new[] { TtsModelMode.Auto, TtsModelMode.Turbo, TtsModelMode.Multilingual },
                "Which Chatterbox checkpoint the server should use.\n" +
                "Auto         = Turbo for English, Chatterbox Multilingual V3 for other languages.\n" +
                "Turbo        = always Chatterbox Turbo. It is English-only, so non-English lines are\n" +
                "               spoken with an English accent.\n" +
                "Multilingual = always Chatterbox Multilingual V3, which covers 23 languages.");

            SkipPocketModel = settings.Bind("4 - Text to speech", "SkipPocketModel", true,
                "Do not load the game's Pocket TTS ONNX models at all. The game still creates its\n" +
                "PocketTTS component and emits frames through it, but the models never enter memory and\n" +
                "Chatterbox provides every sample.\n" +
                "Turn this off if you want TtsFallbackToPocket to work: fallback needs the real model.");

            TtsFallbackToPocket = settings.Bind("4 - Text to speech", "FallbackToPocket", false,
                "If the Chatterbox server fails, use the game's own Pocket TTS for that line. Requires\n" +
                "SkipPocketModel = false. Off by default, because the failure is usually a server that\n" +
                "is not running and the game recovers by letting the player type instead.");

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
            Clamp(ServerStartTimeoutSeconds, settings.Int(ServerStartTimeoutSeconds, 5, 600));

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
                "In effect: server {0}, ASR {1} (English {2}, European {3}, other {4}), TTS {5} ({6}, " +
                "skip Pocket model {7}).",
                SafeUrl(),
                AsrActive ? "Parakeet" : "off",
                EnglishModel.Value,
                EuropeanModel.Value,
                OtherLanguages.Value,
                TtsActive ? "Chatterbox" : "off",
                TtsModel.Value,
                SkipPocketModel.Value ? "yes" : "no"));
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
