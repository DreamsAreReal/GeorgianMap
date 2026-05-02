using System.Buffers.Binary;

namespace GeorgiaPlaces.Application.Places;

/// <summary>
/// Opaque cursor for keyset pagination on <c>places</c> ordered by
/// <c>(data_freshness_score DESC, id DESC)</c>. Encodes the last row's
/// (freshness, id) into base64-url so clients treat it as a black box.
/// </summary>
public static class OpaqueCursor
{
    public static string Encode(double freshness, long id)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteDoubleBigEndian(bytes[..8], freshness);
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], id);
        return Base64UrlEncode(bytes);
    }

    public static bool TryDecode(string cursor, out double freshness, out long id)
    {
        freshness = 0;
        id = 0;
        if (string.IsNullOrEmpty(cursor)) return false;

        try
        {
            var bytes = Base64UrlDecode(cursor);
            if (bytes.Length != 16) return false;
            freshness = BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(0, 8));
            id = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(8, 8));
            // Sanity: freshness in plausible range, id positive.
            if (double.IsNaN(freshness) || freshness < -1.0 || freshness > 1.5 || id <= 0)
            {
                return false;
            }
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        int padLen = (4 - (padded.Length % 4)) % 4;
        if (padLen > 0) padded = padded.PadRight(padded.Length + padLen, '=');
        return Convert.FromBase64String(padded);
    }
}
