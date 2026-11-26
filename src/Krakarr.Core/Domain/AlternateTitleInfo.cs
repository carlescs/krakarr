namespace Krakarr.Core.Domain;

/// <summary>Another name of a movie or series as a metadata source, TheXEM or the user gives it.</summary>
/// <param name="Language"><see cref="MediaLanguages"/> code of the title's language; null when unknown.</param>
public sealed record AlternateTitleInfo(string Title, string? Language = null);

/// <summary>An alternate title as the API lists it.</summary>
public sealed record AlternateTitleDto(int Id, string Title, string? Language, AlternateTitleSource Source);

/// <summary>Replaces the user's own alternate titles of a movie or series (metadata and scene titles are kept).</summary>
public sealed record UpdateAlternateTitlesRequest(IReadOnlyList<AlternateTitleInfo>? Titles);
