namespace Krakarr.Subtitles.Sync;

/// <summary>A time fix-up: t → t × <see cref="Ratio"/> + <see cref="OffsetMs"/>, and how many cues it lines up.</summary>
public sealed record Alignment(double OffsetMs, double Ratio, int Matched, int Total)
{
    public double MatchedFraction => Total == 0 ? 0 : (double)Matched / Total;

    public string Describe()
    {
        var text = $"{(OffsetMs >= 0 ? "+" : "")}{OffsetMs / 1000:0.000} s";
        if (Math.Abs(Ratio - 1) > 1e-6) text += $", framerate × {Ratio:0.0000}";
        return text;
    }
}

/// <summary>
/// Lines a subtitle up with a reference subtitle of the same video (another language is fine: only the timing is
/// compared) by finding the constant shift, and the common framerate conversion, that make the most cue starts
/// coincide. The fallback when neither alass nor ffsubsync is installed; it can't fix cuts or breaks.
/// </summary>
public static class SubtitleAligner
{
    /// <summary>Framerate conversions to try: none, PAL speed-up (25 ↔ 23.976/24) and 24 ↔ 23.976.</summary>
    public static readonly double[] Ratios =
    [
        1, 25 / 23.976, 23.976 / 25, 25.0 / 24, 24.0 / 25, 24 / 23.976, 23.976 / 24,
    ];

    private const double MaxOffsetMs = 180_000;
    private const double BinMs = 100;
    private const double MatchToleranceMs = 300;
    private const int MinMatched = 8;
    private const double MinMatchedFraction = 0.4;

    /// <summary>The best fix-up, or null when the subtitles don't line up convincingly with any of them.</summary>
    public static Alignment? Align(IReadOnlyList<Cue> reference, IReadOnlyList<Cue> subtitle)
    {
        if (reference.Count < MinMatched || subtitle.Count < MinMatched) return null;
        var refStarts = reference.Select(c => c.Start).Order().ToArray();
        var starts = subtitle.Select(c => c.Start).ToArray();

        Alignment? best = null;
        var bins = new int[(int)(2 * MaxOffsetMs / BinMs) + 1];
        foreach (var ratio in Ratios)
        {
            // Vote: every pair of starts within reach suggests an offset; the most suggested one wins.
            Array.Clear(bins);
            foreach (var start in starts)
            {
                var t = start * ratio;
                var from = LowerBound(refStarts, t - MaxOffsetMs);
                for (var i = from; i < refStarts.Length && refStarts[i] <= t + MaxOffsetMs; i++)
                    bins[(int)((refStarts[i] - t + MaxOffsetMs) / BinMs)]++;
            }

            var top = 0;
            var topVotes = -1;
            for (var i = 0; i < bins.Length; i++)
            {
                var votes = bins[i] + (i > 0 ? bins[i - 1] : 0) + (i + 1 < bins.Length ? bins[i + 1] : 0);
                if (votes > topVotes) { topVotes = votes; top = i; }
            }

            var offset = top * BinMs - MaxOffsetMs + BinMs / 2;
            // Refine: the median gap of the cues this offset lines up.
            var gaps = MatchedGaps(refStarts, starts, ratio, offset);
            if (gaps.Count == 0) continue;
            gaps.Sort();
            offset += gaps[gaps.Count / 2];
            var matched = MatchedGaps(refStarts, starts, ratio, offset).Count;
            var candidate = new Alignment(Math.Round(offset), ratio, matched, starts.Length);
            // Prefer no framerate change unless another ratio clearly lines up more cues.
            if (best is null || matched > best.Matched * (best.Ratio == 1 ? 1.1 : 1.0)) best = candidate;
        }

        if (best is null) return null;
        var needed = Math.Max(MinMatched, MinMatchedFraction * Math.Min(starts.Length, refStarts.Length));
        return best.Matched >= needed ? best : null;
    }

    private static List<double> MatchedGaps(double[] refStarts, double[] starts, double ratio, double offset)
    {
        var gaps = new List<double>();
        foreach (var start in starts)
        {
            var t = start * ratio + offset;
            var i = LowerBound(refStarts, t - MatchToleranceMs);
            if (i < refStarts.Length && refStarts[i] <= t + MatchToleranceMs)
            {
                // Nearest reference start within tolerance.
                var nearest = refStarts[i];
                for (var j = i + 1; j < refStarts.Length && refStarts[j] <= t + MatchToleranceMs; j++)
                    if (Math.Abs(refStarts[j] - t) < Math.Abs(nearest - t)) nearest = refStarts[j];
                gaps.Add(nearest - t);
            }
        }

        return gaps;
    }

    private static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}
