# Sheet music in Moonfin Books

Moonbase Books adds a Jellyfin-authenticated sheet-music search and request API. The
app can search a verified Mutopia catalog and the Internet Archive's IMSLP
collection, request a score, see its import status, open the resulting Jellyfin
item and save the PDF to the user's device. This is a separate score path; the
existing Prowlarr/Shelfmark book pipeline remains limited to ebooks and
audiobooks. Prowlarr has no native score catalog or score import workflow.

The shipped Mutopia catalog is at `data/sheetmusic-catalog.json`. It was generated
from `MutopiaProject/MutopiaProject` commit
`2144afd6f52d56c5b6995b8b589ef1268b3139f0` with
`Moonfin-Books/scripts/sheetmusic/build_sheetmusic_catalog.py`. All 1,304
candidates were checked with HTTPS HEAD on 29 September 2026; 1,070 returned
HTTP 200 and a PDF content type. The other 234 were excluded. The source commit,
verification and hash are recorded in `data/SHEETMUSIC_SOURCE.txt`. The catalog
contains a title, composer, instrument, source, license and PDF URL per piece.

Internet Archive search is constrained to its `imslp` text collection. Results
are metadata until a user requests them. The server resolves the identifier
again with Archive metadata and chooses an original PDF file. Mutopia requests
are likewise rechecked against the locally pinned catalog. The app never
submits an arbitrary download URL.

## Deployment

The release ZIP contains the plugin, web client and a `deployment/` folder.
Install the plugin as a replacement for official Moonbase. Copy the verified
catalog to `<MoonfinPlugin.ResolveDataFolderPath()>/sheetmusic-catalog.json`
and the worker to a protected host path. The worker requires access to the
plugin's `sheetmusic-queue`, local download spool, Docker's existing Shelfmark
container (for Jellyfin scan credentials), and the dedicated `Noten` library
root. Run it as a bounded systemd one-shot timer; do not expose a new port.

The host library path is `/mnt/gdrive/media/sheetmusic`, visible to Jellyfin
at `/media/sheetmusic` through the existing `/mnt/gdrive/media:/media:ro` mount.
Create a Jellyfin Books library named `Noten` at that path before enabling
requests. The worker writes a checked PDF, SHA-256 and `metadata.opf` carrying
composer, title, source and license. It marks a request `ready` only after a
Jellyfin item with the exact imported path appears. The app's `Open in app`
action uses that item ID; `Download` uses the authenticated owner-only File API.

The worker currently imports PDF scores. MusicXML/MXL, LilyPond and ABC can be
added once an on-server renderer and native reader path have been tested. The
catalog/search design is source-based and does not require Prowlarr to gain
score-specific support.

## Verification

- `dotnet run --project Jellyfin/backend/BooksProxy.SmokeTests`
- `dotnet test Jellyfin/tests/Moonfin.Server.Tests/Moonfin.Server.Tests.csproj`
- `python3 -m unittest discover -s tests -p test_sheetmusic_worker.py`
- In Moonfin Books: `flutter test test/books/sheet_music`
- Before any production rollout, submit one real request, confirm a PDF and
  OPF under the `Noten` library, a Jellyfin item ID, in-app opening and an
  owner-only download. Check active playback before restarting Jellyfin.
