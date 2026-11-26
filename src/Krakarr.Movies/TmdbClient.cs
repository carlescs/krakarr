using System.Globalization;
using System.Text.Json.Serialization;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Metadata;
using Krakarr.Data;

namespace Krakarr.Movies;

public sealed record MovieLookup
{
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? FanartUrl { get; init; }
    public int Runtime { get; init; }
    public string? Status { get; init; }
    /// <summary>Theatrical, digital and physical release dates per country (only from <see cref="ITmdbClient.GetMovieAsync"/>).</summary>
    public IReadOnlyList<MovieReleaseDate> ReleaseDates { get; init; } = [];
    /// <summary>TMDB collection the movie belongs to (only from <see cref="ITmdbClient.GetMovieAsync"/>).</summary>
    public CollectionLookup? Collection { get; init; }
    /// <summary>TMDB average user score (0-10), when known.</summary>
    public double? Rating { get; init; }
    /// <summary>Local movie id when the movie is already in the library.</summary>
    public int? MovieId { get; init; }
    /// <summary>ISO 639-1 code of the original language ("ja"), when known.</summary>
    public string? OriginalLanguage { get; init; }
    /// <summary>
    /// TMDB alternative titles and translated titles, in every language (only from <see cref="ITmdbClient.GetMovieAsync"/>).
    /// The caller picks the ones to keep (<see cref="Media.Parsing.AlternateTitles.Select"/>).
    /// </summary>
    public IReadOnlyList<AlternateTitleInfo> AlternateTitles { get; init; } = [];
}

public sealed record CollectionLookup(int TmdbId, string Name, string? PosterUrl, string? FanartUrl);

public interface ITmdbClient
{
    Task<IReadOnlyList<MovieLookup>> SearchAsync(string query, int? year, CancellationToken ct = default);
    Task<MovieLookup> GetMovieAsync(int tmdbId, CancellationToken ct = default);
    /// <summary>Finds a movie by IMDb id ("tt0133093"); null when TMDB does not know it.</summary>
    Task<MovieLookup?> FindByImdbAsync(string imdbId, CancellationToken ct = default);
}

/// <summary>Minimal TMDB v3 client. Accepts either a v3 API key or a v4 read access token.</summary>
public sealed class TmdbClient(TmdbApi api, ISettingsService settings) : ITmdbClient
{
    public async Task<IReadOnlyList<MovieLookup>> SearchAsync(string query, int? year, CancellationToken ct = default)
    {
        var path = $"3/search/movie?query={Uri.EscapeDataString(query)}&include_adult=false";
        if (year is { } y) path += $"&year={y.ToString(CultureInfo.InvariantCulture)}";
        var result = await GetAsync<TmdbSearchResponse>(path, ct);
        return result.Results.Select(Map).ToList();
    }

    public async Task<MovieLookup> GetMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        var movie = await GetAsync<TmdbMovie>($"3/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}?append_to_response=release_dates,alternative_titles,translations", ct);
        return Map(movie) with
        {
            ReleaseDates = MapReleaseDates(movie.ReleaseDates),
            Collection = MapCollection(movie.Collection),
            AlternateTitles = MapAlternateTitles(movie),
        };
    }

    public async Task<MovieLookup?> FindByImdbAsync(string imdbId, CancellationToken ct = default)
    {
        var result = await GetAsync<TmdbFindResponse>($"3/find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id", ct);
        var first = result.MovieResults.FirstOrDefault();
        return first is null ? null : await GetMovieAsync(first.Id, ct);
    }

    private async Task<T> GetAsync<T>(string relative, CancellationToken ct) =>
        await api.GetAsync<T>(await settings.GetAsync<MetadataSettings>(ct), relative, ct);

    internal static MovieLookup Map(TmdbMovie m) => new()
    {
        TmdbId = m.Id,
        ImdbId = string.IsNullOrEmpty(m.ImdbId) ? null : m.ImdbId,
        Title = m.Title ?? m.OriginalTitle ?? $"TMDB {m.Id}",
        OriginalTitle = m.OriginalTitle,
        Year = DateOnly.TryParse(m.ReleaseDate, CultureInfo.InvariantCulture, out var d) ? d.Year : 0,
        Overview = m.Overview,
        PosterUrl = TmdbApi.Image(m.PosterPath, "w500"),
        FanartUrl = TmdbApi.Image(m.BackdropPath, "original"),
        Runtime = m.Runtime ?? 0,
        Status = m.Status,
        Rating = m.VoteAverage is > 0 ? Math.Round(m.VoteAverage.Value, 1) : null,
        OriginalLanguage = string.IsNullOrWhiteSpace(m.OriginalLanguage) ? null : m.OriginalLanguage,
    };

    /// <summary>Original title (in its language), translated titles, then alternative titles (AKAs), with their language.</summary>
    internal static List<AlternateTitleInfo> MapAlternateTitles(TmdbMovie m)
    {
        var list = new List<AlternateTitleInfo>();
        if (!string.IsNullOrWhiteSpace(m.OriginalTitle))
        {
            list.Add(new AlternateTitleInfo(m.OriginalTitle, Media.Parsing.AlternateTitles.LanguageFor(m.OriginalLanguage, null)));
        }

        foreach (var t in m.Translations?.Translations ?? [])
        {
            if (string.IsNullOrWhiteSpace(t.Data?.Title)) continue;
            list.Add(new AlternateTitleInfo(t.Data.Title, Media.Parsing.AlternateTitles.LanguageFor(t.Language, t.Country)));
        }

        foreach (var t in m.AlternativeTitles?.Titles ?? [])
        {
            if (string.IsNullOrWhiteSpace(t.Title)) continue;
            list.Add(new AlternateTitleInfo(t.Title, Media.Parsing.AlternateTitles.LanguageFor(null, t.Country)));
        }

        return list;
    }

    internal static CollectionLookup? MapCollection(TmdbCollection? c) =>
        c is { Id: > 0, Name: { Length: > 0 } name }
            ? new CollectionLookup(c.Id, name, TmdbApi.Image(c.PosterPath, "w500"), TmdbApi.Image(c.BackdropPath, "original"))
            : null;

    /// <summary>
    /// Maps TMDB release types to Krakarr's: limited (2) and wide (3) theatrical releases count as cinema, 4 is digital and
    /// 5 physical. Premieres (1) and TV airings (6) are ignored.
    /// </summary>
    internal static List<MovieReleaseDate> MapReleaseDates(TmdbReleaseDates? releases)
    {
        var list = new List<MovieReleaseDate>();
        foreach (var country in releases?.Results ?? [])
        {
            if (string.IsNullOrWhiteSpace(country.Country)) continue;
            foreach (var r in country.ReleaseDates ?? [])
            {
                ReleaseDateType? type = r.Type switch
                {
                    2 or 3 => ReleaseDateType.Theatrical,
                    4 => ReleaseDateType.Digital,
                    5 => ReleaseDateType.Physical,
                    _ => null,
                };
                if (type is null || r.ReleaseDate is not { Length: >= 10 } raw) continue;
                if (DateOnly.TryParseExact(raw[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    list.Add(new MovieReleaseDate(country.Country, type.Value, date));
                }
            }
        }

        return MovieReleases.Normalize(list);
    }

    private sealed record TmdbSearchResponse([property: JsonPropertyName("results")] List<TmdbMovie> Results);

    private sealed record TmdbFindResponse([property: JsonPropertyName("movie_results")] List<TmdbMovie> MovieResults);

    internal sealed record TmdbMovie
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("imdb_id")] public string? ImdbId { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("original_title")] public string? OriginalTitle { get; init; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }
        [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; }
        [JsonPropertyName("runtime")] public int? Runtime { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("vote_average")] public double? VoteAverage { get; init; }
        [JsonPropertyName("release_dates")] public TmdbReleaseDates? ReleaseDates { get; init; }
        [JsonPropertyName("belongs_to_collection")] public TmdbCollection? Collection { get; init; }
        [JsonPropertyName("tagline")] public string? Tagline { get; init; }
        [JsonPropertyName("genres")] public List<TmdbNamed>? Genres { get; init; }
        [JsonPropertyName("credits")] public TmdbCreditList? Credits { get; init; }
        [JsonPropertyName("original_language")] public string? OriginalLanguage { get; init; }
        [JsonPropertyName("alternative_titles")] public TmdbAlternativeTitles? AlternativeTitles { get; init; }
        [JsonPropertyName("translations")] public TmdbTranslations? Translations { get; init; }
    }

    internal sealed record TmdbAlternativeTitles
    {
        [JsonPropertyName("titles")] public List<TmdbAlternativeTitle>? Titles { get; init; }
    }

    internal sealed record TmdbAlternativeTitle
    {
        [JsonPropertyName("iso_3166_1")] public string? Country { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
    }

    internal sealed record TmdbTranslations
    {
        [JsonPropertyName("translations")] public List<TmdbTranslation>? Translations { get; init; }
    }

    internal sealed record TmdbTranslation
    {
        [JsonPropertyName("iso_3166_1")] public string? Country { get; init; }
        [JsonPropertyName("iso_639_1")] public string? Language { get; init; }
        [JsonPropertyName("data")] public TmdbTranslationData? Data { get; init; }
    }

    internal sealed record TmdbTranslationData
    {
        [JsonPropertyName("title")] public string? Title { get; init; }
    }

    internal sealed record TmdbCollection
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }
        [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; }
    }

    internal sealed record TmdbReleaseDates
    {
        [JsonPropertyName("results")] public List<TmdbCountryReleases>? Results { get; init; }
    }

    internal sealed record TmdbCountryReleases
    {
        [JsonPropertyName("iso_3166_1")] public string? Country { get; init; }
        [JsonPropertyName("release_dates")] public List<TmdbRelease>? ReleaseDates { get; init; }
    }

    internal sealed record TmdbRelease
    {
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
        [JsonPropertyName("type")] public int Type { get; init; }
    }
}
