using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.MediaServers;
using Krakarr.Data;
using Krakarr.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Series.Tests;

public sealed class SeriesOrganizerPlanTests
{
    private static readonly MediaManagementSettings Media = new();

    [Fact]
    public void Plans_only_files_that_differ_from_the_naming_format()
    {
        var series = TestData.Office();
        var good = new EpisodeFile { Id = 1, SeasonNumber = 1, QualityId = Qualities.WebDl1080.Id, RelativePath = Path.Combine("Season 01", "The Office - S01E01 - Episode 1 [WEBDL-1080p].mkv") };
        var bad = new EpisodeFile { Id = 2, SeasonNumber = 1, QualityId = Qualities.WebDl1080.Id, RelativePath = "the.office.s01e02.mkv" };
        var double_ = new EpisodeFile { Id = 3, SeasonNumber = 2, QualityId = Qualities.Bluray1080.Id, RelativePath = Path.Combine("S2", "office.s02e01e02.MKV") };
        series.Episodes[0].EpisodeFileId = 1;
        series.Episodes[1].EpisodeFileId = 2;
        series.Episodes[5].EpisodeFileId = 3;
        series.Episodes[6].EpisodeFileId = 3;

        var plan = SeriesOrganizer.Plan(series, [good, bad, double_], series.Episodes.Where(e => e.EpisodeFileId != null).ToLookup(e => e.EpisodeFileId!.Value), Media);

        Assert.Equal(2, plan.Count);
        Assert.Equal(Path.Combine("Season 01", "The Office - S01E02 - Episode 2 [WEBDL-1080p].mkv"), plan[0].New);
        Assert.Equal(Path.Combine("Season 02", "The Office - S02E01-E02 - Episode 1 + Episode 2 [Bluray-1080p].mkv"), plan[1].New);
        Assert.All(plan, p => Assert.Null(p.Problem));
    }

    [Fact]
    public void Keeps_file_names_when_renaming_is_off_and_flags_collisions()
    {
        var series = TestData.Office();
        var a = new EpisodeFile { Id = 1, SeasonNumber = 1, RelativePath = "a.mkv" };
        var b = new EpisodeFile { Id = 2, SeasonNumber = 1, RelativePath = Path.Combine("Season 01", "a.mkv") };
        var orphan = new EpisodeFile { Id = 3, SeasonNumber = 1, RelativePath = "orphan.mkv" };
        series.Episodes[0].EpisodeFileId = 1;
        series.Episodes[1].EpisodeFileId = 2;

        var plan = SeriesOrganizer.Plan(series, [a, b, orphan], series.Episodes.Where(e => e.EpisodeFileId != null).ToLookup(e => e.EpisodeFileId!.Value), Media with { RenameFiles = false });

        // b already sits at "Season 01/a.mkv", so a cannot move there.
        Assert.Equal("another file gets the same name", Assert.Single(plan, p => p.EpisodeFileId == 1).Problem);
        Assert.Equal("no episodes linked to this file", Assert.Single(plan, p => p.EpisodeFileId == 3).Problem);
        Assert.DoesNotContain(plan, p => p.EpisodeFileId == 2);
    }
}

/// <summary>Organizes real files on disk against an in-memory database.</summary>
public sealed class SeriesOrganizerTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("krakarr-organize");
    private ServiceProvider _services = null!;
    private readonly RecordingNotifier _mediaServers = new();
    private string TvRoot => Path.Combine(_root.FullName, "tv");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        services.AddKrakarrMedia();
        services.AddSingleton<IEventHub, NullEventHub>();
        services.AddSingleton<IMediaServerNotifier>(_mediaServers);
        services.AddScoped<SeriesOrganizer>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        Directory.CreateDirectory(TvRoot);
        db.RootFolders.Add(new RootFolder { Path = TvRoot, MediaType = MediaType.Series });
        var profile = new QualityProfile { Name = "HD", AllowedQualityIds = [Qualities.WebDl1080.Id], CutoffQualityId = Qualities.WebDl1080.Id };
        var series = new Core.Domain.Series { TmdbId = 1, Title = "Test Show", Year = 2021, Path = Path.Combine(TvRoot, "test show"), QualityProfile = profile };
        for (var e = 1; e <= 2; e++)
            series.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = e, Title = $"Chapter {e}" });
        series.Seasons.Add(new Season { SeasonNumber = 1 });
        db.Series.Add(series);
        await db.SaveChangesAsync();

        foreach (var episode in series.Episodes)
        {
            var relative = Path.Combine("misc", $"test.show.s01e0{episode.EpisodeNumber}.mkv");
            Write(Path.Combine(series.Path, relative));
            var file = new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = relative, QualityId = Qualities.WebDl1080.Id, ReleaseGroup = "KRK" };
            db.EpisodeFiles.Add(file);
            await db.SaveChangesAsync();
            episode.EpisodeFileId = file.Id;
        }

        var subtitle = Path.Combine(series.Path, "misc", "test.show.s01e01.en.srt");
        Write(subtitle);
        db.SubtitleFiles.Add(new SubtitleFile { EpisodeFileId = series.Episodes[0].EpisodeFileId, Language = "en", Path = subtitle, Provider = "disk" });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        _root.Delete(true);
    }

    private static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[16]);
    }

    [Fact]
    public async Task Renames_files_subtitles_and_series_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _services.CreateScope())
        {
            var preview = await scope.ServiceProvider.GetRequiredService<SeriesOrganizer>().PreviewAsync(1, ct);
            Assert.NotNull(preview);
            Assert.Equal(Path.Combine(TvRoot, "Test Show (2021)"), preview.NewPath);
            Assert.Equal(2, preview.Files.Count);

            var result = await scope.ServiceProvider.GetRequiredService<SeriesOrganizer>().OrganizeAsync(1, renameFolder: true, ct);
            Assert.Contains("renamed 2 files", result);
            Assert.DoesNotContain("skipped", result);
        }

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var series = await db.Series.SingleAsync(ct);
        Assert.Equal(Path.Combine(TvRoot, "Test Show (2021)"), series.Path);
        Assert.False(Directory.Exists(Path.Combine(TvRoot, "test show")));
        Assert.False(Directory.Exists(Path.Combine(series.Path, "misc")), "emptied folders are removed");
        // Media servers rescan the old and the new folder.
        Assert.Equal([Path.Combine(TvRoot, "test show"), series.Path], _mediaServers.Paths.Distinct());

        var files = await db.EpisodeFiles.OrderBy(f => f.Id).ToListAsync(ct);
        Assert.Equal(Path.Combine("Season 01", "Test Show - S01E01 - Chapter 1 [WEBDL-1080p].mkv"), files[0].RelativePath);
        Assert.All(files, f => Assert.True(File.Exists(Path.Combine(series.Path, f.RelativePath))));

        var subtitle = await db.SubtitleFiles.SingleAsync(ct);
        Assert.Equal(Path.Combine(series.Path, "Season 01", "Test Show - S01E01 - Chapter 1 [WEBDL-1080p].en.srt"), subtitle.Path);
        Assert.True(File.Exists(subtitle.Path));

        using var again = _services.CreateScope();
        var second = await again.ServiceProvider.GetRequiredService<SeriesOrganizer>().PreviewAsync(1, ct);
        Assert.Null(second!.NewPath);
        Assert.Empty(second.Files);
    }

    [Fact]
    public async Task Keeps_series_folder_when_not_asked_to_rename_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SeriesOrganizer>().OrganizeAsync(1, renameFolder: false, ct);
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var series = await db.Series.AsNoTracking().SingleAsync(ct);
        Assert.Equal(Path.Combine(TvRoot, "test show"), series.Path);
        Assert.True(File.Exists(Path.Combine(series.Path, "Season 01", "Test Show - S01E02 - Chapter 2 [WEBDL-1080p].mkv")));
    }
}

internal sealed class RecordingNotifier : IMediaServerNotifier
{
    public List<string> Paths { get; } = [];

    public void PathChanged(string path)
    {
        lock (Paths) Paths.Add(path);
    }
}
