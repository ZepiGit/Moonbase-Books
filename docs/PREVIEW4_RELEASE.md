Moonbase Books 2.3.1.103 Preview 4 bundles the Moonfin Books Preview 3 web reader fix. After an imported score reaches `ready`, "Open in app" now displays its PDF in the plugin-served web app. The app still offers an authenticated file download.

The plugin retains the broad sheet-music search from Preview 3: Mutopia and Internet Archive both receive result slots for general searches. A requested score is downloaded, checked, imported with source metadata, and exposed through the user's Jellyfin library. Book and audiobook requests continue through the authenticated Shelfmark bridge.

This is a personal adaptation of official Moonbase, shared for others who want the same features. The ZIP replaces the official plugin with the same plugin ID. Verify `SHA256SUMS` and `SOURCE_REVISIONS.txt` before installation, and use the [server setup guide](https://github.com/ZepiGit/Moonbase-Books/blob/master/docs/SERVER_SETUP.md) for the catalog, worker and import timer.
