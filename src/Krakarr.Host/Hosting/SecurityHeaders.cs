namespace Krakarr.Host.Hosting;

/// <summary>
/// Adds browser hardening headers to every response (SPA, static files and API).
/// <list type="bullet">
/// <item><c>Krakarr:ContentSecurityPolicy</c> replaces the default policy (e.g. to allow another image host), and
/// <c>off</c> drops the header.</item>
/// <item><c>Krakarr:Hsts=true</c> sends <c>Strict-Transport-Security</c> on HTTPS requests. It is opt-in because
/// Krakarr usually sits behind a reverse proxy, which should own HSTS, and a wrong HSTS header locks browsers out of
/// plain HTTP for a year.</item>
/// </list>
/// </summary>
public static class SecurityHeaders
{
    public const string CspSettingName = "Krakarr:ContentSecurityPolicy";
    public const string HstsSettingName = "Krakarr:Hsts";

    /// <summary>
    /// Scripts come only from the app's own files (the Angular build inlines none; see <c>inlineCritical</c> in
    /// angular.json). Styles also allow <c>'unsafe-inline'</c> because Angular injects component styles (each
    /// component's .scss) as &lt;style&gt; elements at runtime. Posters and portraits load from TMDB and TheTVDB.
    /// </summary>
    public const string DefaultContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https://image.tmdb.org https://artworks.thetvdb.com; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "manifest-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>Turns off browser features Krakarr doesn't use. Passkeys (WebAuthn) are allowed for Krakarr itself only.</summary>
    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), geolocation=(), gyroscope=(), magnetometer=(), " +
        "microphone=(), midi=(), payment=(), publickey-credentials-create=(self), publickey-credentials-get=(self), usb=(), " +
        "xr-spatial-tracking=()";

    public const string StrictTransportSecurity = "max-age=31536000";

    private sealed record Policy(string? Csp, bool Hsts);

    /// <summary>Must run after <c>UseForwardedHeaders</c> so HTTPS behind a trusted proxy is recognised for HSTS.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        var configured = configuration[CspSettingName];
        var policy = new Policy(
            string.IsNullOrWhiteSpace(configured) ? DefaultContentSecurityPolicy
                : configured.Trim().Equals("off", StringComparison.OrdinalIgnoreCase) ? null
                : configured.Trim(),
            configuration.GetValue<bool>(HstsSettingName));

        return app.Use((context, next) =>
        {
            // OnStarting, so the headers survive the exception handler clearing the response.
            context.Response.OnStarting(static state =>
            {
                var (ctx, p) = ((HttpContext, Policy))state;
                var headers = ctx.Response.Headers;
                if (p.Csp is not null) headers.ContentSecurityPolicy = p.Csp;
                headers.XFrameOptions = "DENY";
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = PermissionsPolicy;
                if (p.Hsts && ctx.Request.IsHttps) headers.StrictTransportSecurity = StrictTransportSecurity;
                return Task.CompletedTask;
            }, (context, policy));
            return next(context);
        });
    }
}
