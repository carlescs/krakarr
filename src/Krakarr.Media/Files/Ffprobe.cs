using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Krakarr.Media.Files;

/// <summary>
/// Fallback for containers the header reader doesn't know (AVI, MPEG-TS…): runs <c>ffprobe</c> when it is on the PATH.
/// The official image doesn't ship it, so this is a no-op there. Output is bounded and the process is killed after a timeout.
/// </summary>
internal static class Ffprobe
{
    private const int MaxOutputBytes = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<string?> Executable = new(Locate);

    public static bool IsAvailable => Executable.Value is not null;

    public static MediaInfo? TryRead(string path)
    {
        if (Executable.Value is not { } exe) return null;
        try
        {
            var start = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
                     {
                         "-v", "quiet", "-print_format", "json", "-show_entries",
                         "format=duration:stream=codec_type,codec_name,profile,width,height,channels,pix_fmt,bits_per_raw_sample,color_transfer:stream_tags=language,title:stream_disposition=forced,hearing_impaired:stream_side_data=side_data_type",
                         "--", path,
                     })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null) return null;
            var buffer = new byte[MaxOutputBytes];
            var readTask = process.StandardOutput.BaseStream.ReadAtLeastAsync(buffer, MaxOutputBytes, throwOnEndOfStream: false).AsTask();
            // Output that fills the buffer is cut off (and ffprobe would block on the full pipe): give up on it.
            if (!readTask.Wait(Timeout) || readTask.Result >= MaxOutputBytes || !process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            if (process.ExitCode != 0 || readTask.Result == 0) return null;
            return Parse(buffer.AsSpan(0, readTask.Result));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or AggregateException)
        {
            return null;
        }
    }

    internal static MediaInfo? Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        TimeSpan? duration = null;
        if (root.TryGetProperty("format", out var format) && Str(format, "duration") is { } d
            && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and < 1e7)
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        var video = new List<VideoTrackInfo>();
        var audio = new List<AudioTrackInfo>();
        var subtitles = new List<EmbeddedSubtitleTrack>();
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in streams.EnumerateArray())
            {
                var codec = Str(st, "codec_name");
                var tags = st.TryGetProperty("tags", out var t) ? t : default;
                var language = tags.ValueKind == JsonValueKind.Object ? Str(tags, "language") : null;
                if (language is "und") language = null;
                var title = tags.ValueKind == JsonValueKind.Object ? Str(tags, "title") : null;
                switch (Str(st, "codec_type"))
                {
                    case "video" when codec is not ("mjpeg" or "png" or "bmp"): // cover art
                        var dv = st.TryGetProperty("side_data_list", out var side) && side.ValueKind == JsonValueKind.Array
                            && side.EnumerateArray().Any(x => Str(x, "side_data_type")?.Contains("DOVI", StringComparison.OrdinalIgnoreCase) == true);
                        var range = Str(st, "color_transfer") switch { "smpte2084" => DynamicRange.Hdr10, "arib-std-b67" => DynamicRange.Hlg, _ => null };
                        if (dv) range = range == DynamicRange.Hdr10 ? DynamicRange.DolbyVisionHdr10 : DynamicRange.DolbyVision;
                        video.Add(new VideoTrackInfo(VideoCodec(codec), Int(st, "width"), Int(st, "height"), BitDepth(st), range));
                        break;
                    case "audio":
                        audio.Add(new AudioTrackInfo(AudioCodec(codec, Str(st, "profile")), Int(st, "channels"), language, title));
                        break;
                    case "subtitle":
                        var disposition = st.TryGetProperty("disposition", out var disp) ? disp : default;
                        subtitles.Add(new EmbeddedSubtitleTrack(language, title, codec,
                            disposition.ValueKind == JsonValueKind.Object && Int(disposition, "forced") == 1,
                            disposition.ValueKind == JsonValueKind.Object && Int(disposition, "hearing_impaired") == 1));
                        break;
                }
            }
        }

        var info = new MediaInfo(duration, video, audio, subtitles);
        return info.IsEmpty ? null : info;
    }

    private static int? BitDepth(JsonElement st)
    {
        if (Str(st, "bits_per_raw_sample") is { } b && int.TryParse(b, CultureInfo.InvariantCulture, out var bits) && bits is > 0 and <= 16) return bits;
        return Str(st, "pix_fmt") switch
        {
            null => null,
            var f when f.Contains("12le") || f.Contains("12be") => 12,
            var f when f.Contains("10le") || f.Contains("10be") || f == "p010le" => 10,
            _ => 8,
        };
    }

    private static string? VideoCodec(string? codec) => codec switch
    {
        null => null,
        "hevc" => "HEVC",
        "h264" => "AVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg2video" or "mpeg1video" => "MPEG-2",
        "mpeg4" or "msmpeg4v3" => "MPEG-4",
        "vc1" or "wmv3" => "VC-1",
        _ => codec.ToUpperInvariant(),
    };

    private static string? AudioCodec(string? codec, string? profile) => codec switch
    {
        null => null,
        "eac3" => "EAC3",
        "ac3" => "AC3",
        "truehd" => "TrueHD",
        "dts" => profile is not null && profile.Contains("MA", StringComparison.Ordinal) ? "DTS-HD MA" : "DTS",
        "aac" => "AAC",
        "flac" => "FLAC",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "mp3" => "MP3",
        "mp2" => "MP2",
        "alac" => "ALAC",
        _ when codec.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        _ => codec.ToUpperInvariant(),
    };

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string? Locate()
    {
        var name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
