using Krakarr.Core.Domain;
using Krakarr.Media.CustomFormats;
using static Krakarr.Series.Tests.TestData;

namespace Krakarr.Series.Tests;

public class EpisodeCustomFormatTests
{
    private static readonly CustomFormatSet Formats = CustomFormatSet.Compile(
    [
        new CustomFormat { Id = 1, Name = "NF", Specifications = [new() { Condition = CustomFormatCondition.StreamingService, Value = "NF" }] },
        new CustomFormat { Id = 2, Name = "Upscaled", Specifications = [new() { Condition = CustomFormatCondition.Flag, Value = "Upscaled" }] },
    ]);

    private static QualityProfile Profile(int cutoff = 50) => new()
    {
        Id = 1,
        Name = "HD",
        AllowedQualityIds = Hd1080.AllowedQualityIds,
        CutoffQualityId = Qualities.WebDl1080.Id,
        FormatScores = [new(1, 50), new(2, -500)],
        CutoffFormatScore = cutoff,
    };

    private static EpisodeDecision Eval(Core.Domain.Series s, QualityProfile profile, string title) =>
        EpisodeDecisionEngine.Evaluate(s, profile, s.Episodes, Release(title), null, requireMonitored: true, Today, formats: Formats);

    private static Episode WithFile(Core.Domain.Series s, string sceneName, List<int>? formatIds)
    {
        var e1 = s.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1);
        e1.EpisodeFile = new EpisodeFile { Id = 9, SeriesId = 1, RelativePath = "x.mkv", QualityId = Qualities.WebDl1080.Id, SceneName = sceneName, CustomFormatIds = formatIds };
        e1.EpisodeFileId = 9;
        return e1;
    }

    [Fact]
    public void Scores_and_rejects_below_minimum()
    {
        var d = Eval(Office(), Profile(), "The.Office.S01E02.1080p.NF.WEB-DL.DDP5.1.H.264-NTb");
        Assert.Equal(50, d.CustomFormatScore);
        Assert.True(d.Approved, string.Join(", ", d.Rejections));

        Assert.Contains(Eval(Office(), Profile(), "The.Office.S01E02.1080p.UpScaled.WEB-DL.H.264-GRP").Rejections, r => r.StartsWith("Custom format score -500"));
    }

    [Fact]
    public void Score_upgrades_compare_against_the_stored_formats_of_the_file()
    {
        var s = Office();
        WithFile(s, "The.Office.S01E01.1080p.WEB-DL.H.264-GRP", formatIds: []);
        Assert.True(Eval(s, Profile(), "The.Office.S01E01.1080p.NF.WEB-DL.H.264-NTb").Approved);

        // Stored formats win over the scene name: the file already has the NF score.
        WithFile(s, "The.Office.S01E01.1080p.WEB-DL.H.264-GRP", formatIds: [1]);
        Assert.Contains(Eval(s, Profile(), "The.Office.S01E01.1080p.NF.WEB-DL.H.264-NTb").Rejections, r => r.Contains("meets cutoff (WEBDL-1080p, score 50)"));

        // Files imported before custom formats are scored from their scene name.
        WithFile(s, "The.Office.S01E01.1080p.NF.WEB-DL.H.264-NTb", formatIds: null);
        Assert.Contains(Eval(s, Profile(), "The.Office.S01E01.1080p.NF.WEB-DL.H.264-NTb").Rejections, r => r.Contains("meets cutoff"));
    }

    [Fact]
    public void Episodes_below_the_score_cutoff_are_wanted()
    {
        var s = Office();
        var e1 = WithFile(s, "The.Office.S01E01.1080p.WEB-DL.H.264-GRP", formatIds: []);
        Assert.True(EpisodeDecisionEngine.IsWanted(e1, Profile(), Today, Formats));
        Assert.False(EpisodeDecisionEngine.IsWanted(e1, Profile(cutoff: 0), Today, Formats));

        e1.EpisodeFile!.CustomFormatIds = [1];
        Assert.False(EpisodeDecisionEngine.IsWanted(e1, Profile(), Today, Formats));
    }
}
