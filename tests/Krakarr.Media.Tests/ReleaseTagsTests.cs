using Krakarr.Media.Parsing;

namespace Krakarr.Media.Tests;

public class ReleaseTagsTests
{
    [Theory]
    [InlineData("Movie.2020.2160p.UHD.BluRay.REMUX.HDR.HEVC.Atmos-FGT", "HDR10")]
    [InlineData("Movie.2020.2160p.WEB-DL.DDP5.1.HDR10.H.265-FLUX", "HDR10")]
    [InlineData("Movie.2020.2160p.AMZN.WEB-DL.DDP5.1.HDR10+.H.265-FLUX", "HDR10+")]
    [InlineData("Movie.2020.2160p.WEB-DL.HDR10Plus.HEVC-GRP", "HDR10+")]
    [InlineData("Movie.2020.2160p.WEB-DL.DV.H.265-GRP", "DV")]
    [InlineData("Movie.2020.2160p.WEB-DL.DoVi.H.265-GRP", "DV")]
    [InlineData("Movie.2020.2160p.BluRay.Dolby.Vision.HEVC-GRP", "DV")]
    [InlineData("Movie.2020.2160p.WEB-DL.DV.HDR.H.265-GRP", "DV,HDR10")]
    [InlineData("Movie.2020.2160p.WEB-DL.DV.HDR10+.H.265-GRP", "DV,HDR10+")]
    [InlineData("Show.S01E01.2160p.WEB.HLG.H265-GRP", "HLG")]
    [InlineData("Movie.2020.1080p.BluRay.x264-SPARKS", "")]
    [InlineData("Movie.2020.1080p.DVDRip.x264-GRP", "")]
    [InlineData("Movie.2020.1080p.WEB-DL.x264-DV", "")]
    [InlineData("Movie.2020.1080p.WEB-DL.x264-HDRGROUP", "")]
    public void Parses_hdr(string release, string expected)
    {
        Assert.Equal(expected, string.Join(",", ReleaseParser.Parse(release).Hdr));
    }

    [Fact]
    public void Dolby_vision_without_hdr_fallback_is_flagged()
    {
        Assert.True(ReleaseParser.Parse("Movie.2020.2160p.WEB-DL.DV.H.265-GRP").IsDolbyVisionOnly);
        Assert.False(ReleaseParser.Parse("Movie.2020.2160p.WEB-DL.DV.HDR.H.265-GRP").IsDolbyVisionOnly);
        Assert.False(ReleaseParser.Parse("Movie.2020.2160p.WEB-DL.HDR.H.265-GRP").IsDolbyVisionOnly);
    }

    [Theory]
    [InlineData("Movie.2020.1080p.BluRay.x264-SPARKS", "x264")]
    [InlineData("Movie.2020.1080p.WEB-DL.H.264-FLUX", "x264")]
    [InlineData("Movie.2020.1080p.BluRay.REMUX.AVC-FGT", "x264")]
    [InlineData("Movie.2020.2160p.WEB.h265-GRP", "x265")]
    [InlineData("Movie.2020.2160p.BluRay.HEVC-GRP", "x265")]
    [InlineData("Movie.2020.1080p.WEB-DL.AV1.Opus-GRP", "av1")]
    [InlineData("Movie.2020.DVDRip.XviD-GRP", "xvid")]
    [InlineData("Movie.2020.1080p.WEB-DL-GRP", null)]
    public void Parses_video_codec(string release, string? expected)
    {
        Assert.Equal(expected, ReleaseParser.Parse(release).VideoCodec);
    }

    [Theory]
    [InlineData("Movie.2020.2160p.BluRay.REMUX.TrueHD.7.1.Atmos.HEVC-FGT", "Atmos,TrueHD", "7.1")]
    [InlineData("Movie.2020.2160p.AMZN.WEB-DL.DDP5.1.Atmos.H.265-FLUX", "Atmos,DD+", "5.1")]
    [InlineData("Movie.2020.1080p.WEB-DL.DD+5.1.H.264-GRP", "DD+", "5.1")]
    [InlineData("Movie.2020.1080p.WEB-DL.EAC3.H.264-GRP", "DD+", null)]
    [InlineData("Movie.2020.1080p.WEB-DL.DD5.1.H.264-FGT", "DD", "5.1")]
    [InlineData("Movie.2020.720p.BluRay.AC3.x264-GRP", "DD", null)]
    [InlineData("Movie.2020.1080p.BluRay.DTS-HD.MA.5.1.x264-GRP", "DTS-HD MA", "5.1")]
    [InlineData("Movie.2020.1080p.BluRay.DTS-HD.HRA.7.1.x264-GRP", "DTS-HD HRA", "7.1")]
    [InlineData("Movie.2020.2160p.BluRay.REMUX.DTS-X.7.1.HEVC-GRP", "DTS:X", "7.1")]
    [InlineData("Movie.2020.1080p.BluRay.DTS.x264-GRP", "DTS", null)]
    [InlineData("Movie.2020.1080p.BluRay.DTS-ES.x264-GRP", "DTS-ES", null)]
    [InlineData("Movie.2020.1080p.WEBRip.AAC2.0.x264-GRP", "AAC", "2.0")]
    [InlineData("Movie.2020.1080p.BluRay.FLAC.x264-GRP", "FLAC", null)]
    [InlineData("Movie.2020.1080p.BluRay.LPCM.x264-GRP", "PCM", null)]
    [InlineData("Movie.2020.1080p.BluRay.x264-SPARKS", "", null)]
    public void Parses_audio(string release, string audio, string? channels)
    {
        var parsed = ReleaseParser.Parse(release);
        Assert.Equal(audio, string.Join(",", parsed.Audio));
        Assert.Equal(channels, parsed.AudioChannels);
    }

    [Theory]
    [InlineData("Blade.Runner.1982.Directors.Cut.1080p.BluRay.x264-GRP", "Director's Cut")]
    [InlineData("Movie.2001.Director's.Cut.1080p.BluRay.x264-GRP", "Director's Cut")]
    [InlineData("The.Lord.of.the.Rings.2001.EXTENDED.1080p.BluRay.x264-GRP", "Extended")]
    [InlineData("Movie.2020.Extended.Edition.1080p.BluRay.x264-GRP", "Extended")]
    [InlineData("Movie.2020.IMAX.2160p.WEB-DL.DDP5.1.H.265-GRP", "IMAX")]
    [InlineData("Movie.2020.Theatrical.Cut.1080p.BluRay.x264-GRP", "Theatrical")]
    [InlineData("Movie.1985.REMASTERED.1080p.BluRay.x264-GRP", "Remastered")]
    [InlineData("Movie.1960.Criterion.1080p.BluRay.x264-GRP", "Criterion")]
    [InlineData("Movie.1979.Remastered.IMAX.1080p.BluRay.x264-GRP", "IMAX,Remastered")]
    [InlineData("The.Extended.Family.2020.1080p.WEB-DL.x264-GRP", "")]
    [InlineData("Movie.2020.1080p.BluRay.x264-SPARKS", "")]
    public void Parses_editions(string release, string expected)
    {
        Assert.Equal(expected, string.Join(",", ReleaseParser.Parse(release).Editions));
    }

    [Theory]
    [InlineData("Movie.2020.1080p.AMZN.WEB-DL.DDP5.1.H.264-FLUX", "AMZN")]
    [InlineData("Show.S01E01.1080p.NF.WEB-DL.DDP5.1.x264-NTb", "NF")]
    [InlineData("Show.S01E01.2160p.DSNP.WEB-DL.DDP5.1.HDR.H.265-GRP", "DSNP")]
    [InlineData("Show.S01E01.2160p.ATVP.WEB-DL.DDP5.1.Atmos.DV.H.265-GRP", "ATVP")]
    [InlineData("Show.S01E01.1080p.HMAX.WEB-DL.DD5.1.H.264-GRP", "HMAX")]
    [InlineData("Show.S01E01.1080p.MAX.WEB-DL.DD5.1.H.264-GRP", "MAX")]
    [InlineData("Mad.Max.Fury.Road.2015.1080p.BluRay.x264-GRP", null)]
    [InlineData("Movie.2020.1080p.WEB-DL.x264-NF", null)]
    [InlineData("Movie.2020.1080p.BluRay.x264-SPARKS", null)]
    public void Parses_streaming_service(string release, string? expected)
    {
        Assert.Equal(expected, ReleaseParser.Parse(release).StreamingService);
    }

    [Fact]
    public void Parses_flags()
    {
        Assert.True(ReleaseParser.Parse("Avatar.2009.3D.1080p.BluRay.Half-SBS.x264-GRP").Is3D);
        Assert.True(ReleaseParser.Parse("Movie.2020.1080p.BluRay.H-OU.x264-GRP").Is3D);
        Assert.False(ReleaseParser.Parse("Movie.2020.1080p.BluRay.x264-GRP").Is3D);

        Assert.True(ReleaseParser.Parse("Movie.2020.1080p.WEBRip.HC.x264-GRP").HasHardcodedSubs);
        Assert.True(ReleaseParser.Parse("Movie.2020.1080p.WEBRip.KORSUB.x264-GRP").HasHardcodedSubs);
        Assert.False(ReleaseParser.Parse("Movie.2020.1080p.WEBRip.x264-HC").HasHardcodedSubs);

        Assert.True(ReleaseParser.Parse("Movie.1990.2160p.UpScaled.WEB-DL.x265-GRP").IsUpscaled);
        Assert.True(ReleaseParser.Parse("Movie.1990.2160p.AI.Upscale.x265-GRP").IsUpscaled);
        Assert.False(ReleaseParser.Parse("Movie.1990.2160p.WEB-DL.x265-GRP").IsUpscaled);
    }
}
