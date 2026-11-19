using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Krakarr.Core.Configuration;
using Krakarr.Data;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Movies;

public sealed class MetadataException(string message) : Exception(message);

public sealed record MovieLookup
{
    public int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    public int Year { get; init; }
    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? FanartUrl { get; init; }
    public int Runtime { get; init; }
    public string? Status { get; init; }
    /// <summary>Local movie id when the movie is already in the library.</summary>
    public int? MovieId { get; init; }
}

public interface ITmdbClient
{
    Task<IReadOnlyList<MovieLookup>> SearchAsync(string query, int? year, CancellationToken ct = default);
    Task<MovieLookup> GetMovieAsync(int tmdbId, CancellationToken ct = default);
}

/// <summary>Minimal TMDB v3 client. Accepts either a v3 API key or a v4 read access token.</summary>
public sealed class TmdbClient(IHttpClientFactory httpClientFactory, ISettingsService settings, IConfiguration configuration) : ITmdbClient
{
    public const string HttpClientName = "tmdb";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    public async Task<IReadOnlyList<MovieLookup>> SearchAsync(string query, int? year, CancellationToken ct = default)
    {
        var path = $"3/search/movie?query={Uri.EscapeDataString(query)}&include_adult=false";
        if (year is { } y) path += $"&year={y.ToString(CultureInfo.InvariantCulture)}";
        var result = await GetAsync<TmdbSearchResponse>(path, ct);
        return result.Results.Select(Map).ToList();
    }

    public async Task<MovieLookup> GetMovieAsync(int tmdbId, CancellationToken ct = default)
    {
        var movie = await GetAsync<TmdbMovie>($"3/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}", ct);
        return Map(movie);
    }

    private async Task<T> GetAsync<T>(string relative, CancellationToken ct)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        if (string.IsNullOrWhiteSpace(meta.TmdbApiKey))
        {
            throw new MetadataException("TMDB API key is not configured (Settings → Metadata)");
        }

        var baseUrl = (configuration["Tmdb:BaseUrl"] ?? "https://api.themoviedb.org/").TrimEnd('/') + "/";
        var sep = relative.Contains('?') ? '&' : '?';
        var url = $"{baseUrl}{relative}{sep}language={Uri.EscapeDataString(meta.Language)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var key = meta.TmdbApiKey.Trim();
        if (key.Length > 40)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        else
        {
            request.RequestUri = new Uri($"{url}&api_key={Uri.EscapeDataString(key)}");
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new MetadataException($"TMDB request failed with HTTP {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new MetadataException("Empty TMDB response");
    }

    private static MovieLookup Map(TmdbMovie m) => new()
    {
        TmdbId = m.Id,
        ImdbId = string.IsNullOrEmpty(m.ImdbId) ? null : m.ImdbId,
        Title = m.Title ?? m.OriginalTitle ?? $"TMDB {m.Id}",
        OriginalTitle = m.OriginalTitle,
        Year = DateOnly.TryParse(m.ReleaseDate, CultureInfo.InvariantCulture, out var d) ? d.Year : 0,
        Overview = m.Overview,
        PosterUrl = m.PosterPath is null ? null : ImageBase + "w500" + m.PosterPath,
        FanartUrl = m.BackdropPath is null ? null : ImageBase + "original" + m.BackdropPath,
        Runtime = m.Runtime ?? 0,
        Status = m.Status,
    };

    private sealed record TmdbSearchResponse([property: JsonPropertyName("results")] List<TmdbMovie> Results);

    private sealed record TmdbMovie
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("imdb_id")] public string? ImdbId { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("original_title")] public string? OriginalTitle { get; init; }
        [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
        [JsonPropertyName("overview")] public string? Overview { get; init; }
        [JsonPropertyName("poster_path")] public string? PosterPath { get; init; }
        [JsonPropertyName("backdrop_path")] public string? BackdropPath { get; init; }
        [JsonPropertyName("runtime")] public int? Runtime { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
    }
}
