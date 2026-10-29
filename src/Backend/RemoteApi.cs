using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using ScamWYF.VoiceBackend.Api;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// The remote half of the backend: one request shape per service, driven from a coroutine.
    /// </summary>
    /// <remarks>
    /// Every service is asked for the format this mod can consume without a decoder - 16 kHz linear16
    /// up, 24 kHz linear16 or WAV down - so there is no media codec in the game's process. What each
    /// service needs beyond that is in <see cref="ApiRequests"/> and <see cref="ApiParsers"/>, which
    /// are pure and covered by the console harness.
    ///
    /// A remote request is a network call with a real chance of failing: a wrong key, a rate limit, a
    /// service outage. Each method reports through <c>failed</c> and never throws into the game; the
    /// caller decides whether to fall back to Whisper, to Pocket, or to nothing.
    /// </remarks>
    internal static class RemoteApi
    {
        private const int PollSeconds = 1;

        // ---------------------------------------------------------------- speech recognition

        internal static IEnumerator Transcribe(byte[] wav, string language, Action<string, float> ok,
            Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            switch (cfg.AsrService.Value)
            {
                case SttService.Deepgram:
                    yield return DeepgramTranscribe(wav, language, ok, failed);
                    yield break;
                case SttService.Gladia:
                    yield return GladiaTranscribe(wav, language, ok, failed);
                    yield break;
                case SttService.OpenAi:
                    yield return OpenAiTranscribe(wav, language, ok, failed);
                    yield break;
                default:
                    yield return ServerTranscribe(wav, language, ok, failed);
                    yield break;
            }
        }

        private static IEnumerator DeepgramTranscribe(byte[] wav, string language, Action<string, float> ok,
            Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            string key = cfg.DeepgramApiKey.Value.Trim();
            if (key.Length == 0)
            {
                failed("Deepgram is selected but its API key is empty.");
                yield break;
            }

            Dictionary<string, string> headers = ApiRequests.Headers("Token " + key);
            headers["Content-Type"] = "audio/wav";

            HttpResult result = null;
            yield return VoiceHttp.Post(
                ApiRequests.DeepgramListenUrl(cfg.DeepgramUrl.Value, cfg.DeepgramSttModel.Value, language),
                wav, "audio/wav", headers, cfg.AsrTimeoutSeconds.Value,
                delegate (HttpResult r) { result = r; });

            if (result == null || !result.Ok)
            {
                failed(result != null ? result.Describe() : "no response");
                yield break;
            }

            string text;
            float confidence;
            try
            {
                text = ApiParsers.DeepgramTranscript(result.Text, out confidence);
            }
            catch (Exception ex)
            {
                failed(ex.Message);
                yield break;
            }
            ok(text, confidence);
        }

        private static IEnumerator GladiaTranscribe(byte[] wav, string language, Action<string, float> ok,
            Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            string key = cfg.GladiaApiKey.Value.Trim();
            if (key.Length == 0)
            {
                failed("Gladia is selected but its API key is empty.");
                yield break;
            }

            Dictionary<string, string> headers = ApiRequests.Headers("x-gladia-key: " + key);

            // 1. Upload the clip and take the URL Gladia hands back.
            List<FormField> fields = new List<FormField>
            {
                FormField.File("audio", "utterance.wav", "audio/wav", wav)
            };
            string boundary = FormData.NewBoundary();
            string contentType;
            byte[] body = FormData.Build(fields, boundary, out contentType);

            HttpResult upload = null;
            yield return VoiceHttp.Post(ApiRequests.GladiaUploadUrl(cfg.GladiaUrl.Value), body, contentType,
                headers, cfg.AsrTimeoutSeconds.Value, delegate (HttpResult r) { upload = r; });

            if (upload == null || !upload.Ok)
            {
                failed("Gladia upload failed: " + (upload != null ? upload.Describe() : "no response"));
                yield break;
            }

            string audioUrl;
            try
            {
                audioUrl = ApiParsers.GladiaAudioUrl(upload.Text);
            }
            catch (Exception ex)
            {
                failed("Gladia upload answered with something unreadable: " + ex.Message);
                yield break;
            }
            if (string.IsNullOrEmpty(audioUrl))
            {
                failed("Gladia upload returned no file_url.");
                yield break;
            }

            // 2. Ask for a transcription of that URL.
            byte[] job = Encoding.UTF8.GetBytes(ApiRequests.GladiaJobBody(audioUrl, language));
            HttpResult created = null;
            yield return VoiceHttp.Post(ApiRequests.GladiaJobUrl(cfg.GladiaUrl.Value), job,
                "application/json", headers, cfg.AsrTimeoutSeconds.Value,
                delegate (HttpResult r) { created = r; });

            if (created == null || !created.Ok)
            {
                failed("Gladia job creation failed: " + (created != null ? created.Describe() : "no response"));
                yield break;
            }

            string id;
            try
            {
                id = ApiParsers.GladiaJobId(created.Text);
            }
            catch (Exception ex)
            {
                failed("Gladia job creation answered with something unreadable: " + ex.Message);
                yield break;
            }
            if (string.IsNullOrEmpty(id))
            {
                failed("Gladia job creation returned no id.");
                yield break;
            }

            // 3. Poll until the job finishes. Gladia is asynchronous even for short clips.
            string poll = ApiRequests.GladiaPollUrl(cfg.GladiaUrl.Value, id);
            int waited = 0;
            int limit = Math.Max(5, cfg.AsrTimeoutSeconds.Value);
            while (waited < limit)
            {
                HttpResult status = null;
                yield return VoiceHttp.Get(poll, headers, cfg.AsrTimeoutSeconds.Value,
                    delegate (HttpResult r) { status = r; });

                if (status == null || !status.Ok)
                {
                    failed("Gladia polling failed: " + (status != null ? status.Describe() : "no response"));
                    yield break;
                }

                string state;
                try { state = ApiParsers.GladiaStatus(status.Text); }
                catch (Exception ex)
                {
                    failed("Gladia polling answered with something unreadable: " + ex.Message);
                    yield break;
                }

                if (state == "done" || state == "completed" || state == "success")
                {
                    try { ok(ApiParsers.GladiaTranscript(status.Text), 1f); }
                    catch (Exception ex) { failed(ex.Message); }
                    yield break;
                }
                if (state == "error" || state == "failed")
                {
                    failed("Gladia reported the job as " + state + ".");
                    yield break;
                }

                yield return new UnityEngine.WaitForSeconds(PollSeconds);
                waited += PollSeconds;
            }

            failed("Gladia did not finish within " + limit + " s (job " + id + ").");
        }

        private static IEnumerator OpenAiTranscribe(byte[] wav, string language, Action<string, float> ok,
            Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            string key = cfg.OpenAiApiKey.Value.Trim();
            if (key.Length == 0)
            {
                failed("The transcription service is selected but its API key is empty.");
                yield break;
            }

            List<FormField> fields = new List<FormField>
            {
                FormField.File("file", "utterance.wav", "audio/wav", wav),
                FormField.Text("model", cfg.OpenAiSttModel.Value),
                FormField.Text("response_format", "json"),
                FormField.Text("language", ApiRequests.Language(language))
            };
            string boundary = FormData.NewBoundary();
            string contentType;
            byte[] body = FormData.Build(fields, boundary, out contentType);

            HttpResult result = null;
            yield return VoiceHttp.Post(ApiRequests.OpenAiTranscriptionUrl(cfg.OpenAiUrl.Value), body, contentType,
                ApiRequests.Headers("Bearer " + key), cfg.AsrTimeoutSeconds.Value,
                delegate (HttpResult r) { result = r; });

            if (result == null || !result.Ok)
            {
                failed(result != null ? result.Describe() : "no response");
                yield break;
            }

            try { ok(ApiParsers.OpenAiTranscript(result.Text), 1f); }
            catch (Exception ex) { failed(ex.Message); }
        }

        /// <summary>The mod's own server protocol, unchanged from the companion-server days.</summary>
        private static IEnumerator ServerTranscribe(byte[] wav, string language, Action<string, float> ok,
            Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            string url;
            try
            {
                url = cfg.ResolveUrl(cfg.AsrPath) + "?language=" + ApiRequests.Escape(ApiRequests.Language(language));
            }
            catch (Exception ex)
            {
                failed(ex.Message);
                yield break;
            }

            HttpResult result = null;
            yield return VoiceHttp.Post(url, wav, "audio/wav", cfg.AsrTimeoutSeconds.Value,
                delegate (HttpResult r) { result = r; });

            if (result == null || !result.Ok)
            {
                failed(result != null ? result.Describe() : "no response");
                yield break;
            }

            string text;
            float confidence;
            try { text = ApiParsers.ServerTranscript(result.Text, out confidence); }
            catch (Exception ex)
            {
                failed("unreadable reply: " + ex.Message);
                yield break;
            }
            ok(text, confidence);
        }

        // ---------------------------------------------------------------- speech synthesis

        internal static IEnumerator Synthesize(string text, string language, string referenceWav,
            Action<float[], int> ok, Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            switch (cfg.TtsService.Value)
            {
                case TtsService.ElevenLabs:
                    yield return ElevenLabsSynthesize(text, language, ok, failed);
                    yield break;
                case TtsService.Deepgram:
                    yield return DeepgramSynthesize(text, ok, failed);
                    yield break;
                case TtsService.OpenAi:
                    yield return OpenAiSynthesize(text, ok, failed);
                    yield break;
                default:
                    yield return ServerSynthesize(text, language, referenceWav, ok, failed);
                    yield break;
            }
        }

        private static IEnumerator ElevenLabsSynthesize(string text, string language,
            Action<float[], int> ok, Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            string key = cfg.ElevenLabsApiKey.Value.Trim();
            if (key.Length == 0)
            {
                failed("ElevenLabs is selected but its API key is empty.");
                yield break;
            }

            byte[] body = Encoding.UTF8.GetBytes(ApiRequests.ElevenLabsBody(text, language));
            HttpResult result = null;
            yield return VoiceHttp.Post(
                ApiRequests.ElevenLabsUrl(cfg.ElevenLabsUrl.Value, cfg.ElevenLabsVoiceId.Value,
                    cfg.ElevenLabsModel.Value),
                body, "application/json", ApiRequests.Headers("xi-api-key: " + key),
                cfg.TtsTimeoutSeconds.Value, delegate (HttpResult r) { result = r; });

            if (result == null || !result.Ok)
            {
                failed(result != null ? result.Describe() : "no response");
                yield break;
            }

            Deliver(result.Body, "ElevenLabs", ok, failed);
        }

        private static IEnumerator DeepgramSynthesize(string text, Action<float[], int> ok, Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            string key = cfg.DeepgramApiKey.Value.Trim();
            if (key.Length == 0)
            {
                failed("Deepgram is selected but its API key is empty.");
                yield break;
            }

            byte[] body = Encoding.UTF8.GetBytes(ApiRequests.DeepgramSpeakBody(text));
            HttpResult result = null;
            yield return VoiceHttp.Post(
                ApiRequests.DeepgramSpeakUrl(cfg.DeepgramUrl.Value, cfg.DeepgramTtsVoice.Value),
                body, "application/json", ApiRequests.Headers("Token " + key),
                cfg.TtsTimeoutSeconds.Value, delegate (HttpResult r) { result = r; });

            if (result == null || !result.Ok)
            {
                failed(result != null ? result.Describe() : "no response");
                yield break;
            }

            Deliver(result.Body, "Deepgram", ok, failed);
        }

        private static IEnumerator OpenAiSynthesize(string text, Action<float[], int> ok, Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            string key = cfg.OpenAiApiKey.Value.Trim();
            if (key.Length == 0)
            {
                failed("The speech service is selected but its API key is empty.");
                yield break;
            }

            byte[] body = Encoding.UTF8.GetBytes(
                ApiRequests.OpenAiSpeechBody(text, cfg.OpenAiTtsModel.Value, cfg.OpenAiTtsVoice.Value));
            HttpResult result = null;
            yield return VoiceHttp.Post(ApiRequests.OpenAiSpeechUrl(cfg.OpenAiUrl.Value), body,
                "application/json", ApiRequests.Headers("Bearer " + key), cfg.TtsTimeoutSeconds.Value,
                delegate (HttpResult r) { result = r; });

            if (result == null || !result.Ok)
            {
                failed(result != null ? result.Describe() : "no response");
                yield break;
            }

            Deliver(result.Body, "The speech service", ok, failed);
        }

        private static IEnumerator ServerSynthesize(string text, string language, string referenceWav,
            Action<float[], int> ok, Action<string> failed)
        {
            Config cfg = Plugin.Cfg;
            Newtonsoft.Json.Linq.JObject body = new Newtonsoft.Json.Linq.JObject
            {
                ["text"] = text,
                ["language"] = LanguageMap.ChatterboxLanguage(language),
                ["model"] = "auto",
                ["voice_path"] = referenceWav ?? ""
            };

            // A server on another machine cannot read the game's reference clip, so it can be sent the
            // bytes instead. Only the mod's own protocol has a field for that.
            if (cfg.SendVoiceAudio.Value && !string.IsNullOrEmpty(referenceWav))
            {
                try
                {
                    body["voice_b64"] = Convert.ToBase64String(System.IO.File.ReadAllBytes(referenceWav));
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not read the reference voice: " + ex.Message);
                }
            }

            byte[] payload = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            string url;
            try { url = cfg.ResolveUrl(cfg.TtsPath); }
            catch (Exception ex)
            {
                failed(ex.Message);
                yield break;
            }

            HttpResult result = null;
            yield return VoiceHttp.Post(url, payload, "application/json", cfg.TtsTimeoutSeconds.Value,
                delegate (HttpResult r) { result = r; });

            if (result == null || !result.Ok)
            {
                failed(result != null ? result.Describe() : "no response");
                yield break;
            }

            Deliver(result.Body, "The server", ok, failed);
        }

        private static void Deliver(byte[] body, string what, Action<float[], int> ok, Action<string> failed)
        {
            try
            {
                int rate;
                float[] audio = PcmCodec.Read(body, what, out rate);
                if (audio.Length == 0)
                {
                    failed(what + " returned no audio.");
                    return;
                }
                ok(audio, rate);
            }
            catch (Exception ex)
            {
                failed(ex.Message);
            }
        }
    }
}
