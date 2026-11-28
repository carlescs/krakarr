using System.Text.RegularExpressions;
using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Media.CustomFormats;

/// <summary>What custom format conditions look at: a parsed release plus what the indexer said about it.</summary>
public sealed record CustomFormatInput
{
    public required ParsedRelease Parsed { get; init; }
    /// <summary>Audio languages of the release (<see cref="ReleaseLanguages.Resolve"/>).</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];
    public string? IndexerName { get; init; }
    /// <summary>Size in bytes (0 = unknown: size conditions do not match).</summary>
    public long Size { get; init; }

    public static CustomFormatInput From(ParsedRelease parsed, ReleaseInfo release) => new()
    {
        Parsed = parsed,
        Languages = ReleaseLanguages.Resolve(parsed, release),
        IndexerName = release.IndexerName,
        Size = release.Size,
    };
}

/// <summary>A custom format a release matched.</summary>
public sealed record MatchedFormat(int Id, string Name);

/// <summary>
/// The library's custom formats with their regular expressions compiled once, so a search evaluates every release against them
/// without compiling anything per release. Immutable and safe to share between threads.
/// </summary>
public sealed class CustomFormatSet
{
    /// <summary>User regexes run with a timeout so a pathological pattern cannot hang a search.</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static CustomFormatSet Empty { get; } = new([]);

    private readonly CompiledFormat[] _formats;

    private CustomFormatSet(CompiledFormat[] formats) => _formats = formats;

    public int Count => _formats.Length;

    public static CustomFormatSet Compile(IEnumerable<CustomFormat> formats) =>
        new(formats.Where(f => f.Specifications.Count > 0).Select(Compile).ToArray());

    /// <summary>Custom formats the release matches, in format order.</summary>
    public IReadOnlyList<MatchedFormat> Match(CustomFormatInput input)
    {
        List<MatchedFormat>? matched = null;
        foreach (var format in _formats)
        {
            if (format.IsMatch(input)) (matched ??= []).Add(new MatchedFormat(format.Id, format.Name));
        }

        return matched ?? (IReadOnlyList<MatchedFormat>)[];
    }

    /// <summary>
    /// Custom formats of a library file: those stored at import, or, for files imported before custom formats existed,
    /// those its scene name matches now.
    /// </summary>
    public IReadOnlyList<int> FormatIdsOf(ILibraryFile file)
    {
        if (file.CustomFormatIds is { } stored) return stored;
        if (string.IsNullOrWhiteSpace(file.SceneName) || _formats.Length == 0) return [];
        var parsed = ReleaseParser.Parse(file.SceneName);
        return Match(new CustomFormatInput { Parsed = parsed, Languages = parsed.Languages }).Select(m => m.Id).ToList();
    }

    /// <summary>The library file's custom format score under a profile (its current scores, not those at import).</summary>
    public int ScoreOf(ILibraryFile file, QualityProfile profile) =>
        profile.FormatScores.Count == 0 ? 0 : profile.FormatScore(FormatIdsOf(file));

    /// <summary>Returns an error message when a specification cannot be used (bad regex, unknown value), otherwise null.</summary>
    public static string? Validate(CustomFormatSpecification spec)
    {
        switch (spec.Condition)
        {
            case CustomFormatCondition.ReleaseTitle or CustomFormatCondition.ReleaseGroup or CustomFormatCondition.Edition or CustomFormatCondition.Indexer:
                if (string.IsNullOrWhiteSpace(spec.Value)) return "a regular expression is required";
                try
                {
                    _ = new Regex(spec.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                    return null;
                }
                catch (ArgumentException ex)
                {
                    return $"invalid regular expression: {ex.Message}";
                }
            case CustomFormatCondition.Size:
                return spec.Min < 0 || spec.Max < 0 ? "sizes cannot be negative"
                    : spec.Max > 0 && spec.Min > spec.Max ? "the minimum size is larger than the maximum"
                    : spec.Min == 0 && spec.Max == 0 ? "set a minimum or a maximum size"
                    : null;
            default:
                var allowed = AllowedValues(spec.Condition);
                return allowed.Contains(spec.Value, StringComparer.OrdinalIgnoreCase) ? null : $"\"{spec.Value}\" is not one of {string.Join(", ", allowed)}";
        }
    }

    /// <summary>Values a non-regex condition accepts.</summary>
    public static IReadOnlyList<string> AllowedValues(CustomFormatCondition condition) => condition switch
    {
        CustomFormatCondition.Language => MediaLanguages.All.Select(l => l.Code).ToList(),
        CustomFormatCondition.Source => SourceValues,
        CustomFormatCondition.Resolution => ["480", "576", "720", "1080", "2160"],
        CustomFormatCondition.QualityModifier => ["Remux"],
        CustomFormatCondition.Hdr => [.. ReleaseTags.Hdr, HdrAny, DolbyVisionOnly],
        CustomFormatCondition.VideoCodec => ReleaseTags.Codecs,
        CustomFormatCondition.Audio => [.. ReleaseTags.Audio, "7.1", "5.1", "2.0", "1.0"],
        CustomFormatCondition.StreamingService => ReleaseTags.StreamingServices.Keys.ToList(),
        CustomFormatCondition.ReleaseType => ReleaseTypes,
        CustomFormatCondition.Flag => ReleaseTags.Flags,
        _ => [],
    };

    public const string HdrAny = "HDR";
    public const string DolbyVisionOnly = "DV-only";
    public const string SingleEpisode = "Single episode";
    public const string MultiEpisode = "Multi-episode";
    public const string SeasonPack = "Season pack";

    private static readonly IReadOnlyList<string> ReleaseTypes = [SingleEpisode, MultiEpisode, SeasonPack];

    private static readonly IReadOnlyList<string> SourceValues =
    [
        nameof(QualitySource.Cam), nameof(QualitySource.Telesync), nameof(QualitySource.Telecine), nameof(QualitySource.Workprint),
        nameof(QualitySource.Dvd), nameof(QualitySource.Tv), nameof(QualitySource.WebDl), nameof(QualitySource.WebRip), nameof(QualitySource.Bluray),
    ];

    private static CompiledFormat Compile(CustomFormat format) =>
        new(format.Id, format.Name, format.Specifications
            .GroupBy(s => s.Condition)
            .Select(g => g.Select(s => new CompiledSpec(Compile(s), s.Negate, s.Required)).ToArray())
            .ToArray());

    private static Func<CustomFormatInput, bool> Compile(CustomFormatSpecification spec)
    {
        var value = spec.Value.Trim();
        switch (spec.Condition)
        {
            case CustomFormatCondition.ReleaseTitle:
                return CompileRegex(value) is { } title ? i => SafeMatch(title, i.Parsed.ReleaseTitle) : Never;
            case CustomFormatCondition.ReleaseGroup:
                return CompileRegex(value) is { } group ? i => i.Parsed.ReleaseGroup is { } g && SafeMatch(group, g) : Never;
            case CustomFormatCondition.Edition:
                return CompileRegex(value) is { } edition ? i => i.Parsed.Editions.Count > 0 && SafeMatch(edition, string.Join(' ', i.Parsed.Editions)) : Never;
            case CustomFormatCondition.Indexer:
                return CompileRegex(value) is { } indexer ? i => i.IndexerName is { Length: > 0 } n && SafeMatch(indexer, n) : Never;
            case CustomFormatCondition.Language:
                var except = spec.ExceptLanguage;
                return i => except
                    ? i.Languages.Any(l => !l.Equals(value, StringComparison.OrdinalIgnoreCase))
                    : i.Languages.Contains(value, StringComparer.OrdinalIgnoreCase);
            case CustomFormatCondition.Source:
                if (!Enum.TryParse<QualitySource>(value, ignoreCase: true, out var source) || source == QualitySource.Unknown) return Never;
                // Like Radarr, a remux's source is Bluray (the remux part is a modifier).
                return source == QualitySource.Bluray
                    ? i => i.Parsed.Quality.Source is QualitySource.Bluray or QualitySource.Remux
                    : i => i.Parsed.Quality.Source == source;
            case CustomFormatCondition.Resolution:
                return int.TryParse(value, out var resolution) ? i => i.Parsed.Quality.Resolution == resolution : Never;
            case CustomFormatCondition.QualityModifier:
                return value.Equals("Remux", StringComparison.OrdinalIgnoreCase) ? i => i.Parsed.Quality.Source == QualitySource.Remux : Never;
            case CustomFormatCondition.Size:
                var min = (long)(spec.Min * 1024 * 1024 * 1024);
                var max = (long)(spec.Max * 1024 * 1024 * 1024);
                return i => i.Size > 0 && i.Size >= min && (max <= 0 || i.Size <= max);
            case CustomFormatCondition.Hdr:
                if (value.Equals(HdrAny, StringComparison.OrdinalIgnoreCase)) return i => i.Parsed.Hdr.Count > 0;
                if (value.Equals(DolbyVisionOnly, StringComparison.OrdinalIgnoreCase)) return i => i.Parsed.IsDolbyVisionOnly;
                return i => i.Parsed.Hdr.Contains(value, StringComparer.OrdinalIgnoreCase);
            case CustomFormatCondition.VideoCodec:
                return i => string.Equals(i.Parsed.VideoCodec, value, StringComparison.OrdinalIgnoreCase);
            case CustomFormatCondition.Audio:
                return value.Length > 0 && char.IsAsciiDigit(value[0])
                    ? i => i.Parsed.AudioChannels == value
                    : i => i.Parsed.Audio.Contains(value, StringComparer.OrdinalIgnoreCase);
            case CustomFormatCondition.StreamingService:
                return i => string.Equals(i.Parsed.StreamingService, value, StringComparison.OrdinalIgnoreCase);
            case CustomFormatCondition.ReleaseType:
                if (value.Equals(SeasonPack, StringComparison.OrdinalIgnoreCase)) return i => i.Parsed.IsSeasonPack;
                if (value.Equals(MultiEpisode, StringComparison.OrdinalIgnoreCase)) return i => i.Parsed.EpisodeNumbers.Count > 1;
                if (value.Equals(SingleEpisode, StringComparison.OrdinalIgnoreCase)) return i => i.Parsed.EpisodeNumbers.Count == 1;
                return Never;
            case CustomFormatCondition.Flag:
                if (value.Equals(ReleaseTags.Flag3D, StringComparison.OrdinalIgnoreCase)) return i => i.Parsed.Is3D;
                if (value.Equals(ReleaseTags.FlagHardcodedSubs, StringComparison.OrdinalIgnoreCase)) return i => i.Parsed.HasHardcodedSubs;
                if (value.Equals(ReleaseTags.FlagUpscaled, StringComparison.OrdinalIgnoreCase)) return i => i.Parsed.IsUpscaled;
                return Never;
            default:
                return Never;
        }
    }

    private static bool Never(CustomFormatInput _) => false;

    private static Regex? CompileRegex(string pattern)
    {
        if (pattern.Length == 0) return null;
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool SafeMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private readonly record struct CompiledSpec(Func<CustomFormatInput, bool> Test, bool Negate, bool Required)
    {
        public bool IsSatisfiedBy(CustomFormatInput input) => Test(input) != Negate;
    }

    /// <summary>Conditions grouped by kind: each group needs its required conditions and at least one condition to match (Radarr's rule).</summary>
    private sealed record CompiledFormat(int Id, string Name, CompiledSpec[][] Groups)
    {
        public bool IsMatch(CustomFormatInput input)
        {
            foreach (var group in Groups)
            {
                var any = false;
                foreach (var spec in group)
                {
                    var ok = spec.IsSatisfiedBy(input);
                    if (!ok && spec.Required) return false;
                    any |= ok;
                }

                if (!any) return false;
            }

            return true;
        }
    }
}

/// <summary>Records on a library file which custom formats its release matched, so upgrades compare against them later.</summary>
public static class LibraryFileFormats
{
    public static void Apply(ILibraryFile file, CustomFormatSet formats, QualityProfile? profile, string? releaseTitle, string? indexerName)
    {
        if (string.IsNullOrWhiteSpace(releaseTitle))
        {
            file.CustomFormatIds = [];
            file.CustomFormatScore = 0;
            return;
        }

        var parsed = ReleaseParser.Parse(releaseTitle);
        var release = new ReleaseInfo { Title = releaseTitle, Guid = string.Empty, IndexerName = indexerName ?? string.Empty, Size = file is MovieFile m ? m.Size : file is EpisodeFile e ? e.Size : 0 };
        var ids = formats.Match(CustomFormatInput.From(parsed, release)).Select(f => f.Id).ToList();
        file.CustomFormatIds = ids;
        file.CustomFormatScore = profile?.FormatScore(ids) ?? 0;
    }
}
