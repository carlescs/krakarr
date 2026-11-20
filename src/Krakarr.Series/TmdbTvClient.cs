using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Krakarr.Core.Configuration;
using Krakarr.Core.Metadata;
using Krakarr.Data;

namespace Krakarr.Series;

public interface ITmdbTvClient
{
    Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default);
    Task<SeriesInfo> GetSeriesAsync(int tmdbId, CancellationToken ct = default);
    /// <summary>Finds a show by its TheTVDB id (library folders tagged {tvdb-81189}, series added before TMDB); null when unknown.</summary>
    Task<SeriesLookup?> FindByTvdbAsync(int tvdbId, CancellationToken ct = default);
    /// <summary>The show's TheTVDB id from TMDB's external ids; null when TMDB has none.</summary>
    Task<int?> GetTvdbIdAsync(int tmdbId, CancellationToken ct = default);
}

/// <summary>TMDB TV client: search, show details with external ids, and every season's episodes.</summary>
public sealed class TmdbTvClient(TmdbApi api, ISettingsService settings) : ITmdbTvClient
{
    /// <summary>TMDB caps append_to_response at 20 items per request.</summary>
    private const int SeasonsPerRequest = 20;

    public async Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var result = await api.GetAsync<SearchResponse>(meta, $"3/search/tv?query={Uri.EscapeDataString(query)}&include_adult=false", ct);
        return result.Results.Select(Map).ToList();
    }

    public async Task<SeriesLookup?> FindByTvdbAsync(int tvdbId, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var result = await api.GetAsync<FindResponse>(meta, $"3/find/{tvdbId.ToString(CultureInfo.InvariantCulture)}?external_source=tvdb_id", ct);
        return result.TvResults.Select(Map).FirstOrDefault();
    }

    public async Task<int?> GetTvdbIdAsync(int tmdbId, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var ids = await api.GetAsync<ExternalIds>(meta, $"3/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}/external_ids", ct);
        return ids.TvdbId is > 0 ? ids.TvdbId : null;
    }

    public async Task<SeriesInfo> GetSeriesAsync(int tmdbId, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var id = tmdbId.ToString(CultureInfo.InvariantCulture);
        var show = await api.GetAsync<TvShow>(meta, $"3/tv/{id}?append_to_response=external_ids", ct);

        var seasonNumbers = show.Seasons.Select(s => s.SeasonNumber).Distinct().Order().ToList();
        var episodes = new List<EpisodeInfo>();
        foreach (var chunk in seasonNumbers.Chunk(SeasonsPerRequest))
        {
            var append = string.Join(',', chunk.Select(n => $"season/{n.ToString(CultureInfo.InvariantCulture)}"));
            var doc = await api.GetAsync<Dictionary<string, JsonElement>>(meta, $"3/tv/{id}?append_to_response={append}", ct);
            foreach (var n in chunk)
            {
                if (!doc.TryGetValue($"season/{n.ToString(CultureInfo.InvariantCulture)}", out var element) || element.ValueKind != JsonValueKind.Object) continue;
                var season = element.Deserialize<TvSeason>();
                if (season is null) continue;
                episodes.AddRange(season.Episodes.Select(e => new EpisodeInfo
                {
                    TmdbId = e.Id,
                    SeasonNumber = e.SeasonNumber ?? n,
                    EpisodeNumber = e.EpisodeNumber,
                    Title = Blank(e.Name),
                    Overview = Blank(e.Overview),
                    AirDate = Date(e.AirDate),
                    Runtime = e.Runtime ?? 0,
                }));
            }
        }

        episodes = WithAbsoluteNumbers(episodes);
        var firstAired = Date(show.FirstAirDate);
        var runtime = show.EpisodeRunTime.FirstOrDefault(r => r > 0);
        if (runtime == 0)
        {
            var known = episodes.Where(e => e.Runtime > 0).Select(e => e.Runtime).Order().ToList();
            runtime = known.Count > 0 ? known[known.Count / 2] : 0;
        }

        return new SeriesInfo
        {
            TmdbId = show.Id,
            TvdbId = show.ExternalIds?.TvdbId is > 0 and var tvdb ? tvdb : null,
            Title = Blank(show.Name) ?? Blank(show.OriginalName) ?? $"TMDB {show.Id}",
            Year = firstAired?.Year ?? 0,
            Overview = Blank(show.Overview),
            PosterUrl = TmdbApi.Image(show.PosterPath, "w500"),
            FanartUrl = TmdbApi.Image(show.BackdropPath, "original"),
            Network = show.Networks.Select(n => Blank(n.Name)).FirstOrDefault(n => n is not null),
            Status = NormalizeStatus(show.Status),
            Runtime = runtime,
            ImdbId = Blank(show.ExternalIds?.ImdbId),
            FirstAired = firstAired,
            Episodes = episodes,
        };
    }

    /// <summary>TMDB has no absolute numbering; number regular episodes in season/episode order (specials get none).</summary>
    private static List<EpisodeInfo> WithAbsoluteNumbers(List<EpisodeInfo> episodes)
    {
        var absolute = 0;
        return episodes
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .Select(e => e.SeasonNumber > 0 ? e with { AbsoluteNumber = ++absolute } : e)
            .ToList();
    }

    /// <summary>TMDB statuses: Returning Series, In Production, Planned, Pilot, Ended, Canceled (TheTVDB: Continuing, Ended, Upcoming).</summary>
    public static string NormalizeStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "ended" or "canceled" or "cancelled" => "Ended",
        "planned" or "in production" or "pilot" or "upcoming" => "Upcoming",
        _ => "Continuing",
    };

    private static SeriesLookup Map(TvResult r)
    {
        var aired = Date(r.FirstAirDate);
        return new SeriesLookup
        {
            TmdbId = r.Id,
            Title = Blank(r.Name) ?? Blank(r.OriginalName) ?? $"TMDB {r.Id}",
            Year = aired?.Year ?? 0,
            Overview = Blank(r.Overview),
            PosterUrl = TmdbApi.Image(r.PosterPath, "w500"),
        };
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static DateOnly? Date(string? s) =>
        DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;

    private sealed record SearchResponse([property: JsonPropertyName("results")] List<TvResult> Results);

    private sealed record FindResponse([property: JsonPropertyName("tv_results")] List<TvResult> TvResults);

    private sealed record TvResult
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("original_name")] public string? OriginalName { get; init; }
        [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }
    }

    private sealed record TvShow
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("original_name")] public string? OriginalName { get; init; }
        [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }
        [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("episode_run_time")] public List<int> EpisodeRunTime { get; init; } = [];
        [JsonPropertyName("networks")] public List<Named> Networks { get; init; } = [];
        [JsonPropertyName("seasons")] public List<SeasonSummary> Seasons { get; init; } = [];
        [JsonPropertyName("external_ids")] public ExternalIds? ExternalIds { get; init; }
    }

    private sealed record Named([property: JsonPropertyName("name")] string? Name);

    private sealed record SeasonSummary([property: JsonPropertyName("season_number")] int SeasonNumber);

    private sealed record ExternalIds
    {
        [JsonPropertyName("imdb_id")] public string? ImdbId { get; init; }
        [JsonPropertyName("tvdb_id")] public int? TvdbId { get; init; }
    }

    private sealed record TvSeason
    {
        [JsonPropertyName("episodes")] public List<TvEpisode> Episodes { get; init; } = [];
    }

    private sealed record TvEpisode
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("season_number")] public int? SeasonNumber { get; init; }
        [JsonPropertyName("episode_number")] public int EpisodeNumber { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("air_date")] public string? AirDate { get; init; }
        [JsonPropertyName("runtime")] public int? Runtime { get; init; }
    }
}
