using Krakarr.Core.Domain;

namespace Krakarr.Series.Tests;

internal static class TestData
{
    public static readonly DateOnly Today = new(2026, 11, 20);

    public static readonly QualityProfile Hd1080 = new()
    {
        Id = 1,
        Name = "HD-1080p",
        AllowedQualityIds = [Qualities.WebDl1080.Id, Qualities.WebRip1080.Id, Qualities.Bluray1080.Id, Qualities.Hdtv1080.Id],
        CutoffQualityId = Qualities.Bluray1080.Id,
    };

    /// <summary>"The Office" with S01E01-E04 aired, S01E05 in the future, S02E01-E02 aired and one special.</summary>
    public static Core.Domain.Series Office()
    {
        var series = new Core.Domain.Series
        {
            Id = 1, TvdbId = 73244, Title = "The Office", Year = 2005, Path = "/media/tv/The Office", QualityProfileId = 1, QualityProfile = Hd1080,
        };
        var id = 1;
        void Add(int s, int e, DateOnly? aired) =>
            series.Episodes.Add(new Episode { Id = id++, SeriesId = 1, SeasonNumber = s, EpisodeNumber = e, AirDate = aired, Title = $"Episode {e}", Monitored = s > 0 });
        for (var e = 1; e <= 4; e++) Add(1, e, Today.AddDays(-100 + e * 7));
        Add(1, 5, Today.AddDays(5));
        Add(2, 1, Today.AddDays(-30));
        Add(2, 2, Today.AddDays(-23));
        Add(0, 1, Today.AddDays(-200));
        series.Seasons.AddRange([new Season { SeasonNumber = 0, Monitored = false }, new Season { SeasonNumber = 1 }, new Season { SeasonNumber = 2 }]);
        return series;
    }

    public static ReleaseInfo Release(string title, int? seeders = 10, long sizeMb = 1500) => new()
    {
        Title = title, Guid = title, DownloadUrl = "http://x/" + title, Size = sizeMb * 1024 * 1024, Seeders = seeders,
    };
}
