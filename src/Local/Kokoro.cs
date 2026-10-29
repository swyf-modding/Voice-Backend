using System;
using System.IO;
using ScamWYF.VoiceBackend.Ort;

namespace ScamWYF.VoiceBackend.Local
{
    /// <summary>
    /// A Kokoro voice: 510 rows of 256 style values, one row per phoneme count.
    /// </summary>
    /// <remarks>
    /// Kokoro conditions on a style vector chosen by sequence length, which is why a "voice" is a table
    /// rather than a single embedding, and why a cloned voice is produced by writing all 510 rows
    /// (<see cref="InnoTuner"/>). The file is raw little-endian float32, the same layout the stock
    /// <c>voices/*.bin</c> files use.
    /// </remarks>
    internal sealed class KokoroVoice
    {
        internal const int StyleLength = 256;

        private readonly float[] _rows;

        private KokoroVoice(float[] rows, int count)
        {
            _rows = rows;
            RowCount = count;
        }

        internal int RowCount { get; private set; }

        internal static KokoroVoice Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return FromBytes(bytes, Path.GetFileName(path));
        }

        internal static KokoroVoice FromBytes(byte[] bytes, string name)
        {
            const int unit = StyleLength * 4;
            if (bytes.Length == 0 || bytes.Length % unit != 0)
            {
                throw new InvalidDataException(string.Format(
                    "{0} is {1} bytes, which is not a whole number of {2}-byte style rows.", name, bytes.Length, unit));
            }

            int count = bytes.Length / unit;
            float[] rows = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, rows, 0, bytes.Length);
            return new KokoroVoice(rows, count);
        }

        /// <summary>The style row for a phoneme sequence length, clamped to the table.</summary>
        internal void Row(int phonemeCount, float[] destination)
        {
            int index = phonemeCount - 1;
            if (index < 0) index = 0;
            if (index >= RowCount) index = RowCount - 1;
            int offset = index * StyleLength;
            Array.Copy(_rows, offset, destination, 0, StyleLength);
        }

        /// <summary>Wraps a pack that was computed in memory, such as a tuned voice.</summary>
        internal static KokoroVoice FromRows(float[] rows, int count)
        {
            if (rows == null) throw new ArgumentNullException("rows");
            if (count <= 0 || rows.Length < count * StyleLength)
            {
                throw new ArgumentException(string.Format(
                    "a voice needs at least {0} floats, got {1}", count * StyleLength, rows == null ? 0 : rows.Length));
            }
            return new KokoroVoice(rows, count);
        }

        internal float[] Copy()
        {
            float[] copy = new float[_rows.Length];
            Array.Copy(_rows, copy, _rows.Length);
            return copy;
        }
    }

    /// <summary>
    /// Kokoro-82M, the speech half of the local backend: text phonemes plus a style vector in, 24 kHz
    /// audio out, in one graph. There is no sampler loop and no autoregression to drive.
    /// </summary>
    internal sealed class KokoroTts : IDisposable
    {
        internal const int SampleRate = 24000;

        private readonly OrtSession _session;
        private readonly float[] _style = new float[KokoroVoice.StyleLength];

        private KokoroTts(OrtSession session)
        {
            _session = session;
        }

        internal static KokoroTts Load(string modelPath, OrtDevice device, int threads, Action<string> warn)
        {
            OrtSession session = OrtSession.Create(modelPath, device, threads, warn);
            return new KokoroTts(session);
        }

        /// <summary>
        /// Renders phoneme ids. <paramref name="voice"/> supplies the style row for the length of this
        /// sequence; <paramref name="speed"/> is Kokoro's own rate multiplier (1.0 is the model's voice).
        /// </summary>
        internal float[] Synthesize(long[] phonemeIds, KokoroVoice voice, float speed)
        {
            if (phonemeIds.Length == 0) return new float[0];
            if (phonemeIds.Length > KokoroVocab.MaxPhonemes)
            {
                throw new ArgumentException(string.Format(
                    "{0} phonemes is longer than Kokoro's {1}-symbol table; split the text.",
                    phonemeIds.Length, KokoroVocab.MaxPhonemes));
            }

            voice.Row(phonemeIds.Length, _style);

            OrtTensor inputIds = OrtTensor.Long(phonemeIds, new long[] { 1, phonemeIds.Length });
            OrtTensor style = OrtTensor.Float(_style, new long[] { 1, KokoroVoice.StyleLength });
            OrtTensor rate = OrtTensor.Float(new[] { speed }, new long[] { 1 });
            try
            {
                OrtTensor[] outputs = _session.Run(new[] { inputIds, style, rate });
                try
                {
                    return outputs[0].ToFloats();
                }
                finally
                {
                    for (int i = 0; i < outputs.Length; i++) outputs[i].Dispose();
                }
            }
            finally
            {
                inputIds.Dispose();
                style.Dispose();
                rate.Dispose();
            }
        }

        public void Dispose()
        {
            _session.Dispose();
        }
    }
}
