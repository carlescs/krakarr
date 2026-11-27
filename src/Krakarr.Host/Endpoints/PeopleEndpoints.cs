using Krakarr.Core.Configuration;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

public static class PeopleEndpoints
{
    public static IEndpointRouteBuilder MapPeopleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/people").WithTags("People");

        group.MapGet("/lookup", async (string term, IPersonMetadata people, ISettingsService settings, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await people.SearchAsync(await settings.GetAsync<MetadataSettings>(ct), term, ct));
            }
            catch (MetadataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // A person's details and filmography, with titles already in the library linked to them.
        group.MapGet("/{tmdbId:int}", async (int tmdbId, IPersonMetadata people, ISettingsService settings, KrakarrDbContext db, CancellationToken ct) =>
        {
            PersonDetails person;
            try
            {
                person = await people.GetPersonAsync(await settings.GetAsync<MetadataSettings>(ct), tmdbId, ct);
            }
            catch (MetadataException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            var movieIds = person.Credits.Where(c => c.Kind == "movie").Select(c => c.TmdbId).Distinct().ToList();
            var showIds = person.Credits.Where(c => c.Kind == "series").Select(c => (int?)c.TmdbId).Distinct().ToList();
            var movies = await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.TmdbId))
                .Select(m => new { m.TmdbId, m.Id }).ToDictionaryAsync(m => m.TmdbId, m => m.Id, ct);
            var shows = await db.Series.AsNoTracking().Where(s => showIds.Contains(s.TmdbId))
                .Select(s => new { TmdbId = s.TmdbId!.Value, s.Id }).ToDictionaryAsync(s => s.TmdbId, s => s.Id, ct);

            return Results.Ok(person with
            {
                Credits = person.Credits.Select(c => c with
                {
                    MovieId = c.Kind == "movie" && movies.TryGetValue(c.TmdbId, out var movieId) ? movieId : null,
                    SeriesId = c.Kind == "series" && shows.TryGetValue(c.TmdbId, out var seriesId) ? seriesId : null,
                }).ToList(),
            });
        });

        return app;
    }
}
