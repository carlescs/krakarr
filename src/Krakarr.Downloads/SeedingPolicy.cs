using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;

namespace Krakarr.Downloads;

/// <summary>Decides when a finished torrent has seeded enough. Shared by every download client.</summary>
public static class SeedingPolicy
{
    /// <summary>
    /// True once the torrent has completed, its media (if it belongs to a movie/series) has been imported,
    /// and the ratio or time limit has been reached.
    /// </summary>
    public static bool IsDone(DownloadClientSettings settings, Download row, double ratio, DateTimeOffset now)
    {
        if (row.CompletedAt is not { } completed)
        {
            return false;
        }

        if ((row.MovieId is not null || row.SeriesId is not null) && !row.Imported)
        {
            return false; // never pull files out from under a pending import
        }

        if (settings.SeedRatioLimit > 0 && ratio >= settings.SeedRatioLimit)
        {
            return true;
        }

        return settings.SeedTimeLimitMinutes > 0 && now - completed >= TimeSpan.FromMinutes(settings.SeedTimeLimitMinutes);
    }

    /// <summary>
    /// Imported media downloads were copied or hardlinked into the library, so their download data can go.
    /// Anything else (manual downloads) keeps its files.
    /// </summary>
    public static bool DeleteDataOnRemove(Download row) => row.Imported && (row.MovieId is not null || row.SeriesId is not null);
}
