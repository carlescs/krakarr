using System.Diagnostics.Metrics;
using Krakarr.Core.Domain;
using Krakarr.Core.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Data;

/// <summary>
/// Turns saved changes into telemetry counters in one place: history events (grabbed, imported, failed, deleted,
/// subtitle downloaded), movies and series added or removed, and new requests. Does nothing unless metrics are exported.
/// </summary>
internal static class ChangeTelemetry
{
    private static bool Enabled =>
        KrakarrTelemetry.HistoryEvents.Enabled || KrakarrTelemetry.LibraryItemsAdded.Enabled
        || KrakarrTelemetry.LibraryItemsRemoved.Enabled || KrakarrTelemetry.MediaRequestsCreated.Enabled;

    /// <summary>Measurements to record once the save succeeds, or null when there are none.</summary>
    public static List<(Counter<long> Counter, KeyValuePair<string, object?>[] Tags)>? Collect(DbContext db)
    {
        if (!Enabled) return null;
        List<(Counter<long>, KeyValuePair<string, object?>[])>? pending = null;
        foreach (var entry in db.ChangeTracker.Entries())
        {
            var item = (entry.State, entry.Entity) switch
            {
                (EntityState.Added, HistoryEntry h) => (KrakarrTelemetry.HistoryEvents, (KeyValuePair<string, object?>[])
                [
                    new(KrakarrTelemetry.EventTag, h.EventType.ToString().ToLowerInvariant()),
                    new(KrakarrTelemetry.MediaTypeTag, h.MovieId is not null ? "movie" : h.SeriesId is not null ? "series" : "other"),
                ]),
                (EntityState.Added, Movie) => (KrakarrTelemetry.LibraryItemsAdded, [new(KrakarrTelemetry.MediaTypeTag, "movie")]),
                (EntityState.Added, Series) => (KrakarrTelemetry.LibraryItemsAdded, [new(KrakarrTelemetry.MediaTypeTag, "series")]),
                (EntityState.Deleted, Movie) => (KrakarrTelemetry.LibraryItemsRemoved, [new(KrakarrTelemetry.MediaTypeTag, "movie")]),
                (EntityState.Deleted, Series) => (KrakarrTelemetry.LibraryItemsRemoved, [new(KrakarrTelemetry.MediaTypeTag, "series")]),
                (EntityState.Added, MediaRequest r) => (KrakarrTelemetry.MediaRequestsCreated, [new(KrakarrTelemetry.MediaTypeTag, KrakarrTelemetry.MediaTypeValue(r.MediaType))]),
                _ => default((Counter<long>, KeyValuePair<string, object?>[])?),
            };
            if (item is { } i) (pending ??= []).Add(i);
        }

        return pending;
    }

    public static void Record(List<(Counter<long> Counter, KeyValuePair<string, object?>[] Tags)>? pending)
    {
        if (pending is null) return;
        foreach (var (counter, tags) in pending) counter.Add(1, tags);
    }
}
