"""Check release inputs, archives and reachable Git objects without echoing identity data."""
import io
import os
from pathlib import Path
import re
import subprocess
import tarfile
import zipfile


def private_patterns():
    values = {os.environ.get(k, "") for k in ("USERNAME","COMPUTERNAME","GIT_AUTHOR_EMAIL",
                                               "GIT_COMMITTER_EMAIL","GIT_AUTHOR_NAME","GIT_COMMITTER_NAME")}
    for field in ("user.email","user.name"):
        result = subprocess.run(["git","config","--global","--get-all",field],capture_output=True,text=True)
        values.update(result.stdout.splitlines())
    values.update(os.environ.get("SYNCSYNCAV_PRIVATE_TERMS","").splitlines())
    values.difference_update({"SyncSyncAV contributors","project@localhost.invalid"})
    # Generic profile roots are checked even on a different build machine.
    for sep in ("/",chr(92)):
        values.update([":"+sep+"Users"+sep, "Documents"+sep+"Codex"])
    return [(v.lower().encode(enc),enc,v.isalnum()) for v in values if len(v.strip())>=3
            for enc in ("utf-8","utf-16le","utf-16be")]


def contains_identity(data,patterns):
    for token,enc,word in patterns:
        if token not in data:
            continue
        if not word:
            return True
        step=1 if enc=="utf-8" else 2
        # E.g. a short login can also occur in the middle of an upstream Java identifier.
        for hit in re.finditer(re.escape(token),data):
            before=data[max(0,hit.start()-step):hit.start()].decode(enc,errors="ignore")
            if not before.isalnum():
                return True
    return False


def inspect(name, data, patterns):
    folded = data.lower()
    if contains_identity(folded,patterns):
        # The member name itself may contain private data.
        raise ValueError("Private identity or local path detected in "+safe_name(name,patterns))
    owned=name.startswith(("source/","tests/","tools/")) or name in {"README.md","engine/README.md","build-release.ps1"}
    if owned and "/Fonts/" not in name and name.lower().endswith((".cs",".ps1",".py",".md",".xaml",".xml",".txt")):
        emails=re.findall(rb"[A-Za-z0-9_.%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}",data)
        if any(address!=b"project@localhost.invalid" for address in emails):
            raise ValueError("Contact address in first-party file "+safe_name(name,patterns))
    if name.lower().endswith(".zip"):
        with zipfile.ZipFile(io.BytesIO(data)) as z:
            for entry in z.infolist():
                inspect(entry.filename,z.read(entry),patterns)
                inspect("archive metadata",entry.filename.encode()+entry.comment+entry.extra,patterns)
            inspect("archive comment",z.comment,patterns)
    elif name.lower().endswith(".tar.xz"):
        with tarfile.open(fileobj=io.BytesIO(data),mode="r:xz") as t:
            for entry in t:
                inspect("tar metadata",(entry.name+entry.linkname+entry.uname+entry.gname).encode(),patterns)
                if entry.isfile():
                    inspect(entry.name,t.extractfile(entry).read(),patterns)


def safe_name(name,patterns):
    return "[redacted name]" if contains_identity(name.lower().encode(),patterns) else name


def check_file(path,root,patterns):
    name=path.relative_to(root).as_posix()
    inspect("member name",name.encode(),patterns)
    inspect(name,path.read_bytes(),patterns)


def check_history(repo,patterns):
    objects=subprocess.check_output(["git","-C",str(repo),"rev-list","--objects","--all"])
    # Use a single persistent Git process for packed and loose objects.
    with subprocess.Popen(["git","-C",str(repo),"cat-file","--batch"],stdin=subprocess.PIPE,stdout=subprocess.PIPE) as cat:
        for line in objects.splitlines():
            oid,_,name=line.partition(b" ")
            cat.stdin.write(oid+b"\n");cat.stdin.flush()
            _,kind,size=cat.stdout.readline().split()
            data=cat.stdout.read(int(size));cat.stdout.read(1)
            inspect("Git path",name,patterns)
            inspect(name.decode(errors="replace") or kind.decode(),data,patterns)
            if kind==b"commit":
                identities=re.findall(rb"^(?:author|committer) (.+?) [0-9]+ [+-][0-9]+$",data,re.M)
                if len(identities)!=2 or any(i!=b"SyncSyncAV contributors <project@localhost.invalid>" for i in identities):
                    raise ValueError("Non-project commit identity in public history")
        cat.stdin.close()
