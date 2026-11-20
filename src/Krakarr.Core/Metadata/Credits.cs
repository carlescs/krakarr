using System.Text.Json.Serialization;

namespace Krakarr.Core.Metadata;

/// <summary>An actor and the character(s) they play; <see cref="Episodes"/> is only set for series.</summary>
public sealed record CastMember(int TmdbId, string Name, string? Character, string? ProfileUrl, int? Episodes = null);

/// <summary>A key crew member with their jobs on the title ("Director, Screenplay").</summary>
public sealed record CrewMember(int TmdbId, string Name, string Jobs, string? ProfileUrl);

/// <summary>Billed cast and key crew of a movie or show, trimmed to what a detail page shows.</summary>
public sealed record Credits(IReadOnlyList<CastMember> Cast, IReadOnlyList<CrewMember> Crew)
{
    public static readonly Credits Empty = new([], []);
}

/// <summary>Maps TMDB "credits" (movies) and "aggregate_credits" (TV) to <see cref="Credits"/>.</summary>
public static class TmdbCredits
{
    /// <summary>Billed cast members kept per title; TMDB lists hundreds for long-running shows.</summary>
    public const int MaxCast = 30;

    /// <summary>Crew jobs worth showing on a detail page, in display order.</summary>
    private static readonly string[] KeyJobs =
    [
        "Creator", "Director", "Screenplay", "Writer", "Story", "Novel", "Characters", "Original Music Composer", "Director of Photography", "Producer",
    ];

    public static Credits Map(TmdbCreditList? credits, IEnumerable<CrewMember>? creators = null)
    {
        if (credits is null && creators is null) return Credits.Empty;

        var cast = (credits?.Cast ?? [])
            .Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name))
            .OrderBy(c => c.Order ?? int.MaxValue)
            .ThenByDescending(c => c.TotalEpisodeCount ?? 0)
            .DistinctBy(c => c.Id)
            .Take(MaxCast)
            .Select(c => new CastMember(c.Id, c.Name!, Character(c), TmdbApi.Image(c.ProfilePath, "w185"), c.TotalEpisodeCount))
            .ToList();

        // One entry per person, with their key jobs joined in a stable order (creators first for shows).
        var jobs = new List<(int Id, string Name, string Job, string? Profile)>();
        foreach (var c in creators ?? [])
        {
            jobs.Add((c.TmdbId, c.Name, "Creator", c.ProfileUrl));
        }

        foreach (var c in credits?.Crew ?? [])
        {
            if (c.Id <= 0 || string.IsNullOrWhiteSpace(c.Name)) continue;
            IEnumerable<string?> names = c.Jobs is { Count: > 0 } list ? list.Select(j => j.Job) : [c.Job];
            foreach (var job in names)
            {
                if (job is not null && Array.IndexOf(KeyJobs, job) >= 0) jobs.Add((c.Id, c.Name!, job, TmdbApi.Image(c.ProfilePath, "w185")));
            }
        }

        var crew = jobs
            .GroupBy(j => j.Id)
            .Select(g => (
                Rank: g.Min(j => Array.IndexOf(KeyJobs, j.Job)),
                Member: new CrewMember(g.Key, g.First().Name,
                    string.Join(", ", g.Select(j => j.Job).Distinct().OrderBy(j => Array.IndexOf(KeyJobs, j))),
                    g.Select(j => j.Profile).FirstOrDefault(p => p is not null))))
            .OrderBy(x => x.Rank)
            .Take(MaxCast)
            .Select(x => x.Member)
            .ToList();

        return new Credits(cast, crew);
    }

    private static string? Character(TmdbCastCredit c)
    {
        var names = c.Roles is { Count: > 0 } roles ? roles.Select(r => r.Character) : [c.Character];
        var joined = string.Join(" / ", names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
        return joined.Length == 0 ? null : joined;
    }
}

/// <summary>TMDB credits envelope, shared by movie "credits" and TV "aggregate_credits".</summary>
public sealed record TmdbCreditList
{
    [JsonPropertyName("cast")] public List<TmdbCastCredit> Cast { get; init; } = [];
    [JsonPropertyName("crew")] public List<TmdbCrewCredit> Crew { get; init; } = [];
}

public sealed record TmdbCastCredit
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("character")] public string? Character { get; init; }
    [JsonPropertyName("profile_path")] public string? ProfilePath { get; init; }
    [JsonPropertyName("order")] public int? Order { get; init; }
    /// <summary>TV aggregate credits: one entry per character played.</summary>
    [JsonPropertyName("roles")] public List<TmdbRole>? Roles { get; init; }
    [JsonPropertyName("total_episode_count")] public int? TotalEpisodeCount { get; init; }
}

public sealed record TmdbCrewCredit
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("job")] public string? Job { get; init; }
    [JsonPropertyName("profile_path")] public string? ProfilePath { get; init; }
    /// <summary>TV aggregate credits: one entry per job held.</summary>
    [JsonPropertyName("jobs")] public List<TmdbJob>? Jobs { get; init; }
}

public sealed record TmdbRole([property: JsonPropertyName("character")] string? Character);

public sealed record TmdbJob([property: JsonPropertyName("job")] string? Job);

/// <summary>Any TMDB object reduced to its name (genres, networks).</summary>
public sealed record TmdbNamed([property: JsonPropertyName("name")] string? Name);
