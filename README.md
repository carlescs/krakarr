<p align="center"><img src="assets/branding/icon.svg" alt="Krakarr" width="128" /></p>

# Krakarr

**One app, one container** for your whole media automation stack. Krakarr replaces:

| Replaces | Krakarr module | Status (milestone 1) |
|---|---|---|
| **Radarr** – movie library & automation | `Krakarr.Movies` | ✅ TMDB lookup, library, quality profiles, auto/interactive search, RSS sync, rename + hardlink import |
| **Prowlarr** – indexer management | `Krakarr.Indexers` | ✅ Torznab/Newznab indexers, capability detection, aggregated search across indexers |
| **qBittorrent** – torrent client | `Krakarr.Downloads` | ✅ Embedded [MonoTorrent](https://github.com/alanmcgovern/monotorrent) engine: magnets, .torrent files, DHT, speed limits, seeding ratio/time limits, fast-resume — *or* drive an existing qBittorrent (tag-scoped) |
| **Sonarr** – TV series | `Krakarr.Series` | ✅ TMDB or TheTVDB metadata, seasons/episodes, monitoring, season-pack aware search, RSS sync, rename + hardlink import |
| **Bazarr** – subtitles | `Krakarr.Subtitles` | ✅ OpenSubtitles.com, wanted languages, hash + release scoring, automatic and manual search |
| **Seerr** – discovery | `Krakarr.Movies` / `Krakarr.Series` | ✅ Trending, popular, upcoming and top-rated lists, personal recommendations, recommended and similar titles, one-click add |

Everything (API, web UI, torrent engine, schedulers) runs in a single .NET process inside a single Docker container.
There is nothing to wire together: no API keys between apps, no download-client configuration, no path mappings.

## Tech stack

- **Backend:** .NET 11, ASP.NET Core minimal APIs, EF Core 11 (SQLite by default, PostgreSQL optional), SignalR for live updates
- **Frontend:** Angular 22 (standalone, zoneless, signals) + Tailwind CSS 4
- **Packaging:** multi-stage `Dockerfile` → one image (`mcr.microsoft.com/dotnet/aspnet:11.0` base)

## Quick start (Docker)

Images are published to the GitHub Container Registry for `linux/amd64` and `linux/arm64`:

```bash
docker run -d --name krakarr -p 8787:8787 -p 6881:6881 -p 6881:6881/udp \
  -v ./config:/config -v /path/to/data:/data ghcr.io/carlescs/krakarr:latest
```

Or edit `docker-compose.yml` (volumes) and run `docker compose up -d`. Image tags:

| Tag | Meaning |
|---|---|
| `latest` | Newest stable release |
| `X.Y.Z`, `X.Y`, `X` | A specific release (`X` only from 1.0 on) |
| `X.Y.Z-rc.N` | Prereleases |
| `edge`, `sha-<commit>` | Every push to `main` |

Then open <http://localhost:8787>:

1. Create the admin account (first run only). You need the one-time **setup token** that Krakarr prints in its log at
   startup (`docker logs krakarr`) and saves as `/config/setup-token`; it is deleted once the account exists. This stops
   anyone else who can reach the instance from claiming it first.
2. **Settings → Media management:** add a root folder, e.g. `/data/media/movies`.
3. **Settings → Metadata:** paste a [TMDB API key](https://www.themoviedb.org/settings/api) (v3 key or v4 read token).
4. **Indexers:** add one or more Torznab/Newznab feeds and press *Test*.
5. **Movies → Add movie:** search, add, and Krakarr searches, downloads and imports it for you.
6. **Already have a library?** *Movies → Import library* / *Series → Import library* scans a root folder and matches each
   sub-folder by its name (see [Library import](#library-import)).
7. **Series:** add a root folder of type *Series* (e.g. `/data/media/tv`), then **Series → Add series**. Series use the
   same TMDB key by default; pick TheTVDB as *Series metadata source* in *Settings → Metadata* to use it instead.

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

- **Metadata** comes from the *Series metadata source* in *Settings → Metadata*: **TMDB** (default, same key as movies) or
  **TheTVDB** (v4 API key, optional subscriber PIN). Series, seasons and episodes follow that source's numbering, in your
  metadata language. Each series keeps both its TMDB and TheTVDB id when known; the TheTVDB id drives indexer searches
  and release matching.
- **Switching sources** takes effect on each series' next refresh (or *Refresh* on the series page). The other source's
  id is resolved automatically, and episodes keep their files: they are matched by id, else by season and episode number.
  Shows that the two sources number differently (often anime) may end up with different episode numbers.
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

### Discover

*Discover* browses TMDB like Seerr: trending, popular, upcoming and top-rated movies and series, plus in theaters and
airing today (*See all* opens the full, paged list). It needs the TMDB key from *Settings → Metadata*, even when
TheTVDB is the series metadata source.

- **Recommended for you** blends TMDB's recommendations for your 12 most recently added movies and shows. A title that
  several of them point at ranks higher, and titles you already have are left out.
- **Movie and series pages** show TMDB's *Recommendations* and *Similar* titles.
- Titles already in the library carry a ✓ and open their page. Any other title has an *Add* button that asks for a root
  folder and quality profile, as *Add movie* / *Add series* do.

### Calendar

*Calendar* shows upcoming and recent releases of everything in the library, in a **month**, **week** or **agenda** view
(phones start on the agenda).

- **Movies** appear on their **cinema**, **digital** and **physical** release dates, from TMDB. Set a *Release region*
  (two-letter country code) in *Settings → Metadata* to see that country's dates; a release type the country has no date
  for falls back to the earliest date worldwide, and both are shown on the movie page. With no region every date is the
  earliest worldwide. Dates are fetched when a movie is added and on the daily metadata refresh.
- **Episodes** appear on their air date; several episodes of a show on the same day are grouped (`S01E01–E08`).
- **Filters:** series, movies, each movie release type, unmonitored items and download status (any / not downloaded /
  downloaded). The view and filters are remembered per browser.
- **iCal feed:** *iCal* gives a URL (`/api/v1/calendar/feed.ics?apikey=…`) for Google Calendar, Apple Calendar or Outlook,
  with its own filters and day range. It carries the API key, so treat it like a password.

**Minimum availability:** each movie has a *minimum availability* (*announced*, *in cinemas* or *released*; new movies
default to *released*). Automatic searches, search on add and RSS grabs wait until the movie reaches it, judged by the
earliest worldwide dates: *in cinemas* from the cinema date, *released* from the digital or physical date (or 90 days
after the cinema date when neither is known). Interactive search always works. Movies added before this feature use
*announced*, so their behaviour does not change; change it with *Edit* on the movie page.

### Library import

*Movies → Import library* and *Series → Import library* adopt folders that already exist in a root folder, **in place**.
Nothing is moved or renamed.

- **Matching** uses the folder name. `Title (Year)`, `Title [Year]` and release-style names
  (`The.Matrix.1999.1080p.BluRay-GRP`) are understood, and id tags win when present: `{tmdb-603}`, `[tmdbid-603]`,
  `{imdb-tt0133093}`, `{tvdb-81189}`, `[tvdbid-81189]`. For series, `{tmdb-…}` is a TMDB TV id; both tags work with
  either metadata source.
- **Confidence:**

  | Level | Meaning |
  |---|---|
  | Exact | Id tag in the folder name |
  | High | Title + year match |
  | Medium | Title match, year missing or off by one |
  | Low | Top search result only |
  | None | No result |

  Exact, High and Medium are pre-selected. Any row's match can be changed with a search.
- **Movies:** the largest video file in the folder becomes the movie file (samples skipped). Its quality is read from the file name.
- **Series:** every video file is mapped to episodes using `S01E02`/`1x02`, or an episode number inside a `Season N`/`Specials` folder.
  Multi-episode files are supported and duplicates keep the best quality. Files that can't be mapped are listed in the
  task result (*Activity*). Season folders are detected automatically.
- Optionally search right away for missing movies/episodes. Imported files are picked up by the subtitle search,
  and existing `video.en.srt` files are recognised.

### Subtitles

- **Settings → Subtitles:** turn on automatic downloads, pick the wanted languages and paste an
  [OpenSubtitles.com](https://www.opensubtitles.com/consumers) API key. An optional account raises the daily download quota.
- New movie and episode files are searched within a minute of import. Files still missing a language are retried every
  *N* hours (default 24), and changing the wanted languages re-checks everything.
- **Scoring (0-100):** a file-hash match is 100. Otherwise title/series, year or season+episode, source, resolution,
  release group and codec of the subtitle's release are compared with your file's original release name.
  Automatic downloads need the minimum score (70 movies / 80 episodes), respect the hearing-impaired preference and skip
  AI-translated subtitles unless allowed.
- Subtitles are saved next to the video as `{video}.{lang}[.hi][.forced].srt`. Existing files named like that are detected.
  When a video is upgraded, its old subtitles are removed and the new file is searched again.
- The **Subtitles** page lists files missing languages. Every movie and episode has a subtitle dialog with
  automatic search, manual search (all candidates with score and reasons) and delete.

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
- **Seeding** follows Krakarr's seeding settings (ratio/time limit, then remove or stop). With both limits at 0, qBittorrent's own share limits apply.
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
| `AllowedHosts` | `*` | Host names Krakarr answers to, separated by `;` (e.g. `krakarr.example.com;192.168.1.10`). Set it to block DNS rebinding attacks |
| `Krakarr__SetupToken` | *(generated)* | Preset the first-run setup token instead of using a random one (useful for automated deployments) |
| `Krakarr__Auth__AttemptsPerMinutePerIp` | `10` | Requests per minute and client IP to the login, setup and password endpoints |
| `Krakarr__Auth__FailuresBeforeLockout` | `5` | Failed logins for one username before it is locked temporarily (30 s, doubling up to 15 min) |

Any setting can also be placed in `/config/config.json` (environment variables win).

### Security notes

- **Root folders are the boundary.** Movie and series folders must be directly inside a configured root folder, and
  *Delete with files* only ever removes folders inside a root folder.
- **Brute-force protection.** Credential endpoints are rate limited per IP, repeated failures lock the username
  temporarily, and failed logins are logged as warnings (with the client IP) for tools like fail2ban.
- **Expose it carefully.** Krakarr serves plain HTTP. Put a TLS reverse proxy in front before exposing it to the
  internet, and set `AllowedHosts`.

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
  Krakarr.Series/        TMDB/TVDB, episode decisions, search/grab, import, RSS    (Sonarr)
  Krakarr.Subtitles/     OpenSubtitles provider, scoring, search task, files  (Bazarr)
web/                     Angular 22 + Tailwind 4 frontend
tests/                   xUnit v3 unit + HTTP integration tests
```

**Movie pipeline:** *add (TMDB)* → *search all indexers (Torznab, by IMDb id when supported)* → *decision engine*
(title/year/id match, quality profile, size limits, seeders, upgrade/cutoff) → *grab* (.torrent or magnet into the
embedded engine) → *download completes* → *import* (pick main video, rename by template, hardlink into the library)
→ *seed until the ratio/time limit*, then remove the torrent from the client (or stop it).

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

### Commits, versions and releases

- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org): `feat(series): add calendar`,
  `fix: crash on empty folder`, `feat!: …` or a `BREAKING CHANGE:` footer for breaking changes.
  CI checks every pushed commit. Enable the same check locally with `git config core.hooksPath .githooks`.
- Versions are [SemVer](https://semver.org) git tags (`vX.Y.Z`). They are computed by `scripts/release/next-version.mjs`:

  | Commit | Bump |
  |---|---|
  | `feat` | minor |
  | `fix` / `perf` | patch |
  | breaking | major (minor while the version is below 1.0) |
  | `docs`, `chore`, `ci`, `test`, `refactor`, … | none |
- **CI** (`.github/workflows/ci.yml`) runs commit linting, backend and frontend tests on pull requests and on every push to `main`.
- **Release** (`.github/workflows/release.yml`), on every push to `main`:
  1. Runs CI.
  2. Publishes `ghcr.io/carlescs/krakarr:edge`.
  3. If the new commits warrant a version, it also creates the `vX.Y.Z` tag, a GitHub release with grouped notes, and the
     `X.Y.Z`/`X.Y`/`latest` image tags.
- **Manual release:** *Actions → Release → Run workflow*:
  - `bump`: `auto` (from commits, at least a patch), `patch`, `minor` or `major`.
  - `version`: an explicit version, e.g. `1.0.0`.
  - `prerelease`: an identifier such as `rc`, which produces `X.Y.Z-rc.N`.
- Local builds report version `0.0.0-dev`. CI passes the real one with `-p:Version=` and `--build-arg VERSION=`.

### Notes

- `Npgsql.EntityFrameworkCore.PostgreSQL` 11 is still a release candidate and pins EF Core RC; Krakarr lifts it to
  EF Core 11 GA (warning `NU1608` is suppressed). Bump it once Npgsql 11 is released.

## Roadmap

- **Series (Sonarr):** daily and anime (absolute) numbering, wanted view
- **Subtitles (Bazarr):** more providers (Podnapisi, Addic7ed, …), embedded subtitle detection, per-series language profiles, sync/encoding fixes
- **Indexers (Prowlarr):** native tracker definitions (Cardigann-style), per-indexer stats, Torznab proxy for other apps
- **Downloads:** per-torrent limits, file selection, Usenet (NZB) support
- Lists (Trakt/IMDb), notifications, multi-user, optional renaming of imported libraries
