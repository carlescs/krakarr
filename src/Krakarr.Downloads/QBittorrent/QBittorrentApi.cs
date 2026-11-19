using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Krakarr.Core.Configuration;

namespace Krakarr.Downloads.QBittorrent;

public sealed class QBittorrentException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record QbtTorrent
{
    [JsonPropertyName("hash")] public string Hash { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; init; } = string.Empty;
    [JsonPropertyName("progress")] public double Progress { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("total_size")] public long TotalSize { get; init; }
    [JsonPropertyName("downloaded")] public long Downloaded { get; init; }
    [JsonPropertyName("uploaded")] public long Uploaded { get; init; }
    [JsonPropertyName("dlspeed")] public long DownloadSpeed { get; init; }
    [JsonPropertyName("upspeed")] public long UploadSpeed { get; init; }
    [JsonPropertyName("num_seeds")] public int Seeds { get; init; }
    [JsonPropertyName("num_leechs")] public int Leechers { get; init; }
    [JsonPropertyName("ratio")] public double Ratio { get; init; }
    [JsonPropertyName("eta")] public long Eta { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("tags")] public string? Tags { get; init; }
    [JsonPropertyName("save_path")] public string? SavePath { get; init; }
    [JsonPropertyName("content_path")] public string? ContentPath { get; init; }
    [JsonPropertyName("added_on")] public long AddedOn { get; init; }
    [JsonPropertyName("completion_on")] public long CompletionOn { get; init; }

    /// <summary>v1 info hash (qBittorrent 4.4+; empty for v2-only torrents).</summary>
    [JsonPropertyName("infohash_v1")] public string? InfohashV1 { get; init; }
    /// <summary>v2 info hash (qBittorrent 4.4+; empty for v1-only torrents).</summary>
    [JsonPropertyName("infohash_v2")] public string? InfohashV2 { get; init; }

    /// <summary>
    /// All identifiers this torrent can be known by. qBittorrent's own <see cref="Hash"/> (torrent ID) is the v1 hash for
    /// v1 torrents but the truncated v2 hash for hybrid/v2 torrents, whereas Krakarr keys downloads by v1-or-v2 hash.
    /// </summary>
    public IEnumerable<string> Keys
    {
        get
        {
            foreach (var key in new[] { Hash, InfohashV1, InfohashV2, InfohashV2 is { Length: > 40 } v2 ? v2[..40] : null })
            {
                if (!string.IsNullOrEmpty(key)) yield return key.ToLowerInvariant();
            }
        }
    }

    public bool Matches(string infoHash) => Keys.Contains(infoHash, StringComparer.OrdinalIgnoreCase);

    /// <summary>The hash Krakarr uses to identify this torrent (v1 if present, otherwise the qBittorrent ID).</summary>
    public string KrakarrHash => (string.IsNullOrEmpty(InfohashV1) ? Hash : InfohashV1).ToLowerInvariant();

    public bool HasTag(string tag) =>
        (Tags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(tag, StringComparer.OrdinalIgnoreCase);
}

public sealed record QbtFile
{
    /// <summary>Path relative to the torrent's save path (includes the torrent's root folder).</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
}

/// <summary>
/// Minimal qBittorrent WebUI API v2 client. Handles the SID cookie itself (so it works with any handler),
/// logs in lazily and re-authenticates once when a call is rejected with 403.
/// </summary>
public sealed class QBittorrentApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly QBittorrentSettings _settings;
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _sid;
    private bool _loggedIn;
    private Version? _webApiVersion;

    public QBittorrentApi(QBittorrentSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        var baseUrl = settings.Url.TrimEnd('/') + "/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new QBittorrentException("qBittorrent URL must be an absolute http(s) URL");
        }

        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = uri;
        _http.Timeout = TimeSpan.FromSeconds(20);
        // qBittorrent's CSRF protection compares Referer/Origin with its own host.
        _http.DefaultRequestHeaders.Referrer = uri;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
    }

    public QBittorrentSettings Settings => _settings;

    public async Task LoginAsync(CancellationToken ct = default)
    {
        await _loginLock.WaitAsync(ct);
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = _settings.Username ?? string.Empty,
                ["password"] = _settings.Password ?? string.Empty,
            });
            using var response = await _http.PostAsync("api/v2/auth/login", content, ct);
            var body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new QBittorrentException("qBittorrent refused the login (IP banned after too many failed attempts?)");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new QBittorrentException($"qBittorrent login failed with HTTP {(int)response.StatusCode}");
            }

            if (body.Equals("Fails.", StringComparison.OrdinalIgnoreCase))
            {
                throw new QBittorrentException("qBittorrent rejected the username or password");
            }

            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    var first = cookie.Split(';', 2)[0];
                    var eq = first.IndexOf('=');
                    if (eq > 0 && first[..eq].Trim().Equals("SID", StringComparison.OrdinalIgnoreCase))
                    {
                        _sid = first[(eq + 1)..].Trim();
                    }
                }
            }

            _loggedIn = true;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default) =>
        (await SendStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/v2/app/version"), ct)).Trim();

    public async Task<Version> GetWebApiVersionAsync(CancellationToken ct = default)
    {
        if (_webApiVersion is not null) return _webApiVersion;
        var text = (await SendStringAsync(() => new HttpRequestMessage(HttpMethod.Get, "api/v2/app/webapiVersion"), ct)).Trim();
        _webApiVersion = Version.TryParse(text, out var v) ? v : new Version(2, 0);
        return _webApiVersion;
    }

    public async Task<IReadOnlyList<QbtTorrent>> GetTorrentsAsync(string tag, string? hash = null, CancellationToken ct = default)
    {
        var url = $"api/v2/torrents/info?tag={Uri.EscapeDataString(tag)}";
        if (hash is not null) url += $"&hashes={Uri.EscapeDataString(hash)}";
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        var torrents = await response.Content.ReadFromJsonAsync<List<QbtTorrent>>(ct) ?? [];
        // Defence in depth: never surface a torrent that does not actually carry our tag.
        return torrents.Where(t => t.HasTag(tag)).ToList();
    }

    public async Task<IReadOnlyList<QbtFile>> GetFilesAsync(string hash, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/v2/torrents/files?hash={Uri.EscapeDataString(hash)}"), ct);
        return await response.Content.ReadFromJsonAsync<List<QbtFile>>(ct) ?? [];
    }

    public Task CreateTagAsync(string tag, CancellationToken ct = default) =>
        PostFormAsync("api/v2/torrents/createTags", new() { ["tags"] = tag }, ct);

    public async Task CreateCategoryAsync(string category, CancellationToken ct = default)
    {
        // 409 = category already exists (or invalid name); both are fine to ignore here.
        using var response = await SendAsync(() => FormRequest("api/v2/torrents/createCategory", new() { ["category"] = category }), ct, ensureSuccess: false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
        {
            throw new QBittorrentException($"qBittorrent could not create category '{category}' (HTTP {(int)response.StatusCode})");
        }
    }

    /// <summary>Adds a torrent from a magnet link or from .torrent bytes, tagged with <paramref name="tag"/>.</summary>
    public async Task AddAsync(string? magnet, byte[]? torrentFile, string tag, string? category, string? savePath, bool paused, CancellationToken ct = default)
    {
        var body = await SendStringAsync(() =>
        {
            var form = new MultipartFormDataContent();
            if (magnet is not null)
            {
                form.Add(new StringContent(magnet), "urls");
            }
            else if (torrentFile is not null)
            {
                var file = new ByteArrayContent(torrentFile);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                form.Add(file, "torrents", "krakarr.torrent");
            }
            else
            {
                throw new ArgumentException("Either a magnet link or a torrent file is required");
            }

            form.Add(new StringContent(tag), "tags");
            if (!string.IsNullOrWhiteSpace(category)) form.Add(new StringContent(category), "category");
            if (!string.IsNullOrWhiteSpace(savePath)) form.Add(new StringContent(savePath), "savepath");
            if (paused)
            {
                form.Add(new StringContent("true"), "paused");   // qBittorrent 4.x
                form.Add(new StringContent("true"), "stopped");  // qBittorrent 5.x
            }

            return new HttpRequestMessage(HttpMethod.Post, "api/v2/torrents/add") { Content = form };
        }, ct);

        if (body.Trim().Equals("Fails.", StringComparison.OrdinalIgnoreCase))
        {
            throw new QBittorrentException("qBittorrent refused to add the torrent (invalid or duplicate?)");
        }
    }

    public async Task PauseAsync(string hash, CancellationToken ct = default)
    {
        var endpoint = await UsesStopStartAsync(ct) ? "stop" : "pause";
        await PostFormAsync($"api/v2/torrents/{endpoint}", new() { ["hashes"] = hash }, ct);
    }

    public async Task ResumeAsync(string hash, CancellationToken ct = default)
    {
        var endpoint = await UsesStopStartAsync(ct) ? "start" : "resume";
        await PostFormAsync($"api/v2/torrents/{endpoint}", new() { ["hashes"] = hash }, ct);
    }

    public Task DeleteAsync(string hash, bool deleteFiles, CancellationToken ct = default) =>
        PostFormAsync("api/v2/torrents/delete", new() { ["hashes"] = hash, ["deleteFiles"] = deleteFiles ? "true" : "false" }, ct);

    /// <summary>qBittorrent 5 (WebAPI 2.11) renamed pause/resume to stop/start.</summary>
    private async Task<bool> UsesStopStartAsync(CancellationToken ct) => await GetWebApiVersionAsync(ct) >= new Version(2, 11);

    private static HttpRequestMessage FormRequest(string url, Dictionary<string, string> values) =>
        new(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(values) };

    private async Task PostFormAsync(string url, Dictionary<string, string> values, CancellationToken ct)
    {
        using var _ = await SendAsync(() => FormRequest(url, values), ct);
    }

    private async Task<string> SendStringAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        using var response = await SendAsync(factory, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct, bool ensureSuccess = true)
    {
        if (!_loggedIn)
        {
            await LoginAsync(ct);
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = factory();
            if (_sid is not null)
            {
                request.Headers.Add("Cookie", $"SID={_sid}");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new QBittorrentException($"Cannot reach qBittorrent at {_http.BaseAddress}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new QBittorrentException($"qBittorrent at {_http.BaseAddress} timed out", ex);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0)
            {
                // Session expired: log in again and retry once.
                response.Dispose();
                _loggedIn = false;
                await LoginAsync(ct);
                continue;
            }

            if (ensureSuccess && !response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new QBittorrentException($"qBittorrent request {request.RequestUri} failed with HTTP {status}");
            }

            return response;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _loginLock.Dispose();
    }
}
