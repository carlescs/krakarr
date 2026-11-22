using System.Runtime.CompilerServices;
using System.Text.Json;
using Krakarr.Core.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Krakarr.Indexers;

/// <summary>
/// One line of a streamed interactive search. <c>type</c> is:
/// <list type="bullet">
/// <item><c>indexers</c>: first, every indexer being queried (all pending).</item>
/// <item><c>releases</c>: an indexer finished (<c>indexer</c>); <c>releases</c> are its new or better rows, which take the place of the rows whose
/// release guids are in <c>replaced</c> (the same release from another indexer).</item>
/// <item><c>done</c>: last, the final <c>indexers</c> and the release guids of every row in rank order (<c>order</c>).</item>
/// </list>
/// </summary>
public sealed record SearchStreamEvent<T>(string Type)
{
    public IReadOnlyList<IndexerQueryStatus>? Indexers { get; init; }
    public IndexerQueryStatus? Indexer { get; init; }
    public IReadOnlyList<T>? Releases { get; init; }
    public IReadOnlyList<string>? Replaced { get; init; }
    public IReadOnlyList<string>? Order { get; init; }
}

public static class SearchStream
{
    public const string ContentType = "application/x-ndjson";

    /// <summary>
    /// Turns a running search into stream events, de-duplicating across indexers and turning releases into rows with
    /// <paramref name="evaluate"/>, which must return them ranked best first (it also ranks the final list).
    /// </summary>
    public static async IAsyncEnumerable<SearchStreamEvent<T>> EventsAsync<T>(
        this IndexerSearchRun run,
        Func<IReadOnlyList<ReleaseInfo>, IReadOnlyList<T>> evaluate,
        Func<T, ReleaseInfo> releaseOf,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var statuses = run.Indexers.ToList();
        yield return new SearchStreamEvent<T>("indexers") { Indexers = statuses.ToList() };

        var merger = new ReleaseMerger();
        await foreach (var result in run.Results.WithCancellation(ct))
        {
            var index = statuses.FindIndex(s => s.IndexerId == result.Status.IndexerId);
            if (index >= 0) statuses[index] = result.Status;
            var (added, replaced) = merger.Add(result.Releases);
            yield return new SearchStreamEvent<T>("releases")
            {
                Indexer = result.Status,
                Releases = added.Count > 0 ? evaluate(added) : [],
                Replaced = replaced,
            };
        }

        var order = evaluate(merger.Releases.ToList()).Select(r => releaseOf(r).Guid).ToList();
        yield return new SearchStreamEvent<T>("done") { Indexers = statuses, Order = order };
    }

    /// <summary>Whether the client asked for a streamed (newline-delimited JSON) response.</summary>
    public static bool Requested(HttpRequest request) =>
        request.Headers.Accept.Any(a => a?.Contains(ContentType, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Writes the events as newline-delimited JSON, flushing each line so the client sees it at once.</summary>
    public static IResult Result<T>(IAsyncEnumerable<SearchStreamEvent<T>> events) => new NdjsonResult<T>(events);

    private sealed class NdjsonResult<T>(IAsyncEnumerable<SearchStreamEvent<T>> events) : IResult
    {
        private static readonly byte[] NewLine = "\n"u8.ToArray();

        public async Task ExecuteAsync(HttpContext context)
        {
            var options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            var response = context.Response;
            var ct = context.RequestAborted;
            response.ContentType = ContentType;
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no"; // don't let a reverse proxy (nginx) hold the lines back
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await foreach (var e in events.WithCancellation(ct))
            {
                await JsonSerializer.SerializeAsync(response.Body, e, options, ct);
                await response.Body.WriteAsync(NewLine, ct);
                await response.Body.FlushAsync(ct);
            }
        }
    }
}
