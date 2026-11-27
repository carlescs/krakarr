using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Krakarr.Subtitles.Sync;

/// <summary>A subtitle cue's start and end, in milliseconds.</summary>
public readonly record struct Cue(double Start, double End);

/// <summary>
/// Reads and rewrites the timestamps of text subtitles (SubRip, WebVTT, SubStation Alpha) line by line, without
/// loading the file whole. Everything but the timestamps is copied byte for byte: files are read as Latin-1 (one
/// char per byte, so UTF-8 or any legacy code page passes through untouched) unless they start with a UTF-16 BOM.
/// </summary>
public static partial class SubtitleTiming
{
    /// <summary>Longest subtitle we rewrite; real ones are well under 1 MB.</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    [GeneratedRegex(@"(?<h1>\d+:)?(?<m1>\d{1,2}):(?<s1>\d{2})(?<sep1>[,.])(?<f1>\d{1,3})(?<arrow>\s*-->\s*)(?<h2>\d+:)?(?<m2>\d{1,2}):(?<s2>\d{2})(?<sep2>[,.])(?<f2>\d{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex ArrowLine();

    [GeneratedRegex(@"^(?<head>(?:Dialogue|Comment):\s*[^,]*,)(?<start>\d+:\d{2}:\d{2}[.:]\d{1,3}),(?<end>\d+:\d{2}:\d{2}[.:]\d{1,3})(?<tail>,.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex AssLine();

    public static bool IsSupported(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa";

    /// <summary>The cues of a subtitle file, in file order.</summary>
    public static List<Cue> ReadCues(string path)
    {
        using var stream = OpenRead(path);
        return ReadCues(stream);
    }

    public static List<Cue> ReadCues(Stream stream)
    {
        var cues = new List<Cue>();
        using var reader = new StreamReader(stream, DetectEncoding(stream), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            if (AssLine().Match(line) is { Success: true } ass)
            {
                cues.Add(new Cue(ParseAss(ass.Groups["start"].Value), ParseAss(ass.Groups["end"].Value)));
            }
            else if (ArrowLine().Match(line) is { Success: true } m)
            {
                cues.Add(new Cue(Ms(m, "1"), Ms(m, "2")));
            }
        }

        return cues;
    }

    /// <summary>
    /// Rewrites every timestamp t of <paramref name="path"/> as t × <paramref name="ratio"/> + <paramref name="offsetMs"/>
    /// (clamped at zero), through a temporary file next to it that then replaces it. Returns how many cues changed.
    /// </summary>
    public static int Transform(string path, double offsetMs, double ratio = 1)
    {
        if (!IsSupported(path)) throw new NotSupportedException($"Can't retime {Path.GetExtension(path)} subtitles (SubRip, WebVTT and SubStation Alpha only)");
        var temp = path + ".krakarr-tmp";
        int count;
        try
        {
            using (var input = OpenRead(path))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                count = Transform(input, output, offsetMs, ratio);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        return count;
    }

    /// <summary>Copies <paramref name="input"/> to <paramref name="output"/> with every timestamp retimed. Returns how many cues changed.</summary>
    public static int Transform(Stream input, Stream output, double offsetMs, double ratio = 1)
    {
        var encoding = DetectEncoding(input);
        var newline = DetectNewline(input, encoding, out var endsWithNewline);
        double Map(double ms) => Math.Max(0, Math.Round(ms * ratio + offsetMs));

        var count = 0;
        using var reader = new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        // The reader skips a UTF-16 BOM; the writer puts it back (Latin-1 has none, and a UTF-8 BOM passes through as text).
        using var writer = new StreamWriter(output, encoding, bufferSize: 16 * 1024, leaveOpen: true);
        var first = true;
        while (reader.ReadLine() is { } line)
        {
            if (!first) writer.Write(newline);
            first = false;

            if (AssLine().Match(line) is { Success: true } ass)
            {
                var g = ass.Groups;
                writer.Write(g["head"].Value);
                writer.Write(FormatAss(Map(ParseAss(g["start"].Value))));
                writer.Write(',');
                writer.Write(FormatAss(Map(ParseAss(g["end"].Value))));
                writer.Write(g["tail"].Value);
                count++;
            }
            else if (ArrowLine().Match(line) is { Success: true } m)
            {
                var g = m.Groups;
                writer.Write(line.AsSpan(0, m.Index));
                writer.Write(Format(Map(Ms(m, "1")), g["h1"].Success, g["sep1"].Value[0], g["f1"].Length));
                writer.Write(g["arrow"].Value);
                writer.Write(Format(Map(Ms(m, "2")), g["h2"].Success, g["sep2"].Value[0], g["f2"].Length));
                writer.Write(line.AsSpan(m.Index + m.Length));
                count++;
            }
            else
            {
                writer.Write(line);
            }
        }

        if (endsWithNewline && !first) writer.Write(newline);
        return count;
    }

    private static FileStream OpenRead(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 16 * 1024);
        if (stream.Length > MaxBytes)
        {
            stream.Dispose();
            throw new InvalidDataException($"{Path.GetFileName(path)} is too big to be a subtitle");
        }

        return stream;
    }

    private static Encoding DetectEncoding(Stream stream)
    {
        if (!stream.CanSeek) return Encoding.Latin1;
        var start = stream.Position;
        Span<byte> bom = stackalloc byte[2];
        var read = stream.ReadAtLeast(bom, 2, throwOnEndOfStream: false);
        stream.Position = start;
        if (read == 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
        if (read == 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        return Encoding.Latin1;
    }

    /// <summary>The line break the file uses ("\r\n" or "\n"), from its first 8 KB, and whether its last line ends with one.</summary>
    private static string DetectNewline(Stream stream, Encoding encoding, out bool endsWithNewline)
    {
        endsWithNewline = true;
        if (!stream.CanSeek) return "\n";
        var start = stream.Position;
        var unit = encoding.CodePage is 1200 or 1201 ? 2 : 1;
        var newline = "\n";

        var buffer = new byte[8192];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var text = encoding.GetString(buffer, 0, read - read % unit);
        var lf = text.IndexOf('\n');
        if (lf > 0 && text[lf - 1] == '\r') newline = "\r\n";

        if (stream.Length - start >= unit)
        {
            var last = new byte[unit];
            stream.Position = stream.Length - unit;
            stream.ReadExactly(last);
            endsWithNewline = encoding.GetString(last) == "\n";
        }

        stream.Position = start;
        return newline;
    }

    private static double Ms(Match m, string n)
    {
        var g = m.Groups;
        var hours = g["h" + n].Success ? int.Parse(g["h" + n].ValueSpan[..^1], CultureInfo.InvariantCulture) : 0;
        var fraction = g["f" + n].Value;
        // "1,5" means 500 ms: scale the fraction by its number of digits.
        var ms = int.Parse(fraction, CultureInfo.InvariantCulture) * Math.Pow(10, 3 - fraction.Length);
        return ((hours * 60 + int.Parse(g["m" + n].Value, CultureInfo.InvariantCulture)) * 60
            + int.Parse(g["s" + n].Value, CultureInfo.InvariantCulture)) * 1000 + ms;
    }

    private static string Format(double ms, bool withHours, char separator, int fractionDigits)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        var hours = (int)t.TotalHours;
        var fraction = fractionDigits switch
        {
            1 => (t.Milliseconds / 100).ToString(CultureInfo.InvariantCulture),
            2 => (t.Milliseconds / 10).ToString("00", CultureInfo.InvariantCulture),
            _ => t.Milliseconds.ToString("000", CultureInfo.InvariantCulture),
        };
        // WebVTT may leave the hours out; keep that unless the time no longer fits.
        var head = withHours || hours > 0 ? hours.ToString("00", CultureInfo.InvariantCulture) + ":" : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{head}{t.Minutes:00}:{t.Seconds:00}{separator}{fraction}");
    }

    /// <summary>SubStation Alpha times: "h:mm:ss.cc" (centiseconds).</summary>
    private static double ParseAss(string value)
    {
        var parts = value.Split(':', '.');
        var fraction = parts[3];
        return ((int.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + int.Parse(parts[1], CultureInfo.InvariantCulture)) * 60
            + int.Parse(parts[2], CultureInfo.InvariantCulture)) * 1000
            + int.Parse(fraction, CultureInfo.InvariantCulture) * Math.Pow(10, 3 - fraction.Length);
    }

    private static string FormatAss(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(ms / 10) * 10);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }
}
