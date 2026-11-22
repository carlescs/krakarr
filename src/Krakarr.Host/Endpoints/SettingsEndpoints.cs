using System.Security.Cryptography;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

public sealed record RootFolderDto(int Id, string Path, MediaType MediaType, bool Accessible, long? FreeSpace);

public sealed record AddRootFolderRequest(string Path, MediaType MediaType = MediaType.Movie);

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/v1/settings").WithTags("Settings");
        MapSection<GeneralSettings>(settings, "general", (current, incoming) => incoming with { ApiKey = current.ApiKey });
        MapSection<MediaManagementSettings>(settings, "media");
        MapSection<MetadataSettings>(settings, "metadata",
            merge: (_, incoming) => incoming with { ReleaseRegion = MovieReleases.NormalizeRegion(incoming.ReleaseRegion) },
            validate: s => s.ReleaseRegion is { } r && (r.Length != 2 || !r.All(char.IsAsciiLetterUpper))
                ? "Release region must be a two-letter country code (ISO 3166-1), e.g. US or ES"
                : null);
        MapSection<DownloadClientSettings>(settings, "downloads",
            merge: (current, incoming) => incoming with { QBittorrent = incoming.QBittorrent.WithSecretsFrom(current.QBittorrent) },
            present: s => s with { QBittorrent = s.QBittorrent.Masked() },
            validate: s => s.Client != DownloadClientKind.QBittorrent ? null
                : string.IsNullOrWhiteSpace(s.QBittorrent.Tag) ? "A qBittorrent tag is required so Krakarr only manages its own torrents"
                : !Uri.TryCreate(s.QBittorrent.Url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") ? "qBittorrent URL must be an absolute http(s) URL"
                : null);
        MapSection<AutomationSettings>(settings, "automation");
        MapSection<SubtitleSettings>(settings, "subtitles",
            merge: (current, incoming) => incoming.OpenSubtitles.Password == SubtitleSettings.PasswordMask
                ? incoming with { OpenSubtitles = incoming.OpenSubtitles with { Password = current.OpenSubtitles.Password } }
                : incoming,
            present: s => string.IsNullOrEmpty(s.OpenSubtitles.Password)
                ? s
                : s with { OpenSubtitles = s.OpenSubtitles with { Password = SubtitleSettings.PasswordMask } },
            validate: s => s.Languages.Any(l => string.IsNullOrWhiteSpace(l)) ? "Languages must not be empty"
                : s.MinimumScoreMovies is < 0 or > 100 || s.MinimumScoreEpisodes is < 0 or > 100 ? "Minimum scores must be between 0 and 100"
                : null);

        MapSection<JellyfinSettings>(settings, "jellyfin",
            merge: (current, incoming) => incoming.ApiKey == JellyfinSettings.ApiKeyMask ? incoming with { ApiKey = current.ApiKey } : incoming,
            present: s => string.IsNullOrEmpty(s.ApiKey) ? s : s with { ApiKey = JellyfinSettings.ApiKeyMask },
            validate: s => !IsHttpUrl(s.Url) ? "Jellyfin URL must be an absolute http(s) URL"
                : !IsHttpUrl(s.PublicUrl) ? "Jellyfin public URL must be an absolute http(s) URL"
                : null);

        MapSection<TelemetrySettings>(settings, "telemetry",
            merge: (current, incoming) => incoming with
            {
                Headers = incoming.Headers == TelemetrySettings.HeadersMask ? current.Headers : incoming.Headers,
                Endpoint = string.IsNullOrWhiteSpace(incoming.Endpoint) ? null : incoming.Endpoint.Trim(),
                ServiceName = incoming.ServiceName?.Trim() ?? string.Empty,
            },
            present: s => string.IsNullOrEmpty(s.Headers) ? s : s with { Headers = TelemetrySettings.HeadersMask },
            validate: s => s.Validate());

        MapSection<LanguageSettings>(settings, "languages",
            merge: (_, incoming) => incoming with { Preferred = MediaLanguages.Clean(incoming.Preferred) });

        // Language catalogue plus the global wanted languages, which movie and series pages show as their default.
        app.MapGet("/api/v1/languages", async (ISettingsService s, CancellationToken ct) => new
        {
            All = MediaLanguages.All.Select(l => new { l.Code, l.Name }),
            (await s.GetAsync<LanguageSettings>(ct)).Preferred,
        }).WithTags("Languages");

        settings.MapPost("/general/regenerate-api-key", async (ISettingsService s, CancellationToken ct) =>
        {
            var general = await s.GetAsync<GeneralSettings>(ct);
            var updated = general with { ApiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) };
            await s.SaveAsync(updated, ct);
            return updated;
        });

        app.MapGet("/api/v1/qualities", () => Qualities.All).WithTags("Quality");

        var profiles = app.MapGroup("/api/v1/qualityprofiles").WithTags("Quality");
        profiles.MapGet("/", async (KrakarrDbContext db, CancellationToken ct) => await db.QualityProfiles.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct));
        profiles.MapPost("/", async (QualityProfile input, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (ValidateProfile(input) is { } problem) return problem;
            input.Id = 0;
            db.QualityProfiles.Add(input);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/qualityprofiles/{input.Id}", input);
        });
        profiles.MapPut("/{id:int}", async (int id, QualityProfile input, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (ValidateProfile(input) is { } problem) return problem;
            var existing = await db.QualityProfiles.FindAsync([id], ct);
            if (existing is null) return Results.NotFound();
            existing.Name = input.Name;
            existing.AllowedQualityIds = input.AllowedQualityIds;
            existing.CutoffQualityId = input.CutoffQualityId;
            existing.UpgradeAllowed = input.UpgradeAllowed;
            existing.MinSizeMb = input.MinSizeMb;
            existing.MaxSizeMb = input.MaxSizeMb;
            await db.SaveChangesAsync(ct);
            return Results.Ok(existing);
        });
        profiles.MapDelete("/{id:int}", async (int id, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (await db.Movies.AnyAsync(m => m.QualityProfileId == id, ct) || await db.Series.AnyAsync(s => s.QualityProfileId == id, ct))
                return Results.Problem("Profile is in use", statusCode: StatusCodes.Status409Conflict);
            return await db.QualityProfiles.Where(p => p.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
        });

        var roots = app.MapGroup("/api/v1/rootfolders").WithTags("Root folders");
        roots.MapGet("/", async (KrakarrDbContext db, CancellationToken ct) =>
            (await db.RootFolders.AsNoTracking().OrderBy(r => r.Path).ToListAsync(ct)).Select(ToDto));
        roots.MapPost("/", async (AddRootFolderRequest req, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path) || !Path.IsPathRooted(req.Path))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["path"] = ["An absolute path is required"] });
            var path = Path.TrimEndingDirectorySeparator(req.Path.Trim());
            if (await db.RootFolders.AnyAsync(r => r.Path == path, ct))
                return Results.Problem("Root folder already exists", statusCode: StatusCodes.Status409Conflict);
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.Problem($"Cannot create folder: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
            }

            var folder = new RootFolder { Path = path, MediaType = req.MediaType };
            db.RootFolders.Add(folder);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(folder));
        });
        roots.MapDelete("/{id:int}", async (int id, KrakarrDbContext db, CancellationToken ct) =>
            await db.RootFolders.Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());

        app.MapGet("/api/v1/filesystem", (string? path) =>
        {
            var target = string.IsNullOrWhiteSpace(path) ? "/" : path;
            if (!Directory.Exists(target)) return Results.NotFound();
            try
            {
                var dirs = new DirectoryInfo(target).EnumerateDirectories()
                    .Where(d => !d.Name.StartsWith('.'))
                    .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { d.Name, Path = d.FullName })
                    .Take(500)
                    .ToList();
                return Results.Ok(new { Path = Path.GetFullPath(target), Parent = Directory.GetParent(target)?.FullName, Directories = dirs });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem("Access denied", statusCode: StatusCodes.Status403Forbidden);
            }
        }).WithTags("Filesystem");

        return app;
    }

    private static void MapSection<T>(
        RouteGroupBuilder group,
        string name,
        Func<T, T, T>? merge = null,
        Func<T, T>? present = null,
        Func<T, string?>? validate = null)
        where T : class, ISettingsSection, new()
    {
        present ??= v => v;
        group.MapGet($"/{name}", async (ISettingsService s, CancellationToken ct) => present(await s.GetAsync<T>(ct)));
        group.MapPut($"/{name}", async (T input, ISettingsService s, CancellationToken ct) =>
        {
            var value = merge is null ? input : merge(await s.GetAsync<T>(ct), input);
            if (validate?.Invoke(value) is { } error)
            {
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            }

            await s.SaveAsync(value, ct);
            return Results.Ok(present(value));
        });
    }

    /// <summary>True for an empty value or an absolute http(s) URL.</summary>
    private static bool IsHttpUrl(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps));

    private static IResult? ValidateProfile(QualityProfile p)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(p.Name)) errors["name"] = ["Name is required"];
        if (p.AllowedQualityIds.Count == 0) errors["allowedQualityIds"] = ["Select at least one quality"];
        if (!p.AllowedQualityIds.Contains(p.CutoffQualityId)) errors["cutoffQualityId"] = ["Cutoff must be one of the allowed qualities"];
        if (p.MaxSizeMb > 0 && p.MinSizeMb > p.MaxSizeMb) errors["maxSizeMb"] = ["Maximum size must be larger than minimum size"];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static RootFolderDto ToDto(RootFolder r)
    {
        var accessible = Directory.Exists(r.Path);
        long? free = null;
        if (accessible)
        {
            try
            {
                free = new DriveInfo(r.Path).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }

        return new RootFolderDto(r.Id, r.Path, r.MediaType, accessible, free);
    }
}
