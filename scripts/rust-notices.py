"""Collect exact normal/build dependency licenses from Cargo's locked Windows graph."""
import json
from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parent.parent
metadata = json.loads(subprocess.check_output([
    "cargo", "metadata", "--format-version", "1", "--filter-platform", "x86_64-pc-windows-msvc",
    "--locked", "--offline"], cwd=root / "rust"))
packages = {p["id"]: p for p in metadata["packages"]}
nodes = {n["id"]: n for n in metadata["resolve"]["nodes"]}
start = next(p["id"] for p in metadata["packages"] if p["name"] == "sound-app")
seen = set()
def walk(identifier):
    if identifier in seen:
        return
    seen.add(identifier)
    for dep in nodes[identifier]["deps"]:
        if any(k["kind"] in (None, "build") for k in dep["dep_kinds"]):
            walk(dep["pkg"])
walk(start)
result = ["Sound Manager Rust server third-party notices\nGenerated from Cargo.lock for x86_64-pc-windows-msvc.\n"]
count = 0
for package in sorted((packages[i] for i in seen if packages[i]["source"]), key=lambda p:(p["name"],p["version"])):
    folder = Path(package["manifest_path"]).parent
    paths = sorted(p for p in folder.iterdir() if p.is_file() and p.name.lower().startswith(("license", "licence", "copying", "notice")))
    if package["license_file"]:
        custom = folder / package["license_file"]
        if custom.is_file() and custom not in paths:
            paths.append(custom)
    if not paths:
        raise RuntimeError(f"Missing upstream license text: {package['name']} {package['version']}")
    result.append(f"\n{'=' * 72}\n{package['name']} {package['version']} — {package['license']}\n{package['repository'] or 'https://crates.io/crates/' + package['name']}\n")
    for path in paths:
        result.append(f"\n--- {path.name} ---\n{path.read_text(encoding='utf-8-sig')}\n")
    count += 1
destination = root / "RUST-THIRD-PARTY-NOTICES.txt"
content = "".join(result)
if "--check" in sys.argv:
    if destination.read_text(encoding="utf-8") != content:
        raise RuntimeError("Rust dependency notices changed; regenerate with scripts/rust-notices.py")
else:
    destination.write_text(content, encoding="utf-8", newline="\n")
print(f"Collected upstream license texts for {count} Rust dependencies.")
