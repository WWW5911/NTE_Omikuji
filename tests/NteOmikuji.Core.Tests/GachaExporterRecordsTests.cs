using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NteOmikuji.Core.Export;
using NteOmikuji.Core.Import;
using NteOmikuji.Core.Mapping;
using NteOmikuji.Core.Protocol;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class GachaExporterRecordsTests
{
    private const string OldTime = "2026-04-29 23:46:34";
    private const string SharedTime = "2026-05-13 13:24:39";
    private const string Uid = "214000000000";

    private static JsonObject Record(int order, string time, string item, int? roll = 3, string? label = null, string type = "monopoly", string pool = "CardPool_Character", int count = 1) => new()
    {
        ["record_id"] = new string('a', 64),
        ["source_order"] = order,
        ["record_type"] = type,
        ["time"] = time,
        ["pool_id"] = pool,
        ["item_id"] = item,
        ["count"] = count,
        ["roll_points"] = roll,
        ["roll_label_id"] = label,
        ["secondary_item_id"] = null,
        ["secondary_count"] = null,
    };

    private static byte[] File(params JsonObject[] records) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["schema_version"] = 1, ["records"] = new JsonArray(records.Cast<JsonNode>().ToArray()) }.ToJsonString());

    private static GachaExporterImport Read(params JsonObject[] records) => ImportedFiles.ReadGachaExporter(File(records));

    private static HistoryRow Captured(string uid, string time, string reward = "captured") => new()
    {
        Uid = uid,
        PoolGroupId = PoolInfo.LimitedCharacter.Id,
        TimestampDecoded = time,
        RewardId = reward,
        ResultType = "dice",
        Dice = 5,
        EventId = "Lottery_Xun",
    };

    private static IReadOnlyList<HistoryRow> Limited(HistoryLibrary library) =>
        library.Histories(Uid).Single(history => history.Pool.Kind == HistoryKind.LimitedCharacter).Rows;

    [Fact]
    public void TheFileIsRecognizedByItsContent()
    {
        Assert.Equal(ImportKind.GachaExporterRecords, ImportedFiles.Classify("records.json", File(Record(0, OldTime, "x"))));
    }

    [Fact]
    public void AnotherSchemaIsRefused()
    {
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["schema_version"] = 2, ["records"] = new JsonArray() }.ToJsonString());

        Assert.Throws<InvalidDataException>(() => ImportedFiles.ReadGachaExporter(bytes));
    }

    [Theory]
    [InlineData("CardPool_Character", HistoryKind.LimitedCharacter, null)]
    [InlineData("CardPool_NewRole", HistoryKind.Permanent, "Lottery_Permanent")]
    public void BoardsMapByTheirNames(string pool, HistoryKind kind, string? eventId)
    {
        var row = Assert.Single(Read(Record(0, OldTime, "x", pool: pool)).Rows[kind]);

        Assert.Equal(eventId, row.EventId);
        Assert.Equal(PoolInfo.For(kind).Id, row.PoolGroupId);
    }

    [Fact]
    public void ArcsKeepTheirEvent()
    {
        var row = Assert.Single(Read(Record(0, OldTime, "fork_x", null, type: "fork", pool: "ForkLottery_Xun")).Rows[HistoryKind.ArcMiracleBox]);

        Assert.Equal("ForkLottery_Xun", row.EventId);
        Assert.Equal(("", "miracle_box", null), (row.ResultType, row.SourceType, row.Quantity));
    }

    [Theory]
    [InlineData(5, null, "dice", 5)]
    [InlineData(null, "BPUI_LotteryResult_jidianzengli", "points_gift", 0)]
    [InlineData(null, "BPUI_LotteryResult_chenmiandi", "chase_reward", -4)]
    public void ResultsUseThisAppsMeanings(int? roll, string? label, string result, int dice)
    {
        var row = Read(Record(0, OldTime, "x", roll, label)).Rows[HistoryKind.LimitedCharacter].Single();

        Assert.Equal((result, dice), (row.ResultType, row.Dice!.Value));
    }

    [Fact]
    public void QuantityIsKept()
    {
        Assert.Equal(30, Read(Record(0, OldTime, "Dice_ticket_02", count: 30)).Rows[HistoryKind.LimitedCharacter].Single().Quantity);
    }

    [Fact]
    public void RowsTurnNewestFirstWithinASecond()
    {
        var rows = Read(Record(0, OldTime, "oldest", 1), Record(1, OldTime, "middle", 2), Record(2, OldTime, "gift", null, "BPUI_LotteryResult_jidianzengli")).Rows[HistoryKind.LimitedCharacter];

        Assert.Equal([("gift", 0), ("middle", 1), ("oldest", 2)], rows.Select(row => (row.RewardId, row.GroupOrdinal)));
        Assert.Equal(3, rows.Select(row => row.Uid).Distinct().Count());
    }

    [Theory]
    [InlineData("CardPool_Future", "monopoly", "2026-04-29 23:46:34", 3, null)]
    [InlineData("CardPool_Character", "gashapon", "2026-04-29 23:46:34", 3, null)]
    [InlineData("CardPool_Character", "monopoly", "someday", 3, null)]
    [InlineData("CardPool_Character", "monopoly", "2026-04-29 23:46:34", 9, null)]
    [InlineData("CardPool_Character", "monopoly", "2026-04-29 23:46:34", 3, "BPUI_LotteryResult_future")]
    public void WhatIsNotUnderstoodIsCountedNotGuessed(string pool, string type, string time, int roll, string? label)
    {
        var read = Read(Record(0, time, "x", roll, label, type, pool));

        Assert.Equal(1, read.Ignored);
        Assert.Empty(read.Rows);
    }

    private static (TempFolder Folder, HistoryLibrary Library) WithCaptured(params HistoryRow[] rows)
    {
        var folder = new TempFolder();
        var library = new HistoryLibrary(folder.Path);
        library.Merge(Uid, HistoryKind.LimitedCharacter, rows, null, "live_capture", null, RewardCatalog.Empty);
        return (folder, library);
    }

    [Fact]
    public void OnlyTimesThatAreMissingAreAdded()
    {
        var (folder, library) = WithCaptured(Captured("c1", SharedTime));
        using var _ = folder;
        var rows = Read(Record(0, OldTime, "old"), Record(1, SharedTime, "theirs")).Rows[HistoryKind.LimitedCharacter];

        var (added, skipped) = library.Fill(Uid, HistoryKind.LimitedCharacter, rows, RewardCatalog.Empty);

        Assert.Equal((1, 1), (added, skipped));
        Assert.Equal(["captured", "old"], Limited(library).Select(row => row.RewardId));
    }

    [Fact]
    public void CapturedRowsStayExactlyAsTheyWere()
    {
        var (folder, library) = WithCaptured(Captured("c1", SharedTime));
        using var _ = folder;
        var rows = Read(Record(0, SharedTime, "theirs", 1), Record(1, SharedTime, "theirs2", 2)).Rows[HistoryKind.LimitedCharacter];

        library.Fill(Uid, HistoryKind.LimitedCharacter, rows, RewardCatalog.Empty);

        var row = Assert.Single(Limited(library));
        Assert.Equal(("c1", "captured", 5, "Lottery_Xun"), (row.Uid, row.RewardId, row.Dice!.Value, row.EventId));
    }

    [Fact]
    public void ImportingTwiceAddsNothingMore()
    {
        var (folder, library) = WithCaptured(Captured("c1", SharedTime));
        using var _ = folder;
        var rows = Read(Record(0, OldTime, "old")).Rows[HistoryKind.LimitedCharacter];

        library.Fill(Uid, HistoryKind.LimitedCharacter, rows, RewardCatalog.Empty);
        var second = library.Fill(Uid, HistoryKind.LimitedCharacter, rows, RewardCatalog.Empty);

        Assert.Equal((0, 1), second);
        Assert.Equal(2, Limited(library).Count);
    }

    [Fact]
    public void AddedRowsSortIntoTime()
    {
        var (folder, library) = WithCaptured(Captured("c1", "2026-05-13 13:24:39"), Captured("c2", "2026-04-01 00:00:00"));
        using var _ = folder;
        var rows = Read(Record(0, OldTime, "old")).Rows[HistoryKind.LimitedCharacter];

        library.Fill(Uid, HistoryKind.LimitedCharacter, rows, RewardCatalog.Empty);

        Assert.Equal(["2026-05-13 13:24:39", OldTime, "2026-04-01 00:00:00"], Limited(library).Select(row => row.TimestampDecoded));
    }

    [Fact]
    public void ALaterCaptureReplacesImportedRows()
    {
        var (folder, library) = WithCaptured(Captured("c1", SharedTime));
        using var _ = folder;
        library.Fill(Uid, HistoryKind.LimitedCharacter, Read(Record(0, OldTime, "old")).Rows[HistoryKind.LimitedCharacter], RewardCatalog.Empty);

        library.Merge(Uid, HistoryKind.LimitedCharacter, [Captured("fresh", OldTime, "real")], null, "live_capture", null, RewardCatalog.Empty);

        Assert.Equal(["captured", "real"], Limited(library).Select(row => row.RewardId));
    }

    [Fact]
    public void AddedRowsSurviveSavingAndAreBackedUpFirst()
    {
        var (folder, library) = WithCaptured(Captured("c1", SharedTime));
        using var _ = folder;
        library.Save(RewardCatalog.Empty);
        var reloaded = new HistoryLibrary(folder.Path);
        reloaded.Load(RewardCatalog.Empty);

        reloaded.Fill(Uid, HistoryKind.LimitedCharacter, Read(Record(0, OldTime, "old", 4)).Rows[HistoryKind.LimitedCharacter], RewardCatalog.Empty);
        reloaded.Save(RewardCatalog.Empty);

        var again = new HistoryLibrary(folder.Path);
        Assert.Empty(again.Load(RewardCatalog.Empty));
        Assert.Equal([("captured", 5), ("old", 4)], Limited(again).Select(row => (row.RewardId, row.Dice!.Value)));
        Assert.Single(Directory.GetFiles(Path.Combine(folder.Path, "backups")));
    }

    [Fact]
    public void NothingToAddLeavesTheFileAlone()
    {
        var (folder, library) = WithCaptured(Captured("c1", SharedTime));
        using var _ = folder;
        library.Save(RewardCatalog.Empty);

        library.Fill(Uid, HistoryKind.LimitedCharacter, Read(Record(0, SharedTime, "theirs")).Rows[HistoryKind.LimitedCharacter], RewardCatalog.Empty);

        Assert.Equal(0, library.Save(RewardCatalog.Empty));
    }
}
