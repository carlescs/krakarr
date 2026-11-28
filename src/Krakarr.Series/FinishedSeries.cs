using System.Linq.Expressions;
using Krakarr.Core.Domain;

namespace Krakarr.Series;

/// <summary>
/// Series that are finished: ended, with at least one episode file and a file for every monitored regular episode that
/// has aired. The series list menu offers to stop monitoring them (admins) and to unfollow them (each user, for themselves).
/// </summary>
public static class FinishedSeries
{
    /// <summary>Translates to SQL (two EXISTS on episodes); use it in queries, not on loaded series.</summary>
    public static Expression<Func<Core.Domain.Series, bool>> Predicate(DateOnly today) => s =>
        s.Status == SeriesStatus.Ended
        && s.Episodes.Any(e => e.SeasonNumber > 0 && e.EpisodeFileId != null)
        && !s.Episodes.Any(e => e.SeasonNumber > 0 && e.Monitored && e.EpisodeFileId == null && e.AirDate != null && e.AirDate <= today);
}
