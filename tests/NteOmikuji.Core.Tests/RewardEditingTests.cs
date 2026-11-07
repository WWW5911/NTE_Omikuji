using System.Text;
using System.Text.Json;
using NteOmikuji.Core.Import;
using NteOmikuji.Core.Mapping;
using NteOmikuji.Core.Protocol;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class RewardEditingTests
{
    private static MappingStore Store(TempFolder folder, string? rewards = null)
    {
        var store = new MappingStore(folder.Path);
        if (rewards is not null)
        {
            File.WriteAllText(Path.Combine(folder.Path, MappingStore.RewardsFile), rewards);
        }
        store.EnsureSeeded();
        return store;
    }

    private static Dictionary<string, string> Names(params (string Locale, string Name)[] names) =>
        names.ToDictionary(name => name.Locale, name => name.Name);

    [Fact]
    public void SeedHasChineseAndEnglishAndOfficialFixes()
    {
        var catalog = RewardCatalog.Bundled.Value;
        var nanally = catalog.Find("1010")!;

        Assert.Equal("娜娜莉", nanally.Localized("zh-hant"));
        Assert.Equal("Nanally", nanally.Localized("en"));
        Assert.Equal("Nanally", nanally.Name);
        Assert.Equal(RewardFocus.SCharacter, nanally.Focus);
        Assert.Equal("Super Specter", catalog.Find("Fashion_Glide_1072")!.Name);
        Assert.Equal("Day Adrift", catalog.Find("Fashion_Glide_1057")!.Name);
        Assert.Equal(RewardFocus.None, catalog.Find("Dice_ticket_01")!.Focus);
        Assert.Equal(RewardFocus.Outfit, catalog.Find("Fashion_character_1010")!.Focus);
        Assert.Equal(RewardFocus.Glider, catalog.Find("Fashion_Glide_1072")!.Focus);
        Assert.Equal(RewardFocus.VehicleSkin, catalog.Find("Fashion_vehicle_1072_V013")!.Focus);
        Assert.Equal(RewardFocus.SArc, catalog.Find("fork_Arachne")!.Focus);
    }

    [Fact]
    public void LocalizedNameFallsBackSoAGapNeverHidesAReward()
    {
        using var folder = new TempFolder();
        var catalog = Store(folder, "id,rank,name.en,name.zh-hant,name.ja\r\nboth,S,Eng,中,\r\nzh,S,,中文,\r\nja,S,,,日本語\r\n").Load().Catalog!;

        Assert.Equal("Eng", catalog.Find("both")!.Localized("ja"));
        Assert.Equal("中文", catalog.Find("zh")!.Localized("en"));
        Assert.Equal("日本語", catalog.Find("ja")!.Localized("zh-hant"));
    }

    [Fact]
    public void SavingWritesEveryFieldAndReloadsIntoTheCatalog()
    {
        using var folder = new TempFolder();
        var store = Store(folder);

        store.SaveReward(new RewardEntry("new:reward/1", "character", "S", null, Names(("zh-hant", "新角色"), ("en", "New One"))));
        var result = store.Load();

        var meta = result.Catalog!.Find("NEW:REWARD/1")!;
        Assert.Equal(("character", "S", RewardFocus.SCharacter), (meta.Type, meta.Rank!, meta.Focus));
        Assert.Equal("新角色", meta.Localized("zh-hant"));
        Assert.Contains(result.Rewards.Entries, entry => entry.Id == "new:reward/1" && entry.FocusOverride is null);
    }

    [Theory]
    [InlineData(RewardFocus.SLimitedArc, "item", "B", "x", RewardFocus.SLimitedArc)]
    [InlineData(RewardFocus.None, "character", "S", "x", RewardFocus.None)]
    [InlineData(null, "character", "S", "x", RewardFocus.SCharacter)]
    [InlineData(null, "arc", "s", "x", RewardFocus.SArc)]
    [InlineData(null, "item", "S", "x", RewardFocus.None)]
    [InlineData(null, "character", "A", "x", RewardFocus.None)]
    [InlineData(null, "cosmetic", "A", "Fashion_Glide_9", RewardFocus.Glider)]
    [InlineData(null, "cosmetic", "S", "FASHION_vehicle_9", RewardFocus.VehicleSkin)]
    [InlineData(null, "cosmetic", "B", "Fashion_character_9", RewardFocus.Outfit)]
    public void CategoryOverridesTheInferredOne(RewardFocus? focus, string type, string rank, string id, RewardFocus expected)
    {
        using var folder = new TempFolder();
        var store = Store(folder);

        store.SaveReward(new RewardEntry(id, type, rank, focus, Names(("en", "X"))));

        Assert.Equal(expected, store.Load().Catalog!.Find(id)!.Focus);
    }

    [Theory]
    [InlineData("s_limited_character", RewardFocus.SLimitedCharacter)]
    [InlineData("S 限定角色", RewardFocus.SLimitedCharacter)]
    [InlineData("S限定弧盤", RewardFocus.SLimitedArc)]
    [InlineData("S 弧盤", RewardFocus.SArc)]
    [InlineData("時裝", RewardFocus.Outfit)]
    [InlineData("載具外觀", RewardFocus.VehicleSkin)]
    [InlineData("滑翔翼", RewardFocus.Glider)]
    [InlineData("角色服裝", RewardFocus.Outfit)]
    [InlineData("車皮", RewardFocus.VehicleSkin)]
    [InlineData("降落傘", RewardFocus.Glider)]
    [InlineData("不關注", RewardFocus.None)]
    [InlineData("GLIDER", RewardFocus.Glider)]
    public void CategoryAcceptsCodesAndChineseLabels(string text, RewardFocus expected)
    {
        using var folder = new TempFolder();
        var result = Store(folder, $"id,type,rank,focus,name.en\r\nx,item,B,{text},X\r\n").Load();

        Assert.Empty(result.Issues);
        Assert.Equal(expected, result.Catalog!.Find("x")!.Focus);
    }

    [Fact]
    public void BadCategoryIsReportedAndFallsBackToTheInferredOne()
    {
        using var folder = new TempFolder();
        var result = Store(folder, "id,type,rank,focus,name.en\r\nx,character,S,maybe,X\r\n").Load();

        Assert.Contains("第 2 行", result.Issues.Single());
        Assert.Equal(RewardFocus.SCharacter, result.Catalog!.Find("x")!.Focus);
    }

    [Theory]
    [InlineData("1010", "character", "S", "true", "s_character")]
    [InlineData("fork_x", "arc", "A", "true", "s_arc")]
    [InlineData("1010", "character", "S", "false", "none")]
    [InlineData("Fashion_Glide_1", "cosmetic", "A", "false", "glider")]
    [InlineData("Fashion_Glide_1", "cosmetic", "S", "true", "glider")]
    [InlineData("custom", "item", "B", "", "")]
    public void OldFlagBecomesACategoryKeepingEachChoice(string id, string type, string rank, string flag, string expected)
    {
        var table = CsvTable.Parse($"id,type,rank,is_s,name.en\r\n{id},{type},{rank},{flag},X\r\n");

        RewardSchema.Normalize(table);

        Assert.False(table.Has("is_s"));
        Assert.Equal(expected, table.Rows[0].Get("focus"));
    }

    [Fact]
    public void FileWithTheOldFlagIsUpgradedWithItsOwnBackup()
    {
        using var folder = new TempFolder();
        var store = Store(folder, "id,type,rank,is_s,name.en\r\n1010,character,S,false,我關掉的\r\n");
        var path = Path.Combine(folder.Path, MappingStore.RewardsFile);

        Assert.Contains("is_s", File.ReadAllText(path + ".v2.bak"));
        Assert.DoesNotContain("is_s", File.ReadLines(path).First());
        Assert.Equal(RewardFocus.None, store.Load().Catalog!.Find("1010")!.Focus);
    }

    [Fact]
    public void UnmappedRewardIsStillSortedByItsId()
    {
        var catalog = RewardCatalog.Empty;

        Assert.Equal(RewardFocus.Glider, catalog.FocusOf("Fashion_Glide_NEW"));
        Assert.Equal(RewardFocus.None, catalog.FocusOf("9999"));
    }

    [Fact]
    public void AddingALanguageCreatesAColumnThatTakesNames()
    {
        using var folder = new TempFolder();
        var store = Store(folder, "id,rank,name.en\r\nx,A,X\r\n");

        store.AddLocale("PT-BR");
        store.SaveReward(new RewardEntry("x", "item", "A", null, Names(("en", "X"), ("pt-br", "Xis"))));
        var result = store.Load();

        Assert.Contains("pt-br", result.Rewards.Locales);
        Assert.Equal("Xis", result.Catalog!.Find("x")!.Localized("pt-br"));
        Assert.Equal("X", result.Catalog.Find("x")!.Localized("ko"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("中文")]
    [InlineData("../evil")]
    [InlineData("a,b")]
    public void RejectsInvalidLanguageCodes(string code)
    {
        using var folder = new TempFolder();
        Assert.Throws<InvalidDataException>(() => Store(folder).AddLocale(code));
    }

    [Fact]
    public void SaveKeepsOtherRowsAndUnknownColumns()
    {
        using var folder = new TempFolder();
        var store = Store(folder, "id,type,rank,focus,name.en,note\r\na,item,B,,A,我的備註\r\nb,item,B,,B,別動\r\n");

        store.SaveReward(new RewardEntry("A", "item", "S", RewardFocus.SCharacter, Names(("en", "Changed"))));

        var text = File.ReadAllText(Path.Combine(folder.Path, MappingStore.RewardsFile));
        Assert.Contains("我的備註", text);
        Assert.Contains("別動", text);
        Assert.Contains("Changed", text);
    }

    [Fact]
    public void DeleteAndUndoRestoreTheExactFile()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        var path = Path.Combine(folder.Path, MappingStore.RewardsFile);
        var original = File.ReadAllBytes(path);

        store.DeleteReward("1010");
        Assert.Null(store.Load().Catalog!.Find("1010"));
        Assert.True(store.CanUndo);

        Assert.True(store.Undo());

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(store.CanUndo);
        Assert.False(store.Undo());
    }

    [Fact]
    public void FillFromBundledNeverChangesFilledCells()
    {
        using var folder = new TempFolder();
        var store = Store(folder, "id,type,rank,focus,name.en\r\n1010,character,A,s_limited_character,我改過的\r\n");

        Assert.True(store.FillFromBundled() > 0);
        var catalog = store.Load().Catalog!;

        var edited = catalog.Find("1010")!;
        Assert.Equal(("我改過的", "A", RewardFocus.SLimitedCharacter), (edited.Localized("en"), edited.Rank!, edited.Focus));
        Assert.Equal("娜娜莉", edited.Localized("zh-hant"));
        Assert.NotNull(catalog.Find("fork_vine"));
        Assert.Equal(0, store.FillFromBundled());
    }

    [Fact]
    public void WriteFailsCleanlyWhileAnotherProgramHoldsTheFile()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        var path = Path.Combine(folder.Path, MappingStore.RewardsFile);
        var before = File.ReadAllBytes(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var error = Record.Exception(() => store.SaveReward(new RewardEntry("x", "item", "A", null, Names(("en", "X")))));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.GetType().Name);
            Assert.False(store.CanUndo);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));
    }

    [Fact]
    public void ExternalChangesAreDetectedButOwnSavesAreNot()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        store.Load();
        Assert.False(store.HasExternalChanges());

        store.SaveReward(new RewardEntry("x", "item", "A", null, Names(("en", "X"))));
        store.Load();
        Assert.False(store.HasExternalChanges());

        File.AppendAllText(Path.Combine(folder.Path, MappingStore.RewardsFile), "y,item,A,,Y\r\n");
        Assert.True(store.HasExternalChanges());
    }

    [Fact]
    public void UneditedOldFileBecomesTheNewSeedWithABackup()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, MappingStore.RewardsFile);
        var old = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "legacy_rewards_seed.csv"));
        File.WriteAllBytes(path, old);
        var store = new MappingStore(folder.Path);

        store.EnsureSeeded();

        Assert.Equal(old, File.ReadAllBytes(path + ".v1.bak"));
        var catalog = store.Load().Catalog!;
        Assert.Equal(RewardCatalog.Bundled.Value.RewardCount, catalog.RewardCount);
        Assert.Equal("Super Specter", catalog.Find("Fashion_Glide_1072")!.Name);
        Assert.Equal("娜娜莉", catalog.Find("1010")!.Localized("zh-hant"));
    }

    [Fact]
    public void EditedOldFileKeepsEveryValueAndGainsTheNewColumns()
    {
        using var folder = new TempFolder();
        var store = Store(folder, "id,type,name,rank\r\n1010,character,我的譯名,S\r\ncustom,item,Mine,B\r\n");

        var path = Path.Combine(folder.Path, MappingStore.RewardsFile);
        Assert.True(File.Exists(path + ".v1.bak"));
        Assert.Contains("我的譯名", File.ReadAllText(path + ".v1.bak"));
        Assert.Contains("name.zh-hant", File.ReadLines(path).First());
        Assert.DoesNotContain(",name,", File.ReadLines(path).First());

        var catalog = store.Load().Catalog!;
        Assert.Equal("我的譯名", catalog.Find("1010")!.Localized("zh-hant"));
        Assert.Equal("Nanally", catalog.Find("1010")!.Localized("en"));
        Assert.Equal("Mine", catalog.Find("custom")!.Localized("en"));
        Assert.NotNull(catalog.Find("fork_vine"));
    }

    [Fact]
    public void JsonImportAcceptsMultipleLanguagesAndTheFlag()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        var json = Encoding.UTF8.GetBytes("""{"pack_1": {"type": "character", "rank": "A", "focus": "S 限定角色", "names": {"zh-Hant": "繁中", "EN": "Eng", "pt-BR": "Port"}}}""");

        Assert.Equal(ImportKind.RewardMapping, ExportDocuments.Classify("pack.json", json));
        store.Import(false, ExportDocuments.ReadMapping("pack.json", json, false));
        var meta = store.Load().Catalog!.Find("pack_1")!;

        Assert.Equal(("繁中", "Eng", "Port"), (meta.Localized("zh-hant"), meta.Localized("en"), meta.Localized("pt-br")));
        Assert.Equal(RewardFocus.SLimitedCharacter, meta.Focus);
    }

    [Fact]
    public void CsvLanguagePackIsRecognizedAndImported()
    {
        using var folder = new TempFolder();
        var store = Store(folder);
        var csv = Encoding.UTF8.GetBytes("id,name.ja\r\n1010,ナナリー改\r\n");

        Assert.Equal(ImportKind.RewardMapping, ExportDocuments.Classify("ja.csv", csv));
        store.Import(false, ExportDocuments.ReadMapping("ja.csv", csv, false));
        var meta = store.Load().Catalog!.Find("1010")!;

        Assert.Equal("ナナリー改", meta.Localized("ja"));
        Assert.Equal("Nanally", meta.Localized("en"));
    }
}
