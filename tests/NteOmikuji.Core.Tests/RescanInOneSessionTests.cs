using System.Text;
using NteOmikuji.Core.Export;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;
using Xunit;

namespace NteOmikuji.Core.Tests;

/// <summary>
/// A capture can stay open across pulls, so one session can see the history before and after it moved.
/// Pages from before the move sit at other positions now and must not be joined to pages from after it.
/// </summary>
public sealed class RescanInOneSessionTests : IDisposable
{
    private const string Local = "10.0.0.2";
    private const string Server = "1.2.3.4";
    private const string Uid = "214000000000";
    private const string LimitedArc = "fork_TigerTally";
    private const string LimitedCharacter = "1010";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nte-rescan-" + Guid.NewGuid().ToString("N"));
    private readonly HistorySession _session = new(Local, RewardCatalog.Bundled.Value);
    private readonly HistoryLibrary _library;

    public RescanInOneSessionTests() => _library = new HistoryLibrary(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] FString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        return [.. U32((uint)bytes.Length), .. bytes];
    }

    private static ulong Minute(int minute) => (ulong)new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute).Ticks;

    internal static byte[] Board(string item, int minute, string eventId = "Lottery_Nanali", byte[]? tail = null) =>
        [.. U32(3), .. FString(item), .. U32(0), .. U32(1), .. FString(item), .. FString("CardPool_Character"), .. BitConverter.GetBytes(Minute(minute)), .. FString(eventId), .. (tail ?? [])];

    internal static byte[] Arc(string item, int minute) =>
        [.. FString(item), .. FString("ForkLottery_Nanali"), .. BitConverter.GetBytes(Minute(minute))];

    private static List<byte[]> TenPull(Func<string, int, byte[]> row, string prefix, int minute, string? hit = null) =>
        Enumerable.Range(0, 10).Select(i => row(i == 1 && hit is not null ? hit : $"{prefix}{i}", minute)).ToList();

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

    private static byte[] Block(byte[] marker, byte[] envelope, IReadOnlyList<byte[]> rows)
    {
        var body = rows.SelectMany(row => row).ToArray();
        return ToWire([.. envelope, .. marker, 0, .. U32(0), .. U32((uint)body.Length), .. U32((uint)rows.Count), .. body]);
    }

    private static byte[] Envelope(params (int Distance, uint Value)[] fields)
    {
        var envelope = new byte[50];
        foreach (var (distance, value) in fields)
        {
            BitConverter.TryWriteBytes(envelope.AsSpan(envelope.Length - distance), value);
        }
        return envelope;
    }

    internal static byte[] BoardReply(IReadOnlyList<byte[]> rows, int page) =>
        Block(RecordBlockParser.MonopolyMarker, Envelope((26, 0x03000000), (22, 0x80000000), (18, (uint)page), (14, 527), (10, 1), (6, 1_774_080)), rows);

    internal static byte[] ArcReply(IReadOnlyList<byte[]> rows, int page) =>
        Block(RecordBlockParser.ForkMarker, Envelope((17, 0x03000000), (13, 0x80000000), (9, (uint)page), (5, 5906)), rows);

    private static byte[] BoardRequest(int page)
    {
        var content = new byte[45];
        BitConverter.TryWriteBytes(content.AsSpan(31), (uint)(page * 4));
        BitConverter.TryWriteBytes(content.AsSpan(35), 4220u);
        BitConverter.TryWriteBytes(content.AsSpan(40), 8u);
        return content;
    }

    private static byte[] ArcRequest(int page)
    {
        var content = new byte[34];
        BitConverter.TryWriteBytes(content.AsSpan(24), 2060u);
        BitConverter.TryWriteBytes(content.AsSpan(29), (uint)(page * 2));
        return content;
    }

    private void Scan(Func<int, byte[]> request, Func<IReadOnlyList<byte[]>, int, byte[]> reply, IReadOnlyList<byte[]> rows, params int[] pages)
    {
        foreach (var page in pages)
        {
            _session.Process(new IpPacket(Local, Server, 50000, 30031, request(page), false));
            _session.Process(new IpPacket(Server, Local, 30031, 50000, reply(rows.Skip((page - 1) * 5).Take(5).ToList(), page - 1), false));
        }
    }

    private void ScanBoard(IReadOnlyList<byte[]> rows, params int[] pages) => Scan(BoardRequest, BoardReply, rows, pages);

    private void ScanArc(IReadOnlyList<byte[]> rows, params int[] pages) => Scan(ArcRequest, ArcReply, rows, pages);

    /// <summary>What the app does after every captured page.</summary>
    private IReadOnlyList<CaptureWarning> Ingest()
    {
        foreach (var earlier in _session.TakeSuperseded())
        {
            _library.Merge(Uid, earlier.Pool.Kind, earlier.Rows, null, "live_capture", "test", RewardCatalog.Bundled.Value);
        }
        var history = _session.BuildHistories().Single();
        _library.Merge(Uid, history.Pool.Kind, history.Rows, null, "live_capture", "test", RewardCatalog.Bundled.Value);
        return history.Warnings;
    }

    private IReadOnlyList<HistoryRow> Saved() => _library.Histories(Uid).Single().Rows;

    private static string[] Group(IEnumerable<HistoryRow> rows, string member)
    {
        var list = rows.ToList();
        var second = list.First(row => row.RewardId.Equals(member, StringComparison.OrdinalIgnoreCase)).TimestampDecoded;
        return list.Where(row => row.TimestampDecoded == second).Select(row => row.RewardId).ToArray();
    }

    [Fact]
    public void ViewingOnlyTheFirstPageAfterATenPullKeepsTheOneBefore()
    {
        var earlier = TenPull((item, minute) => Board(item, minute), "a", 900, LimitedCharacter);
        var older = Enumerable.Range(0, 15).Select(i => Board("fork_nonos", 800 - i)).ToList();
        ScanBoard([.. earlier, .. older], 1, 2, 3, 4, 5);
        Ingest();

        var newer = TenPull((item, minute) => Board(item, minute), "b", 1000);
        ScanBoard([.. newer, .. earlier, .. older], 1);
        Ingest();

        Assert.Equal(["a0", LimitedCharacter, "a2", "a3", "a4", "a5", "a6", "a7", "a8", "a9"], Group(Saved(), LimitedCharacter));
        Assert.Equal(30, Saved().Count);
    }

    [Fact]
    public void AGapLeftByNewPullsCannotCutATenPull()
    {
        var singles = Enumerable.Range(0, 7).Select(i => Board("fork_vine", 1000 - i)).ToList();
        var batch = TenPull((item, minute) => Board(item, minute), "x", 900, LimitedCharacter);
        var older = Enumerable.Range(0, 8).Select(i => Board("fork_nonos", 800 - i)).ToList();
        List<byte[]> before = [.. singles, .. batch, .. older];
        ScanBoard(before, 1, 2, 3, 4, 5);
        Ingest();

        var newer = Enumerable.Range(0, 3).Select(i => Board("fork_vine", 2002 - i)).ToList();
        ScanBoard([.. newer, .. before], 1, 2);
        Ingest();

        Assert.Equal(["x0", LimitedCharacter, "x2", "x3", "x4", "x5", "x6", "x7", "x8", "x9"], Group(Saved(), LimitedCharacter));
        Assert.Equal(28, Saved().Count);
    }

    [Fact]
    public void ArcTenPullsSurviveTheSameSequence()
    {
        var earlier = TenPull(Arc, "a", 900, LimitedArc);
        var older = TenPull(Arc, "o", 800);
        ScanArc([.. earlier, .. older], 1, 2, 3, 4);
        Ingest();

        var newer = TenPull(Arc, "b", 1000);
        ScanArc([.. newer, .. earlier, .. older], 1);
        Ingest();

        Assert.Equal(["a0", LimitedArc, "a2", "a3", "a4", "a5", "a6", "a7", "a8", "a9"], Group(Saved(), LimitedArc), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(25, Saved().Count);
    }

    [Fact]
    public void ScrollingOnAfterTheHistoryMovedEndsWithEveryRowOnce()
    {
        var earlier = TenPull((item, minute) => Board(item, minute), "a", 900, LimitedCharacter);
        var older = Enumerable.Range(0, 15).Select(i => Board("fork_nonos", 800 - i)).ToList();
        ScanBoard([.. earlier, .. older], 1, 2, 3, 4, 5);
        Ingest();

        var newer = TenPull((item, minute) => Board(item, minute), "b", 1000);
        List<byte[]> now = [.. newer, .. earlier, .. older];
        foreach (var page in new[] { 1, 2, 3, 4, 5, 6, 7 })
        {
            ScanBoard(now, page);
            Assert.Empty(Ingest());
        }

        Assert.Equal(35, Saved().Count);
        Assert.Equal(35, Saved().Select(row => row.Uid).Distinct().Count());
        Assert.Equal(["a0", LimitedCharacter, "a2", "a3", "a4", "a5", "a6", "a7", "a8", "a9"], Group(Saved(), LimitedCharacter));
    }

    [Fact]
    public void AScanOvertakenBeforeItWasSavedIsStillKept()
    {
        var earlier = TenPull((item, minute) => Board(item, minute), "a", 900, LimitedCharacter);
        var older = Enumerable.Range(0, 15).Select(i => Board("fork_nonos", 800 - i)).ToList();
        ScanBoard([.. earlier, .. older], 1, 2, 3, 4, 5);

        var newer = TenPull((item, minute) => Board(item, minute), "b", 1000);
        ScanBoard([.. newer, .. earlier, .. older], 1);
        Ingest();

        Assert.Equal(30, Saved().Count);
        Assert.Equal(["a0", LimitedCharacter, "a2", "a3", "a4", "a5", "a6", "a7", "a8", "a9"], Group(Saved(), LimitedCharacter));
    }

    [Fact]
    public void ASecondPassOverAnUnchangedHistoryStillFillsAGap()
    {
        var rows = Enumerable.Range(0, 15).Select(i => Board($"r{i}", 1000 - i)).ToList();
        ScanBoard(rows, 1, 3);
        Assert.Contains(_session.BuildHistories().Single().Warnings, warning => warning.Code == "PAGE_GAP_DETECTED");

        ScanBoard(rows, 1, 2);

        var history = _session.BuildHistories().Single();
        Assert.Empty(history.Warnings);
        Assert.Equal(15, history.Rows.Count);
    }

    [Fact]
    public void ARepeatedFirstPageKeepsThePagesAlreadySeen()
    {
        var rows = Enumerable.Range(0, 15).Select(i => Board($"r{i}", 1000 - i)).ToList();
        ScanBoard(rows, 1, 2, 3);

        ScanBoard(rows, 1);

        Assert.Equal(15, _session.BuildHistories().Single().Rows.Count);
    }

    [Fact]
    public void ARepeatedPageStillCountsAsAReply()
    {
        var rows = Enumerable.Range(0, 10).Select(i => Board($"r{i}", 1000 - i)).ToList();
        ScanBoard(rows, 1, 2);
        Assert.Equal(2, _session.RepliesSeen(HistoryKind.LimitedCharacter));

        ScanBoard(rows, 1);

        Assert.Equal(3, _session.RepliesSeen(HistoryKind.LimitedCharacter));
    }

    [Fact]
    public void AScanAcrossTwoEventsIsOneRun()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Board($"n{i}", 1000 - i, "Lottery_Xun"))
            .Concat(Enumerable.Range(0, 5).Select(i => Board($"o{i}", 900 - i, "Lottery_Nanali")))
            .ToList();

        ScanBoard(rows, 1, 2);

        var history = _session.BuildHistories().Single();
        Assert.Empty(history.Warnings);
        Assert.Equal(10, history.Rows.Count);
        Assert.Equal(["Lottery_Xun", "Lottery_Nanali"], history.Rows.Select(row => row.EventId).Distinct());
    }
}
