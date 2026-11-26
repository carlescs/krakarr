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
    public void Reads_hdr_from_the_hevc_sps_when_the_container_has_no_transfer()
    {
        var video = Assert.Single(MediaInfoReader.ReadHeaders(Fixture("vui-hdr10-hevc.mkv")).Video);

        Assert.Equal(("HEVC", 10, DynamicRange.Hdr10), (video.Codec, video.BitDepth, video.DynamicRange));
    }

    [Fact]
    public void Reads_bit_depth_and_hlg_from_the_avc_sps()
    {
        var video = Assert.Single(MediaInfoReader.ReadHeaders(Fixture("vui-hlg-avc444.mp4")).Video);

        // High 4:4:4 Predictive at 8 bits: the profile alone would say 10.
        Assert.Equal(("AVC", 8, DynamicRange.Hlg), (video.Codec, video.BitDepth, video.DynamicRange));
    }

    [Theory]
    [InlineData("hdr10plus-hevc.mkv")]
    [InlineData("hdr10plus-hevc.mp4")]
    public void Detects_hdr10_plus_from_the_first_frame(string file)
    {
        var video = Assert.Single(MediaInfoReader.ReadHeaders(Fixture(file)).Video);

        Assert.Equal(("HEVC", 3840, 2160, 10, DynamicRange.Hdr10Plus), (video.Codec, video.Width, video.Height, video.BitDepth, video.DynamicRange));
    }

    [Fact]
    public void Plain_hdr10_files_stay_hdr10()
    {
        Assert.Equal(DynamicRange.Hdr10, Assert.Single(MediaInfoReader.ReadHeaders(Fixture("hdr10-hevc.mkv")).Video).DynamicRange);
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

    /// <summary>An hvcC with no parameter sets and 4-byte NAL lengths.</summary>
    private static readonly byte[] EmptyHvcC = [1, .. new byte[20], 0x03, 0];

    /// <summary>A 4-byte length-prefixed NAL unit.</summary>
    private static byte[] Nal(params byte[] nal)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)nal.Length);
        return [.. length, .. nal];
    }

    private static readonly byte[] Hdr10PlusSei = Nal(0x4E, 0x01, 0x04, 0x07, 0xB5, 0x00, 0x3C, 0x00, 0x01, 0x04, 0x01, 0x80);
    private static readonly byte[] Slice = Nal(0x26, 0x01, 0xAF, 0x00);

    private static byte[] HevcTrack(byte trackNumber, bool dolbyVision) =>
        El(0xAE, UInt(0xD7, trackNumber), UInt(0x83, 1), Str(0x86, "V_MPEGH/ISO/HEVC"), El(0x63A2, EmptyHvcC),
            dolbyVision ? El(0x41E4, UInt(0x41E7, 0x64766343)) : [],
            El(0xE0, UInt(0xB0, 3840), UInt(0xBA, 2160), El(0x55B0, UInt(0x55BA, 16))));

    private static byte[] SimpleBlock(byte trackNumber, params byte[][] nals) =>
        El(0xA3, [(byte)(0x80 | trackNumber), 0, 0, 0x80, .. nals.SelectMany(n => n)]);

    [Fact]
    public void Finds_the_hdr10_plus_sei_in_the_video_tracks_first_block_after_other_tracks()
    {
        var tracks = El(0x1654AE6B,
            El(0xAE, UInt(0xD7, 1), UInt(0x83, 2), Str(0x86, "A_AC3"), El(0xE1, UInt(0x9F, 6))),
            HevcTrack(2, dolbyVision: true));
        var cluster = El(0x1F43B675, UInt(0xE7, 0),
            SimpleBlock(1, new byte[16]), // audio first
            El(0xA0, El(0xA1, [0x82, 0, 0, 0, .. Hdr10PlusSei, .. Slice])));
        // Tags between Tracks and the first Cluster are stepped over.
        var file = EbmlHeader.Concat(El(0x18538067, tracks, El(0x1254C367, new byte[64]), cluster)).ToArray();

        Assert.Equal(DynamicRange.DolbyVisionHdr10Plus, Assert.Single(MediaInfoReader.Read(new MemoryStream(file)).Video).DynamicRange);
    }

    [Fact]
    public void An_sei_after_the_first_slice_or_a_broken_cluster_is_ignored()
    {
        var late = EbmlHeader.Concat(El(0x18538067, El(0x1654AE6B, HevcTrack(1, false)),
            El(0x1F43B675, SimpleBlock(1, Slice, Hdr10PlusSei)))).ToArray();
        Assert.Equal(DynamicRange.Hdr10, Assert.Single(MediaInfoReader.Read(new MemoryStream(late)).Video).DynamicRange);

        // A cluster cut off mid-block still leaves the track info.
        var cut = EbmlHeader.Concat(El(0x18538067, El(0x1654AE6B, HevcTrack(1, false)),
            El(0x1F43B675, SimpleBlock(1, Hdr10PlusSei, Slice)))).ToArray()[..^10];
        var video = Assert.Single(MediaInfoReader.Read(new MemoryStream(cut)).Video);
        Assert.Equal((3840, DynamicRange.Hdr10), (video.Width, video.DynamicRange));
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
