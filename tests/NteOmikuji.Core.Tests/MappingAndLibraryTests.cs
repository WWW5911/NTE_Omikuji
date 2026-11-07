using System.Text;
using System.Text.Json;
using NteOmikuji.Core.Analysis;
using NteOmikuji.Core.Export;
using NteOmikuji.Core.Import;
using NteOmikuji.Core.Mapping;
using NteOmikuji.Core.Protocol;
using Xunit;

namespace NteOmikuji.Core.Tests;

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("nte-tests-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

public class CsvTests
{
    [Fact]
    public void ParsesQuotesCommasNewlinesAndKeepsUnknownColumns()
    {
        var table = CsvTable.Parse("ID, Name ,note\r\na,\"x, \"\"y\"\"\nz\",hello\r\n\r\nb,short\r\n");

        Assert.Equal(["id", "name", "note"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("x, \"y\"\nz", table.Rows[0].Values["name"]);
        Assert.Equal("hello", table.Rows[0].Get("note"));
        Assert.Equal("", table.Rows[1].Get("note"));
        Assert.Equal(5, table.Rows[1].Line);
    }

    [Fact]
    public void WrittenTextParsesBackToTheSameValues()
    {
        var table = new CsvTable(["id", "name"]);
        table.Rows.Add(new CsvRow(0, new() { ["id"] = "限定,1", ["name"] = "a \"b\"\r\nc" }));

        var back = CsvTable.Parse(table.ToText());

        Assert.Equal(table.Rows[0].Values, back.Rows[0].Values);
    }

    [Fact]
    public void UnterminatedQuoteReportsItsLine()
    {
        var error = Assert.Throws<InvalidDataException>(() => CsvTable.Parse("id,name\na,\"oops\n"));
        Assert.Contains("第 2 行", error.Message);
    }

    [Fact]
    public void DecodesUtf8WithBomAndExcelBig5()
    {
        Assert.Equal("id,名稱", CsvTable.DecodeText([.. new byte[] { 0xEF, 0xBB, 0xBF }, .. Encoding.UTF8.GetBytes("id,名稱")]));
        var big5 = CodePagesEncoding().GetBytes("id,名稱");
        Assert.Equal("id,名稱", CsvTable.DecodeText(big5, fallbackCodePage: 950));
    }

    private static Encoding CodePagesEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(950);
    }
}

public class MappingStoreTests
{
    [Fact]
    public void SeedsOnceAndNeverReplacesUserEdits()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        var path = Path.Combine(folder.Path, MappingStore.RewardsFile);
        File.WriteAllText(path, "id,type,rank,name.zh-hant\r\nDice_ticket_01,item,A,我的名稱\r\n");

        store.EnsureSeeded();
        var catalog = store.Load().Catalog!;

        Assert.Equal("我的名稱", catalog.Find("dice_ticket_01")!.Name);
        Assert.Equal("A", catalog.Find("Dice_ticket_01")!.Rank);
        Assert.Null(catalog.Find("fork_nonos"));
    }

    [Fact]
    public void SeedMatchesBundledCatalog()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();

        var result = store.Load();

        Assert.Empty(result.Issues);
        Assert.Equal(136, result.Catalog!.RewardCount);
        Assert.Equal(448, result.Catalog.AchievementCount);
        Assert.Equal(RewardCatalog.Bundled.Value.RewardCount, result.Catalog.RewardCount);
        var battle = result.Catalog.FindAchievement("BATTLE_10")!;
        Assert.Equal(300, battle.Target.GetInt32());
        Assert.Equal("Annulith", battle.Rewards[0].GetProperty("item_id").GetString());
    }

    [Fact]
    public void BadRowsAreReportedAndGoodRowsStillLoad()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        File.WriteAllText(Path.Combine(folder.Path, MappingStore.RewardsFile), "id,name\r\n,沒有編號\r\na,第一\r\nA,第二\r\nb,\r\n");
        File.WriteAllText(Path.Combine(folder.Path, MappingStore.AchievementsFile), "id,name,target\r\nx,好,1\r\ny,壞,not-a-number\r\n");

        var result = store.Load();

        Assert.Contains(result.Issues, issue => issue.Contains("第 2 行") && issue.Contains("缺少 id"));
        Assert.Contains(result.Issues, issue => issue.Contains("第 4 行") && issue.Contains("重複"));
        Assert.Contains(result.Issues, issue => issue.Contains("achievements.csv") && issue.Contains("第 3 行"));
        Assert.Equal("第二", result.Catalog!.Find("a")!.Name);
        Assert.Null(result.Catalog.Find("b"));
        Assert.NotNull(result.Catalog.FindAchievement("x"));
    }

    [Fact]
    public void MissingColumnKeepsThePreviousCatalog()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        File.WriteAllText(Path.Combine(folder.Path, MappingStore.RewardsFile), "id,title\r\na,b\r\n");

        var result = store.Load();

        Assert.Null(result.Catalog);
        Assert.Contains("name", result.Issues.Single());
        Assert.Same(RewardTable.Empty, result.Rewards);
    }

    [Fact]
    public void FileOpenForWritingByAnotherProgramCanStillBeRead()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        using var excelLike = new FileStream(Path.Combine(folder.Path, MappingStore.RewardsFile), FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        Assert.NotNull(store.Load().Catalog);
    }

    [Fact]
    public void ImportingPythonStyleJsonUpsertsAndKeepsOtherRowsAndNotes()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        File.WriteAllText(Path.Combine(folder.Path, MappingStore.RewardsFile), "id,type,name,rank,note\r\nkeep,item,留著,B,我的備註\r\nA1,item,舊名,B,改名\r\n");
        var json = Encoding.UTF8.GetBytes("""{"a1": {"name": "新名", "rank": "S"}, "fork_new": {"name": "新弧光", "rank": "A"}, "1099": {"name": "新角色", "rank": "S"}}""");

        Assert.Equal(ImportKind.RewardMapping, ExportDocuments.Classify("x.json", json));
        Assert.Equal(3, store.Import(false, ExportDocuments.ReadMapping("x.json", json, false)));
        var catalog = store.Load().Catalog!;

        Assert.Equal("新名", catalog.Find("A1")!.Name);
        Assert.Equal("留著", catalog.Find("keep")!.Name);
        Assert.Equal("arc", catalog.Find("fork_new")!.Type);
        Assert.Equal("character", catalog.Find("1099")!.Type);
        Assert.Contains("我的備註", File.ReadAllText(Path.Combine(folder.Path, MappingStore.RewardsFile)));
    }

    [Theory]
    [InlineData("rewards.csv", "id,name\na,b\n", ImportKind.RewardMapping)]
    [InlineData("anything.csv", "id,name,target\na,b,1\n", ImportKind.AchievementMapping)]
    [InlineData("a.json", """{"battle_1": {"name": "n", "description": "d", "target": 1}}""", ImportKind.AchievementMapping)]
    [InlineData("h.json", """{"format": "nte-history-export"}""", ImportKind.HistoryExport)]
    [InlineData("h.json", """{"format": "nte-achievement-export"}""", ImportKind.AchievementExport)]
    [InlineData("c.flows", "", ImportKind.Flows)]
    public void ClassifiesFilesByContent(string name, string content, ImportKind expected) =>
        Assert.Equal(expected, ExportDocuments.Classify(name, Encoding.UTF8.GetBytes(content)));

    [Theory]
    [InlineData("x.txt", "id,name")]
    [InlineData("x.csv", "a,b\n1,2\n")]
    [InlineData("x.json", "[1]")]
    [InlineData("x.json", "{broken")]
    [InlineData("x.json", """{"format": "other"}""")]
    [InlineData("permanent_board.json", """{"pool_key": "permanent", "banner": {}}""")]
    public void RejectsWhatItCannotUse(string name, string content) =>
        Assert.Throws<InvalidDataException>(() => ExportDocuments.Classify(name, Encoding.UTF8.GetBytes(content)));
}

public class LibraryTests
{
    private const string Uid = "218216016349";

    private static HistoryRow Row(string uid, string time, string reward, string result = "dice", int? dice = 3) => new()
    {
        Uid = uid,
        PoolGroupId = BannerInfo.LimitedCharacter.Id,
        TimestampDecoded = time,
        RewardId = reward,
        ResultType = result,
        Dice = dice,
        Quantity = 1,
    };

    private static RewardCatalog CatalogWith(params (string Id, string Type, string Name, string Rank)[] rows)
    {
        var table = new CsvTable(["id", "type", "name", "rank"]);
        foreach (var (id, type, name, rank) in rows)
        {
            table.Rows.Add(new CsvRow(0, new() { ["id"] = id, ["type"] = type, ["name"] = name, ["rank"] = rank }));
        }
        return RewardCatalog.FromTables(table, new CsvTable(["id", "name"]), []);
    }

    [Fact]
    public void RegroupedBatchReplacesItsOldRowsInsteadOfDoublingThem()
    {
        using var folder = new TempFolder();
        var catalog = CatalogWith(("r1", "character", "角色一", "S"));
        var library = new HistoryLibrary(folder.Path);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("old-1", "2026-01-02 00:00:00", "r1"), Row("old-2", "2026-01-02 00:00:00", "r1"), Row("earlier", "2026-01-01 00:00:00", "r1")], null, null, null, catalog);

        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("new-1", "2026-01-02 00:00:00", "r1"), Row("new-2", "2026-01-02 00:00:00", "r1")], null, null, null, catalog);

        Assert.Equal(["new-1", "new-2", "earlier"], library.Histories(Uid).Single().Rows.Select(row => row.Uid));
    }

    [Fact]
    public void ACutOffScanKeepsTheFullerRowsItOnlyPartlyCovers()
    {
        using var folder = new TempFolder();
        var catalog = CatalogWith(("r1", "character", "角色一", "S"));
        var library = new HistoryLibrary(folder.Path);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("a", "2026-01-02 00:00:00", "r1"), Row("b", "2026-01-02 00:00:00", "r1")], null, null, null, catalog);

        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("a", "2026-01-02 00:00:00", "r1")], null, null, null, catalog);

        Assert.Equal(["a", "b"], library.Histories(Uid).Single().Rows.Select(row => row.Uid).Order());
    }

    [Fact]
    public void CorrectedRowsReplaceSavedOnesWithTheSameUid()
    {
        using var folder = new TempFolder();
        var catalog = CatalogWith(("r1", "character", "角色一", "S"));
        var library = new HistoryLibrary(folder.Path);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("a", "2026-01-01 00:00:00", "r1", "points_gift", 0)], null, null, null, catalog);

        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("a", "2026-01-01 00:00:00", "r1", "dice", 5)], null, null, null, catalog);

        var row = library.Histories(Uid).Single().Rows.Single();
        Assert.Equal(("dice", 5), (row.ResultType, row.Dice!.Value));
    }

    [Fact]
    public void MergesByUidAndSurvivesARestart()
    {
        using var folder = new TempFolder();
        var catalog = CatalogWith(("r1", "character", "角色一", "S"));
        var library = new HistoryLibrary(folder.Path);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("a", "2026-01-02 00:00:00", "r1"), Row("b", "2026-01-01 00:00:00", "new reward")], "23003", "live_capture", "windows_packet", catalog);
        library.Save(catalog);
        // A later capture sees only newer pulls plus an overlap; the older row must remain.
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("c", "2026-01-03 00:00:00", "r1"), Row("a", "2026-01-02 00:00:00", "r1")], null, null, null, catalog);
        library.Save(catalog);

        var reloaded = new HistoryLibrary(folder.Path);
        Assert.Empty(reloaded.Load(catalog));

        Assert.Equal(Uid, Assert.Single(reloaded.Accounts));
        var rows = Assert.Single(reloaded.Histories(Uid)).Rows;
        Assert.Equal(["c", "a", "b"], rows.Select(row => row.Uid));
        Assert.Equal("角色一", rows[0].RewardName);
        Assert.Equal("new reward", rows[2].RewardId);
        Assert.False(rows[2].IsMappedReward);
        Assert.Equal("23003", reloaded.ServerOf(Uid));
        Assert.Single(Directory.GetFiles(folder.Path, "*.json"));
        Assert.Equal($"{Uid}_Limited.json", Path.GetFileName(Directory.GetFiles(folder.Path).Single()));
    }

    [Fact]
    public void MappingFixRewritesSavedHistory()
    {
        using var folder = new TempFolder();
        var wrong = CatalogWith(("r1", "item", "錯的名字", "A"));
        var library = new HistoryLibrary(folder.Path);
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("a", "2026-01-01 00:00:00", "r1")], null, "live_capture", null, wrong);
        library.Save(wrong);

        var fixedCatalog = CatalogWith(("R1", "character", "對的名字", "S"));
        library.ApplyCatalog(fixedCatalog);
        library.Save(fixedCatalog);

        var saved = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(folder.Path).Single())).RootElement.GetProperty("records")[0];
        Assert.Equal("對的名字", saved.GetProperty("reward_name").GetString());
        Assert.Equal("S", saved.GetProperty("reward_rank").GetString());
        Assert.Equal("character", saved.GetProperty("reward_type").GetString());
        Assert.Equal("R1", saved.GetProperty("reward_id").GetString());
    }

    [Fact]
    public void ExportRoundTripPreservesEveryRecordField()
    {
        var row = Row("uid-1", "2026-01-01 00:00:00", "限定 獎勵");
        row.ResultText = "隱藏結果";
        row.GroupOrdinal = 4;
        row.Quantity = 30;
        row.RewardType = "item";
        var history = new BannerHistory(BannerInfo.LimitedCharacter, [row], [], []);
        var json = ExportWriter.ToJson(ExportWriter.BuildHistory(history, new ExportContext("live_capture", "npcap", Uid, "23003")));

        var document = ExportDocuments.ReadHistory(Encoding.UTF8.GetBytes(json));

        Assert.Equal(BannerInfo.LimitedCharacter, document.Banner);
        Assert.Equal((Uid, "23003", "live_capture", "npcap"), (document.UserUid, document.ServerId, document.Source, document.CaptureSource));
        var back = Assert.Single(document.Rows);
        Assert.Equal(("uid-1", "2026-01-01 00:00:00", 4, 3, "dice"), (back.Uid, back.TimestampDecoded, back.GroupOrdinal, back.Dice!.Value, back.ResultType));
        Assert.Equal(("限定 獎勵", "隱藏結果", 30L), (back.RewardId, back.ResultText!, back.Quantity!.Value));
    }

    [Fact]
    public void AchievementRoundTripKeepsStatusAndTime()
    {
        var ticks = (ulong)new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc).Ticks;
        var catalog = RewardCatalog.Bundled.Value;
        var json = ExportWriter.ToJson(ExportWriter.BuildAchievements(
            [new AchievementRecord("battle_10", 300, ticks), new AchievementRecord("Battle_25", 20, 0)], catalog, new ExportContext("live_capture")));

        var records = ExportDocuments.ReadAchievements(Encoding.UTF8.GetBytes(json)).Records;

        Assert.Equal(("2026-05-06 07:08:09", true), (records[0].CompletedAt!, records[0].Completed));
        Assert.Equal((20UL, false), (records[1].Progress, records[1].Completed));
        Assert.Equal(300, JsonDocument.Parse(json).RootElement.GetProperty("categories").GetProperty("battle")[0].GetProperty("target").GetInt32());
    }

    [Fact]
    public void UnreadableFileIsKeptNotOverwritten()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, $"{Uid}_Limited.json");
        File.WriteAllText(path, "{ hand edited and broken");
        var catalog = CatalogWith();
        var library = new HistoryLibrary(folder.Path);

        Assert.Contains($"{Uid}_Limited.json", library.Load(catalog).Single());
        library.Merge(Uid, HistoryKind.LimitedCharacter, [Row("a", "2026-01-01 00:00:00", "x")], null, null, null, catalog);
        library.Save(catalog);

        Assert.Equal("{ hand edited and broken", File.ReadAllText(path + ".unreadable"));
        Assert.Contains("\"uid\": \"a\"", File.ReadAllText(path));
    }

    [Fact]
    public void PartialCaptureStaysSeparateAndIsNotLoadedBack()
    {
        using var folder = new TempFolder();
        var catalog = CatalogWith();
        var library = new HistoryLibrary(folder.Path);
        var history = new BannerHistory(BannerInfo.LimitedCharacter, [Row("a", "2026-01-01 00:00:00", "x")], [], []);

        var path = library.SavePartial(Uid, history, new ExportContext("live_capture"), new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.Equal($"{Uid}_Limited_partial_20260102_030405.json", Path.GetFileName(path));
        var reloaded = new HistoryLibrary(folder.Path);
        reloaded.Load(catalog);
        Assert.Empty(reloaded.Accounts);
    }
}

public class AnalysisTests
{
    private static HistoryRow Row(string time, string id, string rank, string type = "character", string result = "dice", int dice = 2) => new()
    {
        Focus = RewardSchema.InferFocus(id, rank, type),
        TimestampDecoded = time,
        RewardId = id,
        RewardName = id,
        RewardRank = rank,
        RewardType = type,
        ResultType = result,
        Dice = dice,
        IsMappedReward = true,
    };

    private static BannerHistory Monopoly(params HistoryRow[] newestFirst) => new(BannerInfo.LimitedCharacter, newestFirst, [], []);

    [Fact]
    public void CountsIntervalsMarksOldestAsLowerBoundAndExcludesItFromTheAverage()
    {
        var rows = new List<HistoryRow>();
        // Oldest first here, reversed below: 2 pulls to S1, 3 to S2, 5 to S3, then 2 more.
        var sequence = new[] { "B", "S", "B", "B", "S", "B", "B", "B", "B", "S", "B", "B" };
        for (var i = 0; i < sequence.Length; i++)
        {
            rows.Add(Row($"2026-01-01 00:00:{i:00}", sequence[i] == "S" ? $"S{i}" : "x", sequence[i]));
        }
        rows.Reverse();

        var result = HistoryAnalyzer.Analyze(Monopoly([.. rows]));

        Assert.Equal(12, result.TotalPulls);
        Assert.Equal(3, result.TopRankCount);
        Assert.Equal(2, result.PullsSinceTopRank);
        Assert.Equal([5, 3, 2], result.TopRankPulls.Select(pull => pull.PullsSpent));
        Assert.Equal([false, false, true], result.TopRankPulls.Select(pull => pull.IsLowerBound));
        Assert.Equal(4.0, result.AverageInterval);
        Assert.Equal((3, 5), (result.ShortestInterval!.Value, result.LongestInterval!.Value));
        Assert.Equal(0.25, result.TopRankRate);
    }

    [Fact]
    public void ItemsDoNotCountAsTopRankAndGiftsDoNotCountAsPulls()
    {
        var result = HistoryAnalyzer.Analyze(Monopoly(
            Row("2026-01-01 00:00:04", "gift", "A", result: "points_gift"),
            Row("2026-01-01 00:00:03", "chase", "S", type: "item", result: "chase_reward"),
            Row("2026-01-01 00:00:02", "dice-item", "S", type: "item"),
            Row("2026-01-01 00:00:01", "x", "B")));

        Assert.Equal(2, result.TotalPulls);
        Assert.Equal(0, result.TopRankCount);
        Assert.Equal(2, result.PullsSinceTopRank);
        Assert.Null(result.AverageInterval);
        Assert.Equal(2, result.RarityMix.Sum(bucket => bucket.Count));
    }

    [Fact]
    public void RateUsesEveryTopRankRewardSoItAgreesWithTheCount()
    {
        var result = HistoryAnalyzer.Analyze(Monopoly(
            Row("2026-01-01 00:00:04", "gift", "S", result: "points_gift"),
            Row("2026-01-01 00:00:03", "x", "B"),
            Row("2026-01-01 00:00:02", "pull", "S"),
            Row("2026-01-01 00:00:01", "x", "B")));

        Assert.Equal(3, result.TotalPulls);
        Assert.Equal(2, result.TopRankCount);
        Assert.Equal(2.0 / 3, result.TopRankRate);
    }

    [Fact]
    public void LimitedAndCosmeticsAreCountedAndOnlySRanksResetTheInterval()
    {
        var limited = Row("2026-01-01 00:00:02", "lim", "S");
        limited.Focus = RewardFocus.SLimitedCharacter;
        var outfit = Row("2026-01-01 00:00:04", "Fashion_character_1", "S", type: "cosmetic");
        var glider = Row("2026-01-01 00:00:05", "Fashion_Glide_1", "A", type: "cosmetic", result: "points_gift");

        var result = HistoryAnalyzer.Analyze(Monopoly(
            Row("2026-01-01 00:00:06", "x", "B"),
            glider,
            outfit,
            Row("2026-01-01 00:00:03", "standard", "S"),
            limited,
            Row("2026-01-01 00:00:01", "x", "B")));

        Assert.Equal(5, result.TotalPulls);
        Assert.Equal(2, result.TopRankCount);
        Assert.Equal(1, result.LimitedCount);
        Assert.Equal(3, result.PullsSinceLimited);
        Assert.Equal(2, result.PullsSinceTopRank);
        Assert.Equal(["S 角色", "S 限定角色"], result.TopRankPulls.Select(pull => pull.FocusLabel));
        Assert.True(result.TopRankPulls[1].IsLimited);
        Assert.Equal(["S 角色", "S 限定角色", "角色服裝", "降落傘"], result.FocusCounts.OrderBy(bucket => bucket.Label == "S 角色" ? 0 : bucket.Label == "S 限定角色" ? 1 : bucket.Label == "角色服裝" ? 2 : 3).Select(bucket => bucket.Label));
        Assert.Equal(["Fashion_Glide_1", "Fashion_character_1"], result.CosmeticItems.Select(item => item.RewardId));
    }

    [Fact]
    public void NoLimitedYetMeansNoLimitedInterval()
    {
        var result = HistoryAnalyzer.Analyze(Monopoly(Row("2026-01-01 00:00:00", "x", "S")));

        Assert.Equal(0, result.LimitedCount);
        Assert.Null(result.PullsSinceLimited);
    }

    [Fact]
    public void GiftOfAnSRewardCountsAsSAndResetsTheInterval()
    {
        var result = HistoryAnalyzer.Analyze(Monopoly(
            Row("2026-01-01 00:00:05", "x", "B"),
            Row("2026-01-01 00:00:04", "gift", "S", result: "points_gift"),
            Row("2026-01-01 00:00:03", "x", "B"),
            Row("2026-01-01 00:00:02", "x", "B"),
            Row("2026-01-01 00:00:01", "pull", "S")));

        Assert.Equal(4, result.TotalPulls);
        Assert.Equal(2, result.TopRankCount);
        Assert.Equal([2, 1], result.TopRankPulls.Select(pull => pull.PullsSpent));
        Assert.Equal(1, result.PullsSinceTopRank);
    }

    [Fact]
    public void GiftTopRankShowsWhereItCameFrom()
    {
        var result = HistoryAnalyzer.Analyze(Monopoly(Row("2026-01-01 00:00:00", "gift", "S", result: "points_gift")));

        Assert.Equal("集點獎勵", Assert.Single(result.TopRankPulls).Source);
    }

    [Fact]
    public void MorePointsRewardsThanTheGameGivesAreFlagged()
    {
        HistoryRow[] Rows(int pulls, int rewards) =>
            [.. Enumerable.Range(0, pulls).Select(_ => Row("2026-01-01 00:00:00", "x", "B")),
             .. Enumerable.Range(0, rewards).Select(_ => Row("2026-01-01 00:00:00", "gift", "A", result: "points_gift"))];

        Assert.False(HistoryAnalyzer.Analyze(Monopoly(Rows(639, 63))).HasExcessPointsRewards);
        Assert.False(HistoryAnalyzer.Analyze(Monopoly(Rows(9, 1))).HasExcessPointsRewards);
        Assert.True(HistoryAnalyzer.Analyze(Monopoly(Rows(502, 200))).HasExcessPointsRewards);
    }

    [Fact]
    public void GashaponRowsAreNeverFlaggedForPointsRewards()
    {
        var arc = new BannerHistory(BannerInfo.ArcMiracleBox, [Row("2026-01-01 00:00:00", "a", "B", result: "points_gift")], [], []);

        Assert.False(HistoryAnalyzer.Analyze(arc).HasExcessPointsRewards);
    }

    [Fact]
    public void SingleTopRankHasNoExactIntervalYet()
    {
        var result = HistoryAnalyzer.Analyze(Monopoly(Row("2026-01-01 00:00:00", "only", "S")));

        Assert.True(Assert.Single(result.TopRankPulls).IsLowerBound);
        Assert.Null(result.AverageInterval);
        Assert.Null(result.LongestInterval);
    }

    [Fact]
    public void TimeBucketsIncludeEmptyPeriodsAndSwitchToMonths()
    {
        var days = HistoryAnalyzer.Analyze(Monopoly(Row("2026-01-03 09:00:00", "a", "B"), Row("2026-01-01 09:00:00", "a", "B")));
        Assert.Equal("日", days.TimeUnit);
        Assert.Equal([1, 0, 1], days.PullsOverTime.Select(bucket => bucket.Count));

        var months = HistoryAnalyzer.Analyze(Monopoly(Row("2026-04-03 09:00:00", "a", "B"), Row("2026-01-01 09:00:00", "a", "B")));
        Assert.Equal("月", months.TimeUnit);
        Assert.Equal(["2026-01", "2026-02", "2026-03", "2026-04"], months.PullsOverTime.Select(bucket => bucket.Label));
        Assert.Equal([1, 0, 0, 1], months.PullsOverTime.Select(bucket => bucket.Count));
    }

    [Fact]
    public void DiceAndResultKindsOnlyApplyToMonopoly()
    {
        var monopoly = HistoryAnalyzer.Analyze(Monopoly(Row("2026-01-01 00:00:00", "a", "B", dice: 6)));
        Assert.Equal(1, monopoly.DiceRolls.Single(bucket => bucket.Label == "6").Count);
        Assert.Equal(6, monopoly.DiceRolls.Count);

        var arc = HistoryAnalyzer.Analyze(new BannerHistory(BannerInfo.ArcMiracleBox, [Row("2026-01-01 00:00:00", "a", "B", result: "")], [], []));
        Assert.Equal(1, arc.TotalPulls);
        Assert.Empty(arc.DiceRolls);
        Assert.Empty(arc.ResultKinds);
    }

    [Fact]
    public void EmptyHistoryIsSafe()
    {
        var result = HistoryAnalyzer.Analyze(Monopoly());
        Assert.Equal(0, result.TotalPulls);
        Assert.Null(result.TopRankRate);
        Assert.Empty(result.PullsOverTime);
    }
}
