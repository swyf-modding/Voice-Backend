using System;
using System.Collections.Generic;
using System.IO;
using ScamWYF.VoiceBackend.Ort;

namespace ScamWYF.VoiceBackend.Local
{
    /// <summary>
    /// The in-process voice engine: loads the ONNX graphs the local backend needs, on demand, and
    /// runs them. Nothing here touches Unity, HTTP or the game, so it can be exercised by the console
    /// harness in tests/local.
    /// </summary>
    /// <remarks>
    /// Loading is lazy and serialised. The Parakeet encoder reads 600 MB and takes about a second to
    /// lay out, and Kokoro reads 325 MB; doing that at plugin start would stall the main menu for no
    /// reason. The first caller to need a model pays for it, behind a lock so two callers do not both
    /// pay.
    ///
    /// Model layout under <c>modelRoot</c>:
    /// <code>
    /// parakeet-v2/{nemo128.onnx,encoder-model.int8.onnx,decoder_joint-model.int8.onnx,vocab.txt}
    /// parakeet-v3/...
    /// kokoro/kokoro-v1.0.onnx
    /// </code>
    /// Voices (inno-kokoro assets) are not here: they ship beside the plugin as
    /// <c>VoiceBackend/inno/</c>, because they are part of the build, not a download.
    /// </remarks>
    internal sealed class LocalEngine : IDisposable
    {
        private readonly string _modelRoot;
        private readonly string _assetRoot;
        private OrtDevice _device;
        private readonly int _threads;
        private readonly Action<string> _log;
        private readonly object _gate = new object();

        private readonly Dictionary<string, ParakeetAsr> _asr = new Dictionary<string, ParakeetAsr>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, KokoroVoice> _voices = new Dictionary<string, KokoroVoice>(StringComparer.OrdinalIgnoreCase);
        private KokoroTts _kokoro;
        private InnoTuner _inno;
        private bool _g2pLoaded;

        internal LocalEngine(string modelRoot, string assetRoot, OrtDevice device, int threads, Action<string> log)
        {
            _modelRoot = modelRoot;
            _assetRoot = assetRoot;
            _device = device;
            _threads = threads;
            _log = log ?? delegate { };
        }

        internal bool TtsReady
        {
            get
            {
                lock (_gate) return _kokoro != null;
            }
        }

        internal static string DirectoryFor(string modelRoot, string model)
        {
            return Path.Combine(modelRoot, model);
        }

        internal void LoadG2P()
        {
            lock (_gate)
            {
                if (_g2pLoaded) return;
                string path = Path.Combine(_assetRoot, "g2p", "en-lexicon.bin");
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("The English pronunciation lexicon is missing.", path);
                }
                EnglishG2P.Load(File.ReadAllBytes(path));
                _g2pLoaded = true;
            }
        }

        internal void LoadAsr(string model)
        {
            lock (_gate)
            {
                EnsureAsr(model);
            }
        }

        private ParakeetAsr EnsureAsr(string model)
        {
            ParakeetAsr existing;
            if (_asr.TryGetValue(model, out existing)) return existing;

            string directory = DirectoryFor(_modelRoot, model);
            long started = Environment.TickCount;
            ParakeetAsr asr = ParakeetAsr.Load(directory, _device, _threads, _log);
            _asr[model] = asr;
            _log(string.Format("Loaded Parakeet {0} from {1} in {2} ms.", model, directory,
                Environment.TickCount - started));
            return asr;
        }

        /// <summary>Transcribes 16 kHz mono audio. <paramref name="model"/> is the directory name.</summary>
        internal AsrResult Transcribe(float[] samples, string model)
        {
            ParakeetAsr asr;
            lock (_gate) asr = EnsureAsr(model);
            try
            {
                return asr.Transcribe(samples);
            }
            catch (Exception ex)
            {
                if (!FallBackToCpu(ex, "Parakeet " + model)) throw;
            }

            lock (_gate) asr = EnsureAsr(model);
            return asr.Transcribe(samples);
        }

        /// <summary>
        /// Drops back to the CPU providers, once, and says why.
        /// </summary>
        /// <remarks>
        /// The game's DirectML provider is a 1.17 build; it refuses some dynamic-shape nodes outright
        /// (Kokoro's ConvTranspose is one). A failed run is not worth failing a caller over, and a
        /// session that already failed once is not worth retrying on the same provider, so the models
        /// are rebuilt on the CPU and every later request uses that.
        /// </remarks>
        private bool FallBackToCpu(Exception ex, string what)
        {
            lock (_gate)
            {
                if (_device != OrtDevice.Gpu) return false;
                _device = OrtDevice.Cpu;

                foreach (KeyValuePair<string, ParakeetAsr> entry in _asr) entry.Value.Dispose();
                _asr.Clear();
                if (_kokoro != null)
                {
                    _kokoro.Dispose();
                    _kokoro = null;
                }
                if (_inno != null)
                {
                    _inno.Dispose();
                    _inno = null;
                }
            }

            _log(string.Format(
                "{0} failed on DirectML ({1}); every model is being reloaded on the CPU.", what, FirstLine(ex.Message)));
            return true;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "no message";
            int newline = text.IndexOfAny(new[] { '\r', '\n' });
            string line = newline > 0 ? text.Substring(0, newline) : text;
            return line.Length <= 200 ? line : line.Substring(0, 200) + "...";
        }

        internal void LoadTts()
        {
            lock (_gate) EnsureTts();
        }

        private KokoroTts EnsureTts()
        {
            if (_kokoro != null) return _kokoro;

            string model = Path.Combine(_modelRoot, "kokoro", "kokoro-v1.0.onnx");
            if (!File.Exists(model)) throw new FileNotFoundException("The Kokoro model is missing.", model);

            LoadG2P();
            long started = Environment.TickCount;
            _kokoro = KokoroTts.Load(model, _device, _threads, _log);
            _log(string.Format("Loaded Kokoro from {0} in {1} ms.", model, Environment.TickCount - started));
            return _kokoro;
        }

        private InnoTuner EnsureInno()
        {
            if (_inno != null) return _inno;

            string encoder = Path.Combine(_assetRoot, "inno", "encoder.onnx");
            string weights = Path.Combine(_assetRoot, "inno", "tuner.bin");
            if (!File.Exists(encoder) || !File.Exists(weights))
            {
                throw new FileNotFoundException("The inno-kokoro tuner assets are missing.", encoder);
            }

            _inno = InnoTuner.Load(encoder, weights, _device, _threads, _log);
            return _inno;
        }

        /// <summary>
        /// Resolves the voice for a caller: the tuned pack for their reference clip when one is
        /// available, otherwise the stock voice that ships with the mod.
        /// </summary>
        private KokoroVoice ResolveVoice(string referencePath)
        {
            if (!string.IsNullOrEmpty(referencePath) && File.Exists(referencePath))
            {
                string key = referencePath + "|" + File.GetLastWriteTimeUtc(referencePath).Ticks;
                KokoroVoice cached;
                if (_voices.TryGetValue(key, out cached)) return cached;

                try
                {
                    float[] samples;
                    int rate;
                    samples = WavReader.ReadMono(referencePath, out rate);
                    long started = Environment.TickCount;
                    string[] packs;
                    float[] rows = EnsureInno().Enroll(samples, rate, out packs);
                    KokoroVoice voice = KokoroVoice.FromRows(rows, rows.Length / KokoroVoice.StyleLength);
                    _voices[key] = voice;
                    _log(string.Format("Tuned a Kokoro voice from {0} in {1} ms (packs: {2}).",
                        Path.GetFileName(referencePath), Environment.TickCount - started, string.Join(", ", packs)));
                    return voice;
                }
                catch (Exception ex)
                {
                    _log("Could not tune a voice from " + referencePath + ": " + ex.Message);
                }
            }

            KokoroVoice fallback;
            string path = Path.Combine(_assetRoot, "inno", "default-voice.bin");
            string fallbackKey = "default";
            if (_voices.TryGetValue(fallbackKey, out fallback)) return fallback;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("No reference voice and no default Kokoro voice.", path);
            }

            fallback = KokoroVoice.Load(path);
            _voices[fallbackKey] = fallback;
            return fallback;
        }

        /// <summary>
        /// Renders one line. Returns 24 kHz mono audio; <paramref name="detail"/> names the voice source
        /// for the log line the mod already writes per request.
        /// </summary>
        internal float[] Synthesize(string text, string referencePath, out int sampleRate, out string detail)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                sampleRate = KokoroTts.SampleRate;
                detail = "empty";
                return new float[0];
            }

            KokoroTts kokoro;
            lock (_gate) kokoro = EnsureTts();

            KokoroVoice voice;
            lock (_gate) voice = ResolveVoice(referencePath);

            string phonemes = EnglishG2P.Phonemize(text);
            List<char> dropped = new List<char>();
            long[] ids = KokoroVocab.Tokenize(phonemes, dropped);
            if (dropped.Count > 0)
            {
                _log("Kokoro has no phoneme for: " + new string(dropped.ToArray()));
            }

            sampleRate = KokoroTts.SampleRate;
            detail = "Kokoro";
            try
            {
                return kokoro.Synthesize(ids, voice, 1.0f);
            }
            catch (Exception ex)
            {
                if (!FallBackToCpu(ex, "Kokoro")) throw;
            }

            lock (_gate) kokoro = EnsureTts();
            return kokoro.Synthesize(ids, voice, 1.0f);
        }

        internal string Phonemes(string text)
        {
            lock (_gate) LoadG2P();
            return EnglishG2P.Phonemize(text);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (KeyValuePair<string, ParakeetAsr> entry in _asr) entry.Value.Dispose();
                _asr.Clear();
                if (_kokoro != null)
                {
                    _kokoro.Dispose();
                    _kokoro = null;
                }
                if (_inno != null)
                {
                    _inno.Dispose();
                    _inno = null;
                }
            }
        }
    }
}
