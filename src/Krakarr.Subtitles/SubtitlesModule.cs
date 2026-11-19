using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Subtitles;

public sealed record SubtitleSearchRequest(string VideoPath, string Language, string? ImdbId, int? Season, int? Episode, string? ReleaseName);

public sealed record SubtitleCandidate(string ProviderName, string Id, string Language, string ReleaseName, int Score, bool HearingImpaired);

/// <summary>A subtitle source (OpenSubtitles, Podnapisi, ...). Providers arrive in a later milestone.</summary>
public interface ISubtitleProvider
{
    string Name { get; }
    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(SubtitleSearchRequest request, CancellationToken ct = default);
    Task<byte[]> DownloadAsync(SubtitleCandidate candidate, CancellationToken ct = default);
}

public static class SubtitlesModule
{
    public static IServiceCollection AddKrakarrSubtitles(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapSubtitleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/subtitles").WithTags("Subtitles");
        group.MapGet("/providers", (IEnumerable<ISubtitleProvider> providers) => providers.Select(p => new { p.Name }));
        return app;
    }
}
