using System.Security.Cryptography;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Data;

/// <summary>Applies migrations and seeds default data on startup.</summary>
public sealed class DatabaseSeeder(KrakarrDbContext db, ISettingsService settings)
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        if (!await db.QualityProfiles.AnyAsync(ct))
        {
            db.QualityProfiles.AddRange(
                new QualityProfile
                {
                    Name = "HD-1080p",
                    AllowedQualityIds = [Qualities.WebRip1080.Id, Qualities.WebDl1080.Id, Qualities.Bluray1080.Id, Qualities.Hdtv1080.Id],
                    CutoffQualityId = Qualities.Bluray1080.Id,
                },
                new QualityProfile
                {
                    Name = "HD-720p/1080p",
                    AllowedQualityIds =
                    [
                        Qualities.Hdtv720.Id, Qualities.WebRip720.Id, Qualities.WebDl720.Id, Qualities.Bluray720.Id,
                        Qualities.Hdtv1080.Id, Qualities.WebRip1080.Id, Qualities.WebDl1080.Id, Qualities.Bluray1080.Id,
                    ],
                    CutoffQualityId = Qualities.Bluray1080.Id,
                },
                new QualityProfile
                {
                    Name = "Ultra-HD",
                    AllowedQualityIds = [Qualities.WebRip2160.Id, Qualities.WebDl2160.Id, Qualities.Bluray2160.Id, Qualities.Remux2160.Id],
                    CutoffQualityId = Qualities.Remux2160.Id,
                },
                new QualityProfile
                {
                    Name = "Any",
                    AllowedQualityIds = Qualities.All.Where(q => q.Weight >= Qualities.Sdtv.Weight).Select(q => q.Id).ToList(),
                    CutoffQualityId = Qualities.Bluray1080.Id,
                });
            await db.SaveChangesAsync(ct);
        }

        var general = await settings.GetAsync<GeneralSettings>(ct);
        if (string.IsNullOrEmpty(general.ApiKey))
        {
            await settings.SaveAsync(general with { ApiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) }, ct);
        }
    }
}
