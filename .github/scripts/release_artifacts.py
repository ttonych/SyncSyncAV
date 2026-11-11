"""Promote the exact ZIPs from a successful Windows build on a GitHub-hosted runner."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile


def api(endpoint, *options):
    raw = subprocess.check_output(["gh", "api", endpoint, *options])
    return json.loads(raw) if raw else None


def fetch_archives(repo, run_id, folder):
    if not re.fullmatch(r"[1-9][0-9]*", run_id):
        raise ValueError("Enter the numeric build run ID")
    run = api(f"{repo}/actions/runs/{run_id}")
    if (run["path"] != ".github/workflows/build-windows.yml" or run["conclusion"] != "success"
            or run["event"] != "workflow_dispatch" or run["head_branch"] != "main"
            or run["head_repository"]["id"] != run["repository"]["id"]):
        raise ValueError("Expected a successful Build Windows ZIPs run from this repository's main branch")

    result = api(f"{repo}/actions/runs/{run_id}/artifacts")
    artifacts = {a["name"]: a for a in result["artifacts"]}
    base = next((n for n in artifacts if re.fullmatch(r"SyncSyncAV_[0-9]+\.[0-9]+\.[0-9]+\.zip", n)), "")
    if not base or result["total_count"] != 2 or set(artifacts) != {base, base[:-4] + "_source.zip"}:
        raise ValueError("The build must contain matching application and source ZIPs")
    digests = {}
    for name, artifact in artifacts.items():
        if artifact["expired"]:
            raise ValueError(f"Build artifact expired: {name}")
        path = folder / name
        with path.open("xb") as out:
            subprocess.run(["gh", "api", f"{repo}/actions/artifacts/{artifact['id']}/zip"], stdout=out, check=True)
        with path.open("rb") as stream:
            digest = "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()
        if digest != artifact["digest"]:
            raise ValueError(f"Build artifact digest mismatch: {name}")
        digests[name] = digest
        print(f"{name}  {digest}", flush=True)
    return run, "v" + base.removeprefix("SyncSyncAV_").removesuffix(".zip"), digests


def check_tag(repo, tag, sha):
    refs = api(f"{repo}/git/matching-refs/tags/{tag}")
    for ref in refs:
        if ref["ref"] == "refs/tags/" + tag:
            obj = ref["object"]
            while obj["type"] == "tag":
                obj = api(f"{repo}/git/tags/{obj['sha']}")["object"]
            if obj["type"] != "commit" or obj["sha"] != sha:
                raise ValueError(f"Existing tag {tag} points to a different revision")


def publish(repo, run, tag, digests, folder):
    sha = run["head_sha"]
    check_tag(repo, tag, sha)
    pages = api(f"{repo}/releases?per_page=100", "--paginate", "--slurp")
    release = next((r for page in pages for r in page if r["tag_name"] == tag), None)
    notes = f"Built and tested in [GitHub Actions run {run['id']}]({run['html_url']})."
    if release is None:
        release = api(f"{repo}/releases", "-f", f"tag_name={tag}", "-f", f"target_commitish={sha}",
                      "-f", f"name=SyncSyncAV {tag}", "-f", f"body={notes}", "-F", "draft=true")
    elif release["draft"] and (release["target_commitish"] != sha or run["html_url"] not in (release["body"] or "")):
        raise ValueError(f"An existing draft for {tag} belongs to a different build")

    endpoint = f"{repo}/releases/{release['id']}"
    for asset in release["assets"]:
        if asset["name"] not in digests:
            raise ValueError(f"Unexpected asset in {tag}: {asset['name']}")
        if release["draft"] and asset["state"] == "starter":
            # GitHub can leave an empty asset after an interrupted upload.
            api(f"{repo}/releases/assets/{asset['id']}", "-X", "DELETE")
        elif asset["digest"] != digests[asset["name"]] or asset["state"] != "uploaded":
            raise ValueError(f"Existing release asset differs from the build: {asset['name']}")

    present = {a["name"] for a in release["assets"] if a["state"] == "uploaded"}
    if release["draft"]:
        for name in sorted(digests.keys() - present):
            url = release["upload_url"].split("{")[0] + "?name=" + name
            api(url, "-H", "Content-Type: application/zip", "--input", str(folder / name))
    assets = api(endpoint)["assets"]
    if len(assets) != len(digests) or {a["name"]: a["digest"] for a in assets if a["state"] == "uploaded"} != digests:
        raise ValueError("Release assets do not match both build artifact digests")
    if release["draft"]:
        check_tag(repo, tag, sha)
        release = api(endpoint, "-X", "PATCH", "-F", "draft=false")
    print(release["html_url"])


if __name__ == "__main__":
    repository = "repos/" + os.environ["GITHUB_REPOSITORY"]
    with tempfile.TemporaryDirectory(dir=os.environ["RUNNER_TEMP"]) as temp:
        folder = Path(temp)
        run, tag, digests = fetch_archives(repository, os.environ["BUILD_RUN_ID"], folder)
        print(f"Verified {tag} from {run['head_sha']}", flush=True)
        if os.environ["PUBLISH"].lower() == "true":
            publish(repository, run, tag, digests, folder)
