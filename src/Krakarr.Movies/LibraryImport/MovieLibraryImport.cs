using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Media.Files;
using Krakarr.Media.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Movies.LibraryImport;

public sealed record LibraryVideoFile(string Name, long Size, string Quality);

public sealed record MovieImportCandidate
{
    public required string FolderName { get; init; }
    public required string Path { get; init; }
    public required string ParsedTitle { get; init; }
    public int? ParsedYear { get; init; }
    public MovieLookup? Match { get; init; }
    public MatchConfidence Confidence { get; init; }
    /// <summary>The matched movie is already in the library (under another folder).</summary>
    public bool AlreadyInLibrary { get; init; }
    public LibraryVideoFile? VideoFile { get; init; }
    public int VideoFileCount { get; init; }
    public string? Error { get; init; }
}

public sealed record MovieImportItem(string Path, int TmdbId, int QualityProfileId, bool Monitored = true, bool SearchForMovie = false);

/// <summary>Scans a movie root folder for existing movie folders and imports them in place.</summary>
public sealed class MovieLibraryImport(
    KrakarrDbContext db,
    ITmdbClient tmdb,
    MovieService movies,
    IEventHub eventHub,
    ILogger<MovieLibraryImport> logger)
{
    public async Task<IReadOnlyList<MovieImportCandidate>> ScanAsync(RootFolder root, CancellationToken ct = default)
    {
        var known = await db.Movies.AsNoTracking().Select(m => new { m.Path, m.TmdbId }).ToListAsync(ct);
        var knownTmdb = known.Select(k => k.TmdbId).ToHashSet();
        var folders = MediaFolder.SubFolders(root.Path)
            .Where(d => !known.Any(k => MediaFolder.SamePath(k.Path, d.FullName)))
            .ToList();

        // Metadata lookups run a few at a time to stay friendly with TMDB's rate limits.
        using var gate = new SemaphoreSlim(4);
        var tasks = folders.Select(async folder =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await ScanFolderAsync(folder, knownTmdb, ct);
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    private async Task<MovieImportCandidate> ScanFolderAsync(DirectoryInfo folder, HashSet<int> knownTmdb, CancellationToken ct)
    {
        var parsed = FolderNameParser.Parse(folder.Name);
        var files = MediaFolder.VideoFiles(folder.FullName);
        LibraryVideoFile? main = files.Count == 0 ? null : new LibraryVideoFile(
            System.IO.Path.GetRelativePath(folder.FullName, files[0].Path), files[0].Length, QualityOf(files[0].Path, folder.Name).Name);

        MovieMatch match;
        string? error = null;
        try
        {
            match = await MatchAsync(parsed, ct);
        }
        catch (MetadataException ex)
        {
            match = new MovieMatch(null, MatchConfidence.None);
            error = ex.Message;
        }

        return new MovieImportCandidate
        {
            FolderName = folder.Name,
            Path = folder.FullName,
            ParsedTitle = parsed.Title,
            ParsedYear = parsed.Year,
            Match = match.Movie,
            Confidence = match.Confidence,
            AlreadyInLibrary = match.Movie is not null && knownTmdb.Contains(match.Movie.TmdbId),
            VideoFile = main,
            VideoFileCount = files.Count,
            Error = error,
        };
    }

    public async Task<MovieMatch> MatchAsync(ParsedFolder folder, CancellationToken ct = default)
    {
        if (folder.TmdbId is { } tmdbId)
        {
            return new MovieMatch(await tmdb.GetMovieAsync(tmdbId, ct), MatchConfidence.Exact);
        }

        if (folder.ImdbId is { } imdb && await tmdb.FindByImdbAsync(imdb, ct) is { } byImdb)
        {
            return new MovieMatch(byImdb, MatchConfidence.Exact);
        }

        var results = await tmdb.SearchAsync(folder.Title, folder.Year, ct);
        if (results.Count == 0 && folder.Year is not null)
        {
            results = await tmdb.SearchAsync(folder.Title, null, ct);
        }

        return MovieMatcher.Pick(folder, results);
    }

    /// <summary>Adds the movie at its existing folder and links the main video file. Returns a summary line.</summary>
    public async Task<string> ImportAsync(MovieImportItem item, CancellationToken ct = default)
    {
        var path = System.IO.Path.TrimEndingDirectorySeparator(item.Path);
        var folderName = System.IO.Path.GetFileName(path);
        var movie = await movies.AddAsync(new AddMovieRequest(
            item.TmdbId, item.QualityProfileId, System.IO.Path.GetDirectoryName(path) ?? path, item.Monitored, SearchForMovie: false, ExistingPath: path), ct);

        var files = MediaFolder.VideoFiles(path);
        if (files.Count == 0)
        {
            return $"{movie.Title}: added, no video file found";
        }

        var (source, size) = files[0];
        var quality = QualityOf(source, folderName);
        var fileName = System.IO.Path.GetFileNameWithoutExtension(source);
        var movieFile = new MovieFile
        {
            MovieId = movie.Id,
            RelativePath = System.IO.Path.GetRelativePath(movie.Path, source),
            Size = size,
            QualityId = quality.Id,
            ReleaseGroup = ReleaseParser.Parse(fileName).ReleaseGroup ?? ReleaseParser.Parse(folderName).ReleaseGroup,
            SceneName = fileName,
        };
        db.MovieFiles.Add(movieFile);
        await db.SaveChangesAsync(ct);

        var tracked = await db.Movies.FirstAsync(m => m.Id == movie.Id, ct);
        tracked.MovieFileId = movieFile.Id;
        db.History.Add(new HistoryEntry
        {
            EventType = HistoryEventType.Imported,
            SourceTitle = System.IO.Path.GetFileName(source),
            MovieId = movie.Id,
            QualityId = quality.Id,
            Message = "Imported existing file",
        });
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(EventNames.MovieUpdated, new { movie.Id }, ct);
        logger.LogInformation("Library import: {Title} with {File} ({Quality})", movie.Title, movieFile.RelativePath, quality.Name);
        return $"{movie.Title}: {quality.Name}" + (files.Count > 1 ? $" ({files.Count - 1} other video file(s) ignored)" : string.Empty);
    }

    private static Quality QualityOf(string file, string folderName)
    {
        var q = ReleaseParser.ParseQuality(System.IO.Path.GetFileName(file));
        return q != Qualities.Unknown ? q : ReleaseParser.ParseQuality(folderName);
    }
}
