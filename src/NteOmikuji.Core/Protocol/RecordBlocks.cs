using System.Buffers.Binary;
using System.Text;

namespace NteOmikuji.Core.Protocol;

public enum RecordBlockType
{
    Monopoly,
    Fork,
}

public sealed record BlockRecord(
    RecordBlockType Type,
    string ItemId,
    long Count,
    ulong Ticks,
    string TimestampDecoded,
    string? PoolId,
    uint? RollPointsRaw,
    string? SecondaryItemId,
    uint? SecondaryCount,
    string? ResultText,
    string RecordHex,
    string ProtocolView,
    int? SourceIndex = null,
    string? EventId = null);

public sealed record ProtocolEnvelope(string StreamKey, int SegmentIndex);

public sealed record StructuredBlock(IReadOnlyList<BlockRecord> Rows, ProtocolEnvelope? Envelope);

public static class RecordBlockParser
{
    private const int MaxRowsPerBlock = 100;
    private const int MaxShiftedBlockPaddingShortfall = 3;
    private const long MinUnixSeconds = 1_500_000_000;
    private const long MaxUnixSeconds = 4_102_444_800;
    private const uint ProtocolConstant = 0x03000000;
    private const uint MonopolyBlockKind = 527;
    private const uint ForkBlockKind = 5906;
    private const uint MonopolyEnvelopeFooter = 1_774_080;
    private const string BoardEventPrefix = "Lottery_";
    private const string ArcEventPrefix = "ForkLottery_";

    public static readonly byte[] MonopolyMarker = "FMonopolyLotteryRecordData"u8.ToArray();
    public static readonly byte[] ForkMarker = "FForkLotteryRecordData"u8.ToArray();

    public static bool IsEventId(string? text) =>
        text is not null && (text.StartsWith(BoardEventPrefix, StringComparison.Ordinal) || text.StartsWith(ArcEventPrefix, StringComparison.Ordinal));

    public static IReadOnlyList<BlockRecord> ParseRecords(byte[] payload, RecordBlockType type)
    {
        var assembler = new RecordBlockAssembler();
        assembler.AddBlocks(ParseBlocks(payload, type));
        return assembler.Rows();
    }

    public static IReadOnlyList<StructuredBlock> ParseBlocks(byte[] payload, RecordBlockType type, int? sourceIndex = null)
    {
        var marker = type == RecordBlockType.Monopoly ? MonopolyMarker : ForkMarker;
        foreach (var (viewName, data) in ProtocolViews(payload))
        {
            var blocks = new List<StructuredBlock>();
            var searchFrom = 0;
            while (true)
            {
                var offset = data.AsSpan(searchFrom).IndexOf(marker);
                if (offset < 0)
                {
                    break;
                }
                var markerPos = searchFrom + offset;
                try
                {
                    blocks.Add(ParseBlock(data, markerPos, type, marker, viewName, sourceIndex));
                }
                catch (InvalidDataException)
                {
                    // A candidate that fails validation is noise, not a decode error.
                }
                searchFrom = markerPos + marker.Length;
            }
            if (blocks.Count > 0)
            {
                return blocks;
            }
        }
        return [];
    }

    internal static IEnumerable<(string Name, byte[] Data)> ProtocolViews(byte[] payload)
    {
        yield return ("raw", payload);
        if (payload.Length <= 8)
        {
            yield break;
        }
        for (var shift = 1; shift < 8; shift++)
        {
            yield return ($"shift8:{shift}", PackedText.Unshift(payload.AsSpan(8), shift));
        }
    }

    private static StructuredBlock ParseBlock(byte[] data, int markerPos, RecordBlockType type, byte[] marker, string viewName, int? sourceIndex)
    {
        var envelope = ParseEnvelope(type, data, markerPos, viewName);
        var reader = new FieldReader(data, markerPos + marker.Length);
        if (reader.Position < data.Length && data[reader.Position] == 0)
        {
            reader.Position++;
        }
        reader.U32();
        var declaredSize = reader.U32();
        var rowCount = reader.U32();
        if (rowCount > MaxRowsPerBlock)
        {
            throw new InvalidDataException("row count is too large");
        }
        var shortfall = (long)declaredSize - (data.Length - reader.Position);
        if (shortfall > 0 && !(viewName.StartsWith("shift8:", StringComparison.Ordinal) && shortfall <= MaxShiftedBlockPaddingShortfall))
        {
            throw new InvalidDataException("declared block size exceeds payload");
        }

        var rows = new List<BlockRecord>((int)rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            rows.Add(type == RecordBlockType.Monopoly
                ? ParseMonopolyRow(reader, viewName, sourceIndex)
                : ParseForkRow(reader, viewName, sourceIndex));
        }
        return new StructuredBlock(rows, envelope);
    }

    private static BlockRecord ParseMonopolyRow(FieldReader reader, string viewName, int? sourceIndex)
    {
        var start = reader.Position;
        var rollPoints = reader.U32();
        // A layout change can put any number here, and it would otherwise read as a real roll.
        if (!AnchoredRows.IsRoll(rollPoints))
        {
            throw new InvalidDataException("roll is out of range");
        }
        var itemSpec = reader.String();
        reader.U32();
        var secondaryCount = reader.U32();
        var secondaryItem = reader.String();
        var resultOrPool = reader.String();

        var poolPosition = reader.Position;
        var possiblePool = reader.TryString();
        string? poolId;
        if (possiblePool is not null && possiblePool.StartsWith("CardPool_", StringComparison.Ordinal))
        {
            poolId = possiblePool;
        }
        else
        {
            reader.Position = poolPosition;
            poolId = resultOrPool.StartsWith("CardPool_", StringComparison.Ordinal) ? resultOrPool : null;
        }
        var resultText = ReferenceEquals(poolId, resultOrPool) || resultOrPool.Length == 0 ? null : resultOrPool;
        var ticks = reader.U64();
        // Rows end with their event ID. Missing it puts the next row out of position.
        var eventPosition = reader.Position;
        var eventId = reader.TryString();
        if (!IsEventId(eventId))
        {
            eventId = null;
            reader.Position = eventPosition;
        }
        var end = reader.Position;
        reader.Position = eventPosition;
        var record = MakeRecord(reader, RecordBlockType.Monopoly, itemSpec, ticks, poolId, rollPoints,
            secondaryItem.Length == 0 ? null : secondaryItem, secondaryCount, resultText, start, viewName, sourceIndex);
        reader.Position = end;
        return record with { EventId = eventId };
    }

    private static BlockRecord ParseForkRow(FieldReader reader, string viewName, int? sourceIndex)
    {
        var start = reader.Position;
        var itemSpec = reader.String();
        var poolId = reader.String();
        var ticks = reader.U64();
        return MakeRecord(reader, RecordBlockType.Fork, itemSpec, ticks, poolId.Length == 0 ? null : poolId,
            null, null, null, null, start, viewName, sourceIndex);
    }

    private static BlockRecord MakeRecord(FieldReader reader, RecordBlockType type, string itemSpec, ulong ticks, string? poolId,
        uint? rollPoints, string? secondaryItem, uint? secondaryCount, string? resultText, int start, string viewName, int? sourceIndex)
    {
        var (itemId, count) = ParseItemSpec(itemSpec);
        if (itemId.Length == 0)
        {
            throw new InvalidDataException("structured item ID is empty");
        }
        if (ticks > (ulong)DateTime.MaxValue.Ticks)
        {
            throw new InvalidDataException("structured timestamp is out of range");
        }
        var timestamp = new DateTime((long)ticks, DateTimeKind.Utc);
        var unixSeconds = (timestamp - DateTime.UnixEpoch).TotalSeconds;
        if (unixSeconds is < MinUnixSeconds or > MaxUnixSeconds)
        {
            throw new InvalidDataException("structured timestamp is out of range");
        }
        return new BlockRecord(type, itemId, count, ticks, timestamp.ToString("yyyy-MM-dd HH:mm:ss"), poolId, rollPoints,
            secondaryItem, secondaryCount, resultText == itemId ? null : resultText,
            Convert.ToHexStringLower(reader.Data.AsSpan(start, reader.Position - start)), viewName, sourceIndex);
    }

    /// <summary>"id,3" carries a count. Text whose last field is not a positive number is kept whole.</summary>
    internal static (string ItemId, long Count) ParseItemSpec(string value)
    {
        var separator = value.LastIndexOf(',');
        if (separator > 0
            && long.TryParse(value.AsSpan(separator + 1), out var count)
            && count > 0)
        {
            return (value[..separator], count);
        }
        return (value, 1);
    }

    private static ProtocolEnvelope? ParseEnvelope(RecordBlockType type, byte[] data, int markerPos, string viewName)
    {
        if (markerPos == 0 || !viewName.StartsWith("shift8:", StringComparison.Ordinal))
        {
            return null;
        }
        uint Relative(int distance)
        {
            var position = markerPos - distance;
            if (position < 0 || position + 4 > data.Length)
            {
                throw new InvalidDataException("envelope is truncated");
            }
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
        }

        uint queryRaw, pageRaw;
        string streamKey;
        if (type == RecordBlockType.Monopoly)
        {
            queryRaw = Relative(22);
            pageRaw = Relative(18);
            if (Relative(26) != ProtocolConstant || Relative(14) != MonopolyBlockKind || Relative(6) != MonopolyEnvelopeFooter)
            {
                throw new InvalidDataException("invalid monopoly envelope constants");
            }
            streamKey = $"monopoly:{Relative(10)}";
        }
        else
        {
            queryRaw = Relative(13);
            pageRaw = Relative(9);
            if (Relative(17) != ProtocolConstant || Relative(5) != ForkBlockKind)
            {
                throw new InvalidDataException("invalid fork envelope constants");
            }
            streamKey = "fork";
        }

        var pageIndex = (int)(pageRaw & 0x7FFFFFFF);
        var queryHigh = (queryRaw & 0x80000000) != 0;
        if (!queryHigh && pageIndex == 0)
        {
            throw new InvalidDataException("low query cannot describe page zero");
        }
        return new ProtocolEnvelope(streamKey, queryHigh ? pageIndex * 2 : (pageIndex * 2) - 1);
    }

    private sealed class FieldReader(byte[] data, int position)
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public byte[] Data { get; } = data;
        public int Position { get; set; } = position;

        public uint U32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Position));
            Position += 4;
            return value;
        }

        public ulong U64()
        {
            Require(8);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(Data.AsSpan(Position));
            Position += 8;
            return value;
        }

        /// <summary>
        /// Reads an Unreal FString: a positive length means UTF-8, a negative one
        /// UTF-16, and zero an empty string. Any text survives, not only identifiers.
        /// </summary>
        public string String()
        {
            var length = (int)U32();
            if (length == 0)
            {
                return "";
            }
            var units = Math.Abs((long)length);
            if (units > PackedText.MaxStringLength)
            {
                throw new InvalidDataException("invalid string length");
            }
            var byteCount = (int)units * (length < 0 ? 2 : 1);
            Require(byteCount);
            var raw = Data.AsSpan(Position, byteCount);
            Position += byteCount;
            try
            {
                if (length < 0)
                {
                    return Encoding.Unicode.GetString(raw).TrimEnd('\0');
                }
                return StrictUtf8.GetString(raw[^1] == 0 ? raw[..^1] : raw);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("string is not valid text", exception);
            }
        }

        public string? TryString()
        {
            var start = Position;
            try
            {
                return String();
            }
            catch (InvalidDataException)
            {
                Position = start;
                return null;
            }
        }

        private void Require(int count)
        {
            if (count > Data.Length - Position)
            {
                throw new InvalidDataException("field exceeds payload");
            }
        }
    }
}

/// <summary>Assembles retransmitted and overlapping structured snapshot segments.</summary>
public sealed class RecordBlockAssembler
{
    private const string LegacyStream = "__legacy__";

    private sealed class Generation
    {
        public SortedDictionary<int, StructuredBlock> Segments { get; } = [];
    }

    private readonly List<string> _streamOrder = [];
    private readonly Dictionary<string, List<Generation>> _streams = [];
    private readonly List<BlockRecord> _legacyRows = [];

    public int WarningCount { get; private set; }

    public void AddBlocks(IEnumerable<StructuredBlock> blocks)
    {
        foreach (var block in blocks)
        {
            AddBlock(block);
        }
    }

    private void AddBlock(StructuredBlock block)
    {
        if (block.Envelope is not { } envelope)
        {
            if (_legacyRows.Count == 0)
            {
                _streamOrder.Add(LegacyStream);
            }
            _legacyRows.AddRange(block.Rows);
            return;
        }

        if (!_streams.TryGetValue(envelope.StreamKey, out var generations))
        {
            generations = [new Generation()];
            _streams[envelope.StreamKey] = generations;
            _streamOrder.Add(envelope.StreamKey);
        }
        var generation = generations[^1];
        if (generation.Segments.TryGetValue(envelope.SegmentIndex, out var existing))
        {
            if (existing.Rows.Select(Signature).SequenceEqual(block.Rows.Select(Signature)))
            {
                return;
            }
            generation = new Generation();
            generations.Add(generation);
        }
        generation.Segments[envelope.SegmentIndex] = block;
    }

    public IReadOnlyList<BlockRecord> Rows()
    {
        WarningCount = 0;
        var rows = new List<BlockRecord>();
        foreach (var key in _streamOrder)
        {
            rows.AddRange(key == LegacyStream ? _legacyRows : AssembleStream(_streams[key]));
        }
        return rows;
    }

    private List<BlockRecord> AssembleStream(List<Generation> generations)
    {
        var result = new List<BlockRecord>();
        int? resultMaxSegment = null;
        foreach (var generation in generations.Where(g => g.Segments.Count > 0))
        {
            var rows = generation.Segments.Values.SelectMany(block => block.Rows).ToList();
            var min = generation.Segments.Keys.First();
            var max = generation.Segments.Keys.Last();
            if (result.Count == 0)
            {
                result = rows;
                resultMaxSegment = max;
            }
            else if (min == 0)
            {
                if (resultMaxSegment is null || max >= resultMaxSegment)
                {
                    result = rows;
                    resultMaxSegment = max;
                }
                else if (MergePartialSnapshot(rows, result) is { } merged)
                {
                    result = merged;
                }
                else
                {
                    WarningCount++;
                }
            }
            else if (resultMaxSegment is not null && min > resultMaxSegment)
            {
                result.AddRange(rows);
                resultMaxSegment = max;
            }
            else
            {
                WarningCount++;
            }
        }
        return result;
    }

    private static List<BlockRecord>? MergePartialSnapshot(List<BlockRecord> newRows, List<BlockRecord> oldRows)
    {
        var newSignatures = newRows.Select(Signature).ToList();
        var oldSignatures = oldRows.Select(Signature).ToList();
        for (var overlap = Math.Min(newRows.Count, oldRows.Count); overlap > 0; overlap--)
        {
            var suffix = newSignatures.Skip(newSignatures.Count - overlap).ToList();
            var matches = Enumerable.Range(0, oldSignatures.Count - overlap + 1)
                .Where(position => oldSignatures.Skip(position).Take(overlap).SequenceEqual(suffix))
                .ToList();
            if (matches.Count > 0)
            {
                // Merging is only safe when the overlap has exactly one placement.
                return matches.Count == 1 ? [.. newRows, .. oldRows.Skip(matches[0] + overlap)] : null;
            }
        }
        return null;
    }

    private static (RecordBlockType, ulong, string?, string, long, uint?, string?, uint?) Signature(BlockRecord row) =>
        (row.Type, row.Ticks, row.PoolId, row.ItemId, row.Count, row.RollPointsRaw, row.SecondaryItemId, row.SecondaryCount);
}
