# Build and update VirtualDub2 and the input plugin

[Русский](README.ru.md) · [SyncSyncAV](../README.md)

`engine/upstream.json` records the repository URLs and full commit IDs for the tested revisions. `engine/patches/host.patch` covers window embedding, keyboard control, appearance and file handling. `engine/patches/decoder.patch` changes avlib's handling of AVC/HEVC, M2TS, MPLS, CLPI, Blu-ray ISO images (UDF) and metadata. The SDK bridge is built from `engine/bridge/`.

## Try another upstream revision

Run these commands from the project root. Omit `--revision` to use the pinned commit. Use `--mirror` to get the commit from a local Git clone.

```powershell
python engine/prepare.py host C:/Build/vd-trial --revision <full-commit-SHA>
./engine/build-host.ps1 -Checkout C:/Build/vd-trial
```

The preparation script checks the entire patch before moving the prepared tree into place. If the patch conflicts, Git reports the affected file and context. Existing working trees and previous builds are preserved. For the decoder, use the `decoder` component and `build-decoder.ps1 -Checkout ... -RuntimeDirectory ...`.

For a quick local test, make a copy of a local application build in a test directory and close any instance running from that directory. Copy both `VirtualDub2.exe` and `VirtualDub2.vdi` from `_out/Release_x64/` into its `Engine/` folder. For decoder changes, replace `Engine/plugins64/avlib-1.vdplugin` with the version in `_bin/Release_x64/`. Run `python tests/file_drop.py <test-directory>`. For decoding changes, also open a known playlist on a disc you can test and seek to a frame you can identify.

To resolve a patch conflict, check out the new upstream SHA in a separate full clone and run `git apply --3way --index <path-to-patch>`. Resolve conflicts and stage the changed files. To save the result, run the command below in that clone, substituting the project's patch path. Git writes the patch bytes directly to the file, including under Windows PowerShell.

```powershell
git diff --cached --binary --full-index --output=C:/Build/project/engine/patches/host.patch HEAD
```

Update the SHA in `engine/upstream.json`, check that the patch applies in an empty directory, then commit the patch and SHA together. When the FFmpeg ABI changes, update the dependencies, their hashes and the import library configuration in `engine/build-decoder.ps1`.

## Build and publish on GitHub Actions

1. Open **Actions → Build Windows ZIPs → Run workflow**. Select `main` for a release candidate. After the tests pass, the run's **Artifacts** section contains the Windows x64 application ZIP and its matching source ZIP.
2. Open **Actions → Release Windows ZIPs → Run workflow** on `main`. Enter the numeric ID from the successful build's URL. By default the runner verifies both SHA-256 digests. To publish, select **Publish the verified ZIPs as a GitHub Release**. The runner attaches both original archives to a draft, verifies their digests and publishes a release tagged at the build commit. The release ZIPs stay on GitHub throughout.

The verification log lists each README inside the application ZIP with its size and SHA-256. The runner compares the guides with the build commit and the ZIP's `SHA256.json` manifest.

On Windows Server 2022, the build takes 35 files from the VirtualDub2 2.5.7.1257 release, including five FFmpeg DLLs. It checks the archive SHA-256 and the file hashes in `engine/runtime-files.json`. It downloads the ffmpeg and ffprobe command-line tools separately for testing, and Yasm and Microsoft's MSVCR100.dll for building the host. Asuka uses DirectX 9 libraries extracted from Microsoft's DirectX x64 package to compile shaders. These build-time DLLs live in the tools directory on the runner. Download versions and checksums are pinned in `tools/prepare-ci.py`.

## Reproduce the build locally

Use a clean working tree on Windows with Visual Studio 2022 (MSVC v143, Windows SDK, .NET Framework 4.8), Git, 7-Zip and Python 3.13 x64. Run these commands from the repository root.

```powershell
python -m pip install meson==1.9.1 ninja==1.13.0 pywin32==311
python tools/prepare-ci.py .local/ci-inputs
$inputsDir = (Resolve-Path .local/ci-inputs).Path
$env:PATH = (Join-Path $inputsDir 'bin') + ';' + $env:PATH
./build-release.ps1 -WorkDirectory .local/ci-build -RuntimeDirectory "$inputsDir/runtime" -BuiltBlurayDirectory "$inputsDir/bluray" -OutputDirectory .local/ci-out
```

Preparation creates a fresh input directory. Reuse the populated `ci-inputs` directory to build again with the same dependencies. Use a fresh output directory when repackaging a version. The build runs session and transport tests, followed by UI tests with a synthetic Blu-ray ISO on an isolated Windows desktop.

## Package a development build with existing dependencies

This path requires Git, Visual Studio 2022 Community with C++ and the Windows SDK, Python 3.11+ with pywin32, and ffmpeg and ffprobe on PATH to generate test videos and a UDF image. Put Yasm on PATH or pass `-YasmDirectory`. Point `-RuntimeDirectory` to files matching the paths and SHA-256 hashes in `engine/runtime-files.json`. The `Engine` folder of a compatible local build can serve as that directory.

```powershell
./build-release.ps1 -WorkDirectory ./.local/build-work -RuntimeDirectory ./.local/runtime -OutputDirectory ./out
```

Each patch and revision combination has its own build directory so repeat builds reuse compiled objects. The packager takes committed sources from a clean working tree, selected engine files and the default INI. The UI tests open synthetic videos and two playlists from one ISO in a separate copy of the application. The source ZIP includes the prepared, patched trees under `upstream/host` and `upstream/decoder`.

New clones need the project Git identity and hooks. [Git setup](../AGENTS.md)

The hooks check the author and committer, staged files and outgoing history. They also check the tagger's name and email on annotated tags. The packager checks reachable Git history and both ZIPs, including nested archives, against information from the local profile and global Git configuration. Extra private names or email addresses can be supplied locally via `SYNCSYNCAV_PRIVATE_TERMS`, one per line. Check reports identify the file. Screenshots, logs, session settings and films used for manual tests stay out of the public build.

## Blu-ray libraries

`engine/dependencies/libbluray-1.5.0.tar.xz` contains the sources for libbluray 1.5.0 and libudfread 1.2.0. The preparation script verifies the archive's SHA-256, applies `libudfread-msvc.diff` and builds both x64 DLLs using Meson, Ninja and MSVC. Pass the output directory to the packager via `-BuiltBlurayDirectory`. The packager checks license files against the manifest and writes the built DLLs' SHA-256 digests to `SHA256.json`. Local packaging using only `-RuntimeDirectory` checks all 39 files against the original hashes.
