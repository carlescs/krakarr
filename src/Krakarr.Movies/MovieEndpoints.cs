using Krakarr.Core.Metadata;
using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Downloads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Movies;

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
    public bool Monitored { get; init; }
    public int QualityProfileId { get; init; }
    public required string Path { get; init; }
    public DateTimeOffset Added { get; init; }
    public bool HasFile { get; init; }
    public MovieFileDto? MovieFile { get; init; }
    /// <summary>Progress (0-100) of an active download, if any.</summary>
    public double? DownloadProgress { get; init; }
}

public sealed record UpdateMovieRequest(bool Monitored, int QualityProfileId);

public sealed record ReleaseDecisionDto(ReleaseInfo Release, string Quality, int QualityWeight, bool Approved, IReadOnlyList<string> Rejections);

public sealed record GrabMovieReleaseRequest(ReleaseInfo Release);

public static class MovieEndpoints
{
    public static IEndpointRouteBuilder MapMovieEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/movies").WithTags("Movies");

        group.MapGet("/", async (KrakarrDbContext db, ITorrentEngine engine, CancellationToken ct) =>
        {
            var movies = await db.Movies.AsNoTracking().Include(m => m.MovieFile).OrderBy(m => m.SortTitle).ToListAsync(ct);
            var progress = ActiveProgress(engine);
            return movies.Select(m => ToDto(m, progress));
        });

        group.MapGet("/{id:int}", async (int id, KrakarrDbContext db, ITorrentEngine engine, CancellationToken ct) =>
        {
            var movie = await db.Movies.AsNoTracking().Include(m => m.MovieFile).FirstOrDefaultAsync(m => m.Id == id, ct);
            return movie is null ? Results.NotFound() : Results.Ok(ToDto(movie, ActiveProgress(engine)));
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

        group.MapPost("/", async (AddMovieRequest req, MovieService movies, ICommandQueue commands, Krakarr.Data.ISettingsService settings, CancellationToken ct) =>
        {
            try
            {
                var movie = await movies.AddAsync(req, ct);
                if (req.SearchForMovie && movie.Monitored)
                {
                    var id = movie.Id;
                    commands.Enqueue($"Search: {movie.Title}", async (sp, token) => await sp.GetRequiredService<MovieService>().AutoSearchAsync(id, token));
                }

                return Results.Created($"/api/v1/movies/{movie.Id}", ToDto(movie, new Dictionary<int, double>()));
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
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:int}", async (int id, bool? deleteFiles, KrakarrDbContext db, CancellationToken ct) =>
        {
            var movie = await db.Movies.Include(m => m.MovieFile).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (movie is null) return Results.NotFound();

            if (deleteFiles == true && Directory.Exists(movie.Path))
            {
                Directory.Delete(movie.Path, recursive: true);
            }

            if (movie.MovieFile is { } file)
            {
                movie.MovieFileId = null;
                db.MovieFiles.Remove(file);
            }

            await db.Downloads.Where(d => d.MovieId == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.MovieId, (int?)null), ct);
            db.Movies.Remove(movie);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:int}/search", async (int id, KrakarrDbContext db, ICommandQueue commands, CancellationToken ct) =>
        {
            var movie = await db.Movies.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
            if (movie is null) return Results.NotFound();
            var command = commands.Enqueue($"Search: {movie.Title}", async (sp, token) => await sp.GetRequiredService<MovieService>().AutoSearchAsync(id, token));
            return Results.Accepted(value: command);
        });

        group.MapGet("/{id:int}/releases", async (int id, KrakarrDbContext db, MovieService movies, CancellationToken ct) =>
        {
            var movie = await db.Movies.Include(m => m.QualityProfile).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (movie is null) return Results.NotFound();
            var (decisions, errors) = await movies.SearchAsync(movie, ct);
            return Results.Ok(new
            {
                Releases = decisions.Select(d => new ReleaseDecisionDto(d.Release, d.Quality.Name, d.Quality.Weight, d.Approved, d.Rejections)),
                Errors = errors,
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

    private static Dictionary<int, double> ActiveProgress(ITorrentEngine engine) =>
        engine.GetStatuses()
            .Where(s => s.MovieId is not null && !s.Imported)
            .GroupBy(s => s.MovieId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(s => s.Progress));

    private static MovieDto ToDto(Movie m, IReadOnlyDictionary<int, double> progress) => new()
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
        Status = m.Status,
        Monitored = m.Monitored,
        QualityProfileId = m.QualityProfileId,
        Path = m.Path,
        Added = m.Added,
        HasFile = m.MovieFile is not null,
        MovieFile = m.MovieFile is { } f
            ? new MovieFileDto(f.Id, f.RelativePath, f.Size, Qualities.FindById(f.QualityId).Name, f.ReleaseGroup, f.DateAdded)
            : null,
        DownloadProgress = progress.TryGetValue(m.Id, out var p) ? p : null,
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
