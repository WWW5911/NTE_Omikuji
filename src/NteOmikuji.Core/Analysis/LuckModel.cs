namespace NteOmikuji.Core.Analysis;

public enum LuckTier
{
    VeryUnlucky,
    Unlucky,
    SlightlyUnlucky,
    Normal,
    SlightlyLucky,
    Lucky,
    VeryLucky,
}

/// <summary>How the rewards a player got compare with what their pull count should bring.</summary>
/// <param name="Hits">Target rewards obtained.</param>
/// <param name="Expected">Rewards this many pulls bring on average.</param>
/// <param name="Variance">Spread of that count across players making the same pulls.</param>
public sealed record LuckScore(int Hits, int Pulls, double Expected, double Variance)
{
    public double StandardDeviation => Math.Sqrt(Variance);

    /// <summary>Standard deviations above the expected count. Zero when the outcome could not have differed.</summary>
    public double Sigma => Variance > 0 ? (Hits - Expected) / StandardDeviation : 0;

    public LuckTier Tier => LuckModel.TierOf(Sigma);

    public string Label => LuckModel.LabelOf(Tier);
}

/// <summary>
/// Scores luck from the published rates, by working out exactly how many target
/// rewards a number of pulls brings. An approximation from the mean alone overstates
/// short histories, because pity makes the first reward arrive late.
/// </summary>
public static class LuckModel
{
    private static readonly Dictionary<PityRule, Moments> Cache = [];

    private sealed class Moments(double[] pmf)
    {
        public double[] Pmf { get; } = pmf;
        public List<double> Mean { get; } = [0];
        public List<double> Square { get; } = [0];
    }

    public static string LabelOf(LuckTier tier) => tier switch
    {
        LuckTier.VeryUnlucky => "非酋",
        LuckTier.Unlucky => "非洲",
        LuckTier.SlightlyUnlucky => "微非",
        LuckTier.SlightlyLucky => "微歐",
        LuckTier.Lucky => "歐洲",
        LuckTier.VeryLucky => "歐皇",
        _ => "正常",
    };

    public static LuckTier TierOf(double sigma) => sigma switch
    {
        <= -2 => LuckTier.VeryUnlucky,
        <= -1 => LuckTier.Unlucky,
        <= -0.5 => LuckTier.SlightlyUnlucky,
        < 0.5 => LuckTier.Normal,
        < 1 => LuckTier.SlightlyLucky,
        < 2 => LuckTier.Lucky,
        _ => LuckTier.VeryLucky,
    };

    /// <summary>The chance that the target reward arrives on each pull, where index 0 is the first pull.</summary>
    public static double[] PullDistribution(PityRule rule) =>
        rule.LimitedAt is { } limitedAt && rule.LimitedChance is { } chance
            ? LimitedDistribution(rule, limitedAt, chance)
            : TopRankDistribution(rule);

    /// <summary>Null below one full guarantee, since so few pulls say nothing about luck.</summary>
    public static LuckScore? Score(PityRule rule, int hits, int pulls)
    {
        if (rule.BaseRate <= 0 || pulls < (rule.LimitedAt ?? rule.TopRankAt))
        {
            return null;
        }
        var (mean, square) = MomentsAt(rule, pulls);
        return new LuckScore(hits, pulls, mean, Math.Max(0, square - (mean * mean)));
    }

    /// <summary>Pools are independent, so their expected counts and variances add.</summary>
    public static LuckScore? Combine(IEnumerable<LuckScore?> scores)
    {
        var known = scores.OfType<LuckScore>().ToList();
        return known.Count == 0
            ? null
            : new LuckScore(known.Sum(score => score.Hits), known.Sum(score => score.Pulls), known.Sum(score => score.Expected), known.Sum(score => score.Variance));
    }

    private static double[] TopRankDistribution(PityRule rule)
    {
        var pmf = new double[rule.TopRankAt];
        var alive = 1.0;
        for (var pull = 1; pull <= rule.TopRankAt; pull++)
        {
            var rate = pull == rule.TopRankAt ? 1
                : rule.RaisedRate is { } raised && pull > rule.RisesFrom ? raised
                : rule.BaseRate;
            pmf[pull - 1] = alive * rate;
            alive *= 1 - rate;
        }
        return pmf;
    }

    /// <summary>Walks pulls since the last S, because an off-banner S resets the S guarantee but not the limited one.</summary>
    private static double[] LimitedDistribution(PityRule rule, int limitedAt, double chance)
    {
        var pmf = new double[limitedAt];
        var states = new double[rule.TopRankAt];
        states[0] = 1;
        for (var pull = 1; pull <= limitedAt; pull++)
        {
            var next = new double[rule.TopRankAt];
            for (var since = 0; since < rule.TopRankAt; since++)
            {
                var chanceOfThis = states[since];
                if (chanceOfThis == 0)
                {
                    continue;
                }
                if (pull == limitedAt)
                {
                    pmf[pull - 1] += chanceOfThis;
                    continue;
                }
                var rate = since + 1 == rule.TopRankAt ? 1 : rule.BaseRate;
                pmf[pull - 1] += chanceOfThis * rate * chance;
                next[0] += chanceOfThis * rate * (1 - chance);
                if (rate < 1)
                {
                    next[since + 1] += chanceOfThis * (1 - rate);
                }
            }
            states = next;
        }
        return pmf;
    }

    /// <summary>Exact first and second moments of the reward count, by conditioning on when the first reward lands.</summary>
    private static (double Mean, double Square) MomentsAt(PityRule rule, int pulls)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(rule, out var moments))
            {
                moments = new Moments(PullDistribution(rule));
                Cache[rule] = moments;
            }
            for (var t = moments.Mean.Count; t <= pulls; t++)
            {
                double mean = 0, square = 0;
                for (var first = 1; first <= Math.Min(t, moments.Pmf.Length); first++)
                {
                    var chance = moments.Pmf[first - 1];
                    var restMean = moments.Mean[t - first];
                    mean += chance * (1 + restMean);
                    square += chance * (1 + (2 * restMean) + moments.Square[t - first]);
                }
                moments.Mean.Add(mean);
                moments.Square.Add(square);
            }
            return (moments.Mean[pulls], moments.Square[pulls]);
        }
    }
}
