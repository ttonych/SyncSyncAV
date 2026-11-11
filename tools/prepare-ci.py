"""Prepare a fresh Windows x64 build directory from pinned public inputs."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile


ENGINE = Path(__file__).resolve().parents[1] / "engine"
BLURAY_DIR = "plugins64/libbluray/"
VIRTUALDUB = "https://github.com/v0lt/VirtualDub2/releases/download/2.5.7/VirtualDub2_v2.5.7.1257.7z"
FFMPEG_TOOLS = "https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.7z"
YASM = "https://www.tortall.net/projects/yasm/releases/yasm-1.3.0-win64.exe"
YASM_CRT = "https://download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe"


def verify(path, expected):
    with path.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != expected:
        raise ValueError(f"SHA-256 mismatch for {path.name}: {actual}")


def download(url, destination, expected):
    subprocess.run(["curl.exe", "--fail", "--silent", "--show-error", "--location",
                    "--retry", "3", "--connect-timeout", "20", "--max-time", "300",
                    "--output", destination, url], check=True)
    verify(destination, expected)


def prepare_vendor(scratch, runtime, manifest):
    archive = scratch / "VirtualDub2.7z"
    download(VIRTUALDUB, archive, "7c14ade9cd3e2335f113e40a57ea6a5e420d225cfef4f07beccf96a543883e1e")
    prefix = "VirtualDub2_v2.5"
    files = [name for name in manifest if not name.startswith(BLURAY_DIR)]
    subprocess.run(["7z", "x", "-y", "-o" + str(scratch), archive,
                    *[prefix + "/" + name for name in files]], check=True)
    for name in files:
        verify(scratch / prefix / name, manifest[name])
    (scratch / prefix).rename(runtime)


def build_bluray(scratch, runtime, binaries, manifest):
    archive = ENGINE / "dependencies/libbluray-1.5.0.tar.xz"
    verify(archive, "f676408e91a5d321abf8b8d4dfdae36205c297dab5c54c3ec519639025f474a2")
    with tarfile.open(archive) as source_archive:
        source_archive.extractall(scratch, filter="data")
    source = scratch / "libbluray-1.5.0"
    udf = source / "contrib/libudfread"
    # Keep git apply relative to libudfread even when scratch is inside a checkout.
    subprocess.run(["git", "init", "-q", str(udf)], check=True)
    subprocess.run(["git", "-c", "core.autocrlf=false", "apply",
                    ENGINE / "dependencies/libudfread-msvc.diff"], cwd=udf, check=True)
    build = scratch / "bd-build"
    env = {**os.environ, "CC": "cl", "_LINK_": "/PDBALTPATH:%_PDB%"}
    meson = [sys.executable, "-m", "mesonbuild.mesonmain"]
    subprocess.run([*meson, "setup", build, source, "--vsenv", "--buildtype=release",
                    "--default-library=shared", "--force-fallback-for=libudfread",
                    "-Db_vscrt=mt", "-Dembed_udfread=false", "-Dlibudfread:embed_udfread=false",
                    "-Dlibudfread:default_library=shared", "-Dlibudfread:enable_examples=false",
                    "-Dbdj_jar=disabled", "-Dfontconfig=disabled", "-Dfreetype=disabled",
                    "-Dlibxml2=disabled", "-Denable_tools=false", "-Denable_examples=false"],
                   env=env, check=True)
    subprocess.run([*meson, "compile", "-C", build], env=env, check=True)
    binaries.mkdir()
    shutil.copyfile(build / "src/bluray-4.dll", binaries / "bluray-4.dll")
    shutil.copyfile(build / "contrib/libudfread/src/udfread-3.dll", binaries / "udfread-3.dll")
    (runtime / BLURAY_DIR).mkdir(parents=True)
    for name, tree in (("COPYING-libbluray.txt", source), ("COPYING-libudfread.txt", udf)):
        verify(tree / "COPYING", manifest[BLURAY_DIR + name])
        shutil.copyfile(tree / "COPYING", runtime / BLURAY_DIR / name)


def prepare_tools(scratch, bin_dir):
    bin_dir.mkdir()
    download(YASM, bin_dir / "yasm.exe", "d160b1d97266f3f28a71b4420a0ad2cd088a7977c2dd3b25af155652d8d8d91f")
    # The official x64 assembler imports the Visual C++ 2010 runtime.
    redist = scratch / "vcredist_x64.exe"
    download(YASM_CRT, redist, "f3b7a76d84d23f91957aa18456a14b4e90609e4ce8194c5653384ed38dada6f3")
    subprocess.run(["7z", "e", "-y", "-o" + str(scratch), redist, "-ir!vc_red.cab"], check=True)
    member = "F_CENTRAL_msvcr100_x64"
    subprocess.run(["7z", "e", "-y", "-o" + str(scratch), scratch / "vc_red.cab", member], check=True)
    (scratch / member).rename(bin_dir / "msvcr100.dll")
    subprocess.run([bin_dir / "yasm.exe", "--version"], check=True)
    archive = scratch / "ffmpeg-tools.7z"
    download(FFMPEG_TOOLS, archive, "e25b682664025d49034c981afb4bae36238a40f29a3cc1c713ad9a8b5b3528f6")
    prefix = "ffmpeg-8.1.2-essentials_build/bin/"
    subprocess.run(["7z", "e", "-y", "-o" + str(bin_dir), archive,
                    prefix + "ffmpeg.exe", prefix + "ffprobe.exe"], check=True)
    for name in ("ffmpeg", "ffprobe"):
        subprocess.run([bin_dir / (name + ".exe"), "-version"], check=True, stdout=subprocess.DEVNULL)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path, help="New directory for runtime, bluray and bin")
    target = parser.parse_args().destination.resolve()
    if target.exists():
        parser.error("Choose a fresh destination.")
    target.parent.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((ENGINE / "runtime-files.json").read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(dir=target.parent, prefix="prep-") as temp:
        scratch = Path(temp)
        inputs = scratch / "inputs"
        inputs.mkdir()
        prepare_vendor(scratch, inputs / "runtime", manifest)
        build_bluray(scratch, inputs / "runtime", inputs / "bluray", manifest)
        prepare_tools(scratch, inputs / "bin")
        inputs.rename(target)
    print(f"Verified build inputs: {target}")


if __name__ == "__main__":
    main()
