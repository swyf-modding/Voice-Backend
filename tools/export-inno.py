#!/usr/bin/env python3
"""Build the inno-kokoro zero-shot voice tuner assets from the upstream safetensors.

    python tools/export-inno.py
    python tools/export-inno.py --weights .work/tuner.safetensors --out assets/inno

Inputs: the weights of remsky/kokoro-inno-clone-tuner revision v0.2.0

    curl -L -o .work/tuner.safetensors \
        https://huggingface.co/remsky/kokoro-inno-clone-tuner/resolve/v0.2.0/model.safetensors

and, for the stock fallback voice, one of the kokoro-onnx voice archives (Apache-2.0)

    curl -L -o .work/voices-v1.0.bin \
        https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1/voices-v1.0.bin

Outputs:

    assets/inno/encoder.onnx      the distilled WeSpeaker ResNet34 speaker encoder, fp32.
    assets/inno/tuner.bin         the style head, the tilt direction, the baked blend tables and the
                                  metadata scalars the enrolment needs, in one flat little-endian file.
    assets/inno/default-voice.bin the stock af_heart pack, unchanged, as the no-reference fallback.

The encoder graph starts where the upstream torch model stops being cheap to port: it takes kaldi
fbank features [B, 80, T] (time down the last axis, samples as produced by fbank() in inno_encoder.py
before its permute) and returns the unit embedding [B, 512] in UniSpeech-SAT-sv space. The fbank front
end itself, the prosody measurements and the nonnegative blend are plain arithmetic and live in
src/Local/InnoTuner.cs, so the plugin never needs torch.

Everything here mirrors github.com/remsky/inno-kokoro v0.2.0 (inno_encoder.py, inno_enroll.py,
inno_prosody.py). The module tree below is a copy of _ResNet34/SpeakerEncoder with the fbank front end
split off, so state_dict names still line up with the enc.* keys one for one and a strict load fails
loudly if the upstream shapes ever move.
"""

import argparse
import os
import struct
import sys

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

KOKORO_VOICES = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1/voices-v1.0.bin"
DEFAULT_VOICE = "af_heart"  # the warmest of the stock American female voices
ROWS = 510  # a stock Kokoro pack: one row per phoneme count
STYLE = 256  # style vector width: 128 timbre + 128 predictor
HALF = STYLE // 2
MAGIC = b"INNOTUN1"


class _Block(nn.Module):
    """ResNet34 basic block, as in wespeaker's models/resnet.py."""

    def __init__(self, cin, c, stride=1):
        super().__init__()
        self.conv1 = nn.Conv2d(cin, c, 3, stride, 1, bias=False)
        self.bn1 = nn.BatchNorm2d(c)
        self.conv2 = nn.Conv2d(c, c, 3, 1, 1, bias=False)
        self.bn2 = nn.BatchNorm2d(c)
        self.shortcut = nn.Sequential()
        if stride != 1 or cin != c:
            self.shortcut = nn.Sequential(nn.Conv2d(cin, c, 1, stride, bias=False), nn.BatchNorm2d(c))

    def forward(self, x):
        out = F.relu(self.bn1(self.conv1(x)))
        return F.relu(self.bn2(self.conv2(out)) + self.shortcut(x))


class _ResNet34(nn.Module):
    """conv/bn stack with mean+std (TSTP) pooling and the 5120->256 segment layer."""

    def __init__(self, m=32, feat_dim=80, embed_dim=256):
        super().__init__()
        self.conv1 = nn.Conv2d(1, m, 3, 1, 1, bias=False)
        self.bn1 = nn.BatchNorm2d(m)
        cin = m
        for i, (c, s) in enumerate([(m, 1), (m * 2, 2), (m * 4, 2), (m * 8, 2)]):
            n = [3, 4, 6, 3][i]
            blocks = []
            for st in [s] + [1] * (n - 1):
                blocks.append(_Block(cin, c, st))
                cin = c
            setattr(self, f"layer{i + 1}", nn.Sequential(*blocks))
        self.seg_1 = nn.Linear(feat_dim // 8 * m * 8 * 2, embed_dim)

    def features(self, x):
        """[B, 1, 80, T] fbank features -> [B, 256] pooled embedding."""
        out = F.relu(self.bn1(self.conv1(x)))
        for i in range(4):
            out = getattr(self, f"layer{i + 1}")(out)
        stats = torch.cat([out.mean(-1).flatten(1), torch.sqrt(out.var(-1) + 1e-7).flatten(1)], 1)
        return self.seg_1(stats)


class SpeakerEncoder(nn.Module):
    """fbank features [B, 80, T] -> unit embedding [B, 512]. Upstream loads `enc.*` into this tree."""

    def __init__(self):
        super().__init__()
        self.backbone = _ResNet34()
        self.proj = nn.Sequential(nn.Linear(256, 1024), nn.GELU(), nn.Linear(1024, 512))

    def forward(self, feat):
        return F.normalize(self.proj(self.backbone.features(feat.unsqueeze(1))), dim=-1)


def export_encoder(sd, path):
    """Trace the feature->embedding half to ONNX at opset 17 and check it against torch."""
    model = SpeakerEncoder()
    model.load_state_dict(sd, strict=True)  # exact key/shape parity with upstream, or this throws
    model.eval().requires_grad_(False)

    # 300 frames is 3 s of 10 ms hops: long enough that every pooling axis is more than one element.
    example = torch.zeros(1, 80, 300)
    torch.onnx.export(
        model,
        (example,),
        path,
        input_names=["feat"],
        output_names=["embed"],
        opset_version=17,
        do_constant_folding=True,
        dynamo=False,
        dynamic_axes={"feat": {0: "batch", 2: "frames"}, "embed": {0: "batch"}},
    )

    import onnx
    import onnxruntime as ort

    graph = onnx.load(path)
    ops = sorted({n.op_type for n in graph.graph.node})
    print(f"encoder: {len(graph.graph.node)} nodes, ops {', '.join(ops)}")
    unknown = set(ops) - {
        "Add", "BatchNormalization", "Cast", "Clip", "Concat", "Conv", "Div", "Erf", "Gemm", "Identity",
        "MatMul", "Mul", "Pow", "ReduceL2", "ReduceMean", "ReduceProd", "ReduceSum", "Relu", "Reshape",
        "Shape", "Sqrt", "Sub", "Transpose", "Unsqueeze", "Expand", "Gather", "Constant", "ConstantOfShape",
        "Slice", "Flatten",
    }
    if unknown:
        raise SystemExit(f"unexpected ops in the graph: {sorted(unknown)}")

    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    worst = 0.0
    for seed, frames in ((0, 120), (1, 400), (2, 900)):
        gen = torch.Generator().manual_seed(seed)
        feat = torch.randn(1, 80, frames, generator=gen)
        with torch.no_grad():
            ref = model(feat).numpy()
        got = sess.run(None, {"feat": feat.numpy()})[0]
        d = float(np.abs(ref - got).max())
        worst = max(worst, d)
        print(f"encoder: frames={frames} max|torch-onnx| = {d:.3e}")
    if worst >= 1e-4:
        raise SystemExit(f"onnx export drifted from torch: {worst:.3e} >= 1e-4")
    print(f"encoder: max|torch-onnx| over 3 tensors = {worst:.3e}  ({os.path.getsize(path)} bytes)")
    return worst


def write_tuner_bin(path, weights):
    """Write the flat tuner file. Layout (little-endian, no padding):

        0   8s   magic b"INNOTUN1"
        8   I    pack_count V                       the stock voices the blend can pick from (28)
        12  I    row_count  R                       510, one per phoneme count
        16  I    embed_dim  E                       512, the encoder embedding width
        20  I    hidden_dim N                       512, the style head's hidden width
        24  I    style_dim  S                       256, the style vector width (2 * predictor half)
        28  I    half       H                       128, the predictor half width
        32  I    name_bytes                         length of the trailing name block
        36  f    blend_gate                         F0 gate in st (4.0)
        40  f    grade_pen                          cost per unit of weight on a pack below C (1.0)
        44  f    tilt_mean                          reference tilt in dB/oct (-7.8)
        48  f    tilt_sd                            tilt sd in dB/oct (1.8)
        52  3f   blend_scale[3]                     per-stat cost scale (1.0, 1.1, 0.8)
        64  N*E f  style_w0   heads.style.0.weight, row-major [out, in]
            N   f  style_b0   heads.style.0.bias
            S*N f  style_w2   heads.style.2.weight, row-major [out, in]
            S   f  style_b2   heads.style.2.bias
            S   f  tilt_dir   heads.tilt.weight squeezed to [S]
            V*3 f  blend_stats  [V, 3]: pack F0 mean st, F0 sd st, syllables/s
            V   f  blend_grades [V]: pack quality, A = 4.0, C = 2.0
            V*R*H e rows        float16 predictor halves, [V, R, H]
            name_bytes bytes  pack names, NUL-terminated UTF-8, in pack order
    """
    meta = weights.metadata()
    names = meta["blend_names"].split(",")
    style_w0 = weights.get_tensor("heads.style.0.weight").float().contiguous()
    style_b0 = weights.get_tensor("heads.style.0.bias").float().contiguous()
    style_w2 = weights.get_tensor("heads.style.2.weight").float().contiguous()
    style_b2 = weights.get_tensor("heads.style.2.bias").float().contiguous()
    tilt = weights.get_tensor("heads.tilt.weight").squeeze(1).float().contiguous()
    stats = weights.get_tensor("blend.stats").float().contiguous()
    grades = weights.get_tensor("blend.grades").float().contiguous()
    rows = weights.get_tensor("blend.rows").to(torch.float16).contiguous()
    gate = float(meta["blend_gate"])
    scale = [float(v) for v in meta["blend_scale"].split(",")]
    grade_pen = float(meta["grade_pen"])
    tilt_mean = float(meta["tilt_mean"])
    tilt_sd = float(meta["tilt_sd"])

    v, r, h = rows.shape
    e, n = style_w0.shape[1], style_w0.shape[0]
    s = style_w2.shape[0]
    if (v, r, h) != (len(names), ROWS, HALF) or stats.shape != (v, 3) or grades.shape != (v,):
        raise SystemExit(f"blend tables do not look like a v0.2.0 pack: rows {tuple(rows.shape)} "
                         f"stats {tuple(stats.shape)} grades {tuple(grades.shape)} names {len(names)}")
    if e != 512 or s != STYLE or tilt.numel() != STYLE or tuple(style_w2.shape) != (s, n) \
            or tuple(style_b2.shape) != (s,) or tuple(style_b0.shape) != (n,):
        raise SystemExit(f"style head is not the expected [E={e}, N={n}] -> S={s} shape: "
                         f"w0 {tuple(style_w0.shape)} w2 {tuple(style_w2.shape)} tilt {tuple(tilt.shape)}")
    bad = [c for name in names for c in name if not (c.isalnum() or c == "_")]
    if bad:
        raise SystemExit(f"pack names are not plain identifiers: {names}")

    name_blob = b"".join(name.encode("utf-8") + b"\0" for name in names)
    with open(path, "wb") as f:
        f.write(MAGIC)
        f.write(struct.pack("<7I", v, r, e, n, s, h, len(name_blob)))
        f.write(struct.pack("<4f", gate, grade_pen, tilt_mean, tilt_sd))
        f.write(struct.pack("<3f", *scale))
        for t in (style_w0, style_b0, style_w2, style_b2, tilt):
            f.write(t.numpy().astype("<f4").tobytes())
        f.write(stats.numpy().astype("<f4").tobytes())
        f.write(grades.numpy().astype("<f4").tobytes())
        f.write(rows.numpy().astype("<f2").tobytes())
        f.write(name_blob)

    expected = 64 + 4 * (n * e + n + s * n + s + s + v * 3 + v) + 2 * v * r * h + len(name_blob)
    actual = os.path.getsize(path)
    if actual != expected:
        raise SystemExit(f"tuner.bin is {actual} bytes, layout says {expected}")
    print(
        f"tuner: {v} packs, rows [{v}, {r}, {h}] fp16, stats [{v}, 3], style head [{e}] -> {n} -> {s}, "
        f"gate {gate}, scale {scale}, grade_pen {grade_pen}, tilt {tilt_mean}+-{tilt_sd}, "
        f"{actual} bytes"
    )
    return actual


def write_default_voice(path, archive):
    """Copy one stock pack out of the kokoro-onnx voice archive as raw float32.

    The archive is a zip of <voice>.npy files, each [510, 1, 256] float32, the same numbers kokoro-onnx
    uses as voices/<voice>.bin. af_heart is written unchanged: the mod plays it when a caller has no
    usable reference clip, and there is no reason for the tuner to touch it.
    """
    import zipfile

    with zipfile.ZipFile(archive) as archive_handle:
        pack = np.load(archive_handle.open(DEFAULT_VOICE + ".npy"))
    if pack.shape != (ROWS, 1, STYLE) or pack.dtype != np.float32:
        raise SystemExit(f"{DEFAULT_VOICE}.npy is {pack.shape} {pack.dtype}; expected ({ROWS}, 1, {STYLE}) float32")
    data = pack.reshape(ROWS * STYLE).astype("<f4").tobytes()
    with open(path, "wb") as handle:
        handle.write(data)
    print(f"voice: {DEFAULT_VOICE} [{ROWS}, {STYLE}] float32, {len(data)} bytes, "
          f"max|value| {float(np.abs(pack).max()):.6f}")
    return len(data)


def main():
    ap = argparse.ArgumentParser(description="Export the inno-kokoro encoder and blend tables.")
    ap.add_argument("--weights", default=os.path.join(REPO, ".work", "tuner.safetensors"),
                    help="model.safetensors from remsky/kokoro-inno-clone-tuner v0.2.0")
    ap.add_argument("--out", default=os.path.join(REPO, "assets", "inno"), help="output directory")
    ap.add_argument("--voices", default=os.path.join(REPO, ".work", "voices-v1.0.bin"),
                    help="kokoro-onnx voices-v1.0.bin archive, for the stock fallback voice")
    args = ap.parse_args()

    if not os.path.exists(args.weights):
        raise SystemExit(f"{args.weights} not found; see this file's header for the download command")
    os.makedirs(args.out, exist_ok=True)

    from safetensors import safe_open

    handle = safe_open(args.weights, "pt", device="cpu")
    keys = list(handle.keys())
    enc = {}
    for k in keys:
        if k.startswith("enc."):
            t = handle.get_tensor(k)
            enc[k[4:]] = t.float() if t.is_floating_point() else t
    if not enc:
        raise SystemExit(f"{args.weights} has no enc.* keys; wrong file?")
    meta = handle.metadata() or {}
    print(f"weights: {len(keys)} tensors ({len(enc)} encoder), {len(meta)} metadata fields, "
          f"version {meta.get('version', 'unknown')}")

    export_encoder(enc, os.path.join(args.out, "encoder.onnx"))
    write_tuner_bin(os.path.join(args.out, "tuner.bin"), handle)
    if os.path.exists(args.voices):
        write_default_voice(os.path.join(args.out, "default-voice.bin"), args.voices)
    else:
        print(f"voice: skipped, {args.voices} not found; download it from\n       {KOKORO_VOICES}",
              file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
