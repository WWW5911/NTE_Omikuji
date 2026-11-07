using NteOmikuji.Core.Analysis;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class EventLuckTests
{
    private static readonly PityRule Board = BannerInfo.LimitedCharacter.Pity!;
    private static readonly PityRule Arc = BannerInfo.ArcMiracleBox.Pity!;

    private static HistoryRow Row(int second, string banner, RewardFocus focus = RewardFocus.None) => new()
    {
        Uid = "row-" + second,
        RewardId = "x",
        RewardName = "x",
        ResultType = "dice",
        Dice = 3,
        TimestampDecoded = new DateTime(2026, 5, 1).AddHours(second).ToString("yyyy-MM-dd HH:mm:ss"),
        Focus = focus,
        BannerId = banner,
    };

    /// <summary>Rows newest first: pulls on one event without an S, then pulls on another with rewards at the given positions, counted from 1.</summary>
    private static BannerHistory History(BannerInfo banner, int first, int second, RewardFocus focus = RewardFocus.SLimitedCharacter, params int[] hitsAt)
    {
        var rows = new List<HistoryRow>();
        for (var i = 0; i < first; i++)
        {
            rows.Add(Row(i, "Lottery_Old"));
        }
        for (var i = 1; i <= second; i++)
        {
            rows.Add(Row(first + i, "Lottery_New", hitsAt.Contains(i) ? focus : RewardFocus.None));
        }
        rows.Reverse();
        return new BannerHistory(banner, rows, [], []);
    }

    private static double BoardRate(int pull) =>
        pull >= Board.TopRankAt ? 1 : pull > Board.RisesFrom ? Board.RaisedRate!.Value : Board.BaseRate;

    /// <summary>Keeps only runs that reach the carried count without a reward, as the history shows.</summary>
    private static (double Mean, double Variance) SimulateBoard(int pulls, int carried, int runs = 100_000)
    {
        var random = new Random(1108);
        double sum = 0, squares = 0;
        var done = 0;
        while (done < runs)
        {
            var survived = true;
            for (var i = 1; i <= carried && survived; i++)
            {
                survived = random.NextDouble() >= BoardRate(i);
            }
            if (!survived)
            {
                continue;
            }
            var since = carried;
            var hits = 0;
            for (var i = 0; i < pulls; i++)
            {
                since++;
                if (random.NextDouble() < BoardRate(since))
                {
                    hits++;
                    since = 0;
                }
            }
            sum += hits;
            squares += hits * hits;
            done++;
        }
        var mean = sum / runs;
        return (mean, (squares / runs) - (mean * mean));
    }

    /// <summary>Follows the model's Arc rule: every S is limited by chance, and the limited guarantee overrides.</summary>
    private static (double Mean, double Variance) SimulateArc(int pulls, int carried, int runs = 100_000)
    {
        var random = new Random(1108);
        double sum = 0, squares = 0;
        var done = 0;
        while (done < runs)
        {
            int sinceTop = 0, sinceLimited = 0, hits = 0;
            var survived = true;
            for (var i = 0; i < carried + pulls && survived; i++)
            {
                sinceTop++;
                sinceLimited++;
                bool limited;
                if (sinceLimited == Arc.LimitedAt)
                {
                    limited = true;
                }
                else if (sinceTop == Arc.TopRankAt || random.NextDouble() < Arc.BaseRate)
                {
                    sinceTop = 0;
                    limited = random.NextDouble() < Arc.LimitedChance;
                }
                else
                {
                    limited = false;
                }
                if (limited)
                {
                    survived = i >= carried;
                    hits++;
                    sinceLimited = 0;
                    sinceTop = 0;
                }
            }
            if (!survived)
            {
                continue;
            }
            sum += hits;
            squares += hits * hits;
            done++;
        }
        var mean = sum / runs;
        return (mean, (squares / runs) - (mean * mean));
    }

    [Theory]
    [InlineData(100, 60)]
    [InlineData(200, 40)]
    [InlineData(30, 85)]
    public void CarriedBoardPullsMatchASimulation(int pulls, int carried)
    {
        var score = LuckModel.Score(Board, 0, pulls, carried)!;
        var (mean, variance) = SimulateBoard(pulls, carried);

        Assert.InRange(score.Expected, mean - 0.01, mean + 0.01);
        Assert.InRange(score.Variance, variance - 0.02, variance + 0.02);
    }

    [Theory]
    [InlineData(100, 60)]
    [InlineData(40, 70)]
    public void CarriedArcPullsMatchASimulation(int pulls, int carried)
    {
        var score = LuckModel.Score(Arc, 0, pulls, carried)!;
        var (mean, variance) = SimulateArc(pulls, carried);

        Assert.InRange(score.Expected, mean - 0.01, mean + 0.01);
        Assert.InRange(score.Variance, variance - 0.02, variance + 0.02);
    }

    [Fact]
    public void CarriedPullsRaiseTheExpectedCount()
    {
        Assert.True(LuckModel.Score(Board, 0, 100, 80)!.Expected > LuckModel.Score(Board, 0, 100)!.Expected);
    }

    [Fact]
    public void TheLastGuaranteedPullLeavesNoDoubt()
    {
        var score = LuckModel.Score(Board, 1, 1, 89)!;

        Assert.Equal(1, score.Expected, 6);
        Assert.Equal(0, score.Sigma, 3);
    }

    [Fact]
    public void CarriedPullsCountTowardTheSample()
    {
        Assert.True(LuckModel.Score(Board, 0, 30)!.IsSmallSample);
        Assert.True(LuckModel.Score(Board, 0, 30, 59)!.IsSmallSample);
        Assert.False(LuckModel.Score(Board, 0, 30, 60)!.IsSmallSample);
    }

    [Fact]
    public void MissingOutOverFewPullsIsNotUnlucky()
    {
        var score = LuckModel.Score(Board, 0, 10)!;

        Assert.Equal(LuckTier.Normal, score.Tier);
        Assert.InRange(score.Expected, 0.09, 0.1);
    }

    [Fact]
    public void AHitOverFewPullsDependsOnWhenItLanded()
    {
        Assert.Equal(LuckTier.VeryLucky, LuckModel.Score(Board, 1, 10, 0, 9)!.Tier);
        Assert.Equal(LuckTier.Lucky, LuckModel.Score(Board, 1, 10, 0, 0)!.Tier);
    }

    [Fact]
    public void AHitNearTheGuaranteeIsNotSoLucky()
    {
        var early = LuckModel.Score(Board, 1, 10)!;
        var late = LuckModel.Score(Board, 1, 10, 64)!;

        Assert.True(late.Sigma < early.Sigma);
    }

    [Fact]
    public void NoPullsHaveNoScore()
    {
        Assert.Null(LuckModel.Score(Board, 0, 0));
    }

    [Fact]
    public void CombinedScoresAreSmallOnlyWhenAllAre()
    {
        var small = LuckModel.Score(Board, 0, 10);
        var full = LuckModel.Score(Board, 2, 200);

        Assert.True(LuckModel.Combine([small, small])!.IsSmallSample);
        Assert.False(LuckModel.Combine([small, full])!.IsSmallSample);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(500)]
    public void CarryPastTheGuaranteeIsIgnored(int carried)
    {
        var score = LuckModel.Score(Board, 1, 100, carried)!;

        Assert.Equal(LuckModel.Score(Board, 1, 100)!.Expected, score.Expected, 6);
    }

    [Fact]
    public void AnEventInheritsPullsFromTheOneBefore()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.LimitedCharacter, 60, 40, hitsAt: 10), bannerId: "Lottery_New");

        Assert.Equal(60, data.CarriedPulls);
        Assert.Equal(LuckModel.Score(Board, 1, 40, 60)!.Expected, data.Luck!.Expected, 6);
        Assert.Equal((1, 40), (data.Luck.Hits, data.Luck.Pulls));
    }

    [Fact]
    public void TheFirstEventInheritsNothing()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.LimitedCharacter, 100, 40, hitsAt: 10), bannerId: "Lottery_Old");

        Assert.Equal(0, data.CarriedPulls);
        Assert.Equal((0, 100), (data.Luck!.Hits, data.Luck.Pulls));
    }

    [Fact]
    public void AnEventIsJudgedOnItsOwnHits()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.LimitedCharacter, 100, 100, hitsAt: [50, 95]), bannerId: "Lottery_New");

        Assert.Equal(2, data.Luck!.Hits);
    }

    [Fact]
    public void AnArcEventCountsLimitedRewardsAndPullsSinceTheLast()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.ArcMiracleBox, 30, 80, RewardFocus.SLimitedArc, 40), bannerId: "Lottery_New");

        Assert.Equal(30, data.CarriedPulls);
        Assert.Equal(1, data.Luck!.Hits);
    }

    [Fact]
    public void AnArcEventIgnoresOrdinaryS()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.ArcMiracleBox, 30, 80, RewardFocus.SArc, 40), bannerId: "Lottery_New");

        Assert.Equal(0, data.Luck!.Hits);
    }

    [Fact]
    public void AnUnsetEventIsNotJudged()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.LimitedCharacter, 0, 100, hitsAt: 50), null, "Lottery_New", ["Lottery_New"]);

        Assert.Null(data.Luck);
    }

    [Fact]
    public void AnEventEndsWithThePullsSinceItsLastHit()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.LimitedCharacter, 60, 40, hitsAt: 10), bannerId: "Lottery_New");

        Assert.Equal(30, data.EndedPulls);
    }

    [Fact]
    public void AnEventWithoutAHitEndsWithEverythingItInherited()
    {
        var history = History(BannerInfo.LimitedCharacter, 60, 20);

        Assert.Equal(80, HistoryAnalyzer.Analyze(history, bannerId: "Lottery_New").EndedPulls);
    }

    [Fact]
    public void AnOlderEventEndsBeforeLaterPullsCount()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.LimitedCharacter, 60, 40, hitsAt: 10), bannerId: "Lottery_Old");

        Assert.Equal(60, data.EndedPulls);
    }

    [Fact]
    public void AnArcEventEndsCountingTowardTheLimitedReward()
    {
        var limited = HistoryAnalyzer.Analyze(History(BannerInfo.ArcMiracleBox, 30, 80, RewardFocus.SLimitedArc, 40), bannerId: "Lottery_New");
        var ordinary = HistoryAnalyzer.Analyze(History(BannerInfo.ArcMiracleBox, 30, 80, RewardFocus.SArc, 40), bannerId: "Lottery_New");

        Assert.Equal((40, 110), (limited.EndedPulls, ordinary.EndedPulls));
    }

    [Fact]
    public void AWholePoolEndsAtItsCurrentCount()
    {
        Assert.Equal(30, HistoryAnalyzer.Analyze(History(BannerInfo.LimitedCharacter, 60, 40, hitsAt: 10)).EndedPulls);
    }

    [Fact]
    public void AWholePoolStillStartsFromNothing()
    {
        var data = HistoryAnalyzer.Analyze(History(BannerInfo.LimitedCharacter, 60, 40, hitsAt: 10));

        Assert.Equal(0, data.CarriedPulls);
        Assert.Equal((1, 100), (data.Luck!.Hits, data.Luck.Pulls));
    }
}
