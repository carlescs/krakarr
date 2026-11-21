namespace Krakarr.Core.Domain;

/// <summary>Library titles a bulk action (table view multi-selection) applies to.</summary>
public sealed record BulkIdsRequest(IReadOnlyList<int> Ids);

/// <summary>Changes applied to every selected title; null fields stay as they are.</summary>
public sealed record BulkEditRequest(IReadOnlyList<int> Ids, bool? Monitored = null, int? QualityProfileId = null);

public sealed record BulkDeleteRequest(IReadOnlyList<int> Ids, bool DeleteFiles = false);

/// <param name="Affected">Titles the action applied to.</param>
/// <param name="Errors">Titles it could not apply to, with the reason.</param>
public sealed record BulkResult(int Affected, IReadOnlyList<string> Errors);

public static class BulkLimits
{
    /// <summary>Most titles one bulk request may name: keeps request bodies and id lists bounded.</summary>
    public const int MaxIds = 20_000;

    /// <summary>The distinct ids of a request, or null when it names none or too many.</summary>
    public static IReadOnlyList<int>? Clean(IReadOnlyList<int>? ids) =>
        ids is null || ids.Count == 0 || ids.Count > MaxIds ? null : ids.Distinct().ToList();

    public static string InvalidMessage => $"Select between 1 and {MaxIds} titles.";

    /// <summary>Short outcome of a bulk command: the text plus the first few failures, never one line per title.</summary>
    public static string Summary(string text, IReadOnlyList<string> failures)
    {
        if (failures.Count == 0) return text;
        var shown = string.Join(" · ", failures.Take(3));
        return failures.Count > 3 ? $"{text}; {failures.Count} failed: {shown} …" : $"{text}; {failures.Count} failed: {shown}";
    }
}
