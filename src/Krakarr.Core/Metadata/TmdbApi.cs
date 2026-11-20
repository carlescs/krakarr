using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Core.Metadata;

public sealed class MetadataException(string message) : Exception(message);

/// <summary>
/// Shared TMDB v3 transport used by the movie and series clients. Accepts either a v3 API key or a v4 read access token
/// and appends the configured metadata language to every request.
/// </summary>
public sealed class TmdbApi(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    public const string HttpClientName = "tmdb";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    /// <summary>Upper bound for one TMDB response; aggregate credits of long-running shows are the largest at a few MB.</summary>
    public const long MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>Absolute image URL for a TMDB image path ("/abc.jpg"), or null.</summary>
    public static string? Image(string? path, string size) => string.IsNullOrEmpty(path) ? null : ImageBase + size + path;

    public async Task<T> GetAsync<T>(MetadataSettings meta, string relative, CancellationToken ct)
    {
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
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MetadataException($"Cannot reach TMDB: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new MetadataException("Not found on TMDB");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MetadataException($"TMDB request failed with HTTP {(int)response.StatusCode}");
            }

            try
            {
                await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxResponseBytes, ct);
                return await JsonSerializer.DeserializeAsync<T>(body, JsonSerializerOptions.Web, ct) ?? throw new MetadataException("Empty TMDB response");
            }
            catch (HttpRequestException ex)
            {
                throw new MetadataException($"Cannot read the TMDB response: {ex.Message}");
            }
            catch (JsonException)
            {
                throw new MetadataException("TMDB returned an invalid response");
            }
        }
    }
}

public static class TmdbServiceCollectionExtensions
{
    /// <summary>Registers the shared TMDB transport. Safe to call from several modules.</summary>
    public static IServiceCollection AddTmdbApi(this IServiceCollection services)
    {
        services.AddHttpClient(TmdbApi.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.TryAddSingleton<TmdbApi>();
        services.TryAddSingleton<IPersonMetadata, TmdbPeople>();
        return services;
    }
}
