using System.Globalization;
using NteOmikuji.Core.Mapping;

namespace NteOmikuji.Core.Analysis;

/// <param name="PullsSpent">Pulls since the previous top-rank reward, including this one.</param>
/// <param name="IsLowerBound">True for the oldest entry, whose earlier pulls may lie before the saved history.</param>
/// <param name="Source">How a board reward arrived when it was not a dice roll, such as the reward given every 10 pulls.</param>
public sealed record TopRankPull(string RewardId, string Name, string Type, string Timestamp, int PullsSpent, bool IsLowerBound, string FocusLabel, bool IsLimited, string? Source);

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
    bool HasExcessPointsRewards = false);

/// <summary>
/// Measures what is in the history and claims nothing about game rules. Pity
/// thresholds are not built in because published figures disagree.
/// </summary>
public static class HistoryAnalyzer
{
    private const string TopRank = "S";
    private const string SecondRank = "A";
    private const string PointsReward = "points_gift";
    private const int MaxDailyBuckets = 62;
    private const int TopRewardCount = 10;
    private const int PullsPerPointsReward = 10;

    /// <param name="nameOf">How to show a reward. Defaults to its export name, or its ID when unmapped.</param>
    public static BannerAnalysis Analyze(BannerHistory history, Func<HistoryRow, string>? nameOf = null)
    {
        nameOf ??= row => row.IsMappedReward && row.RewardName.Length > 0 ? row.RewardName : row.RewardId;
        // Rows are newest first; intervals are counted forward in time.
        var chronological = history.Rows.Reverse().ToList();
        var isMonopoly = history.Banner.Kind is HistoryKind.Permanent or HistoryKind.LimitedCharacter;
        bool IsPull(HistoryRow row) => !isMonopoly || row.ResultType == "dice";

        var pulls = chronological.Where(IsPull).ToList();
        var topRankPulls = new List<TopRankPull>();
        var sinceTopRank = 0;
        var sinceLimited = 0;
        var sawLimited = false;
        foreach (var row in chronological)
        {
            if (IsPull(row))
            {
                sinceTopRank++;
                sinceLimited++;
            }
            // Set per reward in the mapping table, because an S quality alone also covers dice and cosmetics.
            if (row.Focus.IsTopRank())
            {
                topRankPulls.Add(new TopRankPull(row.RewardId, nameOf(row), row.RewardType,
                    row.TimestampDecoded, sinceTopRank, topRankPulls.Count == 0,
                    RewardSchema.FocusLabel(row.Focus), row.Focus.IsLimited(), IsPull(row) ? null : ResultLabel(row)));
                sinceTopRank = 0;
            }
            if (row.Focus.IsLimited())
            {
                sawLimited = true;
                sinceLimited = 0;
            }
        }

        var exact = topRankPulls.Where(pull => !pull.IsLowerBound).Select(pull => pull.PullsSpent).ToList();
        var topCount = topRankPulls.Count;
        topRankPulls.Reverse();
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
            history.Rows.Where(row => row.Focus.IsCosmetic())
                .Select(row => new FocusItem(row.RewardId, nameOf(row), RewardSchema.FocusLabel(row.Focus), row.TimestampDecoded)).ToList(),
            // Saved JSON cannot be corrected without its bytes, so older decoding has to be pointed out.
            isMonopoly && chronological.Count(row => row.ResultType == PointsReward) > (pulls.Count / PullsPerPointsReward) + 1);
    }

    private static bool IsRank(HistoryRow row, string rank) =>
        string.Equals(row.RewardRank, rank, StringComparison.OrdinalIgnoreCase);

    private static List<CountBucket> RarityMix(List<HistoryRow> pulls)
    {
        var known = new[] { TopRank, SecondRank, "B" };
        var buckets = known.Select(rank => new CountBucket(rank, pulls.Count(row => IsRank(row, rank)))).ToList();
        buckets.Add(new CountBucket("未分級", pulls.Count(row => !known.Any(rank => IsRank(row, rank)))));
        return buckets;
    }

    private static List<CountBucket> DiceRolls(List<HistoryRow> pulls)
    {
        var rolls = pulls.Where(row => row.Dice is not null).GroupBy(row => row.Dice!.Value).ToDictionary(group => group.Key, group => group.Count());
        return Enumerable.Range(1, Math.Max(6, rolls.Keys.DefaultIfEmpty(0).Max()))
            .Select(value => new CountBucket(value.ToString(CultureInfo.InvariantCulture), rolls.GetValueOrDefault(value)))
            .ToList();
    }

    private static string ResultLabel(HistoryRow row) => row.ResultType switch
    {
        "dice" => "骰子",
        PointsReward => "集點獎勵",
        "chase_reward" => "追逐獎勵",
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
