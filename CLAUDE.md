# Krakarr

Single-container replacement for Radarr, Sonarr, Bazarr, Prowlarr and qBittorrent.
.NET 11 backend (modular monolith) + Angular 22 / Tailwind CSS 4 frontend. See README.md for the full overview.

## Git workflow

- Commit and push **directly to `main`**. Do not create feature branches or pull requests unless explicitly asked.
- Commit messages **must** follow Conventional Commits (`feat:`, `fix:`, `perf:`, `refactor:`, `docs:`, `test:`, `build:`, `ci:`, `chore:`; `!` or a `BREAKING CHANGE:` footer for breaking changes). CI rejects other messages.
- The commit type drives the release: `feat` → minor, `fix`/`perf` → patch. Pushing to `main` publishes the `edge` image and releases automatically when warranted. Never create `v*` tags by hand; use the Release workflow ("Run workflow") for manual versions.

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
- Other code talks to downloads only through `ITorrentEngine`, which `DownloadClientRouter` points at the selected client.
- Frontend: standalone components with inline templates, signals for async state, Tailwind utility classes (`btn-*`, `input`, `card` and others defined in `web/src/styles.css`).
