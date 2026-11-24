using Krakarr.Core.Configuration;
using Krakarr.Core.Metadata;
using Krakarr.Data;

namespace Krakarr.Host.Endpoints;

/// <summary>TMDB keyword search; the titles tagged with keywords come from <c>movies|series/discover/keywords</c>.</summary>
public static class KeywordEndpoints
{
    public static IEndpointRouteBuilder MapKeywordEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/keywords").WithTags("Discover");

        group.MapGet("/lookup", (string term, IKeywordMetadata keywords, ISettingsService settings, CancellationToken ct) =>
            Run(async () => await keywords.SearchAsync(await settings.GetAsync<MetadataSettings>(ct), term, ct)));

        group.MapGet("/{id:int}", (int id, IKeywordMetadata keywords, ISettingsService settings, CancellationToken ct) =>
            Run(async () => await keywords.GetAsync(await settings.GetAsync<MetadataSettings>(ct), id, ct)));

        return app;
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (MetadataNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (MetadataException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
