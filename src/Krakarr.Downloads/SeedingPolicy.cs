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
    /// A finished media download whose import failed: it is stopped rather than left seeding. Its files stay so the
    /// import can be retried; dismissing the issue turns it back into a plain torrent the user can resume.
    /// </summary>
    public static bool ImportFailed(Download row) =>
        row.CompletedAt is not null && !row.Imported && row.ImportMessage is not null
        && (row.MovieId is not null || row.SeriesId is not null);

    /// <summary>
    /// Imported media downloads were copied or hardlinked into the library, so their download data can go.
    /// Anything else (manual downloads) keeps its files.
    /// </summary>
    public static bool DeleteDataOnRemove(Download row) => row.Imported && (row.MovieId is not null || row.SeriesId is not null);
}
