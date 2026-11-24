using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;

namespace Krakarr.Downloads.Tests;

public sealed class SeedingPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static Download Row(int? movieId = 1, bool imported = true, int completedMinutesAgo = 10) => new()
    {
        InfoHash = "abc", Name = "x", SavePath = "/x", MovieId = movieId, Imported = imported,
        CompletedAt = completedMinutesAgo < 0 ? null : Now.AddMinutes(-completedMinutesAgo),
    };

    [Theory]
    [InlineData(1.0, 0, 1.0, true)]
    [InlineData(1.0, 0, 0.99, false)]
    [InlineData(0, 0, 100, false)]
    [InlineData(0, 10, 0, true)]
    [InlineData(0, 11, 0, false)]
    [InlineData(5, 10, 0, true)]
    public void Ratio_or_time_limit(double ratioLimit, int minutesLimit, double ratio, bool expected)
    {
        var settings = new DownloadClientSettings { SeedRatioLimit = ratioLimit, SeedTimeLimitMinutes = minutesLimit };
        Assert.Equal(expected, SeedingPolicy.IsDone(settings, Row(), ratio, Now));
    }

    [Fact]
    public void Waits_for_completion_and_import()
    {
        var settings = new DownloadClientSettings { SeedRatioLimit = 1 };
        Assert.False(SeedingPolicy.IsDone(settings, Row(completedMinutesAgo: -1), 5, Now));
        Assert.False(SeedingPolicy.IsDone(settings, Row(imported: false), 5, Now));
        Assert.True(SeedingPolicy.IsDone(settings, Row(movieId: null, imported: false), 5, Now));
    }

    [Fact]
    public void Only_imported_media_downloads_lose_their_data()
    {
        Assert.True(SeedingPolicy.DeleteDataOnRemove(Row()));
        Assert.False(SeedingPolicy.DeleteDataOnRemove(Row(imported: false)));
        Assert.False(SeedingPolicy.DeleteDataOnRemove(Row(movieId: null, imported: false)));
    }

    [Fact]
    public void Only_finished_media_downloads_with_a_failed_import_count_as_failed()
    {
        static Download Failed(Download row)
        {
            row.ImportMessage = "Import failed: no video file found in download";
            return row;
        }

        Assert.True(SeedingPolicy.ImportFailed(Failed(Row(imported: false))));
        Assert.False(SeedingPolicy.ImportFailed(Row(imported: false))); // import pending
        Assert.False(SeedingPolicy.ImportFailed(Failed(Row()))); // partially imported season pack
        Assert.False(SeedingPolicy.ImportFailed(Failed(Row(movieId: null, imported: false)))); // dismissed: plain torrent
        Assert.False(SeedingPolicy.ImportFailed(Failed(Row(imported: false, completedMinutesAgo: -1))));
    }
}
