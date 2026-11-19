using System.Net;
using Krakarr.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Krakarr.Downloads;

public interface IDownloadService
{
    /// <summary>Resolves an indexer release (magnet or .torrent URL) and hands it to the torrent engine.</summary>
    Task<Download> GrabAsync(ReleaseInfo release, AddTorrentOptions options, CancellationToken ct = default);
}

public sealed class DownloadService(ITorrentEngine engine, IHttpClientFactory httpClientFactory, ILogger<DownloadService> logger) : IDownloadService
{
    public const string HttpClientName = "torrent-fetch";

    public async Task<Download> GrabAsync(ReleaseInfo release, AddTorrentOptions options, CancellationToken ct = default)
    {
        options = options with { IndexerName = options.IndexerName ?? release.IndexerName, DisplayName = options.DisplayName ?? release.Title };

        if (!string.IsNullOrEmpty(release.DownloadUrl) && Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var url) && url.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var (data, magnet) = await FetchAsync(url, ct);
            if (data is not null)
            {
                return await engine.AddTorrentAsync(data, options, ct);
            }

            if (magnet is not null)
            {
                return await engine.AddMagnetAsync(magnet, options, ct);
            }
        }

        if (!string.IsNullOrEmpty(release.MagnetUrl))
        {
            return await engine.AddMagnetAsync(release.MagnetUrl, options, ct);
        }

        if (!string.IsNullOrEmpty(release.InfoHash))
        {
            return await engine.AddMagnetAsync($"magnet:?xt=urn:btih:{release.InfoHash}&dn={Uri.EscapeDataString(release.Title)}", options, ct);
        }

        throw new InvalidOperationException("Release has no usable download link");
    }

    /// <summary>Downloads a .torrent, following redirects manually so magnet: redirects can be captured.</summary>
    private async Task<(byte[]? Data, string? Magnet)> FetchAsync(Uri url, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        for (var hop = 0; hop < 6; hop++)
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                if (location is null) break;
                var target = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (target.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, target.OriginalString);
                }

                url = target;
                continue;
            }

            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 0 && bytes[0] == (byte)'d')
            {
                return (bytes, null);
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes).Trim();
            if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                return (null, text);
            }

            logger.LogWarning("Download URL {Url} did not return a torrent file", url.GetLeftPart(UriPartial.Path));
            throw new InvalidOperationException("Indexer did not return a valid torrent file");
        }

        throw new InvalidOperationException("Too many redirects while fetching torrent");
    }
}
