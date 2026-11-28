using Krakarr.Core.Domain;

namespace Krakarr.Media.CustomFormats;

public enum UpgradeVerdict
{
    Upgrade,
    UpgradesDisabled,
    CutoffMet,
    NotBetter,
}

/// <summary>Upgrade rules shared by movies and episodes: quality first, then custom format score up to the profile's "upgrade until".</summary>
public static class FormatUpgrade
{
    /// <summary>True when an existing file still has room to improve under the profile (quality or score below the cutoffs).</summary>
    public static bool CutoffNotMet(QualityProfile profile, Quality existing, int existingScore) =>
        profile.UpgradeAllowed
        && (existing.Weight < Qualities.FindById(profile.CutoffQualityId).Weight || existingScore < profile.CutoffFormatScore);

    /// <summary>Whether a release upgrades an existing file.</summary>
    public static UpgradeVerdict Evaluate(QualityProfile profile, Quality existing, int existingScore, Quality candidate, int candidateScore)
    {
        if (!profile.UpgradeAllowed) return UpgradeVerdict.UpgradesDisabled;

        var qualityCutoffMet = existing.Weight >= Qualities.FindById(profile.CutoffQualityId).Weight;
        var scoreCutoffMet = existingScore >= profile.CutoffFormatScore;
        if (qualityCutoffMet && scoreCutoffMet) return UpgradeVerdict.CutoffMet;
        if (candidate.Weight < existing.Weight) return UpgradeVerdict.NotBetter;

        // A better quality is an upgrade until the quality cutoff; past it (or at the same quality) only a better score is.
        if (candidate.Weight > existing.Weight && !qualityCutoffMet) return UpgradeVerdict.Upgrade;
        return !scoreCutoffMet && candidateScore > existingScore ? UpgradeVerdict.Upgrade : UpgradeVerdict.NotBetter;
    }

    /// <summary>"Bluray-1080p" or "Bluray-1080p, score 150".</summary>
    public static string Describe(Quality quality, int score) => score == 0 ? quality.Name : $"{quality.Name}, score {score}";
}
