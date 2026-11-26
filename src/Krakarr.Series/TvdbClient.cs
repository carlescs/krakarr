using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Metadata;
using Krakarr.Data;

namespace Krakarr.Series;

public interface ITvdbClient
{
    Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default);
    Task<SeriesInfo> GetSeriesAsync(int tvdbId, CancellationToken ct = default);

    /// <summary>Logs in with the configured API key and PIN; throws <see cref="MetadataException"/> when that fails.</summary>
    Task LoginAsync(CancellationToken ct = default);
}

/// <summary>TheTVDB v4 client (optional series metadata source): search, series details (with translations and artwork) and the default episode order.</summary>
public sealed class TvdbClient(IHttpClientFactory httpClientFactory, ISettingsService settings) : ITvdbClient
{
    public const string HttpClientName = "tvdb";
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static (string Key, string Token, DateTimeOffset Expires)? _token;

    public async Task LoginAsync(CancellationToken ct = default) =>
        await GetTokenAsync(httpClientFactory.CreateClient(HttpClientName), force: true, ct);

    public async Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default)
    {
        var lang = await LanguageAsync(ct);
        using var doc = await GetJsonAsync($"search?query={Uri.EscapeDataString(query)}&type=series&limit=25", ct);
        var results = new List<SeriesLookup>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = Int(Str(item, "tvdb_id") ?? Str(item, "id")?.Replace("series-", string.Empty, StringComparison.Ordinal));
            if (id is null or 0) continue;
            results.Add(new SeriesLookup
            {
                TvdbId = id.Value,
                Title = Translation(item, "translations", lang) ?? Str(item, "name") ?? $"TVDB {id}",
                Year = Int(Str(item, "year")) ?? 0,
                Overview = Translation(item, "overviews", lang) ?? Str(item, "overview"),
                PosterUrl = Str(item, "image_url") ?? Str(item, "thumbnail"),
                Network = Str(item, "network"),
                Status = Str(item, "status"),
            });
        }

        return results;
    }

    public async Task<SeriesInfo> GetSeriesAsync(int tvdbId, CancellationToken ct = default)
    {
        var lang = await LanguageAsync(ct);
        var id = tvdbId.ToString(CultureInfo.InvariantCulture);

        using var doc = await GetJsonAsync($"series/{id}/extended", ct);
        var s = doc.RootElement.GetProperty("data");

        string? title = null, overview = null;
        try
        {
            using var tr = await GetJsonAsync($"series/{id}/translations/{lang}", ct);
            if (tr.RootElement.TryGetProperty("data", out var t))
            {
                title = Str(t, "name");
                overview = Str(t, "overview");
            }
        }
        catch (MetadataException)
        {
            // No translation in the preferred language; fall back to the original.
        }

        var firstAired = Date(Str(s, "firstAired"));
        var status = s.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object ? Str(st, "name") : null;
        var network = (s.TryGetProperty("originalNetwork", out var nw) && nw.ValueKind == JsonValueKind.Object ? Str(nw, "name") : null)
            ?? (s.TryGetProperty("latestNetwork", out var ln) && ln.ValueKind == JsonValueKind.Object ? Str(ln, "name") : null);

        string? imdb = null;
        int? tmdb = null;
        if (s.TryGetProperty("remoteIds", out var remotes) && remotes.ValueKind == JsonValueKind.Array)
        {
            imdb = remotes.EnumerateArray()
                .Where(r => (Str(r, "sourceName") ?? string.Empty).Contains("IMDB", StringComparison.OrdinalIgnoreCase))
                .Select(r => Str(r, "id"))
                .FirstOrDefault(v => v?.StartsWith("tt", StringComparison.Ordinal) == true);
            tmdb = remotes.EnumerateArray()
                .Where(r => (Str(r, "sourceName") ?? string.Empty).Contains("TheMovieDB", StringComparison.OrdinalIgnoreCase))
                .Select(r => Int(Str(r, "id")))
                .FirstOrDefault(v => v > 0);
        }

        return new SeriesInfo
        {
            TvdbId = tvdbId,
            TmdbId = tmdb,
            Title = title ?? Str(s, "name") ?? $"TVDB {tvdbId}",
            Year = Int(Str(s, "year")) ?? firstAired?.Year ?? 0,
            Overview = overview ?? Str(s, "overview"),
            PosterUrl = Artwork(s, 2, lang) ?? Str(s, "image"),
            FanartUrl = Artwork(s, 3, lang),
            Network = network,
            Status = status is null ? null : TmdbTvClient.NormalizeStatus(status),
            Runtime = Int(Str(s, "averageRuntime")) ?? 0,
            ImdbId = imdb,
            FirstAired = firstAired,
            Episodes = await GetEpisodesAsync(id, lang, ct),
            OriginalLanguage = FromTvdbLanguage(Str(s, "originalLanguage")),
            AlternateTitles = MapAlternateTitles(s),
        };
    }

    /// <summary>The series' own name (in its original language) and its aliases.</summary>
    internal static List<AlternateTitleInfo> MapAlternateTitles(JsonElement series)
    {
        var list = new List<AlternateTitleInfo>();
        if (Str(series, "name") is { } name) list.Add(new AlternateTitleInfo(name, FromTvdbLanguage(Str(series, "originalLanguage"))));
        if (series.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
        {
            foreach (var alias in aliases.EnumerateArray())
            {
                // Aliases are objects ({"language": "spa", "name": "..."}); older answers list bare names.
                var aliasName = alias.ValueKind == JsonValueKind.String ? alias.GetString() : Str(alias, "name");
                if (!string.IsNullOrWhiteSpace(aliasName)) list.Add(new AlternateTitleInfo(aliasName, FromTvdbLanguage(Str(alias, "language"))));
            }
        }

        return list;
    }

    /// <summary>Krakarr language code for a TheTVDB language ("spa", ISO 639-2); null when unknown.</summary>
    public static string? FromTvdbLanguage(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "eng" => "en", "spa" => "es", "fra" => "fr", "deu" => "de", "ita" => "it", "por" => "pt", "pt" => "pt-br", "nld" => "nl",
        "cat" => "ca", "swe" => "sv", "dan" => "da", "nor" => "no", "fin" => "fi", "pol" => "pl", "rus" => "ru", "jpn" => "ja",
        "kor" => "ko", "zho" => "zh", "ces" => "cs", "hun" => "hu", "tur" => "tr", "ell" => "el", "heb" => "he", "ukr" => "uk",
        "ron" => "ro", "bul" => "bg", "hrv" => "hr", "srp" => "sr", "slv" => "sl", "slk" => "sk", "est" => "et", "lav" => "lv",
        "lit" => "lt", "ara" => "ar", "fas" => "fa", "hin" => "hi", "tam" => "ta", "tel" => "te", "vie" => "vi", "tha" => "th",
        "ind" => "id", "isl" => "is", "eus" => "eu", "glg" => "gl",
        var other => MediaLanguages.Normalize(other),
    };

    private async Task<List<EpisodeInfo>> GetEpisodesAsync(string seriesId, string lang, CancellationToken ct)
    {
        var episodes = new List<EpisodeInfo>();
        for (var page = 0; page < 100; page++)
        {
            using var doc = await GetJsonAsync($"series/{seriesId}/episodes/default/{lang}?page={page}", ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("episodes", out var eps) && eps.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in eps.EnumerateArray())
                {
                    var season = Int(Str(e, "seasonNumber"));
                    var number = Int(Str(e, "number"));
                    if (season is null || number is null) continue;
                    episodes.Add(new EpisodeInfo
                    {
                        TvdbId = Int(Str(e, "id")) is > 0 and var episodeId ? episodeId : null,
                        SeasonNumber = season.Value,
                        EpisodeNumber = number.Value,
                        AbsoluteNumber = Int(Str(e, "absoluteNumber")) is > 0 and var abs ? abs : null,
                        Title = Str(e, "name"),
                        Overview = Str(e, "overview"),
                        AirDate = Date(Str(e, "aired")),
                        Runtime = Int(Str(e, "runtime")) ?? 0,
                    });
                }
            }

            var hasNext = root.TryGetProperty("links", out var links)
                && links.TryGetProperty("next", out var next)
                && next.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            if (!hasNext) break;
        }

        return episodes;
    }

    private async Task<JsonDocument> GetJsonAsync(string relative, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(http, force: attempt > 0, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, relative);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new MetadataException($"Cannot reach TheTVDB: {ex.Message}");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue; // token expired
                if (!response.IsSuccessStatusCode)
                {
                    throw new MetadataException($"TheTVDB request failed with HTTP {(int)response.StatusCode}", response.StatusCode);
                }

                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
        }
    }

    private async Task<string> GetTokenAsync(HttpClient http, bool force, CancellationToken ct)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        if (string.IsNullOrWhiteSpace(meta.TvdbApiKey))
        {
            throw new MetadataException("TheTVDB API key is not configured (Settings → Metadata)");
        }

        await TokenLock.WaitAsync(ct);
        try
        {
            if (!force && _token is { } t && t.Key == meta.TvdbApiKey && t.Expires > DateTimeOffset.UtcNow)
            {
                return t.Token;
            }

            object body = string.IsNullOrWhiteSpace(meta.TvdbPin) ? new { apikey = meta.TvdbApiKey } : new { apikey = meta.TvdbApiKey, pin = meta.TvdbPin };
            using var response = await http.PostAsJsonAsync("login", body, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new MetadataException($"TheTVDB login failed (HTTP {(int)response.StatusCode}); check the API key/PIN", response.StatusCode);
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var token = doc.RootElement.TryGetProperty("data", out var d) ? Str(d, "token") : null;
            if (string.IsNullOrEmpty(token)) throw new MetadataException("TheTVDB login returned no token");
            _token = (meta.TvdbApiKey, token, DateTimeOffset.UtcNow.AddDays(25));
            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private async Task<string> LanguageAsync(CancellationToken ct) => ToTvdbLanguage((await settings.GetAsync<MetadataSettings>(ct)).Language);

    /// <summary>TheTVDB uses ISO 639-2 codes ("eng"); settings use culture names ("en-US").</summary>
    public static string ToTvdbLanguage(string? culture)
    {
        var two = (culture ?? "en").Split('-', '_')[0].ToLowerInvariant();
        return two switch
        {
            "en" => "eng", "es" => "spa", "fr" => "fra", "de" => "deu", "it" => "ita", "pt" => "por", "nl" => "nld",
            "ca" => "cat", "sv" => "swe", "da" => "dan", "no" or "nb" => "nor", "fi" => "fin", "pl" => "pol", "ru" => "rus",
            "ja" => "jpn", "ko" => "kor", "zh" => "zho", "cs" => "ces", "hu" => "hun", "tr" => "tur", "el" => "ell", "he" => "heb",
            _ => two.Length == 3 ? two : "eng",
        };
    }

    private static string? Artwork(JsonElement series, int type, string lang)
    {
        if (!series.TryGetProperty("artworks", out var arts) || arts.ValueKind != JsonValueKind.Array) return null;
        return arts.EnumerateArray()
            .Where(a => Int(Str(a, "type")) == type && !string.IsNullOrEmpty(Str(a, "image")))
            .OrderByDescending(a => Str(a, "language") == lang ? 2 : Str(a, "language") is null ? 1 : 0)
            .ThenByDescending(a => Int(Str(a, "score")) ?? 0)
            .Select(a => Str(a, "image"))
            .FirstOrDefault();
    }

    private static string? Translation(JsonElement item, string property, string lang) =>
        item.TryGetProperty(property, out var tr) && tr.ValueKind == JsonValueKind.Object ? Str(tr, lang) : null;

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static int? Int(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : null;

    private static DateOnly? Date(string? s) =>
        DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;
}
