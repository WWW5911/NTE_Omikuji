using NteOmikuji.Core.Export;
using NteOmikuji.Core.Mapping;
using NteOmikuji.Core.Protocol;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class BackupTests
{
    private const string Uid = "214000000000";
    private static readonly RewardCatalog Catalog = RewardCatalog.Empty;

    private static HistoryRow Row(string uid, string reward = "r1") => new()
    {
        Uid = uid,
        PoolGroupId = BannerInfo.LimitedCharacter.Id,
        TimestampDecoded = "2026-01-01 00:00:00",
        RewardId = reward,
        ResultType = "dice",
        Dice = 3,
    };

    private static HistoryLibrary Saved(TempFolder folder, params HistoryRow[] rows)
    {
        var library = new HistoryLibrary(folder.Path);
        library.Merge(Uid, HistoryKind.LimitedCharacter, rows, null, "live_capture", null, Catalog);
        library.Save(Catalog);
        return library;
    }

    private static string[] Backups(TempFolder folder)
    {
        var backups = Path.Combine(folder.Path, "backups");
        return Directory.Exists(backups) ? Directory.GetFiles(backups) : [];
    }

    private static string SavedFile(TempFolder folder) => Path.Combine(folder.Path, $"{Uid}_Limited.json");

    [Fact]
    public void TheFirstSaveHasNothingToBackUp()
    {
        using var folder = new TempFolder();

        Saved(folder, Row("a"));

        Assert.Empty(Backups(folder));
    }

    [Fact]
    public void ChangingASavedFileKeepsTheOldVersion()
    {
        using var folder = new TempFolder();
        Saved(folder, Row("a"));
        var before = File.ReadAllText(SavedFile(folder));

        var library = new HistoryLibrary(folder.Path);
        library.Load(Catalog);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("b")], null, null, null, Catalog);
        library.Save(Catalog);

        Assert.Equal(before, File.ReadAllText(Assert.Single(Backups(folder))));
        Assert.NotEqual(before, File.ReadAllText(SavedFile(folder)));
    }

    [Fact]
    public void ASessionBacksUpOnlyBeforeItsFirstChange()
    {
        using var folder = new TempFolder();
        Saved(folder, Row("a"));

        var library = new HistoryLibrary(folder.Path);
        library.Load(Catalog);
        foreach (var uid in new[] { "b", "c", "d" })
        {
            library.Merge(Uid, HistoryKind.LimitedCharacter, [Row(uid)], null, null, null, Catalog);
            library.Save(Catalog);
        }

        Assert.Single(Backups(folder));
    }

    [Fact]
    public void SavingIdenticalContentDoesNotBackUp()
    {
        using var folder = new TempFolder();
        Saved(folder, Row("a"));

        var library = new HistoryLibrary(folder.Path);
        library.Load(Catalog);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("a")], null, "live_capture", null, Catalog);
        library.Save(Catalog);

        Assert.Empty(Backups(folder));
    }

    [Fact]
    public void OnlyTheNewestBackupsAreKept()
    {
        using var folder = new TempFolder();
        Saved(folder, Row("a"));
        var backups = Directory.CreateDirectory(Path.Combine(folder.Path, "backups")).FullName;
        for (var i = 0; i < HistoryLibrary.BackupsPerFile + 3; i++)
        {
            File.WriteAllText(Path.Combine(backups, $"{Uid}_Limited.20200101-0000{i:00}.json"), "old");
        }
        File.WriteAllText(Path.Combine(backups, "other_Limited.20200101-000000.json"), "another file");

        var library = new HistoryLibrary(folder.Path);
        library.Load(Catalog);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("b")], null, null, null, Catalog);
        library.Save(Catalog);

        var kept = Directory.GetFiles(backups, $"{Uid}_Limited.*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(HistoryLibrary.BackupsPerFile, kept.Count);
        Assert.DoesNotContain($"{Uid}_Limited.20200101-000000.json", kept);
        Assert.Contains(kept, name => !name!.StartsWith($"{Uid}_Limited.2020", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(backups, "other_Limited.20200101-000000.json")));
    }

    [Fact]
    public void BackupsAreNotReadBackAsHistory()
    {
        using var folder = new TempFolder();
        Saved(folder, Row("a"));
        var library = new HistoryLibrary(folder.Path);
        library.Load(Catalog);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("b")], null, null, null, Catalog);
        library.Save(Catalog);
        Assert.Single(Backups(folder));

        var reloaded = new HistoryLibrary(folder.Path);

        // Reading the backup too would bring the replaced row back.
        Assert.Empty(reloaded.Load(Catalog));
        Assert.Equal("b", reloaded.Histories(Uid).Single().Rows.Single().Uid);
    }

    [Fact]
    public void AFailedBackupStopsTheSave()
    {
        using var folder = new TempFolder();
        Saved(folder, Row("a"));
        var before = File.ReadAllText(SavedFile(folder));
        File.WriteAllText(Path.Combine(folder.Path, "backups"), "a file where the folder belongs");

        var library = new HistoryLibrary(folder.Path);
        library.Load(Catalog);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("b")], null, null, null, Catalog);

        Assert.ThrowsAny<IOException>(() => library.Save(Catalog));
        Assert.Equal(before, File.ReadAllText(SavedFile(folder)));
    }
}

public class HistoryCsvTests
{
    private static BannerHistory Limited(params HistoryRow[] rows) => new(BannerInfo.LimitedCharacter, rows, [], []);

    private static HistoryRow Row(string reward = "fork_vine", string result = "dice", int? dice = 3, string? banner = "Lottery_A") => new()
    {
        Uid = "secret-row-uid",
        TimestampDecoded = "2026-01-02 03:04:05",
        RewardId = reward,
        RewardName = "Be Happy",
        RewardRank = "B",
        RewardType = "arc",
        Quantity = 1,
        ResultType = result,
        Dice = dice,
        BannerId = banner,
        Focus = RewardFocus.None,
    };

    private static CsvRow Parse(BannerHistory history, Func<string, string?>? bannerName = null) =>
        CsvTable.Parse(HistoryCsv.ToText([history], row => "笑口常開", bannerName ?? (_ => "明音凜"))).Rows.Single();

    [Fact]
    public void ARowCarriesReadableFields()
    {
        var row = Parse(Limited(Row()));

        Assert.Equal("限定棋盤", row.Get("卡池"));
        Assert.Equal(("明音凜", "Lottery_A"), (row.Get("活動"), row.Get("活動ID")));
        Assert.Equal("2026-01-02 03:04:05", row.Get("時間"));
        Assert.Equal(("骰子", "3"), (row.Get("結果"), row.Get("骰點")));
        Assert.Equal(("笑口常開", "fork_vine"), (row.Get("獎勵"), row.Get("獎勵ID")));
        Assert.Equal(("B", "arc", "1", "不關注"), (row.Get("稀有度"), row.Get("類型"), row.Get("數量"), row.Get("關注類別")));
    }

    [Theory]
    [InlineData("points_gift", 0, "集點贈禮")]
    [InlineData("chase_reward", -4, "沉眠地")]
    public void NonRollsHaveNoRollValue(string result, int dice, string label)
    {
        var row = Parse(Limited(Row(result: result, dice: dice)));

        Assert.Equal((label, ""), (row.Get("結果"), row.Get("骰點")));
    }

    [Fact]
    public void AnArcRowHasNoResultColumnValue()
    {
        var row = Parse(new BannerHistory(BannerInfo.ArcMiracleBox, [Row(result: "", dice: null)], [], []));

        Assert.Equal(("弧盤研募", "", ""), (row.Get("卡池"), row.Get("結果"), row.Get("骰點")));
    }

    [Fact]
    public void TheAccountIsNotInTheFile()
    {
        var text = HistoryCsv.ToText([Limited(Row())], row => "x", _ => null);

        Assert.DoesNotContain("secret-row-uid", text);
        Assert.DoesNotContain("UID", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void TextThatLooksLikeAFormulaIsNeutralized(string reward)
    {
        var row = Parse(Limited(Row(reward: reward)));

        Assert.Equal("'" + reward, row.Get("獎勵ID"));
    }

    [Fact]
    public void AGuardedNameIsNeutralizedToo()
    {
        var text = HistoryCsv.ToText([Limited(Row())], _ => "=cmd", _ => "=evil");
        var row = CsvTable.Parse(text).Rows.Single();

        Assert.Equal(("'=cmd", "'=evil"), (row.Get("獎勵"), row.Get("活動")));
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        Assert.Equal("a=b", HistoryCsv.Guard("a=b"));
        Assert.Equal("", HistoryCsv.Guard(""));
    }

    [Fact]
    public void CommasAndQuotesSurviveARoundTrip()
    {
        var row = CsvTable.Parse(HistoryCsv.ToText([Limited(Row())], _ => "「電音」,\"狂歡\"", _ => null)).Rows.Single();

        Assert.Equal("「電音」,\"狂歡\"", row.Get("獎勵"));
    }

    [Fact]
    public void EveryPoolIsWritten()
    {
        var text = HistoryCsv.ToText([Limited(Row()), new BannerHistory(BannerInfo.Permanent, [Row(), Row()], [], [])], _ => "x", _ => null);

        Assert.Equal(3, CsvTable.Parse(text).Rows.Count);
    }
}
