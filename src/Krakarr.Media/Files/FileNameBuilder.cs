using System.Text.RegularExpressions;

namespace Krakarr.Media.Files;

/// <summary>Expands naming templates like "{Movie Title} ({Release Year}) [{Quality}]".</summary>
public static partial class FileNameBuilder
{
    public static string Build(string template, IReadOnlyDictionary<string, string?> tokens)
    {
        var result = TokenRegex().Replace(template, m =>
        {
            var key = m.Groups["token"].Value;
            return tokens.TryGetValue(key, out var value) ? value ?? string.Empty : m.Value;
        });

        // Remove empty brackets left behind by missing tokens, e.g. "[]" or "()".
        result = EmptyBrackets().Replace(result, string.Empty);
        return MultiSpace().Replace(SanitizeFileName(result), " ").Trim();
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*', '\\', '/']).ToHashSet();
        var chars = name.Select(c => c == ':' ? '-' : invalid.Contains(c) ? '\0' : c).Where(c => c != '\0').ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }

    [GeneratedRegex(@"\{(?<token>[^{}]+)\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\[\s*\]|\(\s*\)")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
