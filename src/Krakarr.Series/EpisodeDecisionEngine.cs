using Krakarr.Core.Domain;
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
    public static IReadOnlyList<EpisodeDecision> Evaluate(
        Core.Domain.Series series, QualityProfile profile, IReadOnlyCollection<Episode> episodes, IEnumerable<ReleaseInfo> releases,
        SearchScope? scope, bool requireMonitored, DateOnly today) =>
        Rank(releases.Select(r => Evaluate(series, profile, episodes, r, scope, requireMonitored, today)));

    public static EpisodeDecision Evaluate(
        Core.Domain.Series series, QualityProfile profile, IReadOnlyCollection<Episode> episodes, ReleaseInfo release,
        SearchScope? scope, bool requireMonitored, DateOnly today)
    {
        var parsed = ReleaseParser.Parse(release.Title);
        var rejections = new List<string>();

        if (parsed.SeasonNumber is not { } season)
        {
            return new EpisodeDecision { Release = release, Parsed = parsed, Rejections = ["Not a TV episode or season release"] };
        }

        // Series match
        var idMatch = (release.TvdbId is { } tvdb && tvdb == series.TvdbId) || (release.TmdbId is { } tmdb && tmdb == series.TmdbId);
        if (!idMatch)
        {
            var normalized = ReleaseParser.NormalizeTitle(parsed.Title);
            if (normalized.Length == 0 || normalized != SeriesNaming.NormalizedTitle(series.Title))
            {
                rejections.Add($"Series mismatch (parsed \"{parsed.Title}\")");
            }
            else if (parsed.Year is { } y && series.Year > 0 && Math.Abs(y - series.Year) > 1)
            {
                rejections.Add($"Year mismatch ({y} vs {series.Year})");
            }
        }

        // Episode mapping
        var mapped = new List<Episode>();
        if (parsed.IsSeasonPack)
        {
            mapped.AddRange(episodes.Where(e => e.SeasonNumber == season && (e.HasAired(today) || e.EpisodeFileId is not null)));
            if (mapped.Count == 0) rejections.Add($"No aired episodes in season {season}");
        }
        else
        {
            foreach (var number in parsed.EpisodeNumbers)
            {
                var ep = episodes.FirstOrDefault(e => e.SeasonNumber == season && e.EpisodeNumber == number);
                if (ep is null) rejections.Add($"Unknown episode {SeriesNaming.EpisodeCode(season, [number])}");
                else mapped.Add(ep);
            }
        }

        if (scope is not null)
        {
            if (scope.SeasonNumber is { } wantedSeason && wantedSeason != season)
            {
                rejections.Add($"Wrong season ({season} instead of {wantedSeason})");
            }

            if (scope.EpisodeIds is { Count: > 0 } wanted && mapped.Count > 0 && !mapped.Any(e => wanted.Contains(e.Id)))
            {
                rejections.Add("Does not contain the requested episode");
            }
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

        // Upgrades: every covered episode that already has a file must benefit.
        var cutoff = Qualities.FindById(profile.CutoffQualityId);
        foreach (var ep in mapped.Where(e => e.EpisodeFile is not null))
        {
            var existing = Qualities.FindById(ep.EpisodeFile!.QualityId);
            var code = SeriesNaming.EpisodeCode(ep.SeasonNumber, [ep.EpisodeNumber]);
            if (!profile.UpgradeAllowed)
            {
                rejections.Add($"{code} already has a file and upgrades are disabled");
            }
            else if (existing.Weight >= cutoff.Weight)
            {
                rejections.Add($"{code} meets cutoff ({existing.Name})");
            }
            else if (parsed.Quality.Weight <= existing.Weight)
            {
                rejections.Add($"Not an upgrade for {code} ({existing.Name})");
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

        return new EpisodeDecision { Release = release, Parsed = parsed, Episodes = mapped, Rejections = rejections.Distinct().ToList() };
    }

    /// <summary>Approved first, then best quality, larger coverage (season packs), propers, seeders, indexer priority.</summary>
    public static IReadOnlyList<EpisodeDecision> Rank(IEnumerable<EpisodeDecision> decisions) =>
        decisions
            .OrderByDescending(d => d.Approved)
            .ThenByDescending(d => d.Quality.Weight)
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

    /// <summary>Monitored, aired episodes that are missing or below the profile cutoff.</summary>
    public static bool IsWanted(Episode episode, QualityProfile profile, DateOnly today)
    {
        if (!episode.Monitored || !episode.HasAired(today)) return false;
        if (episode.EpisodeFile is null) return true;
        return profile.UpgradeAllowed && Qualities.FindById(episode.EpisodeFile.QualityId).Weight < Qualities.FindById(profile.CutoffQualityId).Weight;
    }
}
