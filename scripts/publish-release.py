"""Publish already-verified local packages via GitHub Releases; credentials stay in memory."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--notes", required=True, type=Path)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    remote = subprocess.check_output(["git", "remote", "get-url", "origin"], cwd=root, text=True).strip()
    if remote != "https://github.com/lunce0503/sound_manger.git":
        raise RuntimeError("Unexpected repository remote; refusing to publish")
    version = args.version
    if not version or any(c not in "0123456789." for c in version):
        raise RuntimeError("Invalid version")
    package_root = root / "artifacts" / "release" / f"v{version}"
    files = [package_root / f"SoundManager-{version}-windows-x64.exe", package_root / f"SoundManager-{version}-android.apk", package_root / "SHA256SUMS.txt", package_root / "THIRD-PARTY-NOTICES.md", package_root / "LICENSE", package_root / "README.md"]
    if not all(path.is_file() for path in files):
        raise RuntimeError("Release packages are missing")
    expected = dict(line.split("  ", 1)[::-1] for line in (package_root / "SHA256SUMS.txt").read_text(encoding="utf-8-sig").splitlines())
    for path in files:
        if path.name == "SHA256SUMS.txt":
            continue
        if hashlib.file_digest(path.open("rb"), "sha256").hexdigest() != expected.get(path.name):
            raise RuntimeError(f"Package hash mismatch: {path.name}")
    credential = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not credential:
        result = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\n\n", capture_output=True, text=True, check=True, cwd=root)
        fields = dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)
        credential = fields["password"]

    def api(method, url, body=None, content_type="application/json"):
        data = json.dumps(body).encode() if isinstance(body, dict) else body
        request = urllib.request.Request(url, data=data, method=method, headers={
            "Authorization": "Bearer " + credential, "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "SoundManager-Release",
            "Content-Type": content_type,
        })
        with urllib.request.urlopen(request, timeout=240) as response:
            return json.load(response)

    endpoint = "https://api.github.com/repos/lunce0503/sound_manger"
    tag = "v" + version
    # A unique draft is reviewable/recoverable if upload or verification fails.
    release = api("POST", endpoint + "/releases", {
        "tag_name": tag, "target_commitish": args.commit, "name": "Sound Manager " + tag,
        "body": args.notes.read_text(encoding="utf-8"), "draft": True, "prerelease": True,
    })
    for path in files:
        upload_url = release["upload_url"].split("{")[0] + "?name=" + urllib.parse.quote(path.name)
        asset = api("POST", upload_url, path.read_bytes(), "application/octet-stream")
        digest = "sha256:" + hashlib.file_digest(path.open("rb"), "sha256").hexdigest()
        if asset.get("digest") and asset["digest"] != digest:
            raise RuntimeError(f"Uploaded asset digest mismatch: {path.name}")
        if asset.get("state") != "uploaded" or asset["size"] != path.stat().st_size:
            raise RuntimeError(f"Uploaded asset incomplete: {path.name}")
        print("Uploaded and verified:", path.name, flush=True)
    published = api("PATCH", endpoint + "/releases/" + str(release["id"]), {"draft": False})
    print(json.dumps({"release_url": published["html_url"], "tag": tag, "prerelease": published["prerelease"]}), flush=True)


if __name__ == "__main__":
    try:
        main()
    except urllib.error.HTTPError as error:
        print(f"GitHub release request failed: HTTP {error.code}. Any draft remains recoverable.", file=sys.stderr)
        sys.exit(1)
