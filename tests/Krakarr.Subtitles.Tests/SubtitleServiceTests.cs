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
        public SubtitleQuery? LastQuery { get; set; }
        public bool Fail { get; set; }
        public string Name => "fake";
        public bool IsConfigured(SubtitleSettings settings) => true;

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default)
        {
            LastQuery = query;
            if (Fail) throw new SubtitleProviderException("server error");
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
        services.AddSingleton<Sync.SubtitleSyncGate>();
        services.AddSingleton<Krakarr.Core.Jobs.BackgroundTasks>();
        services.AddSingleton<Sync.SubtitleSyncProgress>();
        services.AddScoped<Sync.SubtitleSyncService>();
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

    private async Task<string> SearchAsync(bool upgrade = false)
    {
        using var scope = _services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
        var target = await service.GetMovieTargetAsync(_movieFileId, TestContext.Current.CancellationToken);
        return await service.SearchMissingAsync(target!, TestContext.Current.CancellationToken, upgrade);
    }

    private async Task SaveSettingsAsync(Func<SubtitleSettings, SubtitleSettings> change)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SaveAsync(change(await settings.GetAsync<SubtitleSettings>()));
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
        Assert.Equal(2, await db.History.CountAsync(h => h.EventType == HistoryEventType.SubtitleDownloaded, TestContext.Current.CancellationToken));
        Assert.NotNull((await db.MovieFiles.SingleAsync(TestContext.Current.CancellationToken)).LastSubtitleSearch);
    }

    [Fact]
    public async Task Detects_existing_subtitles_on_disk()
    {
        await File.WriteAllTextAsync(Path.ChangeExtension(_moviePath, null) + ".eng.srt", "existing", TestContext.Current.CancellationToken);
        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));
        _provider.Candidates.Add(C("es-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", lang: "es"));

        await SearchAsync();

        Assert.Equal(["es-best"], _provider.Downloads); // English already there
        var rows = await RowsAsync();
        Assert.Equal("disk", rows.Single(r => r.Language == "en").Provider);
    }

    [Fact]
    public async Task Upgrade_replaces_present_subtitles_only_with_a_better_score()
    {
        await SaveSettingsAsync(s => s with { UpgradeDays = 0 });
        _provider.Candidates.Add(C("en-ok", "The.Matrix.1999.1080p.BluRay.x264-OTHER"));
        _provider.Candidates.Add(C("es-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", lang: "es"));
        await SearchAsync();
        Assert.Equal(["en-ok", "es-best"], _provider.Downloads);

        // Without upgrade, present languages are skipped.
        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));
        Assert.Contains("all subtitles present", await SearchAsync());
        Assert.Equal(2, _provider.Downloads.Count);

        var summary = await SearchAsync(upgrade: true);
        Assert.Contains("upgraded en", summary);
        Assert.Equal(["en-ok", "es-best", "en-best"], _provider.Downloads); // es already had the best score
        var en = (await RowsAsync()).Single(r => r.Language == "en");
        Assert.Equal(100, en.Score);
        Assert.Equal("subtitle en-best", await File.ReadAllTextAsync(en.Path, TestContext.Current.CancellationToken));

        Assert.Contains("no better subtitles found", await SearchAsync(upgrade: true));
    }

    [Fact]
    public async Task Automatic_search_upgrades_recent_subtitles_below_the_upgrade_score()
    {
        _provider.Candidates.Add(C("en-ok", "The.Matrix.1999.1080p.BluRay.x264-OTHER")); // 80
        _provider.Candidates.Add(C("es-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", lang: "es")); // 100
        await SearchAsync();

        // Nothing better yet: the 80 is kept, and the 100 is never searched again.
        _provider.LastQuery = null;
        Assert.Contains("no better subtitles found", await SearchAsync());
        Assert.Equal(["en"], _provider.LastQuery!.Languages);

        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));
        Assert.Contains("upgraded en", await SearchAsync());
        Assert.Equal(100, (await RowsAsync()).Single(r => r.Language == "en").Score);
        Assert.Contains("all subtitles present", await SearchAsync());
    }

    [Fact]
    public async Task Automatic_search_leaves_manual_and_old_subtitles_alone()
    {
        _provider.Candidates.Add(C("en-ok", "The.Matrix.1999.1080p.BluRay.x264-OTHER"));
        _provider.Candidates.Add(C("es-ok", "The.Matrix.1999.1080p.BluRay.x264-OTHER", lang: "es"));
        await SearchAsync();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            await db.SubtitleFiles.Where(r => r.Language == "en").ExecuteUpdateAsync(x => x.SetProperty(r => r.Manual, true), TestContext.Current.CancellationToken);
            await db.SubtitleFiles.Where(r => r.Language == "es").ExecuteUpdateAsync(x => x.SetProperty(r => r.DateAdded, DateTimeOffset.UtcNow.AddDays(-8)), TestContext.Current.CancellationToken);
        }

        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));
        _provider.Candidates.Add(C("es-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", lang: "es"));
        Assert.Contains("all subtitles present", await SearchAsync());
        Assert.Equal(["en-ok", "es-ok"], _provider.Downloads);

        // A search started by hand still upgrades both.
        Assert.Contains("upgraded en, es", await SearchAsync(upgrade: true));
    }

    private static string Srt(IEnumerable<double> starts) => string.Concat(starts.Select((t, i) =>
        $"{i + 1}\n{TimeSpan.FromMilliseconds(t):hh\\:mm\\:ss\\,fff} --> {TimeSpan.FromMilliseconds(t + 1000):hh\\:mm\\:ss\\,fff}\nLine {i}\n\n"));

    private async Task<SubtitleFile> AddSubtitleAsync(string language, int score, string content)
    {
        var path = SubtitleService.SubtitlePath(_moviePath, language, false, false, ".srt");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var row = new SubtitleFile { MovieFileId = _movieFileId, Language = language, Path = path, Provider = "fake", Score = score };
        db.SubtitleFiles.Add(row);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return row;
    }

    [Fact]
    public async Task Syncs_against_a_perfect_subtitle_of_the_same_file_without_tools()
    {
        // Paths that don't exist: no tool is used, whatever is installed on the machine.
        await SaveSettingsAsync(s => s with { Sync = new SubtitleSyncSettings { AlassPath = "/none/alass", FfsubsyncPath = "/none/ffsubsync", FfmpegPath = "/none/ffmpeg" } });
        var random = new Random(3);
        var starts = new List<double>();
        for (double t = 2_000; starts.Count < 200; t += 1_500 + random.Next(4_000)) starts.Add(t);
        await AddSubtitleAsync("en", 100, Srt(starts));
        var late = await AddSubtitleAsync("es", 70, Srt(starts.Select(t => t + 3_000)));

        using var scope = _services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<Sync.SubtitleSyncService>();
        var result = await sync.SyncAsync(late.Id, TestContext.Current.CancellationToken);

        Assert.True(result!.Success, result.Message);
        Assert.Contains("-3.000 s", result.Message);
        Assert.Equal(Srt(starts), await File.ReadAllTextAsync(late.Path, TestContext.Current.CancellationToken));
        var row = (await RowsAsync()).Single(r => r.Language == "es");
        Assert.NotNull(row.SyncedAt);
        Assert.Contains("English", row.SyncNote);
        var done = Assert.Single(_services.GetRequiredService<Krakarr.Core.Jobs.BackgroundTasks>().Current);
        Assert.Equal(Krakarr.Core.Jobs.BackgroundTaskState.Succeeded, done.State);
        Assert.Equal(result.Message, done.Message);
        var (title, link) = await sync.DescribeAsync(row, TestContext.Current.CancellationToken);
        Assert.Equal("The Matrix (1999) · Spanish", title);
        Assert.StartsWith("/movies/", link);
    }

    [Fact]
    public async Task A_sync_cancelled_while_waiting_its_turn_stops_and_is_not_retried()
    {
        var row = await AddSubtitleAsync("es", 70, Srt([1_000, 5_000]));
        var gate = _services.GetRequiredService<Sync.SubtitleSyncGate>();
        var progress = _services.GetRequiredService<Sync.SubtitleSyncProgress>();
        var tasks = _services.GetRequiredService<Krakarr.Core.Jobs.BackgroundTasks>();
        await gate.Lock.WaitAsync(TestContext.Current.CancellationToken); // another sync is running
        try
        {
            using var scope = _services.CreateScope();
            var running = scope.ServiceProvider.GetRequiredService<Sync.SubtitleSyncService>().SyncAsync(row.Id, TestContext.Current.CancellationToken);
            while (tasks.Current.Count == 0) await Task.Delay(10, TestContext.Current.CancellationToken);

            Assert.True(progress.Cancel(row.Id));
            var result = await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.False(result!.Success);
            Assert.Equal(Sync.SubtitleSyncProgress.CancelledResult, result.Message);
        }
        finally
        {
            gate.Lock.Release();
        }

        var stored = (await RowsAsync()).Single();
        Assert.Equal("Sync cancelled", stored.SyncNote);
        Assert.Null(stored.SyncedAt);
        Assert.Equal(Krakarr.Core.Jobs.BackgroundTaskState.Cancelled, Assert.Single(tasks.Current).State);
        Assert.False(progress.Cancel(row.Id));
    }

    [Fact]
    public async Task Sync_without_tools_or_reference_records_why()
    {
        await SaveSettingsAsync(s => s with { Sync = new SubtitleSyncSettings { AlassPath = "/none/alass", FfsubsyncPath = "/none/ffsubsync", FfmpegPath = "/none/ffmpeg" } });
        var row = await AddSubtitleAsync("es", 70, Srt([1_000, 5_000]));

        using var scope = _services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<Sync.SubtitleSyncService>().SyncAsync(row.Id, TestContext.Current.CancellationToken);

        Assert.False(result!.Success);
        var stored = (await RowsAsync()).Single();
        Assert.Null(stored.SyncedAt);
        Assert.StartsWith("Sync failed: Install alass or ffsubsync", stored.SyncNote);
    }

    [Fact]
    public async Task Shifts_a_subtitle_by_hand()
    {
        var row = await AddSubtitleAsync("en", 70, Srt([1_000, 5_000]));

        using var scope = _services.CreateScope();
        var shifted = await scope.ServiceProvider.GetRequiredService<Sync.SubtitleSyncService>().ShiftAsync(row.Id, -750, TestContext.Current.CancellationToken);

        Assert.Equal("Shifted by -750 ms by hand", shifted!.SyncNote);
        Assert.Equal(Srt([250, 4_250]), await File.ReadAllTextAsync(row.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Upgrade_keeps_subtitles_found_on_disk()
    {
        await File.WriteAllTextAsync(Path.ChangeExtension(_moviePath, null) + ".eng.srt", "existing", TestContext.Current.CancellationToken);
        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));

        await SearchAsync(upgrade: true);

        Assert.Empty(_provider.Downloads);
        Assert.Equal("disk", (await RowsAsync()).Single(r => r.Language == "en").Provider);
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
    public async Task Best_match_ignores_minimum_score_but_downloads_nothing()
    {
        _provider.Candidates.Add(C("en-low", "Completely.Different.Release.2004.720p.HDTV"));
        _provider.Candidates.Add(C("en-hi", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", hi: true));
        await using (var write = _services.CreateAsyncScope())
            await write.ServiceProvider.GetRequiredService<ISettingsService>().SaveAsync(new SubtitleSettings
            {
                Enabled = true, Languages = ["en", "es"], MinimumScoreMovies = 70, HearingImpaired = HearingImpairedMode.ExcludeHearingImpaired,
            }, TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
        var target = (await service.GetMovieTargetAsync(_movieFileId, TestContext.Current.CancellationToken))!;
        var best = await service.BestMatchesAsync(target, TestContext.Current.CancellationToken);

        Assert.Equal(["en", "es"], best.MissingLanguages);
        Assert.Equal(70, best.MinimumScore);
        var en = Assert.Single(best.Best); // hearing impaired stays excluded; no Spanish candidates
        Assert.Equal("en-low", en.Candidate.Payload);
        Assert.True(en.Score < 70);
        Assert.Empty(_provider.Downloads);
    }

    [Fact]
    public async Task Marks_one_file_not_found()
    {
        using var scope = _services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
        Assert.Equal(1, await service.MarkFileNotFoundAsync(_movieFileId, null, TestContext.Current.CancellationToken));
        Assert.Equal(0, await service.MarkFileNotFoundAsync(_movieFileId, null, TestContext.Current.CancellationToken)); // keeps the first mark

        var wanted = Assert.Single(await service.WantedAsync(10, TestContext.Current.CancellationToken, notFound: true));
        Assert.NotNull(wanted.NotFound);
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
        await service.SyncDiskAsync((await service.GetMovieTargetAsync(_movieFileId, TestContext.Current.CancellationToken))!, TestContext.Current.CancellationToken);
        Assert.Empty(await RowsAsync());

        var wanted = await service.WantedAsync(10, TestContext.Current.CancellationToken);
        var item = Assert.Single(wanted);
        Assert.Equal(["en", "es"], item.MissingLanguages);
        Assert.Equal("The Matrix (1999)", item.Target.Label);
    }

    /// <summary>A minimal Matroska file with one subtitle track per (language, forced) pair and some padding.</summary>
    private static byte[] Mkv(params (string Language, bool Forced)[] subtitles)
    {
        static byte[] El(uint id, byte[] body)
        {
            var idBytes = BitConverter.GetBytes(id).Reverse().SkipWhile(b => b == 0);
            var size = BitConverter.GetBytes((ulong)body.Length).Reverse().ToArray();
            size[0] = 0x01;
            return [.. idBytes, .. size, .. body];
        }

        var tracks = subtitles.SelectMany(s => El(0xAE,
        [
            .. El(0x83, [0x11]), .. El(0x22B59C, System.Text.Encoding.ASCII.GetBytes(s.Language)), .. El(0x55AA, [(byte)(s.Forced ? 1 : 0)]),
        ])).ToArray();
        byte[] segment = [.. El(0x1654AE6B, tracks), .. El(0x1F43B675, new byte[300_000])];
        return [.. El(0x1A45DFA3, []), .. El(0x18538067, segment)];
    }

    [Fact]
    public async Task Embedded_subtitles_count_as_present()
    {
        await File.WriteAllBytesAsync(_moviePath, Mkv(("eng", false), ("spa", true)), TestContext.Current.CancellationToken);
        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));
        _provider.Candidates.Add(C("es-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", lang: "es"));

        // Upgrade too: a language that is only embedded is not searched.
        await SearchAsync(upgrade: true);

        Assert.Equal(["es-best"], _provider.Downloads); // English is inside the video; forced Spanish does not count
        Assert.Equal(["es"], _provider.LastQuery!.Languages);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        Assert.Equal("en,es+forced", (await db.MovieFiles.SingleAsync(TestContext.Current.CancellationToken)).EmbeddedSubtitles);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SubtitleService>().WantedAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Embedded_subtitles_are_ignored_when_disabled()
    {
        await File.WriteAllBytesAsync(_moviePath, Mkv(("eng", false)), TestContext.Current.CancellationToken);
        using (var scope = _services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsService>()
                .SaveAsync(new SubtitleSettings { Enabled = true, Languages = ["en", "es"], UseEmbeddedSubtitles = false }, TestContext.Current.CancellationToken);
        }

        _provider.Candidates.Add(C("en-best", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"));

        await SearchAsync();

        Assert.Equal(["en-best"], _provider.Downloads);
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("es-419", "es")]
    [InlineData("zh-Hant", "zh-tw")]
    [InlineData("und", null)]
    [InlineData(null, null)]
    public void Normalizes_embedded_track_languages(string? tag, string? expected) =>
        Assert.Equal(expected, EmbeddedSubtitleList.NormalizeLanguage(tag));

    [Fact]
    public void Embedded_list_round_trips()
    {
        var list = EmbeddedSubtitleList.FromTracks(
        [
            new("eng", null, null, false, false), new("eng", null, null, false, false), new("spa", null, null, true, false),
            new("fre", null, null, false, true), new("und", null, null, false, false),
        ]);
        var stored = EmbeddedSubtitleList.Format(list);
        Assert.Equal("en,es+forced,fr+hi", stored);
        Assert.Equal(list, EmbeddedSubtitleList.Parse(stored));
        Assert.Equal(["en", "fr"], EmbeddedSubtitleList.FullLanguages(stored));
    }

    private async Task<DateTimeOffset?> NotFoundAsync()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().MovieFiles
            .Select(f => f.SubtitlesNotFound).SingleAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Marks_movie_files_where_no_provider_has_subtitles()
    {
        // A provider error may be transient: not marked.
        _provider.Fail = true;
        Assert.Contains("server error", await SearchAsync());
        Assert.Null(await NotFoundAsync());
        using (var scope = _services.CreateScope())
            Assert.Equal((1, 0), await scope.ServiceProvider.GetRequiredService<SubtitleService>().WantedCountsAsync(TestContext.Current.CancellationToken));

        // Every provider answered and none had a wanted language: marked, and the first mark is kept.
        _provider.Fail = false;
        Assert.Contains("nothing suitable found", await SearchAsync());
        var marked = await NotFoundAsync();
        Assert.NotNull(marked);
        await SearchAsync();
        Assert.Equal(marked, await NotFoundAsync());

        using (var scope = _services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
            var ct = TestContext.Current.CancellationToken;
            Assert.Single(await service.WantedAsync(10, ct, notFound: true));
            Assert.Empty(await service.WantedAsync(10, ct, notFound: false));
            Assert.Equal(marked, (await service.WantedAsync(10, ct)).Single().NotFound);
            Assert.Equal((0, 1), await service.WantedCountsAsync(ct));
        }

        // Found later (e.g. a manual search): the mark is cleared.
        _provider.Candidates.AddRange([C("en", "The.Matrix.1999.1080p.BluRay.x264-SPARKS"), C("es", "The.Matrix.1999.1080p.BluRay.x264-SPARKS", lang: "es")]);
        Assert.Contains("downloaded en, es", await SearchAsync());
        Assert.Null(await NotFoundAsync());
        using (var scope = _services.CreateScope())
            Assert.Equal((0, 0), await scope.ServiceProvider.GetRequiredService<SubtitleService>().WantedCountsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Retry_clears_the_not_found_mark_and_makes_the_file_due()
    {
        await SearchAsync();
        Assert.NotNull(await NotFoundAsync());

        using var scope = _services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
        Assert.Equal(1, await service.RetryNotFoundAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(0, await service.RetryNotFoundAsync(_movieFileId, TestContext.Current.CancellationToken));
        Assert.Null(await NotFoundAsync());
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        Assert.Null(await db.MovieFiles.Select(f => f.LastSubtitleSearch).SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Mark_all_not_found_covers_movie_and_episode_files_until_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        int episodeFileId;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var series = new Series { Title = "Show", Year = 2020, Path = Path.Combine(_root.FullName, "Show"), QualityProfileId = await db.QualityProfiles.MinAsync(p => p.Id, ct) };
            db.Series.Add(series);
            await db.SaveChangesAsync(ct);
            var episodeFile = new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = "Season 01/Show - S01E01.mkv", QualityId = 17 };
            db.EpisodeFiles.Add(episodeFile);
            await db.SaveChangesAsync(ct);
            db.Episodes.Add(new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 1, EpisodeFileId = episodeFile.Id });
            await db.SaveChangesAsync(ct);
            episodeFileId = episodeFile.Id;
        }

        using (var scope = _services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
            Assert.Equal((2, 0), await service.WantedCountsAsync(ct));
            Assert.Equal(2, await service.MarkAllNotFoundAsync(ct));
            Assert.Equal(0, await service.MarkAllNotFoundAsync(ct)); // already marked
            Assert.Equal((0, 2), await service.WantedCountsAsync(ct));
            Assert.Empty(await service.WantedAsync(10, ct, notFound: false));
            Assert.All(await service.WantedAsync(10, ct, notFound: true), w => Assert.NotNull(w.NotFound));

            // Retrying one episode only clears that file.
            Assert.Equal(1, await service.RetryNotFoundAsync(null, ct, episodeFileId));
            Assert.Equal((1, 1), await service.WantedCountsAsync(ct));
            Assert.NotNull(await NotFoundAsync());
        }
    }

    [Fact]
    public async Task Wanted_pages_are_sorted_on_the_server_and_counted_per_filter()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var series = new Series { Title = "Andor", SortTitle = "andor", Year = 2022, Path = Path.Combine(_root.FullName, "Andor"), QualityProfileId = await db.QualityProfiles.MinAsync(p => p.Id, ct) };
            db.Series.Add(series);
            await db.SaveChangesAsync(ct);
            for (var e = 2; e >= 1; e--)
            {
                var file = new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = $"Season 01/Andor - S01E0{e}.mkv", QualityId = 17 };
                db.EpisodeFiles.Add(file);
                await db.SaveChangesAsync(ct);
                db.Episodes.Add(new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = e, EpisodeFileId = file.Id });
            }
            // An episode file no episode points at has nothing to search for, so it is not listed or counted.
            db.EpisodeFiles.Add(new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = "Season 01/orphan.mkv", QualityId = 17 });
            await db.SaveChangesAsync(ct);
            await db.MovieFiles.ExecuteUpdateAsync(x => x.SetProperty(f => f.SubtitlesNotFound, DateTimeOffset.UtcNow), ct);
            await db.Movies.ExecuteUpdateAsync(x => x.SetProperty(m => m.SortTitle, "matrix"), ct);
        }

        using (var scope = _services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();

            var first = await service.WantedPageAsync(1, 2, ct);
            Assert.Equal((3, 2, 1), (first.Total, first.Searching, first.NotFound));
            Assert.Equal(["The Matrix (1999)", "Andor S01E02"], first.Items.Select(w => w.Target.Label)); // movies first, then by file
            Assert.Equal(["Andor S01E01"], (await service.WantedPageAsync(2, 2, ct)).Items.Select(w => w.Target.Label));

            var byTitle = await service.WantedPageAsync(1, 10, ct, sort: WantedSort.Title);
            Assert.Equal(["Andor S01E01", "Andor S01E02", "The Matrix (1999)"], byTitle.Items.Select(w => w.Target.Label));
            var byTypeDesc = await service.WantedPageAsync(1, 10, ct, sort: WantedSort.Type, desc: true);
            Assert.Equal(["Andor S01E02", "Andor S01E01", "The Matrix (1999)"], byTypeDesc.Items.Select(w => w.Target.Label));

            var searching = await service.WantedPageAsync(1, 10, ct, notFound: false);
            Assert.Equal(2, searching.Total);
            Assert.All(searching.Items, w => Assert.Null(w.NotFound));
            Assert.Equal(1, (await service.WantedPageAsync(1, 10, ct, notFound: true)).Total);
        }
    }

    [Fact]
    public async Task Lists_external_subtitles_that_duplicate_an_embedded_track()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            await db.MovieFiles.ExecuteUpdateAsync(x => x.SetProperty(f => f.EmbeddedSubtitles, "en+hi,es+forced"), ct);
            await db.Movies.ExecuteUpdateAsync(x => x.SetProperty(m => m.SortTitle, "matrix"), ct);
            var series = new Series { Title = "Andor", SortTitle = "andor", Year = 2022, Path = Path.Combine(_root.FullName, "Andor"), QualityProfileId = await db.QualityProfiles.MinAsync(p => p.Id, ct) };
            db.Series.Add(series);
            await db.SaveChangesAsync(ct);
            var episodeFile = new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = "Season 01/Andor - S01E03.mkv", QualityId = 17, EmbeddedSubtitles = "fr" };
            db.EpisodeFiles.Add(episodeFile);
            await db.SaveChangesAsync(ct);
            db.Episodes.Add(new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 3, EpisodeFileId = episodeFile.Id });
            db.SubtitleFiles.AddRange(
                new SubtitleFile { MovieFileId = _movieFileId, Language = "en", Path = "/m/matrix.en.srt", Provider = "fake" },              // embedded en (hi still counts)
                new SubtitleFile { MovieFileId = _movieFileId, Language = "es", Path = "/m/matrix.es.srt", Provider = "fake" },              // only a forced es track
                new SubtitleFile { MovieFileId = _movieFileId, Language = "es", Forced = true, Path = "/m/matrix.es.forced.srt", Provider = "disk" },
                new SubtitleFile { EpisodeFileId = episodeFile.Id, Language = "fr", Path = "/s/andor.fr.srt", Provider = "fake" },
                new SubtitleFile { EpisodeFileId = episodeFile.Id, Language = "de", Path = "/s/andor.de.srt", Provider = "fake" });
            await db.SaveChangesAsync(ct);
        }

        using (var scope = _services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
            var all = await service.RedundantPageAsync(0, 10, ct);
            Assert.Equal(3, all.Total);
            Assert.Equal(["Andor S01E03 fr", "The Matrix (1999) en", "The Matrix (1999) es"], all.Items.Select(r => $"{r.Label} {r.Language}"));
            Assert.True(all.Items[2].Forced);
            Assert.Equal(["en", "es"], all.Items[1].Embedded.Select(e => e.Language));

            var second = await service.RedundantPageAsync(2, 2, ct);
            Assert.Equal(3, second.Total);
            Assert.Equal(["/m/matrix.es.forced.srt"], second.Items.Select(r => r.Path));
        }
    }

    [Fact]
    public async Task Filters_search_and_backoff_to_selected_providers()
    {
        var ct = TestContext.Current.CancellationToken;
        var primary = new NamedFakeProvider("opensubtitles", "OpenSubtitles");
        var secondary = new NamedFakeProvider("subdl", "SubDL");
        primary.Candidates.Add(new SubtitleCandidate
        {
            Provider = "opensubtitles", Payload = "os-en", Language = "en",
            ReleaseNames = ["The.Matrix.1999.1080p.BluRay.x264-SPARKS"], MatchedById = true,
        });
        secondary.Candidates.Add(new SubtitleCandidate
        {
            Provider = "subdl", Payload = "subdl-en", Language = "en",
            ReleaseNames = ["The.Matrix.1999.1080p.BluRay.x264-SPARKS"], MatchedById = true,
        });

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var service = new SubtitleService(
            db, [primary, secondary], settings, new NullEventHub(), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SubtitleService>.Instance);
        var target = (await service.GetMovieTargetAsync(_movieFileId, ct))!;

        // Primary is backed off; searching only primary skips, while searching only secondary proceeds.
        primary.BackoffUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        var skipped = await service.SearchMissingAsync(target, ct, providers: ["opensubtitles"]);
        Assert.Contains("paused", skipped);
        Assert.Equal(0, primary.Searches);
        Assert.Equal(0, secondary.Searches);

        var summary = await service.SearchMissingAsync(target, ct, providers: ["SUBDL"]);
        Assert.Contains("downloaded en", summary);
        Assert.Equal(0, primary.Searches);
        Assert.Equal(1, secondary.Searches);
        Assert.Equal(["subdl-en"], secondary.Downloads);
        Assert.Equal("subdl", (await RowsAsync()).Single().Provider);

        // A search limited to some providers that finds nothing doesn't mark the file: the others may have it.
        Assert.Contains("nothing suitable found", await service.SearchMissingAsync(target, ct, providers: ["subdl"]));
        Assert.Null(await NotFoundAsync());
    }

    private sealed class NamedFakeProvider(string name, string displayName) : ISubtitleProvider
    {
        public List<SubtitleCandidate> Candidates { get; } = [];
        public List<string> Downloads { get; } = [];
        public int Searches { get; private set; }
        public DateTimeOffset? BackoffUntil { get; set; }
        public string Name => name;
        public string DisplayName => displayName;
        public bool IsConfigured(SubtitleSettings settings) => true;

        public Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleQuery query, SubtitleSettings settings, CancellationToken ct = default)
        {
            Searches++;
            return Task.FromResult<IReadOnlyList<SubtitleCandidate>>(Candidates.Where(c => query.Languages.Contains(c.Language)).ToList());
        }

        public Task<SubtitleContent> DownloadAsync(SubtitleCandidate candidate, SubtitleSettings settings, CancellationToken ct = default)
        {
            Downloads.Add(candidate.Payload);
            return Task.FromResult(new SubtitleContent(System.Text.Encoding.UTF8.GetBytes($"subtitle {candidate.Payload}"), ".srt"));
        }

        public Task<string> TestAsync(SubtitleSettings settings, CancellationToken ct = default) => Task.FromResult("ok");
    }
}

public sealed class SubtitleSearchTaskTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    private sealed class CountingProvider : ISubtitleProvider
    {
        public int Searches { get; private set; }
        public DateTimeOffset? BackoffUntil { get; set; }
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

    [Fact]
    public async Task Stops_retrying_movie_files_without_subtitles_until_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new SubtitleSettings { Enabled = true, Languages = ["en"], SearchIntervalHours = 1 };
        await RunAsync(settings);
        Assert.Equal(1, _provider.Searches);

        async Task MakeDueAsync()
        {
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().MovieFiles
                .ExecuteUpdateAsync(x => x.SetProperty(f => f.LastSubtitleSearch, DateTimeOffset.UtcNow.AddDays(-2)), ct);
        }

        await MakeDueAsync();
        await RunAsync(settings);
        Assert.Equal(1, _provider.Searches); // nothing found last time: not retried

        using (var scope = _services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SubtitleService>().RetryNotFoundAsync(null, ct);
        await RunAsync(settings);
        Assert.Equal(2, _provider.Searches);
    }

    [Fact]
    public async Task Does_not_query_providers_that_asked_to_back_off()
    {
        _provider.BackoffUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        await RunAsync(new SubtitleSettings { Enabled = true, Languages = ["en"] });
        Assert.Equal(0, _provider.Searches);

        using (var scope = _services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<SubtitleService>();
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var fileId = await db.MovieFiles.Select(f => f.Id).SingleAsync(TestContext.Current.CancellationToken);
            var result = await service.SearchMissingAsync((await service.GetMovieTargetAsync(fileId, TestContext.Current.CancellationToken))!, TestContext.Current.CancellationToken);
            Assert.Contains("paused", result);
            Assert.Null(await db.MovieFiles.Select(f => f.LastSubtitleSearch).SingleAsync(TestContext.Current.CancellationToken)); // still due
        }

        _provider.BackoffUntil = null;
        await RunAsync(new SubtitleSettings { Enabled = true, Languages = ["en"] });
        Assert.Equal(1, _provider.Searches);
    }
}
