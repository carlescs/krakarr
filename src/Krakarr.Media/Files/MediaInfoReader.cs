using System.Buffers.Binary;
using System.Text;

namespace Krakarr.Media.Files;

/// <summary>Dynamic range of a video track, as far as the container headers tell.</summary>
public static class DynamicRange
{
    public const string Hdr10 = "HDR10";
    public const string Hlg = "HLG";
    public const string DolbyVision = "DV";
    /// <summary>Dolby Vision with an HDR10 base layer (profile 7/8.1): plays as HDR10 on other displays.</summary>
    public const string DolbyVisionHdr10 = "DV HDR10";
}

/// <summary>A video track. Codec is a display name ("HEVC", "AVC", "AV1"…); DynamicRange is null for SDR or unknown.</summary>
public sealed record VideoTrackInfo(string? Codec, int Width, int Height, int? BitDepth, string? DynamicRange);

/// <summary>An audio track. Language is the raw tag (ISO 639-2 or BCP 47), or null; Channels is 0 when unknown.</summary>
public sealed record AudioTrackInfo(string? Codec, int Channels, string? Language, string? Name);

/// <summary>What the container headers of a video file say about its tracks and length.</summary>
public sealed record MediaInfo(
    TimeSpan? Duration,
    IReadOnlyList<VideoTrackInfo> Video,
    IReadOnlyList<AudioTrackInfo> Audio,
    IReadOnlyList<EmbeddedSubtitleTrack> Subtitles)
{
    public static readonly MediaInfo Empty = new(null, [], [], []);

    public bool IsEmpty => Duration is null && Video.Count == 0 && Audio.Count == 0 && Subtitles.Count == 0;
}

/// <summary>
/// Reads the tracks and duration of Matroska/WebM and MP4/MOV files from their headers only, through a seekable stream.
/// Every read is bounded (no media data is loaded), so this is cheap even for large files on a NAS. Other containers,
/// or files whose headers can't be read, fall back to <c>ffprobe</c> when it is installed; otherwise
/// <see cref="MediaInfo.Empty"/>.
/// </summary>
public static class MediaInfoReader
{
    private const int MaxElementBytes = 1024 * 1024;
    private const int MaxStringBytes = 1024;
    private const int MaxDepth = 8;

    /// <summary>Reads a file; falls back to ffprobe when the headers can't be read and it is available.</summary>
    public static MediaInfo Read(string videoPath)
    {
        var info = ReadHeaders(videoPath);
        return info.IsEmpty && File.Exists(videoPath) ? Ffprobe.TryRead(videoPath) ?? info : info;
    }

    /// <summary>Reads only the container headers of a file (no ffprobe).</summary>
    public static MediaInfo ReadHeaders(string videoPath)
    {
        try
        {
            using var stream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MediaInfo.Empty;
        }
    }

    public static MediaInfo Read(Stream stream)
    {
        try
        {
            Span<byte> head = stackalloc byte[8];
            if (stream.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8) return MediaInfo.Empty;
            stream.Position = 0;
            if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0x1A45DFA3) return Matroska.Read(stream);
            var type = Encoding.ASCII.GetString(head[4..8]);
            if (type is "ftyp" or "moov" or "wide" or "free" or "mdat" or "skip") return Mp4.Read(stream);
            return MediaInfo.Empty;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or ArgumentException)
        {
            return MediaInfo.Empty;
        }
    }

    internal static string? Clean(string? s)
    {
        if (s is null) return null;
        s = s.TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }

    private static bool NameSays(string? name, params string[] words) =>
        name is not null && words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static bool IsHearingImpaired(string? name) => NameSays(name, "sdh", "hearing impaired", "(cc)", "[cc]");

    /// <summary>Transfer characteristics (ITU-T H.273) to a dynamic range: 16 = PQ (HDR10), 18 = HLG.</summary>
    private static string? FromTransfer(ulong transfer) => transfer switch
    {
        16 => DynamicRange.Hdr10,
        18 => DynamicRange.Hlg,
        _ => null,
    };

    private static string? WithDolbyVision(string? range, bool dolbyVision) =>
        !dolbyVision ? range : range == DynamicRange.Hdr10 ? DynamicRange.DolbyVisionHdr10 : DynamicRange.DolbyVision;

    /// <summary>Luma bit depth from an HEVC decoder configuration record (hvcC), or null.</summary>
    private static int? HevcBitDepth(ReadOnlySpan<byte> hvcC) => hvcC.Length > 17 && hvcC[0] == 1 ? (hvcC[17] & 0x07) + 8 : null;

    /// <summary>Bit depth implied by an AVC decoder configuration record (avcC): High 10 and above are 10-bit.</summary>
    private static int? AvcBitDepth(ReadOnlySpan<byte> avcC) => avcC.Length > 1 && avcC[0] == 1 ? avcC[1] is 110 or 122 or 244 ? 10 : 8 : null;

    private static string AppendAtmos(string codec, string? name) =>
        codec is "EAC3" or "TrueHD" && NameSays(name, "atmos") ? codec + " Atmos" : codec;

    // ---- Matroska / WebM ---------------------------------------------------------------------------

    private static class Matroska
    {
        private const uint Segment = 0x18538067, SeekHead = 0x114D9B74, Seek = 0x4DBB, SeekId = 0x53AB, SeekPosition = 0x53AC;
        private const uint Info = 0x1549A966, TimestampScale = 0x2AD7B1, DurationId = 0x4489;
        private const uint Tracks = 0x1654AE6B, TrackEntry = 0xAE, TrackType = 0x83, CodecId = 0x86, CodecPrivate = 0x63A2,
            Language = 0x22B59C, LanguageBcp47 = 0x22B59D, Name = 0x536E, FlagForced = 0x55AA, FlagHearingImpaired = 0x55AB,
            FlagEnabled = 0xB9;
        private const uint Video = 0xE0, PixelWidth = 0xB0, PixelHeight = 0xBA, Colour = 0x55B0, BitsPerChannel = 0x55B2,
            TransferCharacteristics = 0x55BA, BlockAdditionMapping = 0x41E4, BlockAddIdType = 0x41E7;
        private const uint Audio = 0xE1, Channels = 0x9F;
        private const uint Cluster = 0x1F43B675;
        private const ulong VideoTrack = 0x01, AudioTrack = 0x02, SubtitleTrack = 0x11;
        private const ulong UnknownSize = ulong.MaxValue;

        public static MediaInfo Read(Stream s)
        {
            // EBML header, then the Segment.
            var (id, size) = ReadHeader(s);
            Skip(s, size);
            (id, size) = ReadHeader(s);
            if (id != Segment) return MediaInfo.Empty;
            var segmentStart = s.Position;
            var segmentEnd = size == UnknownSize ? s.Length : Math.Min(s.Length, segmentStart + (long)size);

            byte[]? tracks = null, info = null;
            long? tracksAt = null, infoAt = null;
            while (s.Position < segmentEnd && (tracks is null || info is null))
            {
                (id, size) = ReadHeader(s);
                if (id == Tracks) tracks = Load(s, size);
                else if (id == Info) info = Load(s, size);
                else if (id == SeekHead && size != UnknownSize && size <= MaxElementBytes)
                {
                    var seekHead = Load(s, size);
                    tracksAt ??= Find(seekHead, Tracks, segmentStart);
                    infoAt ??= Find(seekHead, Info, segmentStart);
                }
                // Media data starts: jump to the elements the SeekHead pointed at, if any.
                else if (id == Cluster || size == UnknownSize) break;
                else Skip(s, size);
            }

            tracks ??= LoadAt(s, tracksAt, Tracks, segmentEnd);
            info ??= LoadAt(s, infoAt, Info, segmentEnd);
            if (tracks is null && info is null) return MediaInfo.Empty;

            var (video, audio, subtitles) = tracks is null ? ([], [], []) : ParseTracks(tracks);
            return new MediaInfo(info is null ? null : ParseDuration(info), video, audio, subtitles);
        }

        private static byte[]? LoadAt(Stream s, long? at, uint expected, long segmentEnd)
        {
            if (at is not { } p || p <= 0 || p >= segmentEnd) return null;
            s.Position = p;
            var (id, size) = ReadHeader(s);
            return id == expected ? Load(s, size) : null;
        }

        private static long? Find(byte[] seekHead, uint wanted, long segmentStart)
        {
            foreach (var (id, body) in Children(seekHead))
            {
                if (id != Seek) continue;
                uint target = 0;
                ulong? position = null;
                foreach (var (cid, cbody) in Children(body))
                {
                    if (cid == SeekId) target = (uint)UInt(cbody);
                    else if (cid == SeekPosition) position = UInt(cbody);
                }

                if (target == wanted && position is { } p && p < long.MaxValue / 2) return segmentStart + (long)p;
            }

            return null;
        }

        private static TimeSpan? ParseDuration(byte[] info)
        {
            ulong scale = 1_000_000; // nanoseconds per tick, the Matroska default
            double? duration = null;
            foreach (var (id, body) in Children(info))
            {
                if (id == TimestampScale && UInt(body) is > 0 and var sc) scale = sc;
                else if (id == DurationId) duration = Float(body);
            }

            if (duration is not { } d || !double.IsFinite(d) || d <= 0) return null;
            var seconds = d * scale / 1e9;
            return seconds is > 0 and < 1e7 ? TimeSpan.FromSeconds(seconds) : null;
        }

        private static (List<VideoTrackInfo>, List<AudioTrackInfo>, List<EmbeddedSubtitleTrack>) ParseTracks(byte[] tracks)
        {
            var video = new List<VideoTrackInfo>();
            var audio = new List<AudioTrackInfo>();
            var subtitles = new List<EmbeddedSubtitleTrack>();
            foreach (var (id, entry) in Children(tracks))
            {
                if (id != TrackEntry) continue;
                ulong type = 0, channels = 0, width = 0, height = 0, bits = 0, transfer = 0;
                bool forced = false, hi = false, enabled = true, dolbyVision = false;
                string? language = null, bcp47 = null, name = null, codec = null;
                byte[]? codecPrivate = null;
                foreach (var (cid, body) in Children(entry))
                {
                    switch (cid)
                    {
                        case TrackType: type = UInt(body); break;
                        case CodecId: codec = Str(body); break;
                        case CodecPrivate: codecPrivate = body; break;
                        case Language: language = Str(body); break;
                        case LanguageBcp47: bcp47 = Str(body); break;
                        case Name: name = Str(body); break;
                        case FlagForced: forced = UInt(body) != 0; break;
                        case FlagHearingImpaired: hi = UInt(body) != 0; break;
                        case FlagEnabled: enabled = UInt(body) != 0; break;
                        case BlockAdditionMapping:
                            foreach (var (mid, mbody) in Children(body))
                            {
                                // 'dvcC' / 'dvvC': Dolby Vision configuration.
                                if (mid == BlockAddIdType && UInt(mbody) is 0x64766343 or 0x64767643) dolbyVision = true;
                            }
                            break;
                        case Video:
                            foreach (var (vid, vbody) in Children(body))
                            {
                                if (vid == PixelWidth) width = UInt(vbody);
                                else if (vid == PixelHeight) height = UInt(vbody);
                                else if (vid == Colour)
                                {
                                    foreach (var (kid, kbody) in Children(vbody))
                                    {
                                        if (kid == BitsPerChannel) bits = UInt(kbody);
                                        else if (kid == TransferCharacteristics) transfer = UInt(kbody);
                                    }
                                }
                            }
                            break;
                        case Audio:
                            foreach (var (aid, abody) in Children(body))
                            {
                                if (aid == Channels) channels = UInt(abody);
                            }
                            break;
                    }
                }

                if (!enabled) continue;
                // The Matroska default language is English when the element is absent; "und" means unknown.
                var lang = bcp47 ?? language ?? "eng";
                if (lang.Equals("und", StringComparison.OrdinalIgnoreCase)) lang = null;
                switch (type)
                {
                    case VideoTrack:
                        var videoCodec = VideoCodec(codec);
                        int? bitDepth = bits is > 0 and <= 16 ? (int)bits
                            : videoCodec == "HEVC" && codecPrivate is not null ? HevcBitDepth(codecPrivate)
                            : videoCodec == "AVC" && codecPrivate is not null ? AvcBitDepth(codecPrivate)
                            : null;
                        video.Add(new VideoTrackInfo(videoCodec, (int)Math.Min(width, 65535), (int)Math.Min(height, 65535), bitDepth,
                            WithDolbyVision(FromTransfer(transfer), dolbyVision)));
                        break;
                    case AudioTrack:
                        audio.Add(new AudioTrackInfo(AudioCodec(codec, name), (int)Math.Min(channels, 32), lang, name));
                        break;
                    case SubtitleTrack:
                        subtitles.Add(new EmbeddedSubtitleTrack(lang, name, codec, forced || NameSays(name, "forced"), hi || IsHearingImpaired(name)));
                        break;
                }
            }

            return (video, audio, subtitles);
        }

        private static string? VideoCodec(string? id) => id switch
        {
            null => null,
            "V_MPEGH/ISO/HEVC" => "HEVC",
            "V_MPEG4/ISO/AVC" => "AVC",
            "V_AV1" => "AV1",
            "V_VP9" => "VP9",
            "V_VP8" => "VP8",
            "V_MPEG2" or "V_MPEG1" => "MPEG-2",
            "V_MS/VFW/FOURCC" => "VFW",
            _ when id.StartsWith("V_MPEG4/ISO/", StringComparison.Ordinal) => "MPEG-4",
            _ when id.StartsWith("V_REAL", StringComparison.Ordinal) => "RealVideo",
            _ => id.StartsWith("V_", StringComparison.Ordinal) ? id[2..] : id,
        };

        private static string? AudioCodec(string? id, string? name)
        {
            string? codec = id switch
            {
                null => null,
                "A_EAC3" => "EAC3",
                "A_AC3" or "A_AC3/BSID9" or "A_AC3/BSID10" => "AC3",
                "A_TRUEHD" => "TrueHD",
                "A_DTS" => NameSays(name, "dts-hd ma", "dts-hd master", "dts:x", "dts-x") ? "DTS-HD MA" : "DTS",
                "A_DTS/EXPRESS" or "A_DTS/LOSSLESS" => "DTS-HD MA",
                "A_FLAC" => "FLAC",
                "A_OPUS" => "Opus",
                "A_VORBIS" => "Vorbis",
                "A_MPEG/L3" => "MP3",
                "A_MPEG/L2" => "MP2",
                "A_ALAC" => "ALAC",
                _ when id.StartsWith("A_AAC", StringComparison.Ordinal) => "AAC",
                _ when id.StartsWith("A_PCM", StringComparison.Ordinal) => "PCM",
                _ => id.StartsWith("A_", StringComparison.Ordinal) ? id[2..] : id,
            };
            return codec is null ? null : AppendAtmos(codec, name);
        }

        private static IEnumerable<(uint Id, byte[] Body)> Children(byte[] data)
        {
            var pos = 0;
            while (pos < data.Length)
            {
                if (!TryVint(data, ref pos, keepMarker: true, out var id)) yield break;
                if (!TryVint(data, ref pos, keepMarker: false, out var size)) yield break;
                if (size > (ulong)(data.Length - pos)) yield break;
                yield return ((uint)id, data.AsSpan(pos, (int)size).ToArray());
                pos += (int)size;
            }
        }

        private static bool TryVint(byte[] data, ref int pos, bool keepMarker, out ulong value)
        {
            value = 0;
            if (pos >= data.Length) return false;
            var first = data[pos];
            var length = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
            if (first == 0 || length > 8 || pos + length > data.Length) return false;
            value = keepMarker ? first : (ulong)(first & (0xFF >> length));
            for (var i = 1; i < length; i++) value = (value << 8) | data[pos + i];
            pos += length;
            return true;
        }

        private static (uint Id, ulong Size) ReadHeader(Stream s)
        {
            var first = ReadByte(s);
            var idLength = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
            if (first == 0 || idLength > 4) throw new InvalidDataException("Bad EBML id");
            uint id = first;
            for (var i = 1; i < idLength; i++) id = (id << 8) | ReadByte(s);

            first = ReadByte(s);
            var sizeLength = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
            if (first == 0 || sizeLength > 8) throw new InvalidDataException("Bad EBML size");
            var size = (ulong)(first & (0xFF >> sizeLength));
            var allOnes = size == (ulong)(0xFF >> sizeLength);
            for (var i = 1; i < sizeLength; i++)
            {
                var b = ReadByte(s);
                allOnes &= b == 0xFF;
                size = (size << 8) | b;
            }

            return (id, allOnes ? UnknownSize : size);
        }

        private static byte ReadByte(Stream s) => s.ReadByte() is var b and >= 0 ? (byte)b : throw new EndOfStreamException();

        private static void Skip(Stream s, ulong size)
        {
            if (size == UnknownSize || size > (ulong)(s.Length - s.Position)) throw new InvalidDataException("Bad element size");
            s.Position += (long)size;
        }

        private static byte[] Load(Stream s, ulong size)
        {
            if (size == UnknownSize || size > MaxElementBytes) throw new InvalidDataException("Element too large");
            var buffer = new byte[size];
            s.ReadExactly(buffer);
            return buffer;
        }

        private static ulong UInt(byte[] body)
        {
            ulong v = 0;
            foreach (var b in body.AsSpan(0, Math.Min(body.Length, 8))) v = (v << 8) | b;
            return v;
        }

        private static double? Float(byte[] body) => body.Length switch
        {
            4 => BinaryPrimitives.ReadSingleBigEndian(body),
            8 => BinaryPrimitives.ReadDoubleBigEndian(body),
            _ => null,
        };

        private static string? Str(byte[] body) => Clean(Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, MaxStringBytes)));
    }

    // ---- MP4 / QuickTime ---------------------------------------------------------------------------

    private static class Mp4
    {
        private sealed class Track
        {
            public uint Id;
            public string? Handler;
            public string? Codec;
            public string? Language;
            public string? ExtendedLanguage;
            public string? Name;
            public int Width, Height, Channels;
            public int? BitDepth;
            public ulong Transfer;
            public bool DolbyVision;
            public string? AudioCodecDetail;
            public List<uint> ChapterRefs { get; } = [];
        }

        public static MediaInfo Read(Stream s)
        {
            var tracks = new List<Track>();
            TimeSpan? duration = null;
            var found = false;
            foreach (var (type, start, end) in Boxes(s, 0, s.Length))
            {
                if (type != "moov") continue;
                foreach (var (ctype, cstart, cend) in Boxes(s, start, end))
                {
                    if (ctype == "mvhd") duration = ParseMvhd(Load(s, cstart, cend, 32));
                    if (ctype != "trak") continue;
                    var track = new Track();
                    ParseTrak(s, cstart, cend, track);
                    tracks.Add(track);
                }

                found = true;
                break;
            }

            if (!found) return MediaInfo.Empty;
            // QuickTime chapters are text tracks referenced through tref/chap; they are not subtitles.
            var chapters = tracks.SelectMany(t => t.ChapterRefs).ToHashSet();
            var video = tracks.Where(t => t.Handler == "vide" && VideoCodec(t.Codec) is not null)
                .Select(t => new VideoTrackInfo(VideoCodec(t.Codec), t.Width, t.Height, t.BitDepth,
                    WithDolbyVision(FromTransfer(t.Transfer), t.DolbyVision || t.Codec is "dvh1" or "dvhe" or "dav1")))
                .ToList();
            var audio = tracks.Where(t => t.Handler == "soun")
                .Select(t => new AudioTrackInfo(t.AudioCodecDetail ?? AudioCodec(t.Codec, t.Name), t.Channels, t.ExtendedLanguage ?? t.Language, t.Name))
                .ToList();
            var subtitles = tracks
                .Where(t => IsSubtitle(t) && !chapters.Contains(t.Id))
                .Select(t => new EmbeddedSubtitleTrack(t.ExtendedLanguage ?? t.Language, t.Name, t.Codec,
                    NameSays(t.Name, "forced"), t.Handler == "clcp" || IsHearingImpaired(t.Name)))
                .ToList();
            return new MediaInfo(duration, video, audio, subtitles);
        }

        private static TimeSpan? ParseMvhd(byte[] mvhd)
        {
            if (mvhd.Length < 4) return null;
            uint timescale;
            ulong duration;
            if (mvhd[0] == 1)
            {
                if (mvhd.Length < 32) return null;
                timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(20));
                duration = BinaryPrimitives.ReadUInt64BigEndian(mvhd.AsSpan(24));
            }
            else
            {
                if (mvhd.Length < 20) return null;
                timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12));
                duration = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(16));
            }

            if (timescale == 0 || duration is 0 or uint.MaxValue or ulong.MaxValue) return null;
            var seconds = (double)duration / timescale;
            return seconds < 1e7 ? TimeSpan.FromSeconds(seconds) : null;
        }

        private static bool IsSubtitle(Track t) =>
            t.Handler is "sbtl" or "subt" or "clcp" || (t.Handler == "text" && t.Codec is "tx3g" or "wvtt" or "stpp" or "text");

        private static void ParseTrak(Stream s, long start, long end, Track track, int depth = 0)
        {
            if (depth > MaxDepth) return;
            foreach (var (type, cstart, cend) in Boxes(s, start, end))
            {
                switch (type)
                {
                    case "tkhd":
                        // Subtitle tracks are often flagged disabled (not shown by default), so the enabled flag is ignored.
                        var tkhd = Load(s, cstart, cend, 32);
                        if (tkhd.Length < 24) break;
                        track.Id = BinaryPrimitives.ReadUInt32BigEndian(tkhd.AsSpan(tkhd[0] == 1 ? 20 : 12));
                        break;
                    case "tref":
                        foreach (var (rtype, rstart, rend) in Boxes(s, cstart, cend))
                        {
                            if (rtype != "chap") continue;
                            var refs = Load(s, rstart, rend, 256);
                            for (var i = 0; i + 4 <= refs.Length; i += 4) track.ChapterRefs.Add(BinaryPrimitives.ReadUInt32BigEndian(refs.AsSpan(i)));
                        }
                        break;
                    case "mdhd":
                        var mdhd = Load(s, cstart, cend, 40);
                        var at = mdhd.Length > 0 && mdhd[0] == 1 ? 32 : 20;
                        if (mdhd.Length >= at + 2) track.Language = PackedLanguage(BinaryPrimitives.ReadUInt16BigEndian(mdhd.AsSpan(at)));
                        break;
                    case "elng":
                        var elng = Load(s, cstart, cend, 64);
                        if (elng.Length > 4) track.ExtendedLanguage = Clean(Encoding.ASCII.GetString(elng, 4, elng.Length - 4));
                        break;
                    case "hdlr":
                        var hdlr = Load(s, cstart, cend, 12);
                        if (hdlr.Length >= 12) track.Handler = Encoding.ASCII.GetString(hdlr, 8, 4);
                        break;
                    case "stsd":
                        ParseStsd(s, cstart, cend, track);
                        break;
                    case "udta":
                        foreach (var (utype, ustart, uend) in Boxes(s, cstart, cend))
                        {
                            if (utype == "name") track.Name = Clean(Encoding.UTF8.GetString(Load(s, ustart, uend, MaxStringBytes)));
                        }
                        break;
                    case "mdia" or "minf" or "stbl":
                        ParseTrak(s, cstart, cend, track, depth + 1);
                        break;
                }
            }
        }

        /// <summary>The first sample entry: its codec, and for video/audio its size, channels and configuration boxes.</summary>
        private static void ParseStsd(Stream s, long start, long end, Track track)
        {
            // Full box header (4) + entry count (4), then the first sample entry box.
            if (end - start < 16) return;
            var entry = Boxes(s, start + 8, end).FirstOrDefault();
            if (entry.Type is null) return;
            track.Codec = entry.Type;

            var head = Load(s, entry.Start, entry.End, 64);
            long childrenAt;
            switch (track.Handler)
            {
                case "vide":
                    // reserved(6) data_ref(2) pre_defined(2) reserved(2) pre_defined(12) width(2) height(2) ... = 78 bytes.
                    if (head.Length < 28) return;
                    track.Width = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(24));
                    track.Height = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(26));
                    childrenAt = entry.Start + 78;
                    break;
                case "soun":
                    // reserved(6) data_ref(2) version(2) revision(2) vendor(4) channels(2) sample_size(2) ... = 28 bytes (v0).
                    if (head.Length < 18) return;
                    var version = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(8));
                    track.Channels = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(16));
                    childrenAt = entry.Start + version switch { 1 => 44, 2 => 64, _ => 28 };
                    if (version == 2 && head.Length >= 48) track.Channels = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(40));
                    break;
                default:
                    return;
            }

            if (childrenAt >= entry.End) return;
            foreach (var (type, cstart, cend) in Boxes(s, childrenAt, entry.End))
            {
                switch (type)
                {
                    case "hvcC":
                        track.BitDepth = HevcBitDepth(Load(s, cstart, cend, 32));
                        break;
                    case "avcC":
                        track.BitDepth = AvcBitDepth(Load(s, cstart, cend, 8));
                        break;
                    case "av1C":
                        // marker/version(1) seq_profile(3)+level(5) tier(1) high_bitdepth(1) twelve_bit(1) ...
                        var av1 = Load(s, cstart, cend, 4);
                        if (av1.Length >= 3) track.BitDepth = (av1[2] & 0x40) == 0 ? 8 : (av1[2] & 0x20) != 0 ? 12 : 10;
                        break;
                    case "vpcC":
                        // version(1) flags(3) profile(1) level(1) bitDepth(4 bits) chroma(3) range(1) primaries(1) transfer(1)
                        var vp = Load(s, cstart, cend, 12);
                        if (vp.Length >= 10)
                        {
                            track.BitDepth = vp[6] >> 4;
                            track.Transfer = vp[8];
                        }
                        break;
                    case "colr":
                        var colr = Load(s, cstart, cend, 16);
                        if (colr.Length >= 10 && Encoding.ASCII.GetString(colr, 0, 4) is "nclx" or "nclc")
                        {
                            track.Transfer = BinaryPrimitives.ReadUInt16BigEndian(colr.AsSpan(6));
                        }
                        break;
                    case "dvcC" or "dvvC" or "dvwC":
                        track.DolbyVision = true;
                        break;
                    case "dac3":
                        var dac3 = Load(s, cstart, cend, 3);
                        if (dac3.Length >= 2) track.Channels = Ac3Channels((dac3[1] >> 3) & 0x07, (dac3[1] >> 2) & 0x01);
                        break;
                    case "dec3":
                        var dec3 = Load(s, cstart, cend, 8);
                        if (dec3.Length >= 5)
                        {
                            // data_rate(13) num_ind_sub(3), then fscod(2) bsid(5) reserved(1) asvc(1) bsmod(3) acmod(3) lfeon(1) reserved(3) num_dep_sub(4) chan_loc(9)
                            var acmod = (dec3[3] >> 1) & 0x07;
                            var lfe = dec3[3] & 0x01;
                            var channels = Ac3Channels(acmod, lfe);
                            var dependent = (dec3[4] >> 1) & 0x0F;
                            // A dependent substream adds the back surrounds of 7.1.
                            if (dependent > 0) channels += 2;
                            track.Channels = channels;
                            // Atmos (JOC) is flagged after the first substream: reserved(7) flag_ec3_extension_type_a(1).
                            var extensionAt = dependent > 0 ? 6 : 5;
                            if (dec3.Length > extensionAt && (dec3[extensionAt] & 0x01) != 0) track.AudioCodecDetail = "EAC3 Atmos";
                        }
                        break;
                }
            }
        }

        private static int Ac3Channels(int acmod, int lfe) => (acmod switch { 0 => 2, 1 => 1, 2 => 2, 3 => 3, 4 => 3, 5 => 4, 6 => 4, _ => 5 }) + lfe;

        private static string? VideoCodec(string? fourcc) => fourcc switch
        {
            "hvc1" or "hev1" or "dvh1" or "dvhe" => "HEVC",
            "avc1" or "avc3" or "dva1" or "dvav" => "AVC",
            "av01" or "dav1" => "AV1",
            "vp09" => "VP9",
            "vp08" => "VP8",
            "mp4v" => "MPEG-4",
            "mp2v" or "m2v1" => "MPEG-2",
            "apch" or "apcn" or "apcs" or "apco" or "ap4h" or "ap4x" => "ProRes",
            _ => null,
        };

        private static string? AudioCodec(string? fourcc, string? name)
        {
            string? codec = fourcc switch
            {
                null => null,
                "mp4a" => "AAC",
                "ac-3" => "AC3",
                "ec-3" => "EAC3",
                "ac-4" => "AC4",
                "mlpa" => "TrueHD",
                "dtsc" or "dtse" => "DTS",
                "dtsh" or "dtsl" or "dtsx" => "DTS-HD MA",
                "Opus" => "Opus",
                "fLaC" => "FLAC",
                "alac" => "ALAC",
                ".mp3" => "MP3",
                "lpcm" or "sowt" or "twos" or "in24" or "in32" or "fl32" or "fl64" => "PCM",
                _ => fourcc.Trim(),
            };
            return codec is null ? null : AppendAtmos(codec, name);
        }

        private static string? PackedLanguage(ushort packed)
        {
            if (packed is 0 or 0x7FFF) return null;
            Span<char> c = [(char)(((packed >> 10) & 0x1F) + 0x60), (char)(((packed >> 5) & 0x1F) + 0x60), (char)((packed & 0x1F) + 0x60)];
            foreach (var ch in c) if (ch is < 'a' or > 'z') return null;
            var code = new string(c);
            return code == "und" ? null : code;
        }

        /// <summary>Child boxes in [start, end): type and the range of the box's payload.</summary>
        private static IEnumerable<(string Type, long Start, long End)> Boxes(Stream s, long start, long end)
        {
            var header = new byte[16];
            var pos = start;
            while (pos + 8 <= end)
            {
                s.Position = pos;
                s.ReadExactly(header, 0, 8);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header, 4, 4);
                var headerLength = 8;
                if (size == 1)
                {
                    s.ReadExactly(header, 8, 8);
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                    headerLength = 16;
                }
                else if (size == 0)
                {
                    size = end - pos;
                }

                if (size < headerLength || pos + size > end) yield break;
                yield return (type, pos + headerLength, pos + size);
                pos += size;
            }
        }

        private static byte[] Load(Stream s, long start, long end, int max)
        {
            var buffer = new byte[(int)Math.Max(0, Math.Min(end - start, max))];
            s.Position = start;
            s.ReadExactly(buffer);
            return buffer;
        }
    }
}
