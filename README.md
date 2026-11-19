# Krakarr

**One app, one container** for your whole media automation stack. Krakarr replaces:

| Replaces | Krakarr module | Status (milestone 1) |
|---|---|---|
| **Radarr** – movie library & automation | `Krakarr.Movies` | ✅ TMDB lookup, library, quality profiles, auto/interactive search, RSS sync, rename + hardlink import |
| **Prowlarr** – indexer management | `Krakarr.Indexers` | ✅ Torznab/Newznab indexers, capability detection, aggregated search across indexers |
| **qBittorrent** – torrent client | `Krakarr.Downloads` | ✅ Embedded [MonoTorrent](https://github.com/alanmcgovern/monotorrent) engine: magnets, .torrent files, DHT, speed limits, seeding ratio/time limits, fast-resume — *or* drive an existing qBittorrent (tag-scoped) |
| **Sonarr** – TV series | `Krakarr.Series` | ✅ TheTVDB metadata, seasons/episodes, monitoring, season-pack aware search, RSS sync, rename + hardlink import |
| **Bazarr** – subtitles | `Krakarr.Subtitles` | 🚧 Provider interface; providers land in a later milestone |

Everything (API, web UI, torrent engine, schedulers) runs in a single .NET process inside a single Docker container.
There is nothing to wire together: no API keys between apps, no download-client configuration, no path mappings.

## Tech stack

- **Backend:** .NET 11, ASP.NET Core minimal APIs, EF Core 11 (SQLite by default, PostgreSQL optional), SignalR for live updates
- **Frontend:** Angular 22 (standalone, zoneless, signals) + Tailwind CSS 4
- **Packaging:** multi-stage `Dockerfile` → one image (`mcr.microsoft.com/dotnet/aspnet:11.0` base)

## Quick start (Docker)

```bash
docker compose up -d --build
```

Then open <http://localhost:8787>:

1. Create the admin account (first run only).
2. **Settings → Media management:** add a root folder, e.g. `/data/media/movies`.
3. **Settings → Metadata:** paste a [TMDB API key](https://www.themoviedb.org/settings/api) (v3 key or v4 read token).
4. **Indexers:** add one or more Torznab/Newznab feeds and press *Test*.
5. **Movies → Add movie:** search, add, and Krakarr searches, downloads and imports it for you.
6. **Series:** add a root folder of type *Series* (e.g. `/data/media/tv`) and a [TheTVDB API key](https://thetvdb.com/api-information)
   in *Settings → Metadata*, then **Series → Add series**.

### Volumes & ports

| Path / port | Purpose |
|---|---|
| `/config` | Database (`krakarr.db`), settings, torrent fast-resume cache, auth keys |
| `/data` | **Both** downloads (`/data/downloads`) and your library (e.g. `/data/media/movies`) |
| `8787/tcp` | Web UI + REST API |
| `6881/tcp+udp` | BitTorrent peers / DHT (change in *Settings → Download client*) |

> **Why one `/data` mount?** Krakarr keeps seeding the original download and **hardlinks** it into your library,
> so a movie takes disk space only once. Hardlinks only work within a single filesystem *and* a single mount,
> so put downloads and media under one host directory and mount it once.

### TV series

- **Metadata** comes from TheTVDB v4 (series, seasons, episodes in the default order, translations in your language).
  Series are refreshed every 12 hours; new episodes inherit their season's monitoring.
- **Monitoring** on add: all, future, missing, first season, latest season or none. Specials are never monitored automatically.
  Seasons and episodes can be toggled individually.
- **Searching:** *Search missing* searches each season with wanted episodes. It prefers season packs, never grabs
  overlapping releases, and then searches individually for episodes that are still missing. Seasons and episodes also
  have automatic and interactive searches. Quality/size limits apply per episode, and every covered episode must
  benefit from an upgrade.
- **Import:** each video file is matched to its episode(s) (multi-episode files included). It is renamed with
  *Settings → Media management* formats (default `{Series Title} - S{Season}E{Episode} - {Episode Title} [{Quality}]`
  in `Season {Season}` folders) and hardlinked. Samples and extras are skipped.
- Not yet supported: daily shows (date-based numbering) and anime absolute numbering.

### Using an existing qBittorrent instead of the built-in client

*Settings → Download client → qBittorrent* switches Krakarr from its built-in engine to an external qBittorrent
(WebUI API, qBittorrent 4.x and 5.x). Switching takes effect immediately; no restart is needed.

- **Tag-scoped:** every torrent Krakarr adds gets the configured tag (default `krakarr`). Krakarr only lists,
  pauses, resumes, deletes and imports torrents that carry that tag. The rest of your qBittorrent is left alone.
  Tagging a torrent by hand in qBittorrent makes it show up in Krakarr as a manual download.
- **Category / save path:** optional overrides. By default each grab uses Krakarr's category (`movies`, `manual`, …).
- **Remote path mapping:** if qBittorrent runs in another container and sees downloads as e.g. `/downloads`
  while Krakarr sees `/data/downloads`, set *Remote path* = `/downloads` and *Local path* = `/data/downloads`.
  For hardlinks, both containers should mount the same host directory.
- **Seeding** is controlled by qBittorrent's own share limits. The built-in seed ratio/time options don't apply.
- Use **Test** to check the URL, credentials and tag before saving. The password is never sent back to the browser.

Each client keeps its own list of torrents, so switching back and forth never mixes them up.

### Environment variables

| Variable | Default | Description |
|---|---|---|
| `PUID` / `PGID` | `1000` | User/group the app runs as (files are created with this owner) |
| `UMASK` | `002` | File creation mask |
| `KRAKARR__URLBASE` | *(empty)* | Serve under a sub-path, e.g. `/krakarr`, behind a reverse proxy |
| `KRAKARR__PORT` | `8787` | HTTP port (outside Docker; in Docker use `ASPNETCORE_HTTP_PORTS`) |
| `Database__Provider` | `sqlite` | `sqlite` or `postgres` |
| `Database__ConnectionString` | `/config/krakarr.db` | e.g. `Host=postgres;Database=krakarr;Username=krakarr;Password=…` |

Any setting can also be placed in `/config/config.json` (environment variables win).

## Architecture

A **modular monolith**. Each replaced app is a project with its own services, endpoints and background tasks.
They communicate in-process through interfaces, never over HTTP.

```
src/
  Krakarr.Host/          ASP.NET Core host: auth (cookie + X-Api-Key), SignalR hub, settings/system APIs, serves the SPA
  Krakarr.Core/          Domain entities, quality catalogue, event hub abstraction, command queue + scheduler
  Krakarr.Data/          KrakarrDbContext, settings store, seeding
  Krakarr.Data.Sqlite/   SQLite migrations
  Krakarr.Data.Postgres/ PostgreSQL migrations
  Krakarr.Media/         Release-name parser, naming templates, hardlink/copy file transfer
  Krakarr.Indexers/      Torznab/Newznab client + aggregated search          (Prowlarr)
  Krakarr.Downloads/     MonoTorrent engine wrapper, grab-from-indexer        (qBittorrent)
  Krakarr.Movies/        TMDB, decision engine, search/grab, import, RSS sync (Radarr)
  Krakarr.Series/        TVDB, episode decisions, search/grab, import, RSS    (Sonarr)
  Krakarr.Subtitles/     Subtitle provider abstraction                        (Bazarr, WIP)
web/                     Angular 22 + Tailwind 4 frontend
tests/                   xUnit v3 unit + HTTP integration tests
```

**Movie pipeline:** *add (TMDB)* → *search all indexers (Torznab, by IMDb id when supported)* → *decision engine*
(title/year/id match, quality profile, size limits, seeders, upgrade/cutoff) → *grab* (.torrent or magnet into the
embedded engine) → *download completes* → *import* (pick main video, rename by template, hardlink into the library)
→ *seed until the ratio/time limit*.

## Development

Prerequisites: .NET 11 SDK, Node.js 22+.

```bash
# Run everything at http://localhost:8787 (data in src/Krakarr.Host/data).
# The first build runs `npm ci` and builds the Angular UI into src/Krakarr.Host/wwwroot;
# later builds only rebuild the UI when files under web/ change.
dotnet run --project src/Krakarr.Host
```

```bash
# Frontend hot reload: run the backend without the UI build, plus the Angular dev server
# (http://localhost:4200, proxies /api and /hubs to :8787)
dotnet run --project src/Krakarr.Host -p:SkipWebBuild=true
cd web && npm ci && npm start
```

```bash
# Tests
dotnet test --solution Krakarr.slnx -p:SkipWebBuild=true
cd web && npx ng test --watch=false
```

```bash
# New EF Core migration (run once per provider)
dotnet tool restore
dotnet ef migrations add <Name> --project src/Krakarr.Data.Sqlite --startup-project src/Krakarr.Data.Sqlite --output-dir Migrations
dotnet ef migrations add <Name> --project src/Krakarr.Data.Postgres --startup-project src/Krakarr.Data.Postgres --output-dir Migrations
```

API documentation (OpenAPI) is served at `/api/openapi/v1.json`.

### Notes

- `Npgsql.EntityFrameworkCore.PostgreSQL` 11 is still a release candidate and pins EF Core RC; Krakarr lifts it to
  EF Core 11 GA (warning `NU1608` is suppressed). Bump it once Npgsql 11 is released.

## Roadmap

- **Series (Sonarr):** daily and anime (absolute) numbering, calendar / wanted views, importing existing libraries
- **Subtitles (Bazarr):** OpenSubtitles & co, language profiles, automatic download after import
- **Indexers (Prowlarr):** native tracker definitions (Cardigann-style), per-indexer stats, Torznab proxy for other apps
- **Downloads:** per-torrent limits, file selection, Usenet (NZB) support
- Import of existing libraries, lists (Trakt/IMDb), notifications, multi-user
