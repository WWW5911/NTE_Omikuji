using NteOmikuji.Core.Protocol;

namespace NteOmikuji.Core.Session;

/// <summary>Pairs history requests with responses and collects account details from a packet stream.</summary>
public sealed class HistorySession(string localIp, RewardCatalog catalog)
{
    private const int MinResponseLength = 100;
    private const int RowsPerPage = 5;

    private sealed record PendingRequest(int Page, HistoryKind Kind, string SrcIp, string DstIp, int SrcPort, int DstPort, string? Pool = null);

    private readonly List<PendingRequest> _pending = [];
    private readonly List<CapturedPage> _pages = [];
    private readonly Dictionary<string, int> _uidCandidates = [];
    private readonly AchievementCollector _achievements = new(localIp);
    private readonly object _gate = new();

    public string LocalIp { get; } = localIp;
    public RewardCatalog Catalog { get; set; } = catalog;
    public long PacketCount { get; private set; }
    public long InboundCount { get; private set; }
    public long OutboundCount { get; private set; }
    public string? UserUid { get; private set; }
    public string? ServerId { get; private set; }
    public PacketDebugLog? DebugLog { get; set; }

    public IReadOnlyList<AchievementRecord> Achievements
    {
        get { lock (_gate) { return _achievements.Records; } }
    }

    /// <summary>Returns the pages this packet completed.</summary>
    public IReadOnlyList<CapturedPage> Process(IpPacket packet)
    {
        lock (_gate)
        {
            PacketCount++;
            if (packet.DstIp == LocalIp)
            {
                InboundCount++;
            }
            else if (packet.SrcIp == LocalIp)
            {
                OutboundCount++;
            }
            foreach (var candidate in UserUidDecoder.Candidates(packet.Payload))
            {
                _uidCandidates[candidate] = _uidCandidates.GetValueOrDefault(candidate) + 1;
                UserUid = _uidCandidates.MaxBy(entry => entry.Value).Key;
            }
            if (packet.IsTcp)
            {
                if (packet.DstIp == LocalIp)
                {
                    ServerId ??= ServerRegions.ExtractServerId(packet.Payload);
                }
                _achievements.Add(packet);
                return [];
            }
            if (packet.SrcIp == LocalIp && QueueIfRequest(packet))
            {
                return [];
            }
            if (DebugLog is { } unrecognized && packet.SrcIp == LocalIp && MysteryBoxDecoder.LooksLikeRequest(packet.Payload))
            {
                unrecognized.UnrecognizedRequest(packet);
                return [];
            }
            var captured = MatchResponse(packet);
            if (DebugLog is { } log && packet.DstIp == LocalIp && log.FollowsRequest(packet))
            {
                var (rows, error) = DecodeForLog(packet.Payload, captured.Any(page => page.Kind == HistoryKind.MysteryBox));
                log.Inbound(packet, captured, rows, error);
            }
            return captured;
        }
    }

    public IReadOnlyList<HistoryKind> KindsSeen()
    {
        lock (_gate)
        {
            return _pages.Select(page => page.Kind).Distinct().ToList();
        }
    }

    public int PageCount(HistoryKind kind)
    {
        lock (_gate)
        {
            return _pages.Where(page => page.Kind == kind).Select(page => (page.Pool, page.Page)).Distinct().Count();
        }
    }

    public IReadOnlyList<int> MissingPages(HistoryKind kind)
    {
        lock (_gate)
        {
            return _pages.Where(page => page.Kind == kind).GroupBy(page => page.Pool).SelectMany(pool =>
            {
                var pages = pool.Select(page => page.Page).ToHashSet();
                return Enumerable.Range(1, pages.Max()).Where(page => !pages.Contains(page));
            }).Distinct().Order().ToList();
        }
    }

    public IReadOnlyList<PoolHistory> BuildHistories()
    {
        lock (_gate)
        {
            return _pages.Select(page => page.Kind).Distinct()
                .Select(kind => HistoryAssembly.Build(kind, _pages.Where(page => page.Kind == kind).ToList(), Catalog))
                .ToList();
        }
    }

    /// <summary>Tries the data still buffered below the parse threshold.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            _achievements.Finish();
        }
    }

    private bool QueueIfRequest(IpPacket packet)
    {
        var payload = packet.Payload;
        HistoryKind kind;
        int page;
        string? pool = null;
        if (MonopolyDecoder.RequestKind(payload) is { } monopoly)
        {
            (kind, page) = (monopoly, MonopolyDecoder.RequestPage(payload));
        }
        else if (ArcDecoder.IsRequest(payload))
        {
            (kind, page) = (HistoryKind.ArcMiracleBox, ArcDecoder.RequestPage(payload));
        }
        else if (MysteryBoxDecoder.IsRequest(payload))
        {
            (kind, page, pool) = (HistoryKind.MysteryBox, MysteryBoxDecoder.RequestPage(payload), MysteryBoxDecoder.RequestPool(payload));
        }
        else
        {
            return false;
        }

        var request = new PendingRequest(page, kind, packet.SrcIp, packet.DstIp, packet.SrcPort, packet.DstPort, pool);
        // The game pipelines page requests. Only a repeat of the same page, or a
        // fresh scan from page 1, replaces what is already waiting on this stream.
        _pending.RemoveAll(pending =>
            pending.Kind == kind && pending.Pool == pool
            && pending.SrcIp == request.SrcIp && pending.DstIp == request.DstIp
            && pending.SrcPort == request.SrcPort && pending.DstPort == request.DstPort
            && (page == 1 || pending.Page == page));
        _pending.Add(request);
        DebugLog?.Request(packet, kind, page, pool);
        return true;
    }

    /// <summary>Decodes again for the log's row list. This also reaches packets capture ignores, and must not stop it.</summary>
    private (IReadOnlyList<HistoryRow> Rows, string? Error) DecodeForLog(byte[] payload, bool isMysteryBox)
    {
        if (payload.Length < MinResponseLength)
        {
            return ([], null);
        }
        try
        {
            var rows = isMysteryBox ? MysteryBoxDecoder.DecodeResponse(payload, Catalog) : MonopolyDecoder.DecodeResponse(payload, Catalog);
            if (rows.Count == 0)
            {
                rows = ArcDecoder.DecodeResponse(payload, Catalog);
            }
            if (rows.Count == 0)
            {
                rows = MysteryBoxDecoder.DecodeResponse(payload, Catalog);
            }
            return (rows, null);
        }
        catch (Exception exception)
        {
            return ([], exception.Message);
        }
    }

    private List<CapturedPage> MatchResponse(IpPacket packet)
    {
        if (packet.DstIp != LocalIp || packet.Payload.Length < MinResponseLength)
        {
            return [];
        }
        var connection = _pending.Where(request =>
            packet.SrcIp == request.DstIp && packet.DstIp == request.SrcIp
            && packet.SrcPort == request.DstPort && packet.DstPort == request.SrcPort).ToList();
        if (connection.Count == 0)
        {
            return [];
        }

        // A Mystery Box reply names itself, so it goes first when one is awaited rather than relying on other decoders to reject it.
        var mystery = connection.Where(request => request.Kind == HistoryKind.MysteryBox).ToList();
        var records = mystery.Count > 0 ? MysteryBoxDecoder.DecodeResponse(packet.Payload, Catalog) : [];
        List<PendingRequest> candidates;
        if (records.Count > 0)
        {
            var named = mystery.Where(request => string.Equals(request.Pool, records[0].EventId, StringComparison.OrdinalIgnoreCase)).ToList();
            candidates = named.Count > 0 ? named : mystery;
        }
        else if ((records = MonopolyDecoder.DecodeResponse(packet.Payload, Catalog)).Count > 0)
        {
            candidates = connection.Where(request => request.Kind is HistoryKind.Permanent or HistoryKind.LimitedCharacter).ToList();
        }
        else if ((records = ArcDecoder.DecodeResponse(packet.Payload, Catalog)).Count > 0)
        {
            candidates = connection.Where(request => request.Kind == HistoryKind.ArcMiracleBox).ToList();
        }
        else
        {
            return [];
        }
        if (candidates.Count == 0)
        {
            return [];
        }

        // One response can hold several consecutive five-row pages.
        var selected = records.Count < RowsPerPage
            ? [candidates[^1]]
            : candidates.Take(Math.Max(1, (records.Count + RowsPerPage - 1) / RowsPerPage)).ToList();
        var captured = new List<CapturedPage>();
        for (var slice = 0; slice < selected.Count; slice++)
        {
            _pending.Remove(selected[slice]);
            var start = slice * RowsPerPage;
            var count = Math.Min(RowsPerPage, records.Count - start);
            if (count <= 0)
            {
                break;
            }
            captured.Add(new CapturedPage(selected[slice].Page, selected[slice].Kind, packet.Payload, start, count, records[start].EventId ?? selected[slice].Pool));
        }
        _pages.AddRange(captured);
        return captured;
    }
}

/// <summary>Reassembles inbound TCP flows until the login achievement block appears.</summary>
internal sealed class AchievementCollector(string localIp)
{
    private const int MaxFlowBytes = 32 * 1024 * 1024;
    private const int MaxFlows = 64;
    private const int ParseEveryBytes = 128 * 1024;

    private sealed class Flow
    {
        public SortedDictionary<uint, byte[]> Segments { get; } = [];
        public int Bytes { get; set; }
        public int BytesAtLastParse { get; set; }
    }

    private readonly Dictionary<(string, int, string, int), Flow> _flows = [];

    public IReadOnlyList<AchievementRecord> Records { get; private set; } = [];

    public void Add(IpPacket packet)
    {
        if (Records.Count > 0 || packet.Payload.Length == 0 || packet.DstIp != localIp)
        {
            return;
        }
        var key = (packet.SrcIp, packet.SrcPort, packet.DstIp, packet.DstPort);
        if (!_flows.TryGetValue(key, out var flow))
        {
            if (_flows.Count >= MaxFlows)
            {
                return;
            }
            flow = new Flow();
            _flows[key] = flow;
        }
        if (flow.Bytes + packet.Payload.Length > MaxFlowBytes || !flow.Segments.TryAdd(packet.Sequence, packet.Payload))
        {
            return;
        }
        flow.Bytes += packet.Payload.Length;
        if (flow.Bytes - flow.BytesAtLastParse >= ParseEveryBytes)
        {
            TryParse(flow);
        }
    }

    public void Finish()
    {
        foreach (var flow in _flows.Values)
        {
            if (Records.Count > 0)
            {
                break;
            }
            TryParse(flow);
        }
    }

    private void TryParse(Flow flow)
    {
        flow.BytesAtLastParse = flow.Bytes;
        var records = AchievementDecoder.Extract(Reassemble(flow.Segments));
        if (records.Count > 0)
        {
            Records = records;
            _flows.Clear();
        }
    }

    internal static byte[] Reassemble(SortedDictionary<uint, byte[]> segments)
    {
        if (segments.Count == 0)
        {
            return [];
        }
        var start = segments.Keys.First();
        using var output = new MemoryStream();
        foreach (var (sequence, payload) in segments)
        {
            var offset = sequence - start;
            if (offset > output.Length)
            {
                return [];
            }
            var skip = (int)Math.Min(output.Length - offset, payload.Length);
            output.Write(payload, skip, payload.Length - skip);
        }
        return output.ToArray();
    }
}
