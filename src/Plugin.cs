using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScamWYF.VoiceBackend.Api;
using ScamWYF.Modding.Core;
using ScamWYF.Modding.Core.Ui;
using UnityEngine.UIElements;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// Replaces the game's Whisper speech recognition with Parakeet and its Pocket TTS provider with
    /// Kokoro, both in this process.
    /// </summary>
    /// <remarks>
    /// Two game methods cover the whole feature:
    ///
    ///   Whisper.WhisperManager.GetTextAsync(samples, frequency, channels)
    ///       The one place WhisperVcPipeline asks for a transcript. Parakeet answers it instead.
    ///
    ///   PocketTTS.PocketTTS.Prompt(text, voicePath, languageCode)
    ///       The one place PocketTtsSingleton asks for a spoken line. Kokoro answers it, and the
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

        /// <summary>
        /// Where the plugin's own files live: the models it downloads and the assets it ships
        /// (inno-kokoro tuner, English lexicon). Resolved once, next to the assembly.
        /// </summary>
        internal static string PluginDirectory
        {
            get
            {
                if (_pluginDirectory == null)
                {
                    string assembly = typeof(Plugin).Assembly.Location;
                    _pluginDirectory = string.IsNullOrEmpty(assembly)
                        ? System.IO.Path.Combine(Environment.CurrentDirectory, "BepInEx", "plugins", "VoiceBackend")
                        : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(assembly), "VoiceBackend");
                }
                return _pluginDirectory;
            }
        }

        private static string _pluginDirectory;

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

            _asrPrefix = new HarmonyMethod(AccessTools.Method(typeof(AsrClient), nameof(AsrClient.PrefixDynamic)));
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
            VoiceBackend.Shutdown();
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
            int patched = PatchAsrMethods();
            if (patched == 0)
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

        private int PatchAsrMethods()
        {
            int patched = 0;
            var seen = new HashSet<MethodBase>();

            foreach (var target in AsrTargets())
            {
                if (target == null || !seen.Add(target)) continue;

                if (PatchCoordinator.TryPatch(
                        this,
                        target,
                        "answer speech recognition with Parakeet (" + LanguageMap.CodeFor(Cfg.EnglishModel.Value) + "/" +
                        LanguageMap.CodeFor(Cfg.EuropeanModel.Value) + ")",
                        _asrPrefix))
                {
                    patched++;
                }
            }

            return patched;
        }

        private static IEnumerable<MethodBase> AsrTargets()
        {
            var manager = typeof(Whisper.WhisperManager);
            var assembly = manager.Assembly;
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
            }

            if (types == null) yield break;

            for (int i = 0; i < types.Length; i++)
            {
                var type = types[i];
                if (type == null) continue;

                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.Instance | BindingFlags.Static);
                }
                catch (Exception)
                {
                    continue;
                }

                for (int j = 0; j < methods.Length; j++)
                {
                    var method = methods[j];
                    if (!IsAsrTarget(method)) continue;
                    yield return method;
                }
            }
        }

        private static bool IsAsrTarget(MethodInfo method)
        {
            if (method == null) return false;
            if (!string.Equals(method.Name, "GetTextAsync", StringComparison.Ordinal)) return false;
            if (method.ReturnType != typeof(Task<Whisper.WhisperResult>)) return false;

            var args = method.GetParameters();
            if (args.Length < 3) return false;
            return args[0].ParameterType == typeof(float[]) &&
                   args[1].ParameterType == typeof(int) &&
                   args[2].ParameterType == typeof(int);
        }

        private void InstallTts()
        {
            if (!PatchCoordinator.TryPatch(
                    this,
                    typeof(global::PocketTTS.PocketTTS),
                    "Prompt",
                    new[] { typeof(string), typeof(string), typeof(string) },
                    "speak Pocket TTS lines through Kokoro",
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
                "skip Pocket voice warm-up (Kokoro clones from the reference clip)",
                _warmUpPrefix);

            PatchCoordinator.TryPatch(
                this,
                typeof(PocketTtsSingleton),
                "IsVoiceReady",
                new[] { typeof(string) },
                "report every voice ready; Kokoro clones from the same WAV",
                _voiceReadyPrefix);

            // PocketTtsSingleton gives a provider 30 seconds of silence before calling the line
            // stalled. Kokoro on a CPU can take longer than that to produce the first sample,
            // and cancelling there would throw away a line that is nearly ready.
            if (!PatchCoordinator.TryPatch(
                    this,
                    typeof(PocketTtsSingleton),
                    "HasSpeechStalled",
                    new[] { typeof(double), typeof(double), typeof(double), typeof(bool), typeof(bool) },
                    "do not time a line out while Kokoro is still synthesising it",
                    _stalledPrefix))
            {
                ModLog.LogWarning("Could not patch the TTS stall timer; a slow CPU-only Kokoro " +
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

        /// <summary>Build the backend, then load whatever the configuration says will be needed.</summary>
        private IEnumerator Bootstrap()
        {
            try
            {
                VoiceBackend.Create();
                ModLog.LogInfo("Voice backend: " + VoiceBackend.Describe());
            }
            catch (Exception ex)
            {
                ModLog.LogWarning("Could not build the voice backend: " + ex.Message);
                ModMenu.Refresh();
                yield break;
            }

            if (Cfg.WarmupOnLoad.Value) yield return VoiceBackend.WarmUp();

            if (Environment.GetEnvironmentVariable("VOICEBACKEND_SELFTEST") == "1")
            {
                yield return SelfTest();
            }

            ModMenu.Refresh();
        }

        /// <summary>
        /// End-to-end check of the local backend inside the game process: synthesise a known line,
        /// feed the audio back into Parakeet, and log what came out. Run with
        /// <c>VOICEBACKEND_SELFTEST=1</c> in the environment and no arguments beyond
        /// <c>-batchmode -nographics</c>; it downloads whatever the configuration needs first.
        /// </summary>
        private IEnumerator SelfTest()
        {
            const string sentence = "The quick brown fox jumps over the lazy dog.";
            ModLog.LogInfo("Self test: starting with " + VoiceBackend.Describe());

            float[] audio = null;
            int rate = 0;
            string detail = null;
            string error = null;
            long started = Environment.TickCount;
            yield return VoiceBackend.Synthesize(sentence, "en", null,
                delegate (float[] samples, int sampleRate, string source)
                {
                    audio = samples;
                    rate = sampleRate;
                    detail = source;
                },
                delegate (string reason) { error = reason; });

            if (error != null || audio == null || audio.Length == 0)
            {
                ModLog.LogError("Self test: synthesis failed: " + (error ?? "no audio"));
                yield break;
            }

            ModLog.LogInfo(string.Format("Self test: {0} rendered {1:F2} s at {2} Hz in {3} ms", detail,
                audio.Length / (double)rate, rate, Environment.TickCount - started));

            float[] mono = WavCodec.ResampleMono(audio, rate, 16000);
            string text = null;
            error = null;
            started = Environment.TickCount;
            yield return VoiceBackend.Transcribe(mono, "en", Cfg.EnglishModel.Value,
                delegate (string result, float confidence) { text = result; },
                delegate (string reason) { error = reason; });

            if (error != null)
            {
                ModLog.LogError("Self test: recognition failed: " + error);
                yield break;
            }

            ModLog.LogInfo(string.Format("Self test: heard \"{0}\" ({1} ms) for \"{2}\"", text,
                Environment.TickCount - started, sentence));
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

            Widgets.FieldRow(scroll, "Backend", VoiceBackend.Describe());
            Widgets.FieldRow(scroll, "Device mode", Cfg.Mode.Value.ToString());
            Widgets.FieldRow(scroll, "Models", _health);

            Widgets.Spacer(scroll, 8f);
            Widgets.Heading(scroll, "Speech recognition");

            if (!Cfg.AsrActive)
            {
                Widgets.Note(scroll, "Off. The game is using its own Whisper (and Moonshine where it " +
                                     "prefers it).");
            }
            else
            {
                if (Cfg.AsrService.Value == SttService.Server)
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
                if (Cfg.TtsService.Value == TtsService.Server)
                    Widgets.FieldRow(scroll, "Endpoint", SafeUrl(Cfg.TtsPath.Value));
                Widgets.FieldRow(scroll, "Model", "Kokoro 82M");
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
            Widgets.DescribedButton(row, "Check models", "Report which model files are present and loaded", delegate
            {
                _health = "checking...";
                ModMenu.Refresh();
                VoiceBackend.Check(delegate (string message) { _health = message; ModMenu.Refresh(); });
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
