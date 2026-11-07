using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using NteOmikuji.Core.Export;

namespace NteOmikuji.Core.Session;

/// <summary>
/// An optional JSON Lines record of history traffic, for checking decoders against
/// the bytes: UDP history requests, and inbound UDP on the same stream shortly after
/// one. TCP carries login traffic, so it is never written. A problem here only ends
/// the log, because diagnostics must not interrupt a capture.
/// </summary>
public sealed class PacketDebugLog : IDisposable
{
    public const string FolderName = "debug_packets";
    public const long DefaultMaxBytes = 64 * 1024 * 1024;

    /// <summary>Long enough for a reply split across datagrams, short enough to leave out unrelated game traffic.</summary>
    public static readonly TimeSpan RequestWindow = TimeSpan.FromSeconds(5);

    private readonly Dictionary<(string RemoteIp, int RemotePort, int LocalPort), DateTimeOffset> _lastRequest = [];
    private readonly TimeProvider _time;
    private readonly long _maxBytes;
    private StreamWriter? _writer;
    private long _bytes;

    public PacketDebugLog(Stream output, string captureName, TimeProvider? time = null, long maxBytes = DefaultMaxBytes)
    {
        _time = time ?? TimeProvider.System;
        _maxBytes = maxBytes;
        _writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
        WriteLine(new JsonObject
        {
            ["format"] = "nte_packet_debug",
            ["format_version"] = 1,
            ["exporter"] = new JsonObject { ["name"] = HistoryJsonWriter.AppName, ["version"] = HistoryJsonWriter.AppVersion },
            ["capture"] = captureName,
            ["started_at"] = Now(),
        });
    }

    public string? Path { get; private init; }
    public int EntryCount { get; private set; }
    public bool Truncated { get; private set; }
    public string? Failure { get; private set; }

    /// <summary>Starts a new file, named by the time so an earlier log is never overwritten.</summary>
    public static PacketDebugLog Create(string folder, string captureName)
    {
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"packets_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        return new PacketDebugLog(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), captureName) { Path = path };
    }

    public void Request(IpPacket packet, HistoryKind kind, int page, string? pool = null)
    {
        _lastRequest[(packet.DstIp, packet.DstPort, packet.SrcPort)] = _time.GetUtcNow();
        var entry = Entry(packet, "out", "request", packet.DstIp, packet.DstPort, packet.SrcPort);
        entry["kind"] = kind.ToString();
        entry["page"] = page;
        entry["pool"] = pool;
        Write(entry, packet);
    }

    /// <summary>Keeps a request that resembles history but that no decoder accepted, along with whatever answers it.</summary>
    public void UnrecognizedRequest(IpPacket packet)
    {
        _lastRequest[(packet.DstIp, packet.DstPort, packet.SrcPort)] = _time.GetUtcNow();
        Write(Entry(packet, "out", "unrecognized_request", packet.DstIp, packet.DstPort, packet.SrcPort), packet);
    }

    public bool FollowsRequest(IpPacket packet) =>
        !packet.IsTcp
        && _lastRequest.TryGetValue((packet.SrcIp, packet.SrcPort, packet.DstPort), out var at)
        && _time.GetUtcNow() - at <= RequestWindow;

    public void Inbound(IpPacket packet, IReadOnlyList<CapturedPage> pages, IReadOnlyList<HistoryRow> rows, string? decodeError)
    {
        var entry = Entry(packet, "in", pages.Count > 0 ? "response" : "other", packet.SrcIp, packet.SrcPort, packet.DstPort);
        if (pages.Count > 0)
        {
            entry["pages"] = new JsonArray(pages.Select(page => (JsonNode)new JsonObject
            {
                ["kind"] = page.Kind.ToString(),
                ["page"] = page.Page,
                ["slice_start"] = page.SliceStart,
                ["slice_count"] = page.SliceCount,
                ["pool"] = page.Pool,
            }).ToArray());
        }
        if (rows.Count > 0)
        {
            entry["rows"] = new JsonArray(rows.Select(row => (JsonNode)RowNode(row)).ToArray());
        }
        if (decodeError is not null)
        {
            entry["decode_error"] = decodeError;
        }
        Write(entry, packet);
    }

    public void Dispose()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Failure ??= exception.Message;
        }
        _writer = null;
    }

    private JsonObject Entry(IpPacket packet, string direction, string role, string remoteIp, int remotePort, int localPort) => new()
    {
        ["at"] = Now(),
        ["direction"] = direction,
        ["role"] = role,
        ["remote"] = $"{remoteIp}:{remotePort}",
        ["local_port"] = localPort,
        ["length"] = packet.Payload.Length,
    };

    private static JsonObject RowNode(HistoryRow row) => new()
    {
        ["dice"] = row.Dice,
        ["result_type"] = row.ResultType,
        ["result_text"] = row.ResultText,
        ["reward_id"] = row.RewardId,
        ["quantity"] = row.Quantity,
        ["pool"] = row.BannerId,
        ["timestamp"] = row.TimestampDecoded,
        ["timestamp_raw_hex"] = row.TimestampRawHex,
        ["decode_mode"] = row.DecoderMode,
        ["record_hex"] = row.RecordHex,
    };

    private void Write(JsonObject entry, IpPacket packet)
    {
        entry["payload_hex"] = Convert.ToHexStringLower(packet.Payload);
        if (WriteLine(entry))
        {
            EntryCount++;
        }
    }

    private bool WriteLine(JsonObject line)
    {
        if (_writer is null)
        {
            return false;
        }
        try
        {
            var text = line.ToJsonString();
            if (_bytes + text.Length + 1 > _maxBytes)
            {
                Truncated = true;
                _writer.WriteLine(new JsonObject { ["truncated"] = true, ["at"] = Now() }.ToJsonString());
                Dispose();
                return false;
            }
            _writer.WriteLine(text);
            _bytes += text.Length + 1;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Failure = exception.Message;
            Dispose();
            return false;
        }
    }

    private string Now() => _time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
