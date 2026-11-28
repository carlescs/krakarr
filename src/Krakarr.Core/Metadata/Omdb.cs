using Krakarr.Core.Domain;

namespace Krakarr.Core.Metadata;

/// <summary>
/// What OMDb (omdbapi.com) adds to TMDB/TheTVDB metadata for one title, looked up by IMDb id: critic and audience scores
/// plus a few facts TMDB doesn't give in one place. Each value is null when OMDb has none. OMDb is English only.
/// </summary>
/// <param name="Rated">Content rating as OMDb gives it ("PG-13", "TV-MA").</param>
/// <param name="Awards">Awards summary ("Won 4 Oscars. 42 wins &amp; 51 nominations total").</param>
/// <param name="BoxOffice">US box office ("$172,076,928"); movies only.</param>
/// <param name="Plot">English plot; shown only when the metadata source has no overview.</param>
public sealed record OmdbInfo(
    double? Imdb,
    long? ImdbVotes,
    int? RottenTomatoes,
    int? Metacritic,
    string? RottenTomatoesUrl,
    string? Rated = null,
    string? Awards = null,
    string? BoxOffice = null,
    string? Director = null,
    string? Writer = null,
    string? Plot = null)
{
    public const int RatedMaxLength = 32;
    public const int AwardsMaxLength = 256;
    public const int BoxOfficeMaxLength = 32;
    public const int PeopleMaxLength = 512;
    public const int PlotMaxLength = 2048;
    public const int UrlMaxLength = 512;

    /// <summary>The stored copy of <paramref name="item"/>, or null when OMDb hasn't been asked about it yet.</summary>
    public static OmdbInfo? From(IOmdbFields item) => item.OmdbUpdated is null ? null : new(
        item.ImdbRating, item.ImdbVotes, item.RottenTomatoes, item.Metacritic, item.RottenTomatoesUrl,
        item.Certification, item.Awards, item.BoxOffice, item.Director, item.Writer, item.OmdbPlot);

    /// <summary>Stores <paramref name="info"/> (null = OMDb doesn't know the title) on <paramref name="item"/>, cut to the column lengths.</summary>
    public static void Apply(IOmdbFields item, OmdbInfo? info, DateTimeOffset now)
    {
        item.ImdbRating = info?.Imdb;
        item.ImdbVotes = info?.ImdbVotes;
        item.RottenTomatoes = info?.RottenTomatoes;
        item.Metacritic = info?.Metacritic;
        item.RottenTomatoesUrl = Cut(info?.RottenTomatoesUrl, UrlMaxLength);
        item.Certification = Cut(info?.Rated, RatedMaxLength);
        item.Awards = Cut(info?.Awards, AwardsMaxLength);
        item.BoxOffice = Cut(info?.BoxOffice, BoxOfficeMaxLength);
        item.Director = Cut(info?.Director, PeopleMaxLength);
        item.Writer = Cut(info?.Writer, PeopleMaxLength);
        item.OmdbPlot = Cut(info?.Plot, PlotMaxLength);
        item.OmdbUpdated = now;
    }

    /// <summary>Clears what was stored, so the OMDb task looks the title up again soon (e.g. its IMDb id changed).</summary>
    public static void Reset(IOmdbFields item)
    {
        Apply(item, null, default);
        item.OmdbUpdated = null;
    }

    public static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
