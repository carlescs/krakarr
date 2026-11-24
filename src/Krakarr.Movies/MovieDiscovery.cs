using System.Globalization;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Movies;

public enum MovieList { Trending, Popular, Upcoming, TopRated, NowPlaying }

/// <summary>A movie's TMDB page: the lookup plus what a detail page shows for titles outside the library.</summary>
public sealed record MovieDetails
{
    public required MovieLookup Movie { get; init; }
    public string? Tagline { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    /// <summary>Theatrical, digital and physical dates: in the release region and earliest worldwide.</summary>
    public IReadOnlyList<MovieReleaseDto> Releases { get; init; } = [];
    public string? ReleaseRegion { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<CastMember> Cast { get; init; } = [];
    public IReadOnlyList<CrewMember> Crew { get; init; } = [];
}

/// <summary>TMDB movie discovery: curated lists plus per-title recommendations and similar titles.</summary>
public interface IMovieDiscovery
{
    Task<DiscoverPage<MovieLookup>> ListAsync(MovieList list, int page, CancellationToken ct = default);
    /// <summary>Movies tagged with every one of the TMDB keywords, most popular first.</summary>
    Task<DiscoverPage<MovieLookup>> KeywordsAsync(IReadOnlyList<int> keywordIds, int page, CancellationToken ct = default);
    Task<IReadOnlyList<MovieLookup>> RecommendationsAsync(int tmdbId, CancellationToken ct = default);
    Task<IReadOnlyList<MovieLookup>> SimilarAsync(int tmdbId, CancellationToken ct = default);
    /// <summary>Recommendations for several titles at once (missing titles yield an empty list), in the order given.</summary>
    Task<IReadOnlyList<IReadOnlyList<MovieLookup>>> RecommendationsAsync(IReadOnlyList<int> tmdbIds, CancellationToken ct = default);
    /// <summary>Details with cast and key crew, for any TMDB movie.</summary>
    Task<MovieDetails> DetailsAsync(int tmdbId, CancellationToken ct = default);
    Task<Credits> CreditsAsync(int tmdbId, CancellationToken ct = default);
}

public sealed class TmdbMovieDiscovery(TmdbApi api, ISettingsService settings, TimeProvider time) : IMovieDiscovery
{
    public async Task<DiscoverPage<MovieLookup>> ListAsync(MovieList list, int page, CancellationToken ct = default)
    {
        var p = Math.Clamp(page, 1, 500).ToString(CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = list switch
        {
            MovieList.Trending => $"3/trending/movie/week?page={p}",
            MovieList.Popular => $"3/movie/popular?page={p}",
            MovieList.TopRated => $"3/movie/top_rated?page={p}",
            MovieList.NowPlaying => $"3/movie/now_playing?page={p}",
            // Theatrical or digital releases from today on, most anticipated first.
            _ => $"3/discover/movie?page={p}&primary_release_date.gte={today}&sort_by=popularity.desc&with_release_type=2|3&include_adult=false",
        };
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var result = await api.GetAsync<TmdbPaged<TmdbClient.TmdbMovie>>(meta, path, ct);
        return new DiscoverPage<MovieLookup>(result.Results.Select(TmdbClient.Map).ToList(), result.Page, Math.Min(result.TotalPages, 500));
    }

    public async Task<DiscoverPage<MovieLookup>> KeywordsAsync(IReadOnlyList<int> keywordIds, int page, CancellationToken ct = default)
    {
        var p = Math.Clamp(page, 1, 500).ToString(CultureInfo.InvariantCulture);
        var path = $"3/discover/movie?page={p}&with_keywords={KeywordIds.ToTmdb(keywordIds)}&sort_by=popularity.desc&include_adult=false";
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var result = await api.GetAsync<TmdbPaged<TmdbClient.TmdbMovie>>(meta, path, ct);
        return new DiscoverPage<MovieLookup>(result.Results.Select(TmdbClient.Map).ToList(), result.Page, Math.Min(result.TotalPages, 500));
    }

    public async Task<IReadOnlyList<MovieLookup>> RecommendationsAsync(int tmdbId, CancellationToken ct = default) =>
        await RelatedAsync(await settings.GetAsync<MetadataSettings>(ct), tmdbId, "recommendations", ct);

    public async Task<IReadOnlyList<MovieLookup>> SimilarAsync(int tmdbId, CancellationToken ct = default) =>
        await RelatedAsync(await settings.GetAsync<MetadataSettings>(ct), tmdbId, "similar", ct);

    public async Task<IReadOnlyList<IReadOnlyList<MovieLookup>>> RecommendationsAsync(IReadOnlyList<int> tmdbIds, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        return await Task.WhenAll(tmdbIds.Select(async id =>
        {
            try
            {
                return await RelatedAsync(meta, id, "recommendations", ct);
            }
            catch (MetadataException)
            {
                return (IReadOnlyList<MovieLookup>)[];
            }
        }));
    }

    public async Task<MovieDetails> DetailsAsync(int tmdbId, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var m = await api.GetAsync<TmdbClient.TmdbMovie>(meta, $"3/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}?append_to_response=credits,release_dates", ct);
        var lookup = TmdbClient.Map(m) with { Collection = TmdbClient.MapCollection(m.Collection) };
        var credits = TmdbCredits.Map(m.Credits);
        var releaseDates = TmdbClient.MapReleaseDates(m.ReleaseDates);
        var region = MovieReleases.NormalizeRegion(meta.ReleaseRegion);
        return new MovieDetails
        {
            Movie = lookup,
            Tagline = string.IsNullOrWhiteSpace(m.Tagline) ? null : m.Tagline,
            ReleaseDate = DateOnly.TryParseExact(m.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            Releases = MovieEndpoints.ToReleases(releaseDates, region),
            ReleaseRegion = region,
            Genres = (m.Genres ?? []).Select(g => g.Name).OfType<string>().ToList(),
            Cast = credits.Cast,
            Crew = credits.Crew,
        };
    }

    public async Task<Credits> CreditsAsync(int tmdbId, CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        return TmdbCredits.Map(await api.GetAsync<TmdbCreditList>(meta, $"3/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}/credits", ct));
    }

    private async Task<IReadOnlyList<MovieLookup>> RelatedAsync(MetadataSettings meta, int tmdbId, string kind, CancellationToken ct)
    {
        var result = await api.GetAsync<TmdbPaged<TmdbClient.TmdbMovie>>(meta, $"3/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}/{kind}?page=1", ct);
        return result.Results.Select(TmdbClient.Map).ToList();
    }
}

public static class MovieDiscoveryEndpoints
{
    /// <summary>Library titles whose recommendations feed "for you".</summary>
    private const int Seeds = 12;

    public static IEndpointRouteBuilder MapMovieDiscoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/movies").WithTags("Discover");

        group.MapGet("/discover/for-you", async (IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            var library = await db.Movies.AsNoTracking().Select(m => new { m.Id, m.TmdbId, m.Added }).ToListAsync(ct);
            if (library.Count == 0) return Results.Ok(Array.Empty<MovieLookup>());
            var seeds = library.OrderByDescending(m => m.Added).Take(Seeds).Select(m => m.TmdbId).ToList();
            return await Run(async () =>
            {
                var lists = await discovery.RecommendationsAsync(seeds, ct);
                var owned = library.Select(m => m.TmdbId).ToHashSet();
                return RecommendationRanker.Rank(lists, m => m.TmdbId, owned, 40);
            });
        });

        // Comma-separated TMDB keyword ids, all of which must match.
        group.MapGet("/discover/keywords", async (string? keywords, int? page, IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            var ids = KeywordIds.Parse(keywords);
            if (ids.Count == 0) return Results.Problem("Pick at least one keyword.", statusCode: StatusCodes.Status400BadRequest);
            return await Run(async () =>
            {
                var result = await discovery.KeywordsAsync(ids, page ?? 1, ct);
                return result with { Results = await MarkLibraryAsync(db, result.Results, ct) };
            });
        });

        group.MapGet("/discover/{list}", async (string list, int? page, IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (ParseList(list) is not { } kind) return Results.NotFound();
            return await Run(async () =>
            {
                var result = await discovery.ListAsync(kind, page ?? 1, ct);
                return result with { Results = await MarkLibraryAsync(db, result.Results, ct) };
            });
        });

        group.MapGet("/{id:int}/recommendations", (int id, IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
            RelatedAsync(id, db, tmdbId => discovery.RecommendationsAsync(tmdbId, ct), ct));

        group.MapGet("/{id:int}/similar", (int id, IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
            RelatedAsync(id, db, tmdbId => discovery.SimilarAsync(tmdbId, ct), ct));

        group.MapGet("/{id:int}/credits", async (int id, IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
        {
            var tmdbId = await db.Movies.AsNoTracking().Where(m => m.Id == id).Select(m => (int?)m.TmdbId).FirstOrDefaultAsync(ct);
            if (tmdbId is null) return Results.NotFound();
            return await Run(() => discovery.CreditsAsync(tmdbId.Value, ct));
        });

        // Any TMDB movie, in the library or not: the detail page for titles found through discovery or a person.
        group.MapGet("/tmdb/{tmdbId:int}", (int tmdbId, IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) => Run(async () =>
        {
            var details = await discovery.DetailsAsync(tmdbId, ct);
            return details with { Movie = (await MarkLibraryAsync(db, [details.Movie], ct))[0] };
        }));

        group.MapGet("/tmdb/{tmdbId:int}/recommendations", (int tmdbId, IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
            Run(async () => await MarkLibraryAsync(db, await discovery.RecommendationsAsync(tmdbId, ct), ct)));

        group.MapGet("/tmdb/{tmdbId:int}/similar", (int tmdbId, IMovieDiscovery discovery, KrakarrDbContext db, CancellationToken ct) =>
            Run(async () => await MarkLibraryAsync(db, await discovery.SimilarAsync(tmdbId, ct), ct)));

        return app;
    }

    public static MovieList? ParseList(string list) => list.ToLowerInvariant() switch
    {
        "trending" => MovieList.Trending,
        "popular" => MovieList.Popular,
        "upcoming" => MovieList.Upcoming,
        "top-rated" => MovieList.TopRated,
        "now-playing" => MovieList.NowPlaying,
        _ => null,
    };

    private static async Task<IResult> RelatedAsync(int id, KrakarrDbContext db, Func<int, Task<IReadOnlyList<MovieLookup>>> fetch, CancellationToken ct)
    {
        var tmdbId = await db.Movies.AsNoTracking().Where(m => m.Id == id).Select(m => (int?)m.TmdbId).FirstOrDefaultAsync(ct);
        if (tmdbId is null) return Results.NotFound();
        return await Run(async () => await MarkLibraryAsync(db, await fetch(tmdbId.Value), ct));
    }

    private static async Task<IReadOnlyList<MovieLookup>> MarkLibraryAsync(KrakarrDbContext db, IReadOnlyList<MovieLookup> items, CancellationToken ct)
    {
        var ids = items.Select(r => r.TmdbId).ToList();
        var existing = await db.Movies.AsNoTracking().Where(m => ids.Contains(m.TmdbId)).ToDictionaryAsync(m => m.TmdbId, m => m.Id, ct);
        return items.Select(r => r with { MovieId = existing.TryGetValue(r.TmdbId, out var mid) ? mid : null }).ToList();
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
