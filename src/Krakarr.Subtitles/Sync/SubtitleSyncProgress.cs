using System.Globalization;
using System.Text.RegularExpressions;
using Krakarr.Core.Jobs;

namespace Krakarr.Subtitles.Sync;

/// <summary>
/// Reports subtitle syncs to <see cref="BackgroundTasks"/>, which lists them in the tasks menu of the top bar: waiting,
/// then with their progress, then with their outcome. One task per subtitle (<see cref="TaskId"/>).
/// </summary>
public sealed partial class SubtitleSyncProgress(BackgroundTasks tasks)
{
    public const string Kind = "subtitle";
    public const string CancelledResult = BackgroundTasks.CancelledMessage;

    public static string TaskId(int subtitleId) => $"subtitles.sync.{subtitleId}";

    /// <summary>
    /// Shows a sync as waiting; see <see cref="BackgroundTasks.Queue"/>. <paramref name="ownerId"/> is the user who asked
    /// for it (only they see it), null for automatic syncs (staff see those). Returns the token cancelling it trips.
    /// </summary>
    public CancellationToken Queue(int subtitleId, string title, string link, int? ownerId = null) =>
        tasks.Queue(TaskId(subtitleId), Kind, "Sync " + title, link, ownerId);

    public bool Cancel(int subtitleId) => tasks.Cancel(TaskId(subtitleId));

    public void Stage(int subtitleId, string stage) => tasks.Stage(TaskId(subtitleId), stage);

    public void Report(int subtitleId, double percent) => tasks.Report(TaskId(subtitleId), percent);

    /// <summary>Reports the percentage found in a line a sync tool printed, if any.</summary>
    public void ReportLine(int subtitleId, string line)
    {
        if (ParsePercent(line) is { } percent) Report(subtitleId, percent);
    }

    public void Complete(int subtitleId, bool success, string result, bool cancelled = false) =>
        tasks.Complete(TaskId(subtitleId), success, result, cancelled);

    /// <summary>Stops showing a sync that ended without an outcome; keeps finished ones.</summary>
    public void Finish(int subtitleId) => tasks.Finish(TaskId(subtitleId));

    /// <summary>
    /// The last percentage in a progress line, as printed by ffsubsync (tqdm: " 45%|████▌ | 123/273") or alass
    /// ("123 / 273 [====>----] 45.05 %"). Null when the line has none.
    /// </summary>
    public static double? ParsePercent(string line)
    {
        var matches = PercentPattern().Matches(line);
        if (matches.Count == 0) return null;
        var value = double.Parse(matches[^1].Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        return value is >= 0 and <= 100 ? value : null;
    }

    [GeneratedRegex(@"(?<![\d.,])(\d{1,3}(?:[.,]\d+)?)\s?%")]
    private static partial Regex PercentPattern();
}
