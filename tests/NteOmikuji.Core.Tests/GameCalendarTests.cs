using NteOmikuji.Core.Export;
using NteOmikuji.Core.Mapping;
using NteOmikuji.Core.Protocol;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class GameCalendarTests
{
    private const string Uid = "214000000000";
    private const string AtLaunch = "2026-04-29 23:46:34";
    private const string Later = "2026-06-01 12:00:00";

    private static RewardCatalog Catalog() => RewardCatalog.FromTables(
        CsvTable.Parse("id,type,rank,focus,name.en\r\n1010,character,S,s_character,Nanally\r\n"),
        new CsvTable(["id", "name"]),
        [],
        CsvTable.Parse($"id,featured\r\n{GameCalendar.FirstLimitedEvent},1010\r\n"));

    private static HistoryRow Row(string time, string reward = "1010", string? banner = null, string uid = "a", int dice = 3) => new()
    {
        Uid = uid,
        TimestampDecoded = time,
        RewardId = reward,
        ResultType = "dice",
        Dice = dice,
        BannerId = banner,
    };

    private static HistoryRow Merged(HistoryKind kind, HistoryRow row)
    {
        using var folder = new TempFolder();
        var library = new HistoryLibrary(folder.Path);
        library.Merge(Uid, kind, [row], null, null, null, Catalog());
        return library.Histories(Uid).Single().Rows.Single();
    }

    [Theory]
    [InlineData("2026-04-29 00:00:00", true)]
    [InlineData("2026-05-06 23:59:59", true)]
    [InlineData("2026-05-07 00:00:00", false)]
    [InlineData("2026-04-28 23:59:59", false)]
    [InlineData("", false)]
    public void TheLaunchPeriodHasEdges(string time, bool expected)
    {
        Assert.Equal(expected, GameCalendar.IsLaunchPeriod(time));
    }

    [Fact]
    public void ALaunchRowWithoutAnEventBelongsToTheFirstOne()
    {
        var row = Merged(HistoryKind.LimitedCharacter, Row(AtLaunch));

        Assert.Equal(GameCalendar.FirstLimitedEvent, row.BannerId);
    }

    [Fact]
    public void ThatMakesItsFeaturedRewardLimited()
    {
        Assert.Equal(RewardFocus.SLimitedCharacter, Merged(HistoryKind.LimitedCharacter, Row(AtLaunch)).Focus);
    }

    [Fact]
    public void ARowAlreadyWithAnEventKeepsIt()
    {
        Assert.Equal("Lottery_Other", Merged(HistoryKind.LimitedCharacter, Row(AtLaunch, banner: "Lottery_Other")).BannerId);
    }

    [Fact]
    public void ALaterRowStaysWithoutAnEvent()
    {
        Assert.Null(Merged(HistoryKind.LimitedCharacter, Row(Later)).BannerId);
    }

    [Fact]
    public void OtherPoolsAreLeftAlone()
    {
        Assert.Null(Merged(HistoryKind.Permanent, Row(AtLaunch)).BannerId);
    }

    [Fact]
    public void RowsSavedWithoutAnEventGainItOnLoading()
    {
        using var folder = new TempFolder();
        var writing = new HistoryLibrary(folder.Path);
        writing.Merge(Uid, HistoryKind.Permanent, [Row(AtLaunch)], null, null, null, Catalog());
        var limitedFile = Path.Combine(folder.Path, $"{Uid}_Limited.json");
        var document = HistoryJsonWriter.BuildHistory(new BannerHistory(BannerInfo.LimitedCharacter, [Row(Later, uid: "b")], [], []), writing.ContextFor(Uid));
        var text = HistoryJsonWriter.ToJson(document).Replace(Later, AtLaunch, StringComparison.Ordinal);
        File.WriteAllText(limitedFile, text);

        var reading = new HistoryLibrary(folder.Path);
        Assert.Empty(reading.Load(Catalog()));

        var row = reading.Histories(Uid).Single(history => history.Banner.Kind == HistoryKind.LimitedCharacter).Rows.Single();
        Assert.Equal(GameCalendar.FirstLimitedEvent, row.BannerId);
    }
}
