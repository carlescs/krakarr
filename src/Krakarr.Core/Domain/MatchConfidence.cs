namespace Krakarr.Core.Domain;

/// <summary>How sure library import is that a folder matches a metadata entry.</summary>
public enum MatchConfidence
{
    None = 0,
    /// <summary>Only a top search result; needs review.</summary>
    Low = 1,
    /// <summary>Title matches but the year is missing or off by one.</summary>
    Medium = 2,
    /// <summary>Title and year match.</summary>
    High = 3,
    /// <summary>Matched by an id in the folder name.</summary>
    Exact = 4,
}
