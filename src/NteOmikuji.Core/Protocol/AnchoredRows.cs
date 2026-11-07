using System.Buffers.Binary;
using System.Text;

namespace NteOmikuji.Core.Protocol;

/// <summary>
/// Reads rows the block parser cannot, as when a game update changes their layout. The pool name is the one field
/// that stands out on its own, so this finds it and works outward: the time follows it, and the row starts at the
/// nearest place before it from which the known fields read through to the pool. This is a best reading, so
/// callers must not let it replace rows they already hold. A page that makes no sense as a whole is left unread.
/// </summary>
public static class AnchoredRows
{
    private const string BoardPoolPrefix = "CardPool_";
    private const string BoardEventPrefix = "Lottery_";
    private const string ArcPoolPrefix = "ForkLottery_";
    private const int MaxTicksGap = 32;
    private const uint MaxRoll = 6;
    private const uint MaxItems = 16;
    private const int BlockHeaderLength = 12;
    private const int RowCountOffset = 8;
    private const int RowsPerPage = 5;

    /// <summary>The points reward comes once in ten pulls, so a five-row page cannot hold many.</summary>
    private const int MaxPointsRewardsPerPage = 2;

    private static readonly ulong MinTicks = (ulong)new DateTime(2020, 1, 1).Ticks;
    private static readonly ulong MaxTicks = (ulong)new DateTime(2040, 1, 1).Ticks;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsRoll(uint value) => value <= MaxRoll || value == uint.MaxValue;

    public static IReadOnlyList<BlockRecord> Board(byte[] payload)
    {
        foreach (var (view, data) in RecordBlockParser.ProtocolViews(payload))
        {
            var pools = Strings(data, BoardPoolPrefix);
            if (pools.Count > 0)
            {
                var rows = BoardRows(data, pools, view);
                var pointsRewards = rows?.Count(row => row.RollPointsRaw == 0) ?? 0;
                var allowed = MaxPointsRewardsPerPage * Math.Max(1, (rows?.Count ?? 0) / RowsPerPage);
                return rows is not null && pointsRewards <= allowed && MakesSenseAsPage(rows, data, RecordBlockParser.MonopolyMarker, pools[0]) ? rows : [];
            }
        }
        return [];
    }

    public static IReadOnlyList<BlockRecord> Arc(byte[] payload)
    {
        foreach (var (view, data) in RecordBlockParser.ProtocolViews(payload))
        {
            var pools = Strings(data, ArcPoolPrefix);
            if (pools.Count > 0)
            {
                var rows = ArcRows(data, pools, view);
                return rows is not null && MakesSenseAsPage(rows, data, RecordBlockParser.ForkMarker, pools[0]) ? rows : [];
            }
        }
        return [];
    }

    private static List<BlockRecord>? BoardRows(byte[] data, List<int> pools, string view)
    {
        var events = Strings(data, BoardEventPrefix);
        var rows = new List<BlockRecord>();
        var regionStart = FirstRowAfter(data, RecordBlockParser.MonopolyMarker, pools[0]);
        for (var index = 0; index < pools.Count; index++)
        {
            var pool = pools[index];
            var poolId = Text(data, pool, out var poolEnd);
            if (FindTicks(data, poolEnd, out var ticksEnd) is not { } ticks || FindBoardRowStart(data, regionStart, pool) is not { } start)
            {
                return null;
            }
            var next = index + 1 < pools.Count ? pools[index + 1] : data.Length;
            var eventAt = events.FindIndex(position => position >= ticksEnd && position < next);
            string? eventId = null;
            regionStart = ticksEnd;
            if (eventAt >= 0)
            {
                eventId = Text(data, events[eventAt], out regionStart);
            }
            var spec = Text(data, start + 4, out _)!;
            var (itemId, count) = RecordBlockParser.ParseItemSpec(spec);
            rows.Add(new BlockRecord(RecordBlockType.Monopoly, itemId, count, ticks, Decoded(ticks), poolId,
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start)), null, null, null,
                Convert.ToHexStringLower(data.AsSpan(start, ticksEnd - start)), view, null, eventId));
        }
        return rows;
    }

    private static List<BlockRecord>? ArcRows(byte[] data, List<int> pools, string view)
    {
        var rows = new List<BlockRecord>();
        var regionStart = FirstRowAfter(data, RecordBlockParser.ForkMarker, pools[0]);
        foreach (var pool in pools)
        {
            var poolId = Text(data, pool, out var poolEnd);
            if (FindTicks(data, poolEnd, out var ticksEnd) is not { } ticks || FindArcItem(data, regionStart, pool) is not { } start)
            {
                return null;
            }
            var (itemId, count) = RecordBlockParser.ParseItemSpec(Text(data, start, out _)!);
            rows.Add(new BlockRecord(RecordBlockType.Fork, itemId, count, ticks, Decoded(ticks), poolId, null, null, null, null,
                Convert.ToHexStringLower(data.AsSpan(start, ticksEnd - start)), view));
            regionStart = ticksEnd;
        }
        return rows;
    }

    /// <summary>An exact fit nearest the pool wins. Failing that, a reward named again among the row's items, and then the first readable start.</summary>
    private static int? FindBoardRowStart(byte[] data, int regionStart, int pool)
    {
        for (var start = pool - 8; start >= regionStart; start--)
        {
            if (IsRoll(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start))) && ReadsThroughToPool(data, start, pool))
            {
                return start;
            }
        }
        int? first = null;
        for (var start = regionStart; start <= pool - 8; start++)
        {
            if (!IsRoll(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start))) || Text(data, start + 4, out var specEnd) is not { } spec || specEnd > pool)
            {
                continue;
            }
            first ??= start;
            var name = Encoding.UTF8.GetBytes(RecordBlockParser.ParseItemSpec(spec).ItemId + "\0");
            if (data.AsSpan(specEnd, pool - specEnd).IndexOf(name) >= 0)
            {
                return start;
            }
        }
        return first;
    }

    private static int? FindArcItem(byte[] data, int regionStart, int pool)
    {
        for (var start = pool - 5; start >= regionStart; start--)
        {
            if (Text(data, start, out var end) is not null && end == pool)
            {
                return start;
            }
        }
        for (var start = regionStart; start <= pool - 5; start++)
        {
            if (Text(data, start, out var end) is not null && end <= pool)
            {
                return start;
            }
        }
        return null;
    }

    /// <summary>Roll, result text, a zero, an item count, the items and optional result text, ending exactly at the pool.</summary>
    private static bool ReadsThroughToPool(byte[] data, int start, int pool)
    {
        if (Text(data, start + 4, out var position) is null || position + 8 > pool || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position)) != 0)
        {
            return false;
        }
        var items = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4));
        if (items > MaxItems)
        {
            return false;
        }
        position += 8;
        for (var item = 0; item < items; item++)
        {
            if (Text(data, position, out position) is null)
            {
                return false;
            }
        }
        return position == pool || (Text(data, position, out position) is not null && position == pool);
    }

    /// <summary>
    /// History runs newest first, and each block states how many rows it holds. One reply can carry several blocks,
    /// and the count only says something when every row sits inside one.
    /// </summary>
    private static bool MakesSenseAsPage(List<BlockRecord> rows, byte[] data, byte[] marker, int firstPool)
    {
        if (!rows.Zip(rows.Skip(1)).All(pair => pair.First.Ticks >= pair.Second.Ticks))
        {
            return false;
        }
        var stated = 0L;
        var searchFrom = 0;
        var firstMarker = -1;
        while (true)
        {
            var found = data.AsSpan(searchFrom).IndexOf(marker);
            if (found < 0)
            {
                break;
            }
            var markerAt = searchFrom + found;
            firstMarker = firstMarker < 0 ? markerAt : firstMarker;
            var header = HeaderAfter(data, marker, markerAt);
            if (header + RowCountOffset + 4 > data.Length)
            {
                return false;
            }
            stated += BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(header + RowCountOffset));
            searchFrom = markerAt + marker.Length;
        }
        return firstMarker < 0 || firstMarker > firstPool || stated == rows.Count;
    }

    private static int FirstRowAfter(byte[] data, byte[] marker, int firstPool)
    {
        var markerAt = data.AsSpan().IndexOf(marker);
        return markerAt < 0 || markerAt > firstPool ? 0 : Math.Min(HeaderAfter(data, marker, markerAt) + BlockHeaderLength, data.Length);
    }

    private static int HeaderAfter(byte[] data, byte[] marker, int markerAt)
    {
        var position = markerAt + marker.Length;
        return position < data.Length && data[position] == 0 ? position + 1 : position;
    }

    private static ulong? FindTicks(byte[] data, int from, out int end)
    {
        end = from;
        for (var gap = 0; gap <= MaxTicksGap && from + gap + 8 <= data.Length; gap++)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(from + gap));
            if (value >= MinTicks && value <= MaxTicks)
            {
                end = from + gap + 8;
                return value;
            }
        }
        return null;
    }

    private static string Decoded(ulong ticks) => new DateTime((long)ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>Where strings starting with this text begin, so the same letters inside other data do not count.</summary>
    private static List<int> Strings(byte[] data, string prefix)
    {
        var found = new List<int>();
        var needle = Encoding.ASCII.GetBytes(prefix);
        var from = 0;
        while (true)
        {
            var at = data.AsSpan(from).IndexOf(needle);
            if (at < 0)
            {
                return found;
            }
            var position = from + at;
            if (position >= 4 && Text(data, position - 4, out _) is not null)
            {
                found.Add(position - 4);
            }
            from = position + needle.Length;
        }
    }

    /// <summary>A NUL-terminated UTF-8 string with its length in front. Null when these bytes are not one.</summary>
    private static string? Text(byte[] data, int position, out int end)
    {
        end = position;
        if (position < 0 || position + 4 > data.Length)
        {
            return null;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
        if (length is <= 1 or > PackedText.MaxStringLength || position + 4 + length > data.Length || data[position + 3 + length] != 0)
        {
            return null;
        }
        string text;
        try
        {
            text = StrictUtf8.GetString(data, position + 4, length - 1);
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (text.Any(char.IsControl))
        {
            return null;
        }
        end = position + 4 + length;
        return text;
    }
}
