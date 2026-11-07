using System.Globalization;
using NteOmikuji.Core.Mapping;

namespace NteOmikuji.Core.Analysis;

/// <param name="PullsSpent">Pulls since the previous top-rank reward, including this one.</param>
/// <param name="IsLowerBound">True for the oldest entry, whose earlier pulls may lie before the saved history.</param>
/// <param name="Source">How a board reward arrived when it was not a dice roll, such as the reward given every 10 pulls.</param>
/// <param name="LimitedSpent">For a limited reward, pulls since the previous limited one, including this one.</param>
/// <param name="IsLimitedLowerBound">True for the oldest limited reward, whose earlier pulls may lie before the saved history.</param>
/// <param name="IsGuaranteed">True when the pool's limited guarantee forced this reward rather than chance.</param>
public sealed record TopRankPull(string RewardId, string Name, string Type, string Timestamp, int PullsSpent, bool IsLowerBound, string FocusLabel, bool IsLimited, string? Source, string? BannerId = null, int? LimitedSpent = null, bool IsLimitedLowerBound = false, bool IsGuaranteed = false);

/// <param name="IsLowerBound">True when the last reset lies before the saved history, so the real count may be higher.</param>
/// <param name="RisesFrom">The pull from which the odds improve, when the pool has such a point.</param>
public sealed record PityStatus(int Current, int Threshold, bool IsLowerBound, int? RisesFrom = null)
{
    public int Remaining => Math.Max(0, Threshold - Current);
    public bool HasRisen => RisesFrom is { } start && Current >= start;
    public int? PullsUntilRise => RisesFrom is { } start && Current < start ? start - Current : null;
    public bool Exceeded => Current > Threshold;
    public double Progress => Math.Min(1.0, (double)Current / Threshold);
}

/// <summary>One event's rows within a pool. Top-rank intervals still run across events, since pity carries over.</summary>
public sealed record BannerGroup(string Id, string Name, string? FirstDate, string? LastDate, int Pulls, int TopRankCount);

public sealed record FocusItem(string RewardId, string Name, string FocusLabel, string Timestamp);

public sealed record CountBucket(string Label, int Count);

public sealed record BannerAnalysis(
    int TotalPulls,
    int TopRankCount,
    int SecondRankCount,
    double? TopRankRate,
    double? AverageInterval,
    int? ShortestInterval,
    int? LongestInterval,
    int PullsSinceTopRank,
    int LimitedCount,
    int? PullsSinceLimited,
    string? FirstDate,
    string? LastDate,
    IReadOnlyList<TopRankPull> TopRankPulls,
    IReadOnlyList<CountBucket> RarityMix,
    IReadOnlyList<CountBucket> PullsOverTime,
    string TimeUnit,
    IReadOnlyList<CountBucket> DiceRolls,
    IReadOnlyList<CountBucket> ResultKinds,
    IReadOnlyList<CountBucket> TopRewards,
    IReadOnlyList<CountBucket> FocusCounts,
    IReadOnlyList<FocusItem> CosmeticItems,
    bool HasExcessPointsRewards = false,
    double? MedianInterval = null,
    IReadOnlyList<CountBucket>? IntervalHistogram = null,
    PityStatus? TopRankPity = null,
    PityStatus? LimitedPity = null,
    PityStatus? SecondRankPity = null,
    int? TopRanksBeforeRise = null,
    int GuaranteedLimitedCount = 0,
    double? ObservedLimitedRate = null,
    LuckScore? Luck = null,
    int CarriedPulls = 0,
    int EndedPulls = 0);

/// <summary>Measures what is in the history against each pool's stated guarantees, and predicts nothing.</summary>
public static class HistoryAnalyzer
{
    private const string TopRank = "S";
    private const string SecondRank = "A";
    private const string PointsReward = "points_gift";
    public const string TopRankTargets = "S 角色／弧盤";
    public const string TopRankOthers = "S 道具／外觀";
    private const int MaxDailyBuckets = 62;
    private const int TopRewardCount = 10;
    private const int PullsPerPointsReward = 10;
    private const int HistogramStep = 10;

    /// <param name="nameOf">How to show a reward. Defaults to its export name, or its ID when unmapped.</param>
    /// <param name="bannerId">Limits totals to one event. Intervals are still counted across every event in the pool.</param>
    /// <param name="unsetBanners">Events with no featured list, whose rewards cannot be judged, so luck is left unscored.</param>
    public static BannerAnalysis Analyze(BannerHistory history, Func<HistoryRow, string>? nameOf = null, string? bannerId = null, IReadOnlyCollection<string>? unsetBanners = null)
    {
        nameOf ??= DefaultName;
        // Rows are newest first; intervals are counted forward in time.
        var all = history.Rows.Reverse().ToList();
        var chronological = bannerId is null ? all : all.Where(row => row.BannerId == bannerId).ToList();
        var isMonopoly = IsMonopoly(history);
        bool IsPull(HistoryRow row) => !isMonopoly || row.ResultType == "dice";

        var rule = history.Banner.Pity;
        var pulls = chronological.Where(IsPull).ToList();
        var topRankPulls = new List<TopRankPull>();
        var sinceTopRank = 0;
        var sinceLimited = 0;
        var sawLimited = false;
        var sinceSecondRank = 0;
        var sawSecondRank = false;
        // An event inherits pity, so its luck starts from the pulls already made when it began.
        int? carried = null;
        int? ended = null;
        foreach (var row in all)
        {
            if (bannerId is not null && carried is null && row.BannerId == bannerId)
            {
                carried = rule?.LimitedAt is null ? sinceTopRank : sinceLimited;
            }
            if (IsPull(row))
            {
                sinceTopRank++;
                sinceLimited++;
                sinceSecondRank++;
            }
            // A board's A comes from the fixed reward every 10 pulls, whatever the dice landed on.
            if (isMonopoly ? row.ResultType == PointsReward : IsRank(row, SecondRank) || IsRank(row, TopRank))
            {
                sinceSecondRank = 0;
                sawSecondRank = true;
            }
            // Set per reward in the mapping table, because an S quality alone also covers dice and cosmetics.
            if (row.Focus.IsTopRank())
            {
                topRankPulls.Add(new TopRankPull(row.RewardId, nameOf(row), row.RewardType,
                    row.TimestampDecoded, sinceTopRank, topRankPulls.Count == 0,
                    RewardSchema.FocusLabel(row.Focus), row.Focus.IsLimited(), IsPull(row) ? null : ResultLabel(row), row.BannerId,
                    row.Focus.IsLimited() ? sinceLimited : null, row.Focus.IsLimited() && !sawLimited,
                    row.Focus.IsLimited() && sinceLimited == rule?.LimitedAt));
                sinceTopRank = 0;
            }
            if (row.Focus.IsLimited())
            {
                sawLimited = true;
                sinceLimited = 0;
            }
            if (bannerId is not null && row.BannerId == bannerId)
            {
                ended = rule?.LimitedAt is null ? sinceTopRank : sinceLimited;
            }
        }

        var overallTopCount = topRankPulls.Count;
        if (bannerId is not null)
        {
            topRankPulls = topRankPulls.Where(pull => pull.BannerId == bannerId).ToList();
        }
        var exact = topRankPulls.Where(pull => !pull.IsLowerBound).Select(pull => pull.PullsSpent).ToList();
        var topCount = topRankPulls.Count;
        // Forced rewards say nothing about the odds, so the rate only looks at the rest.
        var guaranteed = topRankPulls.Count(pull => pull.IsGuaranteed);
        var byChance = topCount - guaranteed;
        double? observedLimitedRate = rule?.LimitedChance is not null && byChance > 0
            ? (double)topRankPulls.Count(pull => pull.IsLimited && !pull.IsGuaranteed) / byChance
            : null;
        topRankPulls.Reverse();
        var excessRewards = isMonopoly && chronological.Count(row => row.ResultType == PointsReward) > (pulls.Count / PullsPerPointsReward) + 1;
        // Arcs are judged on the limited one, since most S Arcs are not what a player is after.
        // Limited status needs every row's event, so an older file without them is left unscored.
        var isJudgeable = !excessRewards
            && (rule?.LimitedAt is null || all.All(row => row.BannerId is not null))
            && (unsetBanners is not { Count: > 0 } || !all.Any(row => row.BannerId is { } id && unsetBanners.Contains(id, StringComparer.OrdinalIgnoreCase)));
        var luck = isJudgeable && rule is not null
            ? LuckModel.Score(rule, rule.LimitedAt is null ? topCount : chronological.Count(row => row.Focus.IsLimited()), pulls.Count, carried ?? 0, ended ?? (rule.LimitedAt is null ? sinceTopRank : sinceLimited))
            : null;
        var (overTime, unit) = BucketByTime(pulls);
        return new BannerAnalysis(
            pulls.Count,
            topCount,
            pulls.Count(row => IsRank(row, SecondRank)),
            pulls.Count == 0 ? null : (double)topCount / pulls.Count,
            exact.Count == 0 ? null : exact.Average(),
            exact.Count == 0 ? null : exact.Min(),
            exact.Count == 0 ? null : exact.Max(),
            sinceTopRank,
            chronological.Count(row => row.Focus.IsLimited()),
            sawLimited ? sinceLimited : null,
            chronological.FirstOrDefault()?.TimestampDecoded,
            chronological.LastOrDefault()?.TimestampDecoded,
            topRankPulls,
            RarityMix(pulls),
            overTime,
            unit,
            isMonopoly ? DiceRolls(pulls) : [],
            isMonopoly ? ResultKinds(chronological) : [],
            chronological.GroupBy(nameOf)
                .Select(group => new CountBucket(group.Key, group.Count()))
                .OrderByDescending(bucket => bucket.Count).ThenBy(bucket => bucket.Label, StringComparer.Ordinal)
                .Take(TopRewardCount).ToList(),
            RewardSchema.AllFocus.Where(focus => focus != RewardFocus.None)
                .Select(focus => new CountBucket(RewardSchema.FocusLabel(focus), chronological.Count(row => row.Focus == focus)))
                .Where(bucket => bucket.Count > 0).ToList(),
            Enumerable.Reverse(chronological).Where(row => row.Focus.IsCosmetic())
                .Select(row => new FocusItem(row.RewardId, nameOf(row), RewardSchema.FocusLabel(row.Focus), row.TimestampDecoded)).ToList(),
            // Saved JSON cannot be corrected without its bytes, so older decoding has to be pointed out.
            excessRewards,
            Median(exact),
            Histogram(exact, rule?.TopRankAt),
            bannerId is null && rule is not null ? new PityStatus(sinceTopRank, rule.TopRankAt, overallTopCount == 0, rule.RisesFrom) : null,
            bannerId is null && rule?.LimitedAt is { } limitedAt ? new PityStatus(sinceLimited, limitedAt, !sawLimited) : null,
            bannerId is null && !excessRewards && rule?.SecondRankAt is { } secondRankAt ? new PityStatus(sinceSecondRank, secondRankAt, !sawSecondRank) : null,
            rule?.RisesFrom is { } risesFrom ? exact.Count(value => value < risesFrom) : null,
            guaranteed,
            observedLimitedRate,
            luck,
            carried ?? 0,
            ended ?? (rule?.LimitedAt is null ? sinceTopRank : sinceLimited));
    }

    /// <summary>Events in a pool, newest first.</summary>
    /// <param name="bannerName">The name of an event's featured reward. Without one, its most frequent S names it, since IDs alone are not readable.</param>
    public static IReadOnlyList<BannerGroup> Banners(BannerHistory history, Func<HistoryRow, string>? nameOf = null, Func<string, string?>? bannerName = null)
    {
        nameOf ??= DefaultName;
        var isMonopoly = IsMonopoly(history);
        return history.Rows.Where(row => !string.IsNullOrEmpty(row.BannerId))
            .GroupBy(row => row.BannerId!)
            .Select(group =>
            {
                var top = group.Where(row => row.Focus.IsTopRank()).ToList();
                var name = bannerName?.Invoke(group.Key)
                    ?? top.GroupBy(nameOf).OrderByDescending(names => names.Count()).ThenBy(names => names.Key, StringComparer.Ordinal).FirstOrDefault()?.Key
                    ?? group.Key[(group.Key.IndexOf('_') + 1)..];
                var dates = group.Select(row => row.TimestampDecoded).Where(date => date.Length > 0).Order(StringComparer.Ordinal).ToList();
                return new BannerGroup(group.Key, name, dates.FirstOrDefault(), dates.LastOrDefault(),
                    group.Count(row => !isMonopoly || row.ResultType == "dice"), top.Count);
            })
            .OrderByDescending(group => group.LastDate, StringComparer.Ordinal)
            .ToList();
    }

    private static string DefaultName(HistoryRow row) => row.IsMappedReward && row.RewardName.Length > 0 ? row.RewardName : row.RewardId;

    private static bool IsMonopoly(BannerHistory history) => history.Banner.Kind is HistoryKind.Permanent or HistoryKind.LimitedCharacter;

    private static double? Median(List<int> values)
    {
        if (values.Count == 0)
        {
            return null;
        }
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    /// <summary>Runs to the guaranteed count even where empty, so the shape reads against the pity limit.</summary>
    private static List<CountBucket> Histogram(List<int> intervals, int? limit)
    {
        var top = Math.Max(limit ?? 0, intervals.DefaultIfEmpty(0).Max());
        var buckets = new List<CountBucket>();
        for (var start = 1; start <= top; start += HistogramStep)
        {
            var end = start + HistogramStep - 1;
            buckets.Add(new CountBucket($"{start}–{end}", intervals.Count(value => value >= start && value <= end)));
        }
        return buckets;
    }

    private static bool IsRank(HistoryRow row, string rank) =>
        string.Equals(row.RewardRank, rank, StringComparison.OrdinalIgnoreCase);

    private static List<CountBucket> RarityMix(List<HistoryRow> pulls)
    {
        // Items and cosmetics also carry an S, so the rewards people count are shown apart from them.
        var known = new[] { TopRank, SecondRank, "B" };
        return
        [
            new CountBucket(TopRankTargets, pulls.Count(row => row.Focus.IsTopRank())),
            new CountBucket(TopRankOthers, pulls.Count(row => IsRank(row, TopRank) && !row.Focus.IsTopRank())),
            new CountBucket(SecondRank, pulls.Count(row => IsRank(row, SecondRank) && !row.Focus.IsTopRank())),
            new CountBucket("B", pulls.Count(row => IsRank(row, "B") && !row.Focus.IsTopRank())),
            new CountBucket("未分級", pulls.Count(row => !row.Focus.IsTopRank() && !known.Any(rank => IsRank(row, rank)))),
        ];
    }

    private static List<CountBucket> DiceRolls(List<HistoryRow> pulls)
    {
        var rolls = pulls.Where(row => row.Dice is not null).GroupBy(row => row.Dice!.Value).ToDictionary(group => group.Key, group => group.Count());
        return Enumerable.Range(1, Math.Max(6, rolls.Keys.DefaultIfEmpty(0).Max()))
            .Select(value => new CountBucket(value.ToString(CultureInfo.InvariantCulture), rolls.GetValueOrDefault(value)))
            .ToList();
    }

    public static string ResultLabel(HistoryRow row) => row.ResultType switch
    {
        "dice" => "骰子",
        PointsReward => "集點贈禮",
        "chase_reward" => "沉眠地",
        "" or "unknown" => "未知",
        var other => other,
    };

    private static List<CountBucket> ResultKinds(List<HistoryRow> rows) =>
        rows.GroupBy(ResultLabel)
            .Select(group => new CountBucket(group.Key, group.Count()))
            .OrderByDescending(bucket => bucket.Count)
            .ToList();

    /// <summary>Includes empty periods so that gaps between sessions stay visible on the axis.</summary>
    private static (List<CountBucket> Buckets, string Unit) BucketByTime(List<HistoryRow> pulls)
    {
        var dates = pulls
            .Select(row => DateTime.TryParseExact(row.TimestampDecoded, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.Date : (DateTime?)null)
            .OfType<DateTime>()
            .ToList();
        if (dates.Count == 0)
        {
            return ([], "日");
        }
        var first = dates.Min();
        var last = dates.Max();
        var daily = (last - first).TotalDays < MaxDailyBuckets;
        var counts = dates.GroupBy(date => daily ? date : new DateTime(date.Year, date.Month, 1)).ToDictionary(group => group.Key, group => group.Count());
        var buckets = new List<CountBucket>();
        for (var at = daily ? first : new DateTime(first.Year, first.Month, 1); at <= last; at = daily ? at.AddDays(1) : at.AddMonths(1))
        {
            buckets.Add(new CountBucket(at.ToString(daily ? "MM-dd" : "yyyy-MM", CultureInfo.InvariantCulture), counts.GetValueOrDefault(at)));
        }
        return (buckets, daily ? "日" : "月");
    }
}
