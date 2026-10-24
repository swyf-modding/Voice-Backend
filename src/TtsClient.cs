using System;
using System.Collections;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// Replaces PocketTTS.Prompt with a Chatterbox synthesis, then feeds the result back through the
    /// game's own PocketTTS frame machinery.
    /// </summary>
    /// <remarks>
    /// The important decision here is where to join the two. PocketTTS already owns a small audio
    /// queue, a frame pacer, sequence numbers and word timings; PocketTtsSingleton owns the request
    /// queue, cancellation, first-audio timeout, tracked-completion reports and speaker routing.
    /// Synthesising into that queue means every one of those still works, and the only thing that
    /// changed is where the samples came from.
    ///
    /// The stock model can be skipped entirely (SkipPocketModel). PocketTTS is then faked into Ready
    /// at InitModel, which is the state PrepareLegacyProvider waits for, and no ONNX session is ever
    /// created. The game still emits frames through the same component.
    /// </remarks>
    internal static class TtsClient
    {
        [ThreadStatic] private static bool _bypass;
        private static bool _loggedSkip;
        private static bool _loggedFallback;
        private static bool _synthesizing;

        public static bool Synthesizing
        {
            get { return _synthesizing; }
        }

        /// <summary>True while a Chatterbox request is in flight; the game's 30s stall timer waits.</summary>
        public static bool HasSpeechStalledPrefix(ref bool __result)
        {
            if (Plugin.Cfg == null || !Plugin.Cfg.TtsActive || !_synthesizing) return true;
            __result = false;
            return false;
        }

        /// <summary>
        /// The game's slow-non-English recovery starts revealing the known transcript and then cuts
        /// the line off. That is the right trade for a slow local provider already streaming audio;
        /// it is the wrong trade while Chatterbox is still generating the first sample.
        /// </summary>
        public static bool SlowTranscriptPrefix(ref bool __result)
        {
            if (Plugin.Cfg == null || !Plugin.Cfg.TtsActive || !_synthesizing) return true;
            __result = false;
            return false;
        }

        public static bool PromptPrefix(global::PocketTTS.PocketTTS __instance, string prompt, string voicePath,
            string languageCode, ref bool __result)
        {
            if (_bypass) return true;
            if (Plugin.Cfg == null || !Plugin.Cfg.TtsActive || __instance == null) return true;
            if (!PocketBridge.Available)
            {
                if (!_loggedFallback)
                {
                    _loggedFallback = true;
                    Plugin.Log.LogWarning("PocketTTS internals are not where this build expects them; " +
                                          "leaving the stock provider in place.");
                }
                return true;
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                __result = false;
                return false;
            }

            var language = string.IsNullOrEmpty(languageCode) ? "en" : languageCode;
            var resolvedVoice = PocketBridge.ToFullPath(PocketBridge.ResolveVoicePath(__instance, voicePath));

            Stats.TtsRequest();
            _synthesizing = true;
            PocketBridge.Begin(__instance, null);
            var coroutine = VoiceRuntime.Instance.StartCoroutine(Run(__instance, prompt, voicePath, resolvedVoice, language));
            PocketBridge.SetActivePrompt(__instance, coroutine);
            if (!_synthesizing) PocketBridge.SetActivePrompt(__instance, null);

            __result = true;
            return false;
        }

        /// <summary>Skip the stock ONNX model entirely; PocketTTS only has to be Ready, not loaded.</summary>
        public static bool InitModelPrefix(global::PocketTTS.PocketTTS __instance)
        {
            if (!ShouldSkipStockModel()) return true;

            PocketBridge.PrepareFallback(__instance);
            if (!_loggedSkip)
            {
                _loggedSkip = true;
                Plugin.Log.LogInfo("Skipping the Pocket TTS ONNX model; Chatterbox provides every sample.");
            }
            return false;
        }

        public static bool WarmUpPrefix()
        {
            return Plugin.Cfg == null || !Plugin.Cfg.TtsActive;
        }

        public static bool IsVoiceReadyPrefix(ref bool __result)
        {
            if (Plugin.Cfg == null || !Plugin.Cfg.TtsActive) return true;
            __result = true;
            return false;
        }

        private static bool ShouldSkipStockModel()
        {
            return Plugin.Cfg != null && Plugin.Cfg.TtsActive && Plugin.Cfg.SkipPocketModel.Value;
        }

        private static IEnumerator Run(global::PocketTTS.PocketTTS instance, string prompt, string originalVoicePath,
            string resolvedVoice, string language)
        {
            try
            {
            var url = "";
            byte[] payload = null;
            string buildError = null;

            try
            {
                url = Plugin.Cfg.ResolveUrl(Plugin.Cfg.TtsPath);
                var body = new JObject
                {
                    ["text"] = prompt,
                    ["language"] = LanguageMap.ChatterboxLanguage(language),
                    ["model"] = Plugin.Cfg.TtsModel.Value.ToString().ToLowerInvariant(),
                    ["voice_path"] = resolvedVoice ?? ""
                };

                if (Plugin.Cfg.SendVoiceAudio.Value && !string.IsNullOrEmpty(resolvedVoice))
                {
                    try
                    {
                        body["voice_b64"] = Convert.ToBase64String(File.ReadAllBytes(resolvedVoice));
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("Could not read the reference voice '" + resolvedVoice + "': " + ex.Message);
                    }
                }

                payload = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));

                if (Plugin.Cfg.Debug.Value)
                {
                    Plugin.Log.LogInfo(string.Format("TTS {0} language={1} voice={2} chars={3} bytes={4}",
                        Plugin.Cfg.TtsModel.Value, language, resolvedVoice, prompt.Length, payload.Length));
                }
            }
            catch (Exception ex)
            {
                buildError = ex.Message;
            }

            if (buildError != null)
            {
                FailOrFallBack(instance, prompt, originalVoicePath, language, "Could not build the request: " + buildError);
                yield break;
            }

            HttpResult response = null;
            yield return VoiceHttp.Post(url, payload, "application/json", Plugin.Cfg.TtsTimeoutSeconds.Value,
                delegate (HttpResult result) { response = result; });

            if (PocketBridge.Discarding(instance))
            {
                PocketBridge.Finish(instance);
                yield break;
            }

            if (response == null || !response.Ok)
            {
                var reason = response != null ? response.Describe() : "no response";
                FailOrFallBack(instance, prompt, originalVoicePath, language, reason);
                yield break;
            }

            float[] audio;
            int rate;
            try
            {
                audio = WavCodec.DecodeMono(response.Body, out rate);
                audio = WavCodec.ResampleMono(audio, rate, WavCodec.PocketSampleRate);
            }
            catch (Exception ex)
            {
                FailOrFallBack(instance, prompt, originalVoicePath, language, "Unreadable WAV: " + ex.Message);
                yield break;
            }

            if (audio == null || audio.Length == 0)
            {
                FailOrFallBack(instance, prompt, originalVoicePath, language, "Chatterbox returned no audio.");
                yield break;
            }

            if (PocketBridge.Discarding(instance))
            {
                PocketBridge.Finish(instance);
                yield break;
            }

            try
            {
                PocketBridge.Emit(instance, audio, prompt);
                Stats.TtsSuccess("Chatterbox " + Plugin.Cfg.TtsModel.Value + " (" + language + ", " +
                                 (audio.Length / (double)WavCodec.PocketSampleRate).ToString("F1") + "s)");
            }
            catch (Exception ex)
            {
                FailOrFallBack(instance, prompt, originalVoicePath, language, "Could not queue the clip: " + ex.Message);
                yield break;
            }

            // PocketTTS.Update dispatches at real-time pace. Stay in Generate until the queue has
            // actually played out, or PocketTtsSingleton will stop the caller mid-sentence.
            var timeoutAt = Time.realtimeSinceStartupAsDouble + 30.0;
            while (PocketBridge.QueuedSamples(instance) > 0 && Time.realtimeSinceStartupAsDouble < timeoutAt)
            {
                if (PocketBridge.Discarding(instance))
                {
                    PocketBridge.Finish(instance);
                    yield break;
                }
                yield return null;
            }

            PocketBridge.Finish(instance);
            }
            finally
            {
                _synthesizing = false;
            }
        }

        private static void FailOrFallBack(global::PocketTTS.PocketTTS instance, string prompt,
            string originalVoicePath, string language, string reason)
        {
            Stats.TtsFailure(reason);
            Plugin.Log.LogWarning("Chatterbox request failed: " + reason);

            var fallback = Plugin.Cfg.TtsFallbackToPocket.Value && !Plugin.Cfg.SkipPocketModel.Value;
            if (!fallback)
            {
                PocketBridge.Fail(instance, reason);
                return;
            }

            PocketBridge.PrepareFallback(instance);
            try
            {
                _bypass = true;
                var accepted = instance.Prompt(prompt, originalVoicePath, language);
                if (!accepted)
                {
                    PocketBridge.Fail(instance, reason + " (the stock Pocket TTS also declined)");
                    return;
                }

                Plugin.Log.LogInfo("Chatterbox failed; the stock Pocket TTS is speaking this line instead.");
                Stats.TtsSuccess("Pocket (fallback)");
            }
            catch (Exception ex)
            {
                PocketBridge.Fail(instance, reason + "; fallback threw " + ex.Message);
            }
            finally
            {
                _bypass = false;
            }
        }
    }
}
