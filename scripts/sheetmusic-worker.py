#!/usr/bin/env python3
"""Process authenticated Moonfin score requests through a bounded local spool.

The Jellyfin plugin writes queue JSON. Run this on the media host with access
to the Books library and the existing Shelfmark post-import hook. No port or
separate authentication service is exposed.
"""

from __future__ import annotations

import hashlib
import ipaddress
import json
import os
import re
import socket
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request
from pathlib import Path
from xml.etree import ElementTree as ET

CONFIG = Path(os.environ.get("SHEETMUSIC_CONFIG", "/opt/media-stack/config/jellyfin/plugins/configurations/Moonfin"))
BOOKS = Path(os.environ.get("SHEETMUSIC_LIBRARY", "/mnt/gdrive/media/sheetmusic"))
SPOOL = Path(os.environ.get("SHEETMUSIC_SPOOL", "/srv/media-stack/downloads/sheetmusic-spool"))
MAX_BYTES = 100_000_000
QUEUE = CONFIG / "sheetmusic-queue"
ET.register_namespace("dc", "http://purl.org/dc/elements/1.1/")
ET.register_namespace("", "http://www.idpf.org/2007/opf")


def _safe_url(value: str, source: str) -> bool:
    try:
        url = urllib.parse.urlsplit(value)
        host = (url.hostname or "").lower()
        allowed = host == "www.mutopiaproject.org" if source == "mutopia" else (
            host == "archive.org" or host.endswith(".archive.org"))
        return (url.scheme == "https" and allowed and url.port in (None, 443)
                and not url.username and not url.password and not url.query
                and not url.fragment and url.path.lower().endswith(".pdf"))
    except ValueError:
        return False


def _public_host(host: str) -> bool:
    try:
        return all(ipaddress.ip_address(item[4][0]).is_global
                   for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM))
    except (OSError, ValueError):
        return False


class _SafeRedirect(urllib.request.HTTPRedirectHandler):
    def __init__(self, source: str):
        self.source = source

    def redirect_request(self, request, fp, code, msg, headers, newurl):
        host = urllib.parse.urlsplit(newurl).hostname or ""
        if not _safe_url(newurl, self.source) or not _public_host(host):
            raise ValueError("source redirected outside its trusted host")
        return super().redirect_request(request, fp, code, msg, headers, newurl)


def _write_job(path: Path, job: dict) -> None:
    previous = path.stat()
    descriptor, name = tempfile.mkstemp(prefix=".score-status-", dir=path.parent)
    try:
        os.fchmod(descriptor, previous.st_mode & 0o777)
        if os.geteuid() == 0:
            os.fchown(descriptor, previous.st_uid, previous.st_gid)
        with os.fdopen(descriptor, "w") as stream:
            json.dump(job, stream, ensure_ascii=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def _slug(value: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "-", value.lower()).strip("-")[:70]
    return slug or "score"


def _validate(job: dict) -> tuple[str, str]:
    ident = job.get("pieceId", "")
    source = "mutopia" if ident.startswith("mutopia:") else "ia" if ident.startswith("ia:") else ""
    if not source or not re.fullmatch(r"[0-9a-f]{32}", job.get("id", "")):
        raise ValueError("invalid score request")
    url = job.get("pdfUrl", "")
    if not _safe_url(url, source):
        raise ValueError("untrusted score URL")
    host = urllib.parse.urlsplit(url).hostname or ""
    if not _public_host(host):
        raise ValueError("score host is not public")
    for key in ("title", "composer", "license", "sourceUrl", "userId"):
        value = job.get(key)
        if not isinstance(value, str) or not value.strip() or len(value) > 300:
            raise ValueError("invalid score metadata")
    if source == "mutopia":
        catalog = json.loads((CONFIG / "sheetmusic-catalog.json").read_text())
        match = next((p for p in catalog if p.get("id") == ident[8:]), None)
        if not match or match.get("pdf_url") != url:
            raise ValueError("score no longer exists in catalog")
    else:
        archive_id = ident[3:]
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,150}", archive_id):
            raise ValueError("invalid archive identifier")
        if not url.startswith("https://archive.org/download/" + archive_id + "/"):
            raise ValueError("archive identifier and PDF do not match")
        if job["sourceUrl"] != "https://archive.org/details/" + archive_id:
            raise ValueError("archive provenance does not match")
    return source, url


def _download(url: str, source: str, target: Path) -> str:
    opener = urllib.request.build_opener(_SafeRedirect(source))
    request = urllib.request.Request(url, headers={"User-Agent": "Moonfin-Books-Scores/1.0"})
    digest = hashlib.sha256()
    total = 0
    with opener.open(request, timeout=45) as response, target.open("wb") as output:
        final = response.url
        if not _safe_url(final, source) or not _public_host(urllib.parse.urlsplit(final).hostname or ""):
            raise ValueError("untrusted score response")
        declared = response.headers.get("Content-Length")
        if declared and int(declared) > MAX_BYTES:
            raise ValueError("score exceeds size limit")
        prefix = response.read(5)
        if prefix != b"%PDF-":
            raise ValueError("score is not a PDF")
        output.write(prefix)
        digest.update(prefix)
        total = 5
        while chunk := response.read(1024 * 1024):
            total += len(chunk)
            if total > MAX_BYTES:
                raise ValueError("score exceeds size limit")
            output.write(chunk)
            digest.update(chunk)
        output.flush()
        os.fsync(output.fileno())
    return digest.hexdigest()


def _opf(job: dict, digest: str) -> bytes:
    ns = "http://www.idpf.org/2007/opf"
    dc = "http://purl.org/dc/elements/1.1/"
    package = ET.Element(f"{{{ns}}}package", {"version": "2.0", "unique-identifier": "scoreid"})
    metadata = ET.SubElement(package, f"{{{ns}}}metadata")
    for key, value in (
        ("identifier", job["pieceId"]), ("title", job["title"]),
        ("creator", job["composer"]), ("rights", job["license"]),
        ("source", job["sourceUrl"]), ("description", "SHA-256: " + digest),
    ):
        attributes = {"id": "scoreid"} if key == "identifier" else {}
        ET.SubElement(metadata, f"{{{dc}}}{key}", attributes).text = value
    return ET.tostring(package, encoding="utf-8", xml_declaration=True)


def _find_item(container_path: str, title: str) -> str | None:
    # The existing Shelfmark container already owns the Jellyfin API key.
    script = """import json,os,urllib.parse,urllib.request,sys
path,title=sys.argv[1:3]
key=os.environ.get('JELLYFIN_API_KEY','')
if not key: raise SystemExit(2)
query=urllib.parse.urlencode({'Recursive':'true','Fields':'Path','IncludeItemTypes':'Book','SearchTerm':title,'Limit':100})
req=urllib.request.Request('http://jellyfin:8096/Items?'+query,headers={'Authorization':'MediaBrowser Token="'+key+'"'})
with urllib.request.urlopen(req,timeout=20) as r: data=json.load(r)
for item in data.get('Items',[]):
 if item.get('Path')==path:print(item['Id']);break
"""
    result = subprocess.run(["docker", "exec", "-u", "appuser", "shelfmark",
        "python3", "-c", script, container_path, title], capture_output=True,
        text=True, timeout=35, check=False)
    item = result.stdout.strip()
    return item if result.returncode == 0 and re.fullmatch(r"[0-9a-fA-F]{32}", item) else None


def _trigger_scan() -> None:
    script = """import os,urllib.request
key=os.environ.get('JELLYFIN_API_KEY','')
if not key: raise SystemExit(2)
req=urllib.request.Request('http://jellyfin:8096/Library/Refresh',method='POST',
    headers={'Authorization':'MediaBrowser Token="'+key+'"'})
with urllib.request.urlopen(req,timeout=25) as response:
 if response.status not in (200,204): raise SystemExit(3)
"""
    subprocess.run(["docker", "exec", "-u", "appuser", "shelfmark",
        "python3", "-c", script], capture_output=True, text=True, timeout=35, check=True)


def _process(path: Path) -> None:
    job = json.loads(path.read_text())
    if job.get("status") == "importing" and job.get("filePath"):
        item = _find_item(job["filePath"], job["title"])
        if item:
            job.update(status="ready", itemId=item, error=None)
            _write_job(path, job)
        return
    if job.get("status") != "queued":
        return

    source, url = _validate(job)
    job["status"] = "fetching"
    _write_job(path, job)
    SPOOL.mkdir(mode=0o700, parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="score-", dir=SPOOL) as temporary:
        pdf = Path(temporary) / "score.pdf"
        digest = _download(url, source, pdf)
        folder = BOOKS / _slug(job["composer"]) / (
            _slug(job["title"]) + "-" + hashlib.sha256(job["pieceId"].encode()).hexdigest()[:10])
        folder.mkdir(mode=0o775, parents=True, exist_ok=True)
        destination = folder / "score.pdf"
        partial = folder / "score.pdf.part"
        if destination.exists():
            if hashlib.sha256(destination.read_bytes()).hexdigest() != digest:
                raise ValueError("existing score has different content")
        else:
            with pdf.open("rb") as source_file, partial.open("wb") as target_file:
                while chunk := source_file.read(1024 * 1024):
                    target_file.write(chunk)
                target_file.flush()
                os.fsync(target_file.fileno())
            if hashlib.sha256(partial.read_bytes()).hexdigest() != digest:
                raise ValueError("copied score failed hash check")
            os.replace(partial, destination)
        opf = folder / "metadata.opf"
        if not opf.exists():
            opf.write_bytes(_opf(job, digest))
        jellyfin_path = "/media/sheetmusic/" + destination.relative_to(BOOKS).as_posix()
        _trigger_scan()
        job.update(status="importing", filePath=jellyfin_path, error=None)
        _write_job(path, job)


def main() -> int:
    if not QUEUE.is_dir():
        return 0
    for path in sorted(QUEUE.glob("*.json")):
        try:
            _process(path)
        except Exception as error:
            try:
                job = json.loads(path.read_text())
                job.update(status="error", error=type(error).__name__)
                _write_job(path, job)
            except (OSError, ValueError):
                pass
            print(f"score job {path.stem}: {type(error).__name__}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
