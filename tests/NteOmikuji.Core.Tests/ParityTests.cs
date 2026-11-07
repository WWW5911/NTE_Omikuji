using System.Text.Json;
using System.Text.Json.Nodes;
using NteOmikuji.Core.Export;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;
using Xunit;

namespace NteOmikuji.Core.Tests;

/// <summary>Compares against output captured from the Python exporter (see Data/python_golden.json).</summary>
public class ParityTests
{
    private static readonly RewardCatalog Catalog = RewardCatalog.Bundled.Value;
    private static readonly JsonElement Golden = LoadJson("python_golden.json");
    private static readonly JsonElement Fixture = LoadJson("synthetic_history_network.json");

    private static JsonElement LoadJson(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name))).RootElement.Clone();

    private static HistorySession Replay()
    {
        var session = new HistorySession(Fixture.GetProperty("local_ip").GetString()!, Catalog);
        foreach (var packet in Fixture.GetProperty("packets").EnumerateArray())
        {
            if (packet.GetProperty("scenario").GetString() == "replay")
            {
                session.Process(new IpPacket(
                    packet.GetProperty("src_ip").GetString()!,
                    packet.GetProperty("dst_ip").GetString()!,
                    packet.GetProperty("src_port").GetInt32(),
                    packet.GetProperty("dst_port").GetInt32(),
                    Convert.FromHexString(packet.GetProperty("payload_hex").GetString()!),
                    false));
            }
        }
        return session;
    }

    [Theory]
    [InlineData(HistoryKind.Permanent, "permanent")]
    [InlineData(HistoryKind.ArcMiracleBox, "arc")]
    public void ReplayedExportRecordsMatchPython(HistoryKind kind, string goldenKey)
    {
        var history = Replay().BuildHistories().Single(entry => entry.Banner.Kind == kind);
        var export = ExportWriter.BuildHistory(history, new ExportContext("packet_capture"));

        var expected = JsonNode.Parse(Golden.GetProperty(goldenKey).GetRawText());
        Assert.True(JsonNode.DeepEquals(expected, export["records"]), export["records"]!.ToJsonString());
    }

    [Fact]
    public void ReplayMatchesFixtureExpectations()
    {
        var expected = Fixture.GetProperty("expected");
        var histories = Replay().BuildHistories();
        var permanent = histories.Single(entry => entry.Banner.Kind == HistoryKind.Permanent);
        var arc = histories.Single(entry => entry.Banner.Kind == HistoryKind.ArcMiracleBox);

        Assert.Equal(expected.GetProperty("permanent_records").GetInt32(), permanent.Rows.Count);
        Assert.Equal(expected.GetProperty("arc_records").GetInt32(), arc.Rows.Count);
        Assert.Equal(expected.GetProperty("permanent_first_uid").GetString(), permanent.Rows[0].Uid);
        Assert.Equal(expected.GetProperty("arc_first_uid").GetString(), arc.Rows[0].Uid);
        Assert.Equal([1, 2, 3, 4, 5], permanent.PagesSeen);
    }

    public static TheoryData<string> SampleLabels()
    {
        var labels = new TheoryData<string>();
        foreach (var sample in Golden.GetProperty("samples").EnumerateObject())
        {
            labels.Add(sample.Name);
        }
        return labels;
    }

    [Theory]
    [MemberData(nameof(SampleLabels))]
    public void ProtocolSamplesDecodeLikePython(string label)
    {
        var packet = Fixture.GetProperty("packets").EnumerateArray().Single(entry => entry.GetProperty("label").GetString() == label);
        var rows = MonopolyDecoder.DecodeResponse(Convert.FromHexString(packet.GetProperty("payload_hex").GetString()!), Catalog);
        AssertRowsMatch(Golden.GetProperty("samples").GetProperty(label), rows);
    }

    [Theory]
    [InlineData("monopoly")]
    [InlineData("monopoly_shift3")]
    [InlineData("chase")]
    [InlineData("secondary")]
    public void StructuredMonopolyBlocksDecodeLikePython(string label)
    {
        var sample = Golden.GetProperty("structured").GetProperty(label);
        var rows = MonopolyDecoder.DecodeResponse(Convert.FromHexString(sample.GetProperty("hex").GetString()!), Catalog);
        AssertRowsMatch(sample.GetProperty("rows"), rows);
    }

    [Theory]
    [InlineData("fork")]
    [InlineData("fork_shift5")]
    public void StructuredForkBlocksDecodeLikePython(string label)
    {
        var sample = Golden.GetProperty("fork").GetProperty(label);
        var rows = ArcDecoder.DecodeResponse(Convert.FromHexString(sample.GetProperty("hex").GetString()!), Catalog);
        AssertRowsMatch(sample.GetProperty("rows"), rows, hasResult: false);
    }

    private static void AssertRowsMatch(JsonElement expected, List<HistoryRow> rows, bool hasResult = true)
    {
        Assert.Equal(expected.GetArrayLength(), rows.Count);
        foreach (var (want, row) in expected.EnumerateArray().Zip(rows))
        {
            Assert.Equal(want.GetProperty("reward_id").GetString(), row.RewardId);
            Assert.Equal(want.GetProperty("reward_type").GetString(), row.RewardType);
            Assert.Equal(want.GetProperty("reward_name").GetString(), row.RewardName);
            Assert.Equal(want.GetProperty("reward_rank").GetString(), row.RewardRank);
            Assert.Equal(want.GetProperty("timestamp_decoded").GetString(), row.TimestampDecoded);
            Assert.Equal(want.GetProperty("timestamp_raw_hex").GetString(), row.TimestampRawHex);
            if (hasResult)
            {
                Assert.Equal(want.GetProperty("result_type").GetString(), row.ResultType);
                Assert.Equal(want.GetProperty("dice").ValueKind == JsonValueKind.Null ? null : want.GetProperty("dice").GetInt32(), row.Dice);
                var quantity = want.GetProperty("quantity");
                Assert.Equal(quantity.ValueKind == JsonValueKind.Null ? null : quantity.GetInt64(), row.Quantity);
            }
        }
    }
}
