using System.Text.Json;
using NteOmikuji.Core.Analysis;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class ShareCardTests
{
    private const string Uid = "214283000156";
    private static readonly DateOnly Today = new(2026, 11, 7);

    private static HistoryRow Row(int second, RewardFocus focus = RewardFocus.None, string name = "x", string? banner = null) => new()
    {
        Uid = "row-" + second,
        RewardId = name,
        RewardName = name,
        ResultType = "dice",
        Dice = 3,
        TimestampDecoded = new DateTime(2026, 5, 1).AddHours(second).ToString("yyyy-MM-dd HH:mm:ss"),
        Focus = focus,
        BannerId = banner,
    };

    /// <summary>Rows newest first, with a top-rank reward after each gap, oldest gap first.</summary>
    private static BannerHistory Pool(BannerInfo banner, params (int Gap, RewardFocus Focus, string Name)[] rewards)
    {
        var rows = new List<HistoryRow>();
        var second = 0;
        foreach (var (gap, focus, name) in rewards)
        {
            for (var i = 1; i < gap; i++)
            {
                rows.Add(Row(second++));
            }
            rows.Add(Row(second++, focus, name, "Lottery_A"));
        }
        rows.Reverse();
        return new BannerHistory(banner, rows, [], []);
    }

    /// <summary>Two events, oldest first: "Old" has an S after 30 pulls, then "New" 10 plain pulls and an S after 20 more.</summary>
    private static BannerHistory TwoEvents(BannerInfo banner)
    {
        var rows = new List<HistoryRow>();
        var second = 0;
        void Add(int count, string id, string? reward)
        {
            for (var i = 1; i < count; i++)
            {
                rows.Add(Row(second++, banner: id));
            }
            rows.Add(reward is null ? Row(second++, banner: id) : Row(second++, RewardFocus.SLimitedCharacter, reward, id));
        }
        Add(30, "Lottery_Old", "old");
        Add(10, "Lottery_New", null);
        Add(20, "Lottery_New", "new");
        rows.Reverse();
        return new BannerHistory(banner, rows, [], []);
    }

    private static SharePool Selected(BannerHistory history, ShareTarget? target) =>
        ShareCardBuilder.Build([history], null, null, Today, null, id => id == "Lottery_New" ? "明音凜" : null, target).Selected;

    private static ShareCard Card(params BannerHistory[] histories) => ShareCardBuilder.Build(histories, null, null, Today);

    [Fact]
    public void AllThreePoolsAppearInOrderEvenWhenEmpty()
    {
        var card = Card();

        Assert.Equal(["限定棋盤", "常駐棋盤", "弧盤研募"], card.Pools.Select(pool => pool.Label));
        Assert.All(card.Pools, pool => Assert.Equal("尚無紀錄", pool.LuckNote));
        Assert.Null(card.Overall);
        Assert.Equal(0, card.TotalPulls);
    }

    [Fact]
    public void RewardsAreNewestFirstAndMarkTheOldestAsALowerBound()
    {
        var card = Card(Pool(BannerInfo.LimitedCharacter,
            (40, RewardFocus.SCharacter, "first"), (60, RewardFocus.SLimitedCharacter, "second")));

        var rewards = card.Pools[0].Rewards;

        Assert.Equal([("second", 60, false, true), ("first", 40, true, false)], rewards.Select(item => (item.Name, item.Pulls, item.AtLeast, item.Limited)));
    }

    [Fact]
    public void TotalsCoverEveryPool()
    {
        var card = Card(
            Pool(BannerInfo.LimitedCharacter, (40, RewardFocus.SLimitedCharacter, "a")),
            Pool(BannerInfo.Permanent, (30, RewardFocus.SCharacter, "b")));

        Assert.Equal((70, 2, 1), (card.TotalPulls, card.TotalTop, card.TotalLimited));
        Assert.Equal("2026-05-01 00:00:00", card.FirstDate);
    }

    [Fact]
    public void OnlyLimitedPoolsCountLimited()
    {
        var card = Card(Pool(BannerInfo.Permanent, (30, RewardFocus.SCharacter, "b")));

        Assert.Null(card.Pools[1].LimitedCount);
        Assert.Equal(0, card.Pools[0].LimitedCount);
    }

    [Fact]
    public void AShortPoolHasANoteInsteadOfLuck()
    {
        var pool = Card(Pool(BannerInfo.Permanent, (30, RewardFocus.SCharacter, "b"))).Pools[1];

        Assert.Null(pool.Luck);
        Assert.Equal("樣本不足", pool.LuckNote);
    }

    [Fact]
    public void AnUnsetEventHasANoteInsteadOfLuck()
    {
        var history = Pool(BannerInfo.Permanent, (95, RewardFocus.SCharacter, "b"));

        var pool = ShareCardBuilder.Build([history], null, ["Lottery_A"], Today).Pools[1];

        Assert.Equal((null, "無法判定"), (pool.Luck, pool.LuckNote));
    }

    [Fact]
    public void AScoredPoolCarriesItsTier()
    {
        var pool = Card(Pool(BannerInfo.Permanent, (50, RewardFocus.SCharacter, "b"), (50, RewardFocus.SCharacter, "c"))).Pools[1];

        var luck = Assert.IsType<ShareLuck>(pool.Luck);
        Assert.Null(pool.LuckNote);
        Assert.Equal(LuckModel.LabelOf((LuckTier)luck.Tier), luck.Label);
        Assert.Equal((2, 100), (luck.Hits, luck.Pulls));
    }

    [Fact]
    public void AnEventCountsOnlyItsOwnPullsAndKeepsItsPool()
    {
        var selected = Selected(TwoEvents(BannerInfo.LimitedCharacter), new ShareTarget(HistoryKind.LimitedCharacter, "Lottery_New"));

        Assert.Equal(("明音凜", "限定棋盤"), (selected.Label, selected.Context));
        Assert.Equal((30, 1, 1), (selected.TotalPulls, selected.TopCount, selected.LimitedCount));
    }

    [Fact]
    public void AnEventsRewardCountsPullsAcrossEvents()
    {
        var selected = Selected(TwoEvents(BannerInfo.LimitedCharacter), new ShareTarget(HistoryKind.LimitedCharacter, "Lottery_New"));

        var reward = Assert.Single(selected.Rewards);
        Assert.Equal(("new", 30, false), (reward.Name, reward.Pulls, reward.AtLeast));
    }

    [Fact]
    public void AnEventHasNoPityOrLuckOfItsOwn()
    {
        var selected = Selected(TwoEvents(BannerInfo.LimitedCharacter), new ShareTarget(HistoryKind.LimitedCharacter, "Lottery_New"));

        Assert.Equal((null, null, null, null), (selected.Luck, selected.LuckNote, selected.Pity, selected.LimitedPity));
    }

    [Fact]
    public void AnEventKeepsItsPoolsScale()
    {
        var limited = Selected(TwoEvents(BannerInfo.LimitedCharacter), new ShareTarget(HistoryKind.LimitedCharacter, "Lottery_New"));
        var arc = Selected(TwoEvents(BannerInfo.ArcMiracleBox), new ShareTarget(HistoryKind.ArcMiracleBox, "Lottery_New"));

        Assert.Equal((90, 70), (limited.Scale, limited.RisesFrom));
        Assert.Equal((60, null), (arc.Scale, arc.RisesFrom));
    }

    [Fact]
    public void AWholePoolCanBeSelected()
    {
        var selected = Selected(TwoEvents(BannerInfo.Permanent), new ShareTarget(HistoryKind.Permanent));

        Assert.Equal(("常駐棋盤", null, 60), (selected.Label, selected.Context, selected.TotalPulls));
    }

    [Fact]
    public void AnUnknownEventFallsBackToItsPool()
    {
        var selected = Selected(TwoEvents(BannerInfo.LimitedCharacter), new ShareTarget(HistoryKind.LimitedCharacter, "Lottery_Gone"));

        Assert.Equal(("限定棋盤", 60), (selected.Label, selected.TotalPulls));
    }

    [Fact]
    public void WithoutATargetTheFirstPoolIsSelected()
    {
        Assert.Equal("限定棋盤", Selected(TwoEvents(BannerInfo.LimitedCharacter), null).Label);
    }

    [Theory]
    [InlineData(HistoryKind.Permanent, null, "Permanent")]
    [InlineData(HistoryKind.LimitedCharacter, "Lottery_New-2", "Lottery_New-2")]
    [InlineData(HistoryKind.LimitedCharacter, "..\\evil/明:音", "___evil____")]
    public void FileLabelsKeepOnlyPlainCharacters(HistoryKind kind, string? id, string expected)
    {
        Assert.Equal(expected, ShareCardBuilder.FileLabel(new ShareTarget(kind, id)));
    }

    [Fact]
    public void TheCardListsEveryTierLabelWorstFirst()
    {
        Assert.Equal(["非酋", "非洲", "微非", "正常", "微歐", "歐洲", "歐皇"], Card().TierLabels);
    }

    [Fact]
    public void TheAccountIsLeftOutUnlessAsked()
    {
        var history = Pool(BannerInfo.Permanent, (50, RewardFocus.SCharacter, "b"));

        var card = ShareCardBuilder.Build([history], null, null, Today);

        Assert.Null(card.MaskedUid);
        Assert.DoesNotContain(Uid, JsonSerializer.Serialize(card));
        Assert.DoesNotContain("row-", JsonSerializer.Serialize(card));
    }

    [Fact]
    public void AnAskedForAccountIsStillMasked()
    {
        var card = ShareCardBuilder.Build([], null, null, Today, Uid);

        Assert.Equal("214*******56", card.MaskedUid);
        Assert.DoesNotContain(Uid, JsonSerializer.Serialize(card));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("12345", null)]
    [InlineData("123456", "123*56")]
    public void ShortIdsAreNotShown(string? uid, string? expected)
    {
        Assert.Equal(expected, ShareCardBuilder.MaskUid(uid));
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(1, 0.8413)]
    [InlineData(-1, 0.1587)]
    [InlineData(2, 0.9772)]
    [InlineData(-2.16, 0.0154)]
    public void NormalCdfMatchesPublishedValues(double z, double expected)
    {
        Assert.Equal(expected, ShareCardBuilder.NormalCdf(z), 3);
    }

    [Fact]
    public void TheBeatPercentStaysInsideItsRange()
    {
        var unlucky = Card(Pool(BannerInfo.Permanent, Enumerable.Range(0, 40).Select(_ => (90, RewardFocus.SCharacter, "b")).ToArray())).Pools[1].Luck!;
        var lucky = Card(Pool(BannerInfo.Permanent, Enumerable.Range(0, 100).Select(_ => (2, RewardFocus.SCharacter, "b")).ToArray())).Pools[1].Luck!;

        Assert.InRange(unlucky.BeatsPercent, 1, 40);
        Assert.Equal(99, lucky.BeatsPercent);
    }
}
