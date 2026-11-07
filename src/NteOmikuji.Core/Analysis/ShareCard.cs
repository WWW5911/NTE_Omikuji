using NteOmikuji.Core.Export;

namespace NteOmikuji.Core.Analysis;

/// <param name="Tier">The tier's position from worst (0) to best (6).</param>
/// <param name="TopText">Where this falls among the outcomes these pulls can bring, luckiest first.</param>
public sealed record ShareLuck(int Tier, string Label, double Sigma, int Hits, int Pulls, double Expected, double StandardDeviation, string TopText);

/// <param name="AtLeast">True for the oldest entry, whose earlier pulls may lie before the saved history.</param>
public sealed record ShareReward(string Name, int Pulls, bool AtLeast, bool Limited);

/// <param name="LimitedCount">Null for a pool without limited rewards.</param>
/// <param name="LuckNote">Why there is no luck tag, when there is none.</param>
/// <param name="Rewards">The rewards the pool is judged on, newest first: every S on a board, and only limited ones on Arcs.</param>
/// <param name="Average">Average pulls per reward in <paramref name="Rewards"/>, leaving out one whose start is unknown.</param>
/// <param name="Context">The pool an event belongs to, when this covers one event.</param>
/// <param name="Scale">Pulls at which the pool guarantees the reward it is judged on.</param>
/// <param name="TargetLabel">What <paramref name="Rewards"/> holds.</param>
/// <param name="RisesFrom">The pull from which the odds improve, when the pool has such a point.</param>
/// <param name="CarriedPulls">Pulls already made toward the next reward when an event began.</param>
/// <param name="EndedPulls">Pulls made toward the next reward when an event ended.</param>
public sealed record SharePool(
    string Kind,
    string Label,
    string TopLabel,
    int TotalPulls,
    int TopCount,
    int? LimitedCount,
    double? Average,
    int? Shortest,
    int? Longest,
    string? FirstDate,
    string? LastDate,
    ShareLuck? Luck,
    string? LuckNote,
    PityStatus? Pity,
    PityStatus? LimitedPity,
    IReadOnlyList<ShareReward> Rewards,
    int Scale,
    int? RisesFrom,
    string? Context = null,
    int CarriedPulls = 0,
    int EndedPulls = 0,
    string TargetLabel = "S");

/// <summary>A pool to share, or one event within it.</summary>
public sealed record ShareTarget(HistoryKind Kind, string? BannerId = null);

/// <summary>What a share image shows. It carries only what is drawn, and the account only when asked and masked.</summary>
/// <param name="Selected">The pool or event the single pool template shows.</param>
public sealed record ShareCard(
    string? FirstDate,
    string? LastDate,
    int TotalPulls,
    int TotalTop,
    int TotalLimited,
    ShareLuck? Overall,
    IReadOnlyList<SharePool> Pools,
    SharePool Selected,
    IReadOnlyList<string> TierLabels,
    string? MaskedUid,
    string Footer);

public static class ShareCardBuilder
{
    private const int VisibleUidStart = 3;
    private const int VisibleUidEnd = 2;

    /// <param name="uid">Shown masked when given.</param>
    /// <param name="bannerName">The name of an event's featured reward.</param>
    /// <param name="target">What the single pool template shows. Defaults to the first pool.</param>
    public static ShareCard Build(
        IReadOnlyList<BannerHistory> histories,
        Func<HistoryRow, string>? nameOf,
        IReadOnlyCollection<string>? unsetBanners,
        DateOnly today,
        string? uid = null,
        Func<string, string?>? bannerName = null,
        ShareTarget? target = null)
    {
        var pools = new[] { BannerInfo.LimitedCharacter, BannerInfo.Permanent, BannerInfo.ArcMiracleBox }
            .Select(banner => Pool(banner, histories.FirstOrDefault(history => history.Banner.Kind == banner.Kind), nameOf, unsetBanners))
            .ToList();
        var data = pools.Where(pool => pool.Data is not null).Select(pool => pool.Data!).ToList();
        return new ShareCard(
            data.Select(item => item.FirstDate).Where(date => date is not null).Order(StringComparer.Ordinal).FirstOrDefault(),
            data.Select(item => item.LastDate).Where(date => date is not null).Order(StringComparer.Ordinal).LastOrDefault(),
            data.Sum(item => item.TotalPulls),
            data.Sum(item => item.TopRankCount),
            data.Sum(item => item.LimitedCount),
            ToShare(LuckModel.Combine(data.Select(item => item.Luck))),
            pools.Select(pool => pool.Share).ToList(),
            Selected(pools.Select(pool => pool.Share).ToList(), histories, nameOf, bannerName, unsetBanners, target),
            Enum.GetValues<LuckTier>().Select(LuckModel.LabelOf).ToList(),
            MaskUid(uid),
            $"NTE Omikuji {ExportWriter.ExporterVersion}・{today:yyyy-MM-dd}");
    }

    /// <summary>A file name part for a target. Event IDs come from the game, so only plain characters are kept.</summary>
    public static string FileLabel(ShareTarget target) => string.Concat(
        (target.BannerId ?? target.Kind.ToString()).Select(letter => char.IsAsciiLetterOrDigit(letter) || letter is '_' or '-' ? letter : '_'));

    /// <summary>Hides the middle of an account ID. Too short an ID to mask is not shown at all.</summary>
    public static string? MaskUid(string? uid) =>
        uid is null || uid.Length <= VisibleUidStart + VisibleUidEnd
            ? null
            : uid[..VisibleUidStart] + new string('*', uid.Length - VisibleUidStart - VisibleUidEnd) + uid[^VisibleUidEnd..];

    private static SharePool Selected(
        IReadOnlyList<SharePool> pools,
        IReadOnlyList<BannerHistory> histories,
        Func<HistoryRow, string>? nameOf,
        Func<string, string?>? bannerName,
        IReadOnlyCollection<string>? unsetBanners,
        ShareTarget? target)
    {
        var whole = pools.FirstOrDefault(pool => pool.Kind == target?.Kind.ToString()) ?? pools[0];
        if (target?.BannerId is not { } id || histories.FirstOrDefault(history => history.Banner.Kind == target.Kind) is not { } history
            || HistoryAnalyzer.Banners(history, nameOf, bannerName).FirstOrDefault(group => group.Id == id) is not { } group)
        {
            return whole;
        }

        // Pity carries across events, so an event has no meter of its own to show.
        var data = HistoryAnalyzer.Analyze(history, nameOf, id, unsetBanners);
        var rewards = Rewards(history.Banner, data);
        var costs = Costs(rewards);
        return whole with
        {
            Label = group.Name,
            Context = whole.Label,
            TotalPulls = data.TotalPulls,
            TopCount = data.TopRankCount,
            LimitedCount = data.LimitedCount,
            Average = costs.Count == 0 ? null : costs.Average(),
            Shortest = costs.Count == 0 ? null : costs.Min(),
            Longest = costs.Count == 0 ? null : costs.Max(),
            FirstDate = data.FirstDate,
            LastDate = data.LastDate,
            Luck = ToShare(data.Luck),
            LuckNote = LuckNote(data),
            Pity = null,
            LimitedPity = null,
            Rewards = rewards,
            CarriedPulls = data.CarriedPulls,
            EndedPulls = data.EndedPulls,
        };
    }

    /// <summary>Arcs are judged on the limited one, so each one's cost runs back to the last limited one, past ordinary S.</summary>
    private static List<ShareReward> Rewards(BannerInfo banner, BannerAnalysis data) =>
        banner.Pity?.LimitedAt is null
            ? data.TopRankPulls.Select(pull => new ShareReward(pull.Name, pull.PullsSpent, pull.IsLowerBound, pull.IsLimited)).ToList()
            : data.TopRankPulls.Where(pull => pull.IsLimited)
                .Select(pull => new ShareReward(pull.Name, pull.LimitedSpent ?? pull.PullsSpent, pull.IsLimitedLowerBound, true)).ToList();

    private static List<int> Costs(List<ShareReward> rewards) =>
        rewards.Where(reward => !reward.AtLeast).Select(reward => reward.Pulls).ToList();

    private static (SharePool Share, BannerAnalysis? Data) Pool(BannerInfo banner, BannerHistory? history, Func<HistoryRow, string>? nameOf, IReadOnlyCollection<string>? unsetBanners)
    {
        var label = HistoryCsv.PoolName(banner.Kind);
        var topLabel = banner.Kind == HistoryKind.ArcMiracleBox ? "S 弧盤" : "S";
        var hasLimited = banner.Kind != HistoryKind.Permanent;
        if (history is null)
        {
            return (new SharePool(banner.Kind.ToString(), label, topLabel, 0, 0, hasLimited ? 0 : null, null, null, null, null, null, null, "尚無紀錄", null, null, [], Scale(banner), banner.Pity!.RisesFrom, TargetLabel: TargetOf(banner)), null);
        }

        var data = HistoryAnalyzer.Analyze(history, nameOf, unsetBanners: unsetBanners);
        var rewards = Rewards(banner, data);
        var costs = Costs(rewards);
        var share = new SharePool(
            banner.Kind.ToString(), label, topLabel, data.TotalPulls, data.TopRankCount, hasLimited ? data.LimitedCount : null,
            costs.Count == 0 ? null : costs.Average(), costs.Count == 0 ? null : costs.Min(), costs.Count == 0 ? null : costs.Max(), data.FirstDate, data.LastDate,
            ToShare(data.Luck), LuckNote(data), data.TopRankPity, data.LimitedPity, rewards, Scale(banner), banner.Pity!.RisesFrom, TargetLabel: TargetOf(banner));
        return (share, data);
    }

    private static int Scale(BannerInfo banner) => banner.Pity!.LimitedAt ?? banner.Pity.TopRankAt;

    private static string TargetOf(BannerInfo banner) => banner.Pity!.LimitedAt is null ? "S" : "限定";

    private static string? LuckNote(BannerAnalysis data) =>
        data.Luck is not null ? null : data.TotalPulls == 0 ? "尚無紀錄" : "無法判定";

    private static ShareLuck? ToShare(LuckScore? score) => score is null ? null : new ShareLuck(
        (int)score.Tier, score.Label, score.Sigma, score.Hits, score.Pulls, score.Expected, score.StandardDeviation,
        score.TopText);
}
