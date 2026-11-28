using Krakarr.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Media.CustomFormats;

/// <summary>
/// Holds the compiled <see cref="CustomFormatSet"/> so searches, RSS syncs and imports compile the formats' regexes once, not per
/// release or per run. One set for the whole library (bounded by the number of formats); edits call <see cref="Invalidate"/>.
/// </summary>
public sealed class CustomFormatCache
{
    private volatile CustomFormatSet? _set;
    private int _version;

    public async Task<CustomFormatSet> GetAsync(IQueryable<CustomFormat> formats, CancellationToken ct = default)
    {
        if (_set is { } cached) return cached;
        var version = Volatile.Read(ref _version);
        var compiled = CustomFormatSet.Compile(await formats.AsNoTracking().OrderBy(f => f.Id).ToListAsync(ct));
        // A concurrent edit may have invalidated the cache while loading; keep the result for this caller only then.
        if (Volatile.Read(ref _version) == version) _set = compiled;
        return compiled;
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _set = null;
    }
}
