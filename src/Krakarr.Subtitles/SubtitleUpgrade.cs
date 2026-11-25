using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;

namespace Krakarr.Subtitles;

/// <summary>Decides which of a file's subtitles are worth searching again for a better match.</summary>
public static class SubtitleUpgrade
{
    /// <summary>Provider of subtitle files found next to the video; they have no score and are never replaced.</summary>
    public const string DiskProvider = "disk";

    /// <summary>
    /// The wanted languages whose subtitle may be replaced, with the score a replacement has to beat. Forced subtitles
    /// and languages with a subtitle found on disk are left alone.
    /// <para>
    /// <paramref name="all"/> (a search started by hand) takes every downloaded subtitle. Otherwise only subtitles
    /// downloaded automatically in the last <see cref="SubtitleSettings.UpgradeDays"/> days and scoring below
    /// <see cref="SubtitleSettings.UpgradeBelowScore"/> are taken.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, int> Select(IEnumerable<SubtitleFile> rows, SubtitleSettings settings, DateTimeOffset now, bool all)
    {
        var result = new Dictionary<string, int>();
        if (!all && settings.UpgradeDays <= 0) return result;
        var since = now - TimeSpan.FromDays(Math.Max(0, settings.UpgradeDays));

        foreach (var group in rows.Where(r => !r.Forced && settings.Languages.Contains(r.Language)).GroupBy(r => r.Language))
        {
            if (group.Any(r => r.Provider == DiskProvider)) continue;
            var score = group.Max(r => r.Score ?? 0);
            if (!all)
            {
                if (group.Any(r => r.Manual || r.Score is null)) continue;
                if (score >= settings.UpgradeBelowScore) continue;
                if (group.Max(r => r.DateAdded) < since) continue;
            }

            result[group.Key] = score;
        }

        return result;
    }
}
