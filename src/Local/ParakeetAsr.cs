using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ScamWYF.VoiceBackend.Ort;

namespace ScamWYF.VoiceBackend.Local
{
    /// <summary>What the recogniser produced for one clip.</summary>
    internal sealed class AsrResult
    {
        internal string Text;
        internal float Confidence;
        internal int TokenCount;
        internal long Frames;

        internal static readonly AsrResult Empty = new AsrResult { Text = "", Confidence = 0f };
    }

    /// <summary>
    /// NVIDIA Parakeet TDT 0.6B, in process: log-mel, conformer encoder, transducers
    /// decoding by the duration-aware greedy loop.
    /// </summary>
    /// <remarks>
    /// The model arrives as four files from istupakov/parakeet-tdt-0.6b-v{2,3}-onnx:
    /// <c>nemo128.onnx</c> (the NeMo log-mel front end, so no feature code lives here),
    /// <c>encoder-model.int8.onnx</c>, <c>decoder_joint-model.int8.onnx</c> and <c>vocab.txt</c>.
    ///
    /// Decoding is the TDT loop rather than CTC: the joint network emits a token and a duration per
    /// encoder frame, and the duration says how many frames to skip. That is why the loop below can
    /// advance by more than one frame per step and why it needs a cap on tokens emitted per frame -
    /// without it a runaway model would never move forward.
    ///
    /// Shapes are the pinned checkpoint's, quoted from the graph itself: the decoder state is two
    /// [2, 1, 640] LSTM tensors, the joint output is 1030 wide (1025 vocabulary slots including
    /// <c>&lt;blk&gt;</c>, then 5 duration bins).
    /// </remarks>
    internal sealed class ParakeetAsr : IDisposable
    {
        internal const int SampleRate = 16000;

        private const int EncoderWidth = 1024;
        private const int StateLayers = 2;
        private const int StateWidth = 640;
        private const int MaxTokensPerStep = 10;
        private const int MaxSeconds = 60;

        private readonly OrtSession _preprocess;
        private readonly OrtSession _encoder;
        private readonly OrtSession _decoder;
        private readonly Dictionary<long, string> _vocabulary = new Dictionary<long, string>();
        private long _blank = -1;

        /// <summary>Set by the caller when the mod's debug logging is on; one line per decode step.</summary>
        internal Action<string> Trace { get; set; }

        private ParakeetAsr(OrtSession preprocess, OrtSession encoder, OrtSession decoder)
        {
            _preprocess = preprocess;
            _encoder = encoder;
            _decoder = decoder;
        }

        internal static ParakeetAsr Load(string directory, OrtDevice device, int threads, Action<string> warn)
        {
            string preprocess = Path.Combine(directory, "nemo128.onnx");
            string encoder = Quantized(directory, "encoder-model");
            string decoder = Quantized(directory, "decoder_joint-model");
            string vocab = Path.Combine(directory, "vocab.txt");

            if (!File.Exists(preprocess)) throw new FileNotFoundException("Parakeet preprocessor missing.", preprocess);
            if (encoder == null)
            {
                throw new FileNotFoundException("Parakeet encoder missing.", Path.Combine(directory, "encoder-model.onnx"));
            }
            if (decoder == null)
            {
                throw new FileNotFoundException("Parakeet joint decoder missing.",
                    Path.Combine(directory, "decoder_joint-model.onnx"));
            }

            ParakeetAsr result = new ParakeetAsr(
                OrtSession.Create(preprocess, OrtDevice.Cpu, 1, warn),
                OrtSession.Create(encoder, device, threads, warn),
                OrtSession.Create(decoder, OrtDevice.Cpu, 1, warn));
            result.ReadVocabulary(vocab);
            return result;
        }

        private static string Quantized(string directory, string stem)
        {
            string int8 = Path.Combine(directory, stem + ".int8.onnx");
            if (File.Exists(int8)) return int8;
            string plain = Path.Combine(directory, stem + ".onnx");
            return File.Exists(plain) ? plain : null;
        }

        private void ReadVocabulary(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Parakeet vocabulary missing.", path);
            string[] lines = File.ReadAllText(path, Encoding.UTF8).Split(new[] { '\n' });
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int split = line.LastIndexOf(' ');
                if (split <= 0 || split == line.Length - 1) continue;

                long id;
                if (!long.TryParse(line.Substring(split + 1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out id))
                {
                    continue;
                }

                // SentencePiece marks a word boundary with U+2581; the detokeniser wants a space there.
                _vocabulary[id] = line.Substring(0, split).Replace('\u2581', ' ');
            }

            if (_vocabulary.Count == 0) throw new InvalidDataException("The Parakeet vocabulary is empty: " + path);

            foreach (KeyValuePair<long, string> entry in _vocabulary)
            {
                if (string.Equals(entry.Value, "<blk>", StringComparison.Ordinal)) _blank = entry.Key;
            }
            if (_blank < 0) throw new InvalidDataException("The Parakeet vocabulary has no <blk> token: " + path);
        }

        /// <summary>Clips longer than a minute are cut: the game never sends more, and a stale buffer should not OOM.</summary>
        internal AsrResult Transcribe(float[] samples)
        {
            if (samples == null || samples.Length == 0) return AsrResult.Empty;

            int limit = SampleRate * MaxSeconds;
            int count = samples.Length > limit ? limit : samples.Length;

            float[] features;
            long featureCount;
            using (OrtTensor waveform = OrtTensor.Float(samples, new long[] { 1, count }))
            using (OrtTensor length = OrtTensor.Long(new long[] { count }, new long[] { 1 }))
            {
                OrtTensor[] outputs = _preprocess.Run(new[] { waveform, length });
                try
                {
                    features = outputs[0].ToFloats();
                    long[] shape = outputs[0].Shape;
                    featureCount = shape[shape.Length - 1];
                }
                finally
                {
                    for (int i = 0; i < outputs.Length; i++) outputs[i].Dispose();
                }
            }

            float[] encoded;
            long encodedFrames;
            using (OrtTensor input = OrtTensor.Float(features, new long[] { 1, 128, featureCount }))
            using (OrtTensor lengths = OrtTensor.Long(new long[] { featureCount }, new long[] { 1 }))
            {
                OrtTensor[] outputs = _encoder.Run(new[] { input, lengths });
                try
                {
                    encoded = outputs[0].ToFloats();
                    long[] shape = outputs[0].Shape;
                    encodedFrames = shape[shape.Length - 1];
                }
                finally
                {
                    for (int i = 0; i < outputs.Length; i++) outputs[i].Dispose();
                }
            }

            if (Trace != null)
            {
                Trace(string.Format(CultureInfo.InvariantCulture,
                    "features {0} frames, encoded {1} frames, feat {2:F5} {3:F5} {4:F5}, enc {5:F5} {6:F5}",
                    featureCount, encodedFrames, features[0], features[featureCount], features[2 * featureCount], encoded[0],
                    encoded[encodedFrames]));
            }

            return Decode(encoded, encodedFrames);
        }

        private AsrResult Decode(float[] encoded, long frames)
        {
            List<long> tokens = new List<long>(64);
            double confidenceSum = 0;
            int confidences = 0;
            long frame = 0;

            // Two LSTM state tensors, [layers, batch, hidden], zero at the start of every utterance.
            float[] stateA = new float[StateLayers * StateWidth];
            float[] stateB = new float[StateLayers * StateWidth];
            float[] frameValues = new float[EncoderWidth];
            int emitted = 0;

            while (frame < frames)
            {
                for (int channel = 0; channel < EncoderWidth; channel++)
                {
                    frameValues[channel] = encoded[channel * frames + frame];
                }

                long[] last = new long[1];
                last[0] = tokens.Count > 0 ? tokens[tokens.Count - 1] : _blank;

                float[] logits;
                float[] nextA;
                float[] nextB;
                using (OrtTensor input = OrtTensor.Float(frameValues, new long[] { 1, EncoderWidth, 1 }))
                using (OrtTensor targets = OrtTensor.Int(new int[] { (int)last[0] }, new long[] { 1, 1 }))
                using (OrtTensor targetLength = OrtTensor.Int(new int[] { 1 }, new long[] { 1 }))
                using (OrtTensor inputA = OrtTensor.Float(stateA, new long[] { StateLayers, 1, StateWidth }))
                using (OrtTensor inputB = OrtTensor.Float(stateB, new long[] { StateLayers, 1, StateWidth }))
                {
                    OrtTensor[] outputs = _decoder.Run(new[] { input, targets, targetLength, inputA, inputB });
                    try
                    {
                        // The graph returns outputs, prednet_lengths, output_states_1, output_states_2.
                        logits = outputs[0].ToFloats();
                        nextA = outputs[2].ToFloats();
                        nextB = outputs[3].ToFloats();
                    }
                    finally
                    {
                        for (int i = 0; i < outputs.Length; i++) outputs[i].Dispose();
                    }
                }

                int token = ArgMax(logits, 0, _vocabulary.Count);
                int duration = ArgMax(logits, _vocabulary.Count, logits.Length) - _vocabulary.Count;

                if (Trace != null)
                {
                    Trace(string.Format(CultureInfo.InvariantCulture,
                        "frame {0}/{1} token {2} duration {3} emitted {4}", frame, frames, token, duration, emitted));
                }

                if (token != _blank)
                {
                    stateA = nextA;
                    stateB = nextB;
                    tokens.Add(token);
                    confidenceSum += Probability(logits, token);
                    confidences++;
                    emitted++;
                }

                if (duration > 0)
                {
                    frame += duration;
                    emitted = 0;
                }
                else if (token == _blank || emitted >= MaxTokensPerStep)
                {
                    frame++;
                    emitted = 0;
                }
            }

            AsrResult result = new AsrResult();
            result.Text = Detokenize(tokens);
            result.Confidence = confidences > 0 ? (float)(confidenceSum / confidences) : 0f;
            result.TokenCount = tokens.Count;
            result.Frames = frames;
            return result;
        }

        private static int ArgMax(float[] values, int start, int end)
        {
            int best = start;
            float bestValue = values[start];
            for (int i = start + 1; i < end; i++)
            {
                if (values[i] > bestValue)
                {
                    bestValue = values[i];
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Softmax over the vocabulary block of one joint output, for the reported confidence.</summary>
        private float Probability(float[] logits, int token)
        {
            int count = _vocabulary.Count;
            float max = logits[0];
            for (int i = 1; i < count; i++)
            {
                if (logits[i] > max) max = logits[i];
            }

            double total = 0;
            for (int i = 0; i < count; i++) total += Math.Exp(logits[i] - max);
            return (float)(Math.Exp(logits[token] - max) / total);
        }

        private string Detokenize(List<long> tokens)
        {
            StringBuilder text = new StringBuilder(tokens.Count * 2);
            for (int i = 0; i < tokens.Count; i++)
            {
                string piece;
                if (_vocabulary.TryGetValue(tokens[i], out piece)) text.Append(piece);
            }

            // Pieces arrive with leading spaces, so joining them leaves runs of whitespace and a leading
            // space; collapsing here is what onnx-asr's DECODE_SPACE_PATTERN does.
            return CollapseSpaces(text.ToString());
        }

        internal static string CollapseSpaces(string text)
        {
            StringBuilder result = new StringBuilder(text.Length);
            bool pendingSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = result.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    result.Append(' ');
                    pendingSpace = false;
                }
                result.Append(c);
            }
            return result.ToString();
        }

        public void Dispose()
        {
            _preprocess.Dispose();
            _encoder.Dispose();
            _decoder.Dispose();
        }
    }
}
