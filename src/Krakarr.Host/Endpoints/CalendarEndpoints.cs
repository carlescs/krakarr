using System.Globalization;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Host.Auth;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

public enum CalendarEntryKind
{
    Movie = 1,
    Episode = 2,
}

/// <summary>One day's release: a movie's cinema, digital or physical release, or an episode's air date.</summary>
public sealed record CalendarEntry
{
    public CalendarEntryKind Kind { get; init; }
    public DateOnly Date { get; init; }
    /// <summary>Movie entries: which release this is.</summary>
    public ReleaseDateType? ReleaseType { get; init; }
    /// <summary>Movie entries: the country the date comes from.</summary>
    public string? Country { get; init; }
    /// <summary>Movie entries: the date is from the configured release region (false = earliest worldwide).</summary>
    public bool IsRegionDate { get; init; }
    /// <summary>Movie entries: the earliest date of this release type in any country, when it differs.</summary>
    public DateOnly? EarliestDate { get; init; }
    public string? EarliestCountry { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }
    public int? EpisodeId { get; init; }
    /// <summary>Movie or series title.</summary>
    public required string Title { get; init; }
    public int Year { get; init; }
    public string? EpisodeTitle { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string? Network { get; init; }
    public string? PosterUrl { get; init; }
    public string? Overview { get; init; }
    public int Runtime { get; init; }
    /// <summary>The movie, or both the series and the episode, are monitored.</summary>
    public bool Monitored { get; init; }
    public bool HasFile { get; init; }
    public bool Downloading { get; init; }
}

public sealed class CalendarQuery
{
    public DateOnly Start { get; init; }
    public DateOnly End { get; init; }
    public bool Movies { get; init; } = true;
    public bool Series { get; init; } = true;
    public IReadOnlyCollection<ReleaseDateType> ReleaseTypes { get; init; } = MovieReleases.AllTypes;
    public bool IncludeUnmonitored { get; init; } = true;
    /// <summary>Only titles this user subscribes to; null = the whole library.</summary>
    public int? SubscriberId { get; init; }
}

/// <summary>Builds calendar entries for the library (movies and episodes).</summary>
public sealed class CalendarService(KrakarrDbContext db, ISettingsService settings)
{
    /// <summary>Longest range one request may cover.</summary>
    public const int MaxRangeDays = 400;

    public async Task<List<CalendarEntry>> GetAsync(CalendarQuery query, CancellationToken ct = default)
    {
        var region = MovieReleases.NormalizeRegion((await settings.GetAsync<MetadataSettings>(ct)).ReleaseRegion);
        var active = await db.Downloads.AsNoTracking()
            .Where(d => !d.Imported && d.ImportMessage == null && (d.MovieId != null || d.SeriesId != null))
            .Select(d => new { d.MovieId, d.EpisodeIds })
            .ToListAsync(ct);
        var downloadingMovies = active.Where(d => d.MovieId is not null).Select(d => d.MovieId!.Value).ToHashSet();
        var downloadingEpisodes = active.SelectMany(d => d.EpisodeIds).ToHashSet();

        var entries = new List<CalendarEntry>();
        if (query.Movies && query.ReleaseTypes.Count > 0)
        {
            entries.AddRange(await MoviesAsync(query, region, downloadingMovies, ct));
        }

        if (query.Series)
        {
            entries.AddRange(await EpisodesAsync(query, downloadingEpisodes, ct));
        }

        return entries
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)
            .ThenBy(e => e.ReleaseType)
            .ToList();
    }

    private async Task<List<CalendarEntry>> MoviesAsync(CalendarQuery query, string? region, HashSet<int> downloading, CancellationToken ct)
    {
        var (start, end) = (query.Start, query.End);
        // A region's date is never earlier than the earliest worldwide date, so the earliest-date columns bound the end.
        // Without a region they bound the start too.
        var movies = db.Movies.AsNoTracking()
            .Where(m => m.InCinemas <= end || m.DigitalRelease <= end || m.PhysicalRelease <= end);
        if (region is null)
        {
            movies = movies.Where(m => (m.InCinemas >= start && m.InCinemas <= end)
                || (m.DigitalRelease >= start && m.DigitalRelease <= end)
                || (m.PhysicalRelease >= start && m.PhysicalRelease <= end));
        }

        if (!query.IncludeUnmonitored) movies = movies.Where(m => m.Monitored);
        if (query.SubscriberId is { } subscriber)
            movies = movies.Where(m => db.MediaSubscriptions.Any(x => x.UserId == subscriber && x.MovieId == m.Id));

        var rows = await movies
            .Select(m => new { m.Id, m.Title, m.Year, m.PosterUrl, m.Overview, m.Runtime, m.Monitored, m.MovieFileId, m.ReleaseDates })
            .ToListAsync(ct);

        var entries = new List<CalendarEntry>();
        foreach (var m in rows)
        {
            foreach (var type in query.ReleaseTypes.Distinct())
            {
                var regional = MovieReleases.ForRegion(m.ReleaseDates, type, region);
                var earliest = MovieReleases.Earliest(m.ReleaseDates, type);
                var shown = regional ?? earliest;
                if (shown is null || shown.Date < start || shown.Date > end) continue;
                entries.Add(new CalendarEntry
                {
                    Kind = CalendarEntryKind.Movie,
                    Date = shown.Date,
                    ReleaseType = type,
                    Country = shown.Country,
                    IsRegionDate = regional is not null,
                    EarliestDate = regional is not null && earliest is not null && earliest.Date < regional.Date ? earliest.Date : null,
                    EarliestCountry = regional is not null && earliest is not null && earliest.Date < regional.Date ? earliest.Country : null,
                    MovieId = m.Id,
                    Title = m.Title,
                    Year = m.Year,
                    PosterUrl = m.PosterUrl,
                    Overview = m.Overview,
                    Runtime = m.Runtime,
                    Monitored = m.Monitored,
                    HasFile = m.MovieFileId is not null,
                    Downloading = downloading.Contains(m.Id),
                });
            }
        }

        return entries;
    }

    private async Task<List<CalendarEntry>> EpisodesAsync(CalendarQuery query, HashSet<int> downloading, CancellationToken ct)
    {
        var (start, end) = (query.Start, query.End);
        var series = db.Series.AsNoTracking();
        if (query.SubscriberId is { } subscriber)
            series = series.Where(s => db.MediaSubscriptions.Any(x => x.UserId == subscriber && x.SeriesId == s.Id));
        var rows = await (
            from e in db.Episodes.AsNoTracking()
            join s in series on e.SeriesId equals s.Id
            where e.AirDate >= start && e.AirDate <= end
                && (query.IncludeUnmonitored || (e.Monitored && s.Monitored))
            select new
            {
                e.Id, e.SeriesId, e.SeasonNumber, e.EpisodeNumber, e.Title, e.Overview, e.AirDate, e.Runtime, e.Monitored, e.EpisodeFileId,
                SeriesTitle = s.Title, SeriesYear = s.Year, SeriesMonitored = s.Monitored, s.PosterUrl, s.Network, SeriesRuntime = s.Runtime,
            }).ToListAsync(ct);

        return rows.Select(r => new CalendarEntry
        {
            Kind = CalendarEntryKind.Episode,
            Date = r.AirDate!.Value,
            SeriesId = r.SeriesId,
            EpisodeId = r.Id,
            Title = r.SeriesTitle,
            Year = r.SeriesYear,
            EpisodeTitle = r.Title,
            SeasonNumber = r.SeasonNumber,
            EpisodeNumber = r.EpisodeNumber,
            Network = r.Network,
            PosterUrl = r.PosterUrl,
            Overview = r.Overview,
            Runtime = r.Runtime > 0 ? r.Runtime : r.SeriesRuntime,
            Monitored = r.Monitored && r.SeriesMonitored,
            HasFile = r.EpisodeFileId is not null,
            Downloading = downloading.Contains(r.Id),
        }).ToList();
    }
}

/// <summary>Writes calendar entries as an iCalendar (RFC 5545) feed of all-day events.</summary>
public static class ICalendarWriter
{
    public static string Write(IEnumerable<CalendarEntry> entries, string calendarName, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:-//Krakarr//Calendar//EN");
        Line(sb, "CALSCALE:GREGORIAN");
        Line(sb, "METHOD:PUBLISH");
        Line(sb, "X-WR-CALNAME:" + Escape(calendarName));
        Line(sb, "REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Line(sb, "X-PUBLISHED-TTL:PT1H");
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var e in entries)
        {
            Line(sb, "BEGIN:VEVENT");
            Line(sb, "UID:" + Uid(e));
            Line(sb, "DTSTAMP:" + stamp);
            Line(sb, "DTSTART;VALUE=DATE:" + e.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line(sb, "DTEND;VALUE=DATE:" + e.Date.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            Line(sb, "SUMMARY:" + Escape(Summary(e)));
            if (Description(e) is { Length: > 0 } description) Line(sb, "DESCRIPTION:" + Escape(description));
            Line(sb, "CATEGORIES:" + (e.Kind == CalendarEntryKind.Movie ? "Movies" : "Series"));
            Line(sb, "TRANSP:TRANSPARENT");
            Line(sb, "END:VEVENT");
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    public static string ReleaseLabel(ReleaseDateType type) => type switch
    {
        ReleaseDateType.Theatrical => "In cinemas",
        ReleaseDateType.Digital => "Digital",
        _ => "Physical",
    };

    public static string Summary(CalendarEntry e) => e.Kind == CalendarEntryKind.Movie
        ? $"{e.Title} ({ReleaseLabel(e.ReleaseType ?? ReleaseDateType.Theatrical)})"
        : string.IsNullOrWhiteSpace(e.EpisodeTitle)
            ? $"{e.Title} - {EpisodeCode(e)}"
            : $"{e.Title} - {EpisodeCode(e)} - {e.EpisodeTitle}";

    private static string EpisodeCode(CalendarEntry e) =>
        string.Create(CultureInfo.InvariantCulture, $"S{e.SeasonNumber ?? 0:00}E{e.EpisodeNumber ?? 0:00}");

    private static string Uid(CalendarEntry e) => e.Kind == CalendarEntryKind.Movie
        ? string.Create(CultureInfo.InvariantCulture, $"movie-{e.MovieId}-{e.ReleaseType?.ToString().ToLowerInvariant()}@krakarr")
        : string.Create(CultureInfo.InvariantCulture, $"episode-{e.EpisodeId}@krakarr");

    private static string Description(CalendarEntry e)
    {
        var parts = new List<string>();
        if (e.Kind == CalendarEntryKind.Movie && e.Country is not null)
        {
            parts.Add(e.IsRegionDate ? $"Release date in {e.Country}" : $"Earliest release ({e.Country})");
        }

        if (e.Network is { Length: > 0 }) parts.Add(e.Network);
        if (e.Overview is { Length: > 0 }) parts.Add(e.Overview);
        return string.Join("\n\n", parts);
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal);

    /// <summary>Appends a content line, folded at 75 octets as RFC 5545 requires.</summary>
    private static void Line(StringBuilder sb, string line)
    {
        var bytes = 0;
        var first = true;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (bytes + size > (first ? 75 : 74))
            {
                sb.Append("\r\n ");
                bytes = 0;
                first = false;
            }

            sb.Append(rune.ToString());
            bytes += size;
        }

        sb.Append("\r\n");
    }
}

public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/calendar").WithTags("Calendar");

        // user: "me" for the caller's subscriptions, or (staff only) another user's id; omitted = the whole library.
        group.MapGet("/", async (DateOnly? start, DateOnly? end, bool? unmonitored, string? user, HttpContext http, CalendarService calendar, CancellationToken ct) =>
        {
            int? subscriber = null;
            if (!string.IsNullOrEmpty(user))
            {
                if (user.Equals("me", StringComparison.OrdinalIgnoreCase))
                {
                    subscriber = http.User.GetUserId();
                    if (subscriber is null) return Results.Problem("user=me needs a user account, not the API key.", statusCode: 400);
                }
                else if (int.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    if (id != http.User.GetUserId() && http.User.GetRole() is not (UserRole.Admin or UserRole.Manager))
                        return Results.Problem("You can only see your own titles.", statusCode: StatusCodes.Status403Forbidden);
                    subscriber = id;
                }
                else
                {
                    return Results.Problem("user must be \"me\" or a user id", statusCode: 400);
                }
            }

            var from = start ?? Today().AddDays(-7);
            var to = end ?? from.AddDays(35);
            if (to < from) return Results.Problem("end must not be before start", statusCode: 400);
            if (to.DayNumber - from.DayNumber > CalendarService.MaxRangeDays)
            {
                return Results.Problem($"The range may span at most {CalendarService.MaxRangeDays} days", statusCode: 400);
            }

            return Results.Ok(await calendar.GetAsync(new CalendarQuery { Start = from, End = to, IncludeUnmonitored = unmonitored ?? true, SubscriberId = subscriber }, ct));
        });

        // iCalendar feed for external calendar apps; authenticate with ?apikey=. userId limits it to that user's titles.
        group.MapGet("/feed.ics", async (int? pastDays, int? futureDays, bool? movies, bool? series, string? releaseTypes, bool? unmonitored, int? userId,
            CalendarService calendar, ISettingsService settings, CancellationToken ct) =>
        {
            var types = ParseReleaseTypes(releaseTypes);
            if (types is null) return Results.Problem("releaseTypes must be a comma-separated list of theatrical, digital and physical", statusCode: 400);
            var today = Today();
            var entries = await calendar.GetAsync(new CalendarQuery
            {
                Start = today.AddDays(-Math.Clamp(pastDays ?? 14, 0, 180)),
                End = today.AddDays(Math.Clamp(futureDays ?? 90, 0, CalendarService.MaxRangeDays - 180)),
                Movies = movies ?? true,
                Series = series ?? true,
                ReleaseTypes = types,
                IncludeUnmonitored = unmonitored ?? false,
                SubscriberId = userId,
            }, ct);
            var name = (await settings.GetAsync<GeneralSettings>(ct)).InstanceName;
            var ics = ICalendarWriter.Write(entries, string.IsNullOrWhiteSpace(name) ? "Krakarr" : name, DateTimeOffset.UtcNow);
            return Results.Text(ics, "text/calendar; charset=utf-8");
        });

        return app;
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Parses "theatrical,digital,physical" (also "cinema"); null when a value is unknown.</summary>
    internal static IReadOnlyCollection<ReleaseDateType>? ParseReleaseTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return MovieReleases.AllTypes;
        var result = new HashSet<ReleaseDateType>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("cinema", StringComparison.OrdinalIgnoreCase) || part.Equals("cinemas", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(ReleaseDateType.Theatrical);
            }
            else if (Enum.TryParse<ReleaseDateType>(part, ignoreCase: true, out var type) && Enum.IsDefined(type) && !int.TryParse(part, out _))
            {
                result.Add(type);
            }
            else
            {
                return null;
            }
        }

        return result;
    }
}
