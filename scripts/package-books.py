#!/usr/bin/env python3
"""Package the Books plugin after Jellyfin/build.sh has compiled and verified it."""
from pathlib import Path
import hashlib
import json
import sys
import zipfile

root = Path(__file__).resolve().parent.parent
version = sys.argv[1] if len(sys.argv) > 1 else "2.3.1.100"
stage = root / "Jellyfin/release"
assert (stage / "Moonfin.Server.dll").is_file()
assert (stage / "SharpCompress.dll").is_file()
assert (stage / "frontend/index.html").is_file(), "Build and copy the Moonfin Books web bundle first"
meta_path = stage / "meta.json"
meta = json.loads(meta_path.read_text())
meta.update(name="Moonbase Books", owner="ZepiGit", autoUpdate=False,
            overview="Integrated book and audiobook requests for Moonfin Books",
            description="Moonbase Books extends Moonbase with an authenticated Shelfmark bridge for Jellyfin clients.")
meta_path.write_text(json.dumps(meta, indent=2) + "\n")
# Credentials and server-specific configuration never belong in a distributable.
assert not (stage / "frontend/config.json").exists()
assert not any(p.name.endswith((".p12", ".pfx", ".keystore")) for p in stage.rglob("*"))
out = root / "dist"
out.mkdir(exist_ok=True)
package = out / f"Moonbase_Books_Jellyfin_v{version}.zip"
with zipfile.ZipFile(package, "w", zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(stage.rglob("*")):
        if path.is_file():
            archive.write(path, path.relative_to(stage))
    archive.write(root / "LICENSE", "LICENSE")
checksum = hashlib.sha256(package.read_bytes()).hexdigest()
(package.parent / "SHA256SUMS").write_text(f"{checksum}  {package.name}\n")
print(package.name)
