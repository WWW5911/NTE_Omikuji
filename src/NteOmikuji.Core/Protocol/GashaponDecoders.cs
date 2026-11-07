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
        var structured = StructuredParser.ParseRecords(response, StructuredRecordType.Fork);
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
            rows.ForEach(row => row.DecoderMode = "heuristic_enriched");
        }
        return rows;
    }

    public static List<HistoryRow> RowsFromStructured(IEnumerable<StructuredRecord> records, RewardCatalog catalog) =>
        records.Select(record =>
        {
            var row = NewRow(record.ItemId, catalog);
            row.TimestampRawHex = MonopolyDecoder.TicksHex(record.Ticks);
            row.TimestampTicks = record.Ticks;
            row.TimestampDecoded = record.TimestampDecoded;
            row.RecordHex = record.RecordHex;
            row.DecoderMode = "structured_fallback";
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
            if (!TryReadPackedField(response, ref position, 4, out var name) || !TryReadPackedField(response, ref position, 8, out _))
            {
                break;
            }
            var timestamp = response.AsSpan(position, 8);
            position += 8;
            var row = NewRow(DecodeKey(name.Span), catalog);
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
    private const int MaxRecords = 100;

    internal static readonly byte[] Marker = "FGashaponLotteryRecordData"u8.ToArray();

    public static bool IsRequest(ReadOnlySpan<byte> content) =>
        content.Length >= RequestLength
        && BinaryPrimitives.ReadUInt32LittleEndian(content[26..]) == 2060
        && BinaryPrimitives.ReadUInt32LittleEndian(content[35..]) == 2110;

    public static int RequestPage(ReadOnlySpan<byte> content) =>
        (int)(BinaryPrimitives.ReadUInt32LittleEndian(content[31..]) / 2);

    public static List<HistoryRow> DecodeResponse(byte[] payload, RewardCatalog catalog)
    {
        foreach (var (_, view) in StructuredParser.ProtocolViews(payload))
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
        var rowCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 8));
        position += 12;
        if (rowCount > MaxRecords)
        {
            return [];
        }

        var rows = new List<HistoryRow>((int)rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            if (position + 4 > data.Length)
            {
                return [];
            }
            var start = position;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            position += 4;
            if (length is 0 or > PackedText.MaxStringLength || position + length + 13 > data.Length)
            {
                return [];
            }
            var raw = data.AsSpan(position, (int)length);
            position += (int)length;
            if (raw[^1] != 0 || PackedText.TryText(raw[..^1]) is not { } rewardId)
            {
                return [];
            }
            var quantity = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            position += 5; // Quantity and the record flag.
            var timestamp = data.AsSpan(position, 8);
            position += 8;

            var row = new HistoryRow
            {
                Quantity = quantity,
                SourceType = "mystery_box",
                ResultType = "single_pull",
                TimestampRawHex = Convert.ToHexStringLower(timestamp),
                RecordHex = Convert.ToHexStringLower(data.AsSpan(start, position - start)),
                DecoderMode = "structured",
            };
            MonopolyDecoder.ApplyReward(row, rewardId, catalog);
            try
            {
                (row.TimestampTicks, row.TimestampDecoded) = MonopolyDecoder.DecodeTimestamp(timestamp, 1);
            }
            catch (InvalidDataException)
            {
                return [];
            }
            rows.Add(row);
        }
        return rows;
    }
}
