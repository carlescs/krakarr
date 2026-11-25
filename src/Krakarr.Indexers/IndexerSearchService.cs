using System.Diagnostics;
using System.Threading.Channels;
using Krakarr.Core.Domain;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Indexers;

/// <param name="Releases">De-duplicated releases from every indexer that answered.</param>
/// <param name="Errors">Indexers that failed, with the reason.</param>
/// <param name="Indexers">Every indexer that was queried and how its query ended.</param>
public sealed record IndexerSearchResult(IReadOnlyList<ReleaseInfo> Releases, IReadOnlyList<IndexerError> Errors, IReadOnlyList<IndexerQueryStatus> Indexers);

public sealed record IndexerError(int IndexerId, string IndexerName, string Message);

/// <summary><c>Skipped</c>: not queried because it kept failing (see <see cref="IndexerBackoff"/>).</summary>
public enum IndexerQueryState { Pending, Done, Failed, Skipped }

/// <summary>Where one indexer's query stands during (and after) a search.</summary>
/// <param name="Releases">Releases it returned (after the minimum seeders filter), once done.</param>
/// <param name="Message">Why it failed, or why it was skipped.</param>
/// <param name="Seconds">How long the query took, once finished.</param>
public sealed record IndexerQueryStatus(int IndexerId, string IndexerName, IndexerQueryState State, int? Releases = null, string? Message = null, double? Seconds = null);

/// <summary>One indexer's finished query: its final status and the releases it returned (empty when it failed).</summary>
public sealed record IndexerQueryResult(IndexerQueryStatus Status, IReadOnlyList<ReleaseInfo> Releases);

/// <summary>A search in flight.</summary>
/// <param name="Indexers">Every indexer of the search: pending, or skipped while it backs off after failures.</param>
/// <param name="Results">One item per queried indexer as soon as its query finishes (successfully or not), in completion order.</param>
public sealed record IndexerSearchRun(IReadOnlyList<IndexerQueryStatus> Indexers, IAsyncEnumerable<IndexerQueryResult> Results);

public interface IIndexerSearchService
{
    /// <summary>
    /// Queries the enabled indexers in parallel and waits for all of them. Indexers backing off after failures are
    /// skipped unless <paramref name="indexerIds"/> names them.
    /// </summary>
    Task<IndexerSearchResult> SearchAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default);

    /// <summary>
    /// Starts querying the enabled indexers in parallel and returns at once, so callers can show results as each indexer answers.
    /// Releases are not de-duplicated across indexers (see <see cref="ReleaseMerger"/>).
    /// </summary>
    Task<IndexerSearchRun> StartAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default);
}

/// <summary>Fans a search out across all enabled indexers and merges the results.</summary>
public sealed class IndexerSearchService(KrakarrDbContext db, ITorznabClient client, IndexerBackoff backoff, ILogger<IndexerSearchService> logger) : IIndexerSearchService
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    public async Task<IndexerSearchResult> SearchAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default)
    {
        var run = await StartAsync(criteria, indexerIds, ct);
        var finished = new Dictionary<int, IndexerQueryStatus>();
        var merger = new ReleaseMerger();
        await foreach (var result in run.Results.WithCancellation(ct))
        {
            finished[result.Status.IndexerId] = result.Status;
            merger.Add(result.Releases);
        }

        var statuses = run.Indexers.Select(s => finished.GetValueOrDefault(s.IndexerId, s)).ToList();
        var errors = statuses
            .Where(s => s.State == IndexerQueryState.Failed)
            .Select(s => new IndexerError(s.IndexerId, s.IndexerName, s.Message ?? "Unknown error"))
            .ToList();
        return new IndexerSearchResult(merger.Releases.ToList(), errors, statuses);
    }

    public async Task<IndexerSearchRun> StartAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default)
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

        var all = await query.ToListAsync(ct);

        // Indexers that keep failing are left out of automatic and all-indexer searches; picking one explicitly
        // still queries it, so the user can see whether it works again.
        var explicitlyChosen = indexerIds is { Count: > 0 };
        var skipped = explicitlyChosen ? [] : all.Where(i => backoff.IsDisabled(i.Id)).ToList();
        var indexers = all.Except(skipped).ToList();

        // Ends when the last indexer answers, not when this method returns. Started after the query so the
        // indexer queries below are its children.
        var kind = criteria.Rss ? "rss" : criteria.Type.ToString().ToLowerInvariant();
        var activity = KrakarrTelemetry.ActivitySource.StartActivity($"indexer search {kind}");
        if (activity is not null)
        {
            activity.SetTag(KrakarrTelemetry.SearchKindTag, kind);
            activity.SetTag("krakarr.search.query", criteria.Query);
            activity.SetTag("krakarr.search.imdb_id", criteria.ImdbId);
            activity.SetTag("krakarr.search.tmdb_id", criteria.TmdbId);
            activity.SetTag("krakarr.search.tvdb_id", criteria.TvdbId);
            activity.SetTag("krakarr.search.season", criteria.Season);
            activity.SetTag("krakarr.search.episode", criteria.Episode);
            activity.SetTag("krakarr.search.indexers", indexers.Count);
            activity.SetTag("krakarr.search.indexers_skipped", skipped.Count);
        }

        // Holds at most one item per indexer.
        var channel = Channel.CreateUnbounded<IndexerQueryResult>(new UnboundedChannelOptions { SingleReader = true });
        var tasks = indexers.Select(indexer => QueryAsync(indexer, criteria, kind, channel.Writer, ct)).ToList();
        _ = CompleteAsync();

        return new IndexerSearchRun(
            all.Select(i => skipped.Contains(i)
                    ? new IndexerQueryStatus(i.Id, i.Name, IndexerQueryState.Skipped, Message: SkippedMessage(backoff.Get(i.Id)))
                    : new IndexerQueryStatus(i.Id, i.Name, IndexerQueryState.Pending))
                .ToList(),
            channel.Reader.ReadAllAsync(ct));

        async Task CompleteAsync()
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // The caller gave up; its reader sees the cancellation.
            }
            finally
            {
                channel.Writer.TryComplete();
                activity?.Dispose();
            }
        }
    }

    private static string SkippedMessage(IndexerStatus? status) => status is null
        ? "Skipped after failing"
        : $"Skipped after failing {status.Failures} time(s) in a row ({status.LastError}); retried after {status.DisabledUntil:u}";

    private async Task QueryAsync(Indexer indexer, SearchCriteria criteria, string kind, ChannelWriter<IndexerQueryResult> results, CancellationToken ct)
    {
        await Task.Yield(); // run every indexer's query in parallel, not the first part of each one in turn
        using var queryActivity = KrakarrTelemetry.ActivitySource.StartActivity("indexer query", ActivityKind.Client);
        queryActivity?.SetTag(KrakarrTelemetry.IndexerTag, indexer.Name);
        queryActivity?.SetTag(KrakarrTelemetry.SearchKindTag, kind);
        var started = Stopwatch.GetTimestamp();
        var outcome = KrakarrTelemetry.Success;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);
            var releases = await client.SearchAsync(indexer, criteria, timeout.Token);
            var kept = releases.Where(r => r.Seeders is null || r.Seeders >= indexer.MinimumSeeders).ToList();
            queryActivity?.SetTag("krakarr.indexer.releases", kept.Count);
            KrakarrTelemetry.IndexerReleases.Add(kept.Count,
                new KeyValuePair<string, object?>(KrakarrTelemetry.IndexerTag, indexer.Name),
                new KeyValuePair<string, object?>(KrakarrTelemetry.SearchKindTag, kind));
            backoff.Reset(indexer.Id);
            results.TryWrite(new IndexerQueryResult(
                new IndexerQueryStatus(indexer.Id, indexer.Name, IndexerQueryState.Done, kept.Count, Seconds: Stopwatch.GetElapsedTime(started).TotalSeconds),
                kept));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            outcome = KrakarrTelemetry.Failure;
            queryActivity.RecordException(ex);
            // The caller's token isn't cancelled (see the filter), so a cancellation here is a timeout.
            var message = ex is OperationCanceledException ? $"Timed out after {QueryTimeout.TotalSeconds:0} seconds" : ex.Message;
            logger.LogWarning("Search on {Indexer} failed: {Message}", indexer.Name, message);
            backoff.RecordFailure(indexer.Id, indexer.Name, message);
            results.TryWrite(new IndexerQueryResult(
                new IndexerQueryStatus(indexer.Id, indexer.Name, IndexerQueryState.Failed, Message: message, Seconds: Stopwatch.GetElapsedTime(started).TotalSeconds),
                []));
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
    }
}
