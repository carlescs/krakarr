using Krakarr.Core.Domain;
using Krakarr.Media.CustomFormats;
using Krakarr.Media.Parsing;

namespace Krakarr.Series;

public sealed record EpisodeDecision
{
    public required ReleaseInfo Release { get; init; }
    public required ParsedRelease Parsed { get; init; }
    /// <summary>Library episodes the release maps to (empty when it could not be mapped).</summary>
    public IReadOnlyList<Episode> Episodes { get; init; } = [];
    public IReadOnlyList<string> Rejections { get; init; } = [];
    public Quality Quality => Parsed.Quality;
    /// <summary>Audio languages of the release (see <see cref="ReleaseLanguages.Resolve"/>).</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];
    /// <summary>Position of the release's best wanted language (lower is better); <see cref="int.MaxValue"/> when unwanted.</summary>
    public int LanguageRank { get; init; }
    /// <summary>Custom formats the release matches.</summary>
    public IReadOnlyList<MatchedFormat> CustomFormats { get; init; } = [];
    /// <summary>The profile's score for <see cref="CustomFormats"/>.</summary>
    public int CustomFormatScore { get; init; }
    public bool Approved => Rejections.Count == 0;
    public bool IsSeasonPack => Parsed.IsSeasonPack;
}

/// <summary>Restricts a search to a season and/or specific episodes.</summary>
public sealed record SearchScope(int? SeasonNumber, IReadOnlySet<int>? EpisodeIds);

/// <summary>Decides which releases are acceptable for a series and which episodes they cover.</summary>
public static class EpisodeDecisionEngine
{
    /// <param name="episodes">All episodes of the series, with <see cref="Episode.EpisodeFile"/> loaded.</param>
    /// <param name="requireMonitored">Automatic searches/RSS only grab monitored episodes.</param>
    /// <param name="languages">Wanted audio languages, most preferred first; null or empty = any.</param>
    /// <param name="formats">The library's custom formats (null = none).</param>
    public static IReadOnlyList<EpisodeDecision> Evaluate(
        Core.Domain.Series series, QualityProfile profile, IReadOnlyCollection<Episode> episodes, IEnumerable<ReleaseInfo> releases,
        SearchScope? scope, bool requireMonitored, DateOnly today, IReadOnlyList<string>? languages = null, CustomFormatSet? formats = null) =>
        Rank(releases.Select(r => Evaluate(series, profile, episodes, r, scope, requireMonitored, today, languages, formats)));

    public static EpisodeDecision Evaluate(
        Core.Domain.Series series, QualityProfile profile, IReadOnlyCollection<Episode> episodes, ReleaseInfo release,
        SearchScope? scope, bool requireMonitored, DateOnly today, IReadOnlyList<string>? languages = null, CustomFormatSet? formats = null)
    {
        var parsed = ReleaseParser.Parse(release.Title);
        var rejections = new List<string>();
        var releaseLanguages = ReleaseLanguages.Resolve(parsed, release);
        var languageRank = ReleaseLanguages.PreferenceRank(releaseLanguages, languages);
        formats ??= CustomFormatSet.Empty;
        var matched = formats.Count > 0
            ? formats.Match(new CustomFormatInput { Parsed = parsed, Languages = releaseLanguages, IndexerName = release.IndexerName, Size = release.Size })
            : [];
        var score = profile.FormatScore(matched.Select(m => m.Id));

        if (parsed.SeasonNumber is not { } season)
        {
            return new EpisodeDecision
            {
                Release = release, Parsed = parsed, Rejections = ["Not a TV episode or season release"],
                Languages = releaseLanguages, LanguageRank = languageRank ?? int.MaxValue,
                CustomFormats = matched, CustomFormatScore = score,
            };
        }

        // Series match
        var idMatch = (release.TvdbId is { } tvdb && tvdb == series.TvdbId) || (release.TmdbId is { } tmdb && tmdb == series.TmdbId);
        if (!idMatch)
        {
            if (!TitleMatches(series, parsed.Title))
            {
                rejections.Add($"Series mismatch (parsed \"{parsed.Title}\")");
            }
            else if (parsed.Year is { } y && series.Year > 0 && Math.Abs(y - series.Year) > 1)
            {
                rejections.Add($"Year mismatch ({y} vs {series.Year})");
            }
        }

        // Episode mapping (scene numbering first, see SceneNumbering)
        var mapped = new List<Episode>();
        if (parsed.IsSeasonPack)
        {
            mapped.AddRange(SceneNumbering.Season(episodes, season).Where(e => e.HasAired(today) || e.EpisodeFileId is not null));
            if (mapped.Count == 0) rejections.Add($"No aired episodes in season {season}");
        }
        else
        {
            foreach (var number in parsed.EpisodeNumbers)
            {
                var ep = SceneNumbering.Find(episodes, season, number);
                if (ep is null) rejections.Add($"Unknown episode {SeriesNaming.EpisodeCode(season, [number])}");
                else if (!mapped.Contains(ep)) mapped.Add(ep);
            }
        }

        // Never grab episodes before their air date (an unknown date counts as not aired). Season packs already
        // leave unaired episodes out; a single-episode release for one is a fake or a leak.
        var unaired = mapped.Where(e => !e.HasAired(today) && e.EpisodeFileId is null).ToList();
        if (unaired.Count > 0)
        {
            rejections.Add($"Not aired yet: {string.Join(", ", unaired.Select(e =>
                $"{SeriesNaming.EpisodeCode(e.SeasonNumber, [e.EpisodeNumber])} ({(e.AirDate is { } d ? d.ToString("yyyy-MM-dd") : "air date unknown")})"))}");
        }

        if (scope is not null)
        {
            // Mapped episodes carry the library's numbering, which may differ from the release's scene numbering.
            if (scope.SeasonNumber is { } wantedSeason
                && (mapped.Count == 0 ? season != wantedSeason : mapped.Any(e => e.SeasonNumber != wantedSeason)))
            {
                var actual = mapped.Count == 0 ? season.ToString() : string.Join("/", mapped.Select(e => e.SeasonNumber).Distinct().Order());
                rejections.Add($"Wrong season ({actual} instead of {wantedSeason})");
            }

            if (scope.EpisodeIds is { Count: > 0 } wanted && mapped.Count > 0 && !mapped.Any(e => wanted.Contains(e.Id)))
            {
                rejections.Add("Does not contain the requested episode");
            }
        }

        if (languageRank is null)
        {
            rejections.Add(ReleaseLanguages.Rejection(releaseLanguages, languages!));
        }

        // Quality
        if (parsed.Quality == Qualities.Unknown)
        {
            rejections.Add("Unknown quality");
        }
        else if (!profile.AllowedQualityIds.Contains(parsed.Quality.Id))
        {
            rejections.Add($"{parsed.Quality.Name} is not wanted in profile {profile.Name}");
        }

        // Size limits apply per episode.
        if (mapped.Count > 0 && release.Size > 0)
        {
            var perEpisodeMb = release.Size / mapped.Count / 1024 / 1024;
            if (profile.MinSizeMb > 0 && perEpisodeMb < profile.MinSizeMb)
                rejections.Add($"Too small ({perEpisodeMb} MB per episode < {profile.MinSizeMb} MB)");
            if (profile.MaxSizeMb > 0 && perEpisodeMb > profile.MaxSizeMb)
                rejections.Add($"Too large ({perEpisodeMb} MB per episode > {profile.MaxSizeMb} MB)");
        }

        if (release.Seeders is 0)
        {
            rejections.Add("No seeders");
        }

        if (score < profile.MinFormatScore)
        {
            rejections.Add($"Custom format score {score} is below the minimum ({profile.MinFormatScore})");
        }

        // Upgrades: every covered episode that already has a file must benefit.
        foreach (var ep in mapped.Where(e => e.EpisodeFile is not null))
        {
            var existing = Qualities.FindById(ep.EpisodeFile!.QualityId);
            var existingScore = formats.ScoreOf(ep.EpisodeFile, profile);
            var code = SeriesNaming.EpisodeCode(ep.SeasonNumber, [ep.EpisodeNumber]);
            switch (FormatUpgrade.Evaluate(profile, existing, existingScore, parsed.Quality, score))
            {
                case UpgradeVerdict.UpgradesDisabled:
                    rejections.Add($"{code} already has a file and upgrades are disabled");
                    break;
                case UpgradeVerdict.CutoffMet:
                    rejections.Add($"{code} meets cutoff ({FormatUpgrade.Describe(existing, existingScore)})");
                    break;
                case UpgradeVerdict.NotBetter:
                    rejections.Add($"Not an upgrade for {code} ({FormatUpgrade.Describe(existing, existingScore)})");
                    break;
            }
        }

        if (requireMonitored)
        {
            if (!series.Monitored) rejections.Add("Series is not monitored");
            var unmonitored = mapped.Where(e => !e.Monitored).ToList();
            if (unmonitored.Count > 0)
            {
                rejections.Add($"Not monitored: {string.Join(", ", unmonitored.Select(e => SeriesNaming.EpisodeCode(e.SeasonNumber, [e.EpisodeNumber])))}");
            }
        }

        return new EpisodeDecision
        {
            Release = release, Parsed = parsed, Episodes = mapped, Rejections = rejections.Distinct().ToList(),
            Languages = releaseLanguages, LanguageRank = languageRank ?? int.MaxValue,
            CustomFormats = matched, CustomFormatScore = score,
        };
    }

    /// <summary>
    /// Library episodes a release name covers, ignoring the ones the series does not know (lenient: used for
    /// manual downloads, whose files are mapped again at import). A season pack covers the season's aired episodes.
    /// </summary>
    public static IReadOnlyList<Episode> MapEpisodes(IReadOnlyCollection<Episode> episodes, ParsedRelease parsed, DateOnly today)
    {
        if (parsed.SeasonNumber is not { } season)
        {
            return [];
        }

        return parsed.IsSeasonPack
            ? SceneNumbering.Season(episodes, season).Where(e => e.HasAired(today) || e.EpisodeFileId is not null).ToList()
            : parsed.EpisodeNumbers
                .Select(n => SceneNumbering.Find(episodes, season, n))
                .OfType<Episode>()
                .Distinct()
                .ToList();
    }

    /// <summary>
    /// True when a parsed release title names the series: its title, its metadata title (when the user renamed it) or one
    /// of its loaded <see cref="Core.Domain.Series.AlternateTitles"/>, all normalised the same way.
    /// </summary>
    public static bool TitleMatches(Core.Domain.Series series, string? parsedTitle)
    {
        var normalized = ReleaseParser.NormalizeTitle(parsedTitle);
        return normalized.Length > 0
            && (normalized == SeriesNaming.NormalizedTitle(series.Title)
                || (series.MetadataTitle.Length > 0 && normalized == SeriesNaming.NormalizedTitle(series.MetadataTitle))
                || series.AlternateTitles.Any(t => normalized == SeriesNaming.NormalizedTitle(t.Title)));
    }

    /// <summary>Approved first, then preferred language, best quality, custom format score, larger coverage (season packs), propers, seeders, indexer priority.</summary>
    public static IReadOnlyList<EpisodeDecision> Rank(IEnumerable<EpisodeDecision> decisions) =>
        decisions
            .OrderByDescending(d => d.Approved)
            .ThenBy(d => d.LanguageRank)
            .ThenByDescending(d => d.Quality.Weight)
            .ThenByDescending(d => d.CustomFormatScore)
            .ThenByDescending(d => d.Episodes.Count)
            .ThenByDescending(d => d.Parsed.IsProper || d.Parsed.IsRepack)
            .ThenByDescending(d => d.Release.Seeders ?? 0)
            .ThenBy(d => d.Release.IndexerPriority)
            .ToList();

    /// <summary>
    /// Picks approved releases in rank order so that no episode is downloaded twice
    /// (episodes in <paramref name="alreadyCovered"/> — e.g. active downloads — are skipped too).
    /// </summary>
    public static IReadOnlyList<EpisodeDecision> SelectGrabs(IEnumerable<EpisodeDecision> ranked, IEnumerable<int> alreadyCovered)
    {
        var covered = alreadyCovered.ToHashSet();
        var picked = new List<EpisodeDecision>();
        foreach (var decision in ranked.Where(d => d.Approved && d.Episodes.Count > 0))
        {
            if (decision.Episodes.Any(e => covered.Contains(e.Id))) continue;
            picked.Add(decision);
            foreach (var e in decision.Episodes) covered.Add(e.Id);
        }

        return picked;
    }

    /// <summary>Monitored, aired episodes that are missing or below the profile cutoff (quality or custom format score).</summary>
    public static bool IsWanted(Episode episode, QualityProfile profile, DateOnly today, CustomFormatSet? formats = null)
    {
        if (!episode.Monitored || !episode.HasAired(today)) return false;
        if (episode.EpisodeFile is not { } file) return true;
        var score = (formats ?? CustomFormatSet.Empty).ScoreOf(file, profile);
        return FormatUpgrade.CutoffNotMet(profile, Qualities.FindById(file.QualityId), score);
    }
}
