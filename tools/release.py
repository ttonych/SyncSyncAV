"""Package a tested Windows build and its corresponding sources into a fresh output directory."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from privacy import check_file, check_history, private_patterns

repo=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
for arg in ("app","runtime","host","decoder","bridge","output"):
    parser.add_argument("--"+arg,required=True,type=Path)
parser.add_argument("--built-bluray", type=Path, help="Directory containing both freshly built Blu-ray DLLs")
args=parser.parse_args()


def git(tree,*cmd):
    return subprocess.check_output(["git","-C",str(tree),*cmd])


def digest(file):
    with file.open("rb") as stream:
        return hashlib.file_digest(stream,"sha256").hexdigest()


def copy(source,destination):
    destination.parent.mkdir(parents=True,exist_ok=True)
    shutil.copyfile(source,destination)


def archive_tree(archive, prefix, tree, revision):
    # Preserve committed line endings even on Windows runners with automatic checkout conversion.
    raw=git(tree,"-c","core.autocrlf=false","archive","--format=zip",revision)
    with zipfile.ZipFile(io.BytesIO(raw)) as src:
        for item in src.infolist():
            if not item.is_dir():
                archive.writestr(prefix+"/"+item.filename,src.read(item))


def patched_tree(component,tree):
    pin=pins[component]["revision"]
    if git(tree,"rev-parse","HEAD").decode().strip()!=pin:
        raise ValueError(f"{component}: upstream revision differs from upstream.json")
    expected=(repo/"engine/patches"/(component+".patch")).read_bytes()
    actual=git(tree,"diff","--cached","--binary","--full-index","--no-ext-diff","HEAD")
    # Asuka regenerates the tracked font table with Windows line endings.
    if actual!=expected or git(tree,"diff","--ignore-cr-at-eol") or git(tree,"ls-files","--others","--exclude-standard"):
        raise ValueError(f"{component}: checkout differs from the published patch")
    return git(tree,"write-tree").decode().strip()


def make_runtime(runtime):
    for name,expected in json.loads((repo/"engine/runtime-files.json").read_text()).items():
        if args.built_bluray and name in ("plugins64/libbluray/bluray-4.dll", "plugins64/libbluray/udfread-3.dll"):
            copy(args.built_bluray/Path(name).name, runtime/"Engine"/name)
            continue
        src=args.runtime/name
        if digest(src)!=expected:
            raise ValueError("Runtime dependency hash mismatch: "+name)
        copy(src,runtime/"Engine"/name)
    for name in ("vdub_control.py","vdub-control.cmd"):
        copy(repo/"engine/control"/name,runtime/"Engine/control"/name)
    host=args.host/"_out/Release_x64"
    for name,src in {
        "VirtualDub2.exe":host/"VirtualDub2.exe","VirtualDub2.vdi":host/"VirtualDub2.vdi",
        "plugins64/avlib-1.vdplugin":args.decoder/"_bin/Release_x64/avlib-1.vdplugin",
        "plugins64/control-bridge.vdplugin":args.bridge/"control-bridge.vdplugin",
        "VirtualDub2.ini":repo/"engine/VirtualDub2.ini",
    }.items():
        copy(src,runtime/"Engine"/name)
    for name in ("SyncSyncAV.exe","SyncSyncAV.exe.config"):
        copy(args.app/name,runtime/name)
    for name in git(repo,"ls-files","source/Fonts").decode().splitlines():
        copy(repo/name,runtime/"Fonts"/Path(name).name)
    for name in ("README.md","LICENSE.txt"):
        copy(repo/name,runtime/name)
    copy(repo/"engine/bridge/SDK-LICENSE.txt",runtime/"Engine/SDK-LICENSE.txt")
    copy(repo/"source/Assets/SyncSyncAV.ico",runtime/"SyncSyncAV.ico")


pins=json.loads((repo/"engine/upstream.json").read_text())
version=re.search(r'AssemblyFileVersion\("(\d+\.\d+\.\d+)\.0"\)',(repo/"source/Program.cs").read_text(encoding="utf-8"))[1]
name="SyncSyncAV_"+version
output=args.output.resolve()
output.mkdir(parents=True,exist_ok=True)
destinations=[output/name,output/(name+".zip"),output/(name+"_source.zip")]
if any(p.exists() for p in destinations):
    raise SystemExit("Release already exists; select a fresh output directory.")
if git(repo,"status","--porcelain"):
    raise SystemExit("Commit the reviewed public sources first.")
rules=private_patterns()
check_history(repo,rules)
trees={key:patched_tree(key,path) for key,path in (("host",args.host),("decoder",args.decoder))}
with tempfile.TemporaryDirectory(dir=output,prefix=".release-") as temp:
    staging=Path(temp)
    runtime=staging/name
    make_runtime(runtime)
    subprocess.run([sys.executable,str(repo/"tests/file_drop.py"),str(runtime)],check=True)
    manifest={p.relative_to(runtime).as_posix():digest(p) for p in sorted(runtime.rglob("*")) if p.is_file()}
    (runtime/"SHA256.json").write_text(json.dumps({"version":version,"files":manifest},indent=2),encoding="utf-8")
    with zipfile.ZipFile(staging/destinations[1].name,"w",zipfile.ZIP_DEFLATED) as archive:
        for file in sorted(runtime.rglob("*")):
            if file.is_file(): archive.write(file,file.relative_to(staging))
    with zipfile.ZipFile(staging/destinations[2].name,"w",zipfile.ZIP_DEFLATED) as archive:
        prefix=name+"_source"
        archive_tree(archive,prefix,repo,"HEAD")
        for component,tree in (("host",args.host),("decoder",args.decoder)):
            path=prefix+"/upstream/"+component
            archive_tree(archive,path,tree,trees[component])
            archive.write(tree/"revision.h",path+"/revision.h")
    for dst in destinations[1:]:
        check_file(staging/dst.name,staging,rules)
    published=[]
    try:
        for dst in destinations:
            (staging/dst.name).rename(dst)
            published.append(dst)
    except OSError:
        for dst in reversed(published): dst.rename(staging/dst.name)
        raise
print("Verified:",", ".join(p.name for p in destinations))
