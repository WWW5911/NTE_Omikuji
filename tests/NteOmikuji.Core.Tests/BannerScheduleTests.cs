using NteOmikuji.Core.Mapping;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class BannerScheduleTests
{
    private const string Header = "id,version,phase,pool,rerun,name.zh-hant,name.en,featured.zh-hant,featured.en,banner_id,starts,ends,estimate\r\n";

    private static IReadOnlyList<ScheduledBanner> Bundled()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        return store.Load().Schedule!;
    }

    private static List<ScheduledBanner> Parse(string rows, List<string>? issues = null) =>
        BannerSchedule.Parse(CsvTable.Parse(Header + rows), "schedule.csv", issues ?? []).ToList();

    [Fact]
    public void BundledScheduleHasBothPhasesOfVersionOneFive()
    {
        var schedule = Bundled();

        Assert.Equal(8, schedule.Count);
        Assert.All(schedule, entry => Assert.Equal("1.5", entry.Version));
        Assert.Equal(4, schedule.Count(entry => entry.IsArc));
        Assert.Equal(4, schedule.Count(entry => entry.IsRerun));
        Assert.Equal("Android Dreams", schedule.Single(entry => entry.Id == "1.5-1-board-elyms").Name("en"));
        Assert.Equal("曳爾沐", schedule.Single(entry => entry.Id == "1.5-1-board-elyms").FeaturedName("zh-hant"));
    }

    [Fact]
    public void GlobalDatesFollowTheAnnouncement()
    {
        var schedule = Bundled();

        Assert.All(schedule.Where(entry => entry.Phase == 1), entry =>
            Assert.Equal((new DateOnly(2026, 11, 11), new DateOnly(2026, 12, 2), false), (entry.Starts, entry.Ends, entry.IsEstimate)));
        Assert.All(schedule.Where(entry => entry.Phase == 2), entry =>
            Assert.Equal((new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 23), true), (entry.Starts, entry.Ends, entry.IsEstimate)));
    }

    [Fact]
    public void OnlyReturningEventsHaveAnId()
    {
        var schedule = Bundled();

        Assert.All(schedule, entry => Assert.Equal(entry.IsRerun, entry.BannerId is not null));
    }

    [Fact]
    public void ReturningEventsMatchTheEventTable()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        var result = store.Load();

        foreach (var entry in result.Schedule!.Where(entry => entry.BannerId is not null))
        {
            Assert.Contains(result.Banners!.Entries, banner => banner.Id == entry.BannerId && banner.Featured.Count == 1);
        }
    }

    [Theory]
    [InlineData("2026-11-10", SchedulePhase.Upcoming, 1, 22)]
    [InlineData("2026-11-11", SchedulePhase.Active, 0, 21)]
    [InlineData("2026-12-01", SchedulePhase.Active, -20, 1)]
    [InlineData("2026-12-02", SchedulePhase.Ended, -21, 0)]
    public void PhaseFollowsTheDate(string today, SchedulePhase phase, int untilStart, int left)
    {
        var date = DateOnly.Parse(today);
        var entry = Parse("a,1.5,1,board,,名,,獎,,,2026-11-11,2026-12-02,\r\n").Single();

        Assert.Equal((phase, untilStart, left), (entry.PhaseOn(date), entry.DaysUntilStart(date), entry.DaysLeft(date)));
    }

    [Fact]
    public void VisibleDropsEndedEventsAndOrdersTheRest()
    {
        var visible = BannerSchedule.Visible(Bundled(), new DateOnly(2026, 12, 5));

        Assert.Equal(["1.5-2-board-exe", "1.5-2-board-chaos", "1.5-2-arc-exe", "1.5-2-arc-chaos"], visible.Select(entry => entry.Id));
        Assert.Empty(BannerSchedule.Visible(Bundled(), new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void NamesFallBackWhenALanguageIsMissing()
    {
        var entry = Parse("a,1.5,1,arc,,回音特刊,,靜候彩鈴,Echoes on Hold,,2026-11-11,2026-12-02,\r\n").Single();

        Assert.Equal("回音特刊", entry.Name("en"));
        Assert.Equal("Echoes on Hold", entry.FeaturedName("en"));
        Assert.Equal("靜候彩鈴", entry.FeaturedName("ja"));
    }

    [Fact]
    public void ARowWithBadDatesIsReportedAndSkipped()
    {
        var issues = new List<string>();

        var entries = Parse("bad,1.5,1,board,,名,,獎,,,11/11,2026-12-02,\r\ngood,1.5,1,board,,名,,獎,,,2026-11-11,2026-12-02,\r\n", issues);

        Assert.Equal("good", Assert.Single(entries).Id);
        Assert.Contains("第 2 行", Assert.Single(issues));
    }

    [Fact]
    public void AnOlderFolderGainsTheFileAndAMissingOneStillLoads()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        var path = Path.Combine(folder.Path, MappingStore.ScheduleFile);

        File.Delete(path);
        var missing = store.Load();
        Assert.Empty(missing.Issues);
        Assert.Empty(missing.Schedule!);

        store.EnsureSeeded();
        Assert.Equal(8, store.Load().Schedule!.Count);
    }

    [Fact]
    public void FillAddsMissingRowsAndKeepsEdits()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        var path = Path.Combine(folder.Path, MappingStore.ScheduleFile);
        File.WriteAllText(path, Header + "1.5-2-board-exe,1.5,2,board,,我改的名字,,EXE,Exe,Lottery_Exe,2026-12-03,2026-12-24,\r\n");

        store.FillFromBundled();

        var schedule = store.Load().Schedule!;
        var edited = schedule.Single(entry => entry.Id == "1.5-2-board-exe");
        Assert.Equal(8, schedule.Count);
        Assert.Equal(("我改的名字", "Lottery_Exe", new DateOnly(2026, 12, 24)), (edited.Name("zh-hant"), edited.BannerId, edited.Ends));
    }

    [Fact]
    public void AnOutsideEditIsNoticed()
    {
        using var folder = new TempFolder();
        var store = new MappingStore(folder.Path);
        store.EnsureSeeded();
        store.Load();

        File.AppendAllText(Path.Combine(folder.Path, MappingStore.ScheduleFile), "new,1.5,1,board,,名,,獎,,,2026-11-11,2026-12-02,\r\n");

        Assert.True(store.HasExternalChanges());
    }
}
