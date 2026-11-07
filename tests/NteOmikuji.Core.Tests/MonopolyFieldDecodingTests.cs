using System.Text;
using NteOmikuji.Core.Protocol;
using Xunit;

namespace NteOmikuji.Core.Tests;

/// <summary>
/// Pages built to the layout seen in real replies, where every row ends with its
/// event banner: roll, result text, 0, item count, items, pool, ticks, banner.
/// </summary>
public class MonopolyFieldDecodingTests
{
    private const ulong Ticks = 639281865006850000;
    private const uint ChaseRoll = uint.MaxValue;

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] FString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        return [.. U32((uint)bytes.Length), .. bytes];
    }

    private static byte[] Row(uint roll, string spec, string[] items, ulong ticks = Ticks, string? banner = "Lottery_MingYin")
    {
        byte[] row = [.. U32(roll), .. FString(spec), .. U32(0), .. U32((uint)items.Length), .. items.SelectMany(FString), .. FString("CardPool_Character"), .. BitConverter.GetBytes(ticks)];
        return banner is null ? row : [.. row, .. FString(banner)];
    }

    private static byte[] Single(uint roll, string spec, string item) => Row(roll, spec, [item]);

    /// <summary>The fixed envelope before the struct name, then the block header the first row follows.</summary>
    private static byte[] Page(params byte[][] rows)
    {
        const int markerAt = 50;
        var envelope = new byte[markerAt];
        void Put(int distance, uint value) => BitConverter.TryWriteBytes(envelope.AsSpan(markerAt - distance), value);
        Put(26, 0x03000000);
        Put(22, 0x80000000);
        Put(14, 527);
        Put(10, 1);
        Put(6, 1_774_080);
        var body = rows.SelectMany(row => row).ToArray();
        byte[] plain = [.. envelope, .. StructuredParser.MonopolyMarker, 0, .. U32(0), .. U32((uint)body.Length), .. U32((uint)rows.Length), .. body];
        return ToWire(plain);
    }

    /// <summary>Replies sit three bits into the byte stream.</summary>
    private static byte[] ToWire(byte[] plain)
    {
        const int bits = 3;
        var wire = new byte[plain.Length + 1];
        for (var i = 0; i < plain.Length; i++)
        {
            wire[i] |= (byte)(plain[i] << bits);
            wire[i + 1] = (byte)(plain[i] >> (8 - bits));
        }
        return wire;
    }

    private static List<HistoryRow> Decode(params byte[][] rows) => MonopolyDecoder.DecodeResponse(Page(rows), RewardCatalog.Bundled.Value);

    [Fact]
    public void BlocksWithATrailingBannerParse()
    {
        var records = StructuredParser.ParseRecords(Page(Single(4, "fork_dustbin", "fork_dustbin"), Single(2, "Dice_ticket_02,30", "Dice_ticket_02")), StructuredRecordType.Monopoly);

        Assert.Equal(["fork_dustbin", "Dice_ticket_02"], records.Select(record => record.ItemId));
        Assert.Equal([4u, 2u], records.Select(record => record.RollPointsRaw!.Value));
        Assert.Equal([1L, 30L], records.Select(record => record.Count));
    }

    [Fact]
    public void RowsKeepTheirEventBanner()
    {
        var rows = Decode(Single(4, "fork_vine", "fork_vine"), Row(1, "fork_nonos", ["fork_nonos"], banner: "Lottery_ZhenHong"));

        Assert.Equal(["Lottery_MingYin", "Lottery_ZhenHong"], rows.Select(row => row.BannerId));
    }

    [Fact]
    public void BannerIsOptionalSoTheOlderLayoutStillParses()
    {
        var records = StructuredParser.ParseRecords(Page(Row(3, "fork_vine", ["fork_vine"], banner: null), Row(5, "fork_vine", ["fork_vine"], banner: null)), StructuredRecordType.Monopoly);

        Assert.Equal([3u, 5u], records.Select(record => record.RollPointsRaw!.Value));
    }

    [Fact]
    public void RealRollWinsOverAZeroFoundInPadding()
    {
        var rows = Decode(
            Single(3, "fork_appliance", "fork_appliance"),
            Single(2, "Dice_ticket_02,30", "Dice_ticket_02"),
            Single(6, "1020", "1020"),
            Row(5, "1076", ["Dice_ticket_01", "1076"]),
            Single(4, "Dicelimite,5", "Dicelimite"));

        Assert.Equal([3, 2, 6, 5, 4], rows.Select(row => row.Dice!.Value));
        Assert.All(rows, row => Assert.Equal("dice", row.ResultType));
    }

    [Fact]
    public void OnlyARollOfZeroIsThePointsReward()
    {
        var rows = Decode(
            Single(1, "fork_vine", "fork_vine"),
            Single(0, "fork_yaodao", "fork_yaodao"),
            Single(1, "fork_nonos", "fork_nonos"));

        Assert.Equal(["dice", "points_gift", "dice"], rows.Select(row => row.ResultType));
        Assert.Equal(0, rows[1].Dice);
    }

    [Fact]
    public void AllOnesRollIsTheChaseReward()
    {
        var rows = Decode(
            Single(ChaseRoll, "Dice_ticket_01,30", "Dice_ticket_01"),
            Single(2, "fork_vine", "fork_vine"));

        Assert.Equal(("chase_reward", -4, 30L), (rows[0].ResultType, rows[0].Dice!.Value, rows[0].Quantity!.Value));
    }

    [Fact]
    public void QuantityComesFromTheResultText()
    {
        var rows = Decode(
            Single(4, "Dicelimite,5", "Dicelimite"),
            Single(2, "Dice_ticket_02,50", "Dice_ticket_02"),
            Single(1, "Dicelimite", "Dicelimite"));

        Assert.Equal([5L, 50L, 1L], rows.Select(row => row.Quantity!.Value));
    }

    [Fact]
    public void AnExtraItemDoesNotReplaceTheRewardOrAddResultText()
    {
        var row = Decode(Row(5, "1076", ["Dice_ticket_01", "1076"]), Single(1, "fork_vine", "fork_vine"))[0];

        Assert.Equal("1076", row.RewardId);
        Assert.Null(row.ResultText);
    }

    [Fact]
    public void TimestampBytesStayTheSameSoUidsDoNotChange()
    {
        var row = Decode(Single(4, "fork_vine", "fork_vine"), Single(1, "fork_nonos", "fork_nonos"))[0];

        Assert.Equal(Convert.ToHexStringLower(BitConverter.GetBytes(Ticks * 4)), row.TimestampRawHex);
        Assert.Equal("heuristic_enriched", row.DecoderMode);
    }
}
