using System;
using System.Collections;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine.UIElements;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// Replaces the game's Whisper speech recognition with Parakeet and its Pocket TTS provider with
    /// Chatterbox, both through a small companion server.
    /// </summary>
    /// <remarks>
    /// Two game methods cover the whole feature:
    ///
    ///   Whisper.WhisperManager.GetTextAsync(samples, frequency, channels)
    ///       The one place WhisperVcPipeline asks for a transcript. Parakeet answers it instead.
    ///
    ///   PocketTTS.PocketTTS.Prompt(text, voicePath, languageCode)
    ///       The one place PocketTtsSingleton asks for a spoken line. Chatterbox answers it, and the
    ///       returned clip is queued through PocketTTS's own frame pacer, so subtitles, cancellation,
    ///       speaker routing and tracked-completion reports all keep working.
    ///
    /// Both patches can be switched off independently in the .cfg, and the config is watched, so a
    /// hand edit puts the original provider back without a restart.
    /// </remarks>
    [BepInPlugin(PluginGuid, "Scam WYF Voice Backend", PluginBuildInfo.Version)]
    public sealed class Plugin : ScamMod
    {
        public const string PluginGuid = "com.community.scamwyf.voicebackend";

        // Unity build this was written against. A mismatch is a warning, not an error.
        private const string BuiltAgainst = "6000.3.26f1";

        internal static ManualLogSource Log;
        internal static Config Cfg;

        private static string _health = "not checked";
        private static HarmonyMethod _asrPrefix;
        private static HarmonyMethod _forceWhisperPrefix;
        private static HarmonyMethod _ttsPrefix;
        private static HarmonyMethod _initPrefix;
        private static HarmonyMethod _warmUpPrefix;
        private static HarmonyMethod _voiceReadyPrefix;
        private static HarmonyMethod _stalledPrefix;
        private static HarmonyMethod _slowTranscriptPrefix;

        protected override void OnModLoad()
        {
            Log = ModLog;
            GameBuild.CheckUnityVersion(ModLog, ModId, BuiltAgainst);

            Cfg = new Config(Settings);
            Cfg.ApplyLimits(Settings, ModLog);

            VoiceRuntime.Ensure();
            WatchConfig();
            ModMenu.AddPage(this, "Voice Backend", BuildPage, -20);

            _asrPrefix = new HarmonyMethod(AccessTools.Method(typeof(AsrClient), nameof(AsrClient.Prefix)));
            _forceWhisperPrefix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(ForceWhisper)));
            _ttsPrefix = new HarmonyMethod(AccessTools.Method(typeof(TtsClient), nameof(TtsClient.PromptPrefix)));
            _initPrefix = new HarmonyMethod(AccessTools.Method(typeof(TtsClient), nameof(TtsClient.InitModelPrefix)));
            _warmUpPrefix = new HarmonyMethod(AccessTools.Method(typeof(TtsClient), nameof(TtsClient.WarmUpPrefix)));
            _voiceReadyPrefix = new HarmonyMethod(AccessTools.Method(typeof(TtsClient), nameof(TtsClient.IsVoiceReadyPrefix)));
            _stalledPrefix = new HarmonyMethod(AccessTools.Method(typeof(TtsClient), nameof(TtsClient.HasSpeechStalledPrefix)));
            _slowTranscriptPrefix = new HarmonyMethod(AccessTools.Method(typeof(TtsClient), nameof(TtsClient.SlowTranscriptPrefix)));

            Cfg.Describe(ModLog);
            ApplyPatches();

            VoiceRuntime.Instance.StartCoroutine(Bootstrap());

            if (!Cfg.AsrActive && !Cfg.TtsActive)
                ModLog.LogInfo("Both replacements are off; the game is using its own Whisper and Pocket TTS.");
        }

        protected override void OnModUnload()
        {
            ServerLauncher.Stop();
            VoiceRuntime.Shutdown();
        }

        protected override void OnConfigReloaded()
        {
            Cfg.ApplyLimits(Settings, ModLog);
            Cfg.Describe(ModLog);
            ApplyPatches();
            ModMenu.Refresh();
        }

        private void ApplyPatches()
        {
            // A reload changes which patches should exist, so start from the same place every time.
            PatchCoordinator.UnpatchOwner(ModId);

            if (Cfg.AsrActive) InstallAsr();
            if (Cfg.TtsActive) InstallTts();
        }

        private void InstallAsr()
        {
            if (!PatchCoordinator.TryPatch(
                    this,
                    typeof(Whisper.WhisperManager),
                    "GetTextAsync",
                    new[] { typeof(float[]), typeof(int), typeof(int) },
                    "answer Whisper with Parakeet (" + LanguageMap.CodeFor(Cfg.EnglishModel.Value) + "/" +
                    LanguageMap.CodeFor(Cfg.EuropeanModel.Value) + ")",
                    _asrPrefix))
            {
                ModLog.LogWarning("Speech recognition will stay on the game's Whisper: the method this " +
                                  "mod patches is not in this build.");
            }

            // Moonshine is a second recognizer the game can prefer on some machines. It does not go
            // through WhisperManager, so it would quietly bypass the patch above.
            if (!PatchCoordinator.TryPatch(
                    this,
                    typeof(WindowsSpeechCompatibility),
                    "UseMoonshine",
                    new[] { typeof(string) },
                    "keep recognition on the Whisper path so Parakeet can answer it",
                    _forceWhisperPrefix))
            {
                ModLog.LogWarning("Could not disable Moonshine; on a machine where the game prefers it, " +
                                  "speech recognition may not go through Parakeet.");
            }
        }

        private void InstallTts()
        {
            if (!PatchCoordinator.TryPatch(
                    this,
                    typeof(global::PocketTTS.PocketTTS),
                    "Prompt",
                    new[] { typeof(string), typeof(string), typeof(string) },
                    "speak Pocket TTS lines through Chatterbox",
                    _ttsPrefix))
            {
                ModLog.LogWarning("Text to speech will stay on the game's Pocket TTS: the method this " +
                                  "mod patches is not in this build.");
                return;
            }

            if (!Cfg.SkipPocketModel.Value) return;

            PatchCoordinator.TryPatch(
                this,
                typeof(global::PocketTTS.PocketTTS),
                "InitModel",
                Type.EmptyTypes,
                "skip the Pocket TTS model load",
                _initPrefix);

            PatchCoordinator.TryPatch(
                this,
                typeof(PocketTtsSingleton),
                "WarmUpVoice",
                new[] { typeof(string) },
                "skip Pocket voice warm-up (Chatterbox needs no local voice)",
                _warmUpPrefix);

            PatchCoordinator.TryPatch(
                this,
                typeof(PocketTtsSingleton),
                "IsVoiceReady",
                new[] { typeof(string) },
                "report every voice ready; Chatterbox clones from the same WAV",
                _voiceReadyPrefix);

            // PocketTtsSingleton gives a provider 30 seconds of silence before calling the line
            // stalled. Chatterbox on a CPU can take longer than that to produce the first sample,
            // and cancelling there would throw away a line that is nearly ready.
            if (!PatchCoordinator.TryPatch(
                    this,
                    typeof(PocketTtsSingleton),
                    "HasSpeechStalled",
                    new[] { typeof(double), typeof(double), typeof(double), typeof(bool), typeof(bool) },
                    "do not time a line out while Chatterbox is still synthesising it",
                    _stalledPrefix))
            {
                ModLog.LogWarning("Could not patch the TTS stall timer; a slow CPU-only Chatterbox " +
                                  "may be cancelled after 30 seconds.");
            }

            PatchCoordinator.TryPatch(
                this,
                typeof(PocketTtsSingleton),
                "ShouldRecoverSlowTranscript",
                new[] { typeof(string), typeof(double), typeof(double), typeof(double), typeof(int), typeof(double) },
                "do not cut a line off while Chatterbox is still generating it",
                _slowTranscriptPrefix);
        }

        /// <summary>Bring the server up if needed, then warm its models.</summary>
        private IEnumerator Bootstrap()
        {
            bool ready = false;
            yield return ServerLauncher.EnsureStarted(delegate (bool ok, string message)
            {
                ready = ok;
                if (ok) ModLog.LogInfo("Voice backend server: " + message);
                else ModLog.LogWarning("Voice backend server: " + message);
            });

            if (ready && Cfg.WarmupOnLoad.Value) yield return Warmup();
            ModMenu.Refresh();
        }

        /// <summary>Load the models at game start so the first caller does not pay for it.</summary>
        private IEnumerator Warmup()
        {
            string url;
            try
            {
                var baseUrl = Cfg.BaseUrl.Value.Trim().TrimEnd('/');
                var models = Cfg.AsrActive ? "v2,v3" : "none";
                url = baseUrl + "/warmup?asr_model=" + models + "&do_tts=" + (Cfg.TtsActive ? "true" : "false");
            }
            catch (Exception)
            {
                yield break;
            }

            HttpResult result = null;
            yield return VoiceHttp.Post(url, new byte[0], "application/json", 5,
                delegate (HttpResult r) { result = r; });

            if (result != null && result.Ok)
                ModLog.LogInfo("Asked the voice backend to load its models: " + result.Text);
            else
                ModLog.LogInfo("Voice backend warm-up skipped: " +
                               (result != null ? result.Describe() : "no response"));
        }

        /// <summary>Forces the pipeline's non-Moonshine path while Parakeet is active.</summary>
        private static bool ForceWhisper(string languageCode, ref bool __result)
        {
            if (Cfg == null || !Cfg.AsrActive) return true;
            __result = false;
            return false;
        }

        // ---------------------------------------------------------------- the menu tab

        private void BuildPage(VisualElement page)
        {
            var scroll = page;

            Widgets.FieldRow(scroll, "Server", SafeUrl());
            Widgets.FieldRow(scroll, "Device mode", Cfg.Mode.Value.ToString());
            Widgets.FieldRow(scroll, "Server check", _health);
            Widgets.FieldRow(scroll, "Server process", ServerLauncher.Status);

            Widgets.Spacer(scroll, 8f);
            Widgets.Heading(scroll, "Speech recognition");

            if (!Cfg.AsrActive)
            {
                Widgets.Note(scroll, "Off. The game is using its own Whisper (and Moonshine where it " +
                                     "prefers it).");
            }
            else
            {
                Widgets.FieldRow(scroll, "Endpoint", SafeUrl(Cfg.AsrPath.Value));
                Widgets.FieldRow(scroll, "English", "Parakeet " + LanguageMap.CodeFor(Cfg.EnglishModel.Value));
                Widgets.FieldRow(scroll, "European", "Parakeet " + LanguageMap.CodeFor(Cfg.EuropeanModel.Value));
                Widgets.FieldRow(scroll, "Other languages", Cfg.OtherLanguages.Value == OtherLanguageAsr.ParakeetV3
                    ? "Parakeet v3"
                    : "the game's Whisper");
                Widgets.FieldRow(scroll, "Requests", Stats.AsrRequests.ToString());
                Widgets.FieldRow(scroll, "Whisper fallbacks", Stats.AsrFallbacks.ToString());
                Widgets.FieldRow(scroll, "Failures", Stats.AsrFailures.ToString());
                if (!string.IsNullOrEmpty(Stats.LastAsrOutcome))
                    Widgets.FieldRow(scroll, "Last request", Stats.LastAsrOutcome);
                if (!string.IsNullOrEmpty(Stats.LastAsrError))
                    Widgets.Note(scroll, "Last error: " + Stats.LastAsrError, true, true);
            }

            Widgets.Spacer(scroll, 8f);
            Widgets.Heading(scroll, "Text to speech");

            if (!Cfg.TtsActive)
            {
                Widgets.Note(scroll, "Off. The game is using its own Pocket TTS, Qwen TTS or MOSS TTS " +
                                     "depending on the language.");
            }
            else
            {
                Widgets.FieldRow(scroll, "Endpoint", SafeUrl(Cfg.TtsPath.Value));
                Widgets.FieldRow(scroll, "Model", Cfg.TtsModel.Value.ToString());
                Widgets.FieldRow(scroll, "Pocket model", Cfg.SkipPocketModel.Value
                    ? "not loaded"
                    : "loaded (fallback available)");
                Widgets.FieldRow(scroll, "Requests", Stats.TtsRequests.ToString());
                Widgets.FieldRow(scroll, "Failures", Stats.TtsFailures.ToString());
                if (!string.IsNullOrEmpty(Stats.LastTtsOutcome))
                    Widgets.FieldRow(scroll, "Last request", Stats.LastTtsOutcome);
                if (!string.IsNullOrEmpty(Stats.LastTtsError))
                    Widgets.Note(scroll, "Last error: " + Stats.LastTtsError, true, true);
            }

            Widgets.Spacer(scroll, 6f);

            var row = Widgets.WrapRow(scroll);
            Widgets.DescribedButton(row, "Check server", "Ask the companion server for /health", delegate
            {
                _health = "checking...";
                ModMenu.Refresh();
                VoiceRuntime.Instance.StartCoroutine(CheckHealth());
            });

            Widgets.DescribedButton(row, "Start server", "Launch the bundled server now", delegate
            {
                VoiceRuntime.Instance.StartCoroutine(ServerLauncher.EnsureStarted(delegate (bool ok, string message)
                {
                    ModLog.LogInfo("Voice backend server: " + message);
                    ModMenu.Refresh();
                }));
            });

            Widgets.DescribedButton(row, "Stop server", "Stop a server this mod started", delegate
            {
                ServerLauncher.Stop(true);
                ModLog.LogInfo("Voice backend server stopped from the menu.");
                ModMenu.Refresh();
            });

            Widgets.DescribedButton(row, "Re-read config", "Reload the file now", delegate { ReloadConfig(); });

            Widgets.Spacer(scroll, 8f);
            var fold = Widgets.Section(scroll, "All settings", false);
            var body = Widgets.SectionBody(fold);
            if (body == null)
            {
                ConfigEditor.Build(scroll);
                return;
            }

            Widgets.Paragraph(body,
                "Editing here writes straight to " + Config.ConfigFilePath +
                ". Editing that file while the game is running works too: it is watched, and the change " +
                "is picked up within about half a second.");
            ConfigEditor.Build(body);
        }

        private IEnumerator CheckHealth()
        {
            string url;
            try
            {
                var baseUrl = Cfg.BaseUrl.Value.Trim().TrimEnd('/');
                url = baseUrl + "/health";
            }
            catch (Exception ex)
            {
                _health = "unreadable BaseUrl: " + ex.Message;
                ModMenu.Refresh();
                yield break;
            }

            HttpResult result = null;
            yield return VoiceHttp.Get(url, 10, delegate (HttpResult r) { result = r; });

            if (result != null && result.Ok && !string.IsNullOrEmpty(result.Text))
            {
                try
                {
                    var health = JObject.Parse(result.Text);
                    var device = health["device"] as JObject;
                    if (device != null)
                    {
                        _health = string.Format("torch={0} ({1}), asr={2}",
                            device["torch"],
                            device["torch_backend"],
                            device["onnx_active"] != null
                                ? device["onnx_active"].ToString(Formatting.None)
                                : "?");
                    }
                    else
                    {
                        _health = Shorten(result.Text, 160);
                    }
                }
                catch (Exception)
                {
                    _health = Shorten(result.Text, 160);
                }
                ModLog.LogInfo("Voice backend health: " + result.Text);
            }
            else
            {
                _health = result != null ? result.Describe() : "no response";
                ModLog.LogWarning("Voice backend health check failed: " + _health);
            }

            ModMenu.Refresh();
        }

        private static string SafeUrl(string path = null)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return Cfg.ResolveUrl(Cfg.AsrPath);
                return Cfg.ResolveUrl(path);
            }
            catch (Exception ex)
            {
                return "unreadable: " + ex.Message;
            }
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var oneLine = text.Replace("\r", " ").Replace("\n", " ");
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "...";
        }
    }
}
