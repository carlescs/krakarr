using System.Globalization;
using System.Text.Json.Serialization;
using Krakarr.Core.Configuration;

namespace Krakarr.Core.Metadata;

/// <summary>A movie or show a person worked on. <see cref="Kind"/> is "movie" or "series".</summary>
public sealed record PersonCredit
{
    public required string Kind { get; init; }
    public int TmdbId { get; init; }
    public required string Title { get; init; }
    public int Year { get; init; }
    public DateOnly? Date { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public double? Rating { get; init; }
    /// <summary>"Acting" for cast credits, otherwise the crew department ("Directing", "Writing"…).</summary>
    public required string Department { get; init; }
    /// <summary>Character(s) played, or the job(s) held.</summary>
    public string? Role { get; init; }
    /// <summary>Local movie id when the movie is in the library.</summary>
    public int? MovieId { get; init; }
    /// <summary>Local series id when the show is in the library.</summary>
    public int? SeriesId { get; init; }
    internal double Popularity { get; init; }
}

public sealed record PersonDetails
{
    public int TmdbId { get; init; }
    public required string Name { get; init; }
    public string? Biography { get; init; }
    public DateOnly? Birthday { get; init; }
    public DateOnly? Deathday { get; init; }
    public string? PlaceOfBirth { get; init; }
    public string? ProfileUrl { get; init; }
    public string? KnownForDepartment { get; init; }
    public string? ImdbId { get; init; }
    public IReadOnlyList<PersonCredit> Credits { get; init; } = [];
}

/// <summary>People (actors, directors…) and their filmography, from TMDB.</summary>
public interface IPersonMetadata
{
    Task<PersonDetails> GetPersonAsync(MetadataSettings meta, int tmdbId, CancellationToken ct = default);
}

public sealed class TmdbPeople(TmdbApi api) : IPersonMetadata
{
    /// <summary>Upper bound on the credits returned for prolific people.</summary>
    public const int MaxCredits = 300;

    /// <summary>TMDB TV genres for talk shows and news, which flood actors' credits with guest appearances.</summary>
    private static readonly int[] NoiseGenres = [10763, 10767];

    public async Task<PersonDetails> GetPersonAsync(MetadataSettings meta, int tmdbId, CancellationToken ct = default)
    {
        var p = await api.GetAsync<TmdbPerson>(meta, $"3/person/{tmdbId.ToString(CultureInfo.InvariantCulture)}?append_to_response=combined_credits", ct);
        return new PersonDetails
        {
            TmdbId = p.Id,
            Name = Blank(p.Name) ?? $"TMDB {p.Id}",
            Biography = Blank(p.Biography),
            Birthday = Date(p.Birthday),
            Deathday = Date(p.Deathday),
            PlaceOfBirth = Blank(p.PlaceOfBirth),
            ProfileUrl = TmdbApi.Image(p.ProfilePath, "h632"),
            KnownForDepartment = Blank(p.KnownForDepartment),
            ImdbId = Blank(p.ImdbId),
            Credits = MapCredits(p.CombinedCredits),
        };
    }

    /// <summary>One entry per title and department, newest first (announced titles without a date on top).</summary>
    internal static IReadOnlyList<PersonCredit> MapCredits(TmdbCombinedCredits? credits)
    {
        if (credits is null) return [];
        var entries = credits.Cast.Select(c => (Credit: c, Department: "Acting", Role: Blank(c.Character)))
            .Concat(credits.Crew.Select(c => (Credit: c, Department: Blank(c.Department) ?? "Crew", Role: Blank(c.Job))))
            .Where(e => e.Credit.Id > 0 && e.Credit.MediaType is "movie" or "tv")
            .Where(e => e.Credit.MediaType == "movie" || !e.Credit.GenreIds.Any(g => NoiseGenres.Contains(g)));

        return entries
            .GroupBy(e => (e.Credit.MediaType, e.Credit.Id, e.Department))
            .Select(g =>
            {
                var c = g.First().Credit;
                var date = Date(c.MediaType == "movie" ? c.ReleaseDate : c.FirstAirDate);
                var roles = string.Join(", ", g.Select(e => e.Role).Where(r => r is not null).Distinct());
                return new PersonCredit
                {
                    Kind = c.MediaType == "movie" ? "movie" : "series",
                    TmdbId = c.Id,
                    Title = Blank(c.Title) ?? Blank(c.Name) ?? Blank(c.OriginalTitle) ?? Blank(c.OriginalName) ?? $"TMDB {c.Id}",
                    Year = date?.Year ?? 0,
                    Date = date,
                    Overview = Blank(c.Overview),
                    PosterUrl = TmdbApi.Image(c.PosterPath, "w500"),
                    Rating = c.VoteAverage is > 0 ? Math.Round(c.VoteAverage.Value, 1) : null,
                    Department = g.Key.Department,
                    Role = roles.Length == 0 ? null : roles,
                    Popularity = c.Popularity ?? 0,
                };
            })
            // Keep the best known titles when capping, then order the survivors by date.
            .OrderByDescending(c => c.Popularity)
            .Take(MaxCredits)
            .OrderBy(c => c.Date is null ? 0 : 1)
            .ThenByDescending(c => c.Date)
            .ThenByDescending(c => c.Popularity)
            .ToList();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static DateOnly? Date(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    internal sealed record TmdbPerson
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("biography")] public string? Biography { get; init; }
        [JsonPropertyName("birthday")] public string? Birthday { get; init; }
        [JsonPropertyName("deathday")] public string? Deathday { get; init; }
        [JsonPropertyName("place_of_birth")] public string? PlaceOfBirth { get; init; }
        [JsonPropertyName("profile_path")] public string? ProfilePath { get; init; }
        [JsonPropertyName("known_for_department")] public string? KnownForDepartment { get; init; }
        [JsonPropertyName("imdb_id")] public string? ImdbId { get; init; }
        [JsonPropertyName("combined_credits")] public TmdbCombinedCredits? CombinedCredits { get; init; }
    }

    internal sealed record TmdbCombinedCredits
    {
        [JsonPropertyName("cast")] public List<TmdbPersonCredit> Cast { get; init; } = [];
        [JsonPropertyName("crew")] public List<TmdbPersonCredit> Crew { get; init; } = [];
    }

    internal sealed record TmdbPersonCredit
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("media_type")] public string? MediaType { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("original_title")] public string? OriginalTitle { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("original_name")] public string? OriginalName { get; init; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
        [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }
        [JsonPropertyName("vote_average")] public double? VoteAverage { get; init; }
        [JsonPropertyName("popularity")] public double? Popularity { get; init; }
        [JsonPropertyName("genre_ids")] public List<int> GenreIds { get; init; } = [];
        [JsonPropertyName("character")] public string? Character { get; init; }
        [JsonPropertyName("job")] public string? Job { get; init; }
        [JsonPropertyName("department")] public string? Department { get; init; }
    }
}
