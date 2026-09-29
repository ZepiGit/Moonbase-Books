Moonbase Books 2.3.1.101 Preview 2 powers the new Moonfin Books app and web build.

- The authenticated Books proxy accepts title/author metadata search and routes supported Google Books/Hardcover metadata IDs through its bounded release allowlist. Book requests still use the existing Shelfmark/Prowlarr source.
- A separate, authenticated sheet-music API searches a verified 1,070-piece Mutopia PDF catalog and the Internet Archive IMSLP text collection. Requests are owner-bound jobs; a bounded host worker downloads an original PDF, checks it, writes source/licence metadata, scans the dedicated `Noten` Jellyfin library and publishes an item ID only after import.
- The artifact bundles the new Moonfin Books web build plus `deployment/` files for the catalog, worker and systemd timer. Follow `docs/SHEET_MUSIC.md` to install those host-side pieces. The plugin replaces official Moonbase and keeps the same plugin GUID/assembly; do not install both.

This is a personal adaptation of the official Moonbase plugin, shared for others with the same use case. It is not an official upstream release. Verify the ZIP with `SHA256SUMS` and check the pinned app/plugin revisions in `SOURCE_REVISIONS.txt` before deployment. The host importer currently supports validated PDF scores; other score formats need a tested viewer or renderer.
