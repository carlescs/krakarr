using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Indexers;
using Krakarr.Media.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Movies;

/// <param name="ExistingPath">Use this existing folder as-is (library import) instead of building one from the naming format.</param>
public sealed record AddMovieRequest(
    int TmdbId,
    int QualityProfileId,
    string RootFolderPath,
    bool Monitored = true,
    bool SearchForMovie = true,
    string? ExistingPath = null,
    MinimumAvailability MinimumAvailability = MinimumAvailability.Released);

public sealed class MovieService(
    KrakarrDbContext db,
    ITmdbClient tmdb,
    ISettingsService settings,
    IIndexerSearchService indexerSearch,
    IDownloadService downloads,
    IEventHub eventHub,
    ILogger<MovieService> logger)
{
    public async Task<Movie> AddAsync(AddMovieRequest request, CancellationToken ct = default)
    {
        if (await db.Movies.AnyAsync(m => m.TmdbId == request.TmdbId, ct))
        {
            throw new InvalidOperationException("Movie is already in the library");
        }

        if (!await db.QualityProfiles.AnyAsync(p => p.Id == request.QualityProfileId, ct))
        {
            throw new InvalidOperationException("Quality profile does not exist");
        }

        var lookup = await tmdb.GetMovieAsync(request.TmdbId, ct);
        var media = await settings.GetAsync<MediaManagementSettings>(ct);

        var movie = new Movie
        {
            TmdbId = lookup.TmdbId,
            ImdbId = lookup.ImdbId,
            Title = lookup.Title,
            OriginalTitle = lookup.OriginalTitle,
            SortTitle = ReleaseSortTitle(lookup.Title),
            Year = lookup.Year,
            Overview = lookup.Overview,
            PosterUrl = lookup.PosterUrl,
            FanartUrl = lookup.FanartUrl,
            Runtime = lookup.Runtime,
            Monitored = request.Monitored,
            MinimumAvailability = request.MinimumAvailability,
            QualityProfileId = request.QualityProfileId,
            Path = string.Empty,
        };
        MovieReleases.Apply(movie, lookup.ReleaseDates, MapStatus(lookup.Status), Today);
        await ApplyCollectionAsync(movie, lookup.Collection, ct);
        var roots = await db.RootFolders.AsNoTracking().Where(r => r.MediaType == MediaType.Movie).Select(r => r.Path).ToListAsync(ct);
        movie.Path = LibraryPaths.ResolveItemFolder(roots, request.RootFolderPath, request.ExistingPath,
            () => MovieNaming.BuildFolderName(movie, media.MovieFolderFormat));
        if (await db.Movies.AnyAsync(m => m.Path == movie.Path, ct))
        {
            throw new InvalidOperationException($"Another movie already uses {movie.Path}");
        }

        db.Movies.Add(movie);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Added movie {Title} ({Year})", movie.Title, movie.Year);
        await eventHub.PublishAsync(EventNames.MovieUpdated, new { movie.Id }, ct);
        return movie;
    }

    public async Task RefreshAsync(Movie movie, CancellationToken ct = default)
    {
        var lookup = await tmdb.GetMovieAsync(movie.TmdbId, ct);
        movie.ImdbId = lookup.ImdbId ?? movie.ImdbId;
        movie.Title = lookup.Title;
        movie.OriginalTitle = lookup.OriginalTitle;
        movie.SortTitle = ReleaseSortTitle(lookup.Title);
        movie.Year = lookup.Year;
        movie.Overview = lookup.Overview;
        movie.PosterUrl = lookup.PosterUrl ?? movie.PosterUrl;
        movie.FanartUrl = lookup.FanartUrl ?? movie.FanartUrl;
        movie.Runtime = lookup.Runtime;
        MovieReleases.Apply(movie, lookup.ReleaseDates, MapStatus(lookup.Status), Today);
        await ApplyCollectionAsync(movie, lookup.Collection, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Links the movie to its TMDB collection, creating or updating the collection row. Does not save.</summary>
    private async Task ApplyCollectionAsync(Movie movie, CollectionLookup? lookup, CancellationToken ct)
    {
        if (lookup is null)
        {
            movie.CollectionId = null;
            movie.Collection = null;
            return;
        }

        var collection = db.MovieCollections.Local.FirstOrDefault(c => c.TmdbId == lookup.TmdbId)
            ?? await db.MovieCollections.FirstOrDefaultAsync(c => c.TmdbId == lookup.TmdbId, ct);
        if (collection is null)
        {
            collection = new MovieCollection { TmdbId = lookup.TmdbId, Name = lookup.Name };
            db.MovieCollections.Add(collection);
        }

        collection.Name = lookup.Name;
        collection.SortName = ReleaseSortTitle(lookup.Name);
        collection.PosterUrl = lookup.PosterUrl ?? collection.PosterUrl;
        collection.FanartUrl = lookup.FanartUrl ?? collection.FanartUrl;
        movie.Collection = collection;
    }

    /// <summary>Deletes collections that no movie in the library belongs to any more.</summary>
    public static Task<int> PruneCollectionsAsync(KrakarrDbContext db, CancellationToken ct = default) =>
        db.MovieCollections.Where(c => !db.Movies.Any(m => m.CollectionId == c.Id)).ExecuteDeleteAsync(ct);

    /// <summary>Searches all indexers for a movie and evaluates every release.</summary>
    public async Task<(IReadOnlyList<ReleaseDecision> Decisions, IReadOnlyList<IndexerError> Errors)> SearchAsync(Movie movie, CancellationToken ct = default)
    {
        var profile = movie.QualityProfile ?? await db.QualityProfiles.FirstAsync(p => p.Id == movie.QualityProfileId, ct);
        var existing = await ExistingQualityAsync(movie, ct);

        var result = await indexerSearch.SearchAsync(new SearchCriteria
        {
            Type = SearchType.Movie,
            Query = movie.Year > 0 ? $"{movie.Title} {movie.Year}" : movie.Title,
            ImdbId = movie.ImdbId,
            TmdbId = movie.TmdbId,
        }, ct: ct);

        movie.LastSearched = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return (MovieDecisionEngine.Evaluate(movie, profile, existing, result.Releases), result.Errors);
    }

    /// <summary>Searches and grabs the best approved release. Returns a human readable outcome.</summary>
    public async Task<string> AutoSearchAsync(int movieId, CancellationToken ct = default)
    {
        var movie = await db.Movies.Include(m => m.QualityProfile).FirstOrDefaultAsync(m => m.Id == movieId, ct)
            ?? throw new KeyNotFoundException($"Movie {movieId} not found");

        if (await HasActiveDownloadAsync(movie.Id, ct))
        {
            return $"{movie.Title}: a download is already in progress";
        }

        if (!MovieReleases.IsAvailable(movie, Today))
        {
            return $"{movie.Title}: not available yet (minimum availability: {AvailabilityLabel(movie.MinimumAvailability)})";
        }

        var (decisions, errors) = await SearchAsync(movie, ct);
        var best = decisions.FirstOrDefault(d => d.Approved);
        if (best is null)
        {
            var suffix = errors.Count > 0 ? $" ({errors.Count} indexer error(s))" : string.Empty;
            return $"{movie.Title}: no acceptable releases among {decisions.Count} result(s){suffix}";
        }

        await GrabAsync(movie, best.Release, ct);
        return $"{movie.Title}: grabbed {best.Release.Title}";
    }

    public async Task<Download> GrabAsync(Movie movie, ReleaseInfo release, CancellationToken ct = default)
    {
        var quality = Media.Parsing.ReleaseParser.ParseQuality(release.Title);
        var download = await downloads.GrabAsync(release, new AddTorrentOptions
        {
            Category = "movies",
            MediaType = MediaType.Movie,
            MovieId = movie.Id,
            QualityId = quality.Id,
            DisplayName = release.Title,
        }, ct);

        var entry = new HistoryEntry
        {
            EventType = HistoryEventType.Grabbed,
            SourceTitle = release.Title,
            MovieId = movie.Id,
            QualityId = quality.Id,
            Indexer = release.IndexerName,
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        logger.LogInformation("Grabbed {Release} for {Movie}", release.Title, movie.Title);
        return download;
    }

    public Task<bool> HasActiveDownloadAsync(int movieId, CancellationToken ct = default) =>
        db.Downloads.AnyAsync(d => d.MovieId == movieId && !d.Imported && d.ImportMessage == null, ct);

    private async Task<Quality?> ExistingQualityAsync(Movie movie, CancellationToken ct)
    {
        if (movie.MovieFileId is not { } fileId) return null;
        var file = await db.MovieFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        return file is null ? null : Qualities.FindById(file.QualityId);
    }

    private static string ReleaseSortTitle(string title)
    {
        var t = title.Trim();
        foreach (var article in new[] { "The ", "A ", "An " })
        {
            if (t.StartsWith(article, StringComparison.OrdinalIgnoreCase)) return t[article.Length..].ToLowerInvariant();
        }

        return t.ToLowerInvariant();
    }

    /// <summary>Local date used for release-date and availability checks.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static string AvailabilityLabel(MinimumAvailability availability) => availability switch
    {
        MinimumAvailability.Announced => "announced",
        MinimumAvailability.InCinemas => "in cinemas",
        _ => "released",
    };

    /// <summary>TMDB status, used only when TMDB has no release dates for the movie.</summary>
    private static MovieStatus MapStatus(string? status) => status switch
    {
        "Released" => MovieStatus.Released,
        _ => MovieStatus.Announced,
    };
}
