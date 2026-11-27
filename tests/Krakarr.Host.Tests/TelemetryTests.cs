using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Krakarr.Host.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

public sealed class TelemetrySettingsTests
{
    [Theory]
    [InlineData("http://collector:4317", OtlpProtocol.Grpc, "v1/traces", "http://collector:4317/")]
    [InlineData("http://collector:4318", OtlpProtocol.HttpProtobuf, "v1/traces", "http://collector:4318/v1/traces")]
    [InlineData("http://collector:4318/", OtlpProtocol.HttpProtobuf, "v1/metrics", "http://collector:4318/v1/metrics")]
    [InlineData("https://otlp.example.com/otlp/v1/traces", OtlpProtocol.HttpProtobuf, "v1/logs", "https://otlp.example.com/otlp/v1/logs")]
    public void Builds_the_endpoint_for_each_signal(string endpoint, OtlpProtocol protocol, string signal, string expected)
    {
        var uri = TelemetryManager.ExporterEndpoint(new TelemetrySettings { Endpoint = endpoint, Protocol = protocol }, signal);
        Assert.Equal(expected, uri?.ToString());
    }

    [Fact]
    public void An_empty_endpoint_leaves_the_exporter_default()
    {
        Assert.Null(TelemetryManager.ExporterEndpoint(new TelemetrySettings { Endpoint = " " }, "v1/traces"));
    }

    [Fact]
    public void Parses_key_value_pairs()
    {
        var pairs = TelemetrySettings.ParsePairs(" Authorization=Bearer abc , x-scope=home%2Clab ");
        Assert.Equal([new("Authorization", "Bearer abc"), new("x-scope", "home,lab")], pairs);
        Assert.Empty(TelemetrySettings.ParsePairs(null));
        Assert.Throws<FormatException>(() => TelemetrySettings.ParsePairs("novalue"));
    }

    [Fact]
    public void Validates_the_settings()
    {
        Assert.Null(new TelemetrySettings().Validate());
        Assert.Null(new TelemetrySettings { Endpoint = null }.Validate());
        Assert.NotNull(new TelemetrySettings { Endpoint = "collector:4317" }.Validate());
        Assert.NotNull(new TelemetrySettings { ServiceName = " " }.Validate());
        Assert.NotNull(new TelemetrySettings { TraceSampleRatio = 1.5 }.Validate());
        Assert.NotNull(new TelemetrySettings { MetricExportIntervalSeconds = 1 }.Validate());
        Assert.NotNull(new TelemetrySettings { Headers = "broken" }.Validate());
        Assert.NotNull(new TelemetrySettings { ResourceAttributes = "=x" }.Validate());
        Assert.Null(new TelemetrySettings { Headers = TelemetrySettings.HeadersMask }.Validate());
    }

    [Theory]
    [InlineData("Search: Dune", "search")]
    [InlineData("Library import (3 movie(s))", "library")]
    [InlineData("Search season 2: The Wire", "search")]
    [InlineData("Organize: Lost", "organize")]
    [InlineData("", "other")]
    public void Command_kinds_are_low_cardinality(string name, string kind) => Assert.Equal(kind, CommandQueue.CommandKind(name));

    [Theory]
    [InlineData("/api/v1/movies", true)]
    [InlineData("/krakarr/api/v1/series/1", true)]
    [InlineData("/api/v1/health", false)]
    [InlineData("/main-abc.js", false)]
    [InlineData("/hubs/events", false)]
    public void Traces_api_requests_only(string path, bool traced)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        Assert.Equal(traced, TelemetryManager.ShouldTraceRequest(context));
    }
}

/// <summary>Minimal OTLP/HTTP receiver: records the path and raw protobuf body of every export.</summary>
public sealed class FakeCollector : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<(string Path, byte[] Body)> _received = [];

    public FakeCollector()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Url + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string Url { get; }

    public IReadOnlyList<(string Path, byte[] Body)> Received
    {
        get
        {
            lock (_received) return _received.ToList();
        }
    }

    /// <summary>True when an export to <paramref name="path"/> contains <paramref name="text"/> (protobuf keeps strings as UTF-8).</summary>
    public bool Contains(string path, string text)
    {
        var needle = Encoding.UTF8.GetBytes(text);
        return Received.Any(r => r.Path == path && r.Body.AsSpan().IndexOf(needle) >= 0);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            lock (_received) _received.Add((context.Request.Url!.AbsolutePath, body.ToArray()));
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/x-protobuf";
            context.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}

public sealed class TelemetryTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private async Task<HttpClient> AdminAsync(CancellationToken ct)
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Saves_settings_masks_headers_and_rejects_bad_values()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminAsync(ct);

        var defaults = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/telemetry", ct);
        Assert.False(defaults.GetProperty("enabled").GetBoolean());

        var saved = await client.PutAsJsonAsync("/api/v1/settings/telemetry",
            new { enabled = false, endpoint = " http://collector:4318 ", protocol = "HttpProtobuf", headers = "Authorization=Bearer s3cret", serviceName = "krakarr-test" }, ct);
        saved.EnsureSuccessStatusCode();
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(TelemetrySettings.HeadersMask, body.GetProperty("headers").GetString());
        Assert.Equal("http://collector:4318", body.GetProperty("endpoint").GetString());

        // Sending the mask back keeps the stored headers.
        (await client.PutAsJsonAsync("/api/v1/settings/telemetry",
            new { enabled = false, endpoint = "http://collector:4318", protocol = "HttpProtobuf", headers = TelemetrySettings.HeadersMask, serviceName = "krakarr-test" }, ct)).EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<TelemetrySettings>(ct);
            Assert.Equal("Authorization=Bearer s3cret", stored.Headers);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/settings/telemetry", new { endpoint = "collector:4317", serviceName = "k" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/settings/telemetry", new { traceSampleRatio = 2, serviceName = "k" }, ct)).StatusCode);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/telemetry/status", ct);
        Assert.False(status.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Exports_request_traces_and_metrics_over_otlp_http()
    {
        var ct = TestContext.Current.CancellationToken;
        using var collector = new FakeCollector();
        var client = await AdminAsync(ct);
        var manager = factory.Services.GetRequiredService<TelemetryManager>();

        (await client.PutAsJsonAsync("/api/v1/settings/telemetry", new
        {
            enabled = true,
            endpoint = collector.Url,
            protocol = "HttpProtobuf",
            serviceName = "krakarr-otel-test",
            resourceAttributes = "deployment.environment=ci",
            exportTraces = true,
            exportMetrics = true,
            exportLogs = true,
        }, ct)).EnsureSuccessStatusCode();
        try
        {
            // Hosted services don't run in tests: start the manager by hand.
            await manager.StartAsync(ct);
            var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/telemetry/status", ct);
            Assert.True(status.GetProperty("active").GetBoolean());
            Assert.Equal(["traces", "metrics", "logs"], status.GetProperty("signals").EnumerateArray().Select(s => s.GetString()));

            (await client.GetAsync("/api/v1/qualityprofiles", ct)).EnsureSuccessStatusCode();
            (await client.GetAsync("/api/v1/system/status", ct)).EnsureSuccessStatusCode();
        }
        finally
        {
            // Stopping flushes every pipeline.
            await manager.StopAsync(ct);
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SaveAsync(new TelemetrySettings(), ct);
        }

        Assert.True(collector.Contains("/v1/traces", "/api/v1/qualityprofiles"), "request span exported");
        Assert.True(collector.Contains("/v1/traces", "krakarr-otel-test"), "service.name resource attribute");
        Assert.True(collector.Contains("/v1/traces", "deployment.environment"), "custom resource attribute");
        Assert.True(collector.Contains("/v1/metrics", "http.server.request.duration"), "ASP.NET Core metrics exported");
        Assert.True(collector.Contains("/v1/metrics", "krakarr.library.movies"), "library gauges exported");
        Assert.True(collector.Contains("/v1/logs", "OpenTelemetry export started"), "logs exported");
    }

    [Fact]
    public async Task Counts_the_library_with_aggregate_queries()
    {
        var ct = TestContext.Current.CancellationToken;
        var stats = factory.Services.GetRequiredService<LibraryStats>();
        var before = await stats.CountAsync(ct);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var movie = new Movie { TmdbId = 9101, Title = "Stats Movie", Year = 2020, Path = "/m/stats", QualityProfileId = profile };
            var missing = new Movie { TmdbId = 9102, Title = "Stats Missing", Year = 2020, Path = "/m/stats2", QualityProfileId = profile };
            var series = new Krakarr.Core.Domain.Series { TvdbId = 9201, Title = "Stats Show", Year = 2020, Path = "/tv/stats", QualityProfileId = profile, Monitored = false };
            db.AddRange(movie, missing, series);
            await db.SaveChangesAsync(ct);
            movie.MovieFile = new MovieFile { MovieId = movie.Id, RelativePath = "a.mkv", QualityId = 17, Size = 1000 };
            var file = new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = "e.mkv", QualityId = 17, Size = 500 };
            db.EpisodeFiles.Add(file);
            await db.SaveChangesAsync(ct);
            db.Episodes.AddRange(
                new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 1, AirDate = new DateOnly(2020, 1, 1), EpisodeFileId = file.Id },
                new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 2, AirDate = new DateOnly(2020, 1, 8) },
                new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 3, AirDate = new DateOnly(2999, 1, 1) },
                new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 4, Monitored = false });
            await db.SaveChangesAsync(ct);
        }

        var after = await stats.CountAsync(ct);
        Assert.Equal(before.MoviesDownloaded + 1, after.MoviesDownloaded);
        Assert.Equal(before.MoviesMissing + 1, after.MoviesMissing);
        Assert.Equal(before.SeriesUnmonitored + 1, after.SeriesUnmonitored);
        Assert.Equal(before.EpisodesDownloaded + 1, after.EpisodesDownloaded);
        Assert.Equal(before.EpisodesMissing + 1, after.EpisodesMissing);
        Assert.Equal(before.EpisodesUnaired + 1, after.EpisodesUnaired);
        Assert.Equal(before.EpisodesUnmonitored + 1, after.EpisodesUnmonitored);
        Assert.Equal(before.MovieFilesSize + 1000, after.MovieFilesSize);
        Assert.Equal(before.EpisodeFilesSize + 500, after.EpisodeFilesSize);
    }

    [Fact]
    public async Task Counts_history_events_and_library_changes_when_saved()
    {
        var ct = TestContext.Current.CancellationToken;
        var seen = new List<(string Instrument, string? Event, string? MediaType)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == KrakarrTelemetry.Meter) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            string? eventType = null, mediaType = null;
            foreach (var t in tags)
            {
                if (t.Key == KrakarrTelemetry.EventTag) eventType = t.Value?.ToString();
                if (t.Key == KrakarrTelemetry.MediaTypeTag) mediaType = t.Value?.ToString();
            }

            lock (seen) seen.Add((instrument.Name, eventType, mediaType));
        });
        listener.Start();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var movie = new Movie { TmdbId = 9301, Title = "Counted", Year = 2021, Path = "/m/counted", QualityProfileId = profile };
            db.Movies.Add(movie);
            await db.SaveChangesAsync(ct);
            db.History.Add(new HistoryEntry { EventType = HistoryEventType.Grabbed, SourceTitle = "Counted.2021.1080p", MovieId = movie.Id });
            await db.SaveChangesAsync(ct);
            db.Movies.Remove(movie);
            await db.SaveChangesAsync(ct);
        }

        lock (seen)
        {
            Assert.Contains(("krakarr.library.items.added", null, "movie"), seen);
            Assert.Contains(("krakarr.history.events", "grabbed", "movie"), seen);
            Assert.Contains(("krakarr.library.items.removed", null, "movie"), seen);
        }
    }
}
