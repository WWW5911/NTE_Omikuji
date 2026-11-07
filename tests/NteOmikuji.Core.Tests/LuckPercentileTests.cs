using NteOmikuji.Core.Analysis;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class LuckPercentileTests
{
    private static readonly PityRule Board = BannerInfo.LimitedCharacter.Pity!;
    private static readonly PityRule Arc = BannerInfo.ArcMiracleBox.Pity!;

    /// <summary>Expected values come from a separate Python enumeration of every outcome.</summary>
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

    [Fact]
    public void ArcsMatchAFullEnumeration()
    {
        Assert.Equal(92.5, LuckModel.Score(Arc, 7, 320, 0, 9)!.Percentile * 100, 0.06);
    }

    [Fact]
    public void PoolsMergeToTheEnumeratedFigure()
    {
        var merged = LuckModel.Combine([LuckModel.Score(Board, 13, 639, 0, 52), LuckModel.Score(Board, 6, 245, 0, 14), LuckModel.Score(Arc, 7, 320, 0, 9)])!;

        Assert.Equal(97.8, merged.Percentile * 100, 0.06);
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
    [InlineData(0.9779, "前 2.2%")]
    [InlineData(0.09, "前 91%")]
    [InlineData(0.5, "前 50%")]
    [InlineData(0.9, "前 10%")]
    [InlineData(1, "前 0.1%")]
    [InlineData(0, "前 99.9%")]
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
                BannerId = "Lottery_A",
            });
        }
        rows.Reverse();

        var data = HistoryAnalyzer.Analyze(new BannerHistory(BannerInfo.LimitedCharacter, rows, [], []), bannerId: "Lottery_A");

        Assert.Equal("前 91%", data.Luck!.TopText);
    }
}
