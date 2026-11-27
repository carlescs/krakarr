using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

public sealed record DashboardMovieStats(int Total, int Monitored, int Downloaded, int Missing);
public sealed record DashboardSeriesStats(int Total, int Monitored, int Continuing, int Ended);
public sealed record DashboardEpisodeStats(int Total, int Downloaded, int Missing, int AiringThisWeek);
/// <summary>Files of one resolution (480, 720, 1080, 2160; 0 = unknown) across movies and episodes.</summary>
public sealed record DashboardResolution(int Resolution, int Files, long Size);
public sealed record DashboardLibrary(
    DashboardMovieStats Movies, DashboardSeriesStats Series, DashboardEpisodeStats Episodes,
    int Files, long SizeOnDisk, int Subtitles, IReadOnlyList<DashboardResolution> Resolutions);

public sealed record DashboardRequester(string Username, int Requests);
public sealed record DashboardUsers(int Total, int Admins, int Managers, int Users, IReadOnlyList<DashboardRequester> TopRequesters);

public sealed record DashboardRequest(int Id, MediaType MediaType, string Title, int Year, string? PosterUrl, RequestStatus Status, string? RequestedBy, DateTimeOffset RequestedAt);
public sealed record DashboardRequests(int Total, int Pending, int Approved, int Declined, int Last30Days, IReadOnlyList<DashboardRequest> Recent);

/// <summary>History over the last <see cref="DashboardEndpoints.ActivityDays"/> days, plus downloads whose import failed.</summary>
public sealed record DashboardActivity(int Grabbed, int Imported, int Failed, int Subtitles, int ImportIssues);

public sealed record DashboardRecent(MediaType MediaType, int Id, string Title, int Year, string? PosterUrl, DateTimeOffset Date);

public sealed record DashboardDto(DashboardLibrary Library, DashboardUsers Users, DashboardRequests Requests, DashboardActivity Activity, IReadOnlyList<DashboardRecent> RecentlyImported,
    IReadOnlyList<DashboardRecent> RecentlyAdded);

public static class DashboardEndpoints
{
    internal const int ActivityDays = 7;
    internal const int RecentRequests = 5;
    /// <summary>Three pages of six on the dashboard.</summary>
    internal const int RecentImports = 18;
    internal const int RecentAdditions = 18;
    internal const int TopRequesters = 5;

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        // Staff overview. Every figure is an aggregate computed in SQL, or a handful of rows, so the cost doesn't grow
        // with the size of the library.
        app.MapGet("/api/v1/dashboard", async (KrakarrDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            return Results.Ok(new DashboardDto(
                await LibraryAsync(db, DateOnly.FromDateTime(now.UtcDateTime), ct),
                await UsersAsync(db, ct),
                await RequestsAsync(db, now, ct),
                await ActivityAsync(db, now, ct),
                await RecentlyImportedAsync(db, ct),
                await RecentlyAddedAsync(db, ct)));
        }).WithTags("Dashboard");

        return app;
    }

    private static async Task<DashboardLibrary> LibraryAsync(KrakarrDbContext db, DateOnly today, CancellationToken ct)
    {
        var movies = await db.Movies.AsNoTracking().GroupBy(_ => 1)
            .Select(g => new DashboardMovieStats(
                g.Count(),
                g.Count(m => m.Monitored),
                g.Count(m => m.MovieFileId != null),
                g.Count(m => m.Monitored && m.MovieFileId == null)))
            .SingleOrDefaultAsync(ct) ?? new DashboardMovieStats(0, 0, 0, 0);

        var series = await db.Series.AsNoTracking().GroupBy(_ => 1)
            .Select(g => new DashboardSeriesStats(
                g.Count(),
                g.Count(s => s.Monitored),
                g.Count(s => s.Status == SeriesStatus.Continuing),
                g.Count(s => s.Status == SeriesStatus.Ended)))
            .SingleOrDefaultAsync(ct) ?? new DashboardSeriesStats(0, 0, 0, 0);

        // Regular episodes only: specials are rarely wanted and would make "missing" look worse than it is.
        var weekEnd = today.AddDays(7);
        var episodes = await db.Episodes.AsNoTracking().Where(e => e.SeasonNumber > 0).GroupBy(_ => 1)
            .Select(g => new DashboardEpisodeStats(
                g.Count(),
                g.Count(e => e.EpisodeFileId != null),
                g.Count(e => e.Monitored && e.EpisodeFileId == null && e.AirDate != null && e.AirDate <= today),
                g.Count(e => e.AirDate != null && e.AirDate > today && e.AirDate <= weekEnd)))
            .SingleOrDefaultAsync(ct) ?? new DashboardEpisodeStats(0, 0, 0, 0);

        // One row per quality (a couple of dozen at most), folded into resolutions here.
        var movieQualities = await db.MovieFiles.AsNoTracking().GroupBy(f => f.QualityId)
            .Select(g => new { QualityId = g.Key, Files = g.Count(), Size = g.Sum(f => f.Size) }).ToListAsync(ct);
        var episodeQualities = await db.EpisodeFiles.AsNoTracking().GroupBy(f => f.QualityId)
            .Select(g => new { QualityId = g.Key, Files = g.Count(), Size = g.Sum(f => f.Size) }).ToListAsync(ct);
        var resolutions = movieQualities.Concat(episodeQualities)
            .GroupBy(q => Qualities.FindById(q.QualityId).Resolution)
            .Select(g => new DashboardResolution(g.Key, g.Sum(q => q.Files), g.Sum(q => q.Size)))
            .OrderByDescending(r => r.Resolution)
            .ToList();

        var subtitles = await db.SubtitleFiles.CountAsync(ct);
        return new DashboardLibrary(movies, series, episodes,
            resolutions.Sum(r => r.Files), resolutions.Sum(r => r.Size), subtitles, resolutions);
    }

    private static async Task<DashboardUsers> UsersAsync(KrakarrDbContext db, CancellationToken ct)
    {
        var roles = await db.Users.AsNoTracking().GroupBy(u => u.Role)
            .Select(g => new { Role = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(UserRole role) => roles.FirstOrDefault(r => r.Role == role)?.Count ?? 0;

        var top = await db.MediaRequests.AsNoTracking().GroupBy(r => r.RequestedById)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.UserId)
            .Take(TopRequesters).ToListAsync(ct);
        var ids = top.Select(t => t.UserId).ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Username }).ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        return new DashboardUsers(roles.Sum(r => r.Count), Count(UserRole.Admin), Count(UserRole.Manager), Count(UserRole.User),
            top.Where(t => names.ContainsKey(t.UserId)).Select(t => new DashboardRequester(names[t.UserId], t.Count)).ToList());
    }

    private static async Task<DashboardRequests> RequestsAsync(KrakarrDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var byStatus = await db.MediaRequests.AsNoTracking().GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(RequestStatus status) => byStatus.FirstOrDefault(r => r.Status == status)?.Count ?? 0;

        var since = now.AddDays(-30);
        var last30 = await db.MediaRequests.CountAsync(r => r.RequestedAt >= since, ct);

        var recent = await db.MediaRequests.AsNoTracking()
            .OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id)
            .Take(RecentRequests)
            .Select(r => new DashboardRequest(r.Id, r.MediaType, r.Title, r.Year, r.PosterUrl, r.Status,
                r.RequestedBy != null ? r.RequestedBy.Username : null, r.RequestedAt))
            .ToListAsync(ct);

        return new DashboardRequests(byStatus.Sum(r => r.Count), Count(RequestStatus.Pending), Count(RequestStatus.Approved),
            Count(RequestStatus.Declined), last30, recent);
    }

    private static async Task<DashboardActivity> ActivityAsync(KrakarrDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var since = now.AddDays(-ActivityDays);
        var events = await db.History.AsNoTracking().Where(h => h.Date >= since).GroupBy(h => h.EventType)
            .Select(g => new { Type = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(HistoryEventType type) => events.FirstOrDefault(e => e.Type == type)?.Count ?? 0;

        var importIssues = await db.Downloads.CountAsync(d => !d.Imported && d.ImportMessage != null, ct);
        return new DashboardActivity(Count(HistoryEventType.Grabbed), Count(HistoryEventType.Imported),
            Count(HistoryEventType.DownloadFailed), Count(HistoryEventType.SubtitleDownloaded), importIssues);
    }

    /// <summary>The titles of the latest imports, newest first, one entry per movie or series.</summary>
    private static async Task<IReadOnlyList<DashboardRecent>> RecentlyImportedAsync(KrakarrDbContext db, CancellationToken ct)
    {
        // A season pack imports many episodes of one show at once: read a few more rows than needed, then dedupe.
        var imports = await db.History.AsNoTracking()
            .Where(h => h.EventType == HistoryEventType.Imported && (h.MovieId != null || h.SeriesId != null))
            .OrderByDescending(h => h.Date).ThenByDescending(h => h.Id)
            .Take(RecentImports * 5)
            .Select(h => new { h.MovieId, h.SeriesId, h.Date })
            .ToListAsync(ct);

        var latest = imports.DistinctBy(h => (h.MovieId, h.SeriesId)).Take(RecentImports).ToList();
        var movieIds = latest.Where(h => h.MovieId != null).Select(h => h.MovieId!.Value).ToList();
        var seriesIds = latest.Where(h => h.MovieId == null).Select(h => h.SeriesId!.Value).ToList();
        var movies = await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Title, m.Year, m.PosterUrl }).ToDictionaryAsync(m => m.Id, ct);
        var shows = await db.Series.AsNoTracking().Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.Year, s.PosterUrl }).ToDictionaryAsync(s => s.Id, ct);

        var result = new List<DashboardRecent>(latest.Count);
        foreach (var h in latest)
        {
            if (h.MovieId is { } movieId)
            {
                if (movies.TryGetValue(movieId, out var m)) result.Add(new DashboardRecent(MediaType.Movie, m.Id, m.Title, m.Year, m.PosterUrl, h.Date));
            }
            else if (shows.TryGetValue(h.SeriesId!.Value, out var s))
            {
                result.Add(new DashboardRecent(MediaType.Series, s.Id, s.Title, s.Year, s.PosterUrl, h.Date));
            }
        }

        return result;
    }

    /// <summary>The movies and series added to the library most recently, newest first.</summary>
    private static async Task<IReadOnlyList<DashboardRecent>> RecentlyAddedAsync(KrakarrDbContext db, CancellationToken ct)
    {
        // The newest few of each, merged: never more than twice the rows shown.
        var movies = await db.Movies.AsNoTracking()
            .OrderByDescending(m => m.Added).ThenByDescending(m => m.Id)
            .Take(RecentAdditions)
            .Select(m => new DashboardRecent(MediaType.Movie, m.Id, m.Title, m.Year, m.PosterUrl, m.Added))
            .ToListAsync(ct);
        var shows = await db.Series.AsNoTracking()
            .OrderByDescending(s => s.Added).ThenByDescending(s => s.Id)
            .Take(RecentAdditions)
            .Select(s => new DashboardRecent(MediaType.Series, s.Id, s.Title, s.Year, s.PosterUrl, s.Added))
            .ToListAsync(ct);

        return movies.Concat(shows).OrderByDescending(r => r.Date).Take(RecentAdditions).ToList();
    }
}
