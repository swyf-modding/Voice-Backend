using System;

namespace ScamWYF.VoiceBackend.Api
{
    /// <summary>
    /// Turns the bodies the speech APIs return into float samples.
    /// </summary>
    /// <remarks>
    /// Three shapes have to be read, and each provider picks one:
    /// <list type="bullet">
    /// <item>raw little-endian 16-bit mono PCM, which is what ElevenLabs' <c>pcm_24000</c> and OpenAI's
    /// <c>response_format=pcm</c> return - no container, no header, just samples;</item>
    /// <item>a WAV container, which is what Deepgram's speak endpoint returns when asked for
    /// <c>container=wav</c> and what the mod's own server answers;</item>
    /// <item>anything else (mp3, opus), which is rejected by name rather than misread as noise, because
    /// a wrong guess here is not a crash, it is a caller speaking static.</item>
    /// </list>
    /// The sample rate is the caller's business: each provider is asked for 24 kHz, and the caller
    /// resamples from there if it needs to.
    /// </remarks>
    internal static class PcmCodec
    {
        internal const int RemoteSampleRate = 24000;

        internal static bool LooksLikeWav(byte[] body)
        {
            return body != null && body.Length >= 12 &&
                   body[0] == 'R' && body[1] == 'I' && body[2] == 'F' && body[3] == 'F' &&
                   body[8] == 'W' && body[9] == 'A' && body[10] == 'V' && body[11] == 'E';
        }

        /// <summary>Little-endian signed 16-bit samples to floats in [-1, 1].</summary>
        internal static float[] FromLinear16(byte[] body)
        {
            if (body == null || body.Length < 2) return new float[0];
            int count = body.Length / 2;
            float[] samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                short value = (short)(body[i * 2] | (body[i * 2 + 1] << 8));
                samples[i] = value / 32768f;
            }
            return samples;
        }

        /// <summary>
        /// Reads a provider response for 24 kHz mono audio, refusing formats this mod cannot decode.
        /// </summary>
        internal static float[] Read(byte[] body, string what, out int sampleRate)
        {
            sampleRate = RemoteSampleRate;
            if (body == null || body.Length == 0)
            {
                throw new InvalidOperationException(what + " returned an empty body.");
            }

            if (LooksLikeWav(body))
            {
                return WavCodec.DecodeMono(body, out sampleRate);
            }

            if (body.Length >= 3 && body[0] == (byte)'I' && body[1] == (byte)'D' && body[2] == (byte)'3')
            {
                throw new InvalidOperationException(what + " returned MP3; ask it for PCM or WAV.");
            }

            if (body.Length >= 4 && body[0] == 0x1A && body[1] == 0x45 && body[2] == 0xDF && body[3] == 0xA3)
            {
                throw new InvalidOperationException(what + " returned WebM/Opus; ask it for PCM or WAV.");
            }

            if (body.Length >= 4 && body[0] == (byte)'O' && body[1] == (byte)'g' && body[2] == (byte)'g' &&
                body[3] == (byte)'S')
            {
                throw new InvalidOperationException(what + " returned Ogg/Opus; ask it for PCM or WAV.");
            }

            return FromLinear16(body);
        }
    }
}
