# Set up Moonbase Books on Jellyfin

This guide covers the Books integration tested with **Jellyfin 12.1**, **Moonbase Books 2.3.1.0**, and **Shelfmark 1.3.15**. Keep Shelfmark and its downloader on a private network reachable from Jellyfin. Use your own authorized indexers, storage paths, and credentials. Do not publish Shelfmark's proxy-auth endpoint directly to the internet.

## 1. Prepare Shelfmark

1. In Shelfmark **Settings → Security**, select **Proxy Authentication**. Set the user header to `Remote-User`, the admin group header to `Remote-Groups`, and the admin group name to **`admins`**. This group name matters: the plugin sends exactly `admins` for a validated Jellyfin administrator. With another group name, Shelfmark will not recognize that administrator; with no group name, Shelfmark may fall back to its own existing roles.
2. Restrict access to Shelfmark's API to the trusted Jellyfin server or private container network. The proxy-auth headers are authoritative to Shelfmark; any untrusted client that can send them could impersonate a user. If a separate Shelfmark website is exposed, protect it with its own authenticated reverse proxy and ensure only trusted proxy traffic reaches the API.
3. In Shelfmark **Settings → Prowlarr**, enable Prowlarr, set its reachable internal URL and API key, and select the specific indexers you are permitted to use. The Books API accepts only `prowlarr` releases. Other Shelfmark release sources are outside this integration.
4. In Shelfmark's downloader settings, configure a client supported by your chosen Prowlarr results, such as SABnzbd for Usenet (`PROWLARR_USENET_CLIENT=sabnzbd`). Set the client's URL, API key, and book/audiobook categories as needed. Keep credentials in protected server configuration, never in a public repository.
5. Set Shelfmark's output mode and ingest directory to your intended **persistent** book or audiobook destination. Ensure Shelfmark can write there and Jellyfin can read it through the relevant libraries. Verify the downloader's completed files are visible to Shelfmark before relying on imports. Decide how your library scans or other post-import automation will detect new media; the plugin does not configure those tasks for you.

Shelfmark's proxy-auth settings can also be expressed through its documented `AUTH_METHOD=proxy`, `PROXY_AUTH_USER_HEADER=Remote-User`, `PROXY_AUTH_ADMIN_GROUP_HEADER=Remote-Groups`, and `PROXY_AUTH_ADMIN_GROUP_NAME=admins` options. Store any Prowlarr or downloader API key outside the repository.

## 2. Install the Jellyfin plugin

1. Back up your existing Moonbase plugin configuration and plugin files. Check for active playback before restarting Jellyfin.
2. Build or obtain the reviewed **Moonbase Books Jellyfin ZIP** and check its `SHA256SUMS`. The [plugin workflow](../.github/workflows/books-plugin.yml) builds it with a pinned Moonfin Books web source commit. It is a CI artifact until the maintainers publish a verified package.
3. Replace the existing official Moonbase plugin files in your Jellyfin plugins directory with this build while Jellyfin is stopped. Put the ZIP contents in one plugin directory, with `meta.json` and `Moonfin.Server.dll` at that directory's top level. Use the actual plugins data directory for your installation; a Docker installation commonly uses `/config/plugins/`. Do not keep both builds active: they share the assembly and plugin GUID.
4. Start Jellyfin and confirm Moonbase Books loads. Keep automatic upstream replacement disabled; the custom package metadata has `autoUpdate: false`.

## 3. Enable Books

1. Open **Jellyfin Dashboard → Plugins → Moonbase Books → Settings**. Leave **Settings Sync** enabled and, in the **Books (Shelfmark)** section, enable **Books** (`BooksEnabled`). The current client capability check requires both settings.
2. Set **Shelfmark URL** (`BooksShelfmarkUrl`) to the private base URL reachable **from Jellyfin**, for example `http://shelfmark:8084` when both containers share a network. Use only an `http://` or `https://` base URL, without credentials, a path, query, or fragment. Do not enter a public Shelfmark site URL.
3. Save the plugin settings. Sign in to a matching Moonfin Books client with a Jellyfin user who is allowed to use the service. The Books entry appears when the authenticated plugin `Ping` advertises `booksEnabled: true`. Open **Books**, search, switch between **Books** and **Audiobooks**, and inspect releases. A new release search can return `202` while its job runs; the app polls by job ID.
4. Before a real download, verify the desired indexer, download client, category, and import destination in Shelfmark. A submitted release starts a server-side job; it does not download to the user's device. Check that a regular user sees only their own status and that an administrator receives the intended Shelfmark role.

The Books API lives at `/Moonfin/Books/v1/` on the configured Jellyfin server. The web client is at `/Moonfin/Web/`. Clients use their current Jellyfin URL and session, so no fixed host name is compiled into the Books API client. A `503` Books response usually means the feature is disabled or its Shelfmark URL is invalid; an unavailable Books entry can also mean the client or plugin build is too old.

## Protect state and update carefully

The plugin stores its Books data-protection keys under Jellyfin's **plugin configurations** data directory in `Moonfin/books-keys`. Restrict access and include this directory in backups. Losing the keyring invalidates earlier release selections; users must search again. Keep Shelfmark's own data and import destination backed up according to your storage plan.

When updating Jellyfin, Shelfmark, or either fork, rebuild and test the matched plugin/web pair. Review Shelfmark API and authentication behavior before changing its version. The upstream Moonbase plugin or official Moonfin store app may overwrite or omit this custom Books integration if installed in place of these forks. Emby Books requests are not implemented in this build.
