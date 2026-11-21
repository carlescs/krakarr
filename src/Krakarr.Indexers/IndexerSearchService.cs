using System.Diagnostics;
using Krakarr.Core.Domain;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Indexers;

public sealed record IndexerSearchResult(IReadOnlyList<ReleaseInfo> Releases, IReadOnlyList<IndexerError> Errors);

public sealed record IndexerError(int IndexerId, string IndexerName, string Message);

public interface IIndexerSearchService
{
    Task<IndexerSearchResult> SearchAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default);
}

/// <summary>Fans a search out across all enabled indexers and merges the results.</summary>
public sealed class IndexerSearchService(KrakarrDbContext db, ITorznabClient client, ILogger<IndexerSearchService> logger) : IIndexerSearchService
{
    public async Task<IndexerSearchResult> SearchAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default)
    {
        var query = db.Indexers.AsNoTracking().Where(i => i.Enabled);
        if (criteria.Rss)
        {
            query = query.Where(i => i.EnableRss);
        }

        if (indexerIds is { Count: > 0 })
        {
            query = query.Where(i => indexerIds.Contains(i.Id));
        }

        var kind = criteria.Rss ? "rss" : criteria.Type.ToString().ToLowerInvariant();
        using var activity = KrakarrTelemetry.ActivitySource.StartActivity($"indexer search {kind}");
        if (activity is not null)
        {
            activity.SetTag(KrakarrTelemetry.SearchKindTag, kind);
            activity.SetTag("krakarr.search.query", criteria.Query);
            activity.SetTag("krakarr.search.imdb_id", criteria.ImdbId);
            activity.SetTag("krakarr.search.tmdb_id", criteria.TmdbId);
            activity.SetTag("krakarr.search.tvdb_id", criteria.TvdbId);
            activity.SetTag("krakarr.search.season", criteria.Season);
            activity.SetTag("krakarr.search.episode", criteria.Episode);
        }

        var indexers = await query.ToListAsync(ct);
        activity?.SetTag("krakarr.search.indexers", indexers.Count);
        var errors = new List<IndexerError>();

        var tasks = indexers.Select(async indexer =>
        {
            using var queryActivity = KrakarrTelemetry.ActivitySource.StartActivity("indexer query", ActivityKind.Client);
            queryActivity?.SetTag(KrakarrTelemetry.IndexerTag, indexer.Name);
            queryActivity?.SetTag(KrakarrTelemetry.SearchKindTag, kind);
            var started = Stopwatch.GetTimestamp();
            var outcome = KrakarrTelemetry.Success;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var releases = await client.SearchAsync(indexer, criteria, timeout.Token);
                var kept = releases.Where(r => r.Seeders is null || r.Seeders >= indexer.MinimumSeeders).ToList();
                queryActivity?.SetTag("krakarr.indexer.releases", kept.Count);
                KrakarrTelemetry.IndexerReleases.Add(kept.Count,
                    new KeyValuePair<string, object?>(KrakarrTelemetry.IndexerTag, indexer.Name),
                    new KeyValuePair<string, object?>(KrakarrTelemetry.SearchKindTag, kind));
                return kept;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                outcome = KrakarrTelemetry.Failure;
                queryActivity.RecordException(ex);
                logger.LogWarning("Search on {Indexer} failed: {Message}", indexer.Name, ex.Message);
                lock (errors)
                {
                    errors.Add(new IndexerError(indexer.Id, indexer.Name, ex.Message));
                }

                return [];
            }
            finally
            {
                KeyValuePair<string, object?>[] tags =
                [
                    new(KrakarrTelemetry.IndexerTag, indexer.Name),
                    new(KrakarrTelemetry.SearchKindTag, kind),
                    new(KrakarrTelemetry.OutcomeTag, outcome),
                ];
                KrakarrTelemetry.IndexerQueries.Add(1, tags);
                KrakarrTelemetry.IndexerQueryDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            }
        });

        var all = (await Task.WhenAll(tasks)).SelectMany(r => r);

        // De-duplicate across indexers: same info hash, or same title+size.
        var deduped = all
            .GroupBy(r => r.InfoHash ?? $"{r.Title}|{r.Size}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.IndexerPriority).ThenByDescending(r => r.Seeders ?? 0).First())
            .ToList();

        activity?.SetTag("krakarr.search.releases", deduped.Count);
        activity?.SetTag("krakarr.search.errors", errors.Count);
        return new IndexerSearchResult(deduped, errors);
    }
}
