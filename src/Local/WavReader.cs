using System;
using System.IO;

namespace ScamWYF.VoiceBackend.Local
{
    /// <summary>Reads the WAV files the game hands over as reference voices.</summary>
    /// <remarks>
    /// Only what the game actually stores: RIFF/WAVE, 16- or 24-bit PCM or 32-bit float, mono or
    /// stereo. Anything else is rejected by name rather than guessed at, because a misread reference
    /// clip would clone the wrong timbre instead of failing.
    /// </remarks>
    internal static class WavReader
    {
        /// <summary>
        /// Rebuilds an IEEE-754 single from its bits. The game's corlib has neither
        /// <c>BitConverter.ToSingle</c> nor the explicit-layout attributes a union would need, so the
        /// value is assembled arithmetically. Only float WAV files take this path.
        /// </summary>
        private static float ToSingle(int bits)
        {
            if (bits == 0) return 0f;
            int sign = (bits >> 31) != 0 ? -1 : 1;
            int exponent = (bits >> 23) & 0xFF;
            int mantissa = bits & 0x7FFFFF;

            if (exponent == 255)
            {
                // The game's corlib has no float constants for these either.
                if (mantissa != 0) return 0f / 0f;
                return sign > 0 ? 1e38f * 10f : -1e38f * 10f;
            }
            if (exponent == 0) return (float)(sign * mantissa * 1.401298464324817e-45);
            return (float)(sign * (1.0 + mantissa / 8388608.0) * System.Math.Pow(2.0, exponent - 127));
        }

        internal static float[] ReadMono(string path, out int sampleRate)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                throw new InvalidDataException(path + " is not a RIFF file.");
            }

            sampleRate = 0;
            int channels = 1;
            int bits = 16;
            int format = 1;
            int dataStart = -1;
            int dataLength = 0;

            int pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                string id = new string(new[]
                {
                    (char)bytes[pos], (char)bytes[pos + 1], (char)bytes[pos + 2], (char)bytes[pos + 3]
                });
                int size = BitConverter.ToInt32(bytes, pos + 4);

                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(bytes, pos + 8);
                    channels = BitConverter.ToInt16(bytes, pos + 10);
                    sampleRate = BitConverter.ToInt32(bytes, pos + 12);
                    bits = BitConverter.ToInt16(bytes, pos + 22);
                    if (format == 0xFFFE && size >= 40)
                    {
                        // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                        format = BitConverter.ToInt16(bytes, pos + 8 + 24);
                    }
                }
                else if (id == "data")
                {
                    dataStart = pos + 8;
                    dataLength = Math.Min(size, bytes.Length - dataStart);
                    break;
                }

                if (channels <= 0) channels = 1;
                pos += 8 + size + (size & 1);
            }

            if (dataStart < 0) throw new InvalidDataException(path + " has no data chunk.");
            if (sampleRate <= 0) throw new InvalidDataException(path + " has no sample rate.");

            bool floating = format == 3;
            int bytesPerSample = bits / 8;
            if (bytesPerSample <= 0 || (bits != 16 && bits != 24 && bits != 32))
            {
                throw new InvalidDataException(path + " is " + bits + "-bit, which this reader does not handle.");
            }

            int frames = dataLength / (bytesPerSample * channels);
            float[] mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int at = dataStart + (i * channels + c) * bytesPerSample;
                    if (floating)
                    {
                        sum += ToSingle(BitConverter.ToInt32(bytes, at));
                    }
                    else if (bytesPerSample == 2)
                    {
                        sum += BitConverter.ToInt16(bytes, at) / 32768.0;
                    }
                    else if (bytesPerSample == 3)
                    {
                        int value = bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16);
                        if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                        sum += value / 8388608.0;
                    }
                    else
                    {
                        sum += BitConverter.ToInt32(bytes, at) / 2147483648.0;
                    }
                }
                mono[i] = (float)(sum / channels);
            }

            return mono;
        }
    }
}
