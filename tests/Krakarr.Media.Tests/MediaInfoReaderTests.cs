using System.Buffers.Binary;
using System.Text;
using Krakarr.Media.Files;

namespace Krakarr.Media.Tests;

public class MediaInfoReaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // Fixtures are 2-second clips made with ffmpeg (see Fixtures/README.md).

    [Fact]
    public void Reads_a_matroska_file_made_by_ffmpeg()
    {
        var info = MediaInfoReader.ReadHeaders(Fixture("hdr10-hevc.mkv"));

        var video = Assert.Single(info.Video);
        Assert.Equal(("HEVC", 1920, 800, 10, DynamicRange.Hdr10), (video.Codec, video.Width, video.Height, video.BitDepth, video.DynamicRange));
        Assert.Equal(2, info.Audio.Count);
        Assert.Equal(("AC3", 6, "spa"), (info.Audio[0].Codec, info.Audio[0].Channels, info.Audio[0].Language));
        Assert.Equal(("AAC", 2, "eng"), (info.Audio[1].Codec, info.Audio[1].Channels, info.Audio[1].Language));
        var subtitle = Assert.Single(info.Subtitles);
        Assert.Equal("fre", subtitle.Language);
        Assert.True(subtitle.Forced);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 1.9, 2.3);
    }

    [Fact]
    public void Reads_an_mp4_file_made_by_ffmpeg()
    {
        var info = MediaInfoReader.ReadHeaders(Fixture("sdr-avc.mp4"));

        var video = Assert.Single(info.Video);
        Assert.Equal(("AVC", 1280, 720, 8, (string?)null), (video.Codec, video.Width, video.Height, video.BitDepth, video.DynamicRange));
        var audio = Assert.Single(info.Audio);
        Assert.Equal(("EAC3", 6, "ger"), (audio.Codec, audio.Channels, audio.Language));
        Assert.Equal("ita", Assert.Single(info.Subtitles).Language);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 1.9, 2.3);
    }

    [Fact]
    public void Summarizes_a_file_for_storage()
    {
        var summary = MediaInfoSummary.Parse(MediaInfoSummary.ReadFile(Fixture("hdr10-hevc.mkv")))!;

        Assert.Equal(1080, summary.Resolution); // 1920x800 is a cropped 1080p frame
        Assert.Equal(DynamicRange.Hdr10, summary.DynamicRange);
        Assert.Equal(["es", "en"], summary.AudioLanguages);
        Assert.Equal("5.1", summary.AudioChannels);
        Assert.Equal(2, summary.DurationSeconds);
    }

    [Fact]
    public void Unreadable_files_store_an_empty_value()
    {
        Assert.Equal(string.Empty, MediaInfoSummary.Serialize(MediaInfoSummary.From(MediaInfoReader.Read(new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8, 9])))));
        Assert.True(MediaInfoReader.ReadHeaders("/nonexistent/video.mkv").IsEmpty);
        Assert.Null(MediaInfoSummary.Parse(""));
        Assert.Null(MediaInfoSummary.Parse("{not json"));
    }

    // ---- Matroska builders (for what ffmpeg can't produce: Dolby Vision, durations in the SeekHead…) ----

    private static byte[] El(uint id, params byte[][] children) => El(id, children.SelectMany(c => c).ToArray());

    private static byte[] El(uint id, byte[] body)
    {
        var idBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(idBytes, id);
        idBytes = idBytes.SkipWhile(b => b == 0).ToArray();
        var size = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)body.Length);
        size[0] = 0x01;
        return [.. idBytes, .. size, .. body];
    }

    private static byte[] UInt(uint id, ulong value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)value);
        return El(id, b);
    }

    private static byte[] Str(uint id, string value) => El(id, Encoding.UTF8.GetBytes(value));

    private static byte[] Double(uint id, double value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, value);
        return El(id, b);
    }

    private static readonly byte[] EbmlHeader = El(0x1A45DFA3, Str(0x4282, "matroska"));

    [Fact]
    public void Reads_dolby_vision_and_duration_from_matroska()
    {
        var info = El(0x1549A966, UInt(0x2AD7B1, 1_000_000), Double(0x4489, 5_400_000)); // 90 minutes in ms
        var tracks = El(0x1654AE6B,
            El(0xAE, UInt(0x83, 1), Str(0x86, "V_MPEGH/ISO/HEVC"),
                El(0x41E4, UInt(0x41E7, 0x64766343)),
                El(0xE0, UInt(0xB0, 3840), UInt(0xBA, 2160), El(0x55B0, UInt(0x55B2, 10), UInt(0x55BA, 16)))),
            El(0xAE, UInt(0x83, 2), Str(0x86, "A_TRUEHD"), Str(0x536E, "TrueHD Atmos 7.1"), El(0xE1, UInt(0x9F, 8))),
            El(0xAE, UInt(0x83, 2), Str(0x86, "A_EAC3"), Str(0x22B59C, "und"), El(0xE1, UInt(0x9F, 2))));
        var file = EbmlHeader.Concat(El(0x18538067, info, tracks, El(0x1F43B675, new byte[32]))).ToArray();

        var result = MediaInfoReader.Read(new MemoryStream(file));

        Assert.Equal(TimeSpan.FromMinutes(90), result.Duration);
        Assert.Equal(DynamicRange.DolbyVisionHdr10, Assert.Single(result.Video).DynamicRange);
        Assert.Equal(("TrueHD Atmos", 8, "eng"), (result.Audio[0].Codec, result.Audio[0].Channels, result.Audio[0].Language));
        Assert.Null(result.Audio[1].Language);
        var summary = MediaInfoSummary.From(result)!;
        Assert.Equal(2160, summary.Resolution);
        Assert.Equal("7.1", summary.AudioChannels);
    }

    [Fact]
    public void Parses_ffprobe_output()
    {
        const string json = """
            {"streams":[
              {"codec_name":"mpeg4","codec_type":"video","width":720,"height":304,"pix_fmt":"yuv420p"},
              {"codec_name":"mp3","codec_type":"audio","channels":2,"tags":{"language":"eng"}},
              {"codec_name":"hevc","codec_type":"video","width":3840,"height":2160,"pix_fmt":"yuv420p10le","color_transfer":"smpte2084",
               "side_data_list":[{"side_data_type":"DOVI configuration record"}]},
              {"codec_name":"subrip","codec_type":"subtitle","tags":{"language":"spa"},"disposition":{"forced":1,"hearing_impaired":0}}
            ],"format":{"duration":"5025.120000"}}
            """;

        var info = Ffprobe.Parse(Encoding.UTF8.GetBytes(json))!;

        Assert.Equal(("MPEG-4", 8), (info.Video[0].Codec, info.Video[0].BitDepth));
        Assert.Equal((DynamicRange.DolbyVisionHdr10, 10), (info.Video[1].DynamicRange, info.Video[1].BitDepth));
        Assert.Equal(("MP3", 2, "eng"), (info.Audio[0].Codec, info.Audio[0].Channels, info.Audio[0].Language));
        Assert.True(Assert.Single(info.Subtitles).Forced);
        Assert.Equal(5025, (int)info.Duration!.Value.TotalSeconds);
        Assert.Equal(2160, MediaInfoSummary.From(info)!.Resolution); // the largest video track is the main one
    }
}
