using System.Buffers.Binary;
using System.Text;
using NteOmikuji.Core.Export;
using NteOmikuji.Core.Import;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class ArbitraryResultStringTests
{
    private const ulong Ticks = 639_131_653_353_040_000;

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] FString(string value)
    {
        var raw = Encoding.UTF8.GetBytes(value + "\0");
        return [.. U32((uint)raw.Length), .. raw];
    }

    private static byte[] Utf16FString(string value)
    {
        var raw = Encoding.Unicode.GetBytes(value + "\0");
        return [.. U32(unchecked((uint)-(value.Length + 1))), .. raw];
    }

    private static byte[] StructuredBlock(params byte[] row) =>
        [.. RecordBlockParser.MonopolyMarker, 0, .. U32(0), .. U32((uint)row.Length), .. U32(1), .. row];

    private static byte[] MonopolyRow(byte[] item, string result) =>
        [.. U32(2), .. item, .. U32(0), .. U32(0), .. FString(""), .. FString(result), .. FString("CardPool_Character"), .. U64(Ticks)];

    /// <summary>A row ending in a field this app does not know, so the block parser gives up and the pool name has to find it.</summary>
    private static byte[] DriftedBlock(string rewardId)
    {
        byte[] Row(uint roll) => [.. U32(roll), .. FString(rewardId), .. U32(0), .. U32(1), .. FString(rewardId), .. FString("CardPool_Character"), .. U64(Ticks), .. U32(7)];
        return StructuredBlock([.. Row(3), .. Row(4)]) is var block ? [.. block[..(RecordBlockParser.MonopolyMarker.Length + 9)], .. U32(2), .. block[(RecordBlockParser.MonopolyMarker.Length + 13)..]] : [];
    }

    [Theory]
    [InlineData("Brand-New Reward 2.0")]
    [InlineData("限定獎勵_星辰")]
    [InlineData("event:summer/2026")]
    public void RewardsWithUnknownNamesAreKept(string rewardId)
    {
        foreach (var payload in new[] { StructuredBlock(MonopolyRow(FString(rewardId), rewardId)), DriftedBlock(rewardId) })
        {
            var row = MonopolyDecoder.DecodeResponse(payload, RewardCatalog.Empty)[0];
            Assert.Equal(rewardId, row.RewardId);
            Assert.Equal("item", row.RewardType);
            Assert.False(row.IsMappedReward);
            Assert.Equal(1, row.Quantity);
        }
    }

    [Fact]
    public void MappedRewardStillGetsCanonicalIdAndName()
    {
        var row = Assert.Single(MonopolyDecoder.DecodeResponse(StructuredBlock(MonopolyRow(FString("FORK_NONOS"), "FORK_NONOS")), RewardCatalog.Bundled.Value));
        Assert.Equal("fork_nonos", row.RewardId);
        Assert.True(row.IsMappedReward);
        Assert.NotEqual("", row.RewardName);
    }

    [Fact]
    public void StructuredRowKeepsUnicodeRewardAndSeparateResultText()
    {
        var payload = StructuredBlock(MonopolyRow(FString("活動獎勵 α,3"), "抽中了隱藏結果"));
        var row = Assert.Single(MonopolyDecoder.DecodeResponse(payload, RewardCatalog.Empty));
        Assert.Equal("活動獎勵 α", row.RewardId);
        Assert.Equal(3, row.Quantity);
        Assert.Equal("抽中了隱藏結果", row.ResultText);
    }

    [Fact]
    public void StructuredRowReadsUtf16Strings()
    {
        var payload = StructuredBlock(MonopolyRow(Utf16FString("寬字元獎勵"), "寬字元獎勵"));
        var row = Assert.Single(MonopolyDecoder.DecodeResponse(payload, RewardCatalog.Empty));
        Assert.Equal("寬字元獎勵", row.RewardId);
        Assert.Null(row.ResultText);
    }

    [Theory]
    [InlineData("Item, Special Edition", "Item, Special Edition", 1)]
    [InlineData("Item,0", "Item,0", 1)]
    [InlineData("Item,7", "Item", 7)]
    [InlineData(",7", ",7", 1)]
    public void ItemSpecOnlySplitsAPositiveCount(string spec, string id, long count) =>
        Assert.Equal((id, count), RecordBlockParser.ParseItemSpec(spec));

    [Fact]
    public void ExportPreservesTextAndAddsResultTextOnlyWhenPresent()
    {
        var withResult = new HistoryRow { RewardId = "限定", ResultText = "結果 ✓", PoolGroupId = "Lottery_Permanent", ResultType = "dice" };
        var plain = new HistoryRow { RewardId = "限定", PoolGroupId = "Lottery_Permanent", ResultType = "dice" };
        var history = new PoolHistory(PoolInfo.Permanent, [withResult, plain], [], [1]);

        var export = HistoryJsonWriter.BuildHistory(history, new ExportContext("live_capture"));
        var json = HistoryJsonWriter.ToJson(export);

        Assert.Contains("\"reward_id\": \"限定\"", json);
        Assert.Contains("\"result_text\": \"結果 ✓\"", json);
        Assert.Null(export["records"]![1]!["result_text"]);
    }

    [Fact]
    public void FileNameDropsPathCharactersFromUntrustedUid()
    {
        var name = HistoryJsonWriter.FileName(@"..\..\evil:1", "Limited", new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal("evil1_Limited_20260102_030405.json", name);
    }
}

public class RobustnessTests
{
    [Fact]
    public void MalformedResponsesNeverThrow()
    {
        var random = new Random(1234);
        for (var i = 0; i < 2000; i++)
        {
            var payload = new byte[random.Next(0, 400)];
            random.NextBytes(payload);
            MonopolyDecoder.DecodeResponse(payload, RewardCatalog.Empty);
            ArcDecoder.DecodeResponse(payload, RewardCatalog.Empty);
            MysteryBoxDecoder.DecodeResponse(payload, RewardCatalog.Empty);
            AchievementDecoder.Extract(payload);
            ServerRegions.ExtractServerId(payload);
        }
    }

    [Fact]
    public void SessionSeparatesInboundFromOutboundPackets()
    {
        var session = new HistorySession("192.0.2.1", RewardCatalog.Empty);
        session.Process(new IpPacket("192.0.2.1", "198.51.100.1", 1, 2, [], false));
        session.Process(new IpPacket("192.0.2.1", "198.51.100.1", 1, 2, [], true));
        session.Process(new IpPacket("198.51.100.1", "192.0.2.1", 2, 1, [], false));

        Assert.Equal(2, session.OutboundCount);
        Assert.Equal(1, session.InboundCount);
        Assert.Equal(3, session.PacketCount);
    }

    [Fact]
    public void TruncatedStructuredBlocksAreIgnored()
    {
        byte[] block = [.. RecordBlockParser.MonopolyMarker, 0, 0, 0, 0, 0, 200, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0];
        for (var length = 0; length <= block.Length; length++)
        {
            Assert.Empty(RecordBlockParser.ParseRecords(block[..length], RecordBlockType.Monopoly));
        }
    }

    [Theory]
    [InlineData("99999999:abc,")]
    [InlineData("-1:,")]
    [InlineData("3:ab")]
    [InlineData("0:?")]
    public void CorruptFlowsAreRejected(string text) =>
        Assert.Throws<InvalidDataException>(() => FlowCaptureFile.ReadFlows(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void DeeplyNestedFlowIsRejectedInsteadOfOverflowingTheStack()
    {
        var nested = "0:]";
        for (var i = 0; i < 200; i++)
        {
            nested = $"{nested.Length}:{nested}]";
        }
        Assert.Throws<InvalidDataException>(() => FlowCaptureFile.ReadFlows(Encoding.ASCII.GetBytes(nested)));
    }

    [Fact]
    public void PageRunStartsAtPageOneAndReportsGaps()
    {
        CapturedPage Page(int number) => new(number, HistoryKind.Permanent, [], 0, 5);
        var (run, warnings) = HistoryAssembly.SelectRun([Page(1), Page(2), Page(4)]);

        Assert.Equal([1, 2], run.Select(page => page.Page));
        Assert.Equal("PAGE_GAP_DETECTED", Assert.Single(warnings).Code);
        Assert.Equal([4], warnings[0].IgnoredPages!);
    }

    [Fact]
    public void AchievementBufferIsBoundedPerFlow()
    {
        var collector = new AchievementCollector("192.0.2.1");
        var chunk = new byte[1024 * 1024];
        for (uint i = 0; i < 64; i++)
        {
            collector.Add(new IpPacket("198.51.100.1", "192.0.2.1", 1, 2, chunk, true, i * (uint)chunk.Length));
        }
        Assert.Empty(collector.Records);
    }
}
