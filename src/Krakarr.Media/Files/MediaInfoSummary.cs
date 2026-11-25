using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Krakarr.Core.Domain;

namespace Krakarr.Media.Files;

/// <summary>An audio track as stored: codec, channel count (0 = unknown) and language (Krakarr code, raw tag, or null).</summary>
public sealed record AudioSummary(
    [property: JsonPropertyName("c")] string? Codec,
    [property: JsonPropertyName("ch")] int Channels,
    [property: JsonPropertyName("l")] string? Language);

/// <summary>
/// Compact media info stored on movie and episode files (a small JSON document): the main video track, the audio tracks
/// and the duration. "" in the column means the file was read but nothing could be found; null means not read yet.
/// </summary>
public sealed record MediaInfoSummary
{
    /// <summary>Most audio tracks kept; files with more are rare and the column is bounded.</summary>
    public const int MaxAudioTracks = 16;

    /// <summary>Length of the database column.</summary>
    public const int MaxLength = 2048;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [JsonPropertyName("d")] public int? DurationSeconds { get; init; }
    [JsonPropertyName("vc")] public string? VideoCodec { get; init; }
    [JsonPropertyName("w")] public int Width { get; init; }
    [JsonPropertyName("h")] public int Height { get; init; }
    [JsonPropertyName("bd")] public int? BitDepth { get; init; }
    /// <summary>HDR10, HLG, DV or "DV HDR10"; null for SDR or unknown.</summary>
    [JsonPropertyName("dr")] public string? DynamicRange { get; init; }
    [JsonPropertyName("a")] public IReadOnlyList<AudioSummary> Audio { get; init; } = [];

    /// <summary>Resolution class of the video (2160, 1080, 720, 480), or 0 when unknown. Wide (cropped) frames count by their width.</summary>
    [JsonIgnore]
    public int Resolution => (Width, Height) switch
    {
        (<= 0, <= 0) => 0,
        var (w, h) when w >= 3200 || h >= 1800 => 2160,
        var (w, h) when w >= 1700 || h >= 900 => 1080,
        var (w, h) when w >= 1100 || h >= 600 => 720,
        _ => 480,
    };

    /// <summary>Distinct known audio languages, in track order.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> AudioLanguages => Audio.Select(a => a.Language).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Channel layout of the first audio track ("5.1", "2.0"), or null.</summary>
    [JsonIgnore]
    public string? AudioChannels => Audio.Count > 0 ? FormatChannels(Audio[0].Channels) : null;

    public static string? FormatChannels(int channels) => channels switch
    {
        <= 0 => null,
        >= 6 => string.Create(CultureInfo.InvariantCulture, $"{channels - 1}.1"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{channels}.0"),
    };

    /// <summary>The summary of what the reader found, or null when it found nothing.</summary>
    public static MediaInfoSummary? From(MediaInfo info)
    {
        if (info.IsEmpty) return null;
        // The largest video track is the main one; others are usually cover art or previews.
        var video = info.Video.OrderByDescending(v => (long)v.Width * v.Height).FirstOrDefault();
        return new MediaInfoSummary
        {
            DurationSeconds = info.Duration is { } d ? (int)Math.Round(d.TotalSeconds) : null,
            VideoCodec = Short(video?.Codec),
            Width = video?.Width ?? 0,
            Height = video?.Height ?? 0,
            BitDepth = video?.BitDepth,
            DynamicRange = video?.DynamicRange,
            Audio = info.Audio.Take(MaxAudioTracks).Select(a => new AudioSummary(Short(a.Codec), a.Channels, TrackLanguage(a.Language))).ToList(),
        };
    }

    /// <summary>The stored form: JSON within <see cref="MaxLength"/>, or "" when there is nothing to store.</summary>
    public static string Serialize(MediaInfoSummary? summary)
    {
        if (summary is null) return string.Empty;
        var json = JsonSerializer.Serialize(summary, Json);
        while (json.Length > MaxLength && summary.Audio.Count > 0)
        {
            summary = summary with { Audio = summary.Audio.Take(summary.Audio.Count - 1).ToList() };
            json = JsonSerializer.Serialize(summary, Json);
        }

        return json.Length <= MaxLength ? json : string.Empty;
    }

    /// <summary>Reads the stored form; null when not read yet, unreadable, or empty.</summary>
    public static MediaInfoSummary? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<MediaInfoSummary>(value, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a file and returns its stored form ("" when nothing could be read).</summary>
    public static string ReadFile(string path) => Serialize(From(MediaInfoReader.Read(path)));

    /// <summary>Track language tag ("spa", "pt-BR", "es-ES") to a Krakarr code when known, else the lower-cased tag; "und" gives null.</summary>
    public static string? TrackLanguage(string? tag)
    {
        tag = tag?.Trim();
        if (string.IsNullOrEmpty(tag) || tag.Equals("und", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("mul", StringComparison.OrdinalIgnoreCase) || tag.Equals("zxx", StringComparison.OrdinalIgnoreCase)) return null;
        if (MediaLanguages.Normalize(tag) is { } code) return code;
        if (Iso6392.TryGetValue(tag, out code)) return code;
        var dash = tag.IndexOf('-');
        if (dash > 0)
        {
            var primary = tag[..dash];
            if (MediaLanguages.Normalize(primary) is { } p) return p;
            if (Iso6392.TryGetValue(primary, out p)) return p;
        }

        return tag.Length <= 12 ? tag.ToLowerInvariant() : null;
    }

    /// <summary>ISO 639-2 codes (B and T forms) that <see cref="MediaLanguages"/> doesn't list as tokens.</summary>
    private static readonly Dictionary<string, string> Iso6392 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deu"] = "de", ["dut"] = "nl", ["dan"] = "da", ["fin"] = "fi", ["nob"] = "no", ["nno"] = "no", ["isl"] = "is", ["ice"] = "is",
        ["ces"] = "cs", ["slk"] = "sk", ["slo"] = "sk", ["ron"] = "ro", ["rum"] = "ro", ["bul"] = "bg", ["ell"] = "el", ["gre"] = "el",
        ["ara"] = "ar", ["fas"] = "fa", ["per"] = "fa", ["hin"] = "hi", ["tam"] = "ta", ["tel"] = "te", ["vie"] = "vi", ["tha"] = "th",
        ["ind"] = "id", ["hrv"] = "hr", ["srp"] = "sr", ["slv"] = "sl", ["est"] = "et", ["lav"] = "lv", ["lit"] = "lt", ["eus"] = "eu",
        ["baq"] = "eu", ["glg"] = "gl", ["zho"] = "zh", ["fra"] = "fr", ["por"] = "pt", ["jpn"] = "ja", ["kor"] = "ko",
    };

    private static string? Short(string? s) => s is null ? null : s.Length <= 24 ? s : s[..24];
}
