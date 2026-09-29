#!/usr/bin/env python3
"""Repackage the verified ZIP emitted by Jellyfin/build.sh (its stage is removed)."""
from pathlib import Path, PurePosixPath
import hashlib
import json
import sys
import zipfile

root = Path(__file__).resolve().parent.parent
version = sys.argv[1] if len(sys.argv) > 1 else "2.3.1.100"
source = root / "Jellyfin" / f"Moonfin.Server-{version}.zip"
out = root / "dist"
out.mkdir(exist_ok=True)
package = out / f"Moonbase_Books_Jellyfin_v{version}.zip"
with zipfile.ZipFile(source) as original:
    names = set(original.namelist())
    required = {"Moonfin.Server.dll", "SharpCompress.dll", "frontend/index.html", "meta.json"}
    assert required <= names, f"Required plugin files missing: {sorted(required - names)}"
    assert "frontend/config.json" not in names, "Static config would override plugin settings"
    assert not any(name.endswith((".p12", ".pfx", ".keystore")) for name in names)
    assert not any(PurePosixPath(name).is_absolute() or ".." in PurePosixPath(name).parts for name in names)
    meta = json.loads(original.read("meta.json"))
    assert meta["version"] == version
    meta.update(name="Moonbase Books", owner="ZepiGit", autoUpdate=False,
                overview="Integrated book and audiobook requests for Moonfin Books",
                description="Moonbase Books extends Moonbase with an authenticated Shelfmark bridge for Jellyfin clients.")
    with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(names):
            if name.endswith("/") or name in {"meta.json", "LICENSE"}:
                continue
            archive.writestr(name, original.read(name))
        archive.writestr("meta.json", json.dumps(meta, indent=2) + "\n")
        archive.write(root / "LICENSE", "LICENSE")
        archive.write(root / "scripts" / "sheetmusic-worker.py", "deployment/sheetmusic-worker.py")
        archive.write(root / "data" / "sheetmusic-catalog.json", "deployment/sheetmusic-catalog.json")
        archive.write(root / "data" / "SHEETMUSIC_SOURCE.txt", "deployment/SHEETMUSIC_SOURCE.txt")
        archive.write(root / "deployment" / "sheetmusic-import.service", "deployment/sheetmusic-import.service")
        archive.write(root / "deployment" / "sheetmusic-import.timer", "deployment/sheetmusic-import.timer")
checksum = hashlib.sha256(package.read_bytes()).hexdigest()
(out / "SHA256SUMS").write_text(f"{checksum}  {package.name}\n")
print(package.name)
