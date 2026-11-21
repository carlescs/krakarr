using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Krakarr.Core.Telemetry;

/// <summary>
/// Krakarr's own traces and metrics. Modules record through these; the host exports them over OTLP when
/// telemetry is enabled in Settings → Telemetry. With nothing listening, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns null and instruments drop measurements, so instrumented code costs next to nothing when telemetry is off.
/// </summary>
/// <remarks>
/// Keep tag values low-cardinality (media type, indexer name, task id, outcome). Titles and ids belong on spans, never on metrics.
/// </remarks>
public static class KrakarrTelemetry
{
    public const string Name = "Krakarr";

    public static readonly string Version =
        typeof(KrakarrTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static readonly ActivitySource ActivitySource = new(Name, Version);
    public static readonly Meter Meter = new(Name, Version);

    // ---- Indexers ----
    public static readonly Counter<long> IndexerQueries = Meter.CreateCounter<long>(
        "krakarr.indexer.queries", "{query}", "Queries sent to indexers.");
    public static readonly Histogram<double> IndexerQueryDuration = Meter.CreateHistogram<double>(
        "krakarr.indexer.query.duration", "s", "Time an indexer took to answer a query.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.25, 0.5, 1, 2, 5, 10, 20, 30, 60] });
    public static readonly Counter<long> IndexerReleases = Meter.CreateCounter<long>(
        "krakarr.indexer.releases", "{release}", "Releases returned by indexers (after the minimum seeders filter).");

    // ---- Downloads ----
    public static readonly Counter<long> Grabs = Meter.CreateCounter<long>(
        "krakarr.downloads.grabs", "{grab}", "Releases sent to the download client.");
    public static readonly Counter<long> GrabbedBytes = Meter.CreateCounter<long>(
        "krakarr.downloads.grabbed.size", "By", "Size of the releases sent to the download client.");

    // ---- Library ----
    public static readonly Counter<long> HistoryEvents = Meter.CreateCounter<long>(
        "krakarr.history.events", "{event}", "History events: grabbed, imported, download failed, file deleted, subtitle downloaded.");
    public static readonly Counter<long> LibraryItemsAdded = Meter.CreateCounter<long>(
        "krakarr.library.items.added", "{item}", "Movies and series added to the library.");
    public static readonly Counter<long> LibraryItemsRemoved = Meter.CreateCounter<long>(
        "krakarr.library.items.removed", "{item}", "Movies and series removed from the library.");
    public static readonly Counter<long> MediaRequestsCreated = Meter.CreateCounter<long>(
        "krakarr.media_requests.created", "{request}", "Movie and series requests made by users.");

    // ---- Background work ----
    public static readonly Histogram<double> TaskDuration = Meter.CreateHistogram<double>(
        "krakarr.task.duration", "s", "Duration of scheduled task runs.",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.01, 0.05, 0.1, 0.5, 1, 5, 15, 60, 300, 900, 3600] });
    public static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "krakarr.command.duration", "s", "Duration of queued commands (searches, refreshes, library imports).",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.1, 0.5, 1, 5, 15, 60, 300, 900, 3600] });

    // ---- Auth ----
    public static readonly Counter<long> Logins = Meter.CreateCounter<long>(
        "krakarr.auth.logins", "{login}", "Sign-in attempts.");

    // ---- Tag names ----
    public const string MediaTypeTag = "krakarr.media_type";
    public const string IndexerTag = "krakarr.indexer";
    public const string OutcomeTag = "krakarr.outcome";
    public const string TaskTag = "krakarr.task";
    public const string CommandTag = "krakarr.command";
    public const string EventTag = "krakarr.event";
    public const string SearchKindTag = "krakarr.search.kind";
    public const string ClientTag = "krakarr.download_client";

    public const string Success = "success";
    public const string Failure = "failure";

    /// <summary>"movie", "series" or "other".</summary>
    public static string MediaTypeValue(Domain.MediaType? type) => type switch
    {
        Domain.MediaType.Movie => "movie",
        Domain.MediaType.Series => "series",
        _ => "other",
    };

    /// <summary>Marks the activity failed and records the exception as a span event.</summary>
    public static void RecordException(this Activity? activity, Exception ex)
    {
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.AddException(ex);
    }
}
