using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Media.Parsing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Indexers;

public sealed record IndexerTestResult(bool Success, string? Message, IndexerCapabilities? Capabilities);

public sealed record SearchResultDto(ReleaseInfo Release, string Quality, string? ParsedTitle, int? ParsedYear);

public static class IndexerEndpoints
{
    public static IEndpointRouteBuilder MapIndexerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/indexers").WithTags("Indexers");

        group.MapGet("/", async (KrakarrDbContext db, CancellationToken ct) =>
            await db.Indexers.AsNoTracking().OrderBy(i => i.Priority).ThenBy(i => i.Name).ToListAsync(ct));

        group.MapGet("/{id:int}", async (int id, KrakarrDbContext db, CancellationToken ct) =>
            await db.Indexers.FindAsync([id], ct) is { } indexer ? Results.Ok(indexer) : Results.NotFound());

        group.MapPost("/", async (Indexer input, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem) return problem;
            if (await db.Indexers.AnyAsync(i => i.Name == input.Name, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["An indexer with this name already exists"] });

            input.Id = 0;
            input.Added = DateTimeOffset.UtcNow;
            db.Indexers.Add(input);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/indexers/{input.Id}", input);
        });

        group.MapPut("/{id:int}", async (int id, Indexer input, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem) return problem;
            var existing = await db.Indexers.FindAsync([id], ct);
            if (existing is null) return Results.NotFound();

            existing.Name = input.Name;
            existing.Protocol = input.Protocol;
            existing.BaseUrl = input.BaseUrl;
            existing.ApiPath = input.ApiPath;
            existing.ApiKey = input.ApiKey;
            existing.MovieCategories = input.MovieCategories;
            existing.TvCategories = input.TvCategories;
            existing.Enabled = input.Enabled;
            existing.EnableRss = input.EnableRss;
            existing.Priority = input.Priority;
            existing.MinimumSeeders = input.MinimumSeeders;
            await db.SaveChangesAsync(ct);
            return Results.Ok(existing);
        });

        group.MapDelete("/{id:int}", async (int id, KrakarrDbContext db, CancellationToken ct) =>
        {
            var deleted = await db.Indexers.Where(i => i.Id == id).ExecuteDeleteAsync(ct);
            return deleted > 0 ? Results.NoContent() : Results.NotFound();
        });

        group.MapPost("/test", async (Indexer input, ITorznabClient client, CancellationToken ct) =>
        {
            try
            {
                var caps = await client.GetCapabilitiesAsync(input, forceRefresh: true, ct);
                await client.SearchAsync(input, new SearchCriteria { Rss = true, Limit = 5 }, ct);
                return Results.Ok(new IndexerTestResult(true, null, caps));
            }
            catch (Exception ex) when (ex is IndexerException or HttpRequestException or TaskCanceledException)
            {
                return Results.Ok(new IndexerTestResult(false, ex.Message, null));
            }
        });

        // Capabilities (search modes + categories) of a possibly unsaved indexer configuration.
        group.MapPost("/capabilities", async (Indexer input, ITorznabClient client, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem) return problem;
            try
            {
                return Results.Ok(new IndexerTestResult(true, null, await client.GetCapabilitiesAsync(input, forceRefresh: true, ct)));
            }
            catch (Exception ex) when (ex is IndexerException or HttpRequestException or TaskCanceledException)
            {
                return Results.Ok(new IndexerTestResult(false, ex.Message, null));
            }
        });

        // Manual search across indexers (Prowlarr-style).
        app.MapGet("/api/v1/search", async (
            string? query, SearchType? type, string? imdbId, int? tmdbId, int? tvdbId, int? season, int? episode, string? indexerIds,
            IIndexerSearchService search, CancellationToken ct) =>
        {
            var ids = indexerIds?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
            var result = await search.SearchAsync(new SearchCriteria
            {
                Type = type ?? SearchType.Generic,
                Query = query,
                ImdbId = imdbId,
                TmdbId = tmdbId,
                TvdbId = tvdbId,
                Season = season,
                Episode = episode,
            }, ids, ct);

            return new
            {
                Results = result.Releases
                    .Select(r =>
                    {
                        var parsed = ReleaseParser.Parse(r.Title);
                        return new SearchResultDto(r, parsed.Quality.Name, parsed.Title, parsed.Year);
                    })
                    .OrderByDescending(r => r.Release.Seeders ?? 0),
                result.Errors,
            };
        }).WithTags("Search");

        return app;
    }

    private static IResult? Validate(Indexer input)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Name)) errors["name"] = ["Name is required"];
        if (!Uri.TryCreate(input.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            errors["baseUrl"] = ["A valid http(s) URL is required"];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
