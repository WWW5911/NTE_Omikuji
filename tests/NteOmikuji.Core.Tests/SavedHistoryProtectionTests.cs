using NteOmikuji.Core.Export;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;
using Xunit;

namespace NteOmikuji.Core.Tests;

/// <summary>A game update can make new captures unreliable. Saved rows must survive that, and any change must leave a trail.</summary>
public sealed class SavedHistoryProtectionTests : IDisposable
{
    private const string Uid = "214000000000";
    private const string Time = "2026-01-02 00:00:00";

    private static readonly RewardCatalog Catalog = RewardCatalog.Bundled.Value;

    private readonly TempFolder _folder = new();
    private readonly HistoryLibrary _library;

    public SavedHistoryProtectionTests() => _library = new HistoryLibrary(_folder.Path);

    public void Dispose() => _folder.Dispose();

    /// <summary>A row with a field this app does not know, which structured parsing cannot get past.</summary>
    private static readonly byte[] UnknownField = BitConverter.GetBytes(7u);

    private static PoolHistory BoardPage(byte[]? tail, params string[] items)
    {
        var rows = items.Select((item, index) => RescanInOneSessionTests.Board(item, 1000 - index, tail: tail)).ToList();
        var payload = RescanInOneSessionTests.BoardReply(rows, 0);
        return HistoryAssembly.Build(HistoryKind.LimitedCharacter, [new CapturedPage(1, HistoryKind.LimitedCharacter, payload, 0, rows.Count)], Catalog);
    }

    private void Merge(IReadOnlyList<HistoryRow> rows) =>
        _library.Merge(Uid, HistoryKind.LimitedCharacter, rows, null, "live_capture", "test", Catalog);

    private IReadOnlyList<HistoryRow> Saved() => _library.Histories(Uid).Single().Rows;

    private static HistoryRow Row(string uid, string reward, int? dice = 3, string result = "dice", string? eventId = null, string time = Time) => new()
    {
        Uid = uid,
        PoolGroupId = PoolInfo.LimitedCharacter.Id,
        TimestampDecoded = time,
        RewardId = reward,
        ResultType = result,
        Dice = dice,
        EventId = eventId,
    };

    private string ChangeLog() =>
        File.ReadAllText(Path.Combine(_library.ChangesFolder, Path.ChangeExtension(HistoryJsonWriter.LibraryFileName(Uid, PoolInfo.LimitedCharacter.FilePrefix), ".jsonl")));

    [Fact]
    public void ARowOnlyTheScanCouldReadIsFlaggedWithAWarning()
    {
        var history = BoardPage(UnknownField, "fork_vine", "fork_nonos");

        Assert.All(history.Rows, row => Assert.True(row.Unverified));
        Assert.Contains(history.Warnings, warning => warning.Code == HistoryAssembly.UnverifiedCode);
    }

    [Fact]
    public void RowsParsedFromTheirFieldsAreNotFlagged()
    {
        var board = BoardPage(null, "fork_vine", "fork_nonos");
        var arcRows = new[] { RescanInOneSessionTests.Arc("fork_vine", 900) };
        var arcPage = new CapturedPage(1, HistoryKind.ArcMiracleBox, RescanInOneSessionTests.ArcReply(arcRows, 0), 0, 1);
        var arc = HistoryAssembly.Build(HistoryKind.ArcMiracleBox, [arcPage], Catalog);

        Assert.All(board.Rows.Concat(arc.Rows), row => Assert.False(row.Unverified));
        Assert.Empty(board.Warnings);
        Assert.Empty(arc.Warnings);
    }

    [Fact]
    public void AnUnreliableCaptureCannotChangeSavedRows()
    {
        Merge(BoardPage(null, "fork_vine", "fork_nonos").Rows);
        _library.TakeNotes();

        Merge(BoardPage(UnknownField, "fork_vine", "fork_nonos").Rows);

        Assert.Equal(2, Saved().Count);
        Assert.All(Saved(), row => Assert.Equal("Lottery_Nanali", row.EventId));
        Assert.All(Saved(), row => Assert.False(row.Unverified));
        Assert.Equal(new MergeNotes(2, 0), _library.TakeNotes());
    }

    [Fact]
    public void NewTimesFromAnUnreliableCaptureStayFlaggedUntilAGoodOneReplacesThem()
    {
        Merge(BoardPage(UnknownField, "fork_vine", "fork_nonos").Rows);
        _library.Save(Catalog);
        Assert.All(Saved(), row => Assert.True(row.Unverified));

        var reloaded = new HistoryLibrary(_folder.Path);
        reloaded.Load(Catalog);
        Assert.All(reloaded.Histories(Uid).Single().Rows, row => Assert.True(row.Unverified));

        Merge(BoardPage(null, "fork_vine", "fork_nonos").Rows);

        Assert.All(Saved(), row => Assert.False(row.Unverified));
        Assert.All(Saved(), row => Assert.Equal("Lottery_Nanali", row.EventId));
    }

    [Fact]
    public void ARowThatCannotReadItsEventKeepsTheSavedOne()
    {
        Merge([Row("a", "fork_vine", eventId: "Lottery_Nanali")]);

        Merge([Row("a", "fork_vine")]);

        Assert.Equal("Lottery_Nanali", Saved().Single().EventId);
        Assert.Equal(new MergeNotes(0, 0), _library.TakeNotes());
    }

    [Fact]
    public void ChangedContentLeavesTheOldRowsOnFile()
    {
        Merge([Row("a", "fork_vine", 0, "points_gift")]);

        Merge([Row("a", "fork_vine", 5)]);
        _library.Save(Catalog);

        Assert.Equal(new MergeNotes(0, 1), _library.TakeNotes());
        Assert.Contains("points_gift", ChangeLog());
        Assert.Equal(5, Saved().Single().Dice);
    }

    [Fact]
    public void LaterChangesAddToTheFileRatherThanReplaceIt()
    {
        Merge([Row("a", "first")]);
        Merge([Row("a", "second")]);
        _library.Save(Catalog);
        Merge([Row("a", "third")]);
        _library.Save(Catalog);

        var log = ChangeLog();
        Assert.Contains("first", log);
        Assert.Contains("second", log);
    }

    [Fact]
    public void ARepeatedCaptureIsNotAChange()
    {
        Merge([Row("a", "fork_vine")]);

        Merge([Row("a", "fork_vine")]);
        _library.Save(Catalog);

        Assert.Equal(new MergeNotes(0, 0), _library.TakeNotes());
        Assert.False(Directory.Exists(_library.ChangesFolder));
    }

    [Fact]
    public void ABatchThatOnlyGrewOrGainedItsEventIsNotAChange()
    {
        Merge([Row("a", "x"), Row("b", "y")]);

        Merge([Row("a", "x", eventId: "Lottery_Nanali"), Row("b", "y", eventId: "Lottery_Nanali"), Row("c", "z", eventId: "Lottery_Nanali")]);

        Assert.Equal(3, Saved().Count);
        Assert.Equal(new MergeNotes(0, 0), _library.TakeNotes());
    }

    [Fact]
    public void ADifferentEventIsAChange()
    {
        Merge([Row("a", "x", eventId: "Lottery_Nanali")]);

        Merge([Row("a", "x", eventId: "Lottery_Xun")]);

        Assert.Equal(1, _library.TakeNotes().ChangedSeconds);
    }

    [Fact]
    public void WithoutAPlaceForTheOldRowsTheFileOnDiskStaysAsItWas()
    {
        Merge([Row("a", "first")]);
        _library.Save(Catalog);
        var file = Path.Combine(_folder.Path, HistoryJsonWriter.LibraryFileName(Uid, PoolInfo.LimitedCharacter.FilePrefix));
        var before = File.ReadAllText(file);
        File.WriteAllText(_library.ChangesFolder, "a file where the folder should go");

        Merge([Row("a", "second")]);

        Assert.ThrowsAny<IOException>(() => _library.Save(Catalog));
        Assert.Equal(before, File.ReadAllText(file));
    }
}
