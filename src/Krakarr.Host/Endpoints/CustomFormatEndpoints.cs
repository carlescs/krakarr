using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Media.CustomFormats;
using Krakarr.Media.Parsing;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

public sealed record CustomFormatImportRequest(string Json, CustomFormatApp App = CustomFormatApp.Radarr);

public sealed record CustomFormatImportResponse(int Created, int Updated, IReadOnlyList<string> Warnings);

public sealed record CustomFormatTestRequest(string Title);

public static class CustomFormatEndpoints
{
    private const int MaxSpecifications = 50;

    public static IEndpointRouteBuilder MapCustomFormatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/customformats").WithTags("Custom formats");

        group.MapGet("/", async (KrakarrDbContext db, CancellationToken ct) =>
            await db.CustomFormats.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct));

        // What the editor offers: each condition with the values it accepts (empty = free text / regex, or a size range).
        group.MapGet("/schema", () => Enum.GetValues<CustomFormatCondition>()
            .Select(c => new { Condition = c, Values = CustomFormatSet.AllowedValues(c) }));

        group.MapPost("/", async (CustomFormat input, KrakarrDbContext db, CustomFormatCache cache, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem) return problem;
            if (await db.CustomFormats.AnyAsync(f => f.Name == input.Name, ct))
                return Results.Problem("A custom format with this name already exists", statusCode: StatusCodes.Status409Conflict);
            input.Id = 0;
            db.CustomFormats.Add(input);
            await db.SaveChangesAsync(ct);
            cache.Invalidate();
            return Results.Created($"/api/v1/customformats/{input.Id}", input);
        });

        group.MapPut("/{id:int}", async (int id, CustomFormat input, KrakarrDbContext db, CustomFormatCache cache, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem) return problem;
            var existing = await db.CustomFormats.FindAsync([id], ct);
            if (existing is null) return Results.NotFound();
            if (await db.CustomFormats.AnyAsync(f => f.Id != id && f.Name == input.Name, ct))
                return Results.Problem("A custom format with this name already exists", statusCode: StatusCodes.Status409Conflict);
            existing.Name = input.Name;
            existing.Specifications = input.Specifications;
            await db.SaveChangesAsync(ct);
            cache.Invalidate();
            return Results.Ok(existing);
        });

        group.MapDelete("/{id:int}", async (int id, KrakarrDbContext db, CustomFormatCache cache, CancellationToken ct) =>
        {
            if (await db.CustomFormats.Where(f => f.Id == id).ExecuteDeleteAsync(ct) == 0) return Results.NotFound();
            // Profiles are few; drop the deleted format's scores from them.
            foreach (var profile in await db.QualityProfiles.ToListAsync(ct))
            {
                if (profile.FormatScores.Any(s => s.FormatId == id)) profile.FormatScores = profile.FormatScores.Where(s => s.FormatId != id).ToList();
            }

            await db.SaveChangesAsync(ct);
            cache.Invalidate();
            return Results.NoContent();
        });

        // Paste Radarr/Sonarr/TRaSH JSON. Formats whose name already exists are replaced, so pasting an updated guide refreshes them.
        group.MapPost("/import", async (CustomFormatImportRequest req, KrakarrDbContext db, CustomFormatCache cache, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Json)) return Results.Problem("Paste the custom format JSON", statusCode: StatusCodes.Status400BadRequest);
            CustomFormatImportResult result;
            try
            {
                result = CustomFormatJson.Import(req.Json, req.App);
            }
            catch (FormatException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            var names = result.Formats.Select(f => f.Name).ToList();
            var existing = await db.CustomFormats.Where(f => names.Contains(f.Name)).ToDictionaryAsync(f => f.Name, ct);
            int created = 0, updated = 0;
            var warnings = result.Warnings.ToList();
            foreach (var format in result.Formats.DistinctBy(f => f.Name))
            {
                if (format.Specifications.Count > MaxSpecifications)
                {
                    warnings.Add($"Skipped \"{format.Name}\": more than {MaxSpecifications} conditions");
                    continue;
                }

                if (existing.TryGetValue(format.Name, out var current))
                {
                    current.Specifications = format.Specifications;
                    updated++;
                }
                else
                {
                    db.CustomFormats.Add(format);
                    created++;
                }
            }

            await db.SaveChangesAsync(ct);
            cache.Invalidate();
            return Results.Ok(new CustomFormatImportResponse(created, updated, warnings));
        });

        group.MapGet("/export", async (string? ids, CustomFormatApp? app, KrakarrDbContext db, CancellationToken ct) =>
        {
            var wanted = (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var i) ? i : -1).Where(i => i > 0).ToList();
            var query = db.CustomFormats.AsNoTracking();
            if (wanted.Count > 0) query = query.Where(f => wanted.Contains(f.Id));
            var formats = await query.OrderBy(f => f.Name).ToListAsync(ct);
            return Results.Text(CustomFormatJson.Export(formats, app ?? CustomFormatApp.Radarr).ToJsonString(new() { WriteIndented = true }), "application/json");
        });

        // Which formats a release name matches, to check conditions while editing them.
        group.MapPost("/test", async (CustomFormatTestRequest req, KrakarrDbContext db, CustomFormatCache cache, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 500)
                return Results.Problem("Enter a release name (up to 500 characters)", statusCode: StatusCodes.Status400BadRequest);
            var parsed = ReleaseParser.Parse(req.Title.Trim());
            var formats = await cache.GetAsync(db.CustomFormats, ct);
            var matched = formats.Match(new CustomFormatInput { Parsed = parsed, Languages = ReleaseLanguages.Resolve(parsed, new ReleaseInfo { Title = parsed.ReleaseTitle, Guid = string.Empty }) });
            return Results.Ok(new
            {
                Quality = parsed.Quality.Name,
                parsed.ReleaseGroup,
                parsed.Hdr,
                parsed.VideoCodec,
                parsed.Audio,
                parsed.AudioChannels,
                parsed.Editions,
                parsed.StreamingService,
                parsed.Languages,
                Flags = new[] { (parsed.Is3D, ReleaseTags.Flag3D), (parsed.HasHardcodedSubs, ReleaseTags.FlagHardcodedSubs), (parsed.IsUpscaled, ReleaseTags.FlagUpscaled) }
                    .Where(f => f.Item1).Select(f => f.Item2),
                Formats = matched,
            });
        });

        return app;
    }

    private static IResult? Validate(CustomFormat format)
    {
        var errors = new Dictionary<string, string[]>();
        format.Name = format.Name?.Trim() ?? string.Empty;
        format.Specifications ??= [];
        if (format.Name.Length == 0) errors["name"] = ["Name is required"];
        else if (format.Name.Length > 100) errors["name"] = ["Name must be at most 100 characters"];
        if (format.Specifications.Count == 0) errors["specifications"] = ["Add at least one condition"];
        else if (format.Specifications.Count > MaxSpecifications) errors["specifications"] = [$"Add at most {MaxSpecifications} conditions"];
        else
        {
            var problems = format.Specifications
                .Select((s, i) => CustomFormatSet.Validate(s) is { } e ? $"{(string.IsNullOrWhiteSpace(s.Name) ? $"Condition {i + 1}" : s.Name)}: {e}" : null)
                .OfType<string>()
                .ToArray();
            if (problems.Length > 0) errors["specifications"] = problems;
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }
}
