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
/// <param name="IsSmallSample">True under one full guarantee of pulls, where the tier leans on very few rewards.</param>
/// <param name="Percentile">The exact share of outcomes from these pulls that are worse, counting ties as half.</param>
public sealed record LuckScore(int Hits, int Pulls, double Expected, double Variance, bool IsSmallSample = false, double Percentile = 0.5)
{
    public double StandardDeviation => Math.Sqrt(Variance);

    /// <summary>Standard deviations above the expected count. Zero when the outcome could not have differed.</summary>
    public double Sigma => Variance > 0 ? (Hits - Expected) / StandardDeviation : 0;

    public LuckTier Tier => LuckModel.TierOf(Sigma);

    public string Label => LuckModel.LabelOf(Tier);

    /// <summary>Where this falls among the outcomes these pulls can bring, luckiest first. It compares against the odds, not other players.</summary>
    public string TopText
    {
        get
        {
            var top = Math.Clamp((1 - Percentile) * 100, 0.1, 99.9);
            return $"前 {top.ToString(top is < 9.95 or > 99 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture)}%";
        }
    }
}

/// <summary>
/// Scores luck from the published rates, by working out exactly how many target
/// rewards a number of pulls brings. An approximation from the mean alone overstates
/// short histories, because pity makes the first reward arrive late.
/// </summary>
public static class LuckModel
{
    private static readonly Dictionary<PityRule, Moments> Cache = [];
    private static readonly Dictionary<(PityRule, int, int, int, int?), double> Percentiles = [];
    private const int MaxCachedPercentiles = 4096;

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

    /// <summary>Null only without any pulls. The exact moments hold for few pulls too, where missing out is hardly unlucky and a hit is very lucky.</summary>
    /// <param name="carried">Pulls already made toward the next reward when these began, as when an event inherits pity.</param>
    /// <param name="sinceLast">Pulls since the last reward, which tells apart outcomes with the same count. Without it they tie.</param>
    public static LuckScore? Score(PityRule rule, int hits, int pulls, int carried = 0, int? sinceLast = null)
    {
        if (rule.BaseRate <= 0 || pulls <= 0)
        {
            return null;
        }
        var (mean, square) = MomentsAfter(rule, pulls, carried);
        return new LuckScore(hits, pulls, mean, Math.Max(0, square - (mean * mean)), pulls + carried < (rule.LimitedAt ?? rule.TopRankAt), PercentileOf(rule, hits, pulls, carried, sinceLast));
    }

    /// <summary>
    /// Ranks every outcome of these pulls exactly: more rewards is better, and with the same count, the last
    /// one landing earlier is better. This holds at any sample size, where a normal approximation of the
    /// skewed count does not.
    /// </summary>
    private static double PercentileOf(PityRule rule, int hits, int pulls, int carried, int? sinceLast)
    {
        lock (Cache)
        {
            var key = (rule, hits, pulls, carried, sinceLast);
            if (Percentiles.TryGetValue(key, out var known))
            {
                return known;
            }
            if (!Cache.TryGetValue(rule, out var moments))
            {
                moments = new Moments(PullDistribution(rule));
                Cache[rule] = moments;
            }
            var pmf = moments.Pmf;
            var survival = 1 - pmf.Take(carried).Sum();
            var first = carried > 0 && carried < pmf.Length && survival > 0 ? pmf.Skip(carried).Select(chance => chance / survival).ToArray() : pmf;

            double result;
            if (hits <= 0)
            {
                result = (1 - first.Take(pulls).Sum()) / 2;
            }
            else
            {
                // waiting[m] is the chance that the last counted reward lands exactly on pull m.
                var waiting = new double[pulls + 1];
                Array.Copy(first, 0, waiting, 1, Math.Min(first.Length, pulls));
                for (var i = 1; i < hits; i++)
                {
                    waiting = Convolve(waiting, pmf, pulls);
                }
                var below = 1 - waiting.Sum();
                double same = 0;
                for (var last = 1; last <= pulls; last++)
                {
                    var since = pulls - last;
                    var chance = waiting[last] * Math.Max(0, 1 - pmf.Take(since).Sum());
                    if (sinceLast is not { } actual || since == actual)
                    {
                        same += chance;
                    }
                    else if (since < actual)
                    {
                        below += chance;
                    }
                }
                result = below + (same / 2);
            }
            result = Math.Clamp(result, 0, 1);
            if (Percentiles.Count >= MaxCachedPercentiles)
            {
                Percentiles.Clear();
            }
            Percentiles[key] = result;
            return result;
        }
    }

    private static double[] Convolve(double[] waiting, double[] pmf, int limit)
    {
        var next = new double[limit + 1];
        for (var at = 1; at <= limit; at++)
        {
            if (waiting[at] == 0)
            {
                continue;
            }
            for (var gap = 1; gap <= pmf.Length && at + gap <= limit; gap++)
            {
                next[at + gap] += waiting[at] * pmf[gap - 1];
            }
        }
        return next;
    }

    /// <summary>Standard normal cumulative probability, by Abramowitz and Stegun 26.2.17.</summary>
    internal static double NormalCdf(double z)
    {
        var x = Math.Abs(z);
        var t = 1 / (1 + (0.2316419 * x));
        var tail = Math.Exp(-x * x / 2) / Math.Sqrt(2 * Math.PI)
            * t * (0.319381530 + (t * (-0.356563782 + (t * (1.781477937 + (t * (-1.821255978 + (t * 1.330274429))))))));
        return z >= 0 ? 1 - tail : tail;
    }

    private static double InverseNormalCdf(double probability)
    {
        var target = Math.Clamp(probability, 1e-6, 1 - 1e-6);
        double low = -6, high = 6;
        for (var i = 0; i < 60; i++)
        {
            var middle = (low + high) / 2;
            if (NormalCdf(middle) < target)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }
        return (low + high) / 2;
    }

    /// <summary>Pools are independent, so their expected counts and variances add. Their ranks merge as normal scores weighted by how many rewards each should bring.</summary>
    public static LuckScore? Combine(IEnumerable<LuckScore?> scores)
    {
        var known = scores.OfType<LuckScore>().ToList();
        return known.Count == 0
            ? null
            : new LuckScore(known.Sum(score => score.Hits), known.Sum(score => score.Pulls), known.Sum(score => score.Expected), known.Sum(score => score.Variance), known.All(score => score.IsSmallSample), CombinedPercentile(known));
    }

    private static double CombinedPercentile(List<LuckScore> known)
    {
        var weighted = known.Sum(score => Math.Sqrt(Math.Max(score.Expected, 0)) * InverseNormalCdf(score.Percentile));
        var scale = Math.Sqrt(known.Sum(score => Math.Max(score.Expected, 0)));
        return scale > 0 ? NormalCdf(weighted / scale) : 0.5;
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

    /// <summary>Moments when the first reward is known to land past the carried pulls, after which pulls renew as usual.</summary>
    private static (double Mean, double Square) MomentsAfter(PityRule rule, int pulls, int carried)
    {
        var (fresh, freshSquare) = MomentsAt(rule, pulls);
        lock (Cache)
        {
            var moments = Cache[rule];
            var survival = 1 - moments.Pmf.Take(carried).Sum();
            // History past the guarantee cannot happen under the rule, so there is nothing to condition on.
            if (carried <= 0 || carried >= moments.Pmf.Length || survival <= 0)
            {
                return (fresh, freshSquare);
            }
            double mean = 0, square = 0;
            for (var first = 1; first <= Math.Min(pulls, moments.Pmf.Length - carried); first++)
            {
                var chance = moments.Pmf[carried + first - 1] / survival;
                var restMean = moments.Mean[pulls - first];
                mean += chance * (1 + restMean);
                square += chance * (1 + (2 * restMean) + moments.Square[pulls - first]);
            }
            return (mean, square);
        }
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
