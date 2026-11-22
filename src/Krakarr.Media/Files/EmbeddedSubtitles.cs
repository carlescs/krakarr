using System.Buffers.Binary;
using System.Text;

namespace Krakarr.Media.Files;

/// <summary>A subtitle track stored inside a video container. Language is the raw tag (ISO 639-2 or BCP 47), or null.</summary>
public sealed record EmbeddedSubtitleTrack(string? Language, string? Name, string? Codec, bool Forced, bool HearingImpaired);

/// <summary>
/// Lists the subtitle tracks of Matroska/WebM and MP4/MOV files by reading their track headers only. Reads are bounded
/// (no media data is loaded), so this is cheap even for large files on a NAS. Unknown or damaged files give an empty list.
/// </summary>
public static class EmbeddedSubtitles
{
    private const int MaxTracksBytes = 1024 * 1024;
    private const int MaxStringBytes = 1024;
    private const int MaxDepth = 8;

    public static IReadOnlyList<EmbeddedSubtitleTrack> Read(string videoPath)
    {
        try
        {
            using var stream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, FileOptions.RandomAccess);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<EmbeddedSubtitleTrack> Read(Stream stream)
    {
        try
        {
            Span<byte> head = stackalloc byte[8];
            if (stream.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8) return [];
            stream.Position = 0;
            if (BinaryPrimitives.ReadUInt32BigEndian(head) == 0x1A45DFA3) return Matroska.Read(stream);
            var type = Encoding.ASCII.GetString(head[4..8]);
            if (type is "ftyp" or "moov" or "wide" or "free" or "mdat" or "skip") return Mp4.Read(stream);
            return [];
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        s = s.TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }

    private static bool NameSays(string? name, params string[] words) =>
        name is not null && words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    // ---- Matroska / WebM ---------------------------------------------------------------------------

    private static class Matroska
    {
        private const uint Segment = 0x18538067, SeekHead = 0x114D9B74, Seek = 0x4DBB, SeekId = 0x53AB, SeekPosition = 0x53AC;
        private const uint Tracks = 0x1654AE6B, TrackEntry = 0xAE, TrackType = 0x83, CodecId = 0x86, Language = 0x22B59C,
            LanguageBcp47 = 0x22B59D, Name = 0x536E, FlagForced = 0x55AA, FlagHearingImpaired = 0x55AB, FlagEnabled = 0xB9;
        private const uint Cluster = 0x1F43B675;
        private const ulong SubtitleTrack = 0x11;
        private const ulong UnknownSize = ulong.MaxValue;

        public static IReadOnlyList<EmbeddedSubtitleTrack> Read(Stream s)
        {
            // EBML header, then the Segment.
            var (id, size) = ReadHeader(s);
            Skip(s, size);
            (id, size) = ReadHeader(s);
            if (id != Segment) return [];
            var segmentStart = s.Position;
            var segmentEnd = size == UnknownSize ? s.Length : Math.Min(s.Length, segmentStart + (long)size);

            long? tracksAt = null;
            while (s.Position < segmentEnd)
            {
                (id, size) = ReadHeader(s);
                if (id == Tracks) return ParseTracks(Load(s, size));
                if (id == SeekHead && size != UnknownSize && size <= MaxTracksBytes)
                {
                    tracksAt ??= FindTracks(Load(s, size), segmentStart);
                    continue;
                }

                // Media data starts: jump to the Tracks element the SeekHead pointed at, if any.
                if (id == Cluster || size == UnknownSize) break;
                Skip(s, size);
            }

            if (tracksAt is not { } at || at <= 0 || at >= segmentEnd) return [];
            s.Position = at;
            (id, size) = ReadHeader(s);
            return id == Tracks ? ParseTracks(Load(s, size)) : [];
        }

        private static long? FindTracks(byte[] seekHead, long segmentStart)
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

                if (target == Tracks && position is { } p && p < long.MaxValue / 2) return segmentStart + (long)p;
            }

            return null;
        }

        private static IReadOnlyList<EmbeddedSubtitleTrack> ParseTracks(byte[] tracks)
        {
            var result = new List<EmbeddedSubtitleTrack>();
            foreach (var (id, entry) in Children(tracks))
            {
                if (id != TrackEntry) continue;
                ulong type = 0;
                bool forced = false, hi = false, enabled = true;
                string? language = null, bcp47 = null, name = null, codec = null;
                foreach (var (cid, body) in Children(entry))
                {
                    switch (cid)
                    {
                        case TrackType: type = UInt(body); break;
                        case CodecId: codec = Str(body); break;
                        case Language: language = Str(body); break;
                        case LanguageBcp47: bcp47 = Str(body); break;
                        case Name: name = Str(body); break;
                        case FlagForced: forced = UInt(body) != 0; break;
                        case FlagHearingImpaired: hi = UInt(body) != 0; break;
                        case FlagEnabled: enabled = UInt(body) != 0; break;
                    }
                }

                if (type != SubtitleTrack || !enabled) continue;
                // The Matroska default language is English when the element is absent.
                result.Add(new EmbeddedSubtitleTrack(bcp47 ?? language ?? "eng", name, codec,
                    forced || NameSays(name, "forced"), hi || NameSays(name, "sdh", "hearing impaired", "(cc)", "[cc]")));
            }

            return result;
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
            if (size == UnknownSize || size > MaxTracksBytes) throw new InvalidDataException("Tracks element too large");
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
            public List<uint> ChapterRefs { get; } = [];
        }

        public static IReadOnlyList<EmbeddedSubtitleTrack> Read(Stream s)
        {
            var tracks = new List<Track>();
            var found = false;
            foreach (var (type, start, end) in Boxes(s, 0, s.Length))
            {
                if (type != "moov") continue;
                foreach (var (ctype, cstart, cend) in Boxes(s, start, end))
                {
                    if (ctype != "trak") continue;
                    var track = new Track();
                    ParseTrak(s, cstart, cend, track);
                    tracks.Add(track);
                }

                found = true;
                break;
            }

            if (!found) return [];
            // QuickTime chapters are text tracks referenced through tref/chap; they are not subtitles.
            var chapters = tracks.SelectMany(t => t.ChapterRefs).ToHashSet();
            return tracks
                .Where(t => IsSubtitle(t) && !chapters.Contains(t.Id))
                .Select(t => new EmbeddedSubtitleTrack(t.ExtendedLanguage ?? t.Language, t.Name, t.Codec,
                    NameSays(t.Name, "forced"), t.Handler == "clcp" || NameSays(t.Name, "sdh", "hearing impaired", "(cc)", "[cc]")))
                .ToList();
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
                        var stsd = Load(s, cstart, cend, 16);
                        if (stsd.Length >= 16) track.Codec = Encoding.ASCII.GetString(stsd, 12, 4);
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

        private static string? PackedLanguage(ushort packed)
        {
            if (packed is 0 or 0x7FFF) return null;
            Span<char> c = [(char)(((packed >> 10) & 0x1F) + 0x60), (char)(((packed >> 5) & 0x1F) + 0x60), (char)((packed & 0x1F) + 0x60)];
            foreach (var ch in c) if (ch is < 'a' or > 'z') return null;
            return new string(c);
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
            var buffer = new byte[(int)Math.Min(end - start, max)];
            s.Position = start;
            s.ReadExactly(buffer);
            return buffer;
        }
    }
}
