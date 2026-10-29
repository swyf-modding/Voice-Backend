using System;
using System.IO;

namespace ScamWYF.VoiceBackend
{
    /// <summary>
    /// The little bit of WAV handling both directions need. Whisper receives float samples, the
    /// companion server speaks WAV on the wire, and PocketTTS wants float samples again at 24 kHz.
    /// </summary>
    internal static class WavCodec
    {
        public const int PocketSampleRate = 24000;

        public static byte[] EncodePcm16(float[] samples, int sampleRate, int channels)
        {
            if (samples == null) samples = new float[0];
            if (channels < 1) channels = 1;
            if (sampleRate < 1) sampleRate = 16000;

            var dataBytes = samples.Length * 2;
            using (var stream = new MemoryStream(44 + dataBytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

                writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                writer.Write(16);                       // PCM fmt chunk size
                writer.Write((short)1);                 // PCM
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * 2); // byte rate
                writer.Write((short)(channels * 2));     // block align
                writer.Write((short)16);                 // bits per sample

                writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                writer.Write(dataBytes);

                for (int i = 0; i < samples.Length; i++)
                {
                    var value = samples[i];
                    if (float.IsNaN(value)) value = 0f;
                    if (value > 1f) value = 1f;
                    else if (value < -1f) value = -1f;
                    writer.Write((short)Math.Round(value * 32767f));
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>Decode the PCM formats a TTS server plausibly returns. Returns interleaved floats.</summary>
        public static float[] Decode(byte[] wav, out int sampleRate, out int channels)
        {
            sampleRate = PocketSampleRate;
            channels = 1;
            if (wav == null || wav.Length < 44) return new float[0];

            using (var stream = new MemoryStream(wav, false))
            using (var reader = new BinaryReader(stream))
            {
                if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
                reader.ReadInt32();
                if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

                int format = 1;
                int bits = 16;
                int dataOffset = -1;
                int dataLength = 0;

                while (stream.Position + 8 <= stream.Length)
                {
                    var id = new string(reader.ReadChars(4));
                    var size = reader.ReadInt32();
                    var next = stream.Position + size + (size & 1);

                    if (id == "fmt ")
                    {
                        format = reader.ReadInt16();
                        channels = reader.ReadInt16();
                        sampleRate = reader.ReadInt32();
                        reader.ReadInt32(); // byte rate
                        reader.ReadInt16(); // block align
                        bits = reader.ReadInt16();
                        if (format == 0xFFFE && size >= 40)
                        {
                            reader.ReadInt16(); // cbSize
                            reader.ReadInt16(); // valid bits
                            reader.ReadInt32(); // channel mask
                            format = reader.ReadInt16(); // sub format
                        }
                    }
                    else if (id == "data")
                    {
                        dataOffset = (int)stream.Position;
                        dataLength = (int)Math.Min(size, stream.Length - stream.Position);
                    }

                    if (next > stream.Length) break;
                    stream.Position = next;
                }

                if (dataOffset < 0 || channels < 1) return new float[0];

                stream.Position = dataOffset;
                var bytesPerSample = Math.Max(1, bits / 8);
                var count = dataLength / bytesPerSample;
                var samples = new float[count];

                for (int i = 0; i < count; i++)
                {
                    if (format == 3 && bits == 32)
                    {
                        samples[i] = reader.ReadSingle();
                    }
                    else if (bits == 8)
                    {
                        samples[i] = (reader.ReadByte() - 128) / 128f;
                    }
                    else if (bits == 16)
                    {
                        samples[i] = reader.ReadInt16() / 32768f;
                    }
                    else if (bits == 24)
                    {
                        var b0 = reader.ReadByte();
                        var b1 = reader.ReadByte();
                        var b2 = reader.ReadByte();
                        var value = b0 | (b1 << 8) | (b2 << 16);
                        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                        samples[i] = value / 8388608f;
                    }
                    else if (bits == 32)
                    {
                        samples[i] = reader.ReadInt32() / 2147483648f;
                    }
                    else
                    {
                        // Unknown width: consume it and leave silence rather than spin.
                        reader.ReadBytes(bytesPerSample);
                        samples[i] = 0f;
                    }
                }

                return samples;
            }
        }

        public static float[] DecodeMono(byte[] wav, out int sampleRate)
        {
            int channels;
            var interleaved = Decode(wav, out sampleRate, out channels);
            if (channels <= 1) return interleaved;

            var frames = interleaved.Length / channels;
            var mono = new float[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (int channel = 0; channel < channels; channel++) sum += interleaved[frame * channels + channel];
                mono[frame] = sum / channels;
            }
            return mono;
        }

        /// <summary>Plain linear resampling. Chatterbox already returns 24 kHz, so this is a guard.</summary>
        /// <summary>
        /// Sample-rate conversion with a windowed-sinc kernel.
        /// </summary>
        /// <remarks>
        /// Linear interpolation is what this used to do, and decimating with it folds 8-12 kHz back
        /// into 4-8 kHz: on a 24 kHz clip converted to 16 kHz it audibly damages exactly the
        /// fricatives speech recognition reads first. Sixteen taps either side with a Hann window and
        /// a cut-off at 45% of the lower Nyquist is short enough to be free here and clean enough that
        /// a round trip through it does not change what a recogniser hears.
        /// </remarks>
        public static float[] ResampleMono(float[] input, int fromRate, int toRate)
        {
            if (input == null || input.Length == 0 || fromRate <= 0 || toRate <= 0 || fromRate == toRate)
                return input ?? new float[0];

            // Math.PI is stripped from the game's corlib, so the constant lives here.
            const double Pi = 3.14159265358979323846;
            const int Taps = 16;
            double ratio = (double)fromRate / toRate;
            double cutoff = 0.45 * (ratio > 1.0 ? 1.0 / ratio : 1.0);
            int outputLength = Math.Max(1, (int)Math.Round(input.Length / ratio));
            float[] output = new float[outputLength];

            for (int i = 0; i < outputLength; i++)
            {
                double center = i * ratio;
                int first = (int)Math.Floor(center) - Taps + 1;
                double sum = 0;
                double weight = 0;
                for (int k = 0; k < 2 * Taps; k++)
                {
                    int index = first + k;
                    if (index < 0) index = 0;
                    else if (index >= input.Length) index = input.Length - 1;

                    double distance = center - index;
                    double x = distance / Taps;
                    if (x <= -1.0 || x >= 1.0) continue;

                    double sinc = distance == 0 ? 1.0 : Math.Sin(Pi * cutoff * distance) /
                        (Pi * cutoff * distance);
                    double window = 0.5 + 0.5 * Math.Cos(Pi * x);
                    double w = sinc * window;

                    sum += input[index] * w;
                    weight += w;
                }

                output[i] = (float)(weight != 0 ? sum / weight : 0);
            }

            return output;
        }

        /// <summary>Interleave mono samples into however many channels the destination expects.</summary>
        public static float[] ToChannels(float[] mono, int channels)
        {
            if (channels <= 1 || mono == null || mono.Length == 0) return mono ?? new float[0];

            var output = new float[mono.Length * channels];
            for (int i = 0; i < mono.Length; i++)
            {
                for (int channel = 0; channel < channels; channel++)
                    output[i * channels + channel] = mono[i];
            }
            return output;
        }
    }
}
