using System.Buffers.Binary;
using System.Text;
using Krakarr.Media.Files;

namespace Krakarr.Media.Tests;

public class EmbeddedSubtitlesTests
{
    // ---- Matroska builders --------------------------------------------------------------------------

    private static byte[] El(uint id, params byte[][] children) => El(id, children.SelectMany(c => c).ToArray());

    private static byte[] El(uint id, byte[] body)
    {
        var idBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(idBytes, id);
        idBytes = idBytes.SkipWhile(b => b == 0).ToArray();
        // 8-byte size VINT: 0x01 followed by seven size bytes.
        var size = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)body.Length);
        size[0] = 0x01;
        return [.. idBytes, .. size, .. body];
    }

    private static byte[] UInt(uint id, ulong value) => El(id, [(byte)value]);
    private static byte[] Str(uint id, string value) => El(id, Encoding.UTF8.GetBytes(value));

    private static byte[] Track(ulong type, string? language = null, string? bcp47 = null, string? name = null, bool forced = false, bool hi = false, string codec = "S_TEXT/UTF8")
    {
        var parts = new List<byte[]> { UInt(0x83, type), Str(0x86, codec) };
        if (language is not null) parts.Add(Str(0x22B59C, language));
        if (bcp47 is not null) parts.Add(Str(0x22B59D, bcp47));
        if (name is not null) parts.Add(Str(0x536E, name));
        if (forced) parts.Add(UInt(0x55AA, 1));
        if (hi) parts.Add(UInt(0x55AB, 1));
        return El(0xAE, parts.ToArray());
    }

    private static readonly byte[] EbmlHeader = El(0x1A45DFA3, Str(0x4282, "matroska"));

    private static IReadOnlyList<EmbeddedSubtitleTrack> Read(byte[] data) => EmbeddedSubtitles.Read(new MemoryStream(data));

    [Fact]
    public void Reads_matroska_subtitle_tracks_with_flags()
    {
        var tracks = El(0x1654AE6B,
            Track(1, "eng", codec: "V_MPEG4/ISO/AVC"),
            Track(2, "jpn", codec: "A_AAC"),
            Track(0x11, "spa"),
            Track(0x11, "eng", name: "English (SDH)"),
            Track(0x11, "fre", forced: true),
            Track(0x11, "por", bcp47: "pt-BR", hi: true),
            Track(0x11)); // no language element = English
        var file = EbmlHeader.Concat(El(0x18538067, El(0x1549A966, Str(0x4D80, "test")), tracks, El(0x1F43B675, new byte[64]))).ToArray();

        var result = Read(file);

        Assert.Equal(5, result.Count);
        Assert.Equal(["spa", "eng", "fre", "pt-BR", "eng"], result.Select(t => t.Language));
        Assert.True(result[1].HearingImpaired);
        Assert.True(result[2].Forced);
        Assert.True(result[3].HearingImpaired);
        Assert.False(result[0].Forced || result[0].HearingImpaired);
        Assert.Equal("S_TEXT/UTF8", result[0].Codec);
    }

    [Fact]
    public void Follows_the_seek_head_when_tracks_come_after_the_media_data()
    {
        var cluster = El(0x1F43B675, new byte[200]);
        var tracks = El(0x1654AE6B, Track(0x11, "ger"));
        // SeekHead with a fixed-size position so its length does not depend on the value.
        byte[] SeekHead(ulong position)
        {
            var pos = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(pos, position);
            return El(0x114D9B74, El(0x4DBB, El(0x53AB, [0x16, 0x54, 0xAE, 0x6B]), El(0x53AC, pos)));
        }

        var seekHeadLength = SeekHead(0).Length;
        var segmentBody = SeekHead((ulong)(seekHeadLength + cluster.Length)).Concat(cluster).Concat(tracks).ToArray();
        var file = EbmlHeader.Concat(El(0x18538067, segmentBody)).ToArray();

        var result = Read(file);

        Assert.Equal("ger", Assert.Single(result).Language);
    }

    // ---- MP4 builders -------------------------------------------------------------------------------

    private static byte[] Box(string type, params byte[][] children)
    {
        var body = children.SelectMany(c => c).ToArray();
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(body.Length + 8));
        Encoding.ASCII.GetBytes(type, header.AsSpan(4));
        return [.. header, .. body];
    }

    private static byte[] Bytes(int length, Action<byte[]> fill)
    {
        var b = new byte[length];
        fill(b);
        return b;
    }

    private static byte[] Mp4Track(uint id, string handler, string codec, string language, uint? chapterRef = null, string? name = null)
    {
        var tkhd = Box("tkhd", Bytes(84, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12), id)));
        var packed = (ushort)(((language[0] - 0x60) << 10) | ((language[1] - 0x60) << 5) | (language[2] - 0x60));
        var mdhd = Box("mdhd", Bytes(24, b => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(20), packed)));
        var hdlr = Box("hdlr", Bytes(25, b => Encoding.ASCII.GetBytes(handler, b.AsSpan(8))));
        var stsd = Box("stsd", Bytes(24, b => { b[7] = 1; b[11] = 16; Encoding.ASCII.GetBytes(codec, b.AsSpan(12)); }));
        var mdia = Box("mdia", mdhd, hdlr, Box("minf", Box("stbl", stsd)));
        var parts = new List<byte[]> { tkhd };
        if (chapterRef is { } c) parts.Add(Box("tref", Box("chap", Bytes(4, b => BinaryPrimitives.WriteUInt32BigEndian(b, c)))));
        parts.Add(mdia);
        if (name is not null) parts.Add(Box("udta", Box("name", Encoding.UTF8.GetBytes(name))));
        return Box("trak", parts.ToArray());
    }

    [Fact]
    public void Reads_mp4_subtitle_tracks_and_skips_chapter_tracks()
    {
        var moov = Box("moov",
            Mp4Track(1, "vide", "avc1", "und", chapterRef: 4),
            Mp4Track(2, "sbtl", "tx3g", "spa"),
            Mp4Track(3, "subt", "wvtt", "eng", name: "English SDH"),
            Mp4Track(4, "text", "text", "eng")); // chapters
        // moov at the end, after the media data, as many encoders write it.
        var file = Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\0\0")).Concat(Box("mdat", new byte[500])).Concat(moov).ToArray();

        var result = Read(file);

        Assert.Equal(["spa", "eng"], result.Select(t => t.Language));
        Assert.Equal("tx3g", result[0].Codec);
        Assert.True(result[1].HearingImpaired);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 })]
    [InlineData(new byte[] { 0, 0, 0, 0x7F, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0 })]
    public void Unknown_or_damaged_files_give_no_tracks(byte[] data) => Assert.Empty(Read(data));

    [Fact]
    public void Missing_file_gives_no_tracks() => Assert.Empty(EmbeddedSubtitles.Read("/nonexistent/video.mkv"));
}
