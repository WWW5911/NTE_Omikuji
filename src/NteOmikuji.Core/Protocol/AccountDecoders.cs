using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NteOmikuji.Core.Protocol;

public static class UserUidDecoder
{
    private const ulong MinUid = 100_000_000_000;
    private const ulong MaxUid = 999_999_999_999;

    private static readonly (byte[] Marker, int[] Distances)[] Records =
    [
        ("TagOthers"u8.ToArray(), [16]),
        ("PrivateSpawnInfoRecord"u8.ToArray(), [40]),
        ("SimpleQuestRecord"u8.ToArray(), [20, 52]),
        ("FurnitureLayoutDataRec"u8.ToArray(), [56, 64]),
        ("StoreBrandItemSalesVolumeRecord"u8.ToArray(), [16]),
        ("StorePropertyRecord"u8.ToArray(), [20]),
    ];

    public static IEnumerable<string> Candidates(byte[] payload)
    {
        foreach (var (marker, distances) in Records)
        {
            var from = 0;
            while (true)
            {
                var found = payload.AsSpan(from).IndexOf(marker);
                if (found < 0)
                {
                    break;
                }
                var markerAt = from + found;
                foreach (var distance in distances)
                {
                    var offset = markerAt - distance;
                    if (offset >= 0)
                    {
                        var value = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset));
                        if (value is >= MinUid and <= MaxUid)
                        {
                            yield return value.ToString();
                        }
                    }
                }
                from = markerAt + marker.Length;
            }
        }
    }
}

public static class ServerRegions
{
    public static readonly IReadOnlyDictionary<string, (string Region, string Name)> Known = new Dictionary<string, (string, string)>
    {
        ["23001"] = ("AS", "Asia"),
        ["23002"] = ("NA_SA", "America"),
        ["23003"] = ("EU", "Europe"),
        ["23004"] = ("SE", "SEA"),
    };

    public static string? RegionFor(string? serverId) =>
        serverId is not null && Known.TryGetValue(serverId.Trim(), out var server) ? server.Region : null;

    /// <summary>Reads DistrictId from the server-selection response, validating the frame around it.</summary>
    public static string? ExtractServerId(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 168)
        {
            return null;
        }
        var frameSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(payload) + 4;
        if (frameSize > payload.Length || frameSize < 168 || BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) != 20)
        {
            return null;
        }
        var serverId = BinaryPrimitives.ReadUInt32LittleEndian(payload[96..]);
        if (serverId is < 20_000 or > 99_999)
        {
            return null;
        }
        var addressLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[132..]);
        if (addressLength is < 1 or > 255 || 136 + addressLength > frameSize)
        {
            return null;
        }
        var address = payload.Slice(136, (int)addressLength);
        return Ascii.IsValid(address) && IPAddress.TryParse(Encoding.ASCII.GetString(address), out _)
            ? serverId.ToString()
            : null;
    }
}

public static partial class AchievementDecoder
{
    private const int RecordCountOffset = 22;
    private const int RecordsOffset = 38;
    private const int CompletionTicksOffset = 8;
    private const int MaxAchievements = 1_000;
    private const int MaxDecompressedBytes = 128 * 1024 * 1024;

    private static readonly byte[] RecordMarker = "AchievementRecord\0"u8.ToArray();

    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+)\0", RegexOptions.CultureInvariant)]
    private static partial Regex AchievementId();

    public static List<AchievementRecord> Extract(ReadOnlySpan<byte> stream)
    {
        var position = 0;
        while (position + 4 <= stream.Length)
        {
            var frameSize = BinaryPrimitives.ReadUInt32LittleEndian(stream[position..]);
            if (frameSize == 0 || frameSize > stream.Length - position - 4)
            {
                break;
            }
            var frame = stream.Slice(position + 4, (int)frameSize);
            position += 4 + (int)frameSize;
            if (frame.Length < 4)
            {
                break;
            }
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(frame);
            if (headerSize > frame.Length)
            {
                continue;
            }
            var sizeOffset = 12 + (int)headerSize;
            if (sizeOffset + 4 > frame.Length)
            {
                continue;
            }
            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(frame[sizeOffset..]);
            if (dataSize == 0 || dataSize > frame.Length - sizeOffset - 4)
            {
                continue;
            }
            var decoded = DecompressLz4(frame.Slice(sizeOffset + 4, (int)dataSize));
            var marker = decoded.AsSpan().IndexOf(RecordMarker);
            if (marker >= 0 && Records(decoded, marker) is { Count: > 0 } records)
            {
                return records;
            }
        }
        return [];
    }

    private static List<AchievementRecord> Records(byte[] decoded, int marker)
    {
        if (marker + RecordsOffset > decoded.Length)
        {
            return [];
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(marker + RecordCountOffset));
        if (count > MaxAchievements)
        {
            return [];
        }
        // Latin-1 keeps character positions equal to byte positions.
        var text = Encoding.Latin1.GetString(decoded);
        var matches = AchievementId().Matches(text, marker + RecordsOffset);
        if (matches.Count < count)
        {
            return [];
        }
        var records = new List<AchievementRecord>((int)count);
        for (var i = 0; i < count; i++)
        {
            var end = matches[i].Index + matches[i].Length;
            if (end + CompletionTicksOffset + 8 > decoded.Length)
            {
                return [];
            }
            records.Add(new AchievementRecord(
                matches[i].Groups[1].Value,
                BinaryPrimitives.ReadUInt64LittleEndian(decoded.AsSpan(end)),
                BinaryPrimitives.ReadUInt64LittleEndian(decoded.AsSpan(end + CompletionTicksOffset))));
        }
        return records;
    }

    private static byte[] DecompressLz4(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length * 3);
        var position = 0;
        while (position < data.Length)
        {
            if (data.Length - position <= 3 && !data[position..].ContainsAnyExcept((byte)0))
            {
                break;
            }
            var token = data[position++];
            var literalLength = Lz4Length(data, ref position, token >> 4);
            if (literalLength > data.Length - position || output.Count + literalLength > MaxDecompressedBytes)
            {
                return [];
            }
            output.AddRange(data.Slice(position, literalLength));
            position += literalLength;
            if (position == data.Length || (data.Length - position <= 3 && !data[position..].ContainsAnyExcept((byte)0)))
            {
                break;
            }
            if (position + 2 > data.Length)
            {
                return [];
            }
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
            position += 2;
            if (offset == 0 || offset > output.Count)
            {
                return [];
            }
            var matchLength = Lz4Length(data, ref position, token & 15) + 4;
            if (output.Count + matchLength > MaxDecompressedBytes)
            {
                return [];
            }
            for (var i = 0; i < matchLength; i++)
            {
                output.Add(output[^offset]);
            }
        }
        return output.ToArray();
    }

    private static int Lz4Length(ReadOnlySpan<byte> data, ref int position, int length)
    {
        if (length != 15)
        {
            return length;
        }
        while (position < data.Length)
        {
            var value = data[position++];
            length += value;
            if (value != 255 || length > MaxDecompressedBytes)
            {
                break;
            }
        }
        return length;
    }
}
