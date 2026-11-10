# Depot

Personal download manager integrated with qBittorrent and Jellyfin.

Stack: plain HTML, CSS and JavaScript on the frontend; C# with ASP.NET Core on the backend; SQLite with Entity Framework Core for persistence.

## Structure

```
depot/
├── frontend/                      Interface (HTML, CSS and JS, no build); see frontend/README.md
├── backend/
│   ├── Depot.slnx                 Solution with every project
│   ├── Depot.Domain/              Entities and business rules, no dependencies
│   ├── Depot.Application/         Use cases and interfaces (ports); depends only on Domain
│   ├── Depot.Infrastructure/      EF Core/SQLite, qBittorrent and Jellyfin; implements the Application interfaces
│   └── Depot.Api/                 ASP.NET Core: HTTP endpoints and dependency composition
├── data/                          Local data (not versioned): depot.db and keys/
├── flake.nix                      Development environment
├── LICENSE
└── README.md
```

### Backend architecture

The backend follows clean architecture. Dependencies always point inward:

```
Depot.Api ──▶ Depot.Infrastructure ──▶ Depot.Application ──▶ Depot.Domain
    └─────────────────────────────────────────▲
```

- **Domain** knows no other project or framework.
- **Application** defines what the system does (use cases) and the interfaces it needs,
  such as a torrent client or a repository.
- **Infrastructure** implements those interfaces with concrete technologies.
- **Api** only translates HTTP into use cases and registers the dependencies.

## Environment

```sh
nix develop        # or `direnv allow`, which uses .envrc
```

The shell provides the .NET 10 SDK, dotnet-ef, git, curl, jq and sqlite.

## Run

```sh
dotnet run --project backend/Depot.Api
```

The API listens on `http://localhost:5080` and also serves the frontend (the `frontend/` folder, or `Frontend__Path`):
open `http://localhost:5080` in a browser. The first time, the interface asks for the **access token**, which is
in `data/access-token` (`cat data/access-token`). The browser keeps the token after that.

## Docker

```sh
cp .env.example .env                 # then edit .env with your paths and user
mkdir -p /path/to/depot/config       # the DEPOT_CONFIG_DIR from .env, before starting, so it belongs to you and not root
docker compose up -d                 # pulls ghcr.io/6970706f/depot:latest (--build builds from source instead)
docker compose exec depot cat /config/access-token
```

To update to the latest version: `docker compose pull && docker compose up -d`.

The image is published to `ghcr.io/6970706f/depot` by GitHub Actions on every push to `main`, after the tests
pass: `latest` follows `main`, and `sha-<commit>` (such as `sha-99b5c71`) pins a specific build. Pull requests
only build the image, without publishing it.

Open `http://localhost:5080` and paste the token. `docker-compose.yaml` has no personal paths: everything
comes from `.env` (not committed), and `docker compose` stops with a message if a required value is missing:

| Variable | Required | What it is |
| --- | --- | --- |
| `PUID` / `PGID` | yes | The user and group that own the media (`id -u` and `id -g`). Depot must be able to write to the libraries and, to create hardlinks, own the downloads or be able to write to them. |
| `DEPOT_CONFIG_DIR` | yes | Host folder mounted as `/config`: Depot's data (database, keys, token). See [Backup and restore](#backup-and-restore). |
| `MEDIA_DIR` | yes | Host folder mounted as `/data`: the same media folder qBittorrent and Jellyfin mount as `/data`. With a single mount, downloads and libraries are on the same file system and hardlinks work. |
| `DEPOT_PORT` | no (`5080`) | Port on the host for the interface. |
| `DEPOT_ALLOWED_HOSTS` | no (`localhost;127.0.0.1`) | Every name used in the browser to reach Depot (for example `localhost;server.lan`). |

The image ships with `movies` at `/data/movies`, `series` at `/data/series` and the mapping `/data` → `/data`; whatever
is saved in **Settings** takes precedence afterwards. The qBittorrent URL is as seen from inside the container: the
service name (`http://qbittorrent:8080`) in the same compose project, or `http://host.docker.internal:8080` if it runs on the host.
The `HEALTHCHECK` uses `dotnet Depot.Api.dll --healthcheck`, because the image has no curl.

## Access and security

- **Token required.** Every call to `/api/*` needs `Authorization: Bearer <token>`; without it the response
  is `401` with `code: "unauthorized"`. The token is generated randomly on first run in `data/access-token`
  (permission `0600`), or comes from `Auth__Token` (at least 32 characters). To change it, delete the file and restart.
  Only `GET /api/health` is open, for monitoring, and it reveals nothing beyond the status.
- **Same origin, no CORS.** The frontend is served by the API itself. Requests with an `Origin` from another site are
  rejected with `403 cross_origin`, even with the token, and the browser can't send the token header from
  another site. So there is no CSRF and no reading of the data by third-party pages.
- **Allowed hosts.** `AllowedHosts` accepts only `localhost`, `127.0.0.1` and `[::1]`, which blocks DNS rebinding.
  To access it from another machine, add the name used in the browser and listen on the network:

  ```sh
  ASPNETCORE_URLS=http://0.0.0.0:5080 AllowedHosts="localhost;server.lan" dotnet run --project backend/Depot.Api
  ```

  Outside localhost, put a reverse proxy with HTTPS in front: over `http://` the token travels in plain text.
- **Rate limits** per address: 20 per second (bursts of up to 200), and 30 per minute on routes that
  make qBittorrent or Jellyfin work (adding torrents, testing and saving settings, starting a library sync). Above that, `429`.
- **Permissions:** `data/` and `data/keys/` are created (or fixed) with `0700`, and `depot.db` and the token with `0600`.
- `Content-Security-Policy` (images may also be `blob:` URLs, which the page makes from the posters it fetched from `/api`), `X-Frame-Options: DENY` and `X-Content-Type-Options: nosniff` headers on every response.

## Database

The SQLite database lives in `data/depot.db` and is accessed through Entity Framework Core
(`backend/Depot.Infrastructure/Persistence`). Tables are dedicated persistence classes
(`Persistence/Records`), separate from the Domain entities; the repository converts between them.

Migrations are applied automatically when the API starts. To create a new one after changing the model:

```sh
dotnet ef migrations add <Name> \
  --project backend/Depot.Infrastructure \
  --startup-project backend/Depot.Api \
  --output-dir Persistence/Migrations
```

## Backup and restore

Everything Depot needs to start again on another server is in its data folder (`data/`, or `/config` in Docker):

| File | Needed | What it is | If it's lost |
| --- | --- | --- | --- |
| `depot.db` | yes | Settings (qBittorrent, Jellyfin, folders, path mappings), library choices, the history of every job, and the copy of the Jellyfin library with its sync history. | Everything above is gone (the Jellyfin library comes back with the next sync). |
| `keys/*.xml` | yes | ASP.NET Data Protection keys: they decrypt the stored qBittorrent password and Jellyfin API key. | Only those two secrets: see [Losing the keys](#losing-the-keys). |
| `access-token` | recommended | The token browsers send. Not there when `Auth__Token` provides it (keep your `.env` then). | A new one is created on the next start; each browser asks for it once. |

Not part of a backup: the media (`/data`: downloads and libraries, back them up with your own tools), the image cache (`cache/`, refilled from Jellyfin as images are shown), SQLite's
`depot.db-wal`/`depot.db-shm` (the backup reads what they hold), Depot's temporary `.depot-job-*` files, and the image.
Keep `docker-compose.yaml` and `.env` too: they hold your paths, user and allowed host names.

**Don't copy `depot.db` while Depot runs.** The database is in WAL mode: recent changes live in `depot.db-wal` until
SQLite folds them in, so a copy of `depot.db` alone can miss them, and copying the files one after another can catch
them halfway through a write. Either use the backup command below, which uses SQLite's online backup and reads one
consistent state while Depot keeps working, or stop Depot and copy the whole folder.

### Making a backup

```sh
docker compose exec depot dotnet Depot.Api.dll --backup /config/backups
# Backup of /config written to /config/backups/depot-backup-20261104-181911.tar.gz.
```

The archive appears in `$DEPOT_CONFIG_DIR/backups/` on the host. It holds `depot.db` (checked with SQLite's integrity
check), `keys/`, `access-token` and `depot-backup.json` (date, Depot version, last migration, file list), all
readable only by Depot's user. Without Docker, run the same command where Depot runs, with the same settings:
`dotnet run --project backend/Depot.Api -- --backup ./backups`.

**The archive is as sensitive as the data folder:** on Linux the keys are stored unencrypted, so whoever has the
archive can read the qBittorrent password and the Jellyfin API key. Copy it off the server (a backup on the same disk dies with it), encrypted,
for example with `gpg --symmetric` or a backup tool such as restic or borg. A nightly backup with 30 days kept, in the
host's crontab:

```sh
15 3 * * * cd /path/to/depot && docker compose exec -T depot dotnet Depot.Api.dll --backup /config/backups >/dev/null && find /path/to/depot/config/backups -name 'depot-backup-*.tar.gz' -mtime +30 -delete
```

### Restoring on another server

1. Install Docker, copy `docker-compose.yaml` and `.env`, and adjust `.env` to the new server (`PUID`/`PGID`, paths).
2. Create the config folder and put the archive in it, owned by `PUID`:

   ```sh
   mkdir -p /path/to/depot/config/restore
   cp depot-backup-20261104-181911.tar.gz /path/to/depot/config/restore/
   ```

3. Restore **before starting Depot**: a started Depot creates a new, empty installation, and the restore never
   replaces one. It runs in a one-off container as Depot's user, so the files get the right owner and permissions:

   ```sh
   docker compose run --rm --no-deps depot --restore /config/restore/depot-backup-20261104-181911.tar.gz
   # Restored the backup from 2026-11-04 18:19:11Z (3 files) into /config. Start Depot to use it.
   docker compose up -d
   ```

4. Open Depot: the same token works (`docker compose exec depot cat /config/access-token`). In **Settings**, check the
   qBittorrent URL and, if the media folders moved, the library folders and path mappings: they are restored as they
   were.

Before putting anything in place, the restore checks the archive: the manifest, SQLite's integrity check, and that the
backup isn't from a newer Depot (install that version or a later one first). A backup from an older Depot is fine:
its database is brought up to date when Depot starts. If the folder already has `depot.db`, keys or a token, nothing is
changed. To restore over an installation, stop it (`docker compose down`), move the contents of the config folder
away, then restore. The archive is a plain `.tar.gz`, so `tar -tzf` lists it and `tar -xzf ... -C <data folder>` also
works on a stopped Depot, but `--restore` checks it first and sets owner, permissions and journal mode for you.

### Losing the keys

The keys protect only the stored qBittorrent password and Jellyfin API key. If `keys/` is lost, or a database is restored
with keys from another backup or installation, they can't be decrypted anymore: Depot acts as if qBittorrent and Jellyfin
were never set up (**Settings** is empty for them, downloads can't be listed or added, imports wait and the library can't be
synced; the copy of the library already synced stays readable). Everything else in the database is intact, and entering
the qBittorrent password and the Jellyfin API key again fixes it. Keep the database
and keys of the same backup together. Data Protection adds a new key every 90 days and keeps the old ones in `keys/`,
and a backup always takes all of them.

## Health check

`GET /api/health` (no token) returns the overall status and the status of each check:

```json
{ "status": "degraded", "checks": { "database": "healthy", "qbittorrent": "degraded", "jellyfin": "healthy" } }
```

- `database`: SQLite opens. If it fails, the status is `unhealthy` and the response is `503`.
- `qbittorrent`: qBittorrent responds (3 s limit). Unconfigured or down, `degraded` (still `200`):
  Depot keeps responding, and the reason shows up in `GET /api/status`.
- `jellyfin`: Jellyfin responds (3 s limit). Not set up is `healthy`, since Jellyfin is optional; set up but unreachable
  is `degraded`.

The frontend queries the endpoint on load and on every automatic refresh. If the API is
unavailable, an alert appears above the page with the reason and a **Retry** button, and the status
indicator at the top turns red. The alert disappears on its own when the API responds again.

## qBittorrent settings

**Settings > qBittorrent connection** holds the URL, username and password of the qBittorrent Web UI.
The browser never talks to qBittorrent: everything goes through the backend.

| Endpoint | What it does |
| --- | --- |
| `GET /api/settings/qbittorrent` | Returns `{ url, username, hasPassword }`. The password is never returned. |
| `PUT /api/settings/qbittorrent` | Saves `{ url, username, password }`. A blank password keeps the saved one. |
| `POST /api/settings/qbittorrent/test` | Tests the form values (before saving) and returns `{ version }`. |

- Validation errors come back as `400` with the field (`errors.url`, `errors.username`, `errors.password`),
  and the interface shows the message next to the field.
- qBittorrent failures follow the format described in [qBittorrent errors](#qbittorrent-errors).
- The saved password is only reused for the **same URL**. Changing the URL requires typing the password again,
  so nobody can send the saved password to an arbitrary server.
- The password is stored encrypted (ASP.NET Core Data Protection) in `data/depot.db`. The keys live in
  `data/keys/` unencrypted on Linux, so that folder must be readable only by the user running Depot.
  If the keys are lost, the settings have to be entered again.
- The data directory can be changed with the `Storage__DataDirectory` variable.
- With an `http://` URL outside localhost, the interface warns that the password travels unencrypted.
- The backend doesn't follow qBittorrent redirects (which could carry the session or the password to another
  address) and rejects responses larger than 32 MB.

Built for qBittorrent 4.x and 5.x; tested only with 5.2.2.

## qBittorrent client

`IQBittorrentClient` (a port in Application, implemented by `QBittorrentClient` in Infrastructure) is the
only path to qBittorrent. It uses the saved settings and offers: version, torrent list,
adding by magnet link or `.torrent` file, pause, resume and remove (with or without deleting the files).

- **Session:** a single login for the whole application (`QBittorrentSessionStore`). If the session expires or
  qBittorrent restarts, the client logs in again and retries the request once.
- **A wrong password doesn't become a ban.** A rejected login (`invalid_credentials` or `banned`) isn't retried on every
  refresh: Depot waits 30 s before trying again, doubling the wait after each consecutive rejection (up to 10 min).
  Meanwhile requests fail immediately with the same error, without queueing. Saving the settings ends the wait.
- **Versions:** supports qBittorrent 4 and 5 (`SID` or `QBT_SID_<port>` cookie, `pause`/`resume` or
  `stop`/`start`, add response as text or JSON). Tested with 5.2.2 (Web API 2.15.1).
- Torrent **hashes** are validated (40 or 64 hexadecimal characters) before any command, and the
  torrent must exist, because qBittorrent responds 200 even for unknown hashes.

### Status

`GET /api/status` always responds 200 and says whether qBittorrent is configured and connected:

```json
{ "qbittorrent": { "configured": true, "connected": false, "version": null, "error": "Can't reach qBittorrent at ..." } }
```

The check uses a 3 second limit so it doesn't stall the interface's automatic refresh. The indicator
at the top and the connection list in the sidebar use this endpoint; hovering shows the reason for the failure.

### qBittorrent errors

Every failure becomes a `QBittorrentException` with a kind and a readable message, without credentials. The API returns
problem details with the message in `detail` and the kind in `code`:

| `code` | HTTP | When |
| --- | --- | --- |
| `not_configured` | 409 | No saved settings |
| `unreachable` | 502 | Address doesn't respond |
| `timeout` | 502 | No response within 10 seconds |
| `invalid_credentials` | 502 | Username or password rejected |
| `banned` | 502 | Address blocked after too many attempts |
| `unexpected_response` | 502 | The server doesn't respond like qBittorrent |
| `bad_request` | 400/415 | Unreadable parameter or body (e.g. `deleteFiles=maybe`, malformed JSON) |
| `torrent_not_found` | 404 | Invalid hash or nonexistent torrent |
| `torrent_rejected` | 422 | Invalid or already added torrent |

## Downloads

`GET /api/downloads` lists qBittorrent's torrents, newest first:

```json
[{
  "hash": "a467...955f", "name": "big-sample.bin", "status": "downloading",
  "progress": 0.28, "sizeBytes": 200000000, "downloadedBytes": 56115200,
  "downloadSpeed": 1775097, "uploadSpeed": 0, "etaSeconds": 118,
  "category": "series", "savePath": "/media/downloads", "addedAt": "2026-11-03T12:29:24+00:00",
  "library": { "mediaType": "series", "title": "The Last of Us", "season": 1 }
}]
```

- `library` is where the videos go when the download finishes (see [Media organization](#media-organization-for-jellyfin)),
  or `null` for a download that is only kept on the server.
- Speeds in bytes per second. `etaSeconds` is `null` when qBittorrent can't estimate it
  or when the torrent isn't downloading.
- `status`: `downloading`, `queued`, `checking`, `paused`, `completed` or `error`. qBittorrent 4 and 5
  states are converted (for example `stoppedDL` → `paused`, `stalledUP` → `completed`).
- **An empty list isn't a failure:** with no torrents the response is `200 []` and the interface shows "No downloads yet".
  If qBittorrent doesn't respond, the response is an error (see the table above) and the interface shows
  "Downloads didn't load" with the reason and a **Retry** button.

In the interface, the Overview and Downloads pages, the menu counter and search (`Ctrl K`) use this list.

### Adding by magnet link

`POST /api/downloads/magnet` with `{ "magnet": "magnet:?xt=urn:btih:…", "mediaType": "series", "title": "The Last of Us", "season": 1 }`.

- `mediaType`: `movie`, `series` or `other` (or absent = `other`).
- `title`: the name of the movie or series folder in the library. Required for `movie` and `series`; it can't contain `/` or `\`, or start with a dot.
- `season`: required for `series`, from 0 (specials) to 999.

| Situation | Response |
| --- | --- |
| Link accepted by qBittorrent | `201 { "hash", "name" }` (`name` comes from the link's `dn`, or is the hash) |
| Empty link, no `magnet:?`, no valid info hash, or with spaces or line breaks | `400` with `errors.magnet` |
| Invalid type, name or season | `400` with `errors.mediaType`, `errors.title` or `errors.season` |
| **Torrent already exists** | `409` with `code: "already_added"`, the `hash` and the name of the existing download |
| qBittorrent down or rejected the torrent | see [qBittorrent errors](#qbittorrent-errors) |

- The info hash is accepted as hexadecimal (40 characters), base32 (32) or v2 (`urn:btmh:1220…`). A
  duplicate is detected by comparing every known hash (v1 and v2) with the ones qBittorrent
  reports, so the same torrent written differently, or added earlier from a file, also counts.
- The returned `hash` is the id qBittorrent uses: the v2 one (truncated to 40 characters) when the torrent
  has v2, hybrids included; otherwise the v1 one.
- Depot checks for duplicates before sending, so the response is the same with any version
  of qBittorrent.
- Movies and series are sent to qBittorrent in the `movies` and `series` categories (which set the folder where
  it saves them); `other` goes without a category, to the default folder. The choice is stored before sending.
- If sending fails, Depot asks qBittorrent whether the torrent got in: the choice is only undone when it
  isn't there. A timeout after qBittorrent accepted the torrent keeps the choice.
- Additions happen one at a time, and each waits for qBittorrent to list the new torrent (up to 3 s). Two
  simultaneous additions of the same torrent result in one accepted and one `409 already_added`, without losing the
  accepted one's choice.
- The link can't contain spaces or line breaks: qBittorrent reads one link per line, so a line break
  would add a second torrent that skips all of these checks.

### Adding a .torrent file

`POST /api/downloads/torrent` as `multipart/form-data`, with the `file` field and the `mediaType`, `title` and `season` fields.
The responses are the same as for magnet links (`201`, `400`, `409 already_added`), with the validation error
in `errors.file`, plus:

- **Limit:** 10 MB per file (`TorrentFile.MaxSizeBytes`). Up to 64 KB beyond that, the domain responds
  `400` explaining the limit; a larger request is cut off by the server with `413`. The frontend checks
  the size and extension before sending.
- **Validated by content**, not by name: the file must be bencode with an `info` dictionary for a
  v1, v2 or hybrid torrent. Empty file, text, random bytes or truncated: *"This file isn't a
  valid .torrent file."* Also rejected: duplicate keys at the top level or inside `info` (libtorrent
  could read a different `info` and the hash wouldn't match), any byte after the end of the dictionary, and more than 64
  levels of nesting.
- The reader keeps only the few `info` fields it uses and skips the rest without building anything in memory, so a
  10 MB file full of tiny values costs one read and nothing more.
- Hashes are computed from the exact bytes of `info` (SHA-1 and/or SHA-256), the same ones qBittorrent
  uses, and serve to detect duplicates before sending.
- The file only passes through the backend on its way to qBittorrent: Depot writes nothing to disk, and neither
  the content nor credentials appear in logs or responses.

In the interface, file errors appear in the dialog footer (there's no text field for them).

### Pause and resume

`POST /api/downloads/{hash}/pause` and `POST /api/downloads/{hash}/resume` respond `200` with the download
already in its new state (the same format as `GET /api/downloads`), or `404 torrent_not_found` if the hash doesn't
exist in qBittorrent.

- qBittorrent applies the command asynchronously and takes about 1.5 s to report the new state.
  Depot waits up to 3 s for it before responding, so the interface already shows the right state on the
  next refresh.
- Pausing something already paused is accepted and responds immediately. A completed torrent stays `completed`
  after being paused.
- The hash must have 40 or 64 hexadecimal characters. This also prevents a value like `a|b`
  from becoming a list of torrents in the qBittorrent API.
- In the interface, the row button toggles between Pause and Resume and stays busy while waiting. The
  **Retry** button on a torrent in error uses the same `resume`, which is how qBittorrent retries. Failures
  appear in a notice with the reason, and the row keeps its previous state.

### Remove

`DELETE /api/downloads/{hash}?deleteFiles=true|false` responds `200` with what was done:

```json
{ "hash": "4fb9...1825", "name": "api-keep.bin", "filesDeleted": false, "savePath": "/media/downloads" }
```

- **`deleteFiles` is required.** Without it the response is `400` and nothing is removed: files are never
  deleted (or kept) by default. An unreadable value (`deleteFiles=maybe`) is also `400`.
- `false` removes the torrent from qBittorrent and **keeps the files**; `true` also deletes the downloaded
  files (the whole folder, for a multi-file torrent).
- `404 torrent_not_found` for a nonexistent or malformed hash. Depot waits for qBittorrent to confirm the
  removal (up to 3 s), so the torrent doesn't reappear on the next refresh.

In the interface, the remove dialog:

- shows both options side by side, with the affected folder and size, and starts on **keep files**;
- in delete mode, the button turns red ("Remove and delete files") and is only enabled after checking
  *"I understand that 2 MB in /media/downloads will be permanently deleted."*. Switching modes unchecks
  that confirmation;
- after removing, the notice says what happened to the files and where they are (or were).

## Jellyfin library

Jellyfin is the source of the library's metadata and images. Depot reads it through the Jellyfin REST API and keeps
its own copy (titles, seasons, episodes, every file of each item with its technical details), so the library can be
browsed even while Jellyfin is down. Depot never plays media and has no metadata providers of its own.

### Settings

**Settings → Jellyfin connection** holds the server URL and an API key (Jellyfin: *Dashboard → API Keys*).

| Endpoint | What it does |
| --- | --- |
| `GET /api/settings/jellyfin` | `{ url, hasApiKey }`. The key is never returned. |
| `PUT /api/settings/jellyfin` | Saves `{ url, apiKey }`. A blank key keeps the saved one, **only for the same URL**. |
| `POST /api/settings/jellyfin/test` | Tests the form values before saving: first that the URL answers like Jellyfin (its public info, without the key), then that the key is accepted. Returns `{ serverName, version }`. |
| `GET /api/jellyfin/status` | `{ configured, connected, serverName, version, error }`; the same object is in `GET /api/status` as `jellyfin`. Reused for 10 s, so polling doesn't reach Jellyfin every second. |

- The key is stored encrypted with Data Protection (its own purpose, next to the qBittorrent password) and only ever
  travels in the `Authorization: MediaBrowser … Token="…"` header, never in a URL, a response, a log line or the browser.
- **URL rules (SSRF):** `http`/`https` only, no user name, query or fragment. Loopback and private addresses are allowed,
  since Jellyfin usually runs on the same machine or network; link-local (`169.254.0.0/16`, where cloud metadata
  services answer, and `fe80::/10`), `0.0.0.0/8`, multicast, broadcast and reserved ranges are refused, also as
  IPv4-mapped or NAT64 addresses. Host names are checked again **when connecting**, on the addresses they resolve to,
  so DNS rebinding can't get around the rule. Depot uses no proxy for Jellyfin and **never follows redirects** (one
  could carry the key elsewhere): a redirect is an error that asks for the final address.
- Answers are checked: JSON only, at most 32 MB; ids must be GUIDs; numbers in range; text trimmed and cut to a sane
  length. An item that can't be read is skipped and counted, without stopping the page. Requests time out after 15 s.
- Errors are problem details with `code`: `not_configured` (409), `unreachable`, `timeout`, `invalid_api_key`,
  `unexpected_response` (502) and `address_not_allowed` (422).

### Sync

Syncs are **manual** for now: **Settings → Library sync → Sync now**, or `POST /api/jellyfin/sync`, which answers `202`
with the run and `Location: /api/jellyfin/sync/runs/{id}`. The sync runs in the background:

1. Lists the libraries (`/Library/VirtualFolders`). Movies, Shows and mixed libraries are synced; others (music,
   books...) are only listed. A library gone from Jellyfin is deleted locally with its items.
2. Reads each library's items with `/Items` (`ParentId`, `Recursive`, `IncludeItemTypes`), 100 per page, sorted by date
   added so new items don't shift the pages not read yet. Each page is saved as it arrives: new items added, changed ones
   updated (only real changes count), files matched by id. Items are unique by Jellyfin id, so nothing is kept twice.
3. Items of a library read to the end that weren't seen are looked up by id (`/Items?Ids=`): only the ones Jellyfin
   confirms are gone are removed. A page that shifted while items were added or removed never deletes anything.
4. **Partial failures:** a library that fails halfway keeps what it read and removes nothing; the others are still read.
   When Jellyfin is down or refuses the key, the remaining libraries are reported instead of tried.

**Only one sync at a time:** starting another while one is queued or running answers `409 sync_running` with its
`runId`. A run left unfinished by a stop or crash is marked `interrupted` at the next start. Each run records its
status (`running`, `succeeded`, `completedWithErrors`, `failed`, `interrupted`), the library being read, how many items
were seen, added, updated, unchanged, removed and skipped, and an error per failed library. The latest 50 runs are kept.

| Endpoint | What it returns |
| --- | --- |
| `GET /api/jellyfin/sync` | `{ isRunning, latestRun }` |
| `GET /api/jellyfin/sync/runs` / `GET /api/jellyfin/sync/runs/{id}` | The latest 20 runs / one run |

A periodic sync only needs a timer in `JellyfinSyncWorker` calling `JellyfinSyncService.StartAsync(SyncTrigger.Scheduled)`.

### Library

All read from Depot's copy, as of the last sync:

| Endpoint | What it returns |
| --- | --- |
| `GET /api/jellyfin/libraries` | Each library with its kind, folders, counts of movies, series, seasons and episodes, and when it was last read completely. |
| `GET /api/jellyfin/items` | `{ items, total, page, pageSize }`. `kind` (`movie`, `series`, `season`, `episode`, `collection`; movies and series by default), `libraryId`, `search` (name or original title), `genre` (one genre, exactly), `year`, `anime` (`true` only anime, `false` none), `collectionId` (a collection's titles), `sort` (`name`, `added`, `year`, `release`), `page`, `pageSize` (up to 200). Each item carries what a poster card shows: year, genres, rating, length, episode count, image versions; a collection without a poster brings the posters of its first titles (`preview`). |
| `GET /api/jellyfin/items/{id}` | Metadata (overview, genres, studios, countries, ratings, provider ids such as IMDb and TMDB...), directors, writers and cast with their roles, **every file** (a movie can have several versions) with container, size, bitrate, runtime and its video, audio and subtitle streams, the collections it's in; the seasons of a series, the episodes of a season (with their synopses) or the titles of a collection, and the series and season of an episode. |
| `GET /api/jellyfin/filters` | `{ genres: [{ value, count }], years: [{ value, count }] }` for `kind` and `anime`: what the filters can offer. |
| `GET /api/jellyfin/stats` | Counts of movies, series, seasons, episodes, collections and anime, files and their size, the 8 most common genres and the last sync. |
| `GET /api/jellyfin/items/{id}/images/{kind}` | `primary` (poster), `backdrop` (`?index=`), `thumb` or `logo`; `?width=` and `?v=`. |
| `GET /api/jellyfin/items/{id}/people/{index}/image` | The photo of someone in the item's people, by their place in the list; only people the item records, so the browser can't make Depot fetch any other image. |

**Collections** are Jellyfin's box sets: its *Collections* folder is synced like a library, and each collection's titles
are read with it (`/Items?ParentId=<collection>&Recursive=true`).

**Anime** isn't a type in Jellyfin, so Depot works it out at sync time (`MediaCategories.IsAnime`) from metadata
Jellyfin has: a movie or series is anime when its library's name says so (*Anime*, *Anime Movies*...), when *Anime* is
one of its genres or keywords (TMDB tags anime that way), or when it's animation from Japan (country of production, or
an original title in Japanese kana). Animation alone isn't anime.

### Images

Images are never stored in the database. The browser asks Depot (with its token, like any `/api` call); Depot
serves them from its cache in `data/cache/images/` or, the first time, fetches them from Jellyfin with the key, which
never reaches the browser.

- Each item records the version (tag) of each image Jellyfin has. The interface asks with `?v=<version>`: that
  address never changes content, so it's cached `private, max-age=31536000, immutable`; a new image is a new version and
  a new address. Without `v`, the answer is `private, no-cache` with an `ETag` (and `304` on a match).
- An image the item doesn't have answers `404 image_not_found` without asking Jellyfin. Cached images still answer
  while Jellyfin is down.
- Widths are rounded up to 160, 320, 480, 720, 1080 or 1920 px, so arbitrary widths can't fill the cache. Only JPEG, PNG,
  WebP and GIF are passed on, recognized by their bytes (never SVG), up to 15 MB.
- The cache is private (`0700`/`0600`), named by a hash of the item, kind, version and width, and limited to
  `Jellyfin:ImageCacheMegabytes` (default 512): the images used longest ago go first. Deleting the folder loses nothing.
- The page shows them as `blob:` URLs made from what it fetched, since an `<img>` can't send the token.

## Interface

### Dashboard (Overview)

- **Metrics:** active downloads (with paused and errored), total download and upload speed, and
  completed with the space they use. All computed from the real list from `GET /api/downloads`.
- **Connections:** each service with its state and the reason when it isn't connected. The same data
  feeds the indicator at the top and the list in the sidebar (`js/components/service-status.js`), so the
  three places always agree: qBittorrent and Jellyfin. Jellyfin is optional, so while it isn't set up it shows
  as a grey ring (*Not set up*) and doesn't turn the indicator red.
- **No fake numbers:** if qBittorrent becomes unavailable, the metrics show "—" (*Unavailable*) and the table shows the reason,
  instead of keeping stale numbers. Everything recovers on its own when it responds again.

### Download list

- Each row shows name, progress (the row's bottom rule is the progress bar), percentage,
  speeds, ETA, size and state. States have their own icon and label; only errors use red.
- Actions depend on the state: Pause (downloading, queued, checking), Resume (paused), Retry (error),
  and Remove on all of them.
- **Refreshes every 2 s** (adjustable in Settings: 1, 2 or 5 s). A refresh only starts after
  the previous one finishes, and stops while the tab is hidden.
- **One request per endpoint per cycle:** identical reads made at the same time (page, menu counter,
  status bar) share the same in-flight request (`shared()` in `js/api.js`).
- **Refreshing doesn't interrupt the user:** rows are updated in place, so focus on a button,
  the text and selection in the search box, and an open dialog stay as they were.

### Media library

The **Library** group of the menu shows the Jellyfin library as Depot keeps it (see [Jellyfin library](#jellyfin-library)):

- **Overview**: totals (titles, movies, series, episodes, files and size), the split by category (movies, series,
  anime) and the most common genres as bars, then the titles added most recently, the newest releases and the
  collections.
- **All media**, **Movies**, **Series**, **Anime** and **Collections**: a poster grid with as many columns as fit. Search
  by title, filter by genre and year (the lists only offer what the library has, with counts), and, on All media and
  Anime, by type; sort A to Z, by newest release or by recently added. Pages of 60 load as the grid is scrolled. The
  filters are kept in the address, so going back to a list finds it as it was. Hovering a poster shows its rating,
  length or episodes and genres.
- A title's page: backdrop and poster, original title, year, length, rating, age rating, countries, genres (each a
  link to that genre), synopsis, director, writers, studio, cast with photos and roles, the collections it's in, links
  to IMDb and TMDB, and each file with its video, audio and subtitle tracks. A series has its seasons as tabs with
  each episode's still, number, length and synopsis; a collection lists its titles.
- A bar above every library page shows the last sync (when, what it changed, its errors), its progress while it runs,
  and **Sync now**, disabled while a sync runs. When Jellyfin can't be reached the pages keep showing Depot's copy,
  with a notice; images already shown come from Depot's cache.
- Every state is spelled out: loading (skeletons in the shape of the posters), Jellyfin not set up (with a link to
  Settings), never synced (with **Sync now**), nothing in a category, no search results (with **Clear filters**), a
  title without a poster (its name on a plain tile; a collection shows its titles' posters), a title removed since,
  and the API unreachable (with **Retry**).
- Images are fetched as they come near the screen, a few at a time, and kept by the browser for good under their
  version; when Depot's rate limit refuses a burst, they're tried again after a pause.

### No simulated data

All data in the interface comes from the API. Pages for features that don't have a backend yet
(such as activity history) aren't shown. Old routes like `#/movies` open the Overview.

## Media organization for Jellyfin

You decide where each download goes when you add it. Depot **doesn't guess titles from file names**: it puts
the videos in the chosen folder, with their original names, **without touching the downloaded files** (it creates
hardlinks or copies), and the torrent keeps seeding. Within a series, it only reads the download's layout to keep
extras, specials and other seasons out of the chosen season, and asks you whenever that layout is unclear.

When you click **Add download**, the dialog asks what it is:

| Choice | What Depot does when the download finishes |
| --- | --- |
| **Movie** | Asks for the movie's name and puts the videos in `movies/<name>/`. |
| **Series** | Asks for the series (one of the folders that already exist in `series/`, or a new one with the name you type) and the season, and puts the videos in `series/<series>/Season NN/`. |
| **Other** | Nothing: the download just stays on the server, where qBittorrent saved it. |

```text
movies/Dune (2021)/Dune.2021.1080p.BluRay.mkv
series/The Last of Us/Season 01/The.Last.of.Us.S01E01.1080p.mkv
```

- **Only videos** (`.mkv`, `.mp4`, `.avi`, ... — list in `MediaFiles.VideoExtensions`) **and their subtitles** go to the
  library. `.nfo`, images, *samples* (`sample` files or files in a `Sample` folder) and anything else stay only in the download.
- **Subtitles** (`.srt`, `.ass`, `.ssa`, `.vtt`, `.sub`/`.idx`, `.sup`, `.smi` — `MediaFiles.SubtitleExtensions`) come with
  their video when they are **named after it** (`Dune.srt`, `Dune.en.srt`, `Dune.pt-BR.forced.ass`), next to it or in a
  `Subs`/`Subtitles` folder right below it. They keep their names, so the language suffix and the pairing Jellyfin
  uses stay as they are, and they use the same hardlink or copy as the video. Being in the same folder isn't enough:
  `English.srt` or `Subs/2_English.srt` belong to no video and stay in the download. A subtitle that could belong to two
  videos (`Dune.srt` next to `Dune.mkv` and `Dune.mp4`) goes to none; with `Dune.mkv` and `Dune.Extended.mkv`, it goes to
  the longest matching name. Subtitles are placed before the video, so after a crash the video is never there without
  them. If the library already has a subtitle with that name, an identical one counts as imported (no duplicate) and a
  different one is **never overwritten**: it is left out and the job says so. The job's message lists the subtitles
  imported and left out (**Imports** page).
- **Episodes, specials and extras (series):** each video of a series download is sorted by the download's
  *structure* before anything is imported (`SeriesVideos`):
  - an extras folder (`Extras`, `Featurettes`, `Behind the Scenes`, `Deleted Scenes`, `Special Features`, `Trailers`,
    `Interviews`, ...) sends the video to the matching Jellyfin extras folder inside the series:
    `series/<series>/featurettes/`, `.../behind the scenes/`, `.../deleted scenes/`, `.../trailers/`, `.../interviews/`
    or `.../extras/`;
  - a season folder (`Season 2`, `S02`, `Specials`, `Season 00`) and an episode number (`S02E03`, `2x03`, `E03`,
    `Show - 03`) give the season. **Specials are episodes of season 0, never extras**: they go to the series'
    `Specials`/`Season 00` folder;
  - *samples* stay in the download, as before.

  When the clues agree and match the season chosen when adding, the video is imported. Otherwise it **waits for
  review** (`NeedsReview`) with a suggestion and the reason, and is never imported before a person decides: another
  season (a multi-season pack), a special while season 1 was chosen, an episode number inside an extras folder (the
  series *Extras*), a name with "trailer"/"featurette"/"interview"... next to numbered episodes, an unnumbered video
  among numbered ones (one much smaller than the episodes is suggested as an extra), or a name and folder that
  disagree. **A word in a name only ever leads to a suggestion, never to a decision.** Downloads whose videos are all
  named freely go to the chosen season, as before. Movies aren't sorted.
- **Seasons:** if the series already has a folder for the season (`Season 1`, `Season 01`, or `Specials` for
  season 0), it's reused; otherwise Depot creates `Season 01`. When you pick an existing series, the dialog
  shows the seasons it already has and suggests the latest.
- **Nothing is overwritten.** If a file with the same name already exists at the destination, the job fails with the
  reason and can be retried (**Retry**) after moving the file.
- **Interrupted imports** (Depot killed, crash, power loss) are settled on the next start, before any new import.
  Just before a file can appear at the destination, Depot saves with the job the identity (inode) of the file it is
  about to publish: the downloaded file for a hardlink, the temporary copy (`.depot-job-<id>-v.tmp`, kept by the
  final rename) for a copy. On startup, a file at the destination counts as this import's only if it has that
  identity; **its name and size prove nothing**, since an older file, a duplicate or another job's file can match
  both. So: nothing at the destination → the job's temporary files are removed and it runs again; the file this
  import wrote → the job is completed; anything else → the job fails with the reason and the file is left alone
  (skip the job if it is this video, otherwise move it and retry). Recovery never reads file contents, so it costs
  the same for a 50 GB video, and running it again gives the same result. Without inodes (outside Linux, glibc
  older than 2.28) a file found at the destination after a crash always goes to a person.
- Torrents without a choice (added as *Other* or directly in qBittorrent) are never touched.
- The choice is stored per torrent hash (`LibraryTargets` table) and shows up in the download list,
  next to the name. Removing the download through Depot deletes the choice (jobs stay as history). Adding the
  same torrent again replaces any old choice, including as *Other*, which deletes it along with
  jobs not yet imported.

**Depot must run on the machine that sees qBittorrent's files and Jellyfin's folders**
(the server). Configure it in `appsettings.json` → `Organization`, or with environment variables
(`Organization__MoviesPath`, ...); once saved in **Settings → Media library**, the saved values apply:

| Key | Default | What it is |
| --- | --- | --- |
| `MoviesPath` / `SeriesPath` | `~/media/movies` / `.../series` | Library folders (`~/` = home of the user running Depot). They aren't created automatically. The dialog's series list comes from the folders inside `SeriesPath`. |
| `ImportMode` | `Auto` | `Auto` (hardlink, otherwise copy), `Hardlink` (fails instead of copying) or `Copy`. |
| `PollingIntervalSeconds` | `60` | How often Depot looks for completed downloads (10 to 3600). |
| `PathMappings` | `[]` | **Required for importing.** `{ "QbittorrentPath": "/downloads", "DepotPath": "~/media/downloads" }`. These are the only folders Depot reads downloads from: a `save_path` outside them is never used, whatever path qBittorrent reports. With qBittorrent on the same machine, map the folder to itself (`/srv/downloads` → `/srv/downloads`). |

In Jellyfin, create two libraries: **Movies** (type Movies) pointing to the movies folder and **Shows**
(type Shows) pointing to the series folder. If Jellyfin runs in Docker, use the path *inside* the
container that corresponds to the same volume.

Endpoints: `GET /api/library/series` (`[{ "name": "The Last of Us", "seasons": [1, 2] }]`),
`GET|PUT /api/organization/settings`, `POST /api/organization/settings/test`,
`GET /api/organization/status`, `GET /api/organization/jobs[?status=]`, `GET /api/organization/jobs/{id}`,
`POST /api/organization/jobs/{id}/retry|skip`, `POST /api/organization/jobs/{id}/classify` (series only:
`{ "kind": "main", "season": 2 }` for an episode, season 0 for a special, or `{ "kind": "extra", "extrasFolder": "featurettes" }`)
and `POST /api/organization/downloads/{hash}/confirm` (accepts the suggestions for every video of the download that
waits for review).

- **Symbolic links:** the downloaded file can't be a link, nor can any folder between the mapped folder and it;
  on the library side, no folder between the library root and the destination. The configured roots (library
  and mapping folders) can be links.

Tests: `dotnet test backend/Depot.slnx` (domain, use cases, file organizer, qBittorrent and Jellyfin clients
against simulated servers, library sync and image cache on SQLite, and the endpoints with `WebApplicationFactory`). GitHub Actions
(`.github/workflows/ci.yml`) runs the tests and checks the syntax of the frontend scripts on every push and pull request.

### Recommended setup

**1. A single folder, mounted the same way in every service.** Hardlinks only work within the same mount
point: if qBittorrent mounts `/downloads` and Jellyfin mounts `/movies` as separate volumes, the
hardlink fails even on the same disk and everything becomes a copy (twice the space). Mount the whole parent folder:

```text
/path/to/media/                ← MEDIA_DIR; mounted as /data in qBittorrent and Jellyfin
├── downloads/                 ← qBittorrent saves here
│   ├── movies/                ← folder for the "movies" category in qBittorrent
│   └── series/                ← folder for the "series" category in qBittorrent
├── movies/                    ← Jellyfin Movies library (type Movies)
└── series/                    ← Jellyfin Shows library (type Shows)
```

With Depot running directly on the server, the mapping is `/data` → `/path/to/media`.

**2. In Jellyfin**, turn on *Real-time monitoring* for both libraries: items show up as soon as Depot
finishes importing. Since files keep their original names, Jellyfin identifies episodes by
the name itself (`S01E01`, `1x01`, ...); give the movie or series folder a recognizable name (with the year, if
you like) so it finds the right title.

**3. After seeding** (ratio or time limit in qBittorrent), remove the torrent **deleting the
files**. With hardlinks this only removes the name in `downloads/`: the library file remains, because
it's the same file under another name. (In Copy mode there are two real copies.)

### In the interface

- **Add download**: Movie, Series or Other; for a series, the list of series already in the library
  (or *New series…*) and the season; then the magnet link or the `.torrent` file.
- **Downloads**: each row shows, next to the name, where the videos go (`Movie · Dune (2021)` or
  `The Last of Us · S1`).
- **Settings → Media library**: movie and series folders, import mode, interval and path
  mappings. **Test folders** checks the folders without saving; **Folder check** shows each resolved folder
  (with `~/` expanded), whether it can be read/written, the free space, and whether hardlinks are possible between
  downloads and library.
- **Imports**: one job per video, with source, placement (*Episode · Season 2*, *Special · Season 0*,
  *Extra · featurettes*), destination, state and reason. **Retry** tries a failed or skipped job again; **Skip** only
  marks the job: no file is changed or deleted. The **Review** filter lists the videos waiting for review:
  **Confirm** accepts the suggestion, **Confirm all N** accepts it for every video of that download that waits, and
  **Change** imports a series video as an episode (any season), a special or an extra (choosing the extras folder);
  **Change** also works on waiting, failed and skipped jobs. The number in the menu counts failed jobs and those
  waiting for review.

### Upgrading from an earlier version

The `AddMediaLibraryDetails` migration adds cast and crew, keywords, countries, collection members and the anime flag to
the Jellyfin library. Libraries synced before it fill them in on the next sync, which counts every title as updated once.
The import jobs page, formerly **Library**, is now **Imports** (`#/imports`); **Library** is the media library.

The `ChooseLibraryWhenAdding` migration removes file-name-based classification: old jobs that
hadn't been imported yet (in review, waiting or failed) are deleted; imported ones stay
as history. Downloads that were already in qBittorrent have no library choice, so they aren't
organized; to organize one of them, remove it (keeping the files) and add it again choosing
Movie or Series (qBittorrent only reuses what it already downloaded if the destination folder is the same).

The `ClassifySeriesVideos` migration adds each job's placement (episode season, or extras folder). Jobs found by
earlier versions keep going to the season chosen when their download was added; only downloads examined from now on
are sorted and may wait for review.

The `AddExpectedFileIdToOrganizationJobs` migration adds the identity of the file each import publishes. A job
that was interrupted by the version before it has none: if its destination already has a file, recovery fails the
job with the reason instead of assuming the import finished.

When upgrading to the version with an access token:

- The interface is now opened at `http://localhost:5080` (served by the API), no longer as a file, and asks for the
  token from `data/access-token` once per browser.
- Without path mappings nothing is imported: jobs wait with the reason. If qBittorrent runs on the
  same machine, add the downloads folder mapped to itself in **Settings → Media library**.
- To access it from another machine, add its name to `AllowedHosts` (see [Access and security](#access-and-security)).

## License

[MIT](LICENSE).
