using System;
using System.Collections;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using ScamWYF.VoiceBackend.Local;
using ScamWYF.VoiceBackend.Api;
using ScamWYF.VoiceBackend.Models;
using ScamWYF.VoiceBackend.Ort;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// The one place that decides where speech recognition and synthesis happen: in this process, or
    /// on a server that speaks the same two endpoints.
    /// </summary>
    /// <remarks>
    /// Local is the default and needs no other program: the models run on the ONNX Runtime the game
    /// already loads, and the weights are downloaded on first use. Remote exists for a setup that wants
    /// a heavier machine to do the work (and for the same endpoints the old companion server spoke), so
    /// both paths produce the same two results: text with a confidence, and 24 kHz mono audio.
    ///
    /// Inference never runs on the Unity main thread. A Parakeet encoder pass is a few hundred
    /// milliseconds and Kokoro is similar; on the main thread either would drop a frame mid-call, so
    /// each request hands the work to the thread pool and the coroutine returns once it is done.
    /// </remarks>
    internal static class VoiceBackend
    {
        private static LocalEngine _engine;
        private static int _busy;

        internal static bool AsrIsLocal
        {
            get { return Plugin.Cfg == null || Plugin.Cfg.AsrService.Value == SttService.Local; }
        }

        internal static bool TtsIsLocal
        {
            get { return Plugin.Cfg == null || Plugin.Cfg.TtsService.Value == TtsService.Local; }
        }

        internal static LocalEngine Engine
        {
            get { return _engine; }
        }

        internal static string Describe()
        {
            if (Plugin.Cfg == null) return "not configured";

            string asr = AsrIsLocal
                ? "Parakeet v2" + (Plugin.Cfg.EuropeanModel.Value == ParakeetModel.V3 ? " + v3" : "")
                : Plugin.Cfg.AsrService.Value.ToString();
            string tts = TtsIsLocal ? "Kokoro" : Plugin.Cfg.TtsService.Value.ToString();
            return asr + " / " + tts;
        }

        /// <summary>Builds the engine once. Cheap: models load on first use, not here.</summary>
        internal static void Create()
        {
            if (!AsrIsLocal && !TtsIsLocal)
            {
                if (_engine != null)
                {
                    _engine.Dispose();
                    _engine = null;
                }
                return;
            }
            if (_engine != null) return;

            OrtDevice device = Plugin.Cfg.Mode.Value == DeviceMode.Cpu ? OrtDevice.Cpu : OrtDevice.Gpu;
            int threads = Math.Max(1, Environment.ProcessorCount / 2);
            _engine = new LocalEngine(ModelStore.Root, Plugin.PluginDirectory, device, threads, delegate (string message)
            {
                Plugin.Log.LogInfo(message);
            });
        }

        internal static void Shutdown()
        {
            if (_engine != null)
            {
                _engine.Dispose();
                _engine = null;
            }
        }

        /// <summary>
        /// One-line status for the menu. Local reports what is on disk; the mod's own server is asked
        /// for /health; a speech service is described rather than called, because a "check" button
        /// should not spend the player's quota.
        /// </summary>
        internal static void Check(Action<string> report)
        {
            if (Plugin.Cfg == null)
            {
                report("not configured yet");
                return;
            }

            StringBuilder text = new StringBuilder();
            text.Append("ASR: ").Append(DescribeAsr());
            text.Append(". TTS: ").Append(DescribeTts());
            report(text.ToString());

            if (Plugin.Cfg.AsrService.Value == SttService.Server ||
                Plugin.Cfg.TtsService.Value == TtsService.Server)
            {
                string url;
                try
                {
                    url = Plugin.Cfg.BaseUrl.Value.Trim().TrimEnd('/') + "/health";
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Cannot read BaseUrl: " + ex.Message);
                    return;
                }
                VoiceRuntime.Instance.StartCoroutine(CheckRemote(url));
            }
        }

        private static string DescribeAsr()
        {
            if (!AsrIsLocal) return Plugin.Cfg.AsrService.Value + " (" + DescribeKey(Plugin.Cfg.AsrService.Value) + ")";

            long v2 = ModelStore.MissingBytes("parakeet-v2");
            long v3 = ModelStore.MissingBytes("parakeet-v3");
            StringBuilder text = new StringBuilder("Parakeet v2 ");
            text.Append(v2 == 0 ? "on disk" : v2 / 1048576L + " MB to fetch");
            text.Append(", v3 ").Append(v3 == 0 ? "on disk" : v3 / 1048576L + " MB to fetch");
            return text.ToString();
        }

        private static string DescribeTts()
        {
            if (!TtsIsLocal) return Plugin.Cfg.TtsService.Value + " (" + DescribeKey(Plugin.Cfg.TtsService.Value) + ")";

            long kokoro = ModelStore.MissingBytes("kokoro");
            return "Kokoro " + (kokoro == 0 ? "on disk" : kokoro / 1048576L + " MB to fetch");
        }

        private static string DescribeKey(SttService service)
        {
            switch (service)
            {
                case SttService.Deepgram: return HasKey(Plugin.Cfg.DeepgramApiKey) ? "key set" : "API key missing";
                case SttService.Gladia: return HasKey(Plugin.Cfg.GladiaApiKey) ? "key set" : "API key missing";
                case SttService.OpenAi: return HasKey(Plugin.Cfg.OpenAiApiKey) ? "key set" : "API key missing";
                default: return "own server";
            }
        }

        private static string DescribeKey(TtsService service)
        {
            switch (service)
            {
                case TtsService.ElevenLabs:
                    return HasKey(Plugin.Cfg.ElevenLabsApiKey)
                        ? "key set, voice " + Plugin.Cfg.ElevenLabsVoiceId.Value
                        : "API key missing";
                case TtsService.Deepgram:
                    return HasKey(Plugin.Cfg.DeepgramApiKey)
                        ? "key set, voice " + Plugin.Cfg.DeepgramTtsVoice.Value
                        : "API key missing";
                case TtsService.OpenAi:
                    return HasKey(Plugin.Cfg.OpenAiApiKey)
                        ? "key set, voice " + Plugin.Cfg.OpenAiTtsVoice.Value
                        : "API key missing";
                default: return "own server";
            }
        }

        private static bool HasKey(BepInEx.Configuration.ConfigEntry<string> entry)
        {
            try { return !string.IsNullOrEmpty(entry.Value.Trim()); }
            catch (Exception) { return false; }
        }

        private static IEnumerator CheckRemote(string url)
        {
            HttpResult result = null;
            yield return VoiceHttp.Get(url, 10, delegate (HttpResult r) { result = r; });
            if (result != null && result.Ok && !string.IsNullOrEmpty(result.Text))
            {
                string text = result.Text;
                Plugin.Log.LogInfo("Voice backend health: " +
                                   (text.Length <= 200 ? text : text.Substring(0, 200) + "..."));
            }
            else
            {
                Plugin.Log.LogWarning("Voice backend health check failed: " +
                                      (result != null ? result.Describe() : "no response"));
            }
        }

        /// <summary>Loads what the configuration says will be needed, in the background, once.</summary>
        internal static IEnumerator WarmUp()
        {
            if (_engine == null) yield break;

            string model = "parakeet-" + LanguageMap.CodeFor(Plugin.Cfg.EnglishModel.Value);
            if (ModelStore.MissingBytes(model) == 0)
            {
                Start(delegate { _engine.LoadAsr(model); });
            }
            if (ModelStore.MissingBytes("kokoro") == 0)
            {
                Start(delegate { _engine.LoadTts(); });
            }
        }

        /// <summary>The engine assumes one request at a time; a second one is told to wait rather than queued.</summary>
        internal static bool Acquire()
        {
            return System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
        }

        internal static void Release()
        {
            System.Threading.Interlocked.Exchange(ref _busy, 0);
        }

        private static void Start(Action work)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex) { Plugin.Log.LogWarning("Local model load failed: " + ex.Message); }
            });
        }

        /// <summary>Text for one clip of 16 kHz mono audio, one request at a time.</summary>
        internal static IEnumerator Transcribe(float[] mono, string language, ParakeetModel model,
            Action<string, float> ok, Action<string> failed)
        {
            while (!Acquire()) yield return null;
            try
            {
                IEnumerator work = TranscribeCore(mono, language, model, ok, failed);
                while (work.MoveNext()) yield return work.Current;
            }
            finally
            {
                Release();
            }
        }

        private static IEnumerator TranscribeCore(float[] mono, string language, ParakeetModel model,
            Action<string, float> ok, Action<string> failed)
        {
            if (!AsrIsLocal)
            {
                byte[] wav = WavCodec.EncodePcm16(mono, 16000, 1);
                yield return RemoteApi.Transcribe(wav, language, ok, failed);
                yield break;
            }

            string directory = "parakeet-" + LanguageMap.CodeFor(model);
            yield return Prepare(directory, failed);
            if (!_ready) yield break;

            string text = null;
            float confidence = 0;
            string error = null;
            bool finished = false;

            Start(delegate
            {
                try
                {
                    AsrResult result = _engine.Transcribe(mono, directory);
                    text = result.Text;
                    confidence = result.Confidence;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    finished = true;
                }
            });

            while (!finished) yield return null;
            if (error != null) failed(error);
            else ok(text ?? "", confidence);
        }

        private static bool _ready;

        /// <summary>Downloads and loads the model the request needs before it runs.</summary>
        private static IEnumerator Prepare(string directory, Action<string> failed)
        {
            _ready = false;
            if (ModelStore.MissingBytes(directory) > 0)
            {
                bool downloaded = false;
                yield return ModelStore.Ensure(directory, null, failed, delegate { downloaded = true; });
                if (!downloaded) yield break;
            }

            if (_engine == null) Create();
            if (_engine == null)
            {
                failed("The local backend is not available.");
                yield break;
            }

            bool loaded = false;
            string error = null;
            Start(delegate
            {
                try
                {
                    if (directory == "kokoro") _engine.LoadTts();
                    else _engine.LoadAsr(directory);
                    loaded = true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            });

            while (!loaded && error == null) yield return null;
            if (error != null)
            {
                failed(error);
                yield break;
            }
            _ready = true;
        }

        /// <summary>24 kHz mono audio for one line, cloned from the reference clip when there is one.</summary>
        internal static IEnumerator Synthesize(string text, string language, string referenceWav,
            Action<float[], int, string> ok, Action<string> failed)
        {
            while (!Acquire()) yield return null;
            try
            {
                IEnumerator work = SynthesizeCore(text, language, referenceWav, ok, failed);
                while (work.MoveNext()) yield return work.Current;
            }
            finally
            {
                Release();
            }
        }

        private static IEnumerator SynthesizeCore(string text, string language, string referenceWav,
            Action<float[], int, string> ok, Action<string> failed)
        {
            if (!TtsIsLocal)
            {
                if (Plugin.Cfg.TtsService.Value == TtsService.Server)
                {
                    yield return RemoteApi.Synthesize(text, language, referenceWav,
                        delegate (float[] samples, int sampleRate) { ok(samples, sampleRate, "server"); },
                        failed);
                }
                else
                {
                    string name = Plugin.Cfg.TtsService.Value.ToString();
                    yield return RemoteApi.Synthesize(text, language, referenceWav,
                        delegate (float[] samples, int sampleRate) { ok(samples, sampleRate, name); },
                        failed);
                }
                yield break;
            }

            if (!LanguageMap.KokoroSpeaks(language))
            {
                failed("Kokoro does not speak '" + language + "' yet; this line needs the game's own voice.");
                yield break;
            }

            yield return Prepare("kokoro", failed);
            if (!_ready) yield break;

            float[] audio = null;
            int rate = KokoroTts.SampleRate;
            string detail = null;
            string error = null;
            bool finished = false;

            Start(delegate
            {
                try
                {
                    audio = _engine.Synthesize(text, referenceWav, out rate, out detail);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                finally
                {
                    finished = true;
                }
            });

            while (!finished) yield return null;
            if (error != null) failed(error);
            else ok(audio, rate, detail);
        }

    }
}
