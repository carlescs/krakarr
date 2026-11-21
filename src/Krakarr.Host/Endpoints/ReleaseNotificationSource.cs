using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Notifications;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

/// <summary>
/// Monitored movies released (out on digital or physical media) and monitored episodes that aired today or yesterday,
/// using the same dates as the calendar (the configured release region's date when it has one). Each user only hears
/// about titles they follow (<see cref="MediaSubscription"/>). Notifications are computed from the date, so they go away
/// on their own after the window.
/// </summary>
public sealed class ReleaseNotificationSource(KrakarrDbContext db, ISettingsService settings, TimeProvider time) : INotificationSource
{
    /// <summary>How many days, today included, a release stays in the bell.</summary>
    public const int WindowDays = 2;

    /// <summary>
    /// With a release region, how far back the earliest worldwide date may be for the region's date to still fall in the
    /// window. Keeps the query to recent movies instead of the whole library.
    /// </summary>
    internal const int RegionLagDays = 365;

    /// <summary>Titles named in a notification's message; the rest are summed up as "and N more".</summary>
    internal const int NamedTitles = 3;

    private static readonly ReleaseDateType[] AvailableTypes = [ReleaseDateType.Digital, ReleaseDateType.Physical];

    public async Task<IReadOnlyList<Notification>> GetAsync(NotificationContext context, CancellationToken ct = default)
    {
        // The API key has no user, so it follows nothing.
        if (context.UserId is not { } userId) return [];
        var now = time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var start = today.AddDays(1 - WindowDays);
        var result = new List<Notification>(2);
        if (await MoviesAsync(userId, start, today, now.Offset, ct) is { } movies) result.Add(movies);
        if (await EpisodesAsync(userId, start, today, now.Offset, ct) is { } episodes) result.Add(episodes);
        return result;
    }

    private async Task<Notification?> MoviesAsync(int userId, DateOnly start, DateOnly today, TimeSpan offset, CancellationToken ct)
    {
        var region = MovieReleases.NormalizeRegion((await settings.GetAsync<MetadataSettings>(ct)).ReleaseRegion);
        // The earliest-date columns bound the query: a region's date is never earlier than the earliest worldwide one.
        var from = region is null ? start : start.AddDays(-RegionLagDays);
        var rows = await db.Movies.AsNoTracking()
            .Where(m => m.Monitored
                && db.MediaSubscriptions.Any(x => x.UserId == userId && x.MovieId == m.Id)
                && ((m.DigitalRelease >= from && m.DigitalRelease <= today) || (m.PhysicalRelease >= from && m.PhysicalRelease <= today)))
            .Select(m => new { m.Title, m.ReleaseDates })
            .ToListAsync(ct);

        var released = new List<(string Title, DateOnly Date)>();
        foreach (var m in rows)
        {
            // Released on the first of its digital and physical dates, as the calendar shows them.
            DateOnly? date = null;
            foreach (var type in AvailableTypes)
            {
                if (MovieReleases.Resolve(m.ReleaseDates, type, region) is { } d && (date is null || d.Date < date)) date = d.Date;
            }

            if (date is { } day && day >= start && day <= today) released.Add((m.Title, day));
        }

        if (released.Count == 0) return null;
        var n = released.Count;
        var names = released.OrderByDescending(r => r.Date).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).Select(r => r.Title).ToList();
        return new Notification("releases.movies", "movie",
            n == 1 ? "1 movie released" : $"{n} movies released",
            Summarize(names, n), "/calendar", n, Date: AtMidnight(released.Max(r => r.Date), offset));
    }

    private async Task<Notification?> EpisodesAsync(int userId, DateOnly start, DateOnly today, TimeSpan offset, CancellationToken ct)
    {
        // One row per series, aggregated in SQL.
        var perSeries = await (
            from e in db.Episodes.AsNoTracking()
            join s in db.Series.AsNoTracking() on e.SeriesId equals s.Id
            where e.AirDate >= start && e.AirDate <= today && e.Monitored && s.Monitored
                && db.MediaSubscriptions.Any(x => x.UserId == userId && x.SeriesId == s.Id)
            group e by new { s.Id, s.Title } into g
            select new { g.Key.Title, Count = g.Count(), Latest = g.Max(e => e.AirDate) })
            .ToListAsync(ct);

        if (perSeries.Count == 0) return null;
        var n = perSeries.Sum(s => s.Count);
        var names = perSeries
            .OrderByDescending(s => s.Latest).ThenByDescending(s => s.Count).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Count == 1 ? s.Title : $"{s.Title} ({s.Count})")
            .ToList();
        return new Notification("releases.episodes", "episode",
            n == 1 ? "1 new episode aired" : $"{n} new episodes aired",
            Summarize(names, names.Count), "/calendar", n, Date: AtMidnight(perSeries.Max(s => s.Latest)!.Value, offset));
    }

    /// <summary>"A, B and C" or "A, B, C and 2 more".</summary>
    public static string Summarize(IReadOnlyList<string> names, int total)
    {
        var shown = names.Take(NamedTitles).ToList();
        var rest = total - shown.Count;
        if (rest > 0) return $"{string.Join(", ", shown)} and {rest} more";
        return shown.Count == 1 ? shown[0] : $"{string.Join(", ", shown[..^1])} and {shown[^1]}";
    }

    private static DateTimeOffset AtMidnight(DateOnly date, TimeSpan offset) => new(date.ToDateTime(TimeOnly.MinValue), offset);
}
