using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Data;

public static class AlternateTitleLanguages
{
    /// <summary>
    /// Languages whose alternate titles are worth keeping for an item: its wanted audio languages (or the global ones),
    /// the default languages of the indexers, English and the item's original language.
    /// </summary>
    public static async Task<HashSet<string>> ForAsync(
        KrakarrDbContext db, ISettingsService settings, IReadOnlyList<string>? itemLanguages, string? originalLanguage, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MediaLanguages.English };
        set.UnionWith((await settings.GetAsync<LanguageSettings>(ct)).For(itemLanguages));
        set.UnionWith(await db.Indexers.AsNoTracking().Where(i => i.DefaultLanguage != null).Select(i => i.DefaultLanguage!).Distinct().ToListAsync(ct));
        if (!string.IsNullOrWhiteSpace(originalLanguage)) set.Add(originalLanguage);
        return set;
    }
}
