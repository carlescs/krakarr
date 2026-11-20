using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Krakarr.Downloads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Series;

public sealed record SeasonDto(int SeasonNumber, bool Monitored, int EpisodeCount, int EpisodeFileCount, int TotalEpisodeCount, long SizeOnDisk);

public sealed record SeriesDto
{
    public int Id { get; init; }
    public int? TmdbId { get; init; }
    public int? TvdbId { get; init; }
    public string? ImdbId { get; init; }
    public required string Title { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? FanartUrl { get; init; }
    public string? Network { get; init; }
    public int Runtime { get; init; }
    public SeriesStatus Status { get; init; }
    public bool Monitored { get; init; }
    public bool SeasonFolder { get; init; }
    public int QualityProfileId { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset Added { get; init; }
    /// <summary>Aired, monitored episodes plus any episode that has a file (Sonarr's definition).</summary>
    public int EpisodeCount { get; init; }
    public int EpisodeFileCount { get; init; }
    public long SizeOnDisk { get; init; }
    public DateOnly? NextAiring { get; init; }
    public int DownloadingCount { get; init; }
    public IReadOnlyList<SeasonDto> Seasons { get; init; } = [];
}

public sealed record EpisodeFileDto(int Id, string RelativePath, long Size, string Quality, string? ReleaseGroup, DateTimeOffset DateAdded);

public sealed record EpisodeDto(
    int Id, int SeasonNumber, int EpisodeNumber, int? AbsoluteNumber, string? Title, string? Overview, DateOnly? AirDate,
    bool Monitored, bool HasAired, EpisodeFileDto? EpisodeFile, double? DownloadProgress);

public sealed record UpdateSeriesRequest(bool Monitored, int QualityProfileId, bool SeasonFolder);
public sealed record MonitorRequest(bool Monitored);
public sealed record SeriesSearchRequest(int? SeasonNumber, IReadOnlyList<int>? EpisodeIds);
public sealed record EpisodeReleaseDto(ReleaseInfo Release, string Quality, int QualityWeight, bool Approved, IReadOnlyList<string> Rejections, string? Episodes, bool SeasonPack);
public sealed record GrabEpisodeReleaseRequest(ReleaseInfo Release);

public static class SeriesEndpoints
{
    public static IEndpointRouteBuilder MapSeriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/series").WithTags("Series");

        group.MapGet("/", async (KrakarrDbContext db, ITorrentEngine engine, TimeProvider time, CancellationToken ct) =>
        {
            // The list only needs per-season counts and sizes, so project the few episode columns that feed them instead
            // of materialising every episode (with overviews) and file of the whole library.
            var list = await db.Series.AsNoTracking().Include(s => s.Seasons).AsSplitQuery().OrderBy(s => s.SortTitle).ToListAsync(ct);
            var stats = (await db.Episodes.AsNoTracking()
                .Select(e => new { e.SeriesId, Stat = new EpisodeStat(e.SeasonNumber, e.AirDate, e.Monitored, e.EpisodeFileId, e.EpisodeFile != null ? e.EpisodeFile.Size : 0) })
                .ToListAsync(ct))
                .ToLookup(x => x.SeriesId, x => x.Stat);
            var downloading = DownloadingBySeries(engine);
            var today = Today(time);
            return list.Select(s => ToDto(s, stats[s.Id], today, downloading));
        });

        // Library import: scan a root folder for existing show folders, then import selected ones in place.
        group.MapGet("/import/scan", async (int rootFolderId, KrakarrDbContext db, LibraryImport.SeriesLibraryImport import, CancellationToken ct) =>
        {
            var root = await db.RootFolders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rootFolderId && r.MediaType == MediaType.Series, ct);
            if (root is null) return Results.NotFound();
            if (!Directory.Exists(root.Path)) return Results.Problem($"Root folder {root.Path} is not accessible", statusCode: 400);
            return Results.Ok(await import.ScanAsync(root, ct));
        });

        group.MapPost("/import", async (List<LibraryImport.SeriesImportItem> items, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            if (items.Count == 0) return Results.Problem("Nothing to import", statusCode: 400);
            var roots = await db.RootFolders.AsNoTracking().Where(r => r.MediaType == MediaType.Series).Select(r => r.Path).ToListAsync(ct);
            var invalid = items.Where(i => !roots.Any(r => Krakarr.Media.Files.MediaFolder.IsDirectChild(r, i.Path)) || !Directory.Exists(i.Path)).ToList();
            if (invalid.Count > 0)
            {
                return Results.Problem($"Not a folder inside a series root folder: {string.Join(", ", invalid.Select(i => i.Path))}", statusCode: 400);
            }

            var command = commands.Enqueue($"Library import ({items.Count} series)", async (sp, token) =>
            {
                var import = sp.GetRequiredService<LibraryImport.SeriesLibraryImport>();
                var lines = new List<string>();
                foreach (var item in items)
                {
                    try
                    {
                        lines.Add(await import.ImportAsync(item, token));
                        if (item.SearchForMissing && item.Monitor != MonitorMode.None)
                        {
                            var seriesId = await sp.GetRequiredService<KrakarrDbContext>().Series.Where(s => s.Path == System.IO.Path.TrimEndingDirectorySeparator(item.Path)).Select(s => s.Id).FirstAsync(token);
                            lines.Add(await sp.GetRequiredService<SeriesService>().AutoSearchAsync(seriesId, null, null, token));
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or MetadataException)
                    {
                        lines.Add($"{System.IO.Path.GetFileName(item.Path)}: {ex.Message}");
                    }
                }

                return string.Join(" · ", lines);
            });
            return Results.Accepted(value: command);
        });

        group.MapGet("/lookup", async (string term, ISeriesMetadata metadata, KrakarrDbContext db, CancellationToken ct) =>
        {
            try
            {
                var results = await metadata.SearchAsync(term, ct);
                var tmdbIds = results.Where(r => r.TmdbId is not null).Select(r => r.TmdbId).ToList();
                var tvdbIds = results.Where(r => r.TvdbId is not null).Select(r => r.TvdbId).ToList();
                var existing = await db.Series.AsNoTracking()
                    .Where(s => tmdbIds.Contains(s.TmdbId) || tvdbIds.Contains(s.TvdbId))
                    .Select(s => new { s.Id, s.TmdbId, s.TvdbId })
                    .ToListAsync(ct);
                return Results.Ok(results.Select(r => r with
                {
                    SeriesId = existing.FirstOrDefault(s => (r.TmdbId is not null && s.TmdbId == r.TmdbId) || (r.TvdbId is not null && s.TvdbId == r.TvdbId))?.Id,
                }));
            }
            catch (MetadataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/", async (AddSeriesRequest req, SeriesService service, ICommandQueue commands, TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                var series = await service.AddAsync(req, ct);
                if (req.SearchForMissing && req.Monitor != MonitorMode.None)
                {
                    var id = series.Id;
                    commands.Enqueue($"Search: {series.Title}", async (sp, token) => await sp.GetRequiredService<SeriesService>().AutoSearchAsync(id, null, null, token));
                }

                return Results.Created($"/api/v1/series/{series.Id}", ToDto(series, Today(time), new Dictionary<int, int>()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or MetadataException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapGet("/{id:int}", async (int id, SeriesService service, ITorrentEngine engine, TimeProvider time, CancellationToken ct) =>
            await service.LoadAsync(id, ct) is { } s ? Results.Ok(ToDto(s, Today(time), DownloadingBySeries(engine))) : Results.NotFound());

        group.MapPut("/{id:int}", async (int id, UpdateSeriesRequest req, KrakarrDbContext db, CancellationToken ct) =>
        {
            var series = await db.Series.FindAsync([id], ct);
            if (series is null) return Results.NotFound();
            if (!await db.QualityProfiles.AnyAsync(p => p.Id == req.QualityProfileId, ct))
                return Results.Problem("Quality profile does not exist", statusCode: 400);
            series.Monitored = req.Monitored;
            series.QualityProfileId = req.QualityProfileId;
            series.SeasonFolder = req.SeasonFolder;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, bool? deleteFiles, KrakarrDbContext db, CancellationToken ct) =>
        {
            var series = await db.Series.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (series is null) return Results.NotFound();
            if (deleteFiles == true && Directory.Exists(series.Path))
            {
                // Only ever delete strictly inside a configured root folder (never a root itself or anything outside).
                var roots = await db.RootFolders.AsNoTracking().Select(r => r.Path).ToListAsync(ct);
                if (!Krakarr.Media.Files.LibraryPaths.CanDeleteRecursively(roots, series.Path))
                {
                    return Results.Problem($"Refusing to delete {series.Path}: it is not inside a configured root folder", statusCode: StatusCodes.Status400BadRequest);
                }

                Directory.Delete(series.Path, recursive: true);
            }

            await db.Downloads.Where(d => d.SeriesId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.SeriesId, (int?)null).SetProperty(d => d.EpisodeIds, new List<int>()), ct);
            await db.Episodes.Where(e => e.SeriesId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.EpisodeFileId, (int?)null), ct);
            db.Series.Remove(series);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/{id:int}/episodes", async (int id, KrakarrDbContext db, ITorrentEngine engine, TimeProvider time, CancellationToken ct) =>
        {
            var episodes = await db.Episodes.AsNoTracking().Include(e => e.EpisodeFile).Where(e => e.SeriesId == id)
                .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToListAsync(ct);
            var progress = await EpisodeProgressAsync(db, engine, id, ct);
            var today = Today(time);
            return episodes.Select(e => new EpisodeDto(
                e.Id, e.SeasonNumber, e.EpisodeNumber, e.AbsoluteNumber, e.Title, e.Overview, e.AirDate, e.Monitored, e.HasAired(today),
                e.EpisodeFile is { } f ? new EpisodeFileDto(f.Id, f.RelativePath, f.Size, Qualities.FindById(f.QualityId).Name, f.ReleaseGroup, f.DateAdded) : null,
                progress.TryGetValue(e.Id, out var p) ? p : null));
        });

        group.MapPut("/{id:int}/seasons/{season:int}", async (int id, int season, MonitorRequest req, KrakarrDbContext db, CancellationToken ct) =>
        {
            var row = await db.Seasons.FirstOrDefaultAsync(s => s.SeriesId == id && s.SeasonNumber == season, ct);
            if (row is null) return Results.NotFound();
            row.Monitored = req.Monitored;
            await db.Episodes.Where(e => e.SeriesId == id && e.SeasonNumber == season)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Monitored, req.Monitored), ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/refresh", async (int id, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (series is null) return Results.NotFound();
            var command = commands.Enqueue($"Refresh: {series.Title}", async (sp, token) =>
            {
                var service = sp.GetRequiredService<SeriesService>();
                var loaded = await service.LoadAsync(id, token) ?? throw new KeyNotFoundException();
                await service.RefreshAsync(loaded, token);
                return $"{loaded.Title}: {loaded.Episodes.Count} episodes";
            });
            return Results.Accepted(value: command);
        });

        // Organize: rename/move existing files (and optionally the series folder) to match the naming settings.
        group.MapGet("/{id:int}/organize", async (int id, SeriesOrganizer organizer, CancellationToken ct) =>
            await organizer.PreviewAsync(id, ct) is { } preview ? Results.Ok(preview) : Results.NotFound());

        group.MapPost("/{id:int}/organize", async (int id, OrganizeRequest? req, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (series is null) return Results.NotFound();
            var renameFolder = req?.RenameFolder ?? false;
            var command = commands.Enqueue($"Organize: {series.Title}", async (sp, token) =>
                await sp.GetRequiredService<SeriesOrganizer>().OrganizeAsync(id, renameFolder, token));
            return Results.Accepted(value: command);
        });

        group.MapPost("/{id:int}/search", async (int id, SeriesSearchRequest? req, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (series is null) return Results.NotFound();
            var label = req?.EpisodeIds is { Count: > 0 } ? "episode" : req?.SeasonNumber is { } n ? $"season {n}" : "missing";
            var command = commands.Enqueue($"Search {label}: {series.Title}", async (sp, token) =>
                await sp.GetRequiredService<SeriesService>().AutoSearchAsync(id, req?.SeasonNumber, req?.EpisodeIds, token));
            return Results.Accepted(value: command);
        });

        group.MapGet("/{id:int}/releases", async (int id, int? season, int? episodeId, SeriesService service, CancellationToken ct) =>
        {
            var series = await service.LoadAsync(id, ct);
            if (series is null) return Results.NotFound();
            var episode = episodeId is { } eid ? series.Episodes.FirstOrDefault(e => e.Id == eid) : null;
            if (episodeId is not null && episode is null) return Results.NotFound();
            if (episode is null && season is null) return Results.Problem("Specify a season or an episode", statusCode: 400);

            var (decisions, errors) = await service.SearchAsync(series, season, episode, requireMonitored: false, ct);
            return Results.Ok(new
            {
                Releases = decisions.Select(d => new EpisodeReleaseDto(d.Release, d.Quality.Name, d.Quality.Weight, d.Approved, d.Rejections,
                    d.Episodes.Count > 0 ? SeriesService.Codes(d.Episodes) : null, d.IsSeasonPack)),
                Errors = errors,
            });
        });

        group.MapPost("/{id:int}/releases/grab", async (int id, GrabEpisodeReleaseRequest req, SeriesService service, TimeProvider time, CancellationToken ct) =>
        {
            var series = await service.LoadAsync(id, ct);
            if (series is null) return Results.NotFound();
            var decision = EpisodeDecisionEngine.Evaluate(series, series.QualityProfile!, series.Episodes, req.Release, null, false, Today(time));
            if (decision.Episodes.Count == 0)
            {
                return Results.Problem($"Cannot map release to episodes: {string.Join("; ", decision.Rejections)}", statusCode: 400);
            }

            try
            {
                return Results.Ok(await service.GrabAsync(series, req.Release, decision.Episodes, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPut("/api/v1/episodes/{id:int}", async (int id, MonitorRequest req, KrakarrDbContext db, CancellationToken ct) =>
        {
            var updated = await db.Episodes.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Monitored, req.Monitored), ct);
            return updated > 0 ? Results.NoContent() : Results.NotFound();
        }).WithTags("Series");

        app.MapDelete("/api/v1/episodefiles/{id:int}", async (int id, KrakarrDbContext db, CancellationToken ct) =>
        {
            var file = await db.EpisodeFiles.FirstOrDefaultAsync(f => f.Id == id, ct);
            if (file is null) return Results.NotFound();
            var series = await db.Series.AsNoTracking().FirstAsync(s => s.Id == file.SeriesId, ct);
            var path = System.IO.Path.Combine(series.Path, file.RelativePath);
            if (File.Exists(path)) File.Delete(path);
            Krakarr.Media.Files.SidecarFiles.DeleteFor(path);
            await db.Episodes.Where(e => e.EpisodeFileId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.EpisodeFileId, (int?)null), ct);
            db.EpisodeFiles.Remove(file);
            db.History.Add(new HistoryEntry { EventType = HistoryEventType.FileDeleted, SourceTitle = file.RelativePath, SeriesId = file.SeriesId, QualityId = file.QualityId });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Series");

        app.MapSeriesDiscoveryEndpoints();
        return app;
    }

    private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private static Dictionary<int, int> DownloadingBySeries(ITorrentEngine engine) =>
        engine.GetStatuses().Where(s => s.SeriesId is not null && !s.Imported)
            .GroupBy(s => s.SeriesId!.Value).ToDictionary(g => g.Key, g => g.Count());

    private static async Task<Dictionary<int, double>> EpisodeProgressAsync(KrakarrDbContext db, ITorrentEngine engine, int seriesId, CancellationToken ct)
    {
        var active = engine.GetStatuses().Where(s => s.SeriesId == seriesId && !s.Imported).ToDictionary(s => s.InfoHash, s => s.Progress, StringComparer.OrdinalIgnoreCase);
        if (active.Count == 0) return [];
        var rows = await db.Downloads.AsNoTracking().Where(d => d.SeriesId == seriesId && !d.Imported).Select(d => new { d.InfoHash, d.EpisodeIds }).ToListAsync(ct);
        var result = new Dictionary<int, double>();
        foreach (var row in rows)
        {
            if (!active.TryGetValue(row.InfoHash, out var p)) continue;
            foreach (var e in row.EpisodeIds) result[e] = Math.Max(result.GetValueOrDefault(e), p);
        }

        return result;
    }

    /// <summary>The episode columns <see cref="ToDto(Core.Domain.Series, IEnumerable{EpisodeStat}, DateOnly, IReadOnlyDictionary{int, int})"/> needs.</summary>
    internal readonly record struct EpisodeStat(int SeasonNumber, DateOnly? AirDate, bool Monitored, int? EpisodeFileId, long FileSize)
    {
        public bool HasAired(DateOnly today) => AirDate is { } d && d <= today;
    }

    internal static SeriesDto ToDto(Core.Domain.Series s, DateOnly today, IReadOnlyDictionary<int, int> downloading) =>
        ToDto(s, s.Episodes.Select(e => new EpisodeStat(e.SeasonNumber, e.AirDate, e.Monitored, e.EpisodeFileId, e.EpisodeFile?.Size ?? 0)), today, downloading);

    internal static SeriesDto ToDto(Core.Domain.Series s, IEnumerable<EpisodeStat> episodes, DateOnly today, IReadOnlyDictionary<int, int> downloading)
    {
        static bool Counts(EpisodeStat e, DateOnly today) => e.EpisodeFileId is not null || (e.Monitored && e.HasAired(today));
        static long Size(IEnumerable<EpisodeStat> eps) => eps.Where(e => e.EpisodeFileId is not null).DistinctBy(e => e.EpisodeFileId).Sum(e => e.FileSize);
        var all = episodes as IReadOnlyCollection<EpisodeStat> ?? episodes.ToList();
        var regular = all.Where(e => e.SeasonNumber > 0).ToList();
        return new SeriesDto
        {
            Id = s.Id,
            TmdbId = s.TmdbId,
            TvdbId = s.TvdbId,
            ImdbId = s.ImdbId,
            Title = s.Title,
            Year = s.Year,
            Overview = s.Overview,
            PosterUrl = s.PosterUrl,
            FanartUrl = s.FanartUrl,
            Network = s.Network,
            Runtime = s.Runtime,
            Status = s.Status,
            Monitored = s.Monitored,
            SeasonFolder = s.SeasonFolder,
            QualityProfileId = s.QualityProfileId,
            Path = s.Path,
            Added = s.Added,
            EpisodeCount = regular.Count(e => Counts(e, today)),
            EpisodeFileCount = regular.Count(e => e.EpisodeFileId is not null),
            SizeOnDisk = Size(all),
            NextAiring = regular.Where(e => e.AirDate > today).Select(e => e.AirDate).Min(),
            DownloadingCount = downloading.GetValueOrDefault(s.Id),
            Seasons = s.Seasons.OrderBy(x => x.SeasonNumber).Select(season =>
            {
                var eps = all.Where(e => e.SeasonNumber == season.SeasonNumber).ToList();
                return new SeasonDto(season.SeasonNumber, season.Monitored, eps.Count(e => Counts(e, today)), eps.Count(e => e.EpisodeFileId is not null), eps.Count, Size(eps));
            }).ToList(),
        };
    }
}
