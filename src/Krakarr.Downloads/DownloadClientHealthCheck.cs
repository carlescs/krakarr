using Krakarr.Core.Configuration;
using Krakarr.Core.Health;

namespace Krakarr.Downloads;

/// <summary>
/// The download client can't be reached or didn't start, from the error each client keeps from its last sync (no
/// network calls here).
/// </summary>
public sealed class DownloadClientHealthCheck(DownloadClientRouter router) : IHealthCheck
{
    public string Id => "downloadclient";
    public string Name => "Download client";

    public Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default)
    {
        var selected = router.CurrentSettings.Client;
        var problems = router.All
            .Where(c => c.LastError is not null && (c.Kind == selected || c.IsActive))
            .Select(c => c.Kind == selected
                ? new HealthProblem(c.Kind.ToString(), HealthSeverity.Error,
                    $"{Label(c.Kind)} is not working, so nothing can be downloaded: {c.LastError}", "/settings/downloads")
                : new HealthProblem(c.Kind.ToString(), HealthSeverity.Warning,
                    $"{Label(c.Kind)} still has downloads from before switching clients but is not working: {c.LastError}", "/downloads"))
            .ToList();
        return Task.FromResult<IReadOnlyList<HealthProblem>>(problems);
    }

    private static string Label(DownloadClientKind kind) => kind == DownloadClientKind.QBittorrent ? "qBittorrent" : "The built-in download client";
}
