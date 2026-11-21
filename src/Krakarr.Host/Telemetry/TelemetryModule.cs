namespace Krakarr.Host.Telemetry;

public static class TelemetryModule
{
    /// <summary>Optional OpenTelemetry export, configured in Settings → Telemetry (off by default).</summary>
    public static IServiceCollection AddKrakarrTelemetry(this IServiceCollection services)
    {
        services.AddSingleton<TelemetryLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<TelemetryLoggerProvider>());
        services.AddSingleton<LibraryStats>();
        services.AddSingleton<TelemetryManager>();
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryManager>());
        return services;
    }

    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        // Under /api/v1/settings, so admins only (RoleAccess).
        app.MapGet("/api/v1/settings/telemetry/status", (TelemetryManager telemetry) => telemetry.Status).WithTags("Settings");
        return app;
    }
}
