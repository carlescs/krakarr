using Krakarr.Core.Domain;
using Krakarr.Media.CustomFormats;

namespace Krakarr.Media.Tests;

public class CustomFormatJsonTests
{
    // Shape of a TRaSH Guides Radarr custom format (fields as an object).
    private const string TrashHdr10Plus = """
        {
          "trash_id": "b17886cb4158d9fea189859409975758",
          "trash_scores": { "default": 1500 },
          "name": "HDR10+ Boost",
          "includeCustomFormatWhenRenaming": false,
          "specifications": [
            { "name": "HDR10+", "implementation": "ReleaseTitleSpecification", "negate": false, "required": true, "fields": { "value": "\\bHDR10(\\+|P(lus)?\\b)" } },
            { "name": "Not SDR", "implementation": "ReleaseTitleSpecification", "negate": true, "required": false, "fields": { "value": "\\bSDR\\b" } }
          ]
        }
        """;

    [Fact]
    public void Imports_trash_guides_format()
    {
        var result = CustomFormatJson.Import(TrashHdr10Plus, CustomFormatApp.Radarr);
        var format = Assert.Single(result.Formats);
        Assert.Empty(result.Warnings);
        Assert.Equal("HDR10+ Boost", format.Name);
        Assert.Equal(2, format.Specifications.Count);
        Assert.True(format.Specifications[0].Required);
        Assert.True(format.Specifications[1].Negate);
        Assert.Equal(@"\bHDR10(\+|P(lus)?\b)", format.Specifications[0].Value);
    }

    [Fact]
    public void Imports_radarr_api_export_with_field_arrays_and_numbered_values()
    {
        const string json = """
            [{
              "name": "Remux Tier",
              "specifications": [
                { "name": "Bluray", "implementation": "SourceSpecification", "negate": false, "required": true, "fields": [{ "name": "value", "value": 9 }] },
                { "name": "Remux", "implementation": "QualityModifierSpecification", "negate": false, "required": true, "fields": [{ "name": "value", "value": 5 }] },
                { "name": "French", "implementation": "LanguageSpecification", "negate": false, "required": false, "fields": [{ "name": "value", "value": 2 }, { "name": "exceptLanguage", "value": false }] },
                { "name": "Big", "implementation": "SizeSpecification", "negate": false, "required": false, "fields": [{ "name": "min", "value": 20 }, { "name": "max", "value": 100 }] },
                { "name": "2160p", "implementation": "ResolutionSpecification", "negate": false, "required": false, "fields": [{ "name": "value", "value": 2160 }] }
              ]
            }]
            """;
        var format = Assert.Single(CustomFormatJson.Import(json, CustomFormatApp.Radarr).Formats);
        Assert.Equal(
            [(CustomFormatCondition.Source, "Bluray"), (CustomFormatCondition.QualityModifier, "Remux"), (CustomFormatCondition.Language, "fr"),
             (CustomFormatCondition.Size, ""), (CustomFormatCondition.Resolution, "2160")],
            format.Specifications.Select(s => (s.Condition, s.Value)));
        Assert.Equal(20, format.Specifications[3].Min);
        Assert.Equal(100, format.Specifications[3].Max);
    }

    [Fact]
    public void Sonarr_sources_and_release_types_use_sonarr_numbering()
    {
        const string json = """
            { "name": "Packs", "specifications": [
              { "name": "WEB", "implementation": "SourceSpecification", "fields": { "value": 3 } },
              { "name": "Remux", "implementation": "SourceSpecification", "fields": { "value": 7 } },
              { "name": "Season pack", "implementation": "ReleaseTypeSpecification", "fields": { "value": 3 } }
            ] }
            """;
        var format = Assert.Single(CustomFormatJson.Import(json, CustomFormatApp.Sonarr).Formats);
        Assert.Equal(
            [(CustomFormatCondition.Source, "WebDl"), (CustomFormatCondition.QualityModifier, "Remux"), (CustomFormatCondition.ReleaseType, CustomFormatSet.SeasonPack)],
            format.Specifications.Select(s => (s.Condition, s.Value)));
    }

    [Fact]
    public void Skips_formats_with_conditions_krakarr_cannot_evaluate()
    {
        const string json = """
            [
              { "name": "Freeleech", "specifications": [{ "name": "FL", "implementation": "IndexerFlagSpecification", "fields": { "value": 1 } }] },
              { "name": "Not Original", "specifications": [{ "name": "Original", "implementation": "LanguageSpecification", "negate": true, "fields": { "value": -2 } }] },
              { "name": "Bad regex", "specifications": [{ "name": "x", "implementation": "ReleaseTitleSpecification", "fields": { "value": "([" } }] },
              { "name": "Empty", "specifications": [] },
              { "name": "OK", "specifications": [{ "name": "x", "implementation": "ReleaseTitleSpecification", "fields": { "value": "x265" } }] }
            ]
            """;
        var result = CustomFormatJson.Import(json, CustomFormatApp.Radarr);
        Assert.Equal(["OK"], result.Formats.Select(f => f.Name));
        Assert.Equal(4, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("IndexerFlagSpecification"));
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        Assert.Throws<FormatException>(() => CustomFormatJson.Import("{ not json", CustomFormatApp.Radarr));
        Assert.Throws<FormatException>(() => CustomFormatJson.Import("42", CustomFormatApp.Radarr));
    }

    [Fact]
    public void Export_round_trips()
    {
        var formats = new List<CustomFormat>
        {
            new()
            {
                Name = "Everything",
                Specifications =
                [
                    new() { Name = "t", Condition = CustomFormatCondition.ReleaseTitle, Value = @"\bIMAX\b", Required = true },
                    new() { Name = "s", Condition = CustomFormatCondition.Source, Value = "WebRip", Negate = true },
                    new() { Name = "m", Condition = CustomFormatCondition.QualityModifier, Value = "Remux" },
                    new() { Name = "l", Condition = CustomFormatCondition.Language, Value = "es-419", ExceptLanguage = true },
                    new() { Name = "z", Condition = CustomFormatCondition.Size, Min = 1.5, Max = 10 },
                    new() { Name = "h", Condition = CustomFormatCondition.Hdr, Value = "DV" },
                    new() { Name = "a", Condition = CustomFormatCondition.Audio, Value = "7.1" },
                    new() { Name = "r", Condition = CustomFormatCondition.ReleaseType, Value = CustomFormatSet.MultiEpisode },
                ],
            },
        };

        foreach (var app in new[] { CustomFormatApp.Radarr, CustomFormatApp.Sonarr })
        {
            if (app == CustomFormatApp.Sonarr) formats[0].Specifications.RemoveAt(3); // Sonarr ids for es-419 are not mapped
            var json = CustomFormatJson.Export(formats, app).ToJsonString();
            var back = Assert.Single(CustomFormatJson.Import(json, app).Formats);
            Assert.Equal(formats[0].Specifications, back.Specifications);
        }
    }
}
