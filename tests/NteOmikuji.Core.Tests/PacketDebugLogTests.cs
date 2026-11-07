using System.Text;
using System.Text.Json;
using NteOmikuji.Core.Export;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class PacketDebugLogTests
{
    private const string Local = "192.0.2.10";
    private const string Remote = "198.51.100.20";

    private static readonly RewardCatalog Catalog = RewardCatalog.Bundled.Value;
    private static readonly JsonElement Fixture =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "sample_traffic.json"))).RootElement.Clone();

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailingStream : MemoryStream
    {
        public bool Fail { get; set; }

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Fail)
            {
                throw new IOException("disk full");
            }
            base.Write(buffer, offset, count);
        }
    }

    private static IEnumerable<IpPacket> ReplayPackets() =>
        Fixture.GetProperty("packets").EnumerateArray()
            .Where(packet => packet.GetProperty("scenario").GetString() == "replay")
            .Select(packet => new IpPacket(
                packet.GetProperty("src_ip").GetString()!,
                packet.GetProperty("dst_ip").GetString()!,
                packet.GetProperty("src_port").GetInt32(),
                packet.GetProperty("dst_port").GetInt32(),
                Convert.FromHexString(packet.GetProperty("payload_hex").GetString()!),
                false));

    private static IpPacket FirstRequest() => ReplayPackets().First(packet => packet.SrcIp == Local && MonopolyDecoder.RequestKind(packet.Payload) is not null);

    private static IpPacket Inbound(IpPacket request, byte[] payload, bool isTcp = false) =>
        new(request.DstIp, request.SrcIp, request.DstPort, request.SrcPort, payload, isTcp);

    private static List<JsonElement> Lines(MemoryStream output) =>
        Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

    private static List<JsonElement> Entries(MemoryStream output) =>
        Lines(output).Where(line => line.TryGetProperty("direction", out _)).ToList();

    private static (HistorySession Session, MemoryStream Output, ManualTime Time, PacketDebugLog Log) Logged(long maxBytes = PacketDebugLog.DefaultMaxBytes)
    {
        var output = new MemoryStream();
        var time = new ManualTime();
        var log = new PacketDebugLog(output, "test", time, maxBytes);
        return (new HistorySession(Local, Catalog) { DebugLog = log }, output, time, log);
    }

    [Fact]
    public void LoggingDoesNotChangeWhatTheSessionDecodes()
    {
        var plain = new HistorySession(Local, Catalog);
        var (logged, _, _, _) = Logged();

        foreach (var packet in ReplayPackets())
        {
            Assert.Equal(plain.Process(packet).Count, logged.Process(packet).Count);
        }

        var expected = plain.BuildHistories().Select(history => HistoryJsonWriter.ToJson(HistoryJsonWriter.BuildHistory(history, new ExportContext("x"))));
        var actual = logged.BuildHistories().Select(history => HistoryJsonWriter.ToJson(HistoryJsonWriter.BuildHistory(history, new ExportContext("x"))));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RecordedBytesReplayToTheSameHistory()
    {
        var (session, output, _, _) = Logged();
        foreach (var packet in ReplayPackets())
        {
            session.Process(packet);
        }

        var replayed = new HistorySession(Local, Catalog);
        foreach (var entry in Entries(output))
        {
            var remote = entry.GetProperty("remote").GetString()!.Split(':');
            var payload = Convert.FromHexString(entry.GetProperty("payload_hex").GetString()!);
            var localPort = entry.GetProperty("local_port").GetInt32();
            var remotePort = int.Parse(remote[1]);
            replayed.Process(entry.GetProperty("direction").GetString() == "out"
                ? new IpPacket(Local, remote[0], localPort, remotePort, payload, false)
                : new IpPacket(remote[0], Local, remotePort, localPort, payload, false));
        }

        Assert.Equal(
            session.BuildHistories().Select(history => (history.Pool.Kind, history.Rows.Select(row => row.Uid).ToList())),
            replayed.BuildHistories().Select(history => (history.Pool.Kind, history.Rows.Select(row => row.Uid).ToList())),
            new ComparerForKindAndUids());
    }

    private sealed class ComparerForKindAndUids : IEqualityComparer<(HistoryKind Kind, List<string> Uids)>
    {
        public bool Equals((HistoryKind Kind, List<string> Uids) x, (HistoryKind Kind, List<string> Uids) y) => x.Kind == y.Kind && x.Uids.SequenceEqual(y.Uids);

        public int GetHashCode((HistoryKind Kind, List<string> Uids) value) => value.Kind.GetHashCode();
    }

    [Fact]
    public void RepliesCarryTheirPagesAndRowsWithRecordBytes()
    {
        var (session, output, _, _) = Logged();
        foreach (var packet in ReplayPackets())
        {
            session.Process(packet);
        }

        var entries = Entries(output);
        var request = entries.First(entry => entry.GetProperty("role").GetString() == "request");
        Assert.Equal(("out", "Permanent", 1), (request.GetProperty("direction").GetString(), request.GetProperty("kind").GetString(), request.GetProperty("page").GetInt32()));

        var reply = entries.First(entry => entry.GetProperty("role").GetString() == "response");
        Assert.Equal("in", reply.GetProperty("direction").GetString());
        Assert.NotEmpty(reply.GetProperty("pages").EnumerateArray());
        var row = reply.GetProperty("rows")[0];
        Assert.False(string.IsNullOrEmpty(row.GetProperty("reward_id").GetString()));
        Assert.False(string.IsNullOrEmpty(row.GetProperty("record_hex").GetString()));
        Assert.Equal(reply.GetProperty("length").GetInt32() * 2, reply.GetProperty("payload_hex").GetString()!.Length);
    }

    [Fact]
    public void NeitherLocalAddressNorTcpIsWritten()
    {
        var (session, output, _, _) = Logged();
        var request = FirstRequest();
        session.Process(request);
        session.Process(Inbound(request, Encoding.UTF8.GetBytes("password=secret"), isTcp: true));
        session.Process(new IpPacket(request.SrcIp, request.DstIp, request.SrcPort, request.DstPort, Encoding.UTF8.GetBytes("password=secret"), true));

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Single(Entries(output));
        Assert.DoesNotContain(Local, text);
        Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes("password")), text);
    }

    [Fact]
    public void UnrelatedTrafficIsNotWritten()
    {
        var (session, output, _, _) = Logged();
        // Nothing has asked for history yet.
        session.Process(new IpPacket(Remote, Local, 30031, 49152, new byte[200], false));
        var request = FirstRequest();
        session.Process(request);
        // Another server, another local port, and the client's own other traffic.
        session.Process(new IpPacket("203.0.113.5", Local, request.DstPort, request.SrcPort, new byte[200], false));
        session.Process(new IpPacket(request.DstIp, Local, request.DstPort, request.SrcPort + 1, new byte[200], false));
        session.Process(new IpPacket(Local, request.DstIp, request.SrcPort, request.DstPort, new byte[200], false));

        Assert.Equal("request", Assert.Single(Entries(output)).GetProperty("role").GetString());
    }

    [Fact]
    public void PacketsAfterARequestAreKeptOnlyInsideTheWindow()
    {
        var (session, output, time, _) = Logged();
        var request = FirstRequest();
        session.Process(request);

        session.Process(Inbound(request, [1, 2, 3]));
        time.Now += PacketDebugLog.RequestWindow;
        session.Process(Inbound(request, [4, 5, 6]));
        time.Now += TimeSpan.FromMilliseconds(1);
        session.Process(Inbound(request, [7, 8, 9]));

        Assert.Equal(["request", "other", "other"], Entries(output).Select(entry => entry.GetProperty("role").GetString()));
        Assert.Equal("040506", Entries(output)[^1].GetProperty("payload_hex").GetString());
    }

    [Fact]
    public void DecoderFailureInTheLogPathIsNotFatal()
    {
        var (session, output, _, _) = Logged();
        var request = FirstRequest();
        session.Process(request);

        var noise = new byte[4096];
        new Random(1).NextBytes(noise);
        session.Process(Inbound(request, noise));

        Assert.Equal(2, Entries(output).Count);
    }

    [Fact]
    public void SizeLimitEndsTheLogButNotTheCapture()
    {
        var (session, output, _, log) = Logged(maxBytes: 2048);
        var plain = new HistorySession(Local, Catalog);

        foreach (var packet in ReplayPackets())
        {
            Assert.Equal(plain.Process(packet).Count, session.Process(packet).Count);
        }

        Assert.True(log.Truncated);
        Assert.True(Lines(output)[^1].GetProperty("truncated").GetBoolean());
        Assert.True(log.EntryCount < ReplayPackets().Count());
        Assert.Equal(plain.BuildHistories().Sum(history => history.Rows.Count), session.BuildHistories().Sum(history => history.Rows.Count));
    }

    [Fact]
    public void WriteFailureEndsTheLogButNotTheCapture()
    {
        var output = new FailingStream();
        var log = new PacketDebugLog(output, "test");
        var session = new HistorySession(Local, Catalog) { DebugLog = log };
        var plain = new HistorySession(Local, Catalog);
        output.Fail = true;

        foreach (var packet in ReplayPackets())
        {
            Assert.Equal(plain.Process(packet).Count, session.Process(packet).Count);
        }

        Assert.Equal("disk full", log.Failure);
        Assert.Equal(0, log.EntryCount);
    }

    [Fact]
    public void CreateMakesTheFolderAndWritesAHeader()
    {
        using var folder = new TempFolder();
        var target = Path.Combine(folder.Path, PacketDebugLog.FolderName);

        using var log = PacketDebugLog.Create(target, "test");

        Assert.Equal(target, Path.GetDirectoryName(log.Path));
        Assert.EndsWith(".jsonl", log.Path);
        Assert.True(File.Exists(log.Path));
        log.Dispose();
        var header = JsonDocument.Parse(File.ReadLines(log.Path!).First()).RootElement;
        Assert.Equal("nte_packet_debug", header.GetProperty("format").GetString());
        Assert.Equal("test", header.GetProperty("capture").GetString());
    }
}
