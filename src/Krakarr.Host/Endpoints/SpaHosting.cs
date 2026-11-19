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
                    var webRoot = System.Net.WebUtility.HtmlEncode(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"));
                    return Results.Text(
                        $"""
                        <html><body style="font-family:sans-serif;max-width:46rem;margin:3rem auto;line-height:1.5">
                        <h1>Krakarr</h1>
                        <p>The web UI has not been built (no <code>index.html</code> in <code>{webRoot}</code>).</p>
                        <ul>
                          <li>Build it with <code>dotnet build src/Krakarr.Host</code> (requires Node.js 22+), or run <code>npm ci</code> and <code>npm run build</code> in the <code>web</code> folder.</li>
                          <li>Then <strong>restart Krakarr</strong>; the web root is only detected at startup.</li>
                          <li>For frontend development, run <code>npm start</code> in the <code>web</code> folder and open <a href="http://localhost:4200">http://localhost:4200</a>.</li>
                        </ul>
                        <p>The API is running: <a href="api/v1/health">api/v1/health</a></p>
                        </body></html>
                        """,
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
