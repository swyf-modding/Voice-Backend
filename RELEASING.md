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
./server/build-server.ps1 -Clean
./build.ps1 -Version 0.2.0 -NoCopy
```

`build-server.ps1` runs PyInstaller and produces `server/dist/VoiceBackend.Server/`. The result is
about 1.7 GB and needs no Python on the target machine. The model weights are **not** included; the
server downloads them on first use.

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

A GitHub release asset has a 2 GB limit per file. The server folder is 1.68 GB uncompressed and
zips smaller than that; keep the release to one zip. If a host has a tighter limit, split the zip
with 7-Zip volumes and document the extra step, or publish the server folder as a separate asset.

## Checklist

- [ ] `./build.ps1 -NoCopy -Test` passes.
- [ ] The tag is on the commit that produced the DLL; `build.ps1` stamps the version from it.
- [ ] `VoiceBackend.Server.exe` starts and `/health` responds on a machine without Python.
- [ ] The zip contains `BepInEx/plugins/VoiceBackend/VoiceBackend.Server.exe` and its `_internal`.
- [ ] No model files and no `.venv` are in the zip.
