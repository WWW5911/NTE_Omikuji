using System.Net;
using System.Text;
using NteOmikuji.Core.Mapping;
using Xunit;

namespace NteOmikuji.Core.Tests;

public sealed class MappingUpdateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nte-update-" + Guid.NewGuid().ToString("N"));
    private readonly MappingStore _store;

    public MappingUpdateTests()
    {
        _store = new MappingStore(_folder);
        _store.EnsureSeeded();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static Dictionary<string, byte[]> Files(params (string Name, string Text)[] files) =>
        files.ToDictionary(file => file.Name, file => Encoding.UTF8.GetBytes(file.Text));

    private CsvRow? Row(string file, string id) =>
        CsvTable.Parse(CsvTable.DecodeText(File.ReadAllBytes(Path.Combine(_folder, file)))).Rows.FirstOrDefault(row => row.Get("id") == id);

    [Fact]
    public void ANewRewardIsAdded()
    {
        var changed = _store.ApplyUpdate(Files((MappingStore.RewardsFile, "id,type,rank,name.en\nFashion_vehicle_new,cosmetic,S,New Skin\n")));

        Assert.Equal(1, changed);
        Assert.Equal("New Skin", Row(MappingStore.RewardsFile, "Fashion_vehicle_new")!.Get("name.en"));
    }

    [Fact]
    public void AnUpstreamCorrectionReplacesAnUntouchedValueButNotAnEditedOne()
    {
        _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,1052\nLottery_Nanali,1010\n")));
        _store.SaveEvent("Lottery_Nanali", ["mine"]);

        _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,5555\nLottery_Nanali,6666\n")));

        Assert.Equal("5555", Row(MappingStore.EventsFile, "Lottery_Xun")!.Get("featured"));
        Assert.Equal("mine", Row(MappingStore.EventsFile, "Lottery_Nanali")!.Get("featured"));
    }

    [Fact]
    public void AConfirmedDateReplacesTheEstimateAndClearsItsFlag()
    {
        _store.ApplyUpdate(Files((MappingStore.ScheduleFile, "id,starts,estimate\nnext-board,2026-12-01,yes\n")));

        _store.ApplyUpdate(Files((MappingStore.ScheduleFile, "id,starts,estimate\nnext-board,2026-12-03,\n")));

        var row = Row(MappingStore.ScheduleFile, "next-board")!;
        Assert.Equal("2026-12-03", row.Get("starts"));
        Assert.Equal("", row.Get("estimate"));
    }

    [Fact]
    public void ACellClearedHereStaysCleared()
    {
        _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,1052\n")));
        _store.SaveEvent("Lottery_Xun", []);

        Assert.Equal(0, _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,1052\n"))));
        _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,9999\n")));

        Assert.Equal("", Row(MappingStore.EventsFile, "Lottery_Xun")!.Get("featured"));
    }

    [Fact]
    public void WithNoEarlierDownloadABlankCellIsFilled()
    {
        _store.SaveEvent("Lottery_Xun", []);
        var baselines = Path.Combine(_folder, "upstream");
        if (Directory.Exists(baselines))
        {
            Directory.Delete(baselines, recursive: true);
        }

        _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,1052\n")));

        Assert.Equal("1052", Row(MappingStore.EventsFile, "Lottery_Xun")!.Get("featured"));
    }

    [Fact]
    public void ARowDeletedHereDoesNotComeBack()
    {
        var update = Files((MappingStore.RewardsFile, "id,type,rank\nnew_item,item,A\n"));
        _store.ApplyUpdate(update);
        _store.DeleteReward("new_item");

        Assert.Equal(0, _store.ApplyUpdate(update));

        Assert.Null(Row(MappingStore.RewardsFile, "new_item"));
    }

    [Fact]
    public void ARowDeletedBeforeTheFirstUpdateDoesNotComeBack()
    {
        _store.DeleteEvent("Lottery_Xun");

        Assert.Equal(0, _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,1052\n"))));

        Assert.Null(Row(MappingStore.EventsFile, "Lottery_Xun"));
    }

    [Fact]
    public void AFolderFromAnOlderVersionStillGetsRowsItLacks()
    {
        _store.DeleteEvent("Lottery_Xun");
        Directory.Delete(Path.Combine(_folder, "upstream"), recursive: true);

        _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,1052\n")));

        Assert.Equal("1052", Row(MappingStore.EventsFile, "Lottery_Xun")!.Get("featured"));
    }

    [Fact]
    public void SeedingDoesNotReplaceFilesAlreadyThere()
    {
        var path = Path.Combine(_folder, MappingStore.EventsFile);
        File.WriteAllText(path, "id,featured\nMine,1\n");

        _store.EnsureSeeded();

        Assert.Equal("id,featured\nMine,1\n", File.ReadAllText(path));
    }

    [Fact]
    public void AnUnchangedSourceChangesNothing()
    {
        var update = Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,1052\n"));
        _store.ApplyUpdate(update);

        Assert.Equal(0, _store.ApplyUpdate(update));
        Assert.False(_store.CanUndo);
    }

    [Fact]
    public void AnUpdateCanBeUndone()
    {
        _store.ApplyUpdate(Files((MappingStore.RewardsFile, "id,type,rank\nnew_item,item,A\n")));

        Assert.True(_store.Undo());

        Assert.Null(Row(MappingStore.RewardsFile, "new_item"));
    }

    private string Text(string file) => File.ReadAllText(Path.Combine(_folder, file));

    [Fact]
    public void OneUndoRevertsAnUpdateAcrossFiles()
    {
        var (rewards, events) = (Text(MappingStore.RewardsFile), Text(MappingStore.EventsFile));
        _store.ApplyUpdate(Files(
            (MappingStore.RewardsFile, "id,type,rank\nnew_item,item,A\n"),
            (MappingStore.EventsFile, "id,featured\nLottery_New,1\n")));

        Assert.True(_store.Undo());

        Assert.Equal((rewards, events), (Text(MappingStore.RewardsFile), Text(MappingStore.EventsFile)));
        Assert.False(_store.CanUndo);
    }

    [Fact]
    public void AnUndoneUpdateStaysOutUntilTheKeptDownloadIsRemoved()
    {
        var update = Files((MappingStore.RewardsFile, "id,type,rank\nnew_item,item,A\n"));
        _store.ApplyUpdate(update);
        _store.Undo();

        Assert.Equal(0, _store.ApplyUpdate(update));
        Assert.Null(Row(MappingStore.RewardsFile, "new_item"));

        Directory.Delete(Path.Combine(_folder, "upstream"), recursive: true);
        _store.ApplyUpdate(update);

        Assert.NotNull(Row(MappingStore.RewardsFile, "new_item"));
    }

    [Fact]
    public void AValueFromAnOlderSeedIsKeptAsIfEditedHere()
    {
        var path = Path.Combine(_folder, MappingStore.EventsFile);
        File.WriteAllText(path, File.ReadAllText(path).Replace("Lottery_Xun,1052", "Lottery_Xun,older"));
        Directory.Delete(Path.Combine(_folder, "upstream"), recursive: true);

        _store.ApplyUpdate(Files((MappingStore.EventsFile, "id,featured\nLottery_Xun,1052\n")));

        Assert.Equal("older", Row(MappingStore.EventsFile, "Lottery_Xun")!.Get("featured"));
    }

    [Fact]
    public void EachEditKeepsItsOwnUndo()
    {
        _store.SaveEvent("Lottery_A", ["1"]);
        _store.SaveEvent("Lottery_B", ["2"]);

        _store.Undo();

        Assert.NotNull(Row(MappingStore.EventsFile, "Lottery_A"));
        Assert.Null(Row(MappingStore.EventsFile, "Lottery_B"));
    }

    [Fact]
    public void OneUndoRevertsAFillFromBundledAcrossFiles()
    {
        _store.DeleteReward("1010");
        _store.DeleteEvent("Lottery_Xun");

        Assert.True(_store.FillFromBundled() > 0);
        _store.Undo();

        Assert.Null(Row(MappingStore.RewardsFile, "1010"));
        Assert.Null(Row(MappingStore.EventsFile, "Lottery_Xun"));
    }

    [Fact]
    public void AnUpdateStoppedByALockedFileCanStillBeUndone()
    {
        var rewards = Text(MappingStore.RewardsFile);
        var update = Files((MappingStore.RewardsFile, "id,type,rank\nnew_item,item,A\n"), (MappingStore.EventsFile, "id,featured\nLottery_New,1\n"));
        using (new FileStream(Path.Combine(_folder, MappingStore.EventsFile), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => _store.ApplyUpdate(update));
        }

        Assert.NotEqual(rewards, Text(MappingStore.RewardsFile));
        _store.Undo();

        Assert.Equal(rewards, Text(MappingStore.RewardsFile));
    }

    [Fact]
    public void AnUndoStoppedByALockedFileFinishesOnTheNextTry()
    {
        var (rewards, events) = (Text(MappingStore.RewardsFile), Text(MappingStore.EventsFile));
        _store.ApplyUpdate(Files(
            (MappingStore.RewardsFile, "id,type,rank\nnew_item,item,A\n"),
            (MappingStore.EventsFile, "id,featured\nLottery_New,1\n")));
        using (new FileStream(Path.Combine(_folder, MappingStore.RewardsFile), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => _store.Undo());
        }

        Assert.True(_store.CanUndo);
        _store.Undo();

        Assert.Equal((rewards, events), (Text(MappingStore.RewardsFile), Text(MappingStore.EventsFile)));
    }

    [Fact]
    public void ABadFileStopsTheWholeUpdate()
    {
        var update = Files(
            (MappingStore.RewardsFile, "id,type,rank\nnew_item,item,A\n"),
            (MappingStore.EventsFile, "<html>Rate limited</html>\n"));

        Assert.Throws<InvalidDataException>(() => _store.ApplyUpdate(update));

        Assert.Null(Row(MappingStore.RewardsFile, "new_item"));
    }

    [Fact]
    public void TheUpdatedTablesStillLoad()
    {
        _store.ApplyUpdate(Files((MappingStore.RewardsFile, "id,type,rank,name.en\nnew_item,item,A,New Item\n")));

        var loaded = _store.Load();

        Assert.NotNull(loaded.Catalog);
        Assert.Equal("New Item", loaded.Catalog!.Find("new_item")!.Name);
    }

    private sealed class FakeHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request.RequestUri!.Segments[^1]));
    }

    private static MappingUpdater Updater(Func<string, HttpResponseMessage> respond) =>
        new(new HttpClient(new FakeHandler(respond)), "https://example.test/");

    [Fact]
    public async Task AFileTheSourceLacksIsReportedAndTheRestDownload()
    {
        var download = await Updater(name => name == MappingStore.EventsFile
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(name) }).DownloadAsync();

        Assert.Equal([MappingStore.EventsFile], download.Missing);
        Assert.Equal(3, download.Files.Count);
        Assert.Equal(MappingStore.RewardsFile, Encoding.UTF8.GetString(download.Files[MappingStore.RewardsFile]));
    }

    [Theory]
    [InlineData(null, MappingUpdater.DefaultBaseUrl)]
    [InlineData("  ", MappingUpdater.DefaultBaseUrl)]
    [InlineData("http://example.test/maps/", MappingUpdater.DefaultBaseUrl)]
    [InlineData("file:///C:/maps/", MappingUpdater.DefaultBaseUrl)]
    [InlineData("not a url", MappingUpdater.DefaultBaseUrl)]
    [InlineData("https://example.test/maps/", "https://example.test/maps/")]
    [InlineData("https://example.test/maps", "https://example.test/maps/")]
    public void OnlyAnHttpsSourceReplacesTheDefault(string? configured, string expected) =>
        Assert.Equal(expected, MappingUpdater.SourceOrDefault(configured));

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            throw new IOException("connection lost");
    }

    [Fact]
    public async Task AConnectionLostMidDownloadIsAFailedDownload()
    {
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Updater(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }).DownloadAsync());

        Assert.Contains(MappingStore.RewardsFile, failure.Message);
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
    }

    [Fact]
    public async Task AStalledDownloadStopsAtTheTimeLimit()
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));
        var download = new MappingUpdater(http, "https://example.test/", TimeSpan.FromMilliseconds(100)).DownloadAsync();

        Assert.Same(download, await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10))));
        await Assert.ThrowsAsync<HttpRequestException>(() => download);
    }

    [Fact]
    public async Task CancellingADownloadIsNotReportedAsATimeout()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MappingUpdater(http, "https://example.test/").DownloadAsync(cancelled.Token));
    }

    [Fact]
    public async Task AServerErrorFailsTheDownload()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Updater(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)).DownloadAsync());
    }

    [Fact]
    public async Task AnOversizedFileIsRefused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Updater(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[9 * 1024 * 1024]) }).DownloadAsync());
    }
}
