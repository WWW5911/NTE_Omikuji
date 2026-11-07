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
