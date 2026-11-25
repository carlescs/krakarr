using System.Net;
using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Health;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Series.Tests;

public sealed class MetadataHealthCheckTests
{
    private readonly Tmdb _tmdb = new();
    private readonly Tvdb _tvdb = new();
    private readonly Clock _time = new();
    private readonly HealthValidationCache _cache;
    private MetadataSettings _meta = new() { TmdbApiKey = "good" };

    public MetadataHealthCheckTests() => _cache = new HealthValidationCache(_time);

    /// <summary>Answers 401 unless the API key is "good"; counts the requests.</summary>
    private sealed class Tmdb : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Down { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Down) throw new HttpRequestException("Name or service not known");
            var ok = request.RequestUri!.Query.Contains("api_key=good");
            return Task.FromResult(ok
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class Tvdb : ITvdbClient
    {
        public Exception? Failure { get; set; }
        public int Logins { get; private set; }
        public Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SeriesInfo> GetSeriesAsync(int tvdbId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task LoginAsync(CancellationToken ct = default)
        {
            Logins++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Settings(Func<MetadataSettings> meta) : ISettingsService
    {
        public Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.FromResult(meta() as T ?? new T());
        public Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.CompletedTask;
    }

    private Task<IReadOnlyList<HealthProblem>> CheckAsync() =>
        new MetadataHealthCheck(new Settings(() => _meta), new TmdbApi(new Factory(_tmdb), new ConfigurationBuilder().Build()), _tvdb, _cache)
            .CheckAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_missing_tmdb_key_is_an_error()
    {
        _meta = new MetadataSettings();
        var problem = Assert.Single(await CheckAsync());
        Assert.Equal(("tmdb", HealthSeverity.Error), (problem.Key, problem.Severity));
        Assert.Contains("No TMDB API key", problem.Message);
        Assert.Equal(0, _tmdb.Calls);
    }

    [Fact]
    public async Task A_valid_key_is_checked_once_a_day()
    {
        Assert.Empty(await CheckAsync());
        Assert.Empty(await CheckAsync());
        Assert.Equal(1, _tmdb.Calls);

        _time.Now += HealthValidationCache.SuccessLifetime;
        Assert.Empty(await CheckAsync());
        Assert.Equal(2, _tmdb.Calls);
    }

    [Fact]
    public async Task A_rejected_key_is_an_error_and_a_new_key_is_checked_at_once()
    {
        _meta = new MetadataSettings { TmdbApiKey = "wrong" };
        var problem = Assert.Single(await CheckAsync());
        Assert.Equal(HealthSeverity.Error, problem.Severity);
        Assert.Contains("rejected", problem.Message);

        _meta = new MetadataSettings { TmdbApiKey = "good" };
        Assert.Empty(await CheckAsync());
        Assert.Equal(2, _tmdb.Calls);
    }

    [Fact]
    public async Task An_unreachable_service_is_a_warning_retried_after_an_hour()
    {
        _tmdb.Down = true;
        var problem = Assert.Single(await CheckAsync());
        Assert.Equal(HealthSeverity.Warning, problem.Severity);
        Assert.Contains("Could not reach TMDB", problem.Message);

        _tmdb.Down = false;
        Assert.Single(await CheckAsync()); // remembered
        _time.Now += HealthValidationCache.FailureLifetime;
        Assert.Empty(await CheckAsync());
    }

    [Fact]
    public async Task TheTvdb_is_only_checked_when_it_is_the_series_source()
    {
        _tvdb.Failure = new MetadataException("TheTVDB login failed (HTTP 401)", HttpStatusCode.Unauthorized);
        Assert.Empty(await CheckAsync());
        Assert.Equal(0, _tvdb.Logins);

        _meta = _meta with { SeriesSource = SeriesMetadataSource.Tvdb };
        var missing = Assert.Single(await CheckAsync());
        Assert.Equal(("tvdb", HealthSeverity.Error), (missing.Key, missing.Severity));
        Assert.Contains("no TheTVDB API key", missing.Message);

        _meta = _meta with { TvdbApiKey = "key" };
        var rejected = Assert.Single(await CheckAsync());
        Assert.Contains("rejected the API key or PIN", rejected.Message);

        // Changing the PIN is a new configuration, validated again.
        _tvdb.Failure = null;
        _meta = _meta with { TvdbPin = "1234" };
        Assert.Empty(await CheckAsync());
        Assert.Equal(2, _tvdb.Logins);
    }
}
