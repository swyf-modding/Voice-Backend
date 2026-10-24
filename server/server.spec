# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller recipe for the self-contained voice backend server.

The output is a one-folder application:

    dist/VoiceBackend.Server/VoiceBackend.Server.exe
    dist/VoiceBackend.Server/_internal/...

It bundles Python, onnxruntime, torch and the Chatterbox package. Model weights are still downloaded
on first use; they are not part of the executable.
"""

import os
import sys
import importlib.metadata
from pathlib import Path

from PyInstaller.utils.hooks import collect_all, collect_dynamic_libs, copy_metadata

packages = [
    "chatterbox",
    "perth",
    "onnx_asr",
    "transformers",
    "diffusers",
    "safetensors",
    "huggingface_hub",
    "librosa",
    "soundfile",
    "numba",
    "llvmlite",
    "sklearn",
    "scipy",
    "uvicorn",
    "fastapi",
    "pydantic",
    "pydantic_core",
    "torch",
    "torchaudio",
    "pkg_resources",
]

datas = []
binaries = []
hiddenimports = []

for package in packages:
    try:
        package_datas, package_binaries, package_hidden = collect_all(package)
        datas += package_datas
        binaries += package_binaries
        hiddenimports += package_hidden
    except Exception as exc:  # a missing optional package should not stop the build
        print("collect_all(%s) skipped: %s" % (package, exc))

# onnxruntime-gpu and torch load CUDA/cuDNN DLLs from package directories rather than from the
# system. PyInstaller's hooks do not always carry the nvidia-* wheels, so add every DLL at the top
# level of _internal where onnxruntime's loader and PyInstaller's PATH both see it.
try:
    import site
    import sysconfig

    candidates = [Path(sysconfig.get_paths()["purelib"]) / "nvidia"]
    candidates += [Path(path) / "nvidia" for path in site.getsitepackages()]
    nvidia_root = next((path for path in candidates if path.is_dir()), None)
    if nvidia_root is None:
        print("nvidia package directory not found; GPU builds may fall back to CPU.")
    else:
        for dll in nvidia_root.rglob("*.dll"):
            binaries.append((str(dll), "."))
        print("Collected nvidia DLLs from %s" % nvidia_root)
except Exception as exc:
    print("nvidia DLL collection skipped: %s" % exc)

try:
    binaries += collect_dynamic_libs("onnxruntime")
except Exception as exc:
    print("onnxruntime DLL collection skipped: %s" % exc)

hiddenimports += [
    "app",
    "uvicorn.logging",
    "uvicorn.loops.auto",
    "uvicorn.protocols.http.auto",
    "uvicorn.protocols.websockets.auto",
    "uvicorn.lifespan.on",
]

# Library version checks (diffusers, transformers, torch and friends) use importlib.metadata at
# runtime. Without the dist-info folders those checks fail even though the modules are bundled.
for distribution in importlib.metadata.distributions():
    try:
        name = distribution.metadata["Name"]
        if name:
            datas += copy_metadata(name)
    except Exception:
        pass

excludes = [
    "matplotlib",
    "tkinter",
    "IPython",
    "pytest",
    "gradio",
    "gradio_client",
    "notebook",
    "jupyter",
    "jupyter_client",
    "jupyter_core",
    "tensorboard",
    "torchvision",
    "torchtext",
    "torchdata",
]

a = Analysis(
    ["app.py"],
    pathex=[os.path.abspath(".")],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=excludes,
    noarchive=False,
)

pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="VoiceBackend.Server",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    console=True,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name="VoiceBackend.Server",
)
