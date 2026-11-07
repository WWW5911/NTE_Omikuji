using NteOmikuji.Core.Analysis;
using NteOmikuji.Core.Export;
using NteOmikuji.Core.Import;
using NteOmikuji.Core.Session;
using System.Text;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class PoolOverviewTests
{
    private static HistoryRow Row(int second, string reward = "x", RewardFocus focus = RewardFocus.None, string result = "dice", string? eventId = null, string? rank = null) => new()
    {
        RewardRank = rank,
        Uid = $"u{second}",
        TimestampDecoded = $"2026-01-01 00:{second / 60:00}:{second % 60:00}",
        RewardId = reward,
        RewardName = reward,
        IsMappedReward = true,
        ResultType = result,
        Focus = focus,
        EventId = eventId,
    };

    private static PoolHistory Pool(PoolInfo eventId, params HistoryRow[] oldestFirst) => new(eventId, oldestFirst.Reverse().ToList(), [], []);

    private static HistoryRow[] Pulls(int count, int startSecond = 0, string? eventId = null) =>
        Enumerable.Range(startSecond, count).Select(second => Row(second, eventId: eventId)).ToArray();

    [Theory]
    [InlineData(HistoryKind.LimitedCharacter, 90, null, null, 70, 0.0099, 0.1959)]
    [InlineData(HistoryKind.Permanent, 90, null, null, 70, 0.0099, 0.1959)]
    [InlineData(HistoryKind.ArcMiracleBox, 60, 80, 0.25, null, 0.03, null)]
    public void EachPoolHasItsGuarantee(HistoryKind kind, int topRank, int? limited, double? chance, int? rises, double rate, double? raised)
    {
        Assert.Equal(new PityRule(topRank, limited, chance, rises, 10, rate, raised, PullsPerDraw: kind == HistoryKind.ArcMiracleBox ? 10 : 1), PoolInfo.For(kind).Pity);
    }

    [Fact]
    public void ABoardIsJudgedOnItsS()
    {
        var luck = HistoryAnalyzer.Analyze(Pool(PoolInfo.Permanent, [.. Pulls(99), Row(99, "s", RewardFocus.SCharacter)])).Luck!;

        Assert.Equal((1, 100), (luck.Hits, luck.Pulls));
    }

    [Fact]
    public void AnArcPoolIsJudgedOnLimitedRewards()
    {
        var luck = HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox,
        [
            .. Pulls(40, 0, "ForkLottery_A"),
            Row(40, "off", RewardFocus.SArc, eventId: "ForkLottery_A"),
            .. Pulls(40, 41, "ForkLottery_A"),
            Row(81, "limited", RewardFocus.SLimitedArc, eventId: "ForkLottery_A"),
        ])).Luck!;

        Assert.Equal((1, 82), (luck.Hits, luck.Pulls));
    }

    [Fact]
    public void AnArcFileWithoutEventsIsNotJudged()
    {
        Assert.Null(HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox, Pulls(100))).Luck);
    }

    [Fact]
    public void AnEventWithoutAFeaturedListStopsTheJudgement()
    {
        var pool = Pool(PoolInfo.ArcMiracleBox, Pulls(100, 0, "ForkLottery_New"));

        Assert.NotNull(HistoryAnalyzer.Analyze(pool).Luck);
        Assert.Null(HistoryAnalyzer.Analyze(pool, unsetEvents: ["forklottery_new"]).Luck);
        Assert.NotNull(HistoryAnalyzer.Analyze(pool, unsetEvents: ["ForkLottery_Other"]).Luck);
    }

    [Fact]
    public void MisreadHistoryIsNotJudged()
    {
        HistoryRow[] rows = [.. Pulls(100), .. Enumerable.Range(100, 40).Select(second => Row(second, "gift", result: "points_gift"))];

        Assert.Null(HistoryAnalyzer.Analyze(Pool(PoolInfo.LimitedCharacter, rows)).Luck);
    }

    [Fact]
    public void AnEventPageHasALuckScore()
    {
        var pool = Pool(PoolInfo.LimitedCharacter, Pulls(100, 0, "Lottery_A"));

        Assert.NotNull(HistoryAnalyzer.Analyze(pool).Luck);
        Assert.NotNull(HistoryAnalyzer.Analyze(pool, eventId: "Lottery_A").Luck);
    }

    [Fact]
    public void PityCountsPullsSinceTheLastTopRank()
    {
        var result = HistoryAnalyzer.Analyze(Pool(PoolInfo.LimitedCharacter, [.. Pulls(5), Row(5, "s", RewardFocus.SCharacter), .. Pulls(52, 6)]));

        Assert.Equal(new PityStatus(52, 90, false, 70), result.TopRankPity);
        Assert.Equal(38, result.TopRankPity!.Remaining);
        Assert.Equal(18, result.TopRankPity.PullsUntilRise);
        Assert.False(result.TopRankPity.HasRisen);
        Assert.Null(result.LimitedPity);
    }

    [Fact]
    public void NoTopRankYetIsOnlyALowerBound()
    {
        var pity = HistoryAnalyzer.Analyze(Pool(PoolInfo.Permanent, Pulls(30))).TopRankPity!;

        Assert.True(pity.IsLowerBound);
        Assert.Equal(30, pity.Current);
    }

    [Fact]
    public void PityPastTheGuaranteeIsFlaggedAndCappedForDisplay()
    {
        var pity = HistoryAnalyzer.Analyze(Pool(PoolInfo.Permanent, Pulls(95))).TopRankPity!;

        Assert.True(pity.Exceeded);
        Assert.Equal(0, pity.Remaining);
        Assert.Equal(1.0, pity.Progress);
    }

    [Fact]
    public void ArcsTrackTheLimitedGuaranteeSeparately()
    {
        var result = HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox,
        [
            .. Pulls(10),
            Row(10, "limited", RewardFocus.SLimitedArc),
            .. Pulls(20, 11),
            Row(31, "standard", RewardFocus.SArc),
            .. Pulls(5, 32),
        ]));

        Assert.Equal(new PityStatus(5, 60, false), result.TopRankPity);
        Assert.Equal(new PityStatus(26, 80, false), result.LimitedPity);
    }

    /// <summary>Limited arcs after 2, 80, and 3 pulls, with an off-eventId S between the second pair.</summary>
    private static PoolHistory ArcRun()
    {
        var rows = new List<HistoryRow>();
        void Add(int plain, string reward, RewardFocus focus)
        {
            rows.AddRange(Pulls(plain, rows.Count));
            rows.Add(Row(rows.Count, reward, focus, eventId: "ForkLottery_A", rank: "S"));
        }
        Add(1, "first", RewardFocus.SLimitedArc);
        Add(39, "off", RewardFocus.SArc);
        Add(39, "forced", RewardFocus.SLimitedArc);
        Add(2, "lucky", RewardFocus.SLimitedArc);
        return Pool(PoolInfo.ArcMiracleBox, [.. rows]);
    }

    [Fact]
    public void LimitedArcsRecordTheirOwnIntervals()
    {
        var pulls = HistoryAnalyzer.Analyze(ArcRun()).TopRankPulls.Where(pull => pull.IsLimited).ToList();

        Assert.Equal(["lucky", "forced", "first"], pulls.Select(pull => pull.RewardId));
        Assert.Equal([3, 80, 2], pulls.Select(pull => pull.LimitedSpent!.Value));
        Assert.Equal([false, false, true], pulls.Select(pull => pull.IsLimitedLowerBound));
    }

    [Fact]
    public void ALimitedArcAtEightyPullsIsTheGuarantee()
    {
        var result = HistoryAnalyzer.Analyze(ArcRun());

        Assert.Equal(["forced"], result.TopRankPulls.Where(pull => pull.IsGuaranteed).Select(pull => pull.RewardId));
        Assert.Equal(1, result.GuaranteedLimitedCount);
    }

    [Fact]
    public void ObservedLimitedRateLeavesOutForcedRewards()
    {
        // Four S, one forced: of the other three, two were limited.
        Assert.Equal(2.0 / 3, HistoryAnalyzer.Analyze(ArcRun()).ObservedLimitedRate);
    }

    [Fact]
    public void BoardsHaveNoLimitedRateOrGuarantee()
    {
        var result = HistoryAnalyzer.Analyze(Pool(PoolInfo.LimitedCharacter, [.. Pulls(79), Row(79, "s", RewardFocus.SLimitedCharacter)]));

        Assert.Null(result.ObservedLimitedRate);
        Assert.Equal(0, result.GuaranteedLimitedCount);
    }

    [Fact]
    public void AnEventsLimitedStatsCountItsOwnRewards()
    {
        var rows = new List<HistoryRow>(Pulls(5, 0, "ForkLottery_A"));
        rows.Add(Row(5, "a", RewardFocus.SLimitedArc, eventId: "ForkLottery_A"));
        rows.AddRange(Pulls(79, 6, "ForkLottery_B"));
        rows.Add(Row(85, "b", RewardFocus.SLimitedArc, eventId: "ForkLottery_B"));

        var result = HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox, [.. rows]), eventId: "ForkLottery_B");

        Assert.Equal(80, Assert.Single(result.TopRankPulls).LimitedSpent);
        Assert.Equal(1, result.GuaranteedLimitedCount);
    }

    [Fact]
    public void AnEventIsNamedAfterItsFeaturedRewardBeforeItsCommonestS()
    {
        var pool = Pool(PoolInfo.ArcMiracleBox,
            Row(0, "off", RewardFocus.SArc, eventId: "ForkLottery_A"),
            Row(1, "off", RewardFocus.SArc, eventId: "ForkLottery_A"),
            Row(2, "plain", eventId: "ForkLottery_B"));

        var groups = HistoryAnalyzer.Events(pool, eventName: id => id == "ForkLottery_A" ? "限定大獎" : null);

        Assert.Equal(["B", "限定大獎"], groups.Select(group => group.Name));
    }

    [Fact]
    public void LimitedArcCountIsALowerBoundUntilOneIsMarked()
    {
        var limited = HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox, [.. Pulls(3), Row(3, "standard", RewardFocus.SArc)])).LimitedPity!;

        Assert.True(limited.IsLowerBound);
        Assert.Equal(4, limited.Current);
    }

    [Theory]
    [InlineData(69, false, 1)]
    [InlineData(70, true, null)]
    [InlineData(84, true, null)]
    public void OddsRiseFromPullSeventy(int pulls, bool risen, int? until)
    {
        var pity = HistoryAnalyzer.Analyze(Pool(PoolInfo.Permanent, [Row(0, "s", RewardFocus.SCharacter), .. Pulls(pulls, 1)])).TopRankPity!;

        Assert.Equal((risen, until), (pity.HasRisen, pity.PullsUntilRise));
    }

    [Fact]
    public void ArcsHaveNoStatedRisePoint()
    {
        var pity = HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox, Pulls(50))).TopRankPity!;

        Assert.False(pity.HasRisen);
        Assert.Null(pity.PullsUntilRise);
    }

    [Fact]
    public void TopRanksBeforeTheRiseAreCounted()
    {
        var result = HistoryAnalyzer.Analyze(Pool(PoolInfo.Permanent,
        [
            Row(0, "s", RewardFocus.SCharacter),
            .. Pulls(5, 1), Row(6, "s", RewardFocus.SCharacter),
            .. Pulls(74, 7), Row(81, "s", RewardFocus.SCharacter),
        ]));

        Assert.Equal(1, result.TopRanksBeforeRise);
        Assert.Null(HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox, Pulls(5))).TopRanksBeforeRise);
    }

    [Fact]
    public void ABoardsAGuaranteeIsTheRewardEveryTenPulls()
    {
        // An A from the dice does not restart it: the reward arrives every 10 pulls regardless.
        var result = HistoryAnalyzer.Analyze(Pool(PoolInfo.LimitedCharacter,
        [
            .. Pulls(10),
            Row(10, "gift", result: "points_gift", rank: "A"),
            .. Pulls(4, 11),
            Row(15, "a-from-dice", rank: "A"),
            .. Pulls(4, 16),
        ]));

        Assert.Equal(new PityStatus(9, 10, false), result.SecondRankPity);
        Assert.Equal(1, result.SecondRankPity!.Remaining);
    }

    [Fact]
    public void AnArcAGuaranteeRestartsOnAnyAOrS()
    {
        var pity = HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox,
        [
            .. Pulls(3),
            Row(3, "a", rank: "A"),
            .. Pulls(2, 4),
            Row(6, "s", RewardFocus.SArc, rank: "S"),
            .. Pulls(4, 7),
        ])).SecondRankPity;

        Assert.Equal(new PityStatus(4, 10, false), pity);
    }

    [Fact]
    public void AGuaranteeIsALowerBoundUntilOneIsSeen()
    {
        var pity = HistoryAnalyzer.Analyze(Pool(PoolInfo.LimitedCharacter, Pulls(5))).SecondRankPity!;

        Assert.True(pity.IsLowerBound);
        Assert.Equal(5, pity.Current);
    }

    [Fact]
    public void MisreadRewardsDoNotProduceAnAGuarantee()
    {
        HistoryRow[] rows = [.. Pulls(5), .. Enumerable.Range(5, 20).Select(second => Row(second, "gift", result: "points_gift"))];

        Assert.Null(HistoryAnalyzer.Analyze(Pool(PoolInfo.LimitedCharacter, rows)).SecondRankPity);
    }

    [Fact]
    public void RarityMixKeepsSRewardsApartFromSItems()
    {
        var mix = HistoryAnalyzer.Analyze(Pool(PoolInfo.LimitedCharacter,
            Row(0, "character", RewardFocus.SCharacter, rank: "S"),
            Row(1, "dice", rank: "S"),
            Row(2, "dice", rank: "S"),
            Row(3, "a", rank: "A"),
            Row(4, "b", rank: "B"),
            Row(5, "unranked"))).RarityMix.ToDictionary(bucket => bucket.Label, bucket => bucket.Count);

        Assert.Equal(1, mix[HistoryAnalyzer.TopRankTargets]);
        Assert.Equal(2, mix[HistoryAnalyzer.TopRankOthers]);
        Assert.Equal((1, 1, 1), (mix["A"], mix["B"], mix["未分級"]));
    }

    [Fact]
    public void MedianHandlesOddAndEvenCounts()
    {
        HistoryRow[] Sequence(params int[] gaps)
        {
            var rows = new List<HistoryRow> { Row(0, "first", RewardFocus.SCharacter) };
            foreach (var gap in gaps)
            {
                rows.AddRange(Pulls(gap - 1, rows.Count));
                rows.Add(Row(rows.Count, "s", RewardFocus.SCharacter));
            }
            return [.. rows];
        }

        Assert.Equal(5, HistoryAnalyzer.Analyze(Pool(PoolInfo.Permanent, Sequence(2, 5, 9))).MedianInterval);
        Assert.Equal(3.5, HistoryAnalyzer.Analyze(Pool(PoolInfo.Permanent, Sequence(2, 5))).MedianInterval);
        Assert.Null(HistoryAnalyzer.Analyze(Pool(PoolInfo.Permanent, Pulls(3))).MedianInterval);
    }

    [Fact]
    public void HistogramRunsToTheGuaranteeEvenWhenEmpty()
    {
        var buckets = HistoryAnalyzer.Analyze(Pool(PoolInfo.ArcMiracleBox, [Row(0, "a", RewardFocus.SArc), .. Pulls(14, 1), Row(15, "b", RewardFocus.SArc)])).IntervalHistogram!;

        Assert.Equal(["1–10", "11–20", "21–30", "31–40", "41–50", "51–60"], buckets.Select(bucket => bucket.Label));
        Assert.Equal([0, 1, 0, 0, 0, 0], buckets.Select(bucket => bucket.Count));
    }

    [Fact]
    public void AnEventViewCountsItsOwnPullsButIntervalsCarryAcrossEvents()
    {
        var pool = Pool(PoolInfo.LimitedCharacter,
        [
            Row(0, "a", RewardFocus.SCharacter, eventId: "Lottery_A"),
            .. Pulls(10, 1, "Lottery_A"),
            .. Pulls(20, 11, "Lottery_B"),
            Row(31, "b", RewardFocus.SCharacter, eventId: "Lottery_B"),
        ]);

        var second = HistoryAnalyzer.Analyze(pool, eventId: "Lottery_B");

        Assert.Equal(21, second.TotalPulls);
        Assert.Equal(31, Assert.Single(second.TopRankPulls).PullsSpent);
        Assert.Null(second.TopRankPity);
        Assert.Equal(2, HistoryAnalyzer.Analyze(pool).TopRankCount);
    }

    [Fact]
    public void AnEventViewListsOnlyItsOwnCosmetics()
    {
        var pool = Pool(PoolInfo.LimitedCharacter,
            Row(0, "older", RewardFocus.Glider, eventId: "Lottery_A"),
            Row(1, "newer", RewardFocus.Glider, eventId: "Lottery_B"),
            Row(2, "newest", RewardFocus.Outfit, eventId: "Lottery_B"));

        Assert.Equal(["newest", "newer"], HistoryAnalyzer.Analyze(pool, eventId: "Lottery_B").CosmeticItems.Select(item => item.RewardId));
        Assert.Equal(["newest", "newer", "older"], HistoryAnalyzer.Analyze(pool).CosmeticItems.Select(item => item.RewardId));
    }

    [Fact]
    public void EventsAreNamedAfterTheirMostFrequentTopRankNewestFirst()
    {
        var groups = HistoryAnalyzer.Events(Pool(PoolInfo.LimitedCharacter,
        [
            Row(0, "角色甲", RewardFocus.SCharacter, eventId: "Lottery_A"),
            Row(1, "x", eventId: "Lottery_NoTop"),
            Row(2, "角色乙", RewardFocus.SCharacter, eventId: "Lottery_B"),
            Row(3, "角色乙", RewardFocus.SCharacter, eventId: "Lottery_B"),
            Row(4, "角色甲", RewardFocus.SCharacter, eventId: "Lottery_B"),
            Row(5, "x", eventId: null),
        ]));

        Assert.Equal(["Lottery_B", "Lottery_NoTop", "Lottery_A"], groups.Select(group => group.Id));
        Assert.Equal(["角色乙", "NoTop", "角色甲"], groups.Select(group => group.Name));
        Assert.Equal([3, 0, 1], groups.Select(group => group.TopRankCount));
    }

    [Fact]
    public void EventIdSurvivesExportAndImport()
    {
        var row = Row(0, eventId: "Lottery_MingYin");
        row.PoolGroupId = PoolInfo.LimitedCharacter.Id;
        var plain = Row(1);
        plain.PoolGroupId = PoolInfo.LimitedCharacter.Id;
        var json = HistoryJsonWriter.ToJson(HistoryJsonWriter.BuildHistory(new PoolHistory(PoolInfo.LimitedCharacter, [row, plain], [], []), new ExportContext("test")));

        var rows = ImportedFiles.ReadHistory(Encoding.UTF8.GetBytes(json)).Rows;

        Assert.Equal(["Lottery_MingYin", null], rows.Select(item => item.EventId));
    }
}
