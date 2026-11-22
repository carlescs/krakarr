using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Http;
using Krakarr.Media.Files;
using Krakarr.Media.Parsing;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Subtitles;

/// <summary>SubDL.com REST API (v1). Searches by IMDb/TMDB id or title and downloads zip archives from dl.subdl.com.</summary>
public sealed class SubdlProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration) : ISubtitleProvider
{
    public const string ProviderName = "subdl";
    public const string HttpClientName = "subdl";
    private const string UserAgent = "Krakarr v0.1.0";

    public string Name => ProviderName;

    private string DefaultBaseUrl => (configuration["Subdl:BaseUrl"] ?? "https://api.subdl.com/api/v1/").TrimEnd('/') + "/";
    private string DefaultDownloadBaseUrl => (configuration["Subdl:DownloadBaseUrl"] ?? "https://dl.subdl.com").TrimEnd('/');

    public bool IsConfigured(SubtitleSettings settings) =>
        settings.Subdl.Enabled && !string.IsNullOrWhiteSpace(settings.Subdl.ApiKey);

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default)
    {
        var parameters = BuildSearchParameters(query, settings.Subdl.ApiKey ?? string.Empty);
        using var request = NewRequest(HttpMethod.Get, DefaultBaseUrl + "subtitles?" + parameters);
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsBoundedStreamAsync(BoundedContent.MaxSubtitleBytes, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ParseSearchResults(doc, query);
    }

    internal static string BuildSearchParameters(SubtitleQuery q, string apiKey)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_key"] = apiKey.Trim(),
            ["languages"] = string.Join(',', q.Languages.Select(ToSubdlLanguage).Distinct().Order(StringComparer.Ordinal)),
            ["releases"] = "1",
            ["subs_per_page"] = "30",
        };

        var imdb = NormalizeImdbId(q.ImdbId);
        if (q.MediaType == MediaType.Series)
        {
            p["type"] = "tv";
            if (imdb is not null) p["imdb_id"] = imdb;
            else if (q.TmdbId is { } tmdb) p["tmdb_id"] = tmdb.ToString(CultureInfo.InvariantCulture);
            else p["film_name"] = q.Title;
            if (q.SeasonNumber is { } s) p["season_number"] = s.ToString(CultureInfo.InvariantCulture);
            if (q.EpisodeNumber is { } e) p["episode_number"] = e.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            p["type"] = "movie";
            if (imdb is not null) p["imdb_id"] = imdb;
            else if (q.TmdbId is { } tmdb) p["tmdb_id"] = tmdb.ToString(CultureInfo.InvariantCulture);
            else
            {
                p["film_name"] = q.Title;
                if (q.Year > 0) p["year"] = q.Year.ToString(CultureInfo.InvariantCulture);
            }
        }

        return string.Join('&', p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Maps a Krakarr language code to SubDL's uppercase request code.</summary>
    internal static string ToSubdlLanguage(string code) => code.ToLowerInvariant() switch
    {
        "pt-br" => "BR_PT",
        "zh-cn" => "ZH",
        "zh-tw" => "ZH_BG",
        var c => c.ToUpperInvariant(),
    };

    /// <summary>IMDb id with "tt" prefix ("tt0133093" or "133093" → "tt0133093").</summary>
    internal static string? NormalizeImdbId(string? imdb)
    {
        if (string.IsNullOrWhiteSpace(imdb)) return null;
        var digits = imdb.Trim().TrimStart('t', 'T');
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return null;
        return "tt" + digits.PadLeft(7, '0');
    }

    internal static List<SubtitleCandidate> ParseSearchResults(JsonDocument doc, SubtitleQuery query)
    {
        var results = new List<SubtitleCandidate>();
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return results;

        if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.False)
        {
            var error = Str(root, "error") ?? Str(root, "message");
            if (error is not null && IsNotFoundMessage(error)) return results;
            throw new SubtitleProviderException($"SubDL request failed{(error is null ? "" : $": {error}")}");
        }

        if (!root.TryGetProperty("subtitles", out var subs) || subs.ValueKind != JsonValueKind.Array) return results;

        string? featureTitle = null;
        int? featureYear = null;
        if (root.TryGetProperty("results", out var media) && media.ValueKind == JsonValueKind.Array && media.GetArrayLength() > 0)
        {
            var first = media[0];
            featureTitle = Str(first, "name");
            featureYear = PositiveInt(Raw(first, "year"));
        }

        var byId = NormalizeImdbId(query.ImdbId) is not null || query.TmdbId is not null;

        foreach (var item in subs.EnumerateArray())
        {
            var url = Str(item, "url");
            var language = Languages.Normalize(Str(item, "language")) ?? Languages.Normalize(Str(item, "lang"));
            if (url is null || language is null) continue;

            var releaseName = Str(item, "release_name");
            var name = Str(item, "name");
            var releases = new List<string>();
            if (!string.IsNullOrWhiteSpace(releaseName)) releases.Add(releaseName);
            if (item.TryGetProperty("releases", out var relArr) && relArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in relArr.EnumerateArray())
                {
                    if (r.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.GetString()))
                        releases.Add(r.GetString()!);
                }
            }

            var cleanedName = CleanArchiveName(name);
            if (!string.IsNullOrWhiteSpace(cleanedName)) releases.Add(cleanedName);

            results.Add(new SubtitleCandidate
            {
                Provider = ProviderName,
                Payload = url,
                Language = language,
                ReleaseNames = releases.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FileName = cleanedName ?? name,
                HearingImpaired = Bool(item, "hi"),
                SeasonNumber = PositiveInt(Raw(item, "season")),
                EpisodeNumber = PositiveInt(Raw(item, "episode")),
                FeatureTitle = featureTitle,
                FeatureYear = featureYear,
                MatchedById = byId,
            });
        }

        return results;
    }

    private static string? CleanArchiveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = name.Trim();
        const string prefix = "SUBDL.com::";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        if (s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static bool IsNotFoundMessage(string message) =>
        message.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || message.Contains("can't find", StringComparison.OrdinalIgnoreCase)
        || message.Contains("no subtitle", StringComparison.OrdinalIgnoreCase);

    public async Task<SubtitleContent> DownloadAsync(SubtitleCandidate candidate, SubtitleSettings settings, CancellationToken ct = default)
    {
        var link = candidate.Payload.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || candidate.Payload.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? candidate.Payload
            : $"{DefaultDownloadBaseUrl}/{candidate.Payload.TrimStart('/')}";

        using var request = NewRequest(HttpMethod.Get, link);
        using var response = await SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new SubtitleProviderException($"SubDL subtitle download failed with HTTP {(int)response.StatusCode}");

        byte[] bytes;
        try
        {
            bytes = await response.Content.ReadAsByteArrayAsync(BoundedContent.MaxSubtitleBytes, ct);
        }
        catch (ResponseTooLargeException ex)
        {
            throw new SubtitleProviderException($"SubDL subtitle download failed: {ex.Message}");
        }

        return ExtractSubtitle(bytes, candidate);
    }

    internal static SubtitleContent ExtractSubtitle(byte[] bytes, SubtitleCandidate candidate)
    {
        if (IsZipArchive(bytes))
        {
            try
            {
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                var entries = archive.Entries
                    .Where(e => e.Length > 0 && !string.IsNullOrEmpty(e.Name) && SidecarFiles.IsSubtitle(e.Name))
                    .ToList();
                if (entries.Count == 0)
                    throw new SubtitleProviderException("SubDL archive did not contain any supported subtitle file");

                var chosen = PickBestEntry(entries, candidate);
                if (chosen.Length > BoundedContent.MaxSubtitleBytes)
                    throw new SubtitleProviderException("SubDL archive entry exceeds maximum subtitle size");

                using var entryStream = chosen.Open();
                using var output = new MemoryStream((int)chosen.Length);
                var buffer = new byte[16384];
                long total = 0;
                int read;
                while ((read = entryStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > BoundedContent.MaxSubtitleBytes)
                        throw new SubtitleProviderException("SubDL archive entry exceeds maximum subtitle size");
                    output.Write(buffer, 0, read);
                }

                var ext = Path.GetExtension(chosen.Name).ToLowerInvariant();
                return new SubtitleContent(output.ToArray(), string.IsNullOrEmpty(ext) ? ".srt" : ext);
            }
            catch (InvalidDataException ex)
            {
                throw new SubtitleProviderException($"SubDL returned a corrupt zip archive: {ex.Message}");
            }
        }

        var candidateExt = Path.GetExtension(candidate.FileName ?? candidate.Payload).ToLowerInvariant();
        if (!SidecarFiles.SubtitleExtensions.Contains(candidateExt, StringComparer.OrdinalIgnoreCase))
            candidateExt = ".srt";
        return new SubtitleContent(bytes, candidateExt);
    }

    private static ZipArchiveEntry PickBestEntry(List<ZipArchiveEntry> entries, SubtitleCandidate candidate)
    {
        if (entries.Count == 1) return entries[0];

        return entries
            .OrderByDescending(e => MatchesEpisode(e.Name, candidate))
            .ThenByDescending(e => MatchesRelease(e.Name, candidate))
            .ThenByDescending(e => IsTextSubtitle(e.Name))
            .ThenByDescending(e => e.Length)
            .First();
    }

    private static bool MatchesEpisode(string entryName, SubtitleCandidate candidate)
    {
        if (candidate.EpisodeNumber is not { } ep) return true;
        var parsed = ReleaseParser.Parse(Path.GetFileNameWithoutExtension(entryName));
        if (parsed.EpisodeNumbers.Count == 0) return false;
        return parsed.EpisodeNumbers.Contains(ep) && (candidate.SeasonNumber is null || parsed.SeasonNumber is null || parsed.SeasonNumber == candidate.SeasonNumber);
    }

    private static bool MatchesRelease(string entryName, SubtitleCandidate candidate)
    {
        var stem = Path.GetFileNameWithoutExtension(entryName);
        return candidate.ReleaseNames.Any(r => string.Equals(r, stem, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTextSubtitle(string entryName) =>
        Path.GetExtension(entryName).ToLowerInvariant() is ".srt" or ".ass" or ".ssa" or ".vtt";

    private static bool IsZipArchive(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;

    public async Task<string> TestAsync(SubtitleSettings settings, CancellationToken ct = default)
    {
        if (!IsConfigured(settings)) throw new SubtitleProviderException("A SubDL API key is required");

        var url = $"{DefaultBaseUrl}subtitles?api_key={Uri.EscapeDataString(settings.Subdl.ApiKey!.Trim())}&imdb_id=tt0133093&languages=EN&subs_per_page=1&type=movie";
        using var request = NewRequest(HttpMethod.Get, url);
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        await using var stream = await response.Content.ReadAsBoundedStreamAsync(64 * 1024, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.False)
        {
            var error = Str(doc.RootElement, "error") ?? Str(doc.RootElement, "message") ?? "Invalid API key";
            throw new SubtitleProviderException($"SubDL refused the request: {error}");
        }

        return "API key OK";
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SubtitleProviderException($"Cannot reach SubDL: {ex.Message}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            await using var stream = await response.Content.ReadAsBoundedStreamAsync(64 * 1024, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            message = Str(doc.RootElement, "error") ?? Str(doc.RootElement, "message");
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
        }

        throw (int)response.StatusCode switch
        {
            401 or 403 => new SubtitleProviderException($"SubDL refused the request (check the API key){(message is null ? "" : $": {message}")}"),
            429 => new SubtitleProviderException($"SubDL rate limit reached{(message is null ? "" : $": {message}")}"),
            _ => new SubtitleProviderException($"SubDL request failed with HTTP {(int)response.StatusCode}{(message is null ? "" : $": {message}")}"),
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    private static string? Raw(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
            : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? PositiveInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
}
