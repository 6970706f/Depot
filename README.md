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
mkdir -p ~/containers/depot          # before starting, so the folder belongs to you and not root
docker compose up -d --build         # uses the compose.yaml at the root
docker compose exec depot cat /config/access-token
```

Open `http://localhost:5080` and paste the token. In `compose.yaml`, adjust:

- `user`: the same user that owns the media (`id -u` and `id -g`). Depot must be able to write to the libraries
  and, to create hardlinks, own the downloads or be able to write to them.
- `AllowedHosts`: every name used in the browser to reach Depot (for example `localhost;server.lan`).
- Volumes: `/config` holds Depot's data (database, keys, token); `/data` is the same media folder that
  qBittorrent and Jellyfin mount as `/data`. With a single mount, downloads and libraries are on the same
  file system and hardlinks work.

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
  make qBittorrent work (adding torrents, testing and saving settings). Above that, `429`.
- **Permissions:** `data/` and `data/keys/` are created (or fixed) with `0700`, and `depot.db` and the token with `0600`.
- `Content-Security-Policy`, `X-Frame-Options: DENY` and `X-Content-Type-Options: nosniff` headers on every response.

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

## Health check

`GET /api/health` (no token) returns the overall status and the status of each check:

```json
{ "status": "degraded", "checks": { "database": "healthy", "qbittorrent": "degraded" } }
```

- `database`: SQLite opens. If it fails, the status is `unhealthy` and the response is `503`.
- `qbittorrent`: qBittorrent responds (3 s limit). Unconfigured or down, `degraded` (still `200`):
  Depot keeps responding, and the reason shows up in `GET /api/status`.

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

## Interface

### Dashboard (Overview)

- **Metrics:** active downloads (with paused and errored), total download and upload speed, and
  completed with the space they use. All computed from the real list from `GET /api/downloads`.
- **Connections:** each service with its state and the reason when it isn't connected. The same data
  feeds the indicator at the top and the list in the sidebar (`js/components/service-status.js`), so the
  three places always agree. For now the only service is qBittorrent; Jellyfin will join when
  the integration exists.
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

### No simulated data

All data in the interface comes from the API. Pages for features that don't have a backend yet
(such as activity history or Jellyfin integration) aren't shown. Old routes like `#/movies` open the Overview.

## Media organization for Jellyfin

You decide where each download goes when you add it. Depot **doesn't try to infer anything from
file names**: it just puts the videos in the chosen folder, with their original names, **without touching the
downloaded files** (it creates hardlinks or copies), and the torrent keeps seeding.

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

- **Only videos** (`.mkv`, `.mp4`, `.avi`, ... — list in `MediaFiles.VideoExtensions`) go to the
  library. Subtitles, `.nfo`, images and *samples* (`sample` files or files in a `Sample` folder) stay only in the download.
- **Seasons:** if the series already has a folder for the season (`Season 1`, `Season 01`, or `Specials` for
  season 0), it's reused; otherwise Depot creates `Season 01`. When you pick an existing series, the dialog
  shows the seasons it already has and suggests the latest.
- **Nothing is overwritten.** If a file with the same name already exists at the destination, the job fails with the
  reason and can be retried (**Retry**) after moving the file.
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
| `MoviesPath` / `SeriesPath` | `~/containers/media/movies` / `.../series` | Library folders (`~/` = home of the user running Depot). They aren't created automatically. The dialog's series list comes from the folders inside `SeriesPath`. |
| `ImportMode` | `Auto` | `Auto` (hardlink, otherwise copy), `Hardlink` (fails instead of copying) or `Copy`. |
| `PollingIntervalSeconds` | `60` | How often Depot looks for completed downloads (10 to 3600). |
| `PathMappings` | `[]` | **Required for importing.** `{ "QbittorrentPath": "/downloads", "DepotPath": "~/containers/media/downloads" }`. These are the only folders Depot reads downloads from: a `save_path` outside them is never used, whatever path qBittorrent reports. With qBittorrent on the same machine, map the folder to itself (`/srv/downloads` → `/srv/downloads`). |

In Jellyfin, create two libraries: **Movies** (type Movies) pointing to the movies folder and **Shows**
(type Shows) pointing to the series folder. If Jellyfin runs in Docker, use the path *inside* the
container that corresponds to the same volume.

Endpoints: `GET /api/library/series` (`[{ "name": "The Last of Us", "seasons": [1, 2] }]`),
`GET|PUT /api/organization/settings`, `POST /api/organization/settings/test`,
`GET /api/organization/status`, `GET /api/organization/jobs[?status=]`, `GET /api/organization/jobs/{id}` and
`POST /api/organization/jobs/{id}/retry|skip`.

- **Symbolic links:** the downloaded file can't be a link, nor can any folder between the mapped folder and it;
  on the library side, no folder between the library root and the destination. The configured roots (library
  and mapping folders) can be links.

Tests: `dotnet test backend/Depot.slnx` (domain, use cases, file organizer, qBittorrent client
against a simulated server, and the endpoints with `WebApplicationFactory`). GitHub Actions
(`.github/workflows/ci.yml`) runs the tests and checks the syntax of the frontend scripts on every push and pull request.

### Recommended setup

**1. A single folder, mounted the same way in every service.** Hardlinks only work within the same mount
point: if qBittorrent mounts `/downloads` and Jellyfin mounts `/movies` as separate volumes, the
hardlink fails even on the same disk and everything becomes a copy (twice the space). Mount the whole parent folder:

```text
~/containers/media/            ← mounted as /data in qBittorrent and Jellyfin
├── downloads/                 ← qBittorrent saves here
│   ├── movies/                ← folder for the "movies" category in qBittorrent
│   └── series/                ← folder for the "series" category in qBittorrent
├── movies/                    ← Jellyfin Movies library (type Movies)
└── series/                    ← Jellyfin Shows library (type Shows)
```

With Depot running directly on the server, the mapping is `/data` → `~/containers/media`.

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
- **Library**: one job per video, with source, destination, state and reason. **Retry** tries a
  failed or skipped job again; **Skip** only marks the job: no file is changed or deleted. The number
  in the menu counts failed jobs.

### Upgrading from an earlier version

The `ChooseLibraryWhenAdding` migration removes file-name-based classification: old jobs that
hadn't been imported yet (in review, waiting or failed) are deleted; imported ones stay
as history. Downloads that were already in qBittorrent have no library choice, so they aren't
organized; to organize one of them, remove it (keeping the files) and add it again choosing
Movie or Series (qBittorrent only reuses what it already downloaded if the destination folder is the same).

When upgrading to the version with an access token:

- The interface is now opened at `http://localhost:5080` (served by the API), no longer as a file, and asks for the
  token from `data/access-token` once per browser.
- Without path mappings nothing is imported: jobs wait with the reason. If qBittorrent runs on the
  same machine, add the downloads folder mapped to itself in **Settings → Media library**.
- To access it from another machine, add its name to `AllowedHosts` (see [Access and security](#access-and-security)).

## License

[MIT](LICENSE).
