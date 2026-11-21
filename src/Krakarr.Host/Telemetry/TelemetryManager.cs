using System.Diagnostics;
using System.Security.Claims;
using Krakarr.Core.Configuration;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Krakarr.Host.Telemetry;

/// <summary>Current state of the telemetry export, for Settings → Telemetry.</summary>
public sealed record TelemetryStatus(bool Active, IReadOnlyList<string> Signals, string? Endpoint, string? Protocol, string? Error);

/// <summary>
/// Builds the OpenTelemetry trace, metric and log pipelines from <see cref="TelemetrySettings"/> and rebuilds them
/// whenever the settings are saved, so telemetry can be turned on, off or re-pointed without a restart. While
/// disabled nothing listens to Krakarr's <see cref="KrakarrTelemetry"/> sources, so instrumentation is close to free.
/// </summary>
public sealed class TelemetryManager(
    IServiceScopeFactory scopeFactory,
    SettingsCache settingsCache,
    TelemetryLoggerProvider loggerProvider,
    LibraryStats libraryStats,
    ILogger<TelemetryManager> logger) : IHostedService, IDisposable
{
    /// <summary>Meters exported besides Krakarr's own: ASP.NET Core, HttpClient, runtime, EF Core, SignalR, auth.</summary>
    private static readonly string[] ExtraMeters =
    [
        "System.Runtime",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore.Authentication",
        "Microsoft.AspNetCore.Authorization",
        "Microsoft.AspNetCore.RateLimiting",
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TelemetrySettings? _applied;
    private TracerProvider? _tracerProvider;
    private MeterProvider? _meterProvider;
    private bool _stopped;

    public TelemetryStatus Status { get; private set; } = new(false, [], null, null, null);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        settingsCache.Changed += OnSettingsChanged;
        await ApplyAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        settingsCache.Changed -= OnSettingsChanged;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _stopped = true;
            Shutdown();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Shutdown();
        _gate.Dispose();
    }

    private void OnSettingsChanged(string key)
    {
        // Rebuilding flushes the old exporters, which can take a few seconds: don't block the request that saved.
        if (key == TelemetrySettings.SectionKey) _ = Task.Run(() => ApplyAsync(CancellationToken.None));
    }

    /// <summary>Reads the settings and rebuilds the pipelines if they changed.</summary>
    public async Task ApplyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_stopped) return;
            TelemetrySettings settings;
            using (var scope = scopeFactory.CreateScope())
            {
                settings = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<TelemetrySettings>(ct);
            }

            if (settings == _applied) return;
            Shutdown();
            _applied = settings;
            if (!settings.Enabled)
            {
                Status = new(false, [], null, null, null);
                return;
            }

            try
            {
                Build(settings);
            }
            catch (Exception ex)
            {
                Shutdown();
                Status = new(false, [], settings.Endpoint, settings.Protocol.ToString(), ex.Message);
                logger.LogError(ex, "Could not start OpenTelemetry export");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not apply the telemetry settings");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Build(TelemetrySettings s)
    {
        var signals = new List<string>();
        var attributes = TelemetrySettings.ParsePairs(s.ResourceAttributes)
            .Select(p => new KeyValuePair<string, object>(p.Key, p.Value))
            .ToList();
        ResourceBuilder Resource() => ResourceBuilder.CreateEmpty()
            .AddService(s.ServiceName, serviceVersion: KrakarrTelemetry.Version, serviceInstanceId: Environment.MachineName)
            .AddTelemetrySdk()
            .AddEnvironmentVariableDetector()
            .AddAttributes(attributes);

        if (s.ExportTraces)
        {
            _tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(Resource())
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(s.TraceSampleRatio)))
                .AddSource(KrakarrTelemetry.Name)
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ShouldTraceRequest;
                    o.RecordException = true;
                    o.EnrichWithHttpResponse = EnrichWithUser;
                })
                .AddHttpClientInstrumentation(o =>
                {
                    o.RecordException = true;
                    // Background polls (qBittorrent sync, Jellyfin lookups) would each be a root span every few
                    // seconds; keep only calls made while handling a request, task, command or other traced work.
                    o.FilterHttpRequestMessage = _ => Activity.Current?.Parent is not null;
                })
                .AddOtlpExporter(o => ConfigureExporter(o, s, "v1/traces"))
                .Build();
            signals.Add("traces");
        }

        if (s.ExportMetrics)
        {
            var builder = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(Resource())
                .AddMeter(KrakarrTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(ExtraMeters)
                .AddOtlpExporter((o, reader) =>
                {
                    ConfigureExporter(o, s, "v1/metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = s.MetricExportIntervalSeconds * 1000;
                });
            _meterProvider = builder.Build();
            libraryStats.Start(TimeSpan.FromMinutes(s.LibraryStatsIntervalMinutes));
            signals.Add("metrics");
        }

        if (s.ExportLogs)
        {
            var factory = LoggerFactory.Create(b =>
            {
                // The host's Logging:LogLevel filters already ran before records reach the forwarding provider.
                b.SetMinimumLevel(LogLevel.Trace);
                b.AddOpenTelemetry(o =>
                {
                    o.SetResourceBuilder(Resource());
                    o.IncludeFormattedMessage = true;
                    o.IncludeScopes = true;
                    o.AddOtlpExporter(e => ConfigureExporter(e, s, "v1/logs"));
                });
            });
            loggerProvider.Swap(factory)?.Dispose();
            signals.Add("logs");
        }

        Status = new(true, signals, s.Endpoint, s.Protocol.ToString(), null);
        logger.LogInformation("OpenTelemetry export started ({Signals}) to {Endpoint} over {Protocol}",
            string.Join(", ", signals), string.IsNullOrEmpty(s.Endpoint) ? "the default endpoint" : s.Endpoint, s.Protocol);
    }

    /// <summary>Stops and flushes every pipeline.</summary>
    private void Shutdown()
    {
        DisposeQuietly(_tracerProvider);
        // The final metric export still reads the library snapshot, so stop recounting only afterwards.
        DisposeQuietly(_meterProvider);
        libraryStats.Stop();
        DisposeQuietly(loggerProvider.Swap(null));
        _tracerProvider = null;
        _meterProvider = null;
        _applied = null;
        Status = new(false, [], null, null, null);
    }

    private void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error while stopping a telemetry pipeline");
        }
    }

    public static void ConfigureExporter(OtlpExporterOptions o, TelemetrySettings s, string signalPath)
    {
        o.Protocol = s.Protocol == OtlpProtocol.HttpProtobuf ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        if (ExporterEndpoint(s, signalPath) is { } endpoint) o.Endpoint = endpoint;
        if (!string.IsNullOrWhiteSpace(s.Headers) && s.Headers != TelemetrySettings.HeadersMask) o.Headers = s.Headers;
        o.TimeoutMilliseconds = 10_000;
    }

    /// <summary>
    /// The endpoint for one signal. gRPC takes the endpoint as is; OTLP/HTTP needs the signal path, so
    /// http://collector:4318 (or one already ending in /v1/traces) becomes http://collector:4318/v1/metrics.
    /// Null leaves the exporter's default (OTEL_EXPORTER_OTLP_ENDPOINT or localhost).
    /// </summary>
    public static Uri? ExporterEndpoint(TelemetrySettings s, string signalPath)
    {
        if (string.IsNullOrWhiteSpace(s.Endpoint)) return null;
        var endpoint = s.Endpoint.Trim().TrimEnd('/');
        if (s.Protocol == OtlpProtocol.Grpc) return new Uri(endpoint);
        foreach (var known in (string[])["/v1/traces", "/v1/metrics", "/v1/logs"])
        {
            if (endpoint.EndsWith(known, StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint[..^known.Length];
                break;
            }
        }

        return new Uri($"{endpoint}/{signalPath}");
    }

    /// <summary>Traces API calls (not the SPA's static files, health probes or long-lived SignalR connections).</summary>
    public static bool ShouldTraceRequest(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        return path.Contains("/api/", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith("/api/v1/health", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnrichWithUser(Activity activity, HttpResponse response)
    {
        var user = response.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true) return;
        activity.SetTag("enduser.id", user.Identity.Name);
        activity.SetTag("enduser.role", user.FindFirstValue(ClaimTypes.Role));
    }
}
