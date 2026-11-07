using System.Text;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;
using Xunit;

namespace NteOmikuji.Core.Tests;

/// <summary>Replies whose layout changed, as a game update might. Each row keeps its real values and gains or moves a field.</summary>
public class AnchoredRowsTests
{
    private const ulong Newest = 639281865006850000;
    private const uint ChaseRoll = uint.MaxValue;
    private const long TicksPerMinute = 600_000_000;

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] FString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        return [.. U32((uint)bytes.Length), .. bytes];
    }

    private sealed record Sample(uint Roll, string Spec, string[] Items);

    /// <summary>The kinds of row the real history holds.</summary>
    private static readonly Sample[] Page =
    [
        new(3, "fork_appliance", ["fork_appliance"]),
        new(0, "fork_yaodao", ["fork_yaodao"]),
        new(ChaseRoll, "Dice_ticket_01,30", ["Dice_ticket_01"]),
        new(5, "1076", ["Dice_ticket_01", "1076"]),
        new(4, "Dicelimite,5", ["Dicelimite"]),
    ];

    private delegate byte[] Layout(byte[] roll, byte[] spec, byte[] rest, byte[] pool, byte[] ticks, byte[] eventId);

    private static byte[] Current(byte[] roll, byte[] spec, byte[] rest, byte[] pool, byte[] ticks, byte[] eventId) =>
        [.. roll, .. spec, .. rest, .. pool, .. ticks, .. eventId];

    private static byte[] Board(Layout layout, Sample[]? samples = null)
    {
        var rows = (samples ?? Page).Select((row, index) => layout(
            U32(row.Roll),
            FString(row.Spec),
            [.. U32(0), .. U32((uint)row.Items.Length), .. row.Items.SelectMany(FString)],
            FString("CardPool_Character"),
            BitConverter.GetBytes(Newest - (ulong)(index * TicksPerMinute)),
            FString("Lottery_MingYin"))).ToList();
        return RescanInOneSessionTests.BoardReply(rows, 0);
    }

    private static List<HistoryRow> Decode(byte[] reply) => MonopolyDecoder.DecodeResponse(reply, RewardCatalog.Empty);

    private static void AssertPageRead(List<HistoryRow> rows)
    {
        Assert.Equal(["fork_appliance", "fork_yaodao", "Dice_ticket_01", "1076", "Dicelimite"], rows.Select(row => row.RewardId));
        Assert.Equal([3, 0, -4, 5, 4], rows.Select(row => row.Dice!.Value));
        Assert.Equal(["dice", "points_gift", "chase_reward", "dice", "dice"], rows.Select(row => row.ResultType));
        Assert.Equal([1L, 1L, 30L, 1L, 5L], rows.Select(row => row.Quantity!.Value));
        Assert.All(rows, row => Assert.Equal("Lottery_MingYin", row.EventId));
    }

    public static TheoryData<string> Changes() => new(Layouts.Keys);

    private static readonly byte[] Seven = U32(7);

    private static readonly Dictionary<string, Layout> Layouts = new()
    {
        ["a number after the event"] = (roll, spec, rest, pool, ticks, eventId) => [.. roll, .. spec, .. rest, .. pool, .. ticks, .. eventId, .. Seven],
        ["text after the event"] = (roll, spec, rest, pool, ticks, eventId) => [.. roll, .. spec, .. rest, .. pool, .. ticks, .. eventId, .. FString("NewField_1")],
        ["a number before the roll"] = (roll, spec, rest, pool, ticks, eventId) => [.. Seven, .. roll, .. spec, .. rest, .. pool, .. ticks, .. eventId],
        ["a number after the result"] = (roll, spec, rest, pool, ticks, eventId) => [.. roll, .. spec, .. Seven, .. rest, .. pool, .. ticks, .. eventId],
        ["text before the pool"] = (roll, spec, rest, pool, ticks, eventId) => [.. roll, .. spec, .. rest, .. FString("Extra_1"), .. pool, .. ticks, .. eventId],
        ["a number after the time"] = (roll, spec, rest, pool, ticks, eventId) => [.. roll, .. spec, .. rest, .. pool, .. ticks, .. Seven, .. eventId],
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void AChangedLayoutIsStillRead(string change)
    {
        var rows = Decode(Board(Layouts[change]));

        AssertPageRead(rows);
        Assert.All(rows, row => Assert.Equal(DecoderModes.Anchored, row.DecoderMode));
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void AChangedLayoutIsMarkedUnverified(string change)
    {
        var page = new CapturedPage(1, HistoryKind.LimitedCharacter, Board(Layouts[change]), 0, 5);

        var history = HistoryAssembly.Build(HistoryKind.LimitedCharacter, [page], RewardCatalog.Empty);

        Assert.All(history.Rows, row => Assert.True(row.Unverified));
        Assert.Contains(history.Warnings, warning => warning.Code == HistoryAssembly.UnverifiedCode);
    }

    [Fact]
    public void BothReadingsGiveTheSameRowsAndTimes()
    {
        var parsed = Decode(Board(Current));
        var found = Decode(Board(Layouts["a number after the event"]));

        AssertPageRead(parsed);
        Assert.All(parsed, row => Assert.Equal(DecoderModes.Structured, row.DecoderMode));
        Assert.Equal(parsed.Select(row => row.TimestampRawHex), found.Select(row => row.TimestampRawHex));
    }

    [Fact]
    public void BoardTimesKeepTheScaleSavedUidsDependOn()
    {
        var row = Decode(Board(Current))[0];

        Assert.Equal(Convert.ToHexStringLower(BitConverter.GetBytes(Newest * 4)), row.TimestampRawHex);
    }

    [Fact]
    public void ARollOutOfRangeIsNotTakenAsARealRoll()
    {
        var page = Board(Current, [.. Page.Select(row => row with { Roll = row.Roll == ChaseRoll ? 10 : row.Roll * 10 })]);

        Assert.Empty(RecordBlockParser.ParseRecords(page, RecordBlockType.Monopoly));
        Assert.DoesNotContain(Decode(page), row => row.ResultType == "dice" && row.Dice > 6);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(6u)]
    [InlineData(ChaseRoll)]
    public void EveryRealRollIsAccepted(uint roll) =>
        Assert.Single(RecordBlockParser.ParseRecords(Board(Current, [new Sample(roll, "fork_vine", ["fork_vine"])]), RecordBlockType.Monopoly));

    [Fact]
    public void TwoPagesInOneReplyAreBothRead()
    {
        List<byte[]> Rows(int from) => Enumerable.Range(from, 5).Select(index => (byte[])[
            .. U32(3), .. FString($"r{index}"), .. U32(0), .. U32(1), .. FString($"r{index}"), .. FString("CardPool_Character"),
            .. BitConverter.GetBytes(Newest - (ulong)(index * TicksPerMinute)), .. FString("Lottery_MingYin"), .. Seven]).ToList();

        var rows = Decode(RescanInOneSessionTests.TwoBoardPages(Rows(0), Rows(5)));

        Assert.Equal(Enumerable.Range(0, 10).Select(index => $"r{index}"), rows.Select(row => row.RewardId));
        Assert.All(rows, row => Assert.Equal(DecoderModes.Anchored, row.DecoderMode));
    }

    [Fact]
    public void APageFullOfPointsRewardsIsLeftUnread()
    {
        // A zero right before the reward looks like a roll of zero, which is how real pulls once became points rewards.
        Layout zeroAfterRoll = (roll, spec, rest, pool, ticks, eventId) => [.. roll, .. new byte[4], .. spec, .. rest, .. pool, .. ticks, .. eventId];

        Assert.Empty(Decode(Board(zeroAfterRoll)));
    }

    [Fact]
    public void AnExtraPoolStringIsLeftUnread()
    {
        Layout extraPool = (roll, spec, rest, pool, ticks, eventId) => [.. roll, .. spec, .. rest, .. FString("CardPool_Bonus"), .. pool, .. ticks, .. eventId];

        Assert.Empty(Decode(Board(extraPool)));
    }

    [Fact]
    public void TimesRunningTheWrongWayAreLeftUnread()
    {
        var rows = Page.Select((row, index) => (byte[])[
            .. U32(row.Roll), .. FString(row.Spec), .. U32(0), .. U32(1), .. FString(row.Items[0]), .. FString("CardPool_Character"),
            .. BitConverter.GetBytes(Newest + (ulong)(index * TicksPerMinute)), .. FString("Lottery_MingYin"), .. Seven]).ToList();

        Assert.Empty(Decode(RescanInOneSessionTests.BoardReply(rows, 0)));
    }

    [Fact]
    public void ARenamedPoolLeavesNothingRead()
    {
        Layout renamed = (roll, spec, rest, _, ticks, eventId) => [.. roll, .. spec, .. rest, .. FString("GachaPool_Character"), .. ticks, .. eventId];

        Assert.Empty(Decode(Board(renamed)));
    }

    private static byte[] Arc(Func<byte[], byte[], byte[], byte[]> layout)
    {
        var rows = new[] { "fork_vine", "fork_TigerTally" }.Select((item, index) => layout(
            FString(item), FString("ForkLottery_Nanali"), BitConverter.GetBytes(Newest - (ulong)(index * TicksPerMinute)))).ToList();
        return RescanInOneSessionTests.ArcReply(rows, 0);
    }

    public static TheoryData<string> ArcChanges() => new(ArcLayouts.Keys);

    private static readonly Dictionary<string, Func<byte[], byte[], byte[], byte[]>> ArcLayouts = new()
    {
        ["a number after the time"] = (item, pool, ticks) => [.. item, .. pool, .. ticks, .. Seven],
        ["text after the time"] = (item, pool, ticks) => [.. item, .. pool, .. ticks, .. FString("NewField_1")],
        ["a number before the item"] = (item, pool, ticks) => [.. Seven, .. item, .. pool, .. ticks],
        ["a number before the pool"] = (item, pool, ticks) => [.. item, .. Seven, .. pool, .. ticks],
        ["a number before the time"] = (item, pool, ticks) => [.. item, .. pool, .. Seven, .. ticks],
    };

    [Theory]
    [MemberData(nameof(ArcChanges))]
    public void AChangedArcLayoutIsStillRead(string change)
    {
        var rows = ArcDecoder.DecodeResponse(Arc(ArcLayouts[change]), RewardCatalog.Empty);

        Assert.Equal(["fork_vine", "fork_TigerTally"], rows.Select(row => row.RewardId));
        Assert.All(rows, row => Assert.Equal("ForkLottery_Nanali", row.EventId));
        Assert.All(rows, row => Assert.Equal(DecoderModes.Anchored, row.DecoderMode));
    }

    [Fact]
    public void ArcTimesKeepTheScaleSavedUidsDependOn()
    {
        var row = ArcDecoder.DecodeResponse(Arc((item, pool, ticks) => [.. item, .. pool, .. ticks]), RewardCatalog.Empty)[0];

        Assert.Equal(DecoderModes.Structured, row.DecoderMode);
        Assert.Equal(Convert.ToHexStringLower(BitConverter.GetBytes(Newest)), row.TimestampRawHex);
    }
}
