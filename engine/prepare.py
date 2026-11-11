"""Make an isolated, patched upstream checkout. Existing work is never overwritten."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import tempfile

here = Path(__file__).resolve().parent
sources = json.loads((here / "upstream.json").read_text(encoding="utf-8"))
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("component", choices=sources)
parser.add_argument("destination", type=Path)
parser.add_argument("--revision", help="Full upstream commit ID for an upgrade trial")
parser.add_argument("--mirror", type=Path, help="Optional local Git copy containing the upstream commit")
args = parser.parse_args()
upstream = sources[args.component]
revision = args.revision or upstream["revision"]
if not re.fullmatch("[0-9a-f]{40}", revision):
    parser.error("Pass a full 40-character upstream commit ID.")
dest = args.destination.resolve()
if dest.exists():
    parser.error("Choose a new directory. To rebuild, use the existing checkout directly.")
dest.parent.mkdir(parents=True, exist_ok=True)
with tempfile.TemporaryDirectory(prefix=".prepare-", dir=dest.parent, ignore_cleanup_errors=True) as staging:
    tree = Path(staging) / "tree"
    tree.mkdir()
    def git(*argv):
        return subprocess.run(["git", "-C", str(tree), *argv], check=True, stdout=subprocess.PIPE).stdout
    git("init", "-q")
    git("config", "core.autocrlf", "false")
    git("remote", "add", "origin", upstream["url"])
    location = args.mirror.resolve().as_uri() if args.mirror else "origin"
    git("fetch", "--no-tags", "--update-shallow", location, revision)
    git("checkout", "-q", "--detach", "FETCH_HEAD")
    if git("rev-parse", "HEAD").decode().strip() != revision:
        raise SystemExit("Fetched commit differs from requested revision.")
    patch = str(here / "patches" / (args.component + ".patch"))
    git("apply", "--index", "--check", "--whitespace=nowarn", patch)
    git("apply", "--index", "--whitespace=nowarn", patch)
    tree.rename(dest)
print(f"{args.component}: {revision} + {Path(patch).name} -> {dest}")
