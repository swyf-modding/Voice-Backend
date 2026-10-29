using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ScamWYF.VoiceBackend;
using ScamWYF.VoiceBackend.Api;
using ScamWYF.VoiceBackend.Local;
using ScamWYF.VoiceBackend.Ort;

namespace LocalTests
{
    /// <summary>
    /// Console harness for the in-process inference code: it compiles the same sources the mod ships
    /// (src/Ort, src/Local) and runs them outside Unity, against the game's own onnxruntime.dll.
    /// </summary>
    internal static class Program
    {
        [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        private static int Main(string[] args)
        {
            string ortDir = Environment.GetEnvironmentVariable("VOICEBACKEND_ORT_DIR");
            if (!string.IsNullOrEmpty(ortDir)) SetDllDirectory(ortDir);

            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: probe | kokoro <model> <voice> <ids-file> <out.wav>");
                return 2;
            }

            try
            {
                switch (args[0])
                {
                    case "probe":
                        return Probe();
                    case "api":
                        return ApiTests();
                    case "tts":
                        return TtsTest(args);
                    case "kokoro-style":
                        return KokoroStyleTest(args);
                    case "asr":
                        return AsrTest(args);
                    case "load":
                        return LoadTest(args);
                    case "kokoro":
                        return KokoroTest(args);
                    default:
                        Console.Error.WriteLine("unknown command " + args[0]);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void Warn(string message)
        {
            Console.WriteLine("warn: " + message);
        }

        private static int Probe()
        {
            Console.WriteLine("providers: " + OrtApi.Version());
            return 0;
        }

        private static int _failures;

        private static void Check(bool condition, string what, string detail)
        {
            if (condition) Console.WriteLine("  PASS  " + what);
            else
            {
                _failures++;
                Console.WriteLine("  FAIL  " + what + (detail != null ? "  -> " + detail : ""));
            }
        }

        /// <summary>
        /// The pure half of the remote API support: request shapes, response parsers and the audio
        /// formats. Everything here runs without a network and without a key, which is the only part
        /// of a cloud service a repository can assert.
        /// </summary>
        private static int ApiTests()
        {
            Console.WriteLine("request shapes");
            Check(ApiRequests.DeepgramListenUrl("https://api.deepgram.com/", "nova-3", "de-AT") ==
                  "https://api.deepgram.com/v1/listen?model=nova-3&language=de&smart_format=true&punctuate=true" +
                  "&encoding=linear16&sample_rate=16000&channels=1",
                "Deepgram listen URL carries model, language and linear16",
                ApiRequests.DeepgramListenUrl("https://api.deepgram.com/", "nova-3", "de-AT"));
            Check(ApiRequests.ElevenLabsUrl("https://api.elevenlabs.io", "abc def", "") ==
                  "https://api.elevenlabs.io/v1/text-to-speech/abc%20def?output_format=pcm_24000" +
                  "&optimize_streaming_latency=0&model_id=eleven_multilingual_v2",
                "ElevenLabs URL asks for raw 24 kHz PCM and escapes the voice id",
                ApiRequests.ElevenLabsUrl("https://api.elevenlabs.io", "abc def", ""));
            Check(ApiRequests.DeepgramSpeakUrl("https://api.deepgram.com", "") ==
                  "https://api.deepgram.com/v1/speak?model=aura-2-thalia-en&encoding=linear16" +
                  "&sample_rate=24000&container=wav",
                "Deepgram speak asks for WAV so no decoder is needed",
                ApiRequests.DeepgramSpeakUrl("https://api.deepgram.com", ""));
            Check(ApiRequests.OpenAiSpeechBody("hi", "", "").Contains("\"response_format\":\"pcm\""),
                "OpenAI speech asks for raw PCM", ApiRequests.OpenAiSpeechBody("hi", "", ""));
            Check(ApiRequests.GladiaPollUrl("https://api.gladia.io/", "a/b").EndsWith("/v2/pre-recorded/a%2Fb"),
                "Gladia poll URL escapes the job id", ApiRequests.GladiaPollUrl("https://api.gladia.io/", "a/b"));
            Check(ApiRequests.Language("pt-BR") == "pt" && ApiRequests.Language("") == "en" &&
                  ApiRequests.Language("zh-Hant") == "zh",
                "language tags are reduced to two letters", ApiRequests.Language("zh-Hant"));

            Console.WriteLine("multipart body");
            List<FormField> fields = new List<FormField>
            {
                FormField.Text("model", "whisper-1"),
                FormField.File("file", "utterance.wav", "audio/wav", new byte[] { 1, 2, 3, 4 })
            };
            string contentType;
            byte[] body = FormData.Build(fields, "BOUNDARY", out contentType);
            string text = System.Text.Encoding.UTF8.GetString(body);
            Check(contentType == "multipart/form-data; boundary=BOUNDARY", "content type names the boundary", contentType);
            Check(text.StartsWith("--BOUNDARY\r\nContent-Disposition: form-data; name=\"model\"\r\n"),
                "first part is the text field", text.Substring(0, Math.Min(80, text.Length)));
            Check(text.Contains("name=\"file\"; filename=\"utterance.wav\"\r\nContent-Type: audio/wav\r\n\r\n"),
                "file part carries its filename and type", text);
            Check(text.EndsWith("--BOUNDARY--\r\n"), "body ends with the closing boundary", text.Substring(text.Length - 20));
            Check(body.Length == text.Length, "the file bytes are not re-encoded", null);

            Console.WriteLine("response parsers");
            float confidence;
            string deepgram = "{\"results\":{\"channels\":[{\"alternatives\":[{\"transcript\":\"hello there\",\"confidence\":0.93}]}]}}";
            Check(ApiParsers.DeepgramTranscript(deepgram, out confidence) == "hello there" &&
                  Math.Abs(confidence - 0.93f) < 1e-6, "Deepgram transcript and confidence", null);

            string gladiaUtterances = "{\"status\":\"done\",\"result\":{\"transcription\":{\"utterances\":" +
                                      "[{\"text\":\" first part \"},{\"text\":\"second part\"}]," +
                                      "\"full_transcript\":\"ignored\"}}}";
            Check(ApiParsers.GladiaTranscript(gladiaUtterances) == "first part second part",
                "Gladia joins utterances and trims them", ApiParsers.GladiaTranscript(gladiaUtterances));
            Check(ApiParsers.GladiaTranscript("{\"result\":{\"transcription\":{\"full_transcript\":\"only this\"}}}") ==
                  "only this", "Gladia falls back to full_transcript", null);
            Check(ApiParsers.GladiaStatus(gladiaUtterances) == "done", "Gladia status", null);
            Check(ApiParsers.GladiaJobId("{\"id\":\"job-1\"}") == "job-1", "Gladia job id", null);
            Check(ApiParsers.GladiaAudioUrl("{\"file_url\":\"https://x/y.wav\"}") == "https://x/y.wav",
                "Gladia upload url", null);
            Check(ApiParsers.OpenAiTranscript("{\"text\":\" hi \"}") == " hi ", "OpenAI transcript", null);
            Check(ApiParsers.ServerTranscript("{\"text\":\"ok\",\"confidence\":0.5}", out confidence) == "ok" &&
                  Math.Abs(confidence - 0.5f) < 1e-6, "mod server transcript", null);

            Console.WriteLine("error bodies");
            try
            {
                ApiParsers.DeepgramTranscript("{\"err_code\":400,\"err_msg\":\"bad key\"}", out confidence);
                Check(false, "Deepgram error body raises", "no exception");
            }
            catch (Exception ex) { Check(ex.Message.Contains("bad key"), "Deepgram error body raises", ex.Message); }

            try
            {
                ApiParsers.OpenAiTranscript("{\"error\":{\"message\":\"quota\"}}");
                Check(false, "OpenAI error body raises", "no exception");
            }
            catch (Exception ex) { Check(ex.Message.Contains("quota"), "OpenAI error body raises", ex.Message); }

            Console.WriteLine("audio bodies");
            byte[] pcm = new byte[480];                       // 240 samples of silence-plus-one
            pcm[0] = 0x00; pcm[1] = 0x40;                     // +0.5
            pcm[2] = 0x00; pcm[3] = 0xC0;                     // -0.5
            int rate;
            float[] samples = PcmCodec.Read(pcm, "test", out rate);
            Check(samples.Length == 240 && rate == 24000, "raw PCM is read at 24 kHz", samples.Length + "/" + rate);
            Check(Math.Abs(samples[0] - 0.5f) < 1e-6 && Math.Abs(samples[1] + 0.5f) < 1e-6,
                "little-endian signed samples", samples[0] + "," + samples[1]);

            byte[] wav = WavCodec.EncodePcm16(new[] { 0.25f, -0.25f, 0.5f }, 16000, 1);
            float[] fromWav = PcmCodec.Read(wav, "test", out rate);
            Check(fromWav.Length == 3 && rate == 16000 && Math.Abs(fromWav[2] - 0.5f) < 0.01,
                "a WAV body is decoded with its own rate", fromWav.Length + "/" + rate);

            ExpectRejected(new byte[] { 0x49, 0x44, 0x33, 0x04 }, "MP3");
            ExpectRejected(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x00 }, "WebM/Opus");
            ExpectRejected(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0x00 }, "Ogg/Opus");

            Console.WriteLine(_failures == 0 ? "all api checks passed" : _failures + " api check(s) failed");
            return _failures == 0 ? 0 : 1;
        }

        private static void ExpectRejected(byte[] body, string what)
        {
            try
            {
                int rate;
                PcmCodec.Read(body, "test", out rate);
                Check(false, what + " is refused rather than played as noise", "no exception");
            }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message.Contains("PCM or WAV"), what + " is refused with a reason", ex.Message);
            }
        }

        private static int TtsTest(string[] args)
        {
            string assetRoot = args[1];
            string text = File.ReadAllText(args[2]).Trim();
            using (LocalEngine engine = new LocalEngine(Path.Combine(assetRoot, "models"), assetRoot,
                OrtDevice.Cpu, 4, Warn))
            {
                Console.WriteLine("phonemes: " + engine.Phonemes(text));
                int rate;
                string detail;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                float[] audio = engine.Synthesize(text, args.Length > 3 ? args[3] : null, out rate, out detail);
                watch.Stop();
                double rms = 0;
                for (int i = 0; i < audio.Length; i++) rms += audio[i] * (double)audio[i];
                Console.WriteLine(string.Format("{0}: {1} samples at {2} Hz, {3:F2} s, rms {4:F7}, {5} ms",
                    detail, audio.Length, rate, audio.Length / (double)rate,
                    Math.Sqrt(rms / Math.Max(1, audio.Length)), watch.ElapsedMilliseconds));
                WriteWav(args.Length > 4 ? args[4] : "tts.wav", audio, rate);
            }
            return 0;
        }

        private static int KokoroStyleTest(string[] args)
        {
            byte[] style = File.ReadAllBytes(args[2]);
            float[] row = new float[style.Length / 4];
            Buffer.BlockCopy(style, 0, row, 0, style.Length);
            string[] parts = File.ReadAllText(args[3]).Split(new[] { ',', '\n', '\r', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            long[] ids = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++) ids[i] = long.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);

            using (KokoroTts tts = KokoroTts.Load(args[1], OrtDevice.Cpu, 4, Warn))
            {
                KokoroVoice voice = KokoroVoice.FromRows(row, 1);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                float[] audio = tts.Synthesize(ids, voice, 1.0f);
                watch.Stop();
                double rms = 0, peak = 0;
                for (int i = 0; i < audio.Length; i++)
                {
                    rms += audio[i] * (double)audio[i];
                    if (Math.Abs(audio[i]) > peak) peak = Math.Abs(audio[i]);
                }
                rms = Math.Sqrt(rms / Math.Max(1, audio.Length));
                Console.WriteLine(string.Format("samples {0}, rms {1:F7}, peak {2:F6}, {3} ms",
                    audio.Length, rms, peak, watch.ElapsedMilliseconds));
                WriteWav(args[4], audio, KokoroTts.SampleRate);
            }
            return 0;
        }

        private static int AsrTest(string[] args)
        {
            float[] samples;
            int rate;
            ReadWav(args[2], out samples, out rate);
            float[] mono = rate == ParakeetAsr.SampleRate ? samples : Resample(samples, rate, ParakeetAsr.SampleRate);
            Console.WriteLine(string.Format("{0}: {1:F2} s at {2} Hz", args[2], mono.Length / 16000.0, rate));

            using (ParakeetAsr asr = ParakeetAsr.Load(args[1], OrtDevice.Cpu, 4, Warn))
            {
                    if (Array.IndexOf(args, "--trace") >= 0) asr.Trace = Console.WriteLine;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                AsrResult result = asr.Transcribe(mono);
                watch.Stop();
                Console.WriteLine(string.Format("text ({0} tokens, {1} frames, {2} ms): {3}",
                    result.TokenCount, result.Frames, watch.ElapsedMilliseconds, result.Text));
                Console.WriteLine(string.Format("confidence: {0:F3}", result.Confidence));
                if (args.Length > 3 && !args[3].StartsWith("--", StringComparison.Ordinal))
                {
                    string expected = File.ReadAllText(args[3]).Trim();
                    Console.WriteLine(expected == result.Text.Trim()
                        ? "MATCH expected text"
                        : "MISMATCH\n  expected: " + expected);
                }
            }
            return 0;
        }

        internal static void ReadWav(string path, out float[] samples, out int sampleRate)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int pos = 12;
            sampleRate = 0;
            int channels = 1;
            int bits = 16;
            int dataStart = -1;
            int dataLength = 0;
            while (pos + 8 <= bytes.Length)
            {
                string id = Encoding.ASCII.GetString(bytes, pos, 4);
                int size = BitConverter.ToInt32(bytes, pos + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToInt16(bytes, pos + 10);
                    sampleRate = BitConverter.ToInt32(bytes, pos + 12);
                    bits = BitConverter.ToInt16(bytes, pos + 22);
                }
                else if (id == "data")
                {
                    dataStart = pos + 8;
                    dataLength = Math.Min(size, bytes.Length - dataStart);
                    break;
                }
                pos += 8 + size + (size & 1);
            }
            if (dataStart < 0) throw new InvalidDataException("no data chunk in " + path);
            if (bits != 16) throw new InvalidDataException("only 16-bit PCM is supported, got " + bits);

            int frames = dataLength / (2 * channels);
            samples = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                samples[i] = BitConverter.ToInt16(bytes, dataStart + i * 2 * channels) / 32768f;
            }
        }

        internal static float[] Resample(float[] input, int from, int to)
        {
            int length = (int)Math.Round(input.Length * (double)to / from);
            float[] output = new float[length];
            for (int i = 0; i < length; i++)
            {
                double position = i * (double)from / to;
                int index = (int)position;
                double fraction = position - index;
                float a = input[Math.Min(index, input.Length - 1)];
                float b = input[Math.Min(index + 1, input.Length - 1)];
                output[i] = (float)(a + (b - a) * fraction);
            }
            return output;
        }

        private static int LoadTest(string[] args)
        {
            Console.WriteLine("providers: " + OrtApi.Version());
            using (OrtSession session = OrtSession.Create(args[1], OrtDevice.Cpu, 4, Warn))
            {
                Console.WriteLine("loaded " + Path.GetFileName(args[1]));
                Console.WriteLine("  inputs:  " + string.Join(", ", session.InputNames));
                Console.WriteLine("  outputs: " + string.Join(", ", session.OutputNames));
            }
            return 0;
        }

        private static int KokoroTest(string[] args)
        {
            string model = args[1];
            string voicePath = args[2];
            string idsPath = args[3];
            string outPath = args[4];

            string[] parts = File.ReadAllText(idsPath).Split(new[] { ',', '\n', '\r', ' ' },
                StringSplitOptions.RemoveEmptyEntries);
            long[] ids = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++) ids[i] = long.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);

            using (KokoroTts tts = KokoroTts.Load(model, OrtDevice.Cpu, 4, Warn))
            {
                KokoroVoice voice = KokoroVoice.Load(voicePath);
                Console.WriteLine("voice rows: " + voice.RowCount + ", phonemes: " + ids.Length);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                float[] audio = tts.Synthesize(ids, voice, 1.0f);
                watch.Stop();
                Console.WriteLine(string.Format("samples: {0} ({1:F2} s audio) in {2} ms",
                    audio.Length, audio.Length / (double)KokoroTts.SampleRate, watch.ElapsedMilliseconds));
                WriteWav(outPath, audio, KokoroTts.SampleRate);
                return 0;
            }
        }

        internal static void WriteWav(string path, float[] samples, int sampleRate)
        {
            using (FileStream stream = File.Create(path))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                int dataBytes = samples.Length * 2;
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataBytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(dataBytes);
                for (int i = 0; i < samples.Length; i++)
                {
                    float v = samples[i];
                    if (v > 1f) v = 1f;
                    if (v < -1f) v = -1f;
                    writer.Write((short)Math.Round(v * 32767f));
                }
            }
        }
    }
}
