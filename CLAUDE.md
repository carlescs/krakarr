# Krakarr

Single-container replacement for Radarr, Sonarr, Bazarr, Prowlarr and qBittorrent.
.NET 11 backend (modular monolith) + Angular 22 / Tailwind CSS 4 frontend. See README.md for the full overview.

## Git workflow

- **Never push directly to `main`.** Work on a branch, open a pull request, and merge it (squash) only once every CI check is green. Don't merge with failing or pending checks.
- Commit messages **and the PR title** must follow Conventional Commits (`feat:`, `fix:`, `perf:`, `refactor:`, `docs:`, `test:`, `build:`, `ci:`, `chore:`; `!` or a `BREAKING CHANGE:` footer for breaking changes). CI rejects other messages, and the squash commit on `main` takes the PR title.
- The commit type drives the release: `feat` → minor, `fix`/`perf` → patch. Merging into `main` publishes the `edge` image and releases automatically when warranted. Never create `v*` tags by hand; use the Release workflow ("Run workflow") for manual versions.

## Layout

- `src/Krakarr.Host`: ASP.NET Core host, auth, settings/system endpoints, SignalR, serves the SPA from `wwwroot`.
- `src/Krakarr.Core`: domain entities, quality catalogue, settings records, scheduler/command queue.
- `src/Krakarr.Data`: `KrakarrDbContext`. Migrations live in `Krakarr.Data.Sqlite` and `Krakarr.Data.Postgres`.
- Feature modules: `Krakarr.Indexers`, `Krakarr.Downloads` (built-in MonoTorrent + qBittorrent), `Krakarr.Movies`, `Krakarr.Series` (TMDB or TheTVDB, chosen in settings), `Krakarr.Subtitles` (OpenSubtitles.com; `ISubtitleProvider` for more), `Krakarr.Media` (parser, naming, file transfer, download contents).
- Movies and Series follow the same shape: a metadata client (TMDB transport shared in `Krakarr.Core/Metadata/TmdbApi`; series go through `ISeriesMetadata`, which picks `TmdbTvClient` or `TvdbClient`), `*DecisionEngine` (pure, unit tested), `*Service` (add/search/grab), `*ImportService` and RSS/refresh tasks (`IScheduledTask`), `LibraryImport/` (folder matching + in-place import of existing libraries), and `*Endpoints`.
- `web/`: Angular app. It builds into `src/Krakarr.Host/wwwroot`, which is gitignored.
- `tests/`: xUnit v3 projects, run on Microsoft.Testing.Platform.

## Commands

```bash
dotnet run --project src/Krakarr.Host                          # also builds the web UI (needs Node 22+)
dotnet test --solution Krakarr.slnx -p:SkipWebBuild=true       # .NET tests
cd web && npx ng build && npx ng test --watch=false            # frontend build + tests
```

- Pass `-p:SkipWebBuild=true` to skip the MSBuild web UI build. Docker and CI do this because they build the UI separately.
- Schema change: add a migration for **both** providers:
  ```bash
  dotnet tool restore
  dotnet ef migrations add <Name> --project src/Krakarr.Data.Sqlite --startup-project src/Krakarr.Data.Sqlite --output-dir Migrations
  dotnet ef migrations add <Name> --project src/Krakarr.Data.Postgres --startup-project src/Krakarr.Data.Postgres --output-dir Migrations
  ```

## Conventions

- Package versions are managed centrally in `Directory.Packages.props`, and `Directory.Build.props` sets the shared build options.
- Each module exposes `AddKrakarr<Module>()` for DI and `Map<Module>Endpoints()` for minimal API routes under `/api/v1`.
- Roles (`Admin`, `Manager`, `User`) are enforced centrally by `RoleAccess` (`Krakarr.Host/Auth`), keyed on method and path. Endpoints not listed there need `Manager`, so add a rule when plain users need a new endpoint. In the UI, gate actions with `Auth.isStaff()`/`isAdmin()` and routes with `roleGuard`.
- Notifications (the bell in the top bar): implement `INotificationSource` (`Krakarr.Core/Notifications`), register it as scoped, and publish `EventNames.NotificationsUpdated` when its state changes. Sources run on every bell refresh, so use cheap aggregate queries and filter by the caller's role. `GET /api/v1/notifications` aggregates them; `RequestNotificationSource` is the example.
- Other code talks to downloads only through `ITorrentEngine`, which `DownloadClientRouter` points at the selected client.
- Frontend: standalone components, signals for async state, Tailwind utility classes (`btn-*`, `input`, `card` and others defined in `web/src/styles.css`).
  - Each component lives in its own folder with separate files: `foo/foo.ts`, `foo/foo.html`, `foo/foo.scss` (usually empty, styling is Tailwind) and `foo/foo.spec.ts`. `ng generate component` follows this. `core/` holds services and helpers, not components.
  - Specs render the real template against a mocked API: `setupTest()`, `mockApi()` and `settle()` from `web/src/testing/test-setup.ts`. API methods a test doesn't stub never resolve.

## Memory usage

Krakarr runs 24/7 on small home servers (NAS, Raspberry Pi), often next to other containers. **Always take memory into account** when writing or reviewing code, and assume libraries with thousands of movies and series and tens of thousands of episodes.

- Queries: use `AsNoTracking()` for reads and `Select` only the columns you need. Never `Include` episodes (or other unbounded children) for list endpoints; aggregate in SQL or project slim rows (see the series list in `SeriesEndpoints`). Use `AsSplitQuery()` when including several collections.
- Loops over the library (scheduled tasks, bulk commands): load ids or slim rows, then load one item at a time and call `db.ChangeTracker.Clear()` between items. A scoped `DbContext` otherwise keeps every entity it has seen until the task ends.
- HTTP: never buffer an unbounded response. Use `BoundedContent` (`Krakarr.Core/Http`): `ReadAsByteArrayAsync(maxBytes)` or `ReadAsBoundedStreamAsync(maxBytes)`, plus `HttpCompletionOption.ResponseHeadersRead`, and parse JSON/XML from the stream rather than from a string. Check the status code before reading the body.
- Files: stream them (`FileStream`, `CopyToAsync`) rather than `ReadAllBytes`/`ReadAllText` on media-sized files. Don't copy buffers twice (`MemoryStream` + `ToArray`) when the length is known.
- Caches and in-memory collections (static dictionaries, `ConcurrentDictionary`, queues) must be bounded or pruned. Timers that run every second (torrent engine, SignalR updates) must not allocate in proportion to the library.
- Runtime: the host uses workstation, non-concurrent GC (`Krakarr.Host.csproj`, plus `DOTNET_gcServer=0` in the Dockerfile). Don't switch to Server GC.
