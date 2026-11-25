using Krakarr.Core.Configuration;
using Krakarr.Core.Health;
using Krakarr.Data;

namespace Krakarr.Subtitles.Tests;

public sealed class SubtitleHealthCheckTests
{
    private readonly Provider _provider = new();
    private SubtitleSettings _settings = new() { Enabled = true, OpenSubtitles = new OpenSubtitlesSettings { ApiKey = "key" } };

    private sealed class Provider : ISubtitleProvider
    {
        public int Tests { get; private set; }
        public string? Failure { get; set; }
        public DateTimeOffset? BackoffUntil { get; set; }
        public string Name => "opensubtitles";
        public string DisplayName => "OpenSubtitles.com";
        public bool IsConfigured(SubtitleSettings settings) => settings.OpenSubtitles.Enabled && !string.IsNullOrWhiteSpace(settings.OpenSubtitles.ApiKey);

        public Task<string> TestAsync(SubtitleSettings settings, CancellationToken ct = default)
        {
            Tests++;
            return Failure is null ? Task.FromResult("OK") : Task.FromException<string>(new SubtitleProviderException(Failure));
        }

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SubtitleContent> DownloadAsync(SubtitleCandidate candidate, SubtitleSettings settings, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class Settings(Func<SubtitleSettings> subtitles) : ISettingsService
    {
        public Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.FromResult(subtitles() as T ?? new T());
        public Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.CompletedTask;
    }

    private readonly HealthValidationCache _cache = new(TimeProvider.System);

    private Task<IReadOnlyList<HealthProblem>> CheckAsync() =>
        new SubtitleHealthCheck(new Settings(() => _settings), [_provider], _cache, TimeProvider.System).CheckAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Nothing_is_checked_while_subtitles_are_off()
    {
        _settings = new SubtitleSettings { Enabled = false };
        Assert.Empty(await CheckAsync());
    }

    [Fact]
    public async Task Subtitles_on_without_a_configured_provider_is_an_error()
    {
        _settings = _settings with { OpenSubtitles = new OpenSubtitlesSettings { ApiKey = " " } };
        var problem = Assert.Single(await CheckAsync());
        Assert.Equal(("none", HealthSeverity.Error), (problem.Key, problem.Severity));
        Assert.Equal(0, _provider.Tests);
    }

    [Fact]
    public async Task A_failing_provider_is_a_warning_and_tests_are_remembered()
    {
        _provider.Failure = "OpenSubtitles rejected the API key";
        var problem = Assert.Single(await CheckAsync());
        Assert.Equal(("opensubtitles", HealthSeverity.Warning), (problem.Key, problem.Severity));
        Assert.Equal("OpenSubtitles.com doesn't work: OpenSubtitles rejected the API key", problem.Message);
        Assert.Single(await CheckAsync());
        Assert.Equal(1, _provider.Tests);

        // A new key is tested at once.
        _provider.Failure = null;
        _settings = _settings with { OpenSubtitles = new OpenSubtitlesSettings { ApiKey = "new key" } };
        Assert.Empty(await CheckAsync());
        Assert.Equal(2, _provider.Tests);
    }

    [Fact]
    public async Task A_provider_that_asked_to_back_off_is_not_tested()
    {
        _provider.BackoffUntil = DateTimeOffset.UtcNow.AddHours(1);
        Assert.Empty(await CheckAsync());
        Assert.Equal(0, _provider.Tests);
    }
}
