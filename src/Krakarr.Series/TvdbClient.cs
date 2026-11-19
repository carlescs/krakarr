using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Krakarr.Core.Configuration;
using Krakarr.Data;

namespace Krakarr.Series;

public sealed record SeriesLookup
{
    public int TvdbId { get; init; }
    public required string Title { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? Network { get; init; }
    public string? Status { get; init; }
}

public interface ITvdbClient
{
    Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default);
}

/// <summary>TheTVDB v4 client (search only for now; full series/episode sync lands in a later milestone).</summary>
public sealed class TvdbClient(IHttpClientFactory httpClientFactory, ISettingsService settings) : ITvdbClient
{
    public const string HttpClientName = "tvdb";
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static (string Key, string Token, DateTimeOffset Expires)? _token;

    public async Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        var token = await GetTokenAsync(http, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"search?query={Uri.EscapeDataString(query)}&type=series&limit=25");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TvdbResponse<List<TvdbSearchResult>>>(ct);

        return body?.Data?.Select(r => new SeriesLookup
        {
            TvdbId = int.TryParse(r.TvdbId, out var id) ? id : 0,
            Title = r.Name ?? "Unknown",
            Year = int.TryParse(r.Year, out var y) ? y : 0,
            Overview = r.Overview,
            PosterUrl = r.ImageUrl,
            Network = r.Network,
            Status = r.Status,
        }).ToList() ?? [];
    }

    private async Task<string> GetTokenAsync(HttpClient http, CancellationToken ct)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        if (string.IsNullOrWhiteSpace(meta.TvdbApiKey))
        {
            throw new InvalidOperationException("TVDB API key is not configured (Settings → Metadata)");
        }

        await TokenLock.WaitAsync(ct);
        try
        {
            if (_token is { } t && t.Key == meta.TvdbApiKey && t.Expires > DateTimeOffset.UtcNow)
            {
                return t.Token;
            }

            using var response = await http.PostAsJsonAsync("login", new { apikey = meta.TvdbApiKey, pin = meta.TvdbPin }, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<TvdbResponse<TvdbLogin>>(ct);
            var token = body?.Data?.Token ?? throw new InvalidOperationException("TVDB login returned no token");
            _token = (meta.TvdbApiKey, token, DateTimeOffset.UtcNow.AddDays(25));
            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private sealed record TvdbResponse<T>([property: JsonPropertyName("data")] T? Data);

    private sealed record TvdbLogin([property: JsonPropertyName("token")] string? Token);

    private sealed record TvdbSearchResult
    {
        [JsonPropertyName("tvdb_id")] public string? TvdbId { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("year")] public string? Year { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("image_url")] public string? ImageUrl { get; init; }
        [JsonPropertyName("network")] public string? Network { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
    }
}
