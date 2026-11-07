using NteOmikuji.Core.Mapping;
using NteOmikuji.Core.Protocol;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class EventTableTests
{
    private static RewardCatalog Bundled => RewardCatalog.Bundled.Value;

    private static MappingStore Store(TempFolder folder)
    {
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        return store;
    }

    [Fact]
    public void AnEarlierFileNameIsCarriedOver()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "banners.csv"), "id,featured\r\nLottery_Mine,fork_mine\r\n");

        new MappingStore(folder.Path).EnsureSeeded();

        Assert.False(File.Exists(Path.Combine(folder.Path, "banners.csv")));
        Assert.Contains("Lottery_Mine", File.ReadAllText(EventsPath(folder)));
    }

    [Fact]
    public void ACurrentFileIsNotOverwrittenByAnEarlierOne()
    {
        using var folder = new TempFolder();
        File.WriteAllText(EventsPath(folder), "id,featured\r\nLottery_New,fork_new\r\n");
        File.WriteAllText(Path.Combine(folder.Path, "banners.csv"), "id,featured\r\nLottery_Old,fork_old\r\n");

        new MappingStore(folder.Path).EnsureSeeded();

        Assert.Contains("Lottery_New", File.ReadAllText(EventsPath(folder)));
        Assert.True(File.Exists(Path.Combine(folder.Path, "banners.csv")));
    }

    private static string EventsPath(TempFolder folder) => Path.Combine(folder.Path, MappingStore.EventsFile);

    [Theory]
    [InlineData("fork_prism", "ForkLottery_MingYin", RewardFocus.SLimitedArc)]
    [InlineData("fork_twinbirds", "ForkLottery_HeiYu", RewardFocus.SLimitedArc)]
    [InlineData("1057", "Lottery_MingYin", RewardFocus.SLimitedCharacter)]
    public void AFeaturedRewardIsLimitedOnItsEvent(string reward, string eventId, RewardFocus expected)
    {
        Assert.Equal(expected, Bundled.FocusOf(reward, eventId));
    }

    [Theory]
    [InlineData("fork_Butterfly", "ForkLottery_MingYin")]
    [InlineData("fork_BlackBook", "ForkLottery_HeiYu")]
    [InlineData("fork_prism", "ForkLottery_HeiYu")]
    public void AnArcFeaturedElsewhereIsAnOrdinarySHere(string reward, string eventId)
    {
        Assert.Equal(RewardFocus.SArc, Bundled.FocusOf(reward, eventId));
    }

    [Fact]
    public void EventIdsMatchWhateverTheirCase()
    {
        Assert.Equal(RewardFocus.SLimitedArc, Bundled.FocusOf("FORK_PRISM", "forklottery_mingyin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ForkLottery_NotInTheTable")]
    [InlineData("Lottery_Permanent")]
    public void WithoutAFeaturedListTheMappingDecides(string? eventId)
    {
        Assert.Equal(RewardFocus.SArc, Bundled.FocusOf("fork_prism", eventId));
    }

    [Fact]
    public void AnUnmappedFeaturedRewardStillCountsAsLimited()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        store.SaveEvent("ForkLottery_New", ["fork_brand_new"]);
        store.SaveEvent("Lottery_New", ["1999"]);

        var catalog = store.Load().Catalog!;

        Assert.Equal(RewardFocus.SLimitedArc, catalog.FocusOf("fork_brand_new", "ForkLottery_New"));
        Assert.Equal(RewardFocus.SLimitedCharacter, catalog.FocusOf("1999", "Lottery_New"));
    }

    [Fact]
    public void CosmeticsAndItemsKeepTheirCategoryOnAnEvent()
    {
        Assert.Equal(RewardFocus.Glider, Bundled.FocusOf("Fashion_Glide_1072", "Lottery_MingYin"));
        Assert.Equal(RewardFocus.None, Bundled.FocusOf("Dicelimite", "Lottery_MingYin"));
    }

    [Fact]
    public void EveryBundledFeaturedRewardIsS()
    {
        var events = CsvTable.Parse(CsvTable.DecodeText(ReadSeed()));

        foreach (var row in events.Rows)
        {
            foreach (var reward in EventEntry.ParseFeatured(row.Get(EventEntry.FeaturedColumn)))
            {
                Assert.True(Bundled.Find(reward)?.Rank == "S", $"{row.Get("id")} features {reward}");
            }
        }
    }

    private static byte[] ReadSeed()
    {
        using var seed = RewardCatalog.OpenSeed(MappingStore.EventsFile);
        using var buffer = new MemoryStream();
        seed.CopyTo(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public void SeedingAddsTheFileToAnOlderFolder()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        File.Delete(EventsPath(folder));

        store.EnsureSeeded();

        Assert.Contains(store.Load().Events!.Entries, entry => entry.Id == "ForkLottery_MingYin" && entry.Featured.Single() == "fork_prism");
    }

    [Fact]
    public void AMissingFileStillLoads()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        File.Delete(EventsPath(folder));

        var result = store.Load();

        Assert.Empty(result.Issues);
        Assert.Empty(result.Events!.Entries);
        Assert.Equal(RewardFocus.SArc, result.Catalog!.FocusOf("fork_prism", "ForkLottery_MingYin"));
    }

    [Fact]
    public void SavingUpdatesOneEventAndKeepsItsOtherColumns()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        File.WriteAllText(EventsPath(folder), "id,featured,note\r\nForkLottery_A,fork_a,我的備註\r\nForkLottery_B,fork_b,別動\r\n");

        store.SaveEvent("forklottery_a", ["fork_x", "fork_y"]);

        var table = CsvTable.Parse(File.ReadAllText(EventsPath(folder)));
        Assert.Equal(("fork_x;fork_y", "我的備註"), (table.Rows[0].Get("featured"), table.Rows[0].Get("note")));
        Assert.Equal(("fork_b", "別動"), (table.Rows[1].Get("featured"), table.Rows[1].Get("note")));
        var catalog = store.Load().Catalog!;
        Assert.Equal(RewardFocus.SLimitedArc, catalog.FocusOf("fork_y", "ForkLottery_A"));
    }

    [Fact]
    public void ANewEventCanBeAddedDeletedAndUndone()
    {
        using var folder = new TempFolder();
        var store = Store(folder);

        store.SaveEvent("ForkLottery_New", ["fork_new"]);
        Assert.Contains(store.Load().Events!.Entries, entry => entry.Id == "ForkLottery_New");

        store.DeleteEvent("forklottery_new");
        Assert.DoesNotContain(store.Load().Events!.Entries, entry => entry.Id == "ForkLottery_New");

        Assert.True(store.Undo());
        Assert.Contains(store.Load().Events!.Entries, entry => entry.Id == "ForkLottery_New");
    }

    [Fact]
    public void ABlankIdIsRefused()
    {
        using var folder = new TempFolder();

        Assert.Throws<InvalidDataException>(() => Store(folder).SaveEvent("  ", ["fork_a"]));
    }

    [Fact]
    public void FillAddsMissingEventsWithoutChangingFilledOnes()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        File.WriteAllText(EventsPath(folder), "id,featured\r\nForkLottery_MingYin,fork_mine\r\nForkLottery_HeiYu,\r\n");

        Assert.True(store.FillFromBundled() > 0);

        var entries = store.Load().Events!.Entries.ToDictionary(entry => entry.Id);
        Assert.Equal("fork_mine", entries["ForkLottery_MingYin"].Featured.Single());
        Assert.Equal("fork_twinbirds", entries["ForkLottery_HeiYu"].Featured.Single());
        Assert.Equal("fork_Rose", entries["ForkLottery_AnHunQu"].Featured.Single());
    }

    [Fact]
    public void AnOutsideEditIsNoticed()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        store.Load();
        Assert.False(store.HasExternalChanges());

        File.AppendAllText(EventsPath(folder), "ForkLottery_New,fork_new\r\n");

        Assert.True(store.HasExternalChanges());
    }
}
