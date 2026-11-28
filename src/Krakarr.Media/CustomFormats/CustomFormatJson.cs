using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krakarr.Core.Domain;

namespace Krakarr.Media.CustomFormats;

/// <summary>Which app's numbering a custom format JSON uses (sources and languages are numbered differently).</summary>
public enum CustomFormatApp
{
    Radarr,
    Sonarr,
}

public sealed record CustomFormatImportResult(IReadOnlyList<CustomFormat> Formats, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads and writes custom formats in the JSON shape Radarr and Sonarr use (and TRaSH Guides publish), so formats can be pasted in
/// from either. Conditions only Krakarr has are written with Krakarr implementation names, which Radarr/Sonarr will not read.
/// </summary>
public static class CustomFormatJson
{
    /// <summary>Largest JSON accepted for an import.</summary>
    public const int MaxImportLength = 1024 * 1024;

    private const int MaxFormats = 500;

    private static readonly Dictionary<string, CustomFormatCondition> Implementations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ReleaseTitleSpecification"] = CustomFormatCondition.ReleaseTitle,
        ["ReleaseGroupSpecification"] = CustomFormatCondition.ReleaseGroup,
        ["EditionSpecification"] = CustomFormatCondition.Edition,
        ["LanguageSpecification"] = CustomFormatCondition.Language,
        ["SourceSpecification"] = CustomFormatCondition.Source,
        ["ResolutionSpecification"] = CustomFormatCondition.Resolution,
        ["QualityModifierSpecification"] = CustomFormatCondition.QualityModifier,
        ["SizeSpecification"] = CustomFormatCondition.Size,
        ["ReleaseTypeSpecification"] = CustomFormatCondition.ReleaseType,
        ["KrakarrIndexerSpecification"] = CustomFormatCondition.Indexer,
        ["KrakarrHdrSpecification"] = CustomFormatCondition.Hdr,
        ["KrakarrVideoCodecSpecification"] = CustomFormatCondition.VideoCodec,
        ["KrakarrAudioSpecification"] = CustomFormatCondition.Audio,
        ["KrakarrStreamingServiceSpecification"] = CustomFormatCondition.StreamingService,
        ["KrakarrFlagSpecification"] = CustomFormatCondition.Flag,
    };

    private static readonly Dictionary<CustomFormatCondition, string> ImplementationNames = new()
    {
        [CustomFormatCondition.ReleaseTitle] = "ReleaseTitleSpecification",
        [CustomFormatCondition.ReleaseGroup] = "ReleaseGroupSpecification",
        [CustomFormatCondition.Edition] = "EditionSpecification",
        [CustomFormatCondition.Language] = "LanguageSpecification",
        [CustomFormatCondition.Source] = "SourceSpecification",
        [CustomFormatCondition.Resolution] = "ResolutionSpecification",
        [CustomFormatCondition.QualityModifier] = "QualityModifierSpecification",
        [CustomFormatCondition.Size] = "SizeSpecification",
        [CustomFormatCondition.ReleaseType] = "ReleaseTypeSpecification",
        [CustomFormatCondition.Indexer] = "KrakarrIndexerSpecification",
        [CustomFormatCondition.Hdr] = "KrakarrHdrSpecification",
        [CustomFormatCondition.VideoCodec] = "KrakarrVideoCodecSpecification",
        [CustomFormatCondition.Audio] = "KrakarrAudioSpecification",
        [CustomFormatCondition.StreamingService] = "KrakarrStreamingServiceSpecification",
        [CustomFormatCondition.Flag] = "KrakarrFlagSpecification",
    };

    // Radarr's source numbering.
    private static readonly Dictionary<int, QualitySource> RadarrSources = new()
    {
        [1] = QualitySource.Cam, [2] = QualitySource.Telesync, [3] = QualitySource.Telecine, [4] = QualitySource.Workprint,
        [5] = QualitySource.Dvd, [6] = QualitySource.Tv, [7] = QualitySource.WebDl, [8] = QualitySource.WebRip, [9] = QualitySource.Bluray,
    };

    // Sonarr's source numbering (2 = raw HD TV, 7 = Bluray raw, i.e. remux, handled separately).
    private static readonly Dictionary<int, QualitySource> SonarrSources = new()
    {
        [1] = QualitySource.Tv, [2] = QualitySource.Tv, [3] = QualitySource.WebDl, [4] = QualitySource.WebRip,
        [5] = QualitySource.Dvd, [6] = QualitySource.Bluray,
    };

    // Language ids Radarr and Sonarr share.
    private static readonly Dictionary<int, string> SharedLanguages = new()
    {
        [1] = "en", [2] = "fr", [3] = "es", [4] = "de", [5] = "it", [6] = "da", [7] = "nl", [8] = "ja", [9] = "is", [10] = "zh",
        [11] = "ru", [12] = "pl", [13] = "vi", [14] = "sv", [15] = "no", [16] = "fi", [17] = "tr", [18] = "pt", [19] = "nl", [20] = "el",
        [21] = "ko", [22] = "hu", [23] = "he", [24] = "lt", [25] = "cs",
    };

    // Radarr's ids beyond the shared ones.
    private static readonly Dictionary<int, string> RadarrLanguages = new(SharedLanguages)
    {
        [26] = "hi", [27] = "ro", [28] = "th", [29] = "bg", [30] = "pt-br", [31] = "ar", [32] = "uk", [33] = "fa", [35] = "sk", [36] = "lv",
        [37] = "es-419", [38] = "ca", [39] = "hr", [40] = "sr", [42] = "et", [43] = "ta", [44] = "id", [45] = "te", [47] = "sl",
    };

    public static CustomFormatImportResult Import(string json, CustomFormatApp app)
    {
        if (json.Length > MaxImportLength) throw new FormatException("The JSON is too large");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid JSON: {ex.Message}");
        }

        var items = root switch
        {
            JsonArray array => array.OfType<JsonObject>().ToList(),
            JsonObject obj => [obj],
            _ => throw new FormatException("Expected a custom format object or an array of them"),
        };
        if (items.Count > MaxFormats) throw new FormatException($"Import at most {MaxFormats} custom formats at a time");

        var formats = new List<CustomFormat>();
        var warnings = new List<string>();
        foreach (var item in items)
        {
            var name = Str(item["name"])?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                warnings.Add("Skipped a custom format without a name");
                continue;
            }

            var specs = new List<CustomFormatSpecification>();
            string? problem = null;
            foreach (var specNode in (item["specifications"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var (spec, error) = ReadSpecification(specNode, app);
                if (spec is null)
                {
                    problem = error;
                    break;
                }

                specs.Add(spec);
            }

            if (problem is not null)
            {
                // Dropping only the condition would make the format match releases it was meant to exclude.
                warnings.Add($"Skipped \"{name}\": {problem}");
                continue;
            }

            if (specs.Count == 0)
            {
                warnings.Add($"Skipped \"{name}\": it has no conditions");
                continue;
            }

            formats.Add(new CustomFormat { Name = name.Length > 100 ? name[..100] : name, Specifications = specs });
        }

        return new CustomFormatImportResult(formats, warnings);
    }

    public static JsonArray Export(IEnumerable<CustomFormat> formats, CustomFormatApp app) =>
        new(formats.Select(f => (JsonNode)new JsonObject
        {
            ["name"] = f.Name,
            ["includeCustomFormatWhenRenaming"] = false,
            ["specifications"] = new JsonArray(f.Specifications.Select(s => (JsonNode)WriteSpecification(s, app)).ToArray()),
        }).ToArray());

    private static (CustomFormatSpecification? Spec, string? Error) ReadSpecification(JsonObject node, CustomFormatApp app)
    {
        var name = Str(node["name"]) ?? string.Empty;
        var implementation = Str(node["implementation"]) ?? string.Empty;
        if (!Implementations.TryGetValue(implementation, out var condition))
        {
            return (null, $"condition \"{name}\" uses {implementation}, which Krakarr does not support");
        }

        var fields = ReadFields(node["fields"]);
        var value = fields.GetValueOrDefault("value");
        var spec = new CustomFormatSpecification
        {
            Name = name,
            Condition = condition,
            Negate = Bool(node["negate"]),
            Required = Bool(node["required"]),
        };

        switch (condition)
        {
            case CustomFormatCondition.Size:
                spec = spec with { Min = Number(fields.GetValueOrDefault("min")) ?? 0, Max = Number(fields.GetValueOrDefault("max")) ?? 0 };
                break;
            case CustomFormatCondition.Language:
                var languageId = Int(value);
                string? code = languageId is { } lid
                    ? (app == CustomFormatApp.Radarr ? RadarrLanguages : SharedLanguages).GetValueOrDefault(lid)
                    : Str(value);
                if (code is null || !MediaLanguages.All.Any(l => l.Code == code))
                {
                    return (null, $"condition \"{name}\" uses a language Krakarr cannot match ({Str(value)})");
                }

                spec = spec with { Value = code, ExceptLanguage = Bool(fields.GetValueOrDefault("exceptLanguage")) };
                break;
            case CustomFormatCondition.Source:
                var sourceId = Int(value);
                if (sourceId is null)
                {
                    spec = spec with { Value = Str(value) ?? string.Empty };
                    break;
                }

                if (app == CustomFormatApp.Sonarr && sourceId == 7)
                {
                    spec = spec with { Condition = CustomFormatCondition.QualityModifier, Value = "Remux" };
                    break;
                }

                if (!(app == CustomFormatApp.Radarr ? RadarrSources : SonarrSources).TryGetValue(sourceId.Value, out var source))
                {
                    return (null, $"condition \"{name}\" uses an unknown source ({sourceId})");
                }

                spec = spec with { Value = source.ToString() };
                break;
            case CustomFormatCondition.QualityModifier:
                var modifier = Int(value);
                if (modifier is not null && modifier != 5)
                {
                    return (null, $"condition \"{name}\" uses a quality modifier Krakarr does not track ({modifier})");
                }

                spec = spec with { Value = "Remux" };
                break;
            case CustomFormatCondition.ReleaseType:
                spec = spec with
                {
                    Value = Int(value) switch
                    {
                        1 => CustomFormatSet.SingleEpisode,
                        2 => CustomFormatSet.MultiEpisode,
                        3 => CustomFormatSet.SeasonPack,
                        _ => Str(value) ?? string.Empty,
                    },
                };
                break;
            default:
                spec = spec with { Value = Str(value) ?? string.Empty };
                break;
        }

        if (CustomFormatSet.Validate(spec) is { } invalid)
        {
            return (null, $"condition \"{name}\": {invalid}");
        }

        return (spec, null);
    }

    private static JsonObject WriteSpecification(CustomFormatSpecification spec, CustomFormatApp app)
    {
        JsonObject fields;
        switch (spec.Condition)
        {
            case CustomFormatCondition.Size:
                fields = new JsonObject { ["min"] = spec.Min, ["max"] = spec.Max };
                break;
            case CustomFormatCondition.Language:
                var map = app == CustomFormatApp.Radarr ? RadarrLanguages : SharedLanguages;
                var id = map.Where(kv => kv.Value == spec.Value).Select(kv => (int?)kv.Key).FirstOrDefault();
                fields = new JsonObject { ["value"] = id is { } i ? JsonValue.Create(i) : JsonValue.Create(spec.Value), ["exceptLanguage"] = spec.ExceptLanguage };
                break;
            case CustomFormatCondition.Source when Enum.TryParse<QualitySource>(spec.Value, true, out var source):
                var sources = app == CustomFormatApp.Radarr ? RadarrSources : SonarrSources;
                var sid = sources.Where(kv => kv.Value == source).Select(kv => (int?)kv.Key).FirstOrDefault();
                fields = new JsonObject { ["value"] = sid is { } s ? JsonValue.Create(s) : JsonValue.Create(spec.Value) };
                break;
            case CustomFormatCondition.QualityModifier:
                fields = new JsonObject { ["value"] = 5 };
                break;
            case CustomFormatCondition.Resolution when int.TryParse(spec.Value, out var resolution):
                fields = new JsonObject { ["value"] = resolution };
                break;
            case CustomFormatCondition.ReleaseType:
                fields = new JsonObject
                {
                    ["value"] = spec.Value switch
                    {
                        CustomFormatSet.SingleEpisode => 1,
                        CustomFormatSet.MultiEpisode => 2,
                        CustomFormatSet.SeasonPack => 3,
                        _ => 0,
                    },
                };
                break;
            default:
                fields = new JsonObject { ["value"] = spec.Value };
                break;
        }

        var implementation = spec.Condition == CustomFormatCondition.QualityModifier && app == CustomFormatApp.Sonarr
            ? "SourceSpecification"
            : ImplementationNames[spec.Condition];
        if (implementation == "SourceSpecification" && spec.Condition == CustomFormatCondition.QualityModifier)
        {
            fields = new JsonObject { ["value"] = 7 };
        }

        return new JsonObject
        {
            ["name"] = spec.Name,
            ["implementation"] = implementation,
            ["negate"] = spec.Negate,
            ["required"] = spec.Required,
            ["fields"] = fields,
        };
    }

    /// <summary>Fields as TRaSH writes them (<c>{"value": ...}</c>) or as the Radarr API does (<c>[{"name": "value", "value": ...}]</c>).</summary>
    private static Dictionary<string, JsonNode?> ReadFields(JsonNode? node)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj) result[key] = value;
                break;
            case JsonArray array:
                foreach (var field in array.OfType<JsonObject>())
                {
                    if (Str(field["name"]) is { } key) result[key] = field["value"];
                }

                break;
        }

        return result;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v
        ? v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        }
        : null;

    private static bool Bool(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i) ? i
        : node is JsonValue s && s.GetValueKind() == JsonValueKind.String && int.TryParse(s.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p
        : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? d : null;
}
