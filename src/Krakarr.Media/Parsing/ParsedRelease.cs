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
    /// <summary>Dynamic range formats tagged in the name (<see cref="ReleaseTags.Hdr"/> values).</summary>
    public IReadOnlyList<string> Hdr { get; init; } = [];
    /// <summary>Normalized video codec (<see cref="ReleaseTags.Codecs"/> values), or null.</summary>
    public string? VideoCodec { get; init; }
    /// <summary>Audio formats tagged in the name (<see cref="ReleaseTags.Audio"/> values), best first.</summary>
    public IReadOnlyList<string> Audio { get; init; } = [];
    /// <summary>Audio channel layout ("7.1", "5.1", "2.0"), or null.</summary>
    public string? AudioChannels { get; init; }
    /// <summary>Editions tagged in the name (<see cref="ReleaseTags.Editions"/> values), in order of appearance.</summary>
    public IReadOnlyList<string> Editions { get; init; } = [];
    /// <summary>Streaming service the release was taken from (<see cref="ReleaseTags.StreamingServices"/> codes), or null.</summary>
    public string? StreamingService { get; init; }
    public bool Is3D { get; init; }
    public bool HasHardcodedSubs { get; init; }
    public bool IsUpscaled { get; init; }
    public bool IsSeasonPack => SeasonNumber is not null && EpisodeNumbers.Count == 0;
    public bool IsEpisode => EpisodeNumbers.Count > 0;
    /// <summary>Dolby Vision with no HDR10/HDR10+ fallback layer: plays with wrong colours on non-DV displays.</summary>
    public bool IsDolbyVisionOnly => Hdr.Contains(ReleaseTags.DolbyVision) && !Hdr.Contains(ReleaseTags.Hdr10) && !Hdr.Contains(ReleaseTags.Hdr10Plus);
}
