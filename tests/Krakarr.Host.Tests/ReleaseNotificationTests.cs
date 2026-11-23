using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Notifications;
using Krakarr.Data;
using Krakarr.Host.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

public sealed class ReleaseNotificationTests : IClassFixture<KrakarrFactory>
{
    private static readonly DateOnly Today = new(2031, 5, 10);
    /// <summary>Follows every seeded title except "Not Followed"; set by <see cref="SeedAsync"/>.</summary>
    private static int _followerId;
    /// <summary>Follows nothing.</summary>
    private static int _otherId;

    private readonly KrakarrFactory _factory;

    public ReleaseNotificationTests(KrakarrFactory factory) => _factory = factory;

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static MovieReleaseDate D(string country, ReleaseDateType type, DateOnly date) => new(country, type, date);

    private async Task SeedAsync(KrakarrDbContext db)
    {
        var ct = TestContext.Current.CancellationToken;
        if (await db.Movies.AnyAsync(ct)) return;
        var profile = await db.QualityProfiles.Select(p => p.Id).FirstAsync(ct);
        var tmdb = 500;

        Movie AddMovie(string title, bool monitored, params MovieReleaseDate[] dates)
        {
            var movie = new Movie { TmdbId = tmdb++, Title = title, Year = 2031, Path = "/m/" + title, QualityProfileId = profile, Monitored = monitored };
            MovieReleases.Apply(movie, dates, MovieStatus.Announced, Today);
            db.Movies.Add(movie);
            return movie;
        }

        AddMovie("Out Today", true, D("US", ReleaseDateType.Digital, Today));
        AddMovie("Out Yesterday", true, D("US", ReleaseDateType.Physical, Today.AddDays(-1)), D("US", ReleaseDateType.Digital, Today.AddDays(5)));
        AddMovie("Out Last Week", true, D("US", ReleaseDateType.Digital, Today.AddDays(-7)));
        // A later physical release doesn't count as a new release.
        AddMovie("Physical Today", true, D("US", ReleaseDateType.Digital, Today.AddDays(-30)), D("US", ReleaseDateType.Physical, Today));
        AddMovie("Only In Cinemas", true, D("US", ReleaseDateType.Theatrical, Today));
        AddMovie("Unmonitored", false, D("US", ReleaseDateType.Digital, Today));
        AddMovie("Spain Today", true, D("US", ReleaseDateType.Digital, Today.AddDays(-60)), D("ES", ReleaseDateType.Digital, Today));
        var notFollowed = AddMovie("Not Followed", true, D("US", ReleaseDateType.Digital, Today));

        var show = new Krakarr.Core.Domain.Series { TmdbId = 900, Title = "Weekly Show", Year = 2031, Path = "/tv/weekly", QualityProfileId = profile };
        show.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, AirDate = Today.AddDays(-7) });
        show.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 2, AirDate = Today.AddDays(-1) });
        show.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 3, AirDate = Today });
        show.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 4, AirDate = Today, Monitored = false });
        show.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 5, AirDate = Today.AddDays(1) });
        var other = new Krakarr.Core.Domain.Series { TmdbId = 901, Title = "Other Show", Year = 2031, Path = "/tv/other", QualityProfileId = profile };
        other.Episodes.Add(new Episode { SeasonNumber = 2, EpisodeNumber = 1, AirDate = Today.AddDays(-1) });
        var unmonitored = new Krakarr.Core.Domain.Series { TmdbId = 902, Title = "Dropped Show", Year = 2031, Path = "/tv/dropped", QualityProfileId = profile, Monitored = false };
        unmonitored.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, AirDate = Today });
        var notFollowedShow = new Krakarr.Core.Domain.Series { TmdbId = 903, Title = "Not Followed Show", Year = 2031, Path = "/tv/nf", QualityProfileId = profile };
        notFollowedShow.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, AirDate = Today });
        db.Series.AddRange(show, other, unmonitored, notFollowedShow);

        var follower = new User { Username = "release-follower", PasswordHash = "x" };
        var bystander = new User { Username = "release-bystander", PasswordHash = "x" };
        db.Users.AddRange(follower, bystander);
        await db.SaveChangesAsync(ct);
        (_followerId, _otherId) = (follower.Id, bystander.Id);

        foreach (var id in await db.Movies.Where(m => m.Id != notFollowed.Id).Select(m => m.Id).ToListAsync(ct))
            db.MediaSubscriptions.Add(new MediaSubscription { UserId = follower.Id, MovieId = id });
        foreach (var id in await db.Series.Where(x => x.Id != notFollowedShow.Id).Select(x => x.Id).ToListAsync(ct))
            db.MediaSubscriptions.Add(new MediaSubscription { UserId = follower.Id, SeriesId = id });
        await db.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<Notification>> GetAsync(string? region, DateOnly today, bool follower = true)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await SeedAsync(db);
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var metadata = await settings.GetAsync<MetadataSettings>(ct);
        await settings.SaveAsync(metadata with { ReleaseRegion = region }, ct);
        var source = new ReleaseNotificationSource(db, settings, new FixedTime(new DateTimeOffset(today.ToDateTime(new TimeOnly(20, 0)), TimeSpan.Zero)));
        return await source.GetAsync(new NotificationContext(follower ? _followerId : _otherId, UserRole.User), ct);
    }

    [Fact]
    public async Task Reports_movies_released_and_episodes_aired_today_or_yesterday()
    {
        var notes = await GetAsync(null, Today);

        var movies = notes.Single(n => n.Id == "releases.movies");
        Assert.Equal(2, movies.Count);
        Assert.Equal("2 movies released", movies.Title);
        Assert.Equal("Out Today and Out Yesterday", movies.Message);
        Assert.Equal("/calendar", movies.Link);
        Assert.Equal(new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), movies.Date);

        var episodes = notes.Single(n => n.Id == "releases.episodes");
        Assert.Equal(3, episodes.Count);
        Assert.Equal("3 new episodes aired", episodes.Title);
        Assert.Equal("Weekly Show (2) and Other Show", episodes.Message);
    }

    [Fact]
    public async Task Uses_the_release_region_and_goes_quiet_after_the_window()
    {
        var regional = await GetAsync("ES", Today);
        // Spain's date wins; countries without their own date fall back to the earliest worldwide one.
        Assert.Equal("Out Today, Spain Today and Out Yesterday", regional.Single(n => n.Id == "releases.movies").Message);

        var later = await GetAsync(null, Today.AddDays(30));
        Assert.Empty(later);
        await GetAsync(null, Today); // leave the default region for the other test
    }

    [Fact]
    public async Task Only_reports_titles_the_user_follows()
    {
        Assert.Empty(await GetAsync(null, Today, follower: false));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var source = new ReleaseNotificationSource(db, settings, new FixedTime(new DateTimeOffset(Today.ToDateTime(new TimeOnly(20, 0)), TimeSpan.Zero)));
        Assert.Empty(await source.GetAsync(new NotificationContext(null, UserRole.Admin), TestContext.Current.CancellationToken)); // the API key
    }

    [Fact]
    public void Summarizes_long_lists()
    {
        Assert.Equal("A", ReleaseNotificationSource.Summarize(["A"], 1));
        Assert.Equal("A, B and C", ReleaseNotificationSource.Summarize(["A", "B", "C"], 3));
        Assert.Equal("A, B, C and 2 more", ReleaseNotificationSource.Summarize(["A", "B", "C", "D", "E"], 5));
    }

    [Fact]
    public void Stays_dismissed_until_date_or_count_increases()
    {
        var day = new DateTimeOffset(2031, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var dismissal = new NotificationDismissal { NotificationId = "releases.movies", Count = 2, DismissedAt = day };
        Notification Note(int count, DateTimeOffset? date) => new("releases.movies", "movie", "t", null, "/calendar", count, Date: date);

        Assert.True(NotificationEndpoints.IsDismissed(Note(2, day), dismissal));
        Assert.True(NotificationEndpoints.IsDismissed(Note(1, day), dismissal)); // yesterday's title aged out
        Assert.True(NotificationEndpoints.IsDismissed(Note(3, day.AddDays(-1)), dismissal));
        Assert.False(NotificationEndpoints.IsDismissed(Note(3, day), dismissal)); // another title released today
        Assert.False(NotificationEndpoints.IsDismissed(Note(1, day.AddDays(1)), dismissal)); // a title released tomorrow
    }
}
