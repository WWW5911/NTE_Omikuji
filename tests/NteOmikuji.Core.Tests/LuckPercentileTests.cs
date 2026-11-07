using NteOmikuji.Core.Analysis;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class LuckPercentileTests
{
    private static readonly PityRule Board = PoolInfo.LimitedCharacter.Pity!;
    private static readonly PityRule Arc = PoolInfo.ArcMiracleBox.Pity!;

    /// <summary>Expected values come from a separate enumeration of every outcome.</summary>
    [Theory]
    [InlineData(2, 200, 0, 52, 9.0)]
    [InlineData(1, 79, 5, 0, 2.8)]
    [InlineData(1, 10, 1, 5, 95.5)]
    [InlineData(1, 10, 64, 1, 54.9)]
    [InlineData(5, 200, 13, 64, 95.3)]
    [InlineData(2, 120, 5, 13, 47.5)]
    [InlineData(1, 20, 0, 5, 86.5)]
    [InlineData(13, 639, 0, 52, 83.2)]
    [InlineData(6, 245, 0, 14, 90.5)]
    public void BoardsMatchAFullEnumeration(int hits, int pulls, int carried, int sinceLast, double percent)
    {
        Assert.Equal(percent, LuckModel.Score(Board, hits, pulls, carried, sinceLast)!.Percentile * 100, 0.06);
    }

    /// <summary>The tiers these real figures fall in, from the same enumeration.</summary>
    [Theory]
    [InlineData(2, 200, 0, 52, -1.34, LuckTier.Unlucky)]
    [InlineData(1, 79, 5, 0, -1.91, LuckTier.Unlucky)]
    [InlineData(1, 10, 1, 5, 1.70, LuckTier.Lucky)]
    [InlineData(1, 10, 64, 1, 0.12, LuckTier.Normal)]
    [InlineData(5, 200, 13, 64, 1.68, LuckTier.Lucky)]
    [InlineData(2, 120, 5, 13, -0.06, LuckTier.Normal)]
    [InlineData(1, 20, 0, 5, 1.10, LuckTier.Lucky)]
    [InlineData(13, 639, 0, 52, 0.96, LuckTier.SlightlyLucky)]
    [InlineData(6, 245, 0, 14, 1.31, LuckTier.Lucky)]
    public void TiersFollowTheRank(int hits, int pulls, int carried, int sinceLast, double sigma, LuckTier tier)
    {
        var score = LuckModel.Score(Board, hits, pulls, carried, sinceLast)!;

        Assert.Equal(sigma, score.Sigma, 0.01);
        Assert.Equal(tier, score.Tier);
    }

    [Fact]
    public void AMergedScoreTakesItsTierFromTheMergedRank()
    {
        var merged = LuckModel.Combine([LuckModel.Score(Board, 13, 639, 0, 52), LuckModel.Score(Board, 6, 245, 0, 14), LuckModel.Score(Arc, 7, 320, 0, 9)])!;

        Assert.Equal(1.98, merged.Sigma, 0.02);
        Assert.Equal(LuckTier.Lucky, merged.Tier);
    }

    [Theory]
    [InlineData(1, 80, 0, 0, 27.15)]
    [InlineData(1, 10, 0, 8, 96.25)]
    [InlineData(1, 10, 0, 0, 96.25)]
    [InlineData(0, 10, 0, 10, 46.37)]
    [InlineData(1, 40, 30, 1, 71.40)]
    [InlineData(2, 160, 0, 5, 14.70)]
    public void ArcDrawsMatchAFullEnumeration(int hits, int pulls, int carried, int sinceLast, double percent)
    {
        Assert.Equal(percent, LuckModel.Score(Arc, hits, pulls, carried, sinceLast)!.Percentile * 100, 0.01);
    }

    [Fact]
    public void PlacesInsideOneArcDrawTie()
    {
        var first = LuckModel.Score(Arc, 1, 20, 0, 19)!.Percentile;
        var sameDraw = LuckModel.Score(Arc, 1, 20, 0, 10)!.Percentile;
        var nextDraw = LuckModel.Score(Arc, 1, 20, 0, 9)!.Percentile;

        Assert.Equal(first, sameDraw);
        Assert.True(nextDraw < first);
    }

    [Fact]
    public void PlacesOnABoardStillCount()
    {
        Assert.NotEqual(LuckModel.Score(Board, 1, 10, 0, 9)!.Percentile, LuckModel.Score(Board, 1, 10, 0, 8)!.Percentile);
    }

    [Fact]
    public void ArcRatesMatchTheFiguresPlayersReport()
    {
        // One run from a limited reward to the next, tracking pulls since the last S.
        var states = new double[Arc.TopRankAt];
        states[0] = 1;
        double meanPulls = 0, meanS = 0;
        for (var pull = 1; pull <= Arc.LimitedAt; pull++)
        {
            var next = new double[Arc.TopRankAt];
            for (var since = 0; since < Arc.TopRankAt; since++)
            {
                var chance = states[since];
                if (pull == Arc.LimitedAt)
                {
                    meanPulls += pull * chance;
                    meanS += chance;
                    continue;
                }
                var rate = since + 1 == Arc.TopRankAt ? 1 : Arc.BaseRate;
                var limited = chance * rate * Arc.LimitedChance!.Value;
                meanPulls += pull * limited;
                meanS += chance * rate;
                next[0] += chance * rate * (1 - Arc.LimitedChance.Value);
                if (rate < 1)
                {
                    next[since + 1] += chance * (1 - rate);
                }
            }
            states = next;
        }

        Assert.Equal(1.68, 100 / meanPulls, 0.01);
        Assert.Equal(4.19, 100 * meanS / meanPulls, 0.01);
        Assert.Equal(meanPulls, LuckModel.PullDistribution(Arc).Select((chance, index) => chance * (index + 1)).Sum(), 6);
    }

    [Fact]
    public void ALimitedArcResetsTheSGuarantee()
    {
        var rows = new List<HistoryRow>();
        for (var i = 1; i <= 25; i++)
        {
            rows.Add(new HistoryRow
            {
                Uid = "row-" + i,
                RewardId = "x",
                TimestampDecoded = new DateTime(2026, 6, 1).AddHours(i).ToString("yyyy-MM-dd HH:mm:ss"),
                Focus = i == 20 ? RewardFocus.SLimitedArc : RewardFocus.None,
                EventId = "ForkLottery_A",
            });
        }
        rows.Reverse();

        var data = HistoryAnalyzer.Analyze(new PoolHistory(PoolInfo.ArcMiracleBox, rows, [], []));

        Assert.Equal((5, 5), (data.TopRankPity!.Current, data.LimitedPity!.Current));
    }

    [Fact]
    public void ArcsMatchAFullEnumeration()
    {
        Assert.Equal(91.6, LuckModel.Score(Arc, 7, 320, 0, 9)!.Percentile * 100, 0.06);
    }

    [Fact]
    public void PoolsMergeToTheEnumeratedFigure()
    {
        var merged = LuckModel.Combine([LuckModel.Score(Board, 13, 639, 0, 52), LuckModel.Score(Board, 6, 245, 0, 14), LuckModel.Score(Arc, 7, 320, 0, 9)])!;

        Assert.Equal(97.6, merged.Percentile * 100, 0.1);
    }

    [Fact]
    public void NoRewardCountsItsWholeShareAsATie()
    {
        Assert.Equal(45.3, LuckModel.Score(Board, 0, 10, 0, 10)!.Percentile * 100, 0.06);
    }

    [Fact]
    public void WithTheSameCountAnEarlierRewardRanksHigher()
    {
        var early = LuckModel.Score(Board, 1, 10, 0, 9)!.Percentile;
        var late = LuckModel.Score(Board, 1, 10, 0, 0)!.Percentile;

        Assert.True(early > late);
        Assert.Equal(99.1, early * 100, 0.06);
        Assert.Equal(91.0, late * 100, 0.06);
    }

    [Fact]
    public void MoreRewardsAlwaysRankHigher()
    {
        Assert.True(LuckModel.Score(Board, 3, 200, 0, 199)!.Percentile > LuckModel.Score(Board, 2, 200, 0, 0)!.Percentile);
    }

    [Theory]
    [InlineData(0.9779, "前 2.2% 歐")]
    [InlineData(0.09, "前 91% 歐")]
    [InlineData(0.5, "前 50% 歐")]
    [InlineData(0.9, "前 10% 歐")]
    [InlineData(1, "前 0.1% 歐")]
    [InlineData(0, "前 99.9% 歐")]
    public void TheTextRoundsToSuit(double percentile, string expected)
    {
        Assert.Equal(expected, new LuckScore(1, 1, 1, 1, Percentile: percentile).TopText);
    }

    [Fact]
    public void AnalysisUsesThePullsSinceTheLastReward()
    {
        var rows = new List<HistoryRow>();
        for (var i = 1; i <= 200; i++)
        {
            rows.Add(new HistoryRow
            {
                Uid = "row-" + i,
                RewardId = "x",
                ResultType = "dice",
                Dice = 3,
                TimestampDecoded = new DateTime(2026, 5, 1).AddHours(i).ToString("yyyy-MM-dd HH:mm:ss"),
                Focus = i is 76 or 148 ? RewardFocus.SLimitedCharacter : RewardFocus.None,
                EventId = "Lottery_A",
            });
        }
        rows.Reverse();

        var data = HistoryAnalyzer.Analyze(new PoolHistory(PoolInfo.LimitedCharacter, rows, [], []), eventId: "Lottery_A");

        Assert.Equal("前 91% 歐", data.Luck!.TopText);
    }
}
