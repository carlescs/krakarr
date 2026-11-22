using Krakarr.Core.Events;

namespace Krakarr.Indexers;

/// <summary>Streams an interactive search's per-indexer progress to the UI, which tags the request with a search id.</summary>
public static class IndexerSearchProgress
{
    private const int MaxSearchIdLength = 64;

    /// <summary>A progress sink that publishes <see cref="EventNames.SearchProgress"/>, or null when the client passed no (valid) search id.</summary>
    public static IProgress<IReadOnlyList<IndexerQueryStatus>>? For(IEventHub hub, string? searchId) =>
        string.IsNullOrWhiteSpace(searchId) || searchId.Length > MaxSearchIdLength ? null : new Publisher(hub, searchId);

    private sealed class Publisher(IEventHub hub, string searchId) : IProgress<IReadOnlyList<IndexerQueryStatus>>
    {
        // Fire and forget: a slow or broken SignalR connection must not hold up the search. The final HTTP response carries the full status anyway.
        public void Report(IReadOnlyList<IndexerQueryStatus> value) => _ = PublishAsync(value);

        private async Task PublishAsync(IReadOnlyList<IndexerQueryStatus> value)
        {
            try
            {
                await hub.PublishAsync(EventNames.SearchProgress, new { SearchId = searchId, Indexers = value });
            }
            catch (Exception)
            {
                // Best effort: the UI still gets the final status with the response.
            }
        }
    }
}
