namespace Krakarr.Core.Domain;

public enum QualitySource
{
    Unknown = 0,
    Cam,
    Telesync,
    Telecine,
    Workprint,
    Tv,
    Dvd,
    WebRip,
    WebDl,
    Bluray,
    Remux,
}

public sealed record Quality(int Id, string Name, QualitySource Source, int Resolution, int Weight);

/// <summary>Static catalogue of known qualities, ordered from worst to best by <see cref="Quality.Weight"/>.</summary>
public static class Qualities
{
    public static readonly Quality Unknown = new(0, "Unknown", QualitySource.Unknown, 0, 0);
    public static readonly Quality Cam = new(1, "CAM", QualitySource.Cam, 0, 1);
    public static readonly Quality Telesync = new(2, "TELESYNC", QualitySource.Telesync, 0, 2);
    public static readonly Quality Telecine = new(3, "TELECINE", QualitySource.Telecine, 0, 3);
    public static readonly Quality Workprint = new(4, "WORKPRINT", QualitySource.Workprint, 0, 4);
    public static readonly Quality Sdtv = new(5, "SDTV", QualitySource.Tv, 480, 5);
    public static readonly Quality Dvd = new(6, "DVD", QualitySource.Dvd, 480, 6);
    public static readonly Quality WebRip480 = new(7, "WEBRip-480p", QualitySource.WebRip, 480, 7);
    public static readonly Quality WebDl480 = new(8, "WEBDL-480p", QualitySource.WebDl, 480, 8);
    public static readonly Quality Bluray480 = new(9, "Bluray-480p", QualitySource.Bluray, 480, 9);
    public static readonly Quality Hdtv720 = new(10, "HDTV-720p", QualitySource.Tv, 720, 10);
    public static readonly Quality WebRip720 = new(11, "WEBRip-720p", QualitySource.WebRip, 720, 11);
    public static readonly Quality WebDl720 = new(12, "WEBDL-720p", QualitySource.WebDl, 720, 12);
    public static readonly Quality Bluray720 = new(13, "Bluray-720p", QualitySource.Bluray, 720, 13);
    public static readonly Quality Hdtv1080 = new(14, "HDTV-1080p", QualitySource.Tv, 1080, 14);
    public static readonly Quality WebRip1080 = new(15, "WEBRip-1080p", QualitySource.WebRip, 1080, 15);
    public static readonly Quality WebDl1080 = new(16, "WEBDL-1080p", QualitySource.WebDl, 1080, 16);
    public static readonly Quality Bluray1080 = new(17, "Bluray-1080p", QualitySource.Bluray, 1080, 17);
    public static readonly Quality Remux1080 = new(18, "Remux-1080p", QualitySource.Remux, 1080, 18);
    public static readonly Quality Hdtv2160 = new(19, "HDTV-2160p", QualitySource.Tv, 2160, 19);
    public static readonly Quality WebRip2160 = new(20, "WEBRip-2160p", QualitySource.WebRip, 2160, 20);
    public static readonly Quality WebDl2160 = new(21, "WEBDL-2160p", QualitySource.WebDl, 2160, 21);
    public static readonly Quality Bluray2160 = new(22, "Bluray-2160p", QualitySource.Bluray, 2160, 22);
    public static readonly Quality Remux2160 = new(23, "Remux-2160p", QualitySource.Remux, 2160, 23);

    public static IReadOnlyList<Quality> All { get; } =
    [
        Unknown, Cam, Telesync, Telecine, Workprint, Sdtv, Dvd,
        WebRip480, WebDl480, Bluray480,
        Hdtv720, WebRip720, WebDl720, Bluray720,
        Hdtv1080, WebRip1080, WebDl1080, Bluray1080, Remux1080,
        Hdtv2160, WebRip2160, WebDl2160, Bluray2160, Remux2160,
    ];

    private static readonly Dictionary<int, Quality> ById = All.ToDictionary(q => q.Id);

    public static Quality FindById(int id) => ById.GetValueOrDefault(id, Unknown);

    public static Quality Find(QualitySource source, int resolution)
    {
        if (source == QualitySource.Unknown && resolution == 0)
        {
            return Unknown;
        }

        // Resolution-less sources.
        switch (source)
        {
            case QualitySource.Cam: return Cam;
            case QualitySource.Telesync: return Telesync;
            case QualitySource.Telecine: return Telecine;
            case QualitySource.Workprint: return Workprint;
            case QualitySource.Dvd when resolution is 0 or <= 576: return Dvd;
        }

        var res = resolution switch
        {
            >= 2160 => 2160,
            >= 1080 => 1080,
            >= 720 => 720,
            > 0 => 480,
            _ => 0,
        };

        if (source == QualitySource.Unknown)
        {
            // Resolution known but source unknown: assume a web release, the most common case today.
            source = res == 480 ? QualitySource.Tv : QualitySource.WebDl;
        }

        if (res == 0)
        {
            res = source switch
            {
                QualitySource.Tv => 480,
                QualitySource.Remux => 1080,
                QualitySource.Bluray => 720,
                _ => 480,
            };
        }

        if (source == QualitySource.Tv && res == 480) return Sdtv;
        if (source == QualitySource.Remux && res < 1080) res = 1080;
        if (source == QualitySource.Dvd) source = QualitySource.Bluray;

        return All.FirstOrDefault(q => q.Source == source && q.Resolution == res) ?? Unknown;
    }
}
