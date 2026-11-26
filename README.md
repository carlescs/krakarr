<p align="center"><img src="assets/branding/icon.svg" alt="Krakarr" width="128" /></p>

# Krakarr

**One app, one container** for your whole media automation stack. Krakarr replaces:

| Replaces | Krakarr module | Status (milestone 1) |
|---|---|---|
| **Radarr** – movie library & automation | `Krakarr.Movies` | ✅ TMDB lookup, library, quality profiles, auto/interactive search, RSS sync, rename + hardlink import |
| **Prowlarr** – indexer management | `Krakarr.Indexers` | ✅ Torznab/Newznab indexers, import from an existing Prowlarr, capability detection, aggregated search across indexers |
| **qBittorrent** – torrent client | `Krakarr.Downloads` | ✅ Embedded [MonoTorrent](https://github.com/alanmcgovern/monotorrent) engine: magnets, .torrent files, DHT, speed limits, seeding ratio/time limits, fast-resume — *or* drive an existing qBittorrent (tag-scoped) |
| **Sonarr** – TV series | `Krakarr.Series` | ✅ TMDB or TheTVDB metadata, seasons/episodes, monitoring, season-pack aware search, RSS sync, rename + hardlink import |
| **Bazarr** – subtitles | `Krakarr.Subtitles` | ✅ OpenSubtitles.com, wanted languages, hash + release scoring, automatic and manual search, upgrades, sync |
| **Seerr** – discovery | `Krakarr.Movies` / `Krakarr.Series` | ✅ Trending, popular, upcoming and top-rated lists, personal recommendations, recommended and similar titles, detail pages for any title, cast and person pages, one-click add, requests with approval |

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
4. **Indexers:** add one or more Torznab/Newznab feeds and press *Test*, or use *Import from Prowlarr* to add the
   torrent indexers of an existing Prowlarr (they are searched through Prowlarr's Torznab feeds).
5. **Movies → Add movie:** search, add, and Krakarr searches, downloads and imports it for you.
6. **Already have a library?** *Movies → Import library* / *Series → Import library* scans a root folder and matches each
   sub-folder by its name (see [Library import](#library-import)).
7. **Series:** add a root folder of type *Series* (e.g. `/data/media/tv`), then **Series → Add series**. Series use the
   same TMDB key by default; pick TheTVDB as *Series metadata source* in *Settings → Metadata* to use it instead.

### Volumes & ports

| Path / port | Purpose |
|---|---|
| `/config` | Database (`krakarr.db`), settings, torrent fast-resume cache, auth keys, backups (`/config/Backups`) |
| `/data` | **Both** downloads (`/data/downloads`) and your library (e.g. `/data/media/movies`) |
| `8787/tcp` | Web UI + REST API |
| `6881/tcp+udp` | BitTorrent peers / DHT (change in *Settings → Download client*) |

> **Why one `/data` mount?** Krakarr keeps seeding the original download and **hardlinks** it into your library,
> so a movie takes disk space only once. Hardlinks only work within a single filesystem *and* a single mount,
> so put downloads and media under one host directory and mount it once.

### Release languages

- **Wanted languages** (*Settings → Quality profiles → Release languages*) is an ordered list of audio languages. Releases
  in none of them are rejected, and among acceptable releases an earlier language wins over a better quality. Empty
  (the default) accepts any language.
- **Per title:** each movie and series uses that list unless you set its own languages in its *Edit* dialog.
- **Detection:** language tags in the release name (`FRENCH`, `TRUEFRENCH`, `VFF`, `SPANISH`, `CASTELLANO`, `LATINO`,
  `GERMAN`, `iTA`, `PT-BR`, …) and the indexer's `language` attribute. Subtitle tags (`ENG SUBS`, `VOSTFR`) are ignored.
  `MULTi`/`DUAL` releases also count as English. Untagged releases are assumed to be in the indexer's **default
  language** (set per indexer), or English when it has none.

### Alternate titles and scene mapping

- **Alternate titles:** on refresh Krakarr stores each movie's and series' original title, translated titles and AKAs
  (TMDB alternative titles and translations, TheTVDB aliases), keeping those in your wanted languages, your indexers'
  default languages, English and the original language. Releases named by any of them are accepted.
- **Your own titles:** add more in the *Edit* dialog, optionally with a language. Refreshes never change them.
- **Searches:** an indexer that cannot search by IMDb/TMDB/TheTVDB id is also searched (at most two extra queries) with
  the alternate titles in its default language and the ones without a language.
- **TheXEM** (*Settings → Metadata*, off by default): reads scene names and scene season/episode numbering from
  thexem.info when series refresh. Releases and downloaded files are matched by scene numbers first, and searches use
  them. Numbering applies only with TheTVDB as the series source, since TheXEM maps TheTVDB's episode order.

### Media info

- Krakarr reads the **headers** of each video file (Matroska/WebM and MP4/MOV, no ffmpeg needed) for its video codec,
  resolution, bit depth and HDR format (HDR10, HDR10+, HLG, Dolby Vision), its audio tracks (codec, channels, language)
  and its duration. For HEVC and AVC it also reads the parameter sets (bit depth and transfer characteristics the
  container may not repeat) and the start of the first frame (at most 64 KB), where HDR10+ metadata lives. Other
  containers (AVI, TS…) are read with `ffprobe`, which the Docker image ships (outside Docker, when it is on `PATH`).
  Files are read on import, library import and the first rescan after an upgrade, and again when they change on disk.
- **Quality** takes the resolution from the video itself: a release named 2160p that is really 720p is stored (and
  judged for upgrades) as 720p. The source (WEB-DL, Bluray…) still comes from the name.
- **Samples and fakes:** a download whose file is less than half the movie's or episode's runtime is not imported.
  Mislabelled resolutions and audio in none of the wanted languages are flagged on the movie page and episode dialog
  (audio language tags are often missing or wrong, so they only warn).
- **Naming tokens** for file formats: `{MediaInfo VideoCodec}`, `{MediaInfo VideoBitDepth}`,
  `{MediaInfo VideoDynamicRange}`, `{MediaInfo Resolution}`, `{MediaInfo AudioCodec}`, `{MediaInfo AudioChannels}`,
  `{MediaInfo AudioLanguages}` and `{MediaInfo Simple}`.

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
- **Movie and series pages** show the cast and key crew plus TMDB's *Recommendations* and *Similar* titles.
- Titles already in the library carry a ✓ and open their page. Any other title opens a TMDB detail page (overview,
  genres, seasons, cast and crew, related titles) and has an *Add* button that asks for a root folder and quality
  profile, as *Add movie* / *Add series* do.
- **Search** (the button on Discover, or *+ Add movie* / *+ Add series* on the library lists) is one search box for
  movies, series, people and keywords at once. *All* previews each kind; the *Movies*, *Series*, *People* and
  *Keywords* tabs list one kind in full, with overviews and an *Add* (or *Request*) button for titles.
- **Keywords**: opening a keyword ("heist", "time travel", "based on novel"…) lists every movie and show TMDB tags
  with it, most popular first. Add more keywords to narrow it down: a title has to carry all of them.
- **People**: clicking a cast or crew member opens their page with a short biography and every movie and show they
  worked on, grouped by department (acting, directing, writing…). Talk-show and news appearances are left out.

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
- **Whose titles:** *Everything* (the whole library) or *My titles* (what you follow, see [Following titles](#following-titles));
  the calendar starts on *My titles*. Admins and managers can also pick any user who follows titles to see their calendar.
- **iCal feed:** *iCal* gives a URL (`/api/v1/calendar/feed.ics?apikey=…`) for Google Calendar, Apple Calendar or Outlook,
  with its own filters and day range. It carries the API key, so treat it like a password. Only admins see it. With
  another user picked under *Whose titles*, the feed is limited to that user's titles (`userId=`).

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
- Subtitle tracks inside MKV/WebM and MP4/MOV files count as present (forced tracks excepted), so those languages are
  not downloaded again. Only the container's track headers are read, no ffmpeg needed. Turn it off in
  **Settings → Subtitles** to download external subtitles anyway.
- The **Subtitles** page lists files missing languages. Every movie and episode has a subtitle dialog with
  automatic search, manual search (all candidates with score and reasons) and delete.
- **Upgrades:** for 7 days after a download (configurable), subtitles scoring below 90 are searched again with the
  regular retry interval and replaced when a higher-scoring one turns up. Subtitles picked by hand or found on disk are kept.
- **Synchronisation:** a subtitle can be synced against the video from its subtitle list, or automatically after
  download when it scores below a threshold. The reference is a perfect-score subtitle of the same file, a text
  subtitle track inside the video, or the audio. [alass](https://github.com/kaegi/alass) or
  [ffsubsync](https://github.com/smacke/ffsubsync) do the work (plus ffmpeg for the audio and embedded tracks);
  without them, Krakarr fixes a constant shift or a framerate difference against a reference subtitle. The Docker image
  ships alass and a slimmed-down ffmpeg/ffprobe (audio decoding and text subtitle tracks only, ~14 MB in all), so syncing
  works out of the box. ffsubsync isn't included (it needs Python and numpy); outside Docker, install any of the tools on
  `PATH` or set their paths in **Settings → Subtitles**. Syncs run one at a time with a timeout; whoever starts one
  follows it in the tasks menu of the top bar (automatic syncs show there for admins and managers) (progress, time left, cancel, then the outcome). Subtitles (SRT, VTT,
  ASS/SSA) can also be shifted by hand by ± ms.

### Media servers (Jellyfin, Emby, Plex)

- **Settings → Media servers:** enter the URL Krakarr reaches each server on and its key: a Jellyfin API key
  (Dashboard → API Keys), an Emby API key (Settings → Advanced → API Keys) or the Plex owner's `X-Plex-Token`. **Test**
  checks the connection; an empty URL turns a server off.
- **Library refresh:** after Krakarr imports, upgrades, renames or deletes files, the configured servers rescan just the
  affected folder instead of waiting for their scheduled scan. Jellyfin and Emby get the folder through
  `/Library/Media/Updated` (falling back to a full library refresh); Plex refreshes the library section that contains it.
  Changes are grouped per folder for a few seconds, so a season pack triggers one rescan, not one per episode.
- **Path mappings:** when a server sees the library under another path (e.g. Krakarr's `/data/tv` is `/media/tv` or
  `D:\TV` on the server), add a mapping so it gets paths it knows.
- **Open in Jellyfin:** downloaded movies and series show a button to their Jellyfin page, for every user. The title is
  found by its TMDB, TheTVDB or IMDb id (falling back to name and year), so the button appears once Jellyfin has scanned
  it. Set a public URL too if browsers open Jellyfin on a different address.

### Telemetry (OpenTelemetry)

Optional, and off by default. **Settings → Telemetry** exports traces, metrics and logs over OTLP (gRPC on 4317 or
HTTP/protobuf on 4318) to an OpenTelemetry Collector or any compatible backend: Grafana Alloy/Tempo/Mimir/Loki, Jaeger,
SigNoz, Aspire Dashboard and so on. Changes apply right away, without a restart, and an empty endpoint falls back to
`OTEL_EXPORTER_OTLP_ENDPOINT`. Headers (for example `Authorization=Bearer …`) are stored like a password.

- **Traces:** API requests (tagged with the user and role), indexer searches with one span per indexer, grabs, imports,
  movie, series and subtitle searches, scheduled tasks, queued commands (linked to the request that queued them) and
  outgoing HTTP calls made by any of these. Query strings are redacted, so API keys in indexer URLs are not exported.
- **Metrics:** HTTP server and client rates and latency, .NET runtime and EF Core, plus Krakarr's own:

  | Metric | What it measures |
  | --- | --- |
  | `krakarr.library.movies`, `.series`, `.episodes` | Library totals by state (downloaded, missing, unaired, unmonitored) |
  | `krakarr.library.size`, `krakarr.library.subtitles` | Media bytes per media type, subtitle files |
  | `krakarr.library.items.added` / `.removed` | Movies and series added or removed |
  | `krakarr.history.events` | Grabbed, imported, download failed, file deleted, subtitle downloaded |
  | `krakarr.downloads.torrents`, `.rate`, `.remaining`, `.pending_import` | Download client state, throughput and backlog |
  | `krakarr.downloads.grabs`, `.grabbed.size` | Releases sent to the client, by indexer and outcome |
  | `krakarr.indexer.queries`, `.query.duration`, `.releases` | Per-indexer query count, latency, errors and results |
  | `krakarr.task.duration`, `krakarr.command.duration` | Scheduled task and command runs, by outcome |
  | `krakarr.media_requests.created`, `.pending`, `krakarr.auth.logins` | Requests and sign-ins |

- **Logs:** the application log, filtered by `Logging:LogLevel` and linked to the active trace.

Library totals come from a few aggregate queries every few minutes (configurable), and only while metrics are exported.
With telemetry off, nothing listens to the instrumentation, so it costs next to nothing.

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

Each download stays with the client that added it. Switching only changes where *new* torrents go: torrents
already in the other client keep downloading, seeding and importing there, and pause, resume and remove are sent to
the client that owns them. The Downloads page lists both, with a client label on each torrent. A client that isn't
selected keeps running only while it still has torrents, so the built-in engine stops once its last torrent is removed.

### Environment variables

| Variable | Default | Description |
|---|---|---|
| `PUID` / `PGID` | `1000` | User/group the app runs as (files are created with this owner) |
| `UMASK` | `002` | File creation mask |
| `KRAKARR__URLBASE` | *(empty)* | Serve under a sub-path, e.g. `/krakarr`, behind a reverse proxy |
| `KRAKARR__PORT` | `8787` | HTTP port (outside Docker; in Docker use `ASPNETCORE_HTTP_PORTS`) |
| `Database__Provider` | `sqlite` | `sqlite` or `postgres` (to move existing data, use *Settings → Database*) |
| `Database__ConnectionString` | `/config/krakarr.db` | e.g. `Host=postgres;Database=krakarr;Username=krakarr;Password=…` |
| `AllowedHosts` | `*` | Host names Krakarr answers to, separated by `;` (e.g. `krakarr.example.com;192.168.1.10`). Set it to block DNS rebinding attacks |
| `Krakarr__TrustedProxies` | *(loopback only)* | Reverse proxies allowed to set `X-Forwarded-For`/`X-Forwarded-Proto`: IPs or CIDR ranges separated by `,` (e.g. `172.16.0.0/12` for a proxy container on a Docker network). Headers from anyone else are ignored |
| `Krakarr__SecureCookies` | `false` | Always mark the login cookie `Secure` (set it when Krakarr is only reached over HTTPS) |
| `Krakarr__SetupToken` | *(generated)* | Preset the first-run setup token instead of using a random one (useful for automated deployments) |
| `Krakarr__Auth__AttemptsPerMinutePerIp` | `10` | Requests per minute and client IP to the login, setup and password endpoints |
| `Krakarr__Auth__FailuresBeforeLockout` | `5` | Failed logins for one username before it is locked temporarily (30 s, doubling up to 15 min) |
| `Krakarr__Auth__SessionIdleTimeout` | `14.00:00:00` | A sign-in expires after this long without use |
| `Krakarr__Auth__SessionMaxAge` | `30.00:00:00` | A sign-in expires this long after the password was entered, however active it is (`0` disables) |
| `Krakarr__Hsts` | `false` | Send `Strict-Transport-Security` on HTTPS requests. Usually your reverse proxy should do this instead |
| `Krakarr__ContentSecurityPolicy` | *(built in)* | Replace the `Content-Security-Policy` header (e.g. to allow another image host), or `off` to drop it |

Any setting can also be placed in `/config/config.json` (environment variables win).

### Users and roles

The account created during setup is an **admin** (on upgrade, existing accounts become admins too). Admins add people
in *Settings → Users* and give each a role:

| Role | Can |
| --- | --- |
| **Admin** | Everything: settings, indexers, download client, API key and users |
| **Manager** | Add, search, grab and delete movies and series; approve or decline requests; manage downloads, subtitles and library imports |
| **User** | Browse Discover, the library and the calendar; search TMDB/TheTVDB and request movies and series; follow library titles |

Everyone changes their own username and password under *Account* (the current password is required). Changing a role
takes effect immediately; resetting or changing a password, or deleting an account, signs that user out everywhere.
There is always at least one admin, and admins can't delete themselves. The API key acts as an admin.

### Passkeys

Anyone can add **passkeys** under *Settings → Account → Passkeys* (confirming their password) and then use *Sign in
with a passkey* on the login page instead of a username and password: a fingerprint, face, device PIN, phone or
security key. Passkeys always verify the user (PIN or biometrics) and are stored in the device or password manager;
Krakarr keeps only their public keys.

- Browsers only offer passkeys on HTTPS or `localhost`, and never for an IP address: open Krakarr through a domain name
  (behind a TLS reverse proxy that passes the original `Host` header). A passkey belongs to the host name it was
  created on, so add one per name if you reach Krakarr under several.
- Each user can rename or remove their own passkeys. An admin resetting someone's password also removes that user's
  passkeys, so a lost or stolen device can't sign in afterwards.

### Requests

Users can't add titles themselves, so wherever an admin or manager sees *+ Add* (Discover, a title's page, a person's
credits, search results) they see *Request* instead. Users reach the search from Discover (*Search*) or *Search &
request* on the library lists. Requests are listed under **Requests**:

- **Users** see their own requests and can withdraw them while pending.
- **Admins and managers** see everyone's. *Approve* opens the usual add dialog (root
  folder, quality profile, monitoring, search) and adds the title; *Decline* takes an optional reason that the requester
  sees. Adding a requested title directly (or importing it) approves its request too.
- The notification bell in the top bar lists pending requests (those waiting for approval for admins and managers,
  your own open ones for users) and links to the Requests page.
- A title has at most one pending request. Once approved, each request shows whether the title is downloading,
  partially available (series) or available.
- Once a requested title is in the library, its requesters follow it automatically.

### Following titles

Anyone can **Follow** a movie or series that is already in the library from its page; followed titles show under
*My titles* in the calendar and the *Following* filter of the movie and series lists. Admins and managers see who
follows a title next to the button. Removing a title (or a user) removes its follows.

- Whoever adds a title follows it, and requesters follow their titles once they are in the library.
- The notification bell tells you when monitored titles you follow are released (movies out on digital or physical
  media) or air (episodes), today or yesterday.

### Health checks

*Settings → Health* (admins) lists setup problems that would otherwise go unnoticed, and each one also shows in the
admins' notification bell until it is fixed (a dismissed problem comes back if it gets worse):

- Low disk space on the filesystems holding the root folders and the download folder (a warning below 10 % free,
  at most 50 GB; an error below 2 %, at most 5 GB).
- Root folders that are missing, read-only, or empty while the library has files there (an unmounted share).
- Downloads and root folders on different filesystems while hardlinks are on, so imports copy instead.
- A download client that can't be reached, and a download folder that is missing or read-only.
- No enabled indexer, or indexers that keep failing.
- A missing or rejected TMDB or TheTVDB key, and subtitles on without a working provider. Keys are validated against
  the service when they change, then daily (hourly after a failure); *Check now* validates them again.

The checks run every 5 minutes and right after settings, root folders, indexers or the download client change.
`GET /api/v1/system/health` returns the same report; `/api/v1/health` stays an anonymous liveness probe.

**Indexer backoff:** an indexer whose query fails is skipped by searches and RSS syncs for 5 minutes, then 10, 20 and
so on, doubling up to 24 hours while it keeps failing. Any successful query, a successful *Test*, or editing the
indexer clears it. The Indexers page shows which indexers are failing and until when; picking a failing indexer
explicitly in a manual search still queries it.

### Backups and restore

*Settings → Backups* (admins) makes a zip of the database, the data-protection keys (`/config/keys`) and
`/config/config.json` into `/config/Backups`. A backup runs weekly by default (set the interval in days, 0 = off) and
the newest 5 are kept (scheduled and manual together); *Back up now* makes one immediately. The SQLite database is
copied with `VACUUM INTO`, so backups are consistent while Krakarr runs. Copy the zips off the machine too: a backup on
the same SD card or disk won't survive it failing.

To restore, pick *Restore* next to a backup or upload a zip with *Restore from file…*. The archive is checked (it must be
a Krakarr backup for the same database provider) and applied the **next time Krakarr starts**, so restart the container
afterwards. On startup the database is checked with `PRAGMA quick_check` before anything is replaced; the replaced
files stay next to the new ones with a `.pre-restore` suffix, and a backup that fails is kept as
`/config/restore-failed.zip`. The Backups page shows the outcome after the restart.

**PostgreSQL:** backups only hold the settings, keys and `config.json`. Back up the database itself with `pg_dump`,
e.g. `docker exec postgres pg_dump -U krakarr -Fc krakarr > krakarr.dump`, and restore it with `pg_restore` before
restoring the Krakarr backup.

The API is `GET /api/v1/system/backups` (list), `POST` (back up now), `GET`/`DELETE /api/v1/system/backups/{name}`,
`POST /api/v1/system/backups/restore` (zip as the request body), `POST /api/v1/system/backups/{name}/restore` and
`DELETE /api/v1/system/backups/restore` (cancel a staged restore).

### Switching database (SQLite ⇄ PostgreSQL)

*Settings → Database* (admins) moves Krakarr's data to another database without losing any of it. Pick the new database
(a PostgreSQL server, or a SQLite file in `/config`), *Test connection*, then *Switch on next restart*. On the next start,
before anything else opens the database, Krakarr migrates both databases to the current schema, copies every table
(in one transaction, in batches, keeping ids), checks the row counts and points `config.json` at the new database. The
previous `config.json` is kept as `config.json.pre-switch` and the old database is left untouched. If the copy fails,
nothing changes: Krakarr keeps running on the old database and the page shows why.

- The new database must be empty. Krakarr creates a missing PostgreSQL database if the user may create databases.
- If `Database__Provider` / `Database__ConnectionString` environment variables choose the database, Krakarr can't
  change them: the page lists the values to set. Set them and recreate the container; the copy runs on that start.
- PostgreSQL only stores UTC times and enforces column lengths: times are converted, and the rare value longer than its
  column is shortened (the outcome lists how many).
- Make a backup first. Switching back later works the same way, into a new SQLite file.

### Security notes

- **Root folders are the boundary.** Movie and series folders must be directly inside a configured root folder, and
  *Delete with files* only ever removes folders inside a root folder.
- **Brute-force protection.** Credential endpoints are rate limited per IP, repeated failures lock the username
  temporarily, and failed logins are logged as warnings (with the client IP) for tools like fail2ban.
- **Expose it carefully.** Krakarr serves plain HTTP. Put a TLS reverse proxy in front before exposing it to the
  internet, and set `AllowedHosts`.
- **Reverse proxies must be trusted explicitly.** `X-Forwarded-For`/`X-Forwarded-Proto` are only honoured from loopback
  and `Krakarr__TrustedProxies`, so clients cannot fake their IP (rate limits, logs) or HTTPS. If your proxy runs in
  another container or host, add its address or network; Krakarr logs a warning naming the address it ignored.
- **Sessions can be revoked.** Every request re-checks the account, so deleting a user, changing a role or changing a
  password applies at once, and a password change signs out every other session. *Settings → Account → Sign out other
  sessions* does the same without changing the password. Sign-ins also expire 30 days after the password (or passkey)
  was entered.
- **Browser hardening headers.** Every response carries a strict `Content-Security-Policy` (scripts only from Krakarr
  itself, images also from TMDB and TheTVDB), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: no-referrer` and a restrictive `Permissions-Policy`.

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
(title/year/id match, wanted languages, quality profile, size limits, seeders, upgrade/cutoff) → *grab* (.torrent or magnet into the
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
- **Subtitles (Bazarr):** more providers (Podnapisi, Addic7ed, …), embedded subtitle detection, per-series language profiles, encoding fixes
- **Indexers (Prowlarr):** native tracker definitions (Cardigann-style), per-indexer stats, Torznab proxy for other apps
- **Downloads:** per-torrent limits, file selection, Usenet (NZB) support
- Lists (Trakt/IMDb), notifications (including request updates), per-season requests, optional renaming of imported libraries
