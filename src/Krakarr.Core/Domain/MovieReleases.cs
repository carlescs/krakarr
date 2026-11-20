namespace Krakarr.Core.Domain;

/// <summary>When a movie becomes eligible for automatic searches (Radarr's "minimum availability").</summary>
public enum MinimumAvailability
{
    /// <summary>Search as soon as the movie is added.</summary>
    Announced = 0,
    /// <summary>Search once the movie is in cinemas.</summary>
    InCinemas = 1,
    /// <summary>Search once the movie is out on digital or physical media.</summary>
    Released = 2,
}

public enum ReleaseDateType
{
    Theatrical = 1,
    Digital = 2,
    Physical = 3,
}

/// <summary>A movie release date in one country (ISO 3166-1 alpha-2, upper case).</summary>
public sealed record MovieReleaseDate(string Country, ReleaseDateType Type, DateOnly Date);

/// <summary>Release-date rules shared by availability checks, the calendar and the movie DTO.</summary>
public static class MovieReleases
{
    /// <summary>With only a cinema date known, a movie counts as released this many days after it.</summary>
    public const int AssumedReleaseAfterCinemaDays = 90;

    public static IReadOnlyList<ReleaseDateType> AllTypes { get; } = [ReleaseDateType.Theatrical, ReleaseDateType.Digital, ReleaseDateType.Physical];

    /// <summary>The earliest date of a type in any country.</summary>
    public static MovieReleaseDate? Earliest(IEnumerable<MovieReleaseDate> dates, ReleaseDateType type) =>
        dates.Where(d => d.Type == type).OrderBy(d => d.Date).ThenBy(d => d.Country, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>The date of a type in one country, or null when that country has none (or no region is given).</summary>
    public static MovieReleaseDate? ForRegion(IEnumerable<MovieReleaseDate> dates, ReleaseDateType type, string? region)
    {
        if (NormalizeRegion(region) is not { } r) return null;
        return dates.Where(d => d.Type == type && d.Country == r).OrderBy(d => d.Date).FirstOrDefault();
    }

    /// <summary>The date the calendar shows: the region's date when it has one, otherwise the earliest worldwide.</summary>
    public static MovieReleaseDate? Resolve(IEnumerable<MovieReleaseDate> dates, ReleaseDateType type, string? region)
    {
        var list = dates as IReadOnlyCollection<MovieReleaseDate> ?? dates.ToList();
        return ForRegion(list, type, region) ?? Earliest(list, type);
    }

    public static string? NormalizeRegion(string? region) =>
        string.IsNullOrWhiteSpace(region) ? null : region.Trim().ToUpperInvariant();

    /// <summary>Keeps one (the earliest) date per country and type, sorted.</summary>
    public static List<MovieReleaseDate> Normalize(IEnumerable<MovieReleaseDate> dates) =>
        dates.Where(d => !string.IsNullOrWhiteSpace(d.Country))
            .Select(d => d with { Country = d.Country.Trim().ToUpperInvariant() })
            .GroupBy(d => (d.Country, d.Type))
            .Select(g => g.MinBy(d => d.Date)!)
            .OrderBy(d => d.Date).ThenBy(d => d.Type).ThenBy(d => d.Country, StringComparer.Ordinal)
            .ToList();

    /// <summary>Copies the release dates onto the movie and refreshes the earliest-date columns and status.</summary>
    /// <param name="fallbackStatus">Status used when no release dates are known (from the metadata source).</param>
    public static void Apply(Movie movie, IEnumerable<MovieReleaseDate> dates, MovieStatus fallbackStatus, DateOnly today)
    {
        movie.ReleaseDates = Normalize(dates);
        movie.InCinemas = Earliest(movie.ReleaseDates, ReleaseDateType.Theatrical)?.Date;
        movie.DigitalRelease = Earliest(movie.ReleaseDates, ReleaseDateType.Digital)?.Date;
        movie.PhysicalRelease = Earliest(movie.ReleaseDates, ReleaseDateType.Physical)?.Date;
        movie.Status = fallbackStatus;
        movie.Status = CurrentStatus(movie, today);
    }

    /// <summary>
    /// Status on <paramref name="today"/> from the earliest worldwide dates: released once out on digital or physical media
    /// (or <see cref="AssumedReleaseAfterCinemaDays"/> after the cinema date when neither is known), in cinemas from the
    /// cinema date. Without any dates the stored status (from the metadata source) is used.
    /// </summary>
    public static MovieStatus CurrentStatus(Movie movie, DateOnly today)
    {
        if (movie.DigitalRelease <= today || movie.PhysicalRelease <= today) return MovieStatus.Released;
        if (movie.InCinemas is { } cinema)
        {
            if (movie.DigitalRelease is null && movie.PhysicalRelease is null && cinema.AddDays(AssumedReleaseAfterCinemaDays) <= today)
            {
                return MovieStatus.Released;
            }

            if (cinema <= today) return MovieStatus.InCinemas;
        }

        return movie.InCinemas is null && movie.DigitalRelease is null && movie.PhysicalRelease is null
            ? movie.Status
            : MovieStatus.Announced;
    }

    /// <summary>Whether the movie has reached its minimum availability, so automatic searches may run.</summary>
    public static bool IsAvailable(Movie movie, DateOnly today) => movie.MinimumAvailability switch
    {
        MinimumAvailability.Announced => true,
        MinimumAvailability.InCinemas => CurrentStatus(movie, today) >= MovieStatus.InCinemas,
        _ => CurrentStatus(movie, today) >= MovieStatus.Released,
    };
}
