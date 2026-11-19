using Krakarr.Core.Domain;
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

        var indexers = await query.ToListAsync(ct);
        var errors = new List<IndexerError>();

        var tasks = indexers.Select(async indexer =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var releases = await client.SearchAsync(indexer, criteria, timeout.Token);
                return releases.Where(r => r.Seeders is null || r.Seeders >= indexer.MinimumSeeders).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Search on {Indexer} failed: {Message}", indexer.Name, ex.Message);
                lock (errors)
                {
                    errors.Add(new IndexerError(indexer.Id, indexer.Name, ex.Message));
                }

                return [];
            }
        });

        var all = (await Task.WhenAll(tasks)).SelectMany(r => r);

        // De-duplicate across indexers: same info hash, or same title+size.
        var deduped = all
            .GroupBy(r => r.InfoHash ?? $"{r.Title}|{r.Size}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(r => r.IndexerPriority).ThenByDescending(r => r.Seeders ?? 0).First())
            .ToList();

        return new IndexerSearchResult(deduped, errors);
    }
}
