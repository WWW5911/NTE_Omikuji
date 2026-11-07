using System.Buffers.Binary;

namespace NteOmikuji.Core.Protocol;

public static class MonopolyDecoder
{
    private const int RequestEvent = 4220;
    private const int RequestLength = 45;

    /// <summary>Saved board rows identify their pull by the time at this scale, so it has to stay for UIDs to keep matching.</summary>
    private const ulong TicksScale = 4;

    public static HistoryKind? RequestKind(ReadOnlySpan<byte> content)
    {
        if (content.Length < RequestLength || BinaryPrimitives.ReadUInt32LittleEndian(content[35..]) != RequestEvent)
        {
            return null;
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(content[40..]) switch
        {
            4 => HistoryKind.Permanent,
            8 => HistoryKind.LimitedCharacter,
            _ => null,
        };
    }

    public static int RequestPage(ReadOnlySpan<byte> content) =>
        (int)(BinaryPrimitives.ReadUInt32LittleEndian(content[31..]) / 4);

    public static List<HistoryRow> DecodeResponse(byte[] response, RewardCatalog catalog)
    {
        var structured = RecordBlockParser.ParseRecords(response, RecordBlockType.Monopoly);
        return structured.Count > 0
            ? RowsFromStructured(structured, catalog)
            : ToRows(AnchoredRows.Board(response), catalog, DecoderModes.Anchored);
    }

    public static List<HistoryRow> RowsFromStructured(IEnumerable<BlockRecord> records, RewardCatalog catalog) =>
        ToRows(records, catalog, DecoderModes.Structured);

    private static List<HistoryRow> ToRows(IEnumerable<BlockRecord> records, RewardCatalog catalog, string mode)
    {
        var rows = new List<HistoryRow>();
        foreach (var record in records)
        {
            var row = new HistoryRow
            {
                Quantity = record.Count,
                ResultText = record.ResultText,
                TimestampRawHex = TicksHex(record.Ticks * TicksScale),
                TimestampTicks = record.Ticks * TicksScale,
                TimestampDecoded = record.TimestampDecoded,
                RecordHex = record.RecordHex,
                DecoderMode = mode,
            };
            ApplyReward(row, record.ItemId, catalog);
            (row.Dice, row.ResultType) = ClassifyRoll(record.RollPointsRaw);
            row.EventId = record.EventId;
            rows.Add(row);
        }
        return rows;
    }

    internal static string TicksHex(ulong ticks)
    {
        Span<byte> raw = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(raw, ticks);
        return Convert.ToHexStringLower(raw);
    }

    internal static void ApplyReward(HistoryRow row, string rewardId, RewardCatalog catalog)
    {
        var meta = catalog.Find(rewardId);
        row.IsMappedReward = meta is not null;
        row.Focus = catalog.FocusOf(rewardId);
        row.RewardId = meta?.Id ?? rewardId;
        row.RewardName = meta?.Name ?? "";
        row.RewardRank = meta?.Rank;
        row.RewardType = meta?.Type ?? RewardCatalog.InferType(rewardId);
    }

    private static (int? Dice, string ResultType) ClassifyRoll(uint? raw) => raw switch
    {
        null => (null, "unknown"),
        0 => (0, "points_gift"),
        uint.MaxValue => (-4, "chase_reward"),
        _ => ((int)raw.Value, "dice"),
    };

    /// <summary>Scaled ticks to a UTC time. Throws when the bytes cannot be a timestamp.</summary>
    internal static (ulong Ticks, string Decoded) DecodeTimestamp(ReadOnlySpan<byte> raw, int scale)
    {
        if (raw.Length != 8)
        {
            throw new InvalidDataException("history timestamps must be exactly 8 bytes");
        }
        var ticks = BinaryPrimitives.ReadUInt64LittleEndian(raw);
        var dotnetTicks = ticks / (ulong)scale;
        if (dotnetTicks > (ulong)DateTime.MaxValue.Ticks || dotnetTicks < (ulong)DateTime.UnixEpoch.Ticks)
        {
            throw new InvalidDataException("history timestamp is out of range");
        }
        return (ticks, new DateTime((long)dotnetTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss"));
    }
}
