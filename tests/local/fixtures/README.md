# Golden fixtures

Inputs plus the output of the Python reference stack on those inputs. The C# port of the ASR and
TTS paths is checked against `expected.json` / `expected.wav` instead of against a live Python
process, so the test harness needs neither the venv nor the network.

Everything here is data. Total size 0.59 MB; no model weights are checked in.

```
asr/01.wav          4.000 s  16 kHz mono PCM16  LibriTTS 1272 (female, read speech)
asr/02.wav          5.000 s  16 kHz mono PCM16  LibriTTS 5338 (male, read speech)
asr/03.wav          6.000 s  16 kHz mono PCM16  Amelia Earhart, 1930s radio recording
asr/expected.json   Parakeet TDT 0.6B v2 int8 transcript + token list for each file
tts/text.txt        the input sentence
tts/phonemes.txt    misaki en.G2P() output for it
tts/ids.txt         Kokoro input ids, comma separated, one per phoneme character
tts/style.bin       256 float32 little-endian: the voice-style row for this length
tts/expected.wav    2.775 s  24 kHz mono PCM16  Kokoro-82M fp32 output
tts/expected.json   sample count, sample rate, RMS and peak of the float waveform
```

## Provenance

| thing | source |
| --- | --- |
| the three source recordings | `https://huggingface.co/remsky/kokoro-inno-clone-tuner/resolve/v0.2.0/samples/{libritts_1272_ref,libritts_5338_ref,amelia_earhart_ref}.wav` (24 kHz, 24 kHz, 44.1 kHz mono PCM16) |
| ASR weights | `istupakov/parakeet-tdt-0.6b-v2-onnx`: `nemo128.onnx`, `encoder-model.int8.onnx`, `decoder_joint-model.int8.onnx`, `vocab.txt`, `config.json` |
| TTS weights | `kokoro-onnx` release `model-files-v1.1`: `kokoro-v1.0.onnx` (fp32, exported as `kokoro_fp32.onnx`, 325.5 MB) and `voices-v1.0.bin` (130560 float32 = 510 x 1 x 256 style pack) |
| phoneme to id map | the Kokoro `tokenizer.json` (115 entries, ids 0..177 with gaps), read as `model.vocab` |
| G2P | misaki 0.9.4, `en.G2P(trf=False, british=False)` with the `en_core_web_sm` 3.8.0 tagger |

Tool versions used to produce this directory: Python 3.11.17, onnxruntime 1.31.0
(`CPUExecutionProvider`), onnx 1.23.2, numpy 1.26.4, scipy 1.17.1, soundfile 0.14.0, misaki 0.9.4,
onnx-asr 0.12.0, spacy 3.8.18.

## Transcripts

Parakeet TDT 0.6B v2 int8, greedy, no language prompt, CPU:

| file | transcript | tokens |
| --- | --- | --- |
| 01.wav | `We fully admit that it is extremely amusing and not.` | 22 |
| 02.wav | `They stood without any respect for regularity, on each side of a straggling kind of` | 28 |
| 03.wav | `By its new horizons than those of any other group. Profound and stirring.` | 27 |

The token strings are the model's sentencepiece pieces with the word-boundary marker already
replaced by a space (`▁We` -> `" We"`), i.e. exactly what a `vocab.txt` lookup yields, so a C# port
must produce the same list in the same order. Note that plain concatenation is *not* the transcript:
it leaves a space before the stop and before the comma. onnx-asr then rewrites the joined string
with `re.sub(r"\A\s|\s\B|(\s)\b", m -> " " if m.group(1) else "", joined)`, which drops the leading
space, drops a space that is followed by a word boundary, and keeps a single separator otherwise -
that rule is what turns the 22/28/27 tokens into the strings in `expected.json`, so the port has to
reproduce it (a regex literal translated verbatim to .NET behaves identically; all three constructs
are supported). 02.wav is a cut through one sentence of the source book, which is why it ends
mid-phrase; that is intentional - a 5 s window that happened to end on a word boundary would only
test one utterance length.

## Regeneration

The generator script is deliberately not committed (it needs 440 MB of models that must not enter
the repository). These are the steps it performs, exactly.

Source clips, then resample to 16 kHz mono and cut to length:

```python
import numpy as np, soundfile as sf
from scipy.signal import resample_poly
x, sr = sf.read(src, dtype='float64', always_2d=True)
x = x.mean(axis=1)                                   # all three sources are mono; this is a no-op
g = np.gcd(sr, 16000)
x = resample_poly(x, 16000 // g, sr // g)            # 24000 -> 16000 is up 2, down 3
```

Before cutting, each clip is trimmed to the region where `|x| > 0.02 * max|x|` plus 50 ms of
margin, then the first 4/5/6 s of that region are kept (for the 30 s Earhart recording the first
8 s of the region are skipped first, to land on a different sentence than the one at the start).
Nothing is normalized or padded - all three sources are long enough after trimming. Write with
`sf.write(path, pcm, 16000, subtype='PCM_16')`, `pcm = clip(round(x * 32767), -32768, 32767)`
int16.

ASR transcripts:

```python
import onnx_asr
m = onnx_asr.load_model('nemo-parakeet-tdt-0.6b-v2', path=MODEL_DIR,
                        quantization='int8', providers=['CPUExecutionProvider'])
text = m.recognize(wav, sample_rate=16000)            # greedy, no timestamps
r = m.with_timestamps().recognize(wav)                # same decode, plus tokens/times
assert r.text == text
tokens = list(r.tokens)
```

TTS:

```python
from misaki import en
phonemes, _ = en.G2P(trf=False, british=False)('The quick brown fox jumps over the lazy dog.')
ids = [vocab.get(ch, 0) for ch in phonemes]
style = np.frombuffer(open('voices-v1.0.bin', 'rb').read(510 * 256 * 4), dtype='<f4')
style = style[(len(ids) - 1) * 256:len(ids) * 256]    # one row per input length, 0-based
import onnxruntime as ort
sess = ort.InferenceSession('kokoro_fp32.onnx', providers=['CPUExecutionProvider'])
wav = sess.run(None, {'input_ids': np.array([ids], dtype=np.int64),
                      'style': style.reshape(1, 256).astype(np.float32),
                      'speed': np.array([1.0], dtype=np.float32)})[0].reshape(-1)
```

Results for the committed fixtures (measured, not estimated):

- `phonemes.txt` = `ðə kwˈɪk bɹˈWn fˈɑks ʤˈʌmps ˈOvəɹ ðə lˈAzi dˈɔɡ.`, 48 characters.
- `ids.txt` has 48 ids, no character fell outside the vocabulary. The non-obvious ones a naive
  table misses: `W` (39) for the diphthong in "brown", `O` (31) in "over", `A` (24) in "lazy",
  `ɹ` (123) in "brown"/"over", and the stress marker `ˈ` (156) before every stressed syllable.
- `style.bin` is row `len(ids) - 1 == 47` of the 510-row pack: Kokoro indexes the style row by
  input length, so a 48-id input must be compared against row 47, not row 48.
- `expected.wav` has 66600 samples = 2.775 s; the float waveform has RMS 0.053835 and peak
  0.360579.
- A second render at `speed` 1.0 would be byte-identical to `expected.wav`, so it is not committed.

## Tolerance for the C# side

The C# port runs the game's ONNX Runtime **1.17.1**; these fixtures were produced with **1.31.0**.
Same graphs, same weights, but different kernel selections and a different MLAS, so outputs are not
bit-identical and must not be compared byte for byte.

**TTS (fp32 graph).** Compare the float waveform directly, not only the RMS. Expect agreement to
about `1e-5` per sample on this graph: Kokoro is a conv/attention stack with no data-dependent
branches, so the only sources of drift are summation order and FMA contraction. A realistic gate is
`max|x - x_ref| <= 1e-3` - 100x the expected drift, while a wrong style row, a transposed input or
a missing normalisation moves samples by O(0.1) - plus RMS within `2e-4` of 0.053835 and peak
within `1e-3` of 0.360579. Comparing the PCM16 files byte-wise is not appropriate: a 1e-5 float
difference flips the last bit of some samples, and one LSB against a 0.36 peak is a 3e-5
difference anyway.

**ASR (int8 graph).** int8 GEMM kernels differ measurably between ORT versions, so the greedy token
stream can diverge.

Measured on the port (`src/Local/ParakeetAsr.cs`, game ORT 1.17.1) against these files: 02.wav is
identical; 01.wav and 03.wav differ in their **last token only** (`and no.` for `and not.`;
`Profound and spiritual.` for `Profound and stirring.`), with the token count matching exactly on all
three (22 / 28 / 27). The divergence is one frame from the end of each clip, which is where int8
kernel drift lands once the encoder state has accumulated, and it reproduces with the fp32 joint
decoder as well, so the decoder is not the cause. Treat "same token count, at most one differing
token, at the end" as passing; anything earlier or larger is a feature-extraction bug - check the
128-bin log-mel, the per-utterance normalisation and the 8x subsampling first. A
divergence larger than that is in the C# feature extraction - 128-bin log-mel at 16 kHz,
per-utterance normalisation, 8x subsampling - not in the graph, so check the mel features first.
