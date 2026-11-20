using System.Globalization;
using System.Text.RegularExpressions;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Krakarr.Media.Files;
using Krakarr.Media.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Series.LibraryImport;

public sealed record SeriesImportCandidate
{
    public required string FolderName { get; init; }
    public required string Path { get; init; }
    public required string ParsedTitle { get; init; }
    public int? ParsedYear { get; init; }
    public SeriesLookup? Match { get; init; }
    public MatchConfidence Confidence { get; init; }
    public bool AlreadyInLibrary { get; init; }
    public int VideoFileCount { get; init; }
    /// <summary>Video files whose name identifies an episode (SxxEyy, 1x02, "Season 1/Episode 5").</summary>
    public int EpisodeFileCount { get; init; }
    public string? Error { get; init; }
}

public sealed record SeriesImportItem(
    string Path, int? TmdbId, int? TvdbId, int QualityProfileId, MonitorMode Monitor = MonitorMode.All, bool SearchForMissing = false);

/// <summary>A video file of an existing series folder mapped to season/episode numbers.</summary>
public sealed record MappedFile(string Path, long Size, int Season, IReadOnlyList<int> Episodes, Quality Quality, string? ReleaseGroup);

/// <summary>Scans a series root folder for existing show folders and imports them in place.</summary>
public sealed partial class SeriesLibraryImport(
    KrakarrDbContext db,
    ISeriesMetadata metadata,
    SeriesService seriesService,
    IEventHub eventHub,
    ILogger<SeriesLibraryImport> logger)
{
    public async Task<IReadOnlyList<SeriesImportCandidate>> ScanAsync(RootFolder root, CancellationToken ct = default)
    {
        var known = await db.Series.AsNoTracking().Select(s => new { s.Path, s.TmdbId, s.TvdbId }).ToListAsync(ct);
        var knownTmdb = known.Where(k => k.TmdbId is not null).Select(k => k.TmdbId!.Value).ToHashSet();
        var knownTvdb = known.Where(k => k.TvdbId is not null).Select(k => k.TvdbId!.Value).ToHashSet();
        var folders = MediaFolder.SubFolders(root.Path)
            .Where(d => !known.Any(k => MediaFolder.SamePath(k.Path, d.FullName)))
            .ToList();

        using var gate = new SemaphoreSlim(4);
        var tasks = folders.Select(async folder =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var parsed = FolderNameParser.Parse(folder.Name);
                var files = MediaFolder.VideoFiles(folder.FullName);
                var mapped = files.Count(f => Identify(folder.FullName, f.Path) is not null);
                SeriesMatch match;
                string? error = null;
                try
                {
                    match = await MatchAsync(parsed, ct);
                }
                catch (MetadataException ex)
                {
                    match = new SeriesMatch(null, MatchConfidence.None);
                    error = ex.Message;
                }

                return new SeriesImportCandidate
                {
                    FolderName = folder.Name,
                    Path = folder.FullName,
                    ParsedTitle = parsed.Title,
                    ParsedYear = parsed.Year,
                    Match = match.Series,
                    Confidence = match.Confidence,
                    AlreadyInLibrary = match.Series is { } m
                        && ((m.TmdbId is { } t && knownTmdb.Contains(t)) || (m.TvdbId is { } v && knownTvdb.Contains(v))),
                    VideoFileCount = files.Count,
                    EpisodeFileCount = mapped,
                    Error = error,
                };
            }
            finally
            {
                gate.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    public async Task<SeriesMatch> MatchAsync(ParsedFolder folder, CancellationToken ct = default)
    {
        var results = await metadata.SearchAsync(folder.Title, ct);
        return SeriesMatcher.Pick(folder, results);
    }

    /// <summary>Adds the series at its existing folder and links every recognisable episode file. Returns a summary line.</summary>
    public async Task<string> ImportAsync(SeriesImportItem item, CancellationToken ct = default)
    {
        var path = System.IO.Path.TrimEndingDirectorySeparator(item.Path);
        var files = MediaFolder.VideoFiles(path);
        var seasonFolders = files.Any(f => System.IO.Path.GetRelativePath(path, f.Path).Contains(System.IO.Path.DirectorySeparatorChar));

        var added = await seriesService.AddAsync(new AddSeriesRequest(
            item.TmdbId, item.TvdbId, item.QualityProfileId, System.IO.Path.GetDirectoryName(path) ?? path, item.Monitor, seasonFolders,
            SearchForMissing: false, ExistingPath: path), ct);

        var series = await seriesService.LoadAsync(added.Id, ct) ?? throw new InvalidOperationException("Series vanished during import");
        var (linked, problems) = await LinkFilesAsync(series, files, ct);

        db.History.Add(new HistoryEntry
        {
            EventType = HistoryEventType.Imported,
            SourceTitle = System.IO.Path.GetFileName(path),
            SeriesId = series.Id,
            Message = linked.Count > 0 ? $"Imported existing files: {SeriesService.Codes(linked)}" : "No episode files found",
        });
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(EventNames.SeriesUpdated, new { series.Id }, ct);

        logger.LogInformation("Library import: {Title} with {Count} episode(s); {Problems} file(s) skipped", series.Title, linked.Count, problems.Count);
        var summary = $"{series.Title}: {linked.Count} episode(s) linked";
        if (problems.Count > 0) summary += $"; skipped {string.Join(", ", problems.Take(5))}{(problems.Count > 5 ? $" (+{problems.Count - 5} more)" : string.Empty)}";
        return summary;
    }

    /// <summary>Maps files to episodes (best quality first, no episode twice) and stores EpisodeFiles.</summary>
    public async Task<(List<Episode> Linked, List<string> Problems)> LinkFilesAsync(
        Core.Domain.Series series, IReadOnlyList<(string Path, long Length)> files, CancellationToken ct)
    {
        var problems = new List<string>();
        var mapped = new List<MappedFile>();
        foreach (var (file, size) in files)
        {
            var id = Identify(series.Path, file);
            var relative = System.IO.Path.GetRelativePath(series.Path, file);
            if (id is null)
            {
                problems.Add($"{relative} (no episode number)");
                continue;
            }

            var parsed = ReleaseParser.Parse(System.IO.Path.GetFileName(file));
            mapped.Add(new MappedFile(file, size, id.Value.Season, id.Value.Episodes, parsed.Quality, parsed.ReleaseGroup));
        }

        var taken = new HashSet<int>();
        var linked = new List<Episode>();
        foreach (var m in mapped.OrderByDescending(m => m.Quality.Weight).ThenByDescending(m => m.Size))
        {
            var episodes = m.Episodes.Select(n => series.Episodes.FirstOrDefault(e => e.SeasonNumber == m.Season && e.EpisodeNumber == n)).ToList();
            var relative = System.IO.Path.GetRelativePath(series.Path, m.Path);
            if (episodes.Any(e => e is null))
            {
                problems.Add($"{relative} ({SeriesNaming.EpisodeCode(m.Season, m.Episodes)} unknown)");
                continue;
            }

            if (episodes.Any(e => taken.Contains(e!.Id)))
            {
                problems.Add($"{relative} (duplicate)");
                continue;
            }

            var file = new EpisodeFile
            {
                SeriesId = series.Id,
                SeasonNumber = m.Season,
                RelativePath = relative,
                Size = m.Size,
                QualityId = m.Quality.Id,
                ReleaseGroup = m.ReleaseGroup,
                SceneName = System.IO.Path.GetFileNameWithoutExtension(m.Path),
            };
            db.EpisodeFiles.Add(file);
            await db.SaveChangesAsync(ct);
            foreach (var e in episodes)
            {
                e!.EpisodeFileId = file.Id;
                taken.Add(e.Id);
                linked.Add(e);
            }
        }

        await db.SaveChangesAsync(ct);
        return (linked, problems);
    }

    /// <summary>
    /// Season and episode numbers of a file inside a series folder: from the file name (S01E02, 1x02),
    /// or an episode number in the name combined with a "Season N"/"Specials" parent folder.
    /// </summary>
    public static (int Season, IReadOnlyList<int> Episodes)? Identify(string seriesPath, string file)
    {
        var parsed = ReleaseParser.Parse(System.IO.Path.GetFileName(file));
        if (parsed.IsEpisode && parsed.SeasonNumber is { } s)
        {
            return (s, parsed.EpisodeNumbers);
        }

        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file));
        if (parent is null || MediaFolder.SamePath(System.IO.Path.GetDirectoryName(file)!, seriesPath)) return null;
        int? season = parent.Equals("Specials", StringComparison.OrdinalIgnoreCase) ? 0
            : SeasonFolder().Match(parent) is { Success: true } sm ? int.Parse(sm.Groups["n"].Value, CultureInfo.InvariantCulture) : null;
        if (season is null) return null;

        var name = System.IO.Path.GetFileNameWithoutExtension(file);
        var em = EpisodeOnly().Match(name);
        if (!em.Success) em = LeadingNumber().Match(name);
        return em.Success ? (season.Value, [int.Parse(em.Groups["n"].Value, CultureInfo.InvariantCulture)]) : null;
    }

    [GeneratedRegex(@"^(?:season|series|staffel|temporada|saison|s)[ ._-]*(?<n>\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"(?<![a-z0-9])(?:e|ep|episode)[ ._-]?(?<n>\d{1,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeOnly();

    [GeneratedRegex(@"^(?<n>\d{1,3})(?:[ ._-]|$)")]
    private static partial Regex LeadingNumber();
}
