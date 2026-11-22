using Krakarr.Core.Metadata;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Indexers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Movies;

public sealed record ReleaseDateDto(DateOnly Date, string Country);

/// <param name="Region">Date in the configured release region, when it has one.</param>
/// <param name="Earliest">Earliest date in any country.</param>
public sealed record MovieReleaseDto(ReleaseDateType Type, ReleaseDateDto? Region, ReleaseDateDto? Earliest);

public sealed record MovieFileDto(int Id, string RelativePath, long Size, string Quality, string? ReleaseGroup, DateTimeOffset DateAdded);

public sealed record MovieDto
{
    public int Id { get; init; }
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? FanartUrl { get; init; }
    public int Runtime { get; init; }
    public MovieStatus Status { get; init; }
    public DateOnly? InCinemas { get; init; }
    public DateOnly? DigitalRelease { get; init; }
    public DateOnly? PhysicalRelease { get; init; }
    /// <summary>Theatrical, digital and physical dates: in the release region and earliest worldwide.</summary>
    public IReadOnlyList<MovieReleaseDto> Releases { get; init; } = [];
    public string? ReleaseRegion { get; init; }
    public MinimumAvailability MinimumAvailability { get; init; }
    /// <summary>The movie reached its minimum availability, so automatic searches run.</summary>
    public bool IsAvailable { get; init; }
    public bool Monitored { get; init; }
    public int QualityProfileId { get; init; }
    /// <summary>The movie's own wanted audio languages; null = the global default (<c>GET /languages</c>).</summary>
    public IReadOnlyList<string>? Languages { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset Added { get; init; }
    public bool HasFile { get; init; }
    public MovieFileDto? MovieFile { get; init; }
    /// <summary>Progress (0-100) of an active download, if any.</summary>
    public double? DownloadProgress { get; init; }
    /// <summary>Local id of the TMDB collection the movie belongs to (see <c>GET /movies/collections</c>).</summary>
    public int? CollectionId { get; init; }
}

/// <summary>Row of the library grid: only what the movie list shows, so the whole library stays a small payload.</summary>
public sealed record MovieListItemDto(int Id, string Title, int Year, string? PosterUrl, bool Monitored, bool HasFile, string? Quality, double? DownloadProgress, int? CollectionId);

public sealed record MovieCollectionDto(int Id, int TmdbId, string Name, string? PosterUrl);

/// <param name="Languages">Wanted audio languages, most preferred first; null = use the global default.</param>
public sealed record UpdateMovieRequest(bool Monitored, int QualityProfileId, MinimumAvailability? MinimumAvailability = null, IReadOnlyList<string>? Languages = null);

public sealed record ReleaseDecisionDto(ReleaseInfo Release, string Quality, int QualityWeight, bool Approved, IReadOnlyList<string> Rejections, IReadOnlyList<string> Languages);

public sealed record GrabMovieReleaseRequest(ReleaseInfo Release);

public static class MovieEndpoints
{
    public static IEndpointRouteBuilder MapMovieEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/movies").WithTags("Movies");

        group.MapGet("/", async (KrakarrDbContext db, ITorrentEngine engine, CancellationToken ct) =>
        {
            // The grid only needs a handful of columns: skip overviews, paths and release dates of the whole library.
            var rows = await db.Movies.AsNoTracking().OrderBy(m => m.SortTitle)
                .Select(m => new { m.Id, m.Title, m.Year, m.PosterUrl, m.Monitored, m.CollectionId, QualityId = m.MovieFile != null ? m.MovieFile.QualityId : (int?)null })
                .ToListAsync(ct);
            var progress = ActiveProgress(engine);
            return rows.Select(m => new MovieListItemDto(
                m.Id, m.Title, m.Year, m.PosterUrl, m.Monitored, m.QualityId is not null,
                m.QualityId is { } q ? Qualities.FindById(q).Name : null,
                progress.TryGetValue(m.Id, out var p) ? p : null,
                m.CollectionId));
        });

        // Collections that have movies in the library, for grouping the movie list.
        group.MapGet("/collections", async (KrakarrDbContext db, CancellationToken ct) =>
            await db.MovieCollections.AsNoTracking()
                .OrderBy(c => c.SortName)
                .Select(c => new MovieCollectionDto(c.Id, c.TmdbId, c.Name, c.PosterUrl))
                .ToListAsync(ct));

        group.MapGet("/{id:int}", async (int id, KrakarrDbContext db, ITorrentEngine engine, Krakarr.Data.ISettingsService settings, CancellationToken ct) =>
        {
            var movie = await db.Movies.AsNoTracking().Include(m => m.MovieFile).FirstOrDefaultAsync(m => m.Id == id, ct);
            return movie is null ? Results.NotFound() : Results.Ok(ToDto(movie, ActiveProgress(engine), await ReleaseRegionAsync(settings, ct)));
        });

        // Library import: scan a root folder for existing movie folders, then import selected ones in place.
        group.MapGet("/import/scan", async (int rootFolderId, KrakarrDbContext db, LibraryImport.MovieLibraryImport import, CancellationToken ct) =>
        {
            var root = await db.RootFolders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rootFolderId && r.MediaType == MediaType.Movie, ct);
            if (root is null) return Results.NotFound();
            if (!Directory.Exists(root.Path)) return Results.Problem($"Root folder {root.Path} is not accessible", statusCode: 400);
            return Results.Ok(await import.ScanAsync(root, ct));
        });

        group.MapPost("/import", async (List<LibraryImport.MovieImportItem> items, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            if (items.Count == 0) return Results.Problem("Nothing to import", statusCode: 400);
            var roots = await db.RootFolders.AsNoTracking().Where(r => r.MediaType == MediaType.Movie).Select(r => r.Path).ToListAsync(ct);
            var invalid = items.Where(i => !roots.Any(r => Krakarr.Media.Files.MediaFolder.IsDirectChild(r, i.Path)) || !Directory.Exists(i.Path)).ToList();
            if (invalid.Count > 0)
            {
                return Results.Problem($"Not a folder inside a movie root folder: {string.Join(", ", invalid.Select(i => i.Path))}", statusCode: 400);
            }

            var command = commands.Enqueue($"Library import ({items.Count} movie(s))", async (sp, token) =>
            {
                var import = sp.GetRequiredService<LibraryImport.MovieLibraryImport>();
                var lines = new List<string>();
                foreach (var item in items)
                {
                    try
                    {
                        var line = await import.ImportAsync(item, token);
                        lines.Add(line);
                        if (item.SearchForMovie && line.Contains("no video file", StringComparison.Ordinal))
                        {
                            var movieId = await sp.GetRequiredService<KrakarrDbContext>().Movies.Where(m => m.TmdbId == item.TmdbId).Select(m => m.Id).FirstAsync(token);
                            await sp.GetRequiredService<MovieService>().AutoSearchAsync(movieId, token);
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

        group.MapGet("/lookup", async (string term, ITmdbClient tmdb, KrakarrDbContext db, CancellationToken ct) =>
        {
            try
            {
                var (query, year) = SplitYear(term);
                var results = await tmdb.SearchAsync(query, year, ct);
                var ids = results.Select(r => r.TmdbId).ToList();
                var existing = await db.Movies.AsNoTracking().Where(m => ids.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, m => m.Id, ct);
                return Results.Ok(results.Select(r => r with { MovieId = existing.TryGetValue(r.TmdbId, out var mid) ? mid : null }));
            }
            catch (MetadataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/", async (AddMovieRequest req, HttpContext http, MovieService movies, KrakarrDbContext db, ICommandQueue commands,
            Krakarr.Data.ISettingsService settings, CancellationToken ct) =>
        {
            try
            {
                var movie = await movies.AddAsync(req, ct);
                await MediaSubscriptions.FollowAsAdderAsync(db, http.User, MediaType.Movie, movie.Id, ct);
                if (req.SearchForMovie && movie.Monitored)
                {
                    var id = movie.Id;
                    commands.Enqueue($"Search: {movie.Title}", async (sp, token) => await sp.GetRequiredService<MovieService>().AutoSearchAsync(id, token));
                }

                return Results.Created($"/api/v1/movies/{movie.Id}", ToDto(movie, new Dictionary<int, double>(), await ReleaseRegionAsync(settings, ct)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or MetadataException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPut("/{id:int}", async (int id, UpdateMovieRequest req, KrakarrDbContext db, CancellationToken ct) =>
        {
            var movie = await db.Movies.FindAsync([id], ct);
            if (movie is null) return Results.NotFound();
            if (!await db.QualityProfiles.AnyAsync(p => p.Id == req.QualityProfileId, ct))
                return Results.Problem("Quality profile does not exist", statusCode: 400);
            movie.Monitored = req.Monitored;
            movie.QualityProfileId = req.QualityProfileId;
            if (req.MinimumAvailability is { } availability) movie.MinimumAvailability = availability;
            movie.Languages = req.Languages is null ? null : MediaLanguages.Clean(req.Languages);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, bool? deleteFiles, KrakarrDbContext db, CancellationToken ct) =>
        {
            var movie = await db.Movies.Include(m => m.MovieFile).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (movie is null) return Results.NotFound();
            if (await DeleteAsync(db, movie, deleteFiles == true, ct) is { } error)
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            await MovieService.PruneCollectionsAsync(db, ct);
            return Results.NoContent();
        });

        // Bulk actions on a multi-selection of the movie table.
        group.MapPut("/bulk", async (BulkEditRequest req, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (BulkLimits.Clean(req.Ids) is not { } ids) return Results.Problem(BulkLimits.InvalidMessage, statusCode: 400);
            if (req.Monitored is null && req.QualityProfileId is null) return Results.Problem("Nothing to change", statusCode: 400);
            if (req.QualityProfileId is { } profileId && !await db.QualityProfiles.AnyAsync(p => p.Id == profileId, ct))
                return Results.Problem("Quality profile does not exist", statusCode: 400);
            var affected = await db.Movies.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s =>
            {
                if (req.Monitored is { } monitored) s.SetProperty(m => m.Monitored, monitored);
                if (req.QualityProfileId is { } qualityProfileId) s.SetProperty(m => m.QualityProfileId, qualityProfileId);
            }, ct);
            return Results.Ok(new BulkResult(affected, []));
        });

        group.MapPost("/bulk/search", async (BulkIdsRequest req, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            if (BulkLimits.Clean(req.Ids) is not { } requested) return Results.Problem(BulkLimits.InvalidMessage, statusCode: 400);
            var ids = await db.Movies.AsNoTracking().Where(m => requested.Contains(m.Id)).OrderBy(m => m.SortTitle).Select(m => m.Id).ToListAsync(ct);
            if (ids.Count == 0) return Results.NotFound();
            var command = commands.Enqueue($"Search: {ids.Count} movie(s)", async (sp, token) =>
            {
                var scopedDb = sp.GetRequiredService<KrakarrDbContext>();
                var service = sp.GetRequiredService<MovieService>();
                var (searched, grabbed, failures) = (0, 0, new List<string>());
                foreach (var id in ids)
                {
                    scopedDb.ChangeTracker.Clear(); // keep only the current movie tracked
                    try
                    {
                        var result = await service.AutoSearchAsync(id, token);
                        searched++;
                        if (result.Contains(": grabbed ", StringComparison.Ordinal)) grabbed++;
                    }
                    catch (KeyNotFoundException)
                    {
                        // Deleted since the command was queued.
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures.Add(ex.Message);
                    }
                }

                return BulkLimits.Summary($"Searched {searched} movie(s), grabbed {grabbed}", failures);
            });
            return Results.Accepted(value: command);
        });

        group.MapPost("/bulk/refresh", async (BulkIdsRequest req, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            if (BulkLimits.Clean(req.Ids) is not { } requested) return Results.Problem(BulkLimits.InvalidMessage, statusCode: 400);
            var ids = await db.Movies.AsNoTracking().Where(m => requested.Contains(m.Id)).OrderBy(m => m.SortTitle).Select(m => m.Id).ToListAsync(ct);
            if (ids.Count == 0) return Results.NotFound();
            var command = commands.Enqueue($"Refresh: {ids.Count} movie(s)", async (sp, token) =>
            {
                var scopedDb = sp.GetRequiredService<KrakarrDbContext>();
                var service = sp.GetRequiredService<MovieService>();
                var (refreshed, failures) = (0, new List<string>());
                foreach (var id in ids)
                {
                    scopedDb.ChangeTracker.Clear(); // RefreshAsync saves; keep only the current movie tracked
                    var movie = await scopedDb.Movies.FirstOrDefaultAsync(m => m.Id == id, token);
                    if (movie is null) continue;
                    try
                    {
                        await service.RefreshAsync(movie, token);
                        refreshed++;
                    }
                    catch (MetadataException ex)
                    {
                        failures.Add($"{movie.Title}: {ex.Message}");
                        if (failures.Count >= 3 && refreshed == 0) break; // likely a missing API key: don't hammer TMDB
                    }
                }

                await MovieService.PruneCollectionsAsync(scopedDb, token);
                return BulkLimits.Summary($"Refreshed {refreshed} movie(s)", failures);
            });
            return Results.Accepted(value: command);
        });

        group.MapPost("/bulk/delete", async (BulkDeleteRequest req, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (BulkLimits.Clean(req.Ids) is not { } ids) return Results.Problem(BulkLimits.InvalidMessage, statusCode: 400);
            var (deleted, errors) = (0, new List<string>());
            foreach (var id in ids)
            {
                db.ChangeTracker.Clear(); // one movie (and its file) tracked at a time
                var movie = await db.Movies.Include(m => m.MovieFile).FirstOrDefaultAsync(m => m.Id == id, ct);
                if (movie is null) continue;
                try
                {
                    if (await DeleteAsync(db, movie, req.DeleteFiles, ct) is { } error) errors.Add(error);
                    else deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{movie.Title}: {ex.Message}");
                }
            }

            await MovieService.PruneCollectionsAsync(db, ct);
            return Results.Ok(new BulkResult(deleted, errors));
        });

        group.MapPost("/{id:int}/search", async (int id, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            var movie = await db.Movies.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (movie is null) return Results.NotFound();
            var command = commands.Enqueue($"Search: {movie.Title}", async (sp, token) => await sp.GetRequiredService<MovieService>().AutoSearchAsync(id, token));
            return Results.Accepted(value: command);
        });

        // searchId (optional, client-generated): stream per-indexer progress as EventNames.SearchProgress while the search runs.
        group.MapGet("/{id:int}/releases", async (int id, string? searchId, KrakarrDbContext db, MovieService movies, IEventHub events, CancellationToken ct) =>
        {
            var movie = await db.Movies.Include(m => m.QualityProfile).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (movie is null) return Results.NotFound();
            var (decisions, errors, indexers) = await movies.SearchAsync(movie, IndexerSearchProgress.For(events, searchId), ct);
            return Results.Ok(new
            {
                Releases = decisions.Select(d => new ReleaseDecisionDto(d.Release, d.Quality.Name, d.Quality.Weight, d.Approved, d.Rejections, d.Languages)),
                Errors = errors,
                Indexers = indexers,
            });
        });

        group.MapPost("/{id:int}/releases/grab", async (int id, GrabMovieReleaseRequest req, KrakarrDbContext db, MovieService movies, CancellationToken ct) =>
        {
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.Id == id, ct);
            if (movie is null) return Results.NotFound();
            try
            {
                var download = await movies.GrabAsync(movie, req.Release, ct);
                return Results.Ok(download);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapGet("/api/v1/history", async (int? movieId, int? seriesId, int? page, int? pageSize, KrakarrDbContext db, CancellationToken ct) =>
        {
            var size = Math.Clamp(pageSize ?? 50, 1, 500);
            var query = db.History.AsNoTracking();
            if (movieId is { } mid) query = query.Where(h => h.MovieId == mid);
            if (seriesId is { } sid) query = query.Where(h => h.SeriesId == sid);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(h => h.Date).Skip(((page ?? 1) - 1) * size).Take(size).ToListAsync(ct);
            return new
            {
                Total = total,
                Items = items.Select(h => new
                {
                    h.Id, h.EventType, h.Date, h.SourceTitle, h.MovieId, h.SeriesId, h.Indexer, h.Message,
                    Quality = h.QualityId is { } q ? Qualities.FindById(q).Name : null,
                }),
            };
        }).WithTags("History");

        app.MapMovieDiscoveryEndpoints();
        return app;
    }

    /// <summary>Removes a tracked movie (loaded with its file), and its folder when asked. Returns why not, or null when deleted.</summary>
    private static async Task<string?> DeleteAsync(KrakarrDbContext db, Movie movie, bool deleteFiles, CancellationToken ct)
    {
        if (deleteFiles && Directory.Exists(movie.Path))
        {
            // Only ever delete strictly inside a configured root folder (never a root itself or anything outside).
            var roots = await db.RootFolders.AsNoTracking().Select(r => r.Path).ToListAsync(ct);
            if (!Krakarr.Media.Files.LibraryPaths.CanDeleteRecursively(roots, movie.Path))
            {
                return $"Refusing to delete {movie.Path}: it is not inside a configured root folder";
            }

            Directory.Delete(movie.Path, recursive: true);
        }

        if (movie.MovieFile is { } file)
        {
            movie.MovieFileId = null;
            db.MovieFiles.Remove(file);
        }

        var id = movie.Id;
        await db.Downloads.Where(d => d.MovieId == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.MovieId, (int?)null), ct);
        db.Movies.Remove(movie);
        await db.SaveChangesAsync(ct);
        return null;
    }

    private static Dictionary<int, double> ActiveProgress(ITorrentEngine engine) =>
        engine.GetStatuses()
            .Where(s => s.MovieId is not null && !s.Imported)
            .GroupBy(s => s.MovieId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(s => s.Progress));

    private static async Task<string?> ReleaseRegionAsync(Krakarr.Data.ISettingsService settings, CancellationToken ct) =>
        MovieReleases.NormalizeRegion((await settings.GetAsync<Krakarr.Core.Configuration.MetadataSettings>(ct)).ReleaseRegion);

    private static ReleaseDateDto? ToDto(MovieReleaseDate? d) => d is null ? null : new ReleaseDateDto(d.Date, d.Country);

    private static MovieDto ToDto(Movie m, IReadOnlyDictionary<int, double> progress, string? region) => new()
    {
        Id = m.Id,
        TmdbId = m.TmdbId,
        ImdbId = m.ImdbId,
        Title = m.Title,
        OriginalTitle = m.OriginalTitle,
        Year = m.Year,
        Overview = m.Overview,
        PosterUrl = m.PosterUrl,
        FanartUrl = m.FanartUrl,
        Runtime = m.Runtime,
        Status = MovieReleases.CurrentStatus(m, MovieService.Today),
        InCinemas = m.InCinemas,
        DigitalRelease = m.DigitalRelease,
        PhysicalRelease = m.PhysicalRelease,
        Releases = MovieReleases.AllTypes
            .Select(t => new MovieReleaseDto(t, ToDto(MovieReleases.ForRegion(m.ReleaseDates, t, region)), ToDto(MovieReleases.Earliest(m.ReleaseDates, t))))
            .ToList(),
        ReleaseRegion = region,
        MinimumAvailability = m.MinimumAvailability,
        IsAvailable = MovieReleases.IsAvailable(m, MovieService.Today),
        Monitored = m.Monitored,
        QualityProfileId = m.QualityProfileId,
        Languages = m.Languages,
        Path = m.Path,
        Added = m.Added,
        HasFile = m.MovieFile is not null,
        MovieFile = m.MovieFile is { } f
            ? new MovieFileDto(f.Id, f.RelativePath, f.Size, Qualities.FindById(f.QualityId).Name, f.ReleaseGroup, f.DateAdded)
            : null,
        DownloadProgress = progress.TryGetValue(m.Id, out var p) ? p : null,
        CollectionId = m.CollectionId,
    };

    private static (string Query, int? Year) SplitYear(string term)
    {
        var t = term.Trim();
        var parts = t.Split(' ');
        if (parts.Length > 1 && int.TryParse(parts[^1].Trim('(', ')'), out var y) && y is > 1880 and < 2100)
        {
            return (string.Join(' ', parts[..^1]), y);
        }

        return (t, null);
    }
}
