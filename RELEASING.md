# Releasing

Two artefacts make up a release:

1. the mod DLL, built by `./build.ps1`, and
2. the self-contained server folder, built by `server/build-server.ps1`.

## Layout

The release zip should unpack to the game root with this shape:

```text
BepInEx/
|-- core/
|   `-- ScamWYF.Modding.Core.dll
`-- plugins/
    |-- ScamWYF.VoiceBackend.dll
    `-- VoiceBackend/
        |-- VoiceBackend.Server.exe
        `-- _internal/...
```

The server is deliberately one folder below `plugins`: BepInEx scans only the top level of
`plugins` for DLLs, so the runtime's DLLs are not treated as plugins, and `ServerLauncher` looks for
`plugins/VoiceBackend/VoiceBackend.Server.exe` first.

## Build

```powershell
./server/build-server.ps1 -Backend auto -Clean
./build.ps1 -Version 0.2.0 -NoCopy
```

`build-server.ps1` runs PyInstaller and produces `server/dist/VoiceBackend.Server/`. It needs no
Python on the target machine, and the model weights are **not** included; the server downloads them
on first use.

| `-Backend` | Result | Uncompressed |
|---|---|---|
| `cpu` | CPU only | ~1.7 GB |
| `cuda` | NVIDIA CUDA, CPU fallback | ~7.5 GB |
| `rocm` | AMD ROCm on Linux | depends on wheel |
| `directml` | Windows DirectML ASR; Chatterbox CPU | ~2 GB |

The runtime picks the device from the mod's `Mode`; a CUDA build also runs on CPU when no NVIDIA GPU
is visible. Publish the backend matching the audience, or publish CPU and CUDA as separate assets.

## Package

```powershell
$stage = "release/BepInEx"
New-Item -ItemType Directory -Force "$stage/core", "$stage/plugins" | Out-Null

Copy-Item "bin/ScamWYF.VoiceBackend.dll"          "$stage/plugins/"
Copy-Item "vendor/ScamWYF.Modding.Core/bin/ScamWYF.Modding.Core.dll" "$stage/core/"
Copy-Item -Recurse -Force "server/dist/VoiceBackend.Server" "$stage/plugins/VoiceBackend"

# 7-Zip is far faster than Compress-Archive for 19k files and a multi-gigabyte tree.
7z a -tzip -mx=5 "release/Voice-Backend-0.2.0.zip" "release/BepInEx/*"
```

A GitHub release asset has a 2 GB limit per file. The CPU server folder (1.68 GB) zips smaller than
that and fits in one asset. The CUDA folder is too large for one asset: publish it as split 7-Zip
volumes, as a separate download, or keep the CPU build as the fallback and document the GPU asset
separately.

## Checklist

- [ ] `./build.ps1 -NoCopy -Test` passes.
- [ ] The tag is on the commit that produced the DLL; `build.ps1` stamps the version from it.
- [ ] `VoiceBackend.Server.exe` starts and `/health` responds on a machine without Python.
- [ ] `/health` reports the intended `device.torch` and `device.onnx_active` for `Mode = Gpu`.
- [ ] The zip contains `BepInEx/plugins/VoiceBackend/VoiceBackend.Server.exe` and its `_internal`.
- [ ] No model files and no `.venv` are in the zip.
