using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;
using Xunit;

namespace NteOmikuji.Core.Tests;

/// <summary>The layouts follow a third-party description of version 1.4 traffic, not a real capture.</summary>
public class MysteryBoxPoolTests
{
    private const string Local = "192.0.2.1";
    private const string Remote = "198.51.100.1";
    private const string PoolA = "MangHe_bsj2";
    private const string PoolB = "MangHe_summer_01";

    private static byte[] Request(string pool, int page)
    {
        var id = pool.Select(letter => (byte)(letter * 2)).Append((byte)0).ToArray();
        var content = new byte[44 + id.Length + 9];
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(26), 2060);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(31), (uint)page * 2);
        content[35] = 0x1E;
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(40), (uint)id.Length * 2);
        id.CopyTo(content, 44);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(44 + id.Length), 2060);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(44 + id.Length + 5), 10);
        return content;
    }

    private static byte[] LegacyRequest(int page)
    {
        var content = new byte[54];
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(26), 2060);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(31), (uint)page * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(35), 2110);
        return content;
    }

    private static byte[] Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        return [.. BitConverter.GetBytes((uint)bytes.Length), .. bytes];
    }

    /// <param name="pool">Null writes the layout from before pools were added.</param>
    private static byte[] Reply(string? pool, int rows, string reward = "vehicle039", int firstSecond = 0)
    {
        using var body = new MemoryStream();
        for (var i = 0; i < rows; i++)
        {
            body.Write(Text(reward));
            body.Write(BitConverter.GetBytes(2u));
            body.WriteByte(1);
            body.Write(BitConverter.GetBytes(new DateTime(2026, 7, 8, 19, 9, 33, DateTimeKind.Utc).AddSeconds(firstSecond + i).Ticks));
            if (pool is not null)
            {
                body.Write(Text(pool));
                body.Write(BitConverter.GetBytes(7u));
            }
        }
        var size = (uint)body.Length + (pool is null ? 8u : 4u);
        return [.. new byte[16], .. "FGashaponLotteryRecordData\0"u8, .. BitConverter.GetBytes(0u), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes((uint)rows), .. body.ToArray(), .. new byte[100]];
    }

    private static IpPacket Out(byte[] payload) => new(Local, Remote, 1000, 2000, payload, false);

    private static IpPacket In(byte[] payload) => new(Remote, Local, 2000, 1000, payload, false);

    private static HistorySession NewSession() => new(Local, RewardCatalog.Empty);

    private static void Scan(HistorySession session, string pool, int page, int rows, int firstSecond)
    {
        session.Process(Out(Request(pool, page)));
        session.Process(In(Reply(pool, rows, firstSecond: firstSecond)));
    }

    [Fact]
    public void ARequestNamesItsPool()
    {
        var request = Request(PoolA, 3);

        Assert.True(MysteryBoxDecoder.IsRequest(request));
        Assert.Equal(PoolA, MysteryBoxDecoder.RequestPool(request));
        Assert.Equal(3, MysteryBoxDecoder.RequestPage(request));
    }

    [Fact]
    public void TheOlderRequestStillCounts()
    {
        Assert.True(MysteryBoxDecoder.IsRequest(LegacyRequest(2)));
        Assert.Null(MysteryBoxDecoder.RequestPool(LegacyRequest(2)));
    }

    [Theory]
    [InlineData(40, 23u)] // An odd doubled length.
    [InlineData(40, 0u)]
    [InlineData(40, 1000u)]
    public void ABadPoolLengthIsNotARequest(int at, uint value)
    {
        var request = Request(PoolA, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(at), value);

        Assert.False(MysteryBoxDecoder.IsRequest(request));
    }

    [Fact]
    public void AMissingTrailerIsNotARequest()
    {
        var request = Request(PoolA, 1);
        request[^9] ^= 0xFF;

        Assert.False(MysteryBoxDecoder.IsRequest(request));
    }

    [Fact]
    public void AReplyCarriesEachRowsPool()
    {
        var rows = MysteryBoxDecoder.DecodeResponse(Reply(PoolA, 5), RewardCatalog.Empty);

        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Equal((PoolA, "vehicle039", 2u), (row.BannerId, row.RewardId, row.Quantity)));
        Assert.Equal("2026-07-08 19:09:33", rows[0].TimestampDecoded);
    }

    [Fact]
    public void TheOlderReplyHasNoPool()
    {
        var rows = MysteryBoxDecoder.DecodeResponse(Reply(null, 3), RewardCatalog.Empty);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Null(row.BannerId));
    }

    [Fact]
    public void ViewingASecondPoolKeepsTheFirst()
    {
        var session = NewSession();
        Scan(session, PoolA, 1, 5, 0);
        Scan(session, PoolA, 2, 2, 5);
        Scan(session, PoolB, 1, 3, 100);

        var history = Assert.Single(session.BuildHistories());

        Assert.Empty(history.Warnings);
        Assert.Equal(7, history.Rows.Count(row => row.BannerId == PoolA));
        Assert.Equal(3, history.Rows.Count(row => row.BannerId == PoolB));
        Assert.Equal(10, history.Rows.Select(row => row.Uid).Distinct().Count());
    }

    [Fact]
    public void RescanningAPoolDoesNotDuplicateIt()
    {
        var session = NewSession();
        Scan(session, PoolA, 1, 3, 0);
        Scan(session, PoolA, 1, 3, 0);

        Assert.Equal(3, Assert.Single(session.BuildHistories()).Rows.Count);
    }

    [Fact]
    public void ARepliesGoToTheRequestOfTheirPool()
    {
        var session = NewSession();
        session.Process(Out(Request(PoolA, 1)));
        session.Process(Out(Request(PoolB, 1)));

        var pages = session.Process(In(Reply(PoolB, 3)));

        Assert.Equal(PoolB, Assert.Single(pages).Pool);
        Assert.Equal(PoolA, Assert.Single(session.Process(In(Reply(PoolA, 3, firstSecond: 50)))).Pool);
    }

    [Fact]
    public void AGapInOnePoolIsNotHiddenByAnother()
    {
        var session = NewSession();
        Scan(session, PoolA, 1, 5, 0);
        Scan(session, PoolA, 2, 5, 5);
        Scan(session, PoolB, 1, 5, 100);
        Scan(session, PoolB, 3, 5, 110);

        Assert.Equal([2], session.MissingPages(HistoryKind.MysteryBox));
        Assert.Equal(4, session.PageCount(HistoryKind.MysteryBox));
        Assert.Contains(Assert.Single(session.BuildHistories()).Warnings, warning => warning.Code == "PAGE_GAP_DETECTED");
    }

    [Fact]
    public void ABoardLookingRewardStaysInTheMysteryBox()
    {
        var reply = Reply(PoolA, 3, reward: "Fashion_MangHe_01");
        var session = NewSession();
        session.Process(Out(Request(PoolA, 1)));

        var page = Assert.Single(session.Process(In(reply)));

        Assert.Equal(HistoryKind.MysteryBox, page.Kind);
        Assert.Equal("Fashion_MangHe_01", Assert.Single(session.BuildHistories()).Rows[0].RewardId);
    }

    [Fact]
    public void TheLogRecordsThePool()
    {
        using var output = new MemoryStream();
        var session = NewSession();
        using var log = new PacketDebugLog(new NonClosing(output), "test");
        session.DebugLog = log;

        Scan(session, PoolA, 1, 3, 0);

        var lines = Lines(output);
        Assert.Equal(PoolA, lines[1]["pool"]!.GetValue<string>());
        Assert.Equal(PoolA, lines[2]["pages"]![0]!["pool"]!.GetValue<string>());
        Assert.Equal(PoolA, lines[2]["rows"]![0]!["pool"]!.GetValue<string>());
    }

    [Fact]
    public void AnUnknownLayoutStillLeavesItsBytes()
    {
        using var output = new MemoryStream();
        var session = NewSession();
        using var log = new PacketDebugLog(new NonClosing(output), "test");
        session.DebugLog = log;
        var request = Request(PoolA, 1);
        request[35] = 0x7F; // No longer a layout the decoder accepts.
        Assert.False(MysteryBoxDecoder.IsRequest(request));

        session.Process(Out(request));
        session.Process(In(new byte[200]));

        var lines = Lines(output);
        Assert.Equal("unrecognized_request", lines[1]["role"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexStringLower(request), lines[1]["payload_hex"]!.GetValue<string>());
        Assert.Equal("other", lines[2]["role"]!.GetValue<string>());
        Assert.Empty(session.KindsSeen());
    }

    [Fact]
    public void OrdinaryTrafficIsNotLogged()
    {
        using var output = new MemoryStream();
        var session = NewSession();
        using var log = new PacketDebugLog(new NonClosing(output), "test");
        session.DebugLog = log;

        session.Process(Out(new byte[200]));
        session.Process(In(new byte[200]));

        Assert.Single(Lines(output));
    }

    private static List<JsonNode> Lines(MemoryStream output) =>
        Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!).ToList();

    private sealed class NonClosing(Stream inner) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }
}
