using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Media.Parsing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Indexers;

public sealed record IndexerTestResult(bool Success, string? Message, IndexerCapabilities? Capabilities);

/// <summary>A Prowlarr indexer as offered for import; <c>ExistingId</c> is set when it was imported before.</summary>
public sealed record ProwlarrIndexerDto(int Id, string Name, string Protocol, bool Supported, bool Enabled, int Priority, int? ExistingId);

public sealed record ProwlarrListResult(bool Success, string? Message, IReadOnlyList<ProwlarrIndexerDto> Indexers);

public sealed record ProwlarrImportRequest(string Url, string ApiKey, IReadOnlyList<int> IndexerIds);

public sealed record ProwlarrImportResult(int Added, int Updated);

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
            input.DefaultLanguage = NormalizeLanguage(input.DefaultLanguage);
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
            existing.DefaultLanguage = NormalizeLanguage(input.DefaultLanguage);
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

        // Indexers configured in a Prowlarr instance, to import them as Torznab feeds.
        group.MapPost("/prowlarr/list", async (ProwlarrConnection input, IProwlarrClient prowlarr, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (ValidateProwlarr(input) is { } problem) return problem;
            try
            {
                var indexers = await prowlarr.GetIndexersAsync(input, ct);
                var existing = await ExistingFeedsAsync(db, ct);
                return Results.Ok(new ProwlarrListResult(true, null, indexers
                    .Select(i => new ProwlarrIndexerDto(i.Id, i.Name, i.Protocol, i.Supported, i.Enabled, i.Priority,
                        existing.TryGetValue(ProwlarrClient.FeedUrl(input.Url, i.Id), out var id) ? id : null))
                    .ToList()));
            }
            catch (Exception ex) when (ex is IndexerException or HttpRequestException or TaskCanceledException)
            {
                return Results.Ok(new ProwlarrListResult(false, ex.Message, []));
            }
        });

        // Adds the chosen Prowlarr indexers, or refreshes the API key and protocol of ones imported before.
        group.MapPost("/prowlarr/import", async (ProwlarrImportRequest input, IProwlarrClient prowlarr, KrakarrDbContext db, CancellationToken ct) =>
        {
            var connection = new ProwlarrConnection(input.Url, input.ApiKey);
            if (ValidateProwlarr(connection) is { } problem) return problem;

            IReadOnlyList<ProwlarrIndexer> available;
            try
            {
                available = await prowlarr.GetIndexersAsync(connection, ct);
            }
            catch (Exception ex) when (ex is IndexerException or HttpRequestException or TaskCanceledException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["url"] = [ex.Message] });
            }

            var wanted = input.IndexerIds.ToHashSet();
            var chosen = available.Where(i => wanted.Contains(i.Id) && i.Supported).ToList();
            var existing = await ExistingFeedsAsync(db, ct);
            var names = (await db.Indexers.AsNoTracking().Select(i => i.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = 0, updated = 0;
            foreach (var p in chosen)
            {
                var feedUrl = ProwlarrClient.FeedUrl(connection.Url, p.Id);
                if (existing.TryGetValue(feedUrl, out var existingId))
                {
                    var current = await db.Indexers.FindAsync([existingId], ct);
                    if (current is null) continue;
                    current.ApiKey = connection.ApiKey;
                    current.ApiPath = "/api";
                    current.Protocol = IndexerProtocol.Torznab;
                    updated++;
                    continue;
                }

                var name = UniqueName(p.Name, names);
                names.Add(name);
                db.Indexers.Add(new Indexer
                {
                    Name = name,
                    Protocol = IndexerProtocol.Torznab,
                    BaseUrl = feedUrl,
                    ApiPath = "/api",
                    ApiKey = connection.ApiKey,
                    MovieCategories = ProwlarrClient.SuggestCategories(p.Categories, 2000, 2999, 2000),
                    TvCategories = ProwlarrClient.SuggestCategories(p.Categories, 5000, 5999, 5000),
                    Enabled = p.Enabled,
                    EnableRss = p.Enabled && p.SupportsRss,
                    Priority = p.Priority,
                });
                added++;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new ProwlarrImportResult(added, updated));
        });

        // Manual search across indexers (Prowlarr-style). With "Accept: application/x-ndjson" the results stream in as each indexer answers (SearchStream).
        app.MapGet("/api/v1/search", async (
            string? query, SearchType? type, string? imdbId, int? tmdbId, int? tvdbId, int? season, int? episode, string? indexerIds,
            HttpRequest request, IIndexerSearchService search, CancellationToken ct) =>
        {
            var ids = indexerIds?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
            var criteria = new SearchCriteria
            {
                Type = type ?? SearchType.Generic,
                Query = query,
                ImdbId = imdbId,
                TmdbId = tmdbId,
                TvdbId = tvdbId,
                Season = season,
                Episode = episode,
            };

            if (SearchStream.Requested(request))
            {
                var run = await search.StartAsync(criteria, ids, ct);
                return SearchStream.Result(run.EventsAsync(ToSearchResults, r => r.Release, ct));
            }

            var result = await search.SearchAsync(criteria, ids, ct);
            return Results.Ok(new
            {
                Results = ToSearchResults(result.Releases),
                result.Errors,
                result.Indexers,
            });
        }).WithTags("Search");

        return app;
    }

    /// <summary>Parsed search results, most seeders first.</summary>
    private static IReadOnlyList<SearchResultDto> ToSearchResults(IReadOnlyList<ReleaseInfo> releases) =>
        releases
            .Select(r =>
            {
                var parsed = ReleaseParser.Parse(r.Title);
                return new SearchResultDto(r, parsed.Quality.Name, parsed.Title, parsed.Year);
            })
            .OrderByDescending(r => r.Release.Seeders ?? 0)
            .ToList();

    private static string? NormalizeLanguage(string? code) => string.IsNullOrWhiteSpace(code) ? null : MediaLanguages.Normalize(code);

    /// <summary>Indexer ids keyed by their feed URL (trailing slash trimmed), to recognise Prowlarr feeds imported before.</summary>
    private static async Task<Dictionary<string, int>> ExistingFeedsAsync(KrakarrDbContext db, CancellationToken ct)
    {
        var rows = await db.Indexers.AsNoTracking().Select(i => new { i.Id, i.BaseUrl }).ToListAsync(ct);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) result.TryAdd(ProwlarrClient.NormalizeUrl(row.BaseUrl), row.Id);
        return result;
    }

    /// <summary>The Prowlarr name, or "Name (Prowlarr)", "Name (Prowlarr 2)"… when an indexer already has it.</summary>
    internal static string UniqueName(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        var candidate = $"{name} (Prowlarr)";
        for (var n = 2; taken.Contains(candidate); n++) candidate = $"{name} (Prowlarr {n})";
        return candidate;
    }

    private static IResult? ValidateProwlarr(ProwlarrConnection input)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Uri.TryCreate(input.Url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            errors["url"] = ["A valid http(s) URL is required"];
        if (string.IsNullOrWhiteSpace(input.ApiKey)) errors["apiKey"] = ["The Prowlarr API key is required"];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static IResult? Validate(Indexer input)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Name)) errors["name"] = ["Name is required"];
        if (!Uri.TryCreate(input.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            errors["baseUrl"] = ["A valid http(s) URL is required"];
        if (!string.IsNullOrWhiteSpace(input.DefaultLanguage) && !MediaLanguages.IsKnown(input.DefaultLanguage))
            errors["defaultLanguage"] = ["Unknown language"];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
