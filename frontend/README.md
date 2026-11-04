# MediaFlow frontend

Interface for Depot, a self-hosted download manager for qBittorrent. It's plain HTML, CSS and JavaScript, with no dependencies and no build step. Every piece of data it shows comes from the Depot API; nothing is simulated.

## Run it

1. Start the API: `dotnet run --project backend/Depot.Api` from the repository root (inside `nix develop`). It listens on `http://localhost:5080` and serves this folder.
2. Open `http://localhost:5080` in a browser. Opening `index.html` as a file doesn't work: the page calls the API on its own origin.
3. Enter the access token from `data/access-token` when asked. The browser keeps it.
4. The first time, set the qBittorrent URL, username and password in **Settings**.

Theme and interface preferences persist in `localStorage`. If the API isn't running, an alert above every page says so and offers a Retry button.

## Pages

| Page | Shows |
| --- | --- |
| Overview | Active and completed counts, total speeds, active downloads and the qBittorrent connection |
| Downloads | Every torrent, with search, status filters, sorting and the pause, resume, retry and remove actions |
| Settings | The qBittorrent connection (saved on the server), theme and interface preferences |

Pages refresh every 2 seconds by default (1, 2 or 5 in Settings).

## Keyboard

| Key | Action |
| --- | --- |
| `Ctrl K` / `⌘K` | Search downloads and pages |
| `/` | Focus the page's search field |
| `A` | Add download |

Single-key shortcuts can be turned off in Settings.

## Structure

```
frontend/
├── index.html              App shell and inline SVG icon sprite
├── assets/icons/           Favicon
├── css/
│   ├── style.css           Entry point, imports the three files below
│   ├── main.css            Tokens, themes, reset
│   ├── layout.css          Sidebar, top bar, page regions, breakpoints
│   └── components.css      Controls, tables, dialogs, toasts
└── js/
    ├── boot.js             Creates the MediaFlow namespace, applies the saved theme
    ├── app.js              Page mounting, polling, status, shortcuts
    ├── navigation.js       Hash routing and mobile menu
    ├── api.js              All data access: every call goes to the Depot API
    ├── prefs.js            Interface preferences
    ├── format.js           Bytes, speeds, ETAs
    ├── dom.js              Element helper and keyed list rendering
    ├── components/         Tables, dialogs, palette, toasts, form fields, states, service status
    └── pages/              One module per page
```

The files are plain scripts, not ES modules, because browsers block modules on `file://`. Each one wraps its code in a function and shares what it exports through the single `MediaFlow` global, so **the script order in `index.html` matters**: a file can only use what the files above it export.

## Talking to the API

Pages only talk to `js/api.js`, which calls the API on the page's own origin, so the interface works from whatever address it was opened at (localhost or the server's name on the network).

- Every call sends the access token as `Authorization: Bearer`. A `401` dispatches `unauthorized` on `apiEvents`, and `js/components/sign-in-dialog.js` asks for the token, checks it against the API (`signIn`) and keeps it in `localStorage`.
- Errors come back as `ApiError`, with a message ready to show, the input it's about (`field`) and the backend's error code (`code`).
- Writes dispatch `change` on `apiEvents`, which refreshes the visible page.
- Reads made at the same moment from several places share one request (`shared()`), so each refresh asks for each endpoint once.

The browser never calls qBittorrent directly, and secrets are never sent to it. The settings API reports only whether a password is stored (`hasPassword`), and the password field is always sent blank unless the user is replacing the value.

## Design notes

- **Colour means failure.** Interface colour is limited to grays and one red, used only for errors and irreversible actions. Live states use full-contrast foreground instead of an accent.
- **The row rule is the progress bar.** On download rows, the separator line doubles as the progress bar, so progress can be compared across rows without adding a column of bars.
- **Fonts.** The stack prefers Inter when it's installed and otherwise uses the system sans-serif, so nothing is fetched from a third party. To guarantee Inter, add the font files under `assets/` with an `@font-face` rule.
