# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

From-scratch rewrite of Depot. The previous version lives in `~/Documents/Develop/Depot.backup` (read-only
reference: its `README.md` documents every endpoint, error code and edge case it handled). When behaviour is
unclear, make it work like the backup did.

Agreed scope: magnet/.torrent add with a Movie/Series/Other choice, automatic import, Imports page, settings
for qBittorrent, library folders and Jellyfin, and a live view of the Jellyfin library. Docker + CI come after
that. Deliberately left out of the rewrite (ask before adding): automatic series extras/specials
classification with review (`NeedsReview`), a local SQLite copy of the Jellyfin library, the image disk cache,
backup/restore, rate limiting, SSRF IP-range filtering, the command palette.

## What Depot does

A self-hosted, **manual** media organizer sitting between qBittorrent and Jellyfin:

1. The user adds a magnet link or `.torrent` file. Depot asks what it is:
   - **Movie** → asks the movie's name; when the download finishes the videos go to `movies/<name>/`.
   - **Series** → asks the series (existing folder in `series/` or a new name) and the season; videos go to
     `series/<series>/Season NN/`.
   - **Other** → nothing; the download stays where qBittorrent saved it.
2. The choice is stored per torrent hash. A background worker polls qBittorrent for completed torrents and
   imports their videos (and matching subtitles).
3. Depot shows the Jellyfin library (movies and series), read live through the Jellyfin API.

Depot never guesses titles from file names, never renames files, and never touches the downloaded files:
imports are hardlinks (copy as fallback) so the torrent keeps seeding. Nothing at the destination is overwritten.

## Stack and architecture

- .NET 10 / ASP.NET Core minimal APIs, EF Core with SQLite, xUnit. Shared MSBuild settings (target framework,
  nullable, warnings as errors) are in `backend/Directory.Build.props`.
- Clean Architecture under `backend/`, dependencies pointing inward:
  - `Depot.Domain` — entities and rules, no framework references.
  - `Depot.Application` — use cases and ports (`IQBittorrentClient`, `IJellyfinClient`, repositories, file system).
    Interfaces only for external things and repositories, not for every service.
  - `Depot.Infrastructure` — EF Core/SQLite, qBittorrent and Jellyfin HTTP clients, file import, background workers.
  - `Depot.Api` — endpoints and DI composition only.
  - `Depot.Tests` — single test project (domain, use cases, clients against fake HTTP servers, endpoints via
    `WebApplicationFactory` in `TestSupport/ApiFactory.cs`, which uses a temp data folder).
- EF maps the domain entities directly with Fluent API configurations in Infrastructure (no separate record
  classes). Migrations are applied automatically at startup.
- Local data (`depot.db`, Data Protection keys, access token) goes in `data/` (`Storage__DataDirectory`).
- Frontend (`frontend/`): plain HTML/CSS/JS, no build step, served by the API itself (same origin, no CORS).
  Native ES modules: `js/main.js` is the shell (hash routing, page list, 2 s refresh of the visible page);
  each `js/pages/*.js` default-exports `{ title, mount(view, { params, actions }) → { refresh?, unmount? } }`.
  All HTTP goes through `js/api.js`; writes dispatch `change` on `apiEvents`, which refreshes the page.

## Commands

Dev environment comes from the nix flake (`nix develop` or `direnv allow`): .NET 10 SDK, `dotnet-ef`, Node
(only for syntax checks), `sqlite`, `curl`, `jq`.

```sh
dotnet build backend/Depot.slnx
dotnet test backend/Depot.slnx
dotnet test backend/Depot.slnx --filter "FullyQualifiedName~LibraryTargetTests"   # single class/test
dotnet run --project backend/Depot.Api                                            # http://localhost:5080

dotnet ef migrations add <Name> \
  --project backend/Depot.Infrastructure \
  --startup-project backend/Depot.Api \
  --output-dir Persistence/Migrations

find frontend -name '*.js' -exec node --check {} \;                               # frontend syntax check
```

Docker: `Dockerfile` (Debian runtime, `/config` for Depot's data, `/data` for media, `Library__*` env defaults,
`HEALTHCHECK` via `dotnet Depot.Api.dll --healthcheck`) and `docker-compose.yaml`, which reads `PUID`, `PGID`,
`DEPOT_CONFIG_DIR` and `MEDIA_DIR` from an uncommitted `.env` (documented in the compose file's header). CI
(`.github/workflows/ci.yml`) builds and tests in Release, syntax-checks the frontend and publishes
`ghcr.io/<owner>/depot` on pushes to `main`.

If the user's own instance is running on :5080, don't build into `backend/*/bin` (overwriting its assemblies
crashes it): use `--artifacts-path <scratchpad>` and run test instances on another port.

## Integration details that are easy to get wrong

**qBittorrent (Web API, support 4.x and 5.x)**
- 5.2 answers a successful login with `204` and an empty body (4.x: `200 Ok.`); only the cookie matters.
- Session cookie is `SID` or `QBT_SID_<port>`; 5.x renamed `pause`/`resume` to `stop`/`start`; the add endpoint
  answers as text or JSON depending on version. Map states of both versions (`stoppedDL` → paused,
  `stalledUP` → completed...).
- It answers 200 for unknown hashes: validate hashes (40 or 64 hex) and check the torrent exists.
- The library choice is saved by info hash *before* the torrent is sent. Magnet btih may be base32; `.torrent`
  hashes come from the exact bytes of the bencoded `info` dict; with libtorrent 2 a hybrid torrent's id is
  its v2 hash cut to 40 chars.
- Keep one shared login session; on expiry re-login and retry once. Back off after rejected logins
  (qBittorrent bans IPs after repeated failures).
- The `save_path` qBittorrent reports may be a path inside its container: translate it through configured path
  mappings (qBittorrent path → Depot path) and refuse paths outside them.

**Jellyfin**
- Read live through `/Items` with the API key and no user. In 10.11 `ParentId=<series>` returns nothing and
  `SeriesIds` is ignored, so seasons are listed by type and filtered by their `SeriesId`, then episodes are read with
  `ParentId=<season>` (see `Infrastructure/Jellyfin/JellyfinClient.cs`). `/Items/{id}` needs a user; use `/Items?Ids=`.
- `System/Info/Public` answers in camelCase and `System/Info` in PascalCase: read JSON with the case-insensitive
  helpers in `JsonReading.cs`.
- Auth header: `Authorization: MediaBrowser Token="<apiKey>"`; the key never goes in URLs, responses or logs.
- The browser never calls qBittorrent or Jellyfin directly; images are proxied through Depot's API.
- Disable redirects on both HTTP clients (they could carry credentials elsewhere).
- Secrets (qBittorrent password, Jellyfin key) are encrypted with ASP.NET Core Data Protection; settings
  endpoints return only `hasPassword`/`hasApiKey`, and a blank value on save keeps the stored one only for the same URL.

**File import**
- Hardlinks only work within one filesystem/mount: downloads and libraries should share a parent mount
  (e.g. `/data/downloads`, `/data/movies`, `/data/series`).
- Hardlinks use `link(2)` via P/Invoke; the Docker runtime image must be Debian-based (glibc), not Alpine.
- Only video extensions and subtitles named after their video are imported; samples and `.nfo`/images stay.
- Reuse an existing `Season 1`/`Season 01` (or `Specials` for season 0) folder, otherwise create `Season 01`
  (`Season 00`). These rules are pure domain code in `Depot.Domain/Library/ImportPlanner.cs` and `MediaFiles.cs`.

## Testing against real services

Real qBittorrent (`qbittorrent-nox`), Jellyfin and headless Chromium come from `nix build nixpkgs#<pkg>`; run
their binaries with `env -u LD_LIBRARY_PATH` (direnv's alsa-lib path breaks them). Point test instances at a
scratch `Storage__DataDirectory` and `HOME`, never at `data/`.

## Conventions

- All code, comments, logs and UI strings in English; replies to the user in Portuguese (pt-BR).
- Every feature ships end to end: backend + `frontend/js/api.js` + the page that uses it, plus tests in
  `Depot.Tests` when it has real rules (classification, file safety, persistence).
- One commit per finished feature, with the user's global git identity and no `Co-Authored-By` trailer.
- No README, `.env.example` or other doc/sample files; project knowledge goes in this file.
- The UI shows only real API data; no mock or placeholder data for features without a backend.
- Enum values in query strings and routes arrive in lowercase; parse them with `Api/Endpoints/QueryValues.cs`
  (minimal API binding is case-sensitive and answers 400). JSON bodies already accept camelCase enums.
- The C# VS Code extension on Linux creates a folder literally named `bin\Debug`; keep it in `.gitignore`.
