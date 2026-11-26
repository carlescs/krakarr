using System.Globalization;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Core.Http;
using Krakarr.Core.Metadata;

namespace Krakarr.Series;

/// <summary>Where scene releases put a TheTVDB episode: TheTVDB numbers on the left, scene numbers on the right.</summary>
public sealed record SceneMapping(int Season, int Episode, int SceneSeason, int SceneEpisode);

/// <summary>TheXEM (thexem.info): scene numbering and scene names for series, keyed by TheTVDB id.</summary>
public interface IXemClient
{
    /// <summary>The series' scene numbering; empty when XEM has none.</summary>
    Task<IReadOnlyList<SceneMapping>> GetMappingsAsync(int tvdbId, CancellationToken ct = default);

    /// <summary>Names scene releases use for the series; empty when XEM has none.</summary>
    Task<IReadOnlyList<string>> GetNamesAsync(int tvdbId, CancellationToken ct = default);
}

public sealed class XemClient(IHttpClientFactory httpClientFactory) : IXemClient
{
    public const string HttpClientName = "thexem";

    /// <summary>Upper bound for one XEM answer; the mapping of a long anime is a few hundred KB.</summary>
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<SceneMapping>> GetMappingsAsync(int tvdbId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"map/all?id={tvdbId.ToString(CultureInfo.InvariantCulture)}&origin=tvdb", ct);
        return doc is null ? [] : ParseMappings(doc.RootElement);
    }

    public async Task<IReadOnlyList<string>> GetNamesAsync(int tvdbId, CancellationToken ct = default)
    {
        using var doc = await GetAsync($"map/names?id={tvdbId.ToString(CultureInfo.InvariantCulture)}&origin=tvdb", ct);
        return doc is null ? [] : ParseNames(doc.RootElement);
    }

    /// <summary>The JSON answer, or null when XEM reports a failure (it answers "failure" for series it does not map).</summary>
    private async Task<JsonDocument?> GetAsync(string relative, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(relative, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MetadataException($"Cannot reach TheXEM: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new MetadataException($"TheXEM request failed with HTTP {(int)response.StatusCode}", response.StatusCode);
            }

            JsonDocument doc;
            try
            {
                await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxResponseBytes, ct);
                doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is JsonException or HttpRequestException or ResponseTooLargeException)
            {
                throw new MetadataException($"TheXEM returned an invalid response: {ex.Message}");
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("result", out var result)
                && result.ValueKind == JsonValueKind.String
                && result.GetString() == "success")
            {
                return doc;
            }

            doc.Dispose();
            return null;
        }
    }

    /// <summary><c>{"data": [{"scene": {"season": 1, "episode": 13}, "tvdb": {"season": 2, "episode": 1}}, …]}</c></summary>
    internal static List<SceneMapping> ParseMappings(JsonElement root)
    {
        var list = new List<SceneMapping>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in data.EnumerateArray())
        {
            if (Numbers(item, "tvdb") is { } tvdb && Numbers(item, "scene") is { } scene)
            {
                list.Add(new SceneMapping(tvdb.Season, tvdb.Episode, scene.Season, scene.Episode));
            }
        }

        return list;
    }

    /// <summary>
    /// <c>{"data": {"all": ["Name", …], "2": ["Season 2 name"]}}</c>; entries may also be objects whose keys are names
    /// (<c>{"Name": "us"}</c>). Every name is a name of the series.
    /// </summary>
    internal static List<string> ParseNames(JsonElement root)
    {
        var names = new List<string>();
        if (root.TryGetProperty("data", out var data)) Collect(data, names, depth: 0);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Collect(JsonElement e, List<string> names, int depth)
    {
        if (depth > 3) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.String when !string.IsNullOrWhiteSpace(e.GetString()):
                names.Add(e.GetString()!.Trim());
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Collect(item, names, depth + 1);
                break;
            case JsonValueKind.Object when depth == 0:
                // Keyed by season ("all", "1", …).
                foreach (var p in e.EnumerateObject()) Collect(p.Value, names, depth + 1);
                break;
            case JsonValueKind.Object:
                // {"Name": "language or season"}
                foreach (var p in e.EnumerateObject())
                {
                    if (!string.IsNullOrWhiteSpace(p.Name)) names.Add(p.Name.Trim());
                }

                break;
        }
    }

    private static (int Season, int Episode)? Numbers(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        return Int(p, "season") is { } season && Int(p, "episode") is { } episode ? (season, episode) : null;
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}

/// <summary>
/// Scene numbering (TheXEM) for matching releases and building searches: a release's season/episode is looked up in the
/// scene numbers first, then in the metadata numbers of episodes that have no scene mapping.
/// </summary>
public static class SceneNumbering
{
    /// <summary>Stores the mapping on the episodes; episodes it does not list lose theirs. Returns how many episodes changed.</summary>
    public static int Apply(IEnumerable<Episode> episodes, IReadOnlyCollection<SceneMapping> mappings)
    {
        var map = new Dictionary<(int, int), SceneMapping>();
        foreach (var m in mappings) map.TryAdd((m.Season, m.Episode), m);
        var changed = 0;
        foreach (var e in episodes)
        {
            int? season = null, number = null;
            if (map.TryGetValue((e.SeasonNumber, e.EpisodeNumber), out var m))
            {
                season = m.SceneSeason;
                number = m.SceneEpisode;
            }

            if (e.SceneSeasonNumber == season && e.SceneEpisodeNumber == number) continue;
            e.SceneSeasonNumber = season;
            e.SceneEpisodeNumber = number;
            changed++;
        }

        return changed;
    }

    /// <summary>The episode a release's season/episode number names.</summary>
    public static Episode? Find(IEnumerable<Episode> episodes, int season, int number)
    {
        Episode? fallback = null;
        foreach (var e in episodes)
        {
            if (e.SceneSeasonNumber == season && e.SceneEpisodeNumber == number) return e;
            if (fallback is null && e.SceneSeasonNumber is null && e.SeasonNumber == season && e.EpisodeNumber == number) fallback = e;
        }

        return fallback;
    }

    /// <summary>Episodes a season pack of scene season <paramref name="season"/> holds.</summary>
    public static IEnumerable<Episode> Season(IEnumerable<Episode> episodes, int season) =>
        episodes.Where(e => (e.SceneSeasonNumber ?? e.SeasonNumber) == season);

    /// <summary>Season and episode number scene releases use for an episode.</summary>
    public static (int Season, int Episode) SceneNumbers(Episode e) =>
        e.SceneSeasonNumber is { } s && e.SceneEpisodeNumber is { } n ? (s, n) : (e.SeasonNumber, e.EpisodeNumber);

    /// <summary>Scene season to search for a metadata season: the only scene season its episodes map to, otherwise the season itself.</summary>
    public static int SearchSeason(IEnumerable<Episode> seasonEpisodes, int season)
    {
        var scene = seasonEpisodes.Select(e => SceneNumbers(e).Season).Distinct().Take(2).ToList();
        return scene.Count == 1 ? scene[0] : season;
    }
}
