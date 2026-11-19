using Microsoft.Extensions.FileProviders;

namespace Krakarr.Host.Endpoints;

/// <summary>Serves the Angular app from wwwroot, rewriting &lt;base href&gt; for the configured URL base.</summary>
public static class SpaHosting
{
    /// <summary>Static assets must be served before authorization (the fallback policy would otherwise apply).</summary>
    public static WebApplication UseKrakarrStaticFiles(this WebApplication app)
    {
        app.UseStaticFiles();
        return app;
    }

    public static WebApplication MapKrakarrSpa(this WebApplication app, string urlBase)
    {
        string? cachedIndex = null;
        app.MapFallback(async (HttpContext http, IWebHostEnvironment env) =>
        {
            var path = http.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            if (cachedIndex is null)
            {
                IFileInfo file = env.WebRootFileProvider.GetFileInfo("index.html");
                if (!file.Exists)
                {
                    return Results.Text(
                        "<html><body style=\"font-family:sans-serif\"><h1>Krakarr</h1><p>The web UI has not been built. Run <code>npm run build</code> in <code>web/</code>, or use <code>ng serve</code> during development.</p></body></html>",
                        "text/html");
                }

                using var reader = new StreamReader(file.CreateReadStream());
                var html = await reader.ReadToEndAsync();
                cachedIndex = html.Replace("<base href=\"/\">", $"<base href=\"{urlBase}/\">", StringComparison.Ordinal);
            }

            http.Response.Headers.CacheControl = "no-cache";
            return Results.Content(cachedIndex, "text/html");
        }).AllowAnonymous();

        return app;
    }
}
