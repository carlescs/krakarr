using Krakarr.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Series;

public static class SeriesModule
{
    public static IServiceCollection AddKrakarrSeries(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(TvdbClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri((configuration["Tvdb:BaseUrl"] ?? "https://api4.thetvdb.com/v4/").TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.AddScoped<ITvdbClient, TvdbClient>();
        return services;
    }

    public static IEndpointRouteBuilder MapSeriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/series").WithTags("Series");

        group.MapGet("/", async (KrakarrDbContext db, CancellationToken ct) =>
            await db.Series.AsNoTracking().OrderBy(s => s.Title).ToListAsync(ct));

        group.MapGet("/lookup", async (string term, ITvdbClient tvdb, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await tvdb.SearchAsync(term, ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        return app;
    }
}
