using System.Globalization;
using System.Text.Json.Serialization;
using Krakarr.Core.Configuration;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Series;

public enum SeriesList { Trending, Popular, Upcoming, TopRated, AiringToday }

public sealed record SeasonSummary(int SeasonNumber, string? Name, int EpisodeCount, DateOnly? AirDate, string? PosterUrl);

/// <summary>A show's TMDB page: the lookup plus what a detail page shows for shows outside the library.</summary>
public sealed record SeriesDetails
{
    public required SeriesLookup Show { get; init; }
    public string? FanartUrl { get; init; }
    public string? ImdbId { get; init; }
    public string? Tagline { get; init; }
    public DateOnly? FirstAired { get; init; }
    public DateOnly? LastAired { get; init; }
    public int Runtime { get; init; }
    public int EpisodeCount { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<SeasonSummary> Seasons { get; init; } = [];
    public IReadOnlyList<CastMember> Cast { get; init; } = [];
    public IReadOnlyList<CrewMember> Crew { get; init; } = [];
}

/// <summary>What TMDB knows about one episode beyond what the library stores: still, score, guest stars, crew and ids.</summary>
public sealed record EpisodeDetails
{
    /// <summary>The show's TMDB id (resolved for shows added from TheTVDB), for linking to the episode's TMDB page.</summary>
    public int ShowTmdbId { get; init; }
    public int? TmdbId { get; init; }
    public int? TvdbId { get; init; }
    public string? ImdbId { get; init; }
    public string? Title { get; init; }
    public string? Overview { get; init; }
    public DateOnly? AirDate { get; init; }
    public int Runtime { get; init; }
    public string? StillUrl { get; init; }
    /// <summary>TMDB average user score (0-10); null when nobody has voted.</summary>
    public double? Rating { get; init; }
    public int VoteCount { get; init; }
    public IReadOnlyList<CastMember> GuestStars { get; init; } = [];
    public IReadOnlyList<CrewMember> Crew { get; init; } = [];
}

/// <summary>
/// TMDB TV discovery: curated lists plus per-show recommendations and similar shows. It needs a TMDB key whichever
/// series metadata source is selected; results carry TMDB ids, which adding a series resolves for TheTVDB.
/// </summary>
public interface ISeriesDiscovery
{
    Task<DiscoverPage<SeriesLookup>> ListAsync(SeriesList list, int page, CancellationToken ct = default);
    /// <summary>Shows tagged with every one of the TMDB keywords, most popular first.</summary>
    Task<DiscoverPage<SeriesLookup>> KeywordsAsync(IReadOnlyList<int> keywordIds, int page, CancellationToken ct = default);
    Task<IReadOnlyList<SeriesLookup>> RecommendationsAsync(SeriesIds ids, CancellationToken ct = default);
    Task<IReadOnlyList<SeriesLookup>> SimilarAsync(SeriesIds ids, CancellationToken ct = default);
    /// <summary>Recommendations for several shows at once (unknown shows yield an empty list), in the order given.</summary>
    Task<IReadOnlyList<IReadOnlyList<SeriesLookup>>> RecommendationsAsync(IReadOnlyList<SeriesIds> shows, CancellationToken ct = default);
    /// <summary>Details with seasons, cast and key crew, for any TMDB show.</summary>
    Task<SeriesDetails> DetailsAsync(int tmdbId, CancellationToken ct = default);
    /// <summary>Cast across all seasons plus creators; empty when the show has no TMDB match.</summary>
    Task<Credits> CreditsAsync(SeriesIds ids, CancellationToken ct = default);
    /// <summary>One episode by season and episode number; null when the show or the episode isn't on TMDB.</summary>
    Task<EpisodeDetails?> EpisodeDetailsAsync(SeriesIds ids, int season, int episode, CancellationToken ct = default);
}

public sealed class TmdbSeriesDiscovery(TmdbApi api, ISettingsService settings, TimeProvider time) : ISeriesDiscovery
{
    public async Task<DiscoverPage<SeriesLookup>> ListAsync(SeriesList list, int page, CancellationToken ct = default)
    {
        var p = Math.Clamp(page, 1, 500).ToString(CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = list switch
        {
            SeriesList.Trending => $"3/trending/tv/week?page={p}",
            SeriesList.Popular => $"3/tv/popular?page={p}",
            SeriesList.TopRated => $"3/tv/top_rated?page={p}",
            SeriesList.AiringToday => $"3/tv/airing_today?page={p}",
            // Shows premiering from today on, most anticipated first.
            _ => $"3/discover/tv?page={p}&first_air_date.gte={today}&sort_by=popularity.desc&include_adult=false",
        };
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var result = await api.GetAsync<TmdbPaged<TmdbTvClient.TvResult>>(meta, path, ct);
        return new DiscoverPage<SeriesLookup>(result.Results.Select(TmdbTvClient.Map).ToList(), result.Page, Math.Min(result.TotalPages, 500));
    }

    public async Task<DiscoverPage<SeriesLookup>> KeywordsAsync(IReadOnlyList<int> keywordIds, int page, CancellationToken ct = default)
    {
        var p = Math.Clamp(page, 1, 500).ToString(CultureInfo.InvariantCulture);
        var path = $"3/discover/tv?page={p}&with_keywords={KeywordIds.ToTmdb(keywordIds)}&sort_by=popularity.desc&include_adult=false";
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var result = await api.GetAsync<TmdbPaged<TmdbTvClient.TvResult>>(meta, path, ct);
        return new DiscoverPage<SeriesLookup>(result.Results.Select(TmdbTvClient.Map).ToList(), result.Page, Math.Min(result.TotalPages, 500));
    }

    public async Task<IReadOnlyList<SeriesLookup>> RecommendationsAsync(SeriesIds ids, CancellationToken ct = default) =>
        await RelatedAsync(await settings.GetAsync<MetadataSettings>(ct), ids, "recommendations", ct);

    public async Task<IReadOnlyList<SeriesLookup>> SimilarAsync(SeriesIds ids, CancellationToken ct = default) =>
        await RelatedAsync(await settings.GetAsync<MetadataSettings>(ct), ids, "similar", ct);

    public async Task<IReadOnlyList<IReadOnlyList<SeriesLookup>>> RecommendationsAsync(IReadOnlyList<SeriesIds> shows, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        return await Task.WhenAll(shows.Select(async ids =>
        {
            try
            {
                return await RelatedAsync(meta, ids, "recommendations", ct);
            }
            catch (MetadataException)
            {
                return (IReadOnlyList<SeriesLookup>)[];
            }
        }));
    }

    public async Task<SeriesDetails> DetailsAsync(int tmdbId, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var show = await api.GetAsync<TvDetails>(meta, $"3/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}?append_to_response=aggregate_credits,external_ids", ct);
        var firstAired = Date(show.FirstAirDate);
        var credits = TmdbCredits.Map(show.Credits, Creators(show.CreatedBy));
        return new SeriesDetails
        {
            Show = TmdbTvClient.Map(new TmdbTvClient.TvResult
            {
                Id = show.Id, Name = show.Name, OriginalName = show.OriginalName, FirstAirDate = show.FirstAirDate, Overview = show.Overview,
                PosterPath = show.PosterPath, VoteAverage = show.VoteAverage,
            }) with
            {
                TvdbId = show.ExternalIds?.TvdbId is > 0 and var tvdb ? tvdb : null,
                Network = show.Networks.Select(n => Blank(n.Name)).FirstOrDefault(n => n is not null),
                Status = TmdbTvClient.NormalizeStatus(show.Status),
            },
            FanartUrl = TmdbApi.Image(show.BackdropPath, "original"),
            ImdbId = Blank(show.ExternalIds?.ImdbId),
            Tagline = Blank(show.Tagline),
            FirstAired = firstAired,
            LastAired = Date(show.LastAirDate),
            Runtime = show.EpisodeRunTime.FirstOrDefault(r => r > 0),
            EpisodeCount = show.NumberOfEpisodes ?? 0,
            Genres = show.Genres.Select(g => Blank(g.Name)).OfType<string>().ToList(),
            Seasons = show.Seasons
                .OrderBy(s => s.SeasonNumber == 0 ? int.MaxValue : s.SeasonNumber)
                .Select(s => new SeasonSummary(s.SeasonNumber, Blank(s.Name), s.EpisodeCount ?? 0, Date(s.AirDate), TmdbApi.Image(s.PosterPath, "w342")))
                .ToList(),
            Cast = credits.Cast,
            Crew = credits.Crew,
        };
    }

    public async Task<Credits> CreditsAsync(SeriesIds ids, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        if (await ResolveTmdbIdAsync(meta, ids, ct) is not { } tmdbId) return Credits.Empty;
        // aggregate_credits spans every season; plain "credits" only lists the latest one.
        var show = await api.GetAsync<TvDetails>(meta, $"3/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}?append_to_response=aggregate_credits", ct);
        return TmdbCredits.Map(show.Credits, Creators(show.CreatedBy));
    }

    public async Task<EpisodeDetails?> EpisodeDetailsAsync(SeriesIds ids, int season, int episode, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        if (await ResolveTmdbIdAsync(meta, ids, ct) is not { } tmdbId) return null;
        TvEpisode e;
        try
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"3/tv/{tmdbId}/season/{season}/episode/{episode}?append_to_response=external_ids");
            e = await api.GetAsync<TvEpisode>(meta, path, ct);
        }
        catch (MetadataNotFoundException)
        {
            // TheTVDB and TMDB sometimes number seasons differently, so a library episode may have no TMDB twin.
            return null;
        }

        // Regular cast is on the series page; the episode shows who guest-starred and who made it.
        var credits = TmdbCredits.Map(new TmdbCreditList { Cast = e.GuestStars, Crew = e.Crew });
        return new EpisodeDetails
        {
            ShowTmdbId = tmdbId,
            TmdbId = e.Id > 0 ? e.Id : null,
            TvdbId = e.ExternalIds?.TvdbId is > 0 and var tvdb ? tvdb : null,
            ImdbId = Blank(e.ExternalIds?.ImdbId),
            Title = Blank(e.Name),
            Overview = Blank(e.Overview),
            AirDate = Date(e.AirDate),
            Runtime = e.Runtime ?? 0,
            StillUrl = TmdbApi.Image(e.StillPath, "w780"),
            Rating = e.VoteCount > 0 && e.VoteAverage > 0 ? Math.Round(e.VoteAverage.Value, 1) : null,
            VoteCount = e.VoteCount ?? 0,
            GuestStars = credits.Cast,
            Crew = credits.Crew,
        };
    }

    private static IEnumerable<CrewMember> Creators(List<TvCreator> creators) =>
        creators.Where(c => c.Id > 0 && Blank(c.Name) is not null).Select(c => new CrewMember(c.Id, c.Name!, "Creator", TmdbApi.Image(c.ProfilePath, "w185")));

    private async Task<int?> ResolveTmdbIdAsync(MetadataSettings meta, SeriesIds ids, CancellationToken ct)
    {
        if (ids.TmdbId is not null || ids.TvdbId is not { } tvdbId) return ids.TmdbId;
        // Shows added from TheTVDB may not know their TMDB id yet.
        var found = await api.GetAsync<FindResponse>(meta, $"3/find/{tvdbId.ToString(CultureInfo.InvariantCulture)}?external_source=tvdb_id", ct);
        return found.TvResults.FirstOrDefault()?.Id;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static DateOnly? Date(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private async Task<IReadOnlyList<SeriesLookup>> RelatedAsync(MetadataSettings meta, SeriesIds ids, string kind, CancellationToken ct)
    {
        var tmdbId = await ResolveTmdbIdAsync(meta, ids, ct);
        if (tmdbId is null) return [];
        var result = await api.GetAsync<TmdbPaged<TmdbTvClient.TvResult>>(meta, $"3/tv/{tmdbId.Value.ToString(CultureInfo.InvariantCulture)}/{kind}?page=1", ct);
        return result.Results.Select(TmdbTvClient.Map).ToList();
    }

    private sealed record FindResponse([property: JsonPropertyName("tv_results")] List<TmdbTvClient.TvResult> TvResults);

    private sealed record TvDetails
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("original_name")] public string? OriginalName { get; init; }
        [JsonPropertyName("tagline")] public string? Tagline { get; init; }
        [JsonPropertyName("first_air_date")] public string? FirstAirDate { get; init; }
        [JsonPropertyName("last_air_date")] public string? LastAirDate { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }
        [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("vote_average")] public double? VoteAverage { get; init; }
        [JsonPropertyName("number_of_episodes")] public int? NumberOfEpisodes { get; init; }
        [JsonPropertyName("episode_run_time")] public List<int> EpisodeRunTime { get; init; } = [];
        [JsonPropertyName("genres")] public List<TmdbNamed> Genres { get; init; } = [];
        [JsonPropertyName("networks")] public List<TmdbNamed> Networks { get; init; } = [];
        [JsonPropertyName("created_by")] public List<TvCreator> CreatedBy { get; init; } = [];
        [JsonPropertyName("seasons")] public List<TvSeasonSummary> Seasons { get; init; } = [];
        [JsonPropertyName("external_ids")] public TvExternalIds? ExternalIds { get; init; }
        [JsonPropertyName("aggregate_credits")] public TmdbCreditList? Credits { get; init; }
    }

    private sealed record TvEpisode
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("air_date")] public string? AirDate { get; init; }
        [JsonPropertyName("runtime")] public int? Runtime { get; init; }
        [JsonPropertyName("still_path")] public string? StillPath { get; init; }
        [JsonPropertyName("vote_average")] public double? VoteAverage { get; init; }
        [JsonPropertyName("vote_count")] public int? VoteCount { get; init; }
        [JsonPropertyName("guest_stars")] public List<TmdbCastCredit> GuestStars { get; init; } = [];
        [JsonPropertyName("crew")] public List<TmdbCrewCredit> Crew { get; init; } = [];
        [JsonPropertyName("external_ids")] public TvExternalIds? ExternalIds { get; init; }
    }

    private sealed record TvCreator
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("profile_path")] public string? ProfilePath { get; init; }
    }

    private sealed record TvSeasonSummary
    {
        [JsonPropertyName("season_number")] public int SeasonNumber { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("episode_count")] public int? EpisodeCount { get; init; }
        [JsonPropertyName("air_date")] public string? AirDate { get; init; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }
    }

    private sealed record TvExternalIds
    {
        [JsonPropertyName("imdb_id")] public string? ImdbId { get; init; }
        [JsonPropertyName("tvdb_id")] public int? TvdbId { get; init; }
    }
}

public static class SeriesDiscoveryEndpoints
{
    /// <summary>Library shows whose recommendations feed "for you".</summary>
    private const int Seeds = 12;

    public static IEndpointRouteBuilder MapSeriesDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/series").WithTags("Discover");

        group.MapGet("/discover/for-you", async (ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            var library = await db.Series.AsNoTracking().Select(s => new { s.TmdbId, s.TvdbId, s.Added }).ToListAsync(ct);
            if (library.Count == 0) return Results.Ok(Array.Empty<SeriesLookup>());
            var seeds = library.OrderByDescending(s => s.Added).Take(Seeds).Select(s => new SeriesIds(s.TmdbId, s.TvdbId)).ToList();
            return await Run(async () =>
            {
                var lists = await discovery.RecommendationsAsync(seeds, ct);
                var owned = library.Where(s => s.TmdbId is not null).Select(s => s.TmdbId!.Value).ToHashSet();
                var ranked = RecommendationRanker.Rank(lists, s => s.TmdbId ?? 0, owned, 40);
                // Shows stored without a TMDB id slip past the exclusion above; drop them once matched.
                return (await MarkLibraryAsync(db, ranked, ct)).Where(s => s.SeriesId is null).ToList();
            });
        });

        // Comma-separated TMDB keyword ids, all of which must match.
        group.MapGet("/discover/keywords", async (string? keywords, int? page, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            var ids = KeywordIds.Parse(keywords);
            if (ids.Count == 0) return Results.Problem("Pick at least one keyword.", statusCode: StatusCodes.Status400BadRequest);
            return await Run(async () =>
            {
                var result = await discovery.KeywordsAsync(ids, page ?? 1, ct);
                return result with { Results = await MarkLibraryAsync(db, result.Results, ct) };
            });
        });

        group.MapGet("/discover/{list}", async (string list, int? page, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (ParseList(list) is not { } kind) return Results.NotFound();
            return await Run(async () =>
            {
                var result = await discovery.ListAsync(kind, page ?? 1, ct);
                return result with { Results = await MarkLibraryAsync(db, result.Results, ct) };
            });
        });

        group.MapGet("/{id:int}/recommendations", (int id, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
            RelatedAsync(id, db, ids => discovery.RecommendationsAsync(ids, ct), ct));

        group.MapGet("/{id:int}/similar", (int id, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
            RelatedAsync(id, db, ids => discovery.SimilarAsync(ids, ct), ct));

        group.MapGet("/{id:int}/credits", async (int id, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            var show = await db.Series.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.TmdbId, s.TvdbId }).FirstOrDefaultAsync(ct);
            if (show is null) return Results.NotFound();
            return await Run(() => discovery.CreditsAsync(new SeriesIds(show.TmdbId, show.TvdbId), ct));
        });

        // TMDB details of one library episode; 204 when TMDB doesn't know the show or the episode.
        group.MapGet("/{id:int}/episodes/{episodeId:int}/details", async (int id, int episodeId, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            var row = await (
                from e in db.Episodes.AsNoTracking()
                join s in db.Series.AsNoTracking() on e.SeriesId equals s.Id
                where e.Id == episodeId && e.SeriesId == id
                select new { e.SeasonNumber, e.EpisodeNumber, s.TmdbId, s.TvdbId }).FirstOrDefaultAsync(ct);
            if (row is null) return Results.NotFound();
            try
            {
                var details = await discovery.EpisodeDetailsAsync(new SeriesIds(row.TmdbId, row.TvdbId), row.SeasonNumber, row.EpisodeNumber, ct);
                return details is null ? Results.NoContent() : Results.Ok(details);
            }
            catch (MetadataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // Any TMDB show, in the library or not: the detail page for shows found through discovery or a person.
        group.MapGet("/tmdb/{tmdbId:int}", (int tmdbId, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) => Run(async () =>
        {
            var details = await discovery.DetailsAsync(tmdbId, ct);
            var show = details.Show;
            // Shows added from TheTVDB may be stored without their TMDB id.
            var seriesId = await db.Series.AsNoTracking()
                .Where(s => s.TmdbId == show.TmdbId || (show.TvdbId != null && s.TvdbId == show.TvdbId))
                .Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
            return details with { Show = show with { SeriesId = seriesId } };
        }));

        group.MapGet("/tmdb/{tmdbId:int}/recommendations", (int tmdbId, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
            Run(async () => await MarkLibraryAsync(db, await discovery.RecommendationsAsync(new SeriesIds(tmdbId, null), ct), ct)));

        group.MapGet("/tmdb/{tmdbId:int}/similar", (int tmdbId, ISeriesDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
            Run(async () => await MarkLibraryAsync(db, await discovery.SimilarAsync(new SeriesIds(tmdbId, null), ct), ct)));

        return app;
    }

    public static SeriesList? ParseList(string list) => list.ToLowerInvariant() switch
    {
        "trending" => SeriesList.Trending,
        "popular" => SeriesList.Popular,
        "upcoming" => SeriesList.Upcoming,
        "top-rated" => SeriesList.TopRated,
        "airing-today" => SeriesList.AiringToday,
        _ => null,
    };

    private static async Task<IResult> RelatedAsync(int id, KrakarrDbContext db, Func<SeriesIds, Task<IReadOnlyList<SeriesLookup>>> fetch, CancellationToken ct)
    {
        var show = await db.Series.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.TmdbId, s.TvdbId }).FirstOrDefaultAsync(ct);
        if (show is null) return Results.NotFound();
        return await Run(async () => await MarkLibraryAsync(db, await fetch(new SeriesIds(show.TmdbId, show.TvdbId)), ct));
    }

    private static async Task<IReadOnlyList<SeriesLookup>> MarkLibraryAsync(KrakarrDbContext db, IReadOnlyList<SeriesLookup> items, CancellationToken ct)
    {
        var ids = items.Where(r => r.TmdbId is not null).Select(r => r.TmdbId).ToList();
        var existing = await db.Series.AsNoTracking().Where(s => ids.Contains(s.TmdbId)).ToDictionaryAsync(s => s.TmdbId!.Value, s => s.Id, ct);
        return items.Select(r => r with { SeriesId = r.TmdbId is { } t && existing.TryGetValue(t, out var sid) ? sid : null }).ToList();
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (MetadataException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
