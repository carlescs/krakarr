using System.Runtime.CompilerServices;
using Krakarr.Core.Domain;

namespace Krakarr.Indexers.Tests;

public class SearchStreamTests
{
    private static ReleaseInfo Release(string guid, string title, int priority = 25, int? seeders = null, string? hash = null) => new()
    {
        Title = title, Guid = guid, Size = 100, IndexerPriority = priority, Seeders = seeders, InfoHash = hash,
    };

    [Fact]
    public void Merger_keeps_the_copy_from_the_preferred_indexer()
    {
        var merger = new ReleaseMerger();
        var (added, replaced) = merger.Add([Release("a1", "X.1080p", priority: 25, seeders: 5), Release("b1", "Y.720p", hash: "H")]);
        Assert.Equal(["a1", "b1"], added.Select(r => r.Guid));
        Assert.Empty(replaced);

        // Same title+size (case-insensitive) from a higher-priority indexer replaces it; the same hash with lower priority doesn't.
        (added, replaced) = merger.Add([Release("a2", "x.1080P", priority: 10), Release("b2", "Other", priority: 50, hash: "h")]);
        Assert.Equal(["a2"], added.Select(r => r.Guid));
        Assert.Equal(["a1"], replaced);

        // Equal priority: more seeders wins; within one batch only the winner comes out and nothing earlier is replaced.
        (added, replaced) = merger.Add([Release("c1", "Z", seeders: 1), Release("c2", "Z", seeders: 9)]);
        Assert.Equal(["c2"], added.Select(r => r.Guid));
        Assert.Empty(replaced);

        Assert.Equal(["a2", "b1", "c2"], merger.Releases.Select(r => r.Guid));
    }

    private static async IAsyncEnumerable<IndexerQueryResult> Results(IEnumerable<IndexerQueryResult> items, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    [Fact]
    public async Task Events_stream_rows_per_indexer_and_end_with_the_ranking()
    {
        var a = new IndexerQueryStatus(1, "A", IndexerQueryState.Pending);
        var b = new IndexerQueryStatus(2, "B", IndexerQueryState.Pending);
        var run = new IndexerSearchRun([a, b], Results(
        [
            new(b with { State = IndexerQueryState.Done, Releases = 2 }, [Release("b1", "Low", priority: 50, seeders: 1), Release("b2", "Dup", priority: 50, seeders: 3)]),
            new(a with { State = IndexerQueryState.Done, Releases = 1 }, [Release("a1", "Dup", priority: 10, seeders: 2)]),
        ], TestContext.Current.CancellationToken));

        // Rows are the guids, ranked by seeders.
        IReadOnlyList<string> Evaluate(IReadOnlyList<ReleaseInfo> releases) =>
            releases.OrderByDescending(r => r.Seeders).Select(r => r.Guid).ToList();
        var lookup = new Dictionary<string, ReleaseInfo>();
        var events = new List<SearchStreamEvent<string>>();
        await foreach (var e in run.EventsAsync(rs => { foreach (var r in rs) lookup[r.Guid] = r; return Evaluate(rs); }, g => lookup[g],
            TestContext.Current.CancellationToken))
        {
            events.Add(e);
        }

        Assert.Equal(["indexers", "releases", "releases", "done"], events.Select(e => e.Type));
        Assert.All(events[0].Indexers!, s => Assert.Equal(IndexerQueryState.Pending, s.State));
        Assert.Equal("B", events[1].Indexer!.IndexerName);
        Assert.Equal(["b2", "b1"], events[1].Releases!);
        Assert.Equal(["a1"], events[2].Releases!);
        Assert.Equal(["b2"], events[2].Replaced!);
        Assert.All(events[3].Indexers!, s => Assert.Equal(IndexerQueryState.Done, s.State));
        Assert.Equal(["a1", "b1"], events[3].Order!);
    }
}
