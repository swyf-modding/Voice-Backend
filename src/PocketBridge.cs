using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// The private parts of PocketTTS the replacement needs.
    /// </summary>
    /// <remarks>
    /// PocketTTS exposes Prompt publicly and keeps everything else it needs to play a clip private:
    /// status, the generated-audio queue, the completion flag, the voice-path resolver. Those are
    /// still the right things to drive, because the game's own PocketTtsSingleton and
    /// DispatchGeneratedAudioFrames already do the frame pacing, subtitle timing, cancellation and
    /// speaker routing. Reimplementing any of that would be a second copy to keep in step with the
    /// game.
    ///
    /// Every member is cached once. A missing member is reported at load rather than throwing on
    /// every spoken line, so a game update that moves one is visible rather than mysterious.
    /// </remarks>
    internal static class PocketBridge
    {
        private static readonly Type Type = typeof(global::PocketTTS.PocketTTS);

        private static readonly MethodInfo SetStatus = PropertySetter("Status");
        private static readonly MethodInfo SetComplete = PropertySetter("IsAudioGenerationComplete");
        private static readonly MethodInfo ResolvePath = Method("ResolveVoicePath");
        private static readonly MethodInfo ClearAudio = Method("ClearGeneratedAudio");
        private static readonly MethodInfo EmitResponse = Method("OnResponseGenerated");
        private static readonly MethodInfo PadFinalFrame = Method("PadFinalAudioFrame");

        private static readonly FieldInfo DiscardField = Field("discardPlayback");
        private static readonly FieldInfo AwaitingFirstFrameField = Field("awaitingFirstFrame");
        private static readonly FieldInfo GeneratedSamplesField = Field("generatedAudioSamples");
        private static readonly FieldInfo QueuedSamplesField = Field("queuedAudioSampleCount");
        private static readonly FieldInfo ActivePromptField = Field("activePromptCoroutine");
        private static readonly FieldInfo PromptStartedField = Field("promptStartedAt");
        private static readonly FieldInfo GenerationStartedField = Field("generationStartedAt");

        public static bool Available
        {
            get
            {
                return SetStatus != null && SetComplete != null && EmitResponse != null &&
                       ClearAudio != null && PadFinalFrame != null && ResolvePath != null &&
                       DiscardField != null && QueuedSamplesField != null;
            }
        }

        public static string ResolveVoicePath(global::PocketTTS.PocketTTS instance, string voicePath)
        {
            if (instance == null || string.IsNullOrEmpty(voicePath)) return voicePath;
            try
            {
                return (string)ResolvePath.Invoke(instance, new object[] { voicePath });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not resolve voice path '" + voicePath + "': " + ex.Message);
                return voicePath;
            }
        }

        /// <summary>
        /// PocketTTS normalises a path under StreamingAssets to a path relative to it. The server
        /// does not share the game's working directory, so send the absolute form.
        /// </summary>
        public static string ToFullPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try
            {
                var full = global::PocketTTS.PocketTTS.GetStreamingAssetsFullPath(path);
                return string.IsNullOrEmpty(full) ? path : full;
            }
            catch (Exception)
            {
                return path;
            }
        }

        public static void Begin(global::PocketTTS.PocketTTS instance, Coroutine coroutine)
        {
            SetField(ActivePromptField, instance, coroutine);

            try { ClearAudio.Invoke(instance, null); }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not clear PocketTTS audio: " + ex.Message); }

            SetStatusValue(instance, global::PocketTTS.ModelStatus.Generate);
            SetCompleteValue(instance, false);
            SetField(DiscardField, instance, false);
            SetField(AwaitingFirstFrameField, instance, true);
            SetField(GeneratedSamplesField, instance, 0L);

            var now = Time.realtimeSinceStartupAsDouble;
            SetField(PromptStartedField, instance, now);
            SetField(GenerationStartedField, instance, now);
        }

        public static void SetActivePrompt(global::PocketTTS.PocketTTS instance, Coroutine coroutine)
        {
            SetField(ActivePromptField, instance, coroutine);
        }

        public static void Emit(global::PocketTTS.PocketTTS instance, float[] audio, string text)
        {
            if (audio == null || audio.Length == 0) return;

            var words = BuildWords(text, audio.Length);
            var response = new global::PocketTTS.PocketTtsAudioResponse(audio, audio.Length, words);

            try
            {
                EmitResponse.Invoke(instance, new object[] { response });
                PadFinalFrame.Invoke(instance, null);
                SetCompleteValue(instance, true);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not hand the Chatterbox clip to PocketTTS: " + ex);
                throw;
            }
        }

        public static bool Discarding(global::PocketTTS.PocketTTS instance)
        {
            return ReadField<bool>(DiscardField, instance);
        }

        public static int QueuedSamples(global::PocketTTS.PocketTTS instance)
        {
            return ReadField<int>(QueuedSamplesField, instance);
        }

        public static void Finish(global::PocketTTS.PocketTTS instance)
        {
            SetField<Coroutine>(ActivePromptField, instance, null);
            SetStatusValue(instance, global::PocketTTS.ModelStatus.Ready);
        }

        public static void PrepareFallback(global::PocketTTS.PocketTTS instance)
        {
            try { ClearAudio.Invoke(instance, null); }
            catch (Exception) { }
            SetCompleteValue(instance, false);
            SetField(DiscardField, instance, false);
            SetField<Coroutine>(ActivePromptField, instance, null);
            SetStatusValue(instance, global::PocketTTS.ModelStatus.Ready);
        }

        public static void Fail(global::PocketTTS.PocketTTS instance, string reason)
        {
            if (instance == null) return;
            try { ClearAudio.Invoke(instance, null); }
            catch (Exception) { }
            SetCompleteValue(instance, false);
            SetField<Coroutine>(ActivePromptField, instance, null);
            SetStatusValue(instance, global::PocketTTS.ModelStatus.Error);
            Debug.LogWarning("[VoiceBackend] Chatterbox failed: " + reason);
        }

        /// <summary>
        /// Rough word timings for the caller's live subtitle. Chatterbox does not return them, so the
        /// clip's duration is divided in proportion to word length. Getting within a few frames is
        /// enough for PocketTtsSingleton, which only uses them to advance transcriptText.
        /// </summary>
        public static global::PocketTTS.PocketTtsTranscriptWord[] BuildWords(string text, int sampleCount)
        {
            if (sampleCount <= 0 || string.IsNullOrEmpty(text))
                return new global::PocketTTS.PocketTtsTranscriptWord[0];

            var matches = Regex.Matches(text, @"\S+");
            var words = new List<string>();
            var lengths = new List<int>();
            var total = 0;

            for (int i = 0; i < matches.Count; i++)
            {
                var word = Regex.Replace(matches[i].Value, @"\[[^\]]*\]", "").Trim();
                if (word.Length == 0) continue;
                words.Add(word);
                lengths.Add(word.Length + 1);
                total += word.Length + 1;
            }

            if (words.Count == 0) return new global::PocketTTS.PocketTtsTranscriptWord[0];

            var output = new global::PocketTTS.PocketTtsTranscriptWord[words.Count];
            var cursor = 0;
            for (int i = 0; i < words.Count; i++)
            {
                output[i] = new global::PocketTTS.PocketTtsTranscriptWord(words[i], cursor);
                cursor += Math.Max(1, (int)Math.Round(sampleCount * (lengths[i] / (double)total)));
                if (cursor >= sampleCount) cursor = sampleCount - 1;
            }

            return output;
        }

        // ---------------------------------------------------------------- reflection plumbing

        private static MethodInfo Method(string name)
        {
            var method = Type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) Plugin.Log.LogError("PocketTTS." + name + " is missing; the Chatterbox patch cannot work.");
            return method;
        }

        private static MethodInfo PropertySetter(string name)
        {
            var property = Type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var setter = property != null ? property.GetSetMethod(true) : null;
            if (setter == null) Plugin.Log.LogError("PocketTTS." + name + " has no setter; the Chatterbox patch cannot work.");
            return setter;
        }

        private static FieldInfo Field(string name)
        {
            var field = Type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) Plugin.Log.LogError("PocketTTS." + name + " is missing; the Chatterbox patch may not work.");
            return field;
        }

        private static void SetStatusValue(global::PocketTTS.PocketTTS instance, global::PocketTTS.ModelStatus status)
        {
            SetStatus.Invoke(instance, new object[] { status });
        }

        private static void SetCompleteValue(global::PocketTTS.PocketTTS instance, bool complete)
        {
            SetComplete.Invoke(instance, new object[] { complete });
        }

        private static void SetField<T>(FieldInfo field, object instance, T value)
        {
            if (field == null || instance == null) return;
            try { field.SetValue(instance, value); }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not set PocketTTS." + field.Name + ": " + ex.Message); }
        }

        private static T ReadField<T>(FieldInfo field, object instance)
        {
            if (field == null || instance == null) return default(T);
            try { return (T)field.GetValue(instance); }
            catch (Exception) { return default(T); }
        }
    }
}
