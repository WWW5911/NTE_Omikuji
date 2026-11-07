using NteOmikuji.Core.Analysis;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class LuckModelTests
{
    private static readonly PityRule Board = PoolInfo.LimitedCharacter.Pity!;
    private static readonly PityRule Arc = PoolInfo.ArcMiracleBox.Pity!;

    private static (double Mean, double Deviation) Stats(double[] pmf)
    {
        var mean = pmf.Select((chance, index) => chance * (index + 1)).Sum();
        var square = pmf.Select((chance, index) => chance * (index + 1) * (index + 1)).Sum();
        return (mean, Math.Sqrt(square - (mean * mean)));
    }

    /// <summary>Counts rewards over many simulated players, as a check that does not share the model's arithmetic.</summary>
    private static (double Mean, double Variance) Simulate(double[] pmf, int pulls, int players = 60000)
    {
        var random = new Random(7);
        var counts = new double[players];
        for (var player = 0; player < players; player++)
        {
            var used = 0;
            while (true)
            {
                var roll = random.NextDouble();
                var gap = 0;
                for (var cumulative = pmf[0]; roll > cumulative && gap < pmf.Length - 1; cumulative += pmf[gap])
                {
                    gap++;
                }
                used += gap + 1;
                if (used > pulls)
                {
                    break;
                }
                counts[player]++;
            }
        }
        var mean = counts.Average();
        return (mean, counts.Sum(count => (count - mean) * (count - mean)) / players);
    }

    [Fact]
    public void BoardRatesReproduceThePublishedExpectation()
    {
        var pmf = LuckModel.PullDistribution(Board);

        Assert.Equal(1, pmf.Sum(), 9);
        // Published as about 53.5 pulls, or 1.87%.
        Assert.Equal(53.2, Stats(pmf).Mean, 0.1);
        Assert.InRange(1 / Stats(pmf).Mean, 0.0185, 0.0190);
    }

    [Fact]
    public void ArcRatesReproduceThePublishedLimitedExpectation()
    {
        var pmf = LuckModel.PullDistribution(Arc);

        Assert.Equal(1, pmf.Sum(), 9);
        // Published as about 59.5 pulls.
        Assert.Equal(59.4, Stats(pmf).Mean, 0.1);
        Assert.Equal(80, pmf.Length);
    }

    [Fact]
    public void BoardGuaranteeEndsTheDistribution()
    {
        var pmf = LuckModel.PullDistribution(Board);

        Assert.Equal(90, pmf.Length);
        Assert.True(pmf[70] > pmf[69] * 10, "the odds rise after pull 70");
    }

    [Theory]
    [InlineData(639)]
    [InlineData(245)]
    [InlineData(100)]
    public void ExactMomentsMatchASimulationOnBoards(int pulls)
    {
        var score = LuckModel.Score(Board, 0, pulls)!;
        var simulated = Simulate(LuckModel.PullDistribution(Board), pulls);

        Assert.Equal(simulated.Mean, score.Expected, 0.04);
        Assert.Equal(simulated.Variance, score.Variance, 0.1);
    }

    [Fact]
    public void ExactMomentsMatchASimulationOnArcs()
    {
        var score = LuckModel.Score(Arc, 0, 320)!;
        var simulated = Simulate(LuckModel.PullDistribution(Arc), 320);

        Assert.Equal(simulated.Mean, score.Expected, 0.04);
        Assert.Equal(simulated.Variance, score.Variance, 0.1);
    }

    [Fact]
    public void ShortHistoriesExpectFewerThanTheMeanRateSuggests()
    {
        var score = LuckModel.Score(Board, 0, 639)!;

        Assert.True(score.Expected < 639 / Stats(LuckModel.PullDistribution(Board)).Mean - 0.3);
    }

    [Theory]
    [InlineData(-3.0, LuckTier.VeryUnlucky, "非酋")]
    [InlineData(-2.0, LuckTier.VeryUnlucky, "非酋")]
    [InlineData(-1.99, LuckTier.Unlucky, "非洲")]
    [InlineData(-1.0, LuckTier.Unlucky, "非洲")]
    [InlineData(-0.99, LuckTier.SlightlyUnlucky, "微非")]
    [InlineData(-0.5, LuckTier.SlightlyUnlucky, "微非")]
    [InlineData(-0.49, LuckTier.Normal, "正常")]
    [InlineData(0, LuckTier.Normal, "正常")]
    [InlineData(0.49, LuckTier.Normal, "正常")]
    [InlineData(0.5, LuckTier.SlightlyLucky, "微歐")]
    [InlineData(0.99, LuckTier.SlightlyLucky, "微歐")]
    [InlineData(1.0, LuckTier.Lucky, "歐洲")]
    [InlineData(1.99, LuckTier.Lucky, "歐洲")]
    [InlineData(2.0, LuckTier.VeryLucky, "歐皇")]
    [InlineData(3.5, LuckTier.VeryLucky, "歐皇")]
    public void TiersFollowStandardDeviations(double sigma, LuckTier tier, string label)
    {
        Assert.Equal(tier, LuckModel.TierOf(sigma));
        Assert.Equal(label, LuckModel.LabelOf(tier));
    }

    [Fact]
    public void MoreRewardsMeanBetterLuck()
    {
        var sigmas = Enumerable.Range(0, 25).Select(hits => LuckModel.Score(Board, hits, 639)!.Sigma).ToList();

        Assert.Equal(sigmas.Order(), sigmas);
        Assert.Equal(LuckTier.VeryUnlucky, LuckModel.Score(Board, 5, 639)!.Tier);
        Assert.Equal(LuckTier.VeryLucky, LuckModel.Score(Board, 20, 639)!.Tier);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(89)]
    [InlineData(90)]
    public void BoardsScoreAnyNumberOfPulls(int pulls)
    {
        Assert.NotNull(LuckModel.Score(Board, 1, pulls));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(79)]
    [InlineData(80)]
    public void ArcsScoreAnyNumberOfPulls(int pulls)
    {
        Assert.NotNull(LuckModel.Score(Arc, 1, pulls));
    }

    [Fact]
    public void CombiningAddsCountsAndVariancesAndSkipsMissingPools()
    {
        var board = LuckModel.Score(Board, 13, 639)!;
        var arc = LuckModel.Score(Arc, 7, 320)!;

        var combined = LuckModel.Combine([board, null, arc])!;

        Assert.Equal(20, combined.Hits);
        Assert.Equal(959, combined.Pulls);
        Assert.Equal(board.Expected + arc.Expected, combined.Expected, 9);
        Assert.Equal(board.Variance + arc.Variance, combined.Variance, 9);
        Assert.Null(LuckModel.Combine([null, null]));
    }

    [Fact]
    public void RepeatedScoresAgreeWhicheverLengthComesFirst()
    {
        var longFirst = LuckModel.Score(Board, 3, 400)!;
        var shortAfter = LuckModel.Score(Board, 3, 150)!;
        var longAgain = LuckModel.Score(Board, 3, 400)!;

        Assert.Equal(longFirst, longAgain);
        Assert.True(shortAfter.Expected < longFirst.Expected);
    }
}
