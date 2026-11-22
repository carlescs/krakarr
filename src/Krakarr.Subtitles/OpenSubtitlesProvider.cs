using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Http;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Subtitles;

/// <summary>OpenSubtitles.com REST API (v1). Needs a consumer API key; an account raises the daily download quota.</summary>
public sealed class OpenSubtitlesProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration, OpenSubtitlesBackoff backoff) : ISubtitleProvider
{
    public const string ProviderName = "opensubtitles";
    public const string HttpClientName = "opensubtitles";
    private const string UserAgent = "Krakarr v0.1.0";

    private static readonly SemaphoreSlim LoginLock = new(1, 1);
    private static (string User, string Token, string BaseUrl, DateTimeOffset Expires)? _session;

    public string Name => ProviderName;

    /// <summary>Automatic searches pause while either requests or downloads are blocked: a search we cannot act on still uses up the request quota.</summary>
    public DateTimeOffset? BackoffUntil => backoff.DownloadsBlockedUntil;

    private string DefaultBaseUrl => (configuration["OpenSubtitles:BaseUrl"] ?? "https://api.opensubtitles.com/api/v1/").TrimEnd('/') + "/";

    public bool IsConfigured(SubtitleSettings settings) =>
        settings.OpenSubtitles.Enabled && !string.IsNullOrWhiteSpace(settings.OpenSubtitles.ApiKey);

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default)
    {
        var parameters = BuildSearchParameters(query);
        using var request = NewRequest(HttpMethod.Get, DefaultBaseUrl + "subtitles?" + parameters, settings);
        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return ParseSearchResults(doc, query);
    }

    /// <summary>Query string with keys sorted alphabetically and lower-case values, as the API requires (it redirects otherwise).</summary>
    internal static string BuildSearchParameters(SubtitleQuery q)
    {
        var p = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = string.Join(',', q.Languages.Select(l => l.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)),
        };

        var imdb = ImdbNumber(q.ImdbId);
        if (q.MediaType == MediaType.Series)
        {
            p["type"] = "episode";
            if (imdb is not null) p["parent_imdb_id"] = imdb;
            else p["query"] = q.Title.ToLowerInvariant();
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
                p["query"] = q.Title.ToLowerInvariant();
                if (q.Year > 0) p["year"] = q.Year.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (!string.IsNullOrEmpty(q.FileHash)) p["moviehash"] = q.FileHash.ToLowerInvariant();

        return string.Join('&', p.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>IMDb numeric id without "tt" and leading zeros ("tt0133093" → "133093").</summary>
    internal static string? ImdbNumber(string? imdb)
    {
        if (string.IsNullOrWhiteSpace(imdb)) return null;
        var digits = imdb.Trim().TrimStart('t', 'T').TrimStart('0');
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    internal static List<SubtitleCandidate> ParseSearchResults(JsonDocument doc, SubtitleQuery query)
    {
        var results = new List<SubtitleCandidate>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return results;
        var byId = ImdbNumber(query.ImdbId) is not null || query.TmdbId is not null;

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("attributes", out var a)) continue;
            if (!a.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) continue;
            var file = files[0];
            var fileId = Raw(file, "file_id");
            var language = Languages.Normalize(Str(a, "language"));
            if (fileId is null || language is null) continue;

            var feature = a.TryGetProperty("feature_details", out var fd) ? fd : default;
            var release = Str(a, "release");
            var fileName = Str(file, "file_name");
            results.Add(new SubtitleCandidate
            {
                Provider = ProviderName,
                Payload = fileId,
                Language = language,
                ReleaseNames = new[] { release, fileName }.Where(n => !string.IsNullOrWhiteSpace(n)).Cast<string>().Distinct().ToList(),
                FileName = fileName,
                HearingImpaired = Bool(a, "hearing_impaired"),
                Forced = Bool(a, "foreign_parts_only"),
                HashMatch = Bool(a, "moviehash_match"),
                AiTranslated = Bool(a, "ai_translated") || Bool(a, "machine_translated"),
                TrustedUploader = Bool(a, "from_trusted"),
                Downloads = Int(Raw(a, "download_count")) ?? 0,
                SeasonNumber = feature.ValueKind == JsonValueKind.Object ? Int(Raw(feature, "season_number")) : null,
                EpisodeNumber = feature.ValueKind == JsonValueKind.Object ? Int(Raw(feature, "episode_number")) : null,
                FeatureTitle = feature.ValueKind == JsonValueKind.Object ? Str(feature, "parent_title") ?? Str(feature, "movie_name") ?? Str(feature, "title") : null,
                FeatureYear = feature.ValueKind == JsonValueKind.Object ? Int(Raw(feature, "year")) : null,
                MatchedById = byId,
            });
        }

        return results;
    }

    public async Task<SubtitleContent> DownloadAsync(SubtitleCandidate candidate, SubtitleSettings settings, CancellationToken ct = default)
    {
        var session = await LoginAsync(settings, ct);
        var baseUrl = session?.BaseUrl ?? DefaultBaseUrl;
        using var request = NewRequest(HttpMethod.Post, baseUrl + "download", settings);
        request.Content = JsonContent.Create(new { file_id = int.Parse(candidate.Payload, CultureInfo.InvariantCulture) });
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Value.Token);

        using var response = await SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var link = Str(doc.RootElement, "link") ?? throw new SubtitleProviderException("OpenSubtitles returned no download link");
        var fileName = Str(doc.RootElement, "file_name") ?? candidate.FileName ?? "subtitle.srt";

        var http = httpClientFactory.CreateClient(HttpClientName);
        using var fileResponse = await http.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!fileResponse.IsSuccessStatusCode)
            throw new SubtitleProviderException($"Subtitle download failed with HTTP {(int)fileResponse.StatusCode}");
        byte[] bytes;
        try
        {
            bytes = await fileResponse.Content.ReadAsByteArrayAsync(BoundedContent.MaxSubtitleBytes, ct);
        }
        catch (ResponseTooLargeException ex)
        {
            throw new SubtitleProviderException($"Subtitle download failed: {ex.Message}");
        }
        var ext = Path.GetExtension(fileName);
        return new SubtitleContent(bytes, string.IsNullOrEmpty(ext) ? ".srt" : ext.ToLowerInvariant());
    }

    public async Task<string> TestAsync(SubtitleSettings settings, CancellationToken ct = default)
    {
        if (!IsConfigured(settings)) throw new SubtitleProviderException("An OpenSubtitles API key is required");

        if (!string.IsNullOrWhiteSpace(settings.OpenSubtitles.Username))
        {
            _session = null; // force a fresh login with the (possibly new) credentials
            var session = await LoginAsync(settings, ct);
            using var request = NewRequest(HttpMethod.Get, session!.Value.BaseUrl + "infos/user", settings);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Value.Token);
            using var response = await SendAsync(request, ct);
            await EnsureSuccessAsync(response, ct);
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var user = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            var remaining = user.ValueKind == JsonValueKind.Object ? Raw(user, "remaining_downloads") : null;
            return remaining is null ? "Logged in" : $"Logged in · {remaining} downloads left today";
        }

        using var formats = NewRequest(HttpMethod.Get, DefaultBaseUrl + "infos/formats", settings);
        using var check = await SendAsync(formats, ct);
        await EnsureSuccessAsync(check, ct);
        return "API key OK (anonymous: limited daily downloads)";
    }

    private async Task<(string User, string Token, string BaseUrl, DateTimeOffset Expires)?> LoginAsync(SubtitleSettings settings, CancellationToken ct)
    {
        var os = settings.OpenSubtitles;
        if (string.IsNullOrWhiteSpace(os.Username) || string.IsNullOrEmpty(os.Password)) return null;

        await LoginLock.WaitAsync(ct);
        try
        {
            if (_session is { } s && s.User == os.Username && s.Expires > DateTimeOffset.UtcNow) return s;

            using var request = NewRequest(HttpMethod.Post, DefaultBaseUrl + "login", settings);
            request.Content = JsonContent.Create(new { username = os.Username, password = os.Password });
            using var response = await SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new SubtitleProviderException("OpenSubtitles rejected the username or password");
            await EnsureSuccessAsync(response, ct);

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var token = Str(doc.RootElement, "token") ?? throw new SubtitleProviderException("OpenSubtitles login returned no token");
            var host = Str(doc.RootElement, "base_url");
            // The API may direct logged-in users to another host (e.g. vip-api.opensubtitles.com).
            var baseUrl = !string.IsNullOrEmpty(host) && configuration["OpenSubtitles:BaseUrl"] is null
                ? $"https://{host.Trim('/')}/api/v1/"
                : DefaultBaseUrl;
            _session = (os.Username, token, baseUrl, DateTimeOffset.UtcNow.AddHours(23));
            return _session;
        }
        finally
        {
            LoginLock.Release();
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, SubtitleSettings settings)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Api-Key", settings.OpenSubtitles.ApiKey);
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
            throw new SubtitleProviderException($"Cannot reach OpenSubtitles: {ex.Message}");
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
            message = Str(doc.RootElement, "message") ?? (doc.RootElement.TryGetProperty("errors", out var e) ? e.ToString() : null);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
        }

        throw (int)response.StatusCode switch
        {
            401 or 403 => new SubtitleProviderException($"OpenSubtitles refused the request (check the API key){(message is null ? "" : $": {message}")}"),
            406 or 429 => new SubtitleProviderException($"OpenSubtitles quota or rate limit reached{(message is null ? "" : $": {message}")}"),
            _ => new SubtitleProviderException($"OpenSubtitles request failed with HTTP {(int)response.StatusCode}{(message is null ? "" : $": {message}")}"),
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

    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
}
