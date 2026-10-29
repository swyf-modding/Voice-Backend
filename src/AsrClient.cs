using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Whisper;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// Replaces the game's Whisper call with a request to the Parakeet server.
    /// </summary>
    /// <remarks>
    /// WhisperVcPipeline does its own voice-activity detection, buffering, partial inference and
    /// transcript events. It then asks one WhisperManager for text. Patching exactly that call keeps
    /// all of the pipeline's behaviour and changes only which model produced the text, which is also
    /// why this is a much smaller change than replacing the pipeline would be.
    ///
    /// The prefix returns a Task immediately. The HTTP call itself runs in a coroutine on
    /// VoiceRuntime and completes the Task on the main thread, so the pipeline's await resumes where
    /// it would have resumed after a native Whisper inference.
    /// </remarks>
    internal static class AsrClient
    {
        private const int SampleRate = 16000;

        [ThreadStatic] private static bool _bypass;

        public static bool PrefixDynamic(object __instance, MethodBase __originalMethod, object[] __args,
            ref Task<WhisperResult> __result)
        {
            if (_bypass) return true;
            if (Plugin.Cfg == null || !Plugin.Cfg.AsrActive) return true;

            if (__args == null || __args.Length < 3) return true;
            var samples = __args[0] as float[];
            if (samples == null) return true;

            int frequency;
            int channels;
            if (!TryReadInt(__args[1], out frequency) || !TryReadInt(__args[2], out channels)) return true;

            var language = LanguageMap.Normalize(ReadLanguage(__instance));
            ParakeetModel? chosen;
            try { chosen = LanguageMap.ModelFor(language, Plugin.Cfg); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not choose a Parakeet model: " + ex.Message);
                return true;
            }

            // Outside v3's languages, and the player chose Whisper: let the original run.
            if (!chosen.HasValue) return true;

            if (samples == null || samples.Length < SampleRate / 5)
            {
                // Too short to contain a word. Hand the pipeline an empty result without a round trip.
                __result = Task.FromResult(EmptyResult(language));
                return false;
            }

            int actualRate = frequency > 0 ? frequency : SampleRate;
            int actualChannels = channels > 0 ? channels : 1;
            float[] mono;
            try
            {
                mono = actualRate == SampleRate && actualChannels == 1
                    ? samples
                    : Whisper.Utils.AudioUtils.Preprocess(samples, actualRate, actualChannels, SampleRate);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not prepare microphone audio for Parakeet: " + ex.Message);
                return true;
            }

            if (mono == null || mono.Length < SampleRate / 5)
            {
                __result = Task.FromResult(EmptyResult(language));
                return false;
            }

            Stats.AsrRequest();
            __result = Request(__instance, __originalMethod, __args, mono, language, chosen.Value);
            return false;
        }

        private static Task<WhisperResult> Request(object instance, MethodBase originalMethod, object[] originalArgs,
            float[] mono, string language, ParakeetModel model)
        {
            var completion = new TaskCompletionSource<WhisperResult>();

            try
            {
                if (Plugin.Cfg.Debug.Value)
                {
                    Plugin.Log.LogInfo(string.Format("ASR {0} language={1} audio={2:F2}s",
                        model, language, mono.Length / (double)SampleRate));
                }

                VoiceRuntime.Instance.StartCoroutine(VoiceBackend.Transcribe(mono, language, model,
                    delegate (string text, float confidence)
                    {
                        Stats.AsrSuccess("Parakeet " + LanguageMap.CodeFor(model) + " (" + language + ")");
                        completion.TrySetResult(BuildResult(text, confidence, language, mono.Length));
                    },
                    delegate (string error)
                    {
                        Stats.AsrFailure(error);
                        Plugin.Log.LogWarning("Parakeet failed: " + error);

                        if (Plugin.Cfg.AsrFallbackToWhisper.Value)
                        {
                            FallBackToWhisper(instance, originalMethod, originalArgs, completion, error);
                            return;
                        }

                        completion.TrySetException(new InvalidOperationException(
                            "Parakeet speech recognition failed: " + error));
                    }));
            }
            catch (Exception ex)
            {
                Stats.AsrFailure(ex.Message);
                completion.TrySetException(ex);
            }

            return completion.Task;
        }

        private static void FallBackToWhisper(object instance, MethodBase originalMethod, object[] originalArgs,
            TaskCompletionSource<WhisperResult> completion, string reason)
        {
            Stats.AsrFallback(reason);
            Plugin.Log.LogWarning("Falling back to the game's Whisper for this request.");

            Task<WhisperResult> fallback;
            try
            {
                _bypass = true;
                fallback = originalMethod.Invoke(instance, originalArgs) as Task<WhisperResult>;
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                return;
            }
            finally
            {
                _bypass = false;
            }

            if (fallback == null)
            {
                completion.TrySetException(new InvalidOperationException("Whisper returned no task."));
                return;
            }

            fallback.ContinueWith(delegate (Task<WhisperResult> task)
            {
                if (task.IsCanceled) completion.TrySetCanceled();
                else if (task.IsFaulted)
                {
                    var error = task.Exception != null && task.Exception.InnerException != null
                        ? task.Exception.InnerException
                        : task.Exception;
                    completion.TrySetException(error);
                }
                else completion.TrySetResult(task.Result);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private static WhisperResult EmptyResult(string language)
        {
            return BuildResult("", 0f, language, 0);
        }

        private static WhisperResult BuildResult(string text, float confidence, string language, int sampleCount)
        {
            var segments = new List<WhisperSegment>();
            if (!string.IsNullOrEmpty(text))
            {
                // WhisperSegment's timestamps are centiseconds (10 ms units), not samples.
                var end = (ulong)Math.Max(1, (sampleCount * 10L) / SampleRate);
                segments.Add(new WhisperSegment(0, text, 0, end));
            }

            var result = new WhisperResult(segments, 0);
            return result;
        }

        private static string ReadLanguage(object instance)
        {
            if (instance == null) return null;

            try
            {
                var type = instance.GetType();
                var field = type.GetField("language", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    var value = field.GetValue(instance) as string;
                    if (!string.IsNullOrEmpty(value)) return value;
                }

                var property = type.GetProperty("language", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.PropertyType == typeof(string))
                {
                    var value = property.GetValue(instance, null) as string;
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not read recognizer language: " + ex.Message);
            }

            return null;
        }

        private static bool TryReadInt(object value, out int number)
        {
            if (value is int)
            {
                number = (int)value;
                return true;
            }

            if (value is long)
            {
                number = (int)(long)value;
                return true;
            }

            if (value is short)
            {
                number = (short)value;
                return true;
            }

            number = 0;
            return false;
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= 500 ? text : text.Substring(0, 500) + "...";
        }
    }
}
