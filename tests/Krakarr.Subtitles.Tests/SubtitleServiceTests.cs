using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Subtitles.Tests;

public sealed class SubtitleServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("krakarr-subs");
    private readonly FakeProvider _provider = new();
    private ServiceProvider _services = null!;
    private string _moviePath = null!;
    private int _movieFileId;

    private sealed class FakeProvider : ISubtitleProvider
    {
        public List<SubtitleCandidate> Candidates { get; } = [];
        public List<string> Downloads { get; } = [];
        public SubtitleQuery? LastQuery { get; private set; }
        public string Name => "fake";
        public bool IsConfigured(SubtitleSettings settings) => true;

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<SubtitleCandidate>>(Candidates.Where(c => query.Languages.Contains(c.Language)).ToList());
        }

        public Task<SubtitleContent> DownloadAsync(SubtitleCandidate candidate, SubtitleSettings settings, CancellationToken ct = default)
        {
            Downloads.Add(candidate.Payload);
            return Task.FromResult(new SubtitleContent(System.Text.Encoding.UTF8.GetBytes($"subtitle {candidate.Payload}"), ".srt"));
        }

        public Task<string> TestAsync(SubtitleSettings settings, CancellationToken ct = default) => Task.FromResult("ok");
    }

    private static SubtitleCandidate C(string payload, string release, string lang = "en", bool hi = false) => new()
    {
        Provider = "fake", Payload = payload, Language = lang, ReleaseNames = [release], HearingImpaired = hi, MatchedById = true,
    };

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        services.AddSingleton<ISubtitleProvider>(_provider);
        services.AddSingleton<IEventHub, NullEventHub>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SubtitleService>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        var profile = new QualityProfile { Name = "HD", AllowedQualityIds = [17], CutoffQualityId = 17 };
        var movie = new Movie { TmdbId = 603, ImdbId = "tt0133093", Title = "The Matrix", Year = 1999, Path = Path.Combine(_root.FullName, "The Matrix (1999)"), QualityProfile = profile };
        db.Movies.Add(movie);
        await db.SaveChangesAsync();
        var file = new MovieFile { MovieId = movie.Id, RelativePath = "The Matrix (1999) [Bluray-1080p].mkv", SceneName = "The.Matrix.1999.1080p.BluRay.x264-SPARKS", QualityId = 17 };
        db.MovieFiles.Add(file);
        await db.SaveChangesAsync();
        _movieFileId = file.Id;
        _moviePath = Path.Combine(movie.Path, file.RelativePath);
        Directory.CreateDirectory(movie.Path);
        await File.WriteAllBytesAsync(_moviePath, new byte[300_000]);
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SaveAsync(new SubtitleSettings { Enabled = true, Languages = ["en", "es"], MinimumScoreMovies = 70 });
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        _root.Delete(true);
    }

    private async Task<string> SearchAsync()
    {
        using var scope = _services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
        var target = await service.GetMovieTargetAsync(_movieFileId, TestContext.Current.CancellationToken);
        return await service.SearchMissingAsync(target!, TestContext.Current.CancellationToken);
    }

    private async Task<List<SubtitleFile>> RowsAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().SubtitleFiles.OrderBy(s => s.Language).ToListAsync();
    }

    [Fact]
    public async Task Downloads_best_subtitle_per_missing_language()
    {
        _provider.Candidates.AddRange(
        [
            C("en-low", "The.Matrix.1999.DVDRip.XviD-AMIABLE"),
            C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"),
            C("es-ok", "The.Matrix.1999.1080p.BluRay.x264-OTHER", lang: "es"),
            C("fr", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", lang: "fr"),
        ]);

        var summary = await SearchAsync();

        Assert.Contains("downloaded en, es", summary);
        Assert.Equal(["en-best", "es-ok"], _provider.Downloads);
        Assert.Equal("tt0133093", _provider.LastQuery!.ImdbId);
        Assert.Equal(16, _provider.LastQuery.FileHash!.Length);
        var rows = await RowsAsync();
        Assert.Equal(["en", "es"], rows.Select(r => r.Language));
        Assert.All(rows, r => Assert.True(File.Exists(r.Path)));
        Assert.EndsWith("The Matrix (1999) [Bluray-1080p].en.srt", rows[0].Path);
        Assert.Equal(100, rows[0].Score);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        Assert.Equal(2, await db.History.CountAsync(h => h.EventType == HistoryEventType.SubtitleDownloaded));
        Assert.NotNull((await db.MovieFiles.SingleAsync()).LastSubtitleSearch);
    }

    [Fact]
    public async Task Detects_existing_subtitles_on_disk()
    {
        await File.WriteAllTextAsync(Path.ChangeExtension(_moviePath, null) + ".eng.srt", "existing");
        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));
        _provider.Candidates.Add(C("es-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", lang: "es"));

        await SearchAsync();

        Assert.Equal(["es-best"], _provider.Downloads); // English already there
        var rows = await RowsAsync();
        Assert.Equal("disk", rows.Single(r => r.Language == "en").Provider);
    }

    [Fact]
    public async Task Skips_candidates_below_minimum_score()
    {
        _provider.Candidates.Add(C("en-low", "Completely.Different.Release.2004.720p.HDTV"));
        var summary = await SearchAsync();
        Assert.Empty(_provider.Downloads);
        Assert.Contains("nothing suitable", summary);
    }

    [Fact]
    public async Task Forgets_deleted_files_and_lists_wanted()
    {
        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));
        await SearchAsync();
        var en = (await RowsAsync()).Single();
        File.Delete(en.Path);

        using var scope = _services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
        await service.SyncDiskAsync((await service.GetMovieTargetAsync(_movieFileId))!);
        Assert.Empty(await RowsAsync());

        var wanted = await service.WantedAsync(10);
        var item = Assert.Single(wanted);
        Assert.Equal(["en", "es"], item.MissingLanguages);
        Assert.Equal("The Matrix (1999)", item.Target.Label);
    }
}

public sealed class SubtitleSearchTaskTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    private sealed class CountingProvider : ISubtitleProvider
    {
        public int Searches { get; private set; }
        public string Name => "count";
        public bool IsConfigured(SubtitleSettings settings) => true;
        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<SubtitleCandidate>>([]);
        }
        public Task<SubtitleContent> DownloadAsync(SubtitleCandidate candidate, SubtitleSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> TestAsync(SubtitleSettings settings, CancellationToken ct = default) => Task.FromResult("ok");
    }

    private readonly CountingProvider _provider = new();

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        services.AddSingleton<ISubtitleProvider>(_provider);
        services.AddSingleton<IEventHub, NullEventHub>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SubtitleService>();
        services.AddScoped<SubtitleSearchTask>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        var movie = new Movie { TmdbId = 1, Title = "A", Year = 2000, Path = "/nonexistent", QualityProfile = new QualityProfile { Name = "p", AllowedQualityIds = [1], CutoffQualityId = 1 } };
        db.Movies.Add(movie);
        await db.SaveChangesAsync();
        db.MovieFiles.Add(new MovieFile { MovieId = movie.Id, RelativePath = "a.mkv", QualityId = 1 });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task RunAsync(SubtitleSettings settings)
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SaveAsync(settings);
        await scope.ServiceProvider.GetRequiredService<SubtitleSearchTask>().ExecuteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Searches_new_files_once_and_again_when_languages_change()
    {
        await RunAsync(new SubtitleSettings { Enabled = false });
        Assert.Equal(0, _provider.Searches);

        await RunAsync(new SubtitleSettings { Enabled = true, Languages = ["en"] });
        Assert.Equal(1, _provider.Searches); // never searched before

        await RunAsync(new SubtitleSettings { Enabled = true, Languages = ["en"] });
        Assert.Equal(1, _provider.Searches); // not due yet

        await RunAsync(new SubtitleSettings { Enabled = true, Languages = ["en", "fr"] });
        Assert.Equal(2, _provider.Searches); // language added → due again
    }
}
