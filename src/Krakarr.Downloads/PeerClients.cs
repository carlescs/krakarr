using System.Text.RegularExpressions;

namespace Krakarr.Downloads;

/// <summary>Turns Azureus-style peer-id prefixes ("-qB4630-", "MO3002") into readable client names.</summary>
public static partial class PeerClients
{
    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        ["qB"] = "qBittorrent", ["TR"] = "Transmission", ["UT"] = "µTorrent", ["UM"] = "µTorrent Mac", ["UW"] = "µTorrent Web",
        ["LT"] = "libtorrent (Rasterbar)", ["lt"] = "libTorrent (rakshasa)", ["DE"] = "Deluge", ["AZ"] = "Vuze", ["BI"] = "BiglyBT",
        ["MO"] = "MonoTorrent", ["KT"] = "KTorrent", ["BC"] = "BitComet", ["BT"] = "BitTorrent", ["FD"] = "Free Download Manager",
        ["WW"] = "WebTorrent", ["WD"] = "WebTorrent Desktop", ["TL"] = "Tribler", ["XL"] = "Xunlei", ["SD"] = "Thunder", ["PI"] = "PicoTorrent",
        ["FW"] = "FrostWire", ["BF"] = "BitFlu", ["RT"] = "Retriever", ["LW"] = "LimeWire", ["SZ"] = "Shareaza", ["TT"] = "TuoTu",
    };

    /// <summary>Readable name for a short peer-id ("MO3002" → "MonoTorrent 3.0.0.2"); falls back to the input.</summary>
    public static string Describe(string shortId)
    {
        var m = ShortId().Match(shortId);
        if (!m.Success || !Codes.TryGetValue(m.Groups["code"].Value, out var name)) return shortId;
        var version = string.Join('.', m.Groups["ver"].Value.Select(c => char.IsAsciiDigit(c) ? c.ToString() : ((int)char.ToUpperInvariant(c) - 'A' + 10).ToString()));
        return $"{name} {version}";
    }

    [GeneratedRegex(@"^-?(?<code>[A-Za-z]{2})(?<ver>[0-9A-Za-z]{4})-?")]
    private static partial Regex ShortId();
}
