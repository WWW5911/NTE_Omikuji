using System.Buffers.Binary;

namespace NteOmikuji.Core.Protocol;

public static class ArcDecoder
{
    private const int RequestConstant = 2060;
    private const int RequestLength = 34;
    private const int CursorOffset = 29;
    private const int FirstRecordOffset = 0x4C;
    private const int TicksScale = 2;
    private const int TextShiftBits = 1;
    private const string UnknownName = "UNKNOWN";

    public static bool IsRequest(ReadOnlySpan<byte> content) =>
        content.Length >= RequestLength && BinaryPrimitives.ReadUInt32LittleEndian(content[24..]) == RequestConstant;

    public static int RequestPage(ReadOnlySpan<byte> content) =>
        (int)(BinaryPrimitives.ReadUInt32LittleEndian(content[CursorOffset..]) / 2);

    public static string DecodeKey(ReadOnlySpan<byte> raw) =>
        PackedText.Decode(raw, TextShiftBits) ?? Convert.ToHexStringLower(raw);

    public static List<HistoryRow> DecodeResponse(byte[] response, RewardCatalog catalog)
    {
        var structured = RecordBlockParser.ParseRecords(response, RecordBlockType.Fork);
        var rows = ParseLegacy(response, catalog);
        if (rows.Count == 0)
        {
            return RowsFromStructured(structured, catalog);
        }
        var compatible = rows.Count == structured.Count && rows.Zip(structured).All(pair =>
            pair.First.RewardId.Equals(pair.Second.ItemId, StringComparison.OrdinalIgnoreCase)
            && (pair.First.TimestampTicks == pair.Second.Ticks || pair.First.TimestampTicks == pair.Second.Ticks * TicksScale));
        if (compatible)
        {
            rows.ForEach(row => row.DecoderMode = "scan_checked");
        }
        return rows;
    }

    public static List<HistoryRow> RowsFromStructured(IEnumerable<BlockRecord> records, RewardCatalog catalog) =>
        records.Select(record =>
        {
            var row = NewRow(record.ItemId, catalog);
            row.TimestampRawHex = MonopolyDecoder.TicksHex(record.Ticks);
            row.TimestampTicks = record.Ticks;
            row.TimestampDecoded = record.TimestampDecoded;
            row.RecordHex = record.RecordHex;
            row.DecoderMode = "block_fallback";
            row.BannerId = RecordBlockParser.IsEventBanner(record.PoolId) ? record.PoolId : null;
            return row;
        }).ToList();

    private static HistoryRow NewRow(string rewardId, RewardCatalog catalog)
    {
        var meta = catalog.Find(rewardId);
        return new HistoryRow
        {
            RewardType = meta?.Type ?? "arc",
            RewardId = meta?.Id ?? rewardId,
            RewardName = meta?.Name ?? UnknownName,
            RewardRank = meta?.Rank ?? "",
            IsMappedReward = meta is not null,
            Focus = catalog.FocusOf(rewardId),
            SourceType = "miracle_box",
            ResultType = "",
        };
    }

    private static List<HistoryRow> ParseLegacy(byte[] response, RewardCatalog catalog)
    {
        var rows = new List<HistoryRow>();
        var position = FirstRecordOffset;
        while (position + 4 <= response.Length)
        {
            var start = position;
            if (!TryReadPackedField(response, ref position, 4, out var name) || !TryReadPackedField(response, ref position, 8, out var pool))
            {
                break;
            }
            var timestamp = response.AsSpan(position, 8);
            position += 8;
            var row = NewRow(DecodeKey(name.Span), catalog);
            var banner = DecodeKey(pool.Span);
            row.BannerId = RecordBlockParser.IsEventBanner(banner) ? banner : null;
            try
            {
                (row.TimestampTicks, row.TimestampDecoded) = MonopolyDecoder.DecodeTimestamp(timestamp, TicksScale);
            }
            catch (InvalidDataException)
            {
                return [];
            }
            row.TimestampRawHex = Convert.ToHexStringLower(timestamp);
            row.RecordHex = Convert.ToHexStringLower(response.AsSpan(start, position - start));
            rows.Add(row);
        }
        return rows;
    }

    private static bool TryReadPackedField(byte[] response, ref int position, int trailingBytes, out ReadOnlyMemory<byte> field)
    {
        field = default;
        if (position + 4 > response.Length)
        {
            return false;
        }
        var doubled = BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(position));
        if (doubled is 0 or > 200 || doubled % 2 != 0)
        {
            return false;
        }
        var length = (int)doubled / 2;
        if (position + 4 + length + trailingBytes > response.Length)
        {
            return false;
        }
        field = response.AsMemory(position + 4, length);
        position += 4 + length;
        return true;
    }
}

public static class MysteryBoxDecoder
{
    private const int RequestLength = 54;
    private const int RequestConstantAt = 26;
    private const uint RequestConstant = 2060;
    private const int RequestKindAt = 35;
    private const uint LegacyRequestKind = 2110;
    private const byte PoolRequestTag = 0x1E;
    private const int PoolLengthAt = 40;
    private const uint PoolRequestPageSize = 10;
    private const int MaxPoolIdLength = 64;
    private const int MaxRecords = 100;

    internal static readonly byte[] Marker = "FGashaponLotteryRecordData"u8.ToArray();
    private static readonly byte[] PoolPrefix = "MangHe"u8.ToArray();
    private static readonly byte[] DoubledPoolPrefix = PoolPrefix.Select(value => (byte)(value * 2)).ToArray();

    /// <summary>History requests share this value, whatever their layout, so a future layout can still be noticed.</summary>
    public static bool HasRequestConstant(ReadOnlySpan<byte> content) =>
        content.Length >= RequestLength && BinaryPrimitives.ReadUInt32LittleEndian(content[RequestConstantAt..]) == RequestConstant;

    /// <summary>For the debug log, so a layout this decoder does not know yet still leaves its bytes behind.</summary>
    public static bool LooksLikeRequest(ReadOnlySpan<byte> content) =>
        HasRequestConstant(content) || content.IndexOf(DoubledPoolPrefix) >= 0 || content.IndexOf(PoolPrefix) >= 0;

    public static bool IsRequest(ReadOnlySpan<byte> content) =>
        HasRequestConstant(content)
        && (BinaryPrimitives.ReadUInt32LittleEndian(content[RequestKindAt..]) == LegacyRequestKind || RequestPool(content) is not null);

    public static int RequestPage(ReadOnlySpan<byte> content) =>
        (int)(BinaryPrimitives.ReadUInt32LittleEndian(content[31..]) / 2);

    /// <summary>Since version 1.4 a request names its pool, in text doubled the way Arc text is. Null for the older layout.</summary>
    public static string? RequestPool(ReadOnlySpan<byte> content)
    {
        if (content.Length < RequestLength || content[RequestKindAt] != PoolRequestTag)
        {
            return null;
        }
        var doubled = BinaryPrimitives.ReadUInt32LittleEndian(content[PoolLengthAt..]);
        if (doubled % 2 != 0 || doubled is < 2 or > MaxPoolIdLength * 2)
        {
            return null;
        }
        var length = (int)doubled / 2;
        var trailer = PoolLengthAt + 4 + length;
        if (trailer + 9 > content.Length || content[trailer - 1] != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(content[trailer..]) != RequestConstant
            || BinaryPrimitives.ReadUInt32LittleEndian(content[(trailer + 5)..]) != PoolRequestPageSize)
        {
            return null;
        }
        var id = new char[length - 1];
        for (var i = 0; i < id.Length; i++)
        {
            id[i] = (char)(content[PoolLengthAt + 4 + i] / 2);
        }
        return new string(id);
    }

    public static List<HistoryRow> DecodeResponse(byte[] payload, RewardCatalog catalog)
    {
        foreach (var (_, view) in RecordBlockParser.ProtocolViews(payload))
        {
            var rows = ParseView(view, catalog);
            if (rows.Count > 0)
            {
                return rows;
            }
        }
        return [];
    }

    private static List<HistoryRow> ParseView(byte[] data, RewardCatalog catalog)
    {
        var markerAt = data.AsSpan().IndexOf(Marker);
        if (markerAt < 0)
        {
            return [];
        }
        var position = markerAt + Marker.Length;
        if (position < data.Length && data[position] == 0)
        {
            position++;
        }
        if (position + 12 > data.Length)
        {
            return [];
        }
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
        var rowCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 8));
        position += 12;
        if (rowCount > MaxRecords)
        {
            return [];
        }

        // Only the newer layout's declared size covers its rows and the count, which tells the two apart.
        var pooled = ParseRows(data, position, (int)rowCount, catalog, withPool: true, out var end);
        return pooled is not null && end - position + 4 == declaredSize
            ? pooled
            : ParseRows(data, position, (int)rowCount, catalog, withPool: false, out _) ?? [];
    }

    private static List<HistoryRow>? ParseRows(byte[] data, int position, int rowCount, RewardCatalog catalog, bool withPool, out int end)
    {
        end = position;
        var rows = new List<HistoryRow>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            if (position + 4 > data.Length)
            {
                return null;
            }
            var start = position;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            position += 4;
            if (length is 0 or > PackedText.MaxStringLength || position + length + 13 > data.Length)
            {
                return null;
            }
            var raw = data.AsSpan(position, (int)length);
            position += (int)length;
            if (raw[^1] != 0 || PackedText.TryText(raw[..^1]) is not { } rewardId)
            {
                return null;
            }
            var quantity = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            position += 5; // Quantity and the record flag.
            var timestamp = data.AsSpan(position, 8);
            position += 8;
            string? pool = null;
            if (withPool)
            {
                if (position + 4 > data.Length)
                {
                    return null;
                }
                var poolLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
                position += 4;
                // The pool is followed by a value whose meaning is unknown.
                if (poolLength is 0 or > MaxPoolIdLength || position + poolLength + 4 > data.Length || data[position + (int)poolLength - 1] != 0)
                {
                    return null;
                }
                pool = PackedText.TryText(data.AsSpan(position, (int)poolLength - 1));
                if (pool is null)
                {
                    return null;
                }
                position += (int)poolLength + 4;
            }

            var row = new HistoryRow
            {
                Quantity = quantity,
                SourceType = "mystery_box",
                ResultType = "single_pull",
                TimestampRawHex = Convert.ToHexStringLower(timestamp),
                RecordHex = Convert.ToHexStringLower(data.AsSpan(start, position - start)),
                DecoderMode = "block",
                BannerId = pool,
            };
            MonopolyDecoder.ApplyReward(row, rewardId, catalog);
            try
            {
                (row.TimestampTicks, row.TimestampDecoded) = MonopolyDecoder.DecodeTimestamp(timestamp, 1);
            }
            catch (InvalidDataException)
            {
                return null;
            }
            rows.Add(row);
        }
        end = position;
        return rows;
    }
}
