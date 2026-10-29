using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace ScamWYF.VoiceBackend.Api
{
    /// <summary>Which remote service a direction talks to.</summary>
    public enum SttService
    {
        /// <summary>Parakeet in this process.</summary>
        Local,
        /// <summary>A server speaking this mod's own /asr endpoint.</summary>
        Server,
        /// <summary>Deepgram pre-recorded transcription.</summary>
        Deepgram,
        /// <summary>Gladia pre-recorded transcription.</summary>
        Gladia,
        /// <summary>Anything with the OpenAI /v1/audio/transcriptions shape.</summary>
        OpenAi
    }

    /// <summary>Which remote service a direction talks to.</summary>
    public enum TtsService
    {
        /// <summary>Kokoro in this process.</summary>
        Local,
        /// <summary>A server speaking this mod's own /tts endpoint.</summary>
        Server,
        /// <summary>ElevenLabs text to speech.</summary>
        ElevenLabs,
        /// <summary>Deepgram Aura text to speech.</summary>
        Deepgram,
        /// <summary>Anything with the OpenAI /v1/audio/speech shape.</summary>
        OpenAi
    }

    /// <summary>
    /// URLs, headers and body shapes for the speech services, kept separate from the coroutines so the
    /// shapes can be asserted without a network.
    /// </summary>
    /// <remarks>
    /// Every request asks for the cheapest format this mod can consume: raw 16-bit PCM at 24 kHz where
    /// the service offers it, WAV where it does not, and never MP3 or Opus - decoding those would mean
    /// shipping a decoder for a format the caller never hears.
    /// </remarks>
    internal static class ApiRequests
    {
        internal static string Language(string code)
        {
            string normalized = (code ?? "").Trim().ToLowerInvariant();
            if (normalized.Length == 0) return "en";
            int separator = normalized.IndexOfAny(new[] { '-', '_' });
            if (separator > 0) normalized = normalized.Substring(0, separator);
            return normalized.Length == 2 ? normalized : "en";
        }

        /// <summary>
        /// Percent-encodes a path or query segment. <c>Uri.EscapeDataString</c> is not in the game's
        /// stripped corlib, and a key or a voice id can contain anything, so this encodes the UTF-8
        /// bytes and leaves only the RFC 3986 unreserved set alone.
        /// </summary>
        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            StringBuilder text = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                bool unreserved = (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') ||
                                  b == '-' || b == '_' || b == '.' || b == '~';
                if (unreserved) text.Append((char)b);
                else text.Append('%').Append(b.ToString("X2"));
            }
            return text.ToString();
        }

        internal static string AppendQuery(string url, params string[] pairs)
        {
            StringBuilder text = new StringBuilder(url);
            bool first = text.ToString().IndexOf('?') < 0;
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                if (string.IsNullOrEmpty(pairs[i + 1])) continue;
                text.Append(first ? '?' : '&');
                first = false;
                text.Append(pairs[i]).Append('=').Append(Escape(pairs[i + 1]));
            }
            return text.ToString();
        }

        internal static Dictionary<string, string> Headers(string authorizationHeader)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(authorizationHeader)) headers["Authorization"] = authorizationHeader;
            return headers;
        }

        // ---------------------------------------------------------------- speech recognition

        internal static string DeepgramListenUrl(string baseUrl, string model, string language)
        {
            return AppendQuery(TrimSlash(baseUrl) + "/v1/listen",
                "model", string.IsNullOrEmpty(model) ? "nova-3" : model,
                "language", Language(language),
                "smart_format", "true",
                "punctuate", "true",
                "encoding", "linear16",
                "sample_rate", "16000",
                "channels", "1");
        }

        internal static string GladiaUploadUrl(string baseUrl)
        {
            return TrimSlash(baseUrl) + "/v2/upload";
        }

        internal static string GladiaJobUrl(string baseUrl)
        {
            return TrimSlash(baseUrl) + "/v2/pre-recorded";
        }

        internal static string GladiaPollUrl(string baseUrl, string id)
        {
            return TrimSlash(baseUrl) + "/v2/pre-recorded/" + Escape(id ?? "");
        }

        internal static string GladiaJobBody(string audioUrl, string language)
        {
            JObject body = new JObject
            {
                ["audio_url"] = audioUrl,
                ["language_config"] = new JObject
                {
                    ["languages"] = new JArray(Language(language)),
                    ["code_switching"] = false
                },
                ["diarization"] = false
            };
            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string OpenAiTranscriptionUrl(string baseUrl)
        {
            return TrimSlash(baseUrl) + "/v1/audio/transcriptions";
        }

        // ---------------------------------------------------------------- speech synthesis

        internal static string ElevenLabsUrl(string baseUrl, string voiceId, string model)
        {
            string voice = string.IsNullOrEmpty(voiceId) ? "21m00Tcm4TlvDq8ikWAM" : voiceId;
            return AppendQuery(TrimSlash(baseUrl) + "/v1/text-to-speech/" + Escape(voice),
                "output_format", "pcm_24000",
                "optimize_streaming_latency", "0",
                "model_id", string.IsNullOrEmpty(model) ? "eleven_multilingual_v2" : model);
        }

        internal static string ElevenLabsBody(string text, string language)
        {
            JObject body = new JObject
            {
                ["text"] = text,
                ["model_id"] = null,
                ["voice_settings"] = new JObject
                {
                    ["stability"] = 0.5,
                    ["similarity_boost"] = 0.75,
                    ["style"] = 0.0,
                    ["use_speaker_boost"] = true
                }
            };
            body.Remove("model_id");

            // The multilingual models infer the language from the text, so the game's code is only used
            // to steer the few models that accept a language hint. Never send an empty field.
            string code = Language(language);
            if (code != "en" && string.IsNullOrEmpty(text)) body["language_code"] = code;
            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string DeepgramSpeakUrl(string baseUrl, string voice)
        {
            return AppendQuery(TrimSlash(baseUrl) + "/v1/speak",
                "model", string.IsNullOrEmpty(voice) ? "aura-2-thalia-en" : voice,
                "encoding", "linear16",
                "sample_rate", "24000",
                "container", "wav");
        }

        internal static string DeepgramSpeakBody(string text)
        {
            return new JObject { ["text"] = text }.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string OpenAiSpeechUrl(string baseUrl)
        {
            return TrimSlash(baseUrl) + "/v1/audio/speech";
        }

        internal static string OpenAiSpeechBody(string text, string model, string voice)
        {
            return new JObject
            {
                ["model"] = string.IsNullOrEmpty(model) ? "gpt-4o-mini-tts" : model,
                ["voice"] = string.IsNullOrEmpty(voice) ? "alloy" : voice,
                ["input"] = text,
                ["response_format"] = "pcm"
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        internal static string TrimSlash(string url)
        {
            return (url ?? "").Trim().TrimEnd('/');
        }
    }

    /// <summary>
    /// Response parsers, one per service. Each returns the transcript and takes the service's own idea
    /// of confidence where it has one.
    /// </summary>
    internal static class ApiParsers
    {
        /// <summary>Deepgram: results.channels[0].alternatives[0].transcript.</summary>
        internal static string DeepgramTranscript(string json, out float confidence)
        {
            confidence = 1f;
            JObject body = JObject.Parse(json ?? "{}");

            string error = (string)body["err_msg"];
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException("Deepgram: " + error);

            JArray channels = body["results"] != null ? body["results"]["channels"] as JArray : null;
            if (channels == null || channels.Count == 0) return "";

            JArray alternatives = channels[0]["alternatives"] as JArray;
            if (alternatives == null || alternatives.Count == 0) return "";

            JToken alternative = alternatives[0];
            if (alternative["confidence"] != null) confidence = alternative["confidence"].Value<float>();
            return (string)alternative["transcript"] ?? "";
        }

        /// <summary>
        /// Gladia: result.transcription.utterances[].text, or full_transcript when the job reported no
        /// utterances. The utterances are preferred because they are already stripped of the padding
        /// Gladia adds around silence.
        /// </summary>
        internal static string GladiaTranscript(string json)
        {
            JObject body = JObject.Parse(json ?? "{}");

            if (body["error"] != null)
            {
                string message = (string)body["error"]["message"] ?? body["error"].ToString();
                throw new InvalidOperationException("Gladia: " + message);
            }

            JToken transcription = body["result"] != null ? body["result"]["transcription"] : null;
            if (transcription == null) return "";

            JArray utterances = transcription["utterances"] as JArray;
            if (utterances != null && utterances.Count > 0)
            {
                StringBuilder text = new StringBuilder();
                for (int i = 0; i < utterances.Count; i++)
                {
                    string part = ((string)utterances[i]["text"] ?? "").Trim();
                    if (part.Length == 0) continue;
                    if (text.Length > 0) text.Append(' ');
                    text.Append(part);
                }
                return text.ToString();
            }

            return ((string)transcription["full_transcript"] ?? "").Trim();
        }

        /// <summary>Reads the job status out of a Gladia pre-recorded response.</summary>
        internal static string GladiaStatus(string json)
        {
            JObject body = JObject.Parse(json ?? "{}");
            JToken status = body["status"];
            if (status == null && body["result"] != null) status = body["result"]["status"];
            return status != null ? ((string)status ?? "").ToLowerInvariant() : "";
        }

        /// <summary>Reads the id out of a Gladia job-creation response.</summary>
        internal static string GladiaJobId(string json)
        {
            JObject body = JObject.Parse(json ?? "{}");
            if (body["id"] != null) return (string)body["id"];
            if (body["result"] != null && body["result"]["id"] != null) return (string)body["result"]["id"];
            return "";
        }

        /// <summary>Reads the audio url out of a Gladia upload response.</summary>
        internal static string GladiaAudioUrl(string json)
        {
            JObject body = JObject.Parse(json ?? "{}");
            if (body["file_url"] != null) return (string)body["file_url"];
            if (body["audio_url"] != null) return (string)body["audio_url"];
            if (body["result"] != null && body["result"]["audio_url"] != null)
            {
                return (string)body["result"]["audio_url"];
            }
            return "";
        }

        /// <summary>OpenAI-shaped transcription: {"text": "..."}.</summary>
        internal static string OpenAiTranscript(string json)
        {
            JObject body = JObject.Parse(json ?? "{}");
            if (body["error"] != null)
            {
                string message = (string)body["error"]["message"] ?? body["error"].ToString();
                throw new InvalidOperationException("The transcription service: " + message);
            }
            return (string)body["text"] ?? "";
        }

        /// <summary>The mod's own server answer: {"text": "...", "confidence": 0.0}.</summary>
        internal static string ServerTranscript(string json, out float confidence)
        {
            JObject body = JObject.Parse(json ?? "{}");
            confidence = body["confidence"] != null ? body["confidence"].Value<float>() : 1f;
            return (string)body["text"] ?? "";
        }
    }
}
