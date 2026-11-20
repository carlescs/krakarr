using System.Text.Json;
using Krakarr.Core.Domain;

namespace Krakarr.Movies.Tests;

public class MovieReleasesTests
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    private static Movie NewMovie(MinimumAvailability availability = MinimumAvailability.Released) =>
        new() { Title = "Test", Path = "/m/Test", MinimumAvailability = availability };

    private static MovieReleaseDate D(string country, ReleaseDateType type, string date) => new(country, type, DateOnly.Parse(date));

    [Fact]
    public void Maps_tmdb_release_types_and_keeps_earliest_per_country()
    {
        const string json = """
            {
              "id": 1, "title": "X",
              "release_dates": { "results": [
                { "iso_3166_1": "US", "release_dates": [
                  { "release_date": "2026-03-01T00:00:00.000Z", "type": 1 },
                  { "release_date": "2026-03-07T00:00:00.000Z", "type": 3 },
                  { "release_date": "2026-03-05T00:00:00.000Z", "type": 2 },
                  { "release_date": "2026-04-20T00:00:00.000Z", "type": 4 },
                  { "release_date": "2026-05-30T00:00:00.000Z", "type": 5 },
                  { "release_date": "2026-09-01T00:00:00.000Z", "type": 6 }
                ] },
                { "iso_3166_1": "es", "release_dates": [ { "release_date": "2026-03-12T00:00:00.000Z", "type": 3 } ] },
                { "iso_3166_1": "FR", "release_dates": [ { "release_date": "garbage", "type": 3 } ] }
              ] }
            }
            """;
        var movie = JsonSerializer.Deserialize<TmdbClient.TmdbMovie>(json)!;

        var dates = TmdbClient.MapReleaseDates(movie.ReleaseDates);

        Assert.Equal(
            [
                D("US", ReleaseDateType.Theatrical, "2026-03-05"),
                D("ES", ReleaseDateType.Theatrical, "2026-03-12"),
                D("US", ReleaseDateType.Digital, "2026-04-20"),
                D("US", ReleaseDateType.Physical, "2026-05-30"),
            ],
            dates);
    }

    [Fact]
    public void Resolves_region_date_with_earliest_fallback()
    {
        List<MovieReleaseDate> dates =
        [
            D("US", ReleaseDateType.Theatrical, "2026-03-07"),
            D("ES", ReleaseDateType.Theatrical, "2026-03-12"),
            D("US", ReleaseDateType.Digital, "2026-04-20"),
        ];

        Assert.Equal(D("ES", ReleaseDateType.Theatrical, "2026-03-12"), MovieReleases.Resolve(dates, ReleaseDateType.Theatrical, "es"));
        Assert.Equal(D("US", ReleaseDateType.Digital, "2026-04-20"), MovieReleases.Resolve(dates, ReleaseDateType.Digital, "ES"));
        Assert.Equal(D("US", ReleaseDateType.Theatrical, "2026-03-07"), MovieReleases.Resolve(dates, ReleaseDateType.Theatrical, null));
        Assert.Null(MovieReleases.ForRegion(dates, ReleaseDateType.Digital, "ES"));
        Assert.Null(MovieReleases.Resolve(dates, ReleaseDateType.Physical, "US"));
    }

    [Fact]
    public void Apply_sets_earliest_columns_and_status()
    {
        var movie = NewMovie();
        MovieReleases.Apply(movie, [D("US", ReleaseDateType.Theatrical, "2026-05-01"), D("GB", ReleaseDateType.Theatrical, "2026-04-25"), D("US", ReleaseDateType.Digital, "2026-07-01")],
            MovieStatus.Released, Today);

        Assert.Equal(new DateOnly(2026, 4, 25), movie.InCinemas);
        Assert.Equal(new DateOnly(2026, 7, 1), movie.DigitalRelease);
        Assert.Null(movie.PhysicalRelease);
        Assert.Equal(MovieStatus.InCinemas, movie.Status);
    }

    [Theory]
    [InlineData(null, null, null, MovieStatus.Announced)]          // no dates: metadata status
    [InlineData("2026-07-01", null, null, MovieStatus.Announced)]
    [InlineData("2026-05-01", null, null, MovieStatus.InCinemas)]
    [InlineData("2026-05-01", "2026-08-01", null, MovieStatus.InCinemas)]
    [InlineData("2026-01-01", null, null, MovieStatus.Released)]   // 90+ days after cinema, no home release known
    [InlineData("2026-01-01", "2026-08-01", null, MovieStatus.InCinemas)]
    [InlineData(null, "2026-05-31", null, MovieStatus.Released)]
    [InlineData("2026-05-01", null, "2026-06-01", MovieStatus.Released)]
    public void Computes_current_status(string? cinema, string? digital, string? physical, MovieStatus expected)
    {
        var movie = NewMovie();
        movie.Status = MovieStatus.Announced;
        movie.InCinemas = cinema is null ? null : DateOnly.Parse(cinema);
        movie.DigitalRelease = digital is null ? null : DateOnly.Parse(digital);
        movie.PhysicalRelease = physical is null ? null : DateOnly.Parse(physical);

        Assert.Equal(expected, MovieReleases.CurrentStatus(movie, Today));
    }

    [Theory]
    [InlineData(MinimumAvailability.Announced, true)]
    [InlineData(MinimumAvailability.InCinemas, true)]
    [InlineData(MinimumAvailability.Released, false)]
    public void Availability_follows_minimum(MinimumAvailability minimum, bool expected)
    {
        var movie = NewMovie(minimum);
        movie.InCinemas = new DateOnly(2026, 5, 1);
        movie.DigitalRelease = new DateOnly(2026, 7, 1);

        Assert.Equal(expected, MovieReleases.IsAvailable(movie, Today));
    }

    [Fact]
    public void Movies_without_dates_fall_back_to_metadata_status()
    {
        var movie = NewMovie();
        movie.Status = MovieStatus.Released;
        Assert.True(MovieReleases.IsAvailable(movie, Today));

        movie.Status = MovieStatus.Announced;
        Assert.False(MovieReleases.IsAvailable(movie, Today));
        movie.MinimumAvailability = MinimumAvailability.Announced;
        Assert.True(MovieReleases.IsAvailable(movie, Today));
    }
}
