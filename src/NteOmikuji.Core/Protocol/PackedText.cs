using System.Buffers.Binary;
using System.Text;

namespace NteOmikuji.Core.Protocol;

/// <summary>
/// History responses are bit-packed: Monopoly text sits two bits into the byte
/// stream and Arc text one bit. What looks like "ASCII * 4 with carry" is that
/// shift, so undoing it recovers any UTF-8 text rather than a known alphabet.
/// </summary>
public static class PackedText
{
    public const int MaxStringLength = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Unshift(ReadOnlySpan<byte> data, int bits)
    {
        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var next = i + 1 < data.Length ? data[i + 1] : 0;
            output[i] = (byte)((data[i] >> bits) | (next << (8 - bits)));
        }
        return output;
    }

    /// <summary>Decodes a packed string with its optional NUL; null when it is not valid text.</summary>
    public static string? Decode(ReadOnlySpan<byte> raw, int bits)
    {
        var plain = Unshift(raw, bits).AsSpan();
        var end = plain.Length;
        while (end > 0 && plain[end - 1] == 0)
        {
            end--;
        }
        return TryText(plain[..end]);
    }

    /// <summary>
    /// Locates the last length-prefixed string in a packed record, whatever it
    /// contains. A record's final string is its result, so this does not depend
    /// on the reward having a known prefix. Returns the string's packed bytes.
    /// </summary>
    public static ReadOnlySpan<byte> FindLastPackedString(ReadOnlySpan<byte> chunk, int bits)
    {
        var plain = Unshift(chunk, bits).AsSpan();
        for (var i = plain.Length - 5; i >= 0; i--)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(plain[i..]);
            if (length is 0 or > MaxStringLength)
            {
                continue;
            }
            var start = i + 4;
            var end = start + (int)length;
            // The terminator can be cut off at the end of the record.
            if (end > plain.Length + 1 || (end <= plain.Length && plain[end - 1] != 0))
            {
                continue;
            }
            if (TryText(plain[start..Math.Min(end - 1, plain.Length)]) is not null)
            {
                return chunk[start..Math.Min(end, chunk.Length)];
            }
        }
        return default;
    }

    internal static string? TryText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }
        return text.Any(char.IsControl) ? null : text;
    }
}
