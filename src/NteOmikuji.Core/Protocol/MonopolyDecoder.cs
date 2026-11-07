using System.Buffers.Binary;

namespace NteOmikuji.Core.Protocol;

public static class MonopolyDecoder
{
    private const int RequestBanner = 4220;
    private const int RequestLength = 45;
    private const int FirstRecordOffset = 0x50;
    private const int TicksScale = 4;
    private const int TextShiftBits = 2;

    internal static readonly byte[] PermanentMarker = Convert.FromHexString("440000000c85c99141bdbdb17d3995dd49bdb19501");
    internal static readonly byte[] LimitedMarker = Convert.FromHexString("4c0000000c85c99141bdbdb17d0da185c9858dd195c901");

    private static readonly byte[][] Markers = [PermanentMarker, LimitedMarker];
    private static readonly HashSet<uint> ValidDiceFields = [0, 4, 8, 12, 16, 20, 24];
    private static readonly byte[] ChasePattern = Convert.FromHexString("c4b0ccc00000000000040000003c00000010a58d957dd1a58dad95d17dc1c400");
    private static readonly byte[] FashionPrefix = Convert.FromHexString("1885cda1a5bdb97d");
    private static readonly byte[] CharacterPrefix = Convert.FromHexString("c4c0");
    private static readonly byte[][] ItemPrefixes = [Convert.FromHexString("98bdc9ad"), Convert.FromHexString("10a58d95")];

    public static HistoryKind? RequestKind(ReadOnlySpan<byte> content)
    {
        if (content.Length < RequestLength || BinaryPrimitives.ReadUInt32LittleEndian(content[35..]) != RequestBanner)
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

    public static string DecodeRewardKey(ReadOnlySpan<byte> raw) => PackedText.Decode(raw, TextShiftBits) ?? "";

    public static bool ContainsMarker(byte[] content) =>
        Alignments(content).Any(view => Markers.Any(marker => view.AsSpan().IndexOf(marker) >= 0));

    public static List<HistoryRow> DecodeResponse(byte[] response, RewardCatalog catalog)
    {
        var structured = StructuredParser.ParseRecords(response, StructuredRecordType.Monopoly);
        foreach (var view in Alignments(response))
        {
            if (!Markers.Any(marker => view.AsSpan().IndexOf(marker) >= 0))
            {
                continue;
            }
            List<HistoryRow> rows;
            try
            {
                rows = DecodeAligned(view, catalog);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (rows.Count > 0)
            {
                Enrich(rows, structured, catalog);
                return rows;
            }
        }
        return RowsFromStructured(structured, catalog);
    }

    public static List<HistoryRow> RowsFromStructured(IEnumerable<StructuredRecord> records, RewardCatalog catalog)
    {
        var rows = new List<HistoryRow>();
        foreach (var record in records)
        {
            var row = new HistoryRow
            {
                Quantity = record.Count,
                ResultText = record.ResultText,
                TimestampRawHex = TicksHex(record.Ticks),
                TimestampTicks = record.Ticks,
                TimestampDecoded = record.TimestampDecoded,
                RecordHex = record.RecordHex,
                DecoderMode = "structured_fallback",
            };
            ApplyReward(row, record.ItemId, catalog);
            (row.Dice, row.ResultType) = ClassifyStructured(record.RollPointsRaw);
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

    private static (int? Dice, string ResultType) ClassifyStructured(uint? raw) => raw switch
    {
        null => (null, "unknown"),
        0 => (0, "points_gift"),
        uint.MaxValue => (-4, "chase_reward"),
        _ => ((int)raw.Value, "dice"),
    };

    private static IEnumerable<byte[]> Alignments(byte[] content)
    {
        yield return content;
        if (content.Length == 0)
        {
            yield break;
        }
        for (var bits = 1; bits < 8; bits++)
        {
            yield return PackedText.Unshift(content, bits)[..^1];
        }
    }

    private static List<HistoryRow> DecodeAligned(byte[] response, RewardCatalog catalog)
    {
        byte[] marker = [];
        var markerOffsets = new List<int>();
        foreach (var candidate in Markers)
        {
            for (var at = response.AsSpan().IndexOf(candidate); at >= 0;)
            {
                markerOffsets.Add(at);
                var next = response.AsSpan(at + 1).IndexOf(candidate);
                at = next < 0 ? -1 : at + 1 + next;
            }
            if (markerOffsets.Count > 0)
            {
                marker = candidate;
                break;
            }
        }

        var rows = new List<HistoryRow>();
        var previous = FirstRecordOffset;
        foreach (var markerOffset in markerOffsets)
        {
            var recordEnd = Math.Min(markerOffset + marker.Length + 8, response.Length);
            var chunk = previous < markerOffset ? response.AsSpan(previous, markerOffset - previous) : default;
            var recordStart = previous;
            var (dice, diceOffset) = ExtractDice(chunk);
            var originalKey = ExtractKey(chunk);
            var keyPosition = originalKey.IsEmpty ? chunk.Length : chunk.IndexOf(originalKey);
            if ((dice is null || (PageFirstDice(chunk) is null && keyPosition > 32)) && chunk.Length > 32)
            {
                if (FindEmbeddedRecord(chunk, originalKey, dice is null) is { } trim)
                {
                    chunk = chunk[trim..];
                    recordStart = previous + trim;
                    (dice, diceOffset) = ExtractDice(chunk);
                }
            }

            var key = ExtractKey(chunk);
            var rewardId = key.IsEmpty ? "" : DecodeRewardKey(key);
            var resultType = ClassifyResult(chunk, dice, diceOffset, rewardId);
            if (resultType == "points_gift")
            {
                dice = 0;
            }
            else if (resultType == "chase_reward")
            {
                dice = -4;
            }

            var timestamp = response.AsSpan(markerOffset + marker.Length, recordEnd - markerOffset - marker.Length);
            var (ticks, decoded) = DecodeTimestamp(timestamp, TicksScale);
            var row = new HistoryRow
            {
                Dice = dice,
                ResultType = resultType,
                Quantity = GuessQuantity(Convert.ToHexStringLower(chunk), rewardId, resultType),
                TimestampRawHex = Convert.ToHexStringLower(timestamp),
                TimestampTicks = ticks,
                TimestampDecoded = decoded,
                RecordHex = recordStart <= recordEnd ? Convert.ToHexStringLower(response.AsSpan(recordStart, recordEnd - recordStart)) : "",
            };
            ApplyReward(row, rewardId, catalog);
            rows.Add(row);
            previous = markerOffset + marker.Length + 8;
        }
        return rows;
    }

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

    private static int? FindEmbeddedRecord(ReadOnlySpan<byte> chunk, ReadOnlySpan<byte> originalKey, bool diceMissing)
    {
        (int KeyCount, bool NotPageFirst, bool NotOffsetFive, int KeyPosition, int Trim)? best = null;
        for (var trim = 1; trim < Math.Min(96, chunk.Length); trim++)
        {
            var candidate = chunk[trim..];
            var (dice, offset) = ExtractDice(candidate);
            var isPageFirst = PageFirstDice(candidate) is not null;
            if (dice is null || offset is not (0 or 5 or 9) || !(diceMissing || isPageFirst) || !ExtractKey(candidate).SequenceEqual(originalKey))
            {
                continue;
            }
            var score = (
                KeyCount: originalKey.IsEmpty ? 0 : -candidate.Count(originalKey),
                NotPageFirst: !isPageFirst,
                NotOffsetFive: offset != 5,
                KeyPosition: originalKey.IsEmpty ? candidate.Length : candidate.IndexOf(originalKey),
                Trim: trim);
            if (best is null || IsBetter(score, best.Value))
            {
                best = score;
            }
        }
        return best?.Trim;
    }

    // The widest trim wins the final tie.
    private static bool IsBetter(
        (int KeyCount, bool NotPageFirst, bool NotOffsetFive, int KeyPosition, int Trim) a,
        (int KeyCount, bool NotPageFirst, bool NotOffsetFive, int KeyPosition, int Trim) b)
    {
        var order = a.KeyCount.CompareTo(b.KeyCount);
        if (order == 0) { order = a.NotPageFirst.CompareTo(b.NotPageFirst); }
        if (order == 0) { order = a.NotOffsetFive.CompareTo(b.NotOffsetFive); }
        if (order == 0) { order = a.KeyPosition.CompareTo(b.KeyPosition); }
        if (order == 0) { order = b.Trim.CompareTo(a.Trim); }
        return order < 0;
    }

    /// <summary>
    /// The length-prefixed string decides first because it holds for any reward.
    /// The known prefixes only rescue records whose string header was damaged.
    /// </summary>
    private static ReadOnlySpan<byte> ExtractKey(ReadOnlySpan<byte> chunk)
    {
        var packed = PackedText.FindLastPackedString(chunk, TextShiftBits);
        if (!packed.IsEmpty)
        {
            return packed;
        }

        var fashion = chunk.LastIndexOf(FashionPrefix);
        if (fashion != -1)
        {
            return chunk[fashion..];
        }
        var character = chunk.IndexOf(CharacterPrefix);
        if (character != -1 && character + 5 <= chunk.Length)
        {
            return chunk.Slice(character, 5);
        }
        var best = -1;
        foreach (var prefix in ItemPrefixes)
        {
            best = Math.Max(best, chunk.LastIndexOf(prefix));
        }
        return best == -1 ? default : chunk[best..];
    }

    private static uint? PageFirstDice(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length >= 17 && chunk[0] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(chunk[5..]) == 20)
        {
            var dice = BinaryPrimitives.ReadUInt32LittleEndian(chunk[9..]);
            if (ValidDiceFields.Contains(dice))
            {
                return dice;
            }
        }
        return null;
    }

    private static (int? Dice, int? Offset) ExtractDice(ReadOnlySpan<byte> chunk)
    {
        if (PageFirstDice(chunk) is { } prefixed)
        {
            return ((int)prefixed / 4, 9);
        }
        for (var offset = 0; offset < Math.Min(16, Math.Max(0, chunk.Length - 3)); offset++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(chunk[offset..]);
            if (ValidDiceFields.Contains(value))
            {
                return ((int)value / 4, offset);
            }
        }
        return (null, null);
    }

    private static string ClassifyResult(ReadOnlySpan<byte> chunk, int? dice, int? diceOffset, string rewardId)
    {
        if (dice is null || diceOffset is null)
        {
            return "unknown";
        }
        if (rewardId.Equals("dice_ticket_01", StringComparison.OrdinalIgnoreCase) && chunk.IndexOf(ChasePattern) >= 0)
        {
            return "chase_reward";
        }
        if (dice == 0)
        {
            return "points_gift";
        }
        var source = diceOffset.Value + 4;
        if (source + 4 <= chunk.Length)
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(chunk[source..]);
            return value switch
            {
                0 => "points_gift",
                -4 => "chase_reward",
                _ => "dice",
            };
        }
        return "dice";
    }

    private static long? GuessQuantity(string chunkHex, string rewardId, string resultType)
    {
        if (rewardId.Equals("dice_ticket_01", StringComparison.OrdinalIgnoreCase))
        {
            return resultType == "chase_reward" ? 30 : 4;
        }
        if (rewardId.Equals("dice_ticket_02", StringComparison.OrdinalIgnoreCase))
        {
            return chunkHex.Contains("c8b0d4c0", StringComparison.Ordinal) ? 50
                : chunkHex.Contains("c8b0ccc0", StringComparison.Ordinal) ? 30
                : null;
        }
        return rewardId.Length > 0 ? 1 : null;
    }

    private static void Enrich(List<HistoryRow> rows, IReadOnlyList<StructuredRecord> structured, RewardCatalog catalog)
    {
        if (rows.Count != structured.Count)
        {
            return;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var id = rows[i].RewardId;
            if (id.Length > 0 && !id.Equals(structured[i].ItemId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (rows[i].TimestampTicks != structured[i].Ticks && rows[i].TimestampTicks != structured[i].Ticks * TicksScale)
            {
                return;
            }
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var (row, record) = (rows[i], structured[i]);
            if (row.RewardId.Length == 0)
            {
                ApplyReward(row, record.ItemId, catalog);
            }
            row.Quantity = record.Count;
            row.ResultText = record.ResultText;
            // The parsed field is the real roll. The scan above can land on any zero byte near a record.
            if (record.RollPointsRaw is not null || row.ResultType == "unknown" || row.Dice is null)
            {
                (row.Dice, row.ResultType) = ClassifyStructured(record.RollPointsRaw);
            }
            row.DecoderMode = "heuristic_enriched";
        }
    }
}
