using Krakarr.Core.Domain;

namespace Krakarr.Media.Parsing;

public sealed record ParsedRelease
{
    public required string ReleaseTitle { get; init; }
    /// <summary>Parsed media title (movie or series), human readable.</summary>
    public string? Title { get; init; }
    public int? Year { get; init; }
    public Quality Quality { get; init; } = Qualities.Unknown;
    public bool IsProper { get; init; }
    public bool IsRepack { get; init; }
    public string? ReleaseGroup { get; init; }
    public int? SeasonNumber { get; init; }
    public IReadOnlyList<int> EpisodeNumbers { get; init; } = [];
    /// <summary>Audio languages tagged in the name (<see cref="MediaLanguages"/> codes), in order of appearance.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];
    /// <summary>Tagged MULTi / DUAL: several audio tracks, usually including the original (English) one.</summary>
    public bool IsMultiLanguage { get; init; }
    public bool IsSeasonPack => SeasonNumber is not null && EpisodeNumbers.Count == 0;
    public bool IsEpisode => EpisodeNumbers.Count > 0;
}
