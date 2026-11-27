using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Media.Tests;

public class AlternateTitlesTests
{
    private static string Norm(string t) => ReleaseParser.NormalizeTitle(t);

    [Theory]
    [InlineData("es", "ES", "es")]
    [InlineData("es", "MX", "es-419")]
    [InlineData("pt", "BR", "pt-br")]
    [InlineData("pt", "PT", "pt")]
    [InlineData(null, "AR", "es-419")]
    [InlineData(null, "FR", "fr")]
    [InlineData(null, "US", "en")]
    [InlineData(null, "XX", null)]
    [InlineData("xx", null, null)]
    public void Maps_tmdb_language_and_country(string? language, string? country, string? expected) =>
        Assert.Equal(expected, AlternateTitles.LanguageFor(language, country));

    [Fact]
    public void Select_drops_blank_duplicate_main_and_unwanted_language_titles()
    {
        var kept = AlternateTitles.Select(
            [
                new("The Matrix", "en"), // the main title
                new("  ", "es"),
                new("Matrix Español", "es"),
                new("MATRIX ESPANOL", "it"), // same once normalised
                new("Матрица", "ru"), // language not kept
                new("Matorikkusu", null), // unknown language: kept
                new(new string('x', AlternateTitle.MaxTitleLength + 1), null),
            ],
            ["The Matrix"], Norm, new HashSet<string> { "en", "es", "it" });
        Assert.Equal([new AlternateTitleInfo("Matrix Español", "es"), new AlternateTitleInfo("Matorikkusu")], kept);
    }

    [Fact]
    public void Select_caps_the_count()
    {
        var many = Enumerable.Range(1, 50).Select(i => new AlternateTitleInfo($"Title {i}"));
        Assert.Equal(AlternateTitles.MaxPerSource, AlternateTitles.Select(many, [], Norm).Count);
    }

    [Fact]
    public void Sync_touches_only_its_source()
    {
        var current = new List<AlternateTitle>
        {
            new() { Id = 1, Title = "Old", NormalizedTitle = "old", Source = AlternateTitleSource.Metadata },
            new() { Id = 2, Title = "Kept", NormalizedTitle = "kept", Language = "en", Source = AlternateTitleSource.Metadata },
            new() { Id = 3, Title = "Mine", NormalizedTitle = "mine", Source = AlternateTitleSource.User },
        };

        var removed = AlternateTitles.Sync(current, AlternateTitleSource.Metadata, [new("KEPT", "es"), new("New", "fr")], Norm);

        Assert.Equal([1], removed.Select(t => t.Id));
        Assert.Equal(["KEPT", "Mine", "New"], current.Select(t => t.Title).Order(StringComparer.Ordinal));
        var kept = current.Single(t => t.Id == 2);
        Assert.Equal(("KEPT", "es"), (kept.Title, kept.Language));
        var added = current.Single(t => t.Title == "New");
        Assert.Equal(("new", AlternateTitleSource.Metadata, "fr"), (added.NormalizedTitle, added.Source, added.Language));
    }

    [Fact]
    public void SetUserTitles_validates_and_normalises_languages()
    {
        var current = new List<AlternateTitle> { new() { Title = "Meta", NormalizedTitle = "meta", Source = AlternateTitleSource.Metadata } };
        var (_, error) = AlternateTitles.SetUserTitles(current,
            Enumerable.Range(0, AlternateTitles.MaxUserTitles + 1).Select(i => new AlternateTitleInfo($"T{i}")).ToList(), [], Norm);
        Assert.NotNull(error);

        var (removed, ok) = AlternateTitles.SetUserTitles(current, [new("Mi título", "Spanish"), new("Other", "klingon"), new("Main", null)], ["Main"], Norm);
        Assert.Null(ok);
        Assert.Empty(removed);
        Assert.Equal(
            [("Meta", (string?)null, AlternateTitleSource.Metadata), ("Mi título", "es", AlternateTitleSource.User), ("Other", null, AlternateTitleSource.User)],
            current.Select(t => (t.Title, t.Language, t.Source)));
    }

    [Fact]
    public void ForIndexer_prefers_the_indexer_language_and_includes_unknown_languages()
    {
        AlternateTitleInfo[] titles = [new("Unknown"), new("Spanish", "es"), new("French", "fr"), new("English", "en")];
        Assert.Equal(["Spanish", "Unknown"], AlternateTitles.ForIndexer(titles, "es", 5));
        Assert.Equal(["English"], AlternateTitles.ForIndexer(titles, null, 1));
    }
}
