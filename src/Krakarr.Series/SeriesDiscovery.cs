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

/// <summary>
/// TMDB TV discovery: curated lists plus per-show recommendations and similar shows. It needs a TMDB key whichever
/// series metadata source is selected; results carry TMDB ids, which adding a series resolves for TheTVDB.
/// </summary>
public interface ISeriesDiscovery
{
    Task<DiscoverPage<SeriesLookup>> ListAsync(SeriesList list, int page, CancellationToken ct = default);
    Task<IReadOnlyList<SeriesLookup>> RecommendationsAsync(SeriesIds ids, CancellationToken ct = default);
    Task<IReadOnlyList<SeriesLookup>> SimilarAsync(SeriesIds ids, CancellationToken ct = default);
    /// <summary>Recommendations for several shows at once (unknown shows yield an empty list), in the order given.</summary>
    Task<IReadOnlyList<IReadOnlyList<SeriesLookup>>> RecommendationsAsync(IReadOnlyList<SeriesIds> shows, CancellationToken ct = default);
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

    private async Task<IReadOnlyList<SeriesLookup>> RelatedAsync(MetadataSettings meta, SeriesIds ids, string kind, CancellationToken ct)
    {
        var tmdbId = ids.TmdbId;
        if (tmdbId is null && ids.TvdbId is { } tvdbId)
        {
            // Shows added from TheTVDB may not know their TMDB id yet.
            var found = await api.GetAsync<FindResponse>(meta, $"3/find/{tvdbId.ToString(CultureInfo.InvariantCulture)}?external_source=tvdb_id", ct);
            tmdbId = found.TvResults.FirstOrDefault()?.Id;
        }

        if (tmdbId is null) return [];
        var result = await api.GetAsync<TmdbPaged<TmdbTvClient.TvResult>>(meta, $"3/tv/{tmdbId.Value.ToString(CultureInfo.InvariantCulture)}/{kind}?page=1", ct);
        return result.Results.Select(TmdbTvClient.Map).ToList();
    }

    private sealed record FindResponse([property: JsonPropertyName("tv_results")] List<TmdbTvClient.TvResult> TvResults);
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
