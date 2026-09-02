using System.Text;

namespace CodeRag.Git.Helpers;

/// <summary>Расшиковывает сырые байты содержимого файла в строку (BOM → strict UTF-8 → cp1251).</summary>
public static class ContentEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Cp1251;
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly byte[] Utf16LeBom = { 0xFF, 0xFE };
    private static readonly byte[] Utf16BeBom = { 0xFE, 0xFF };

    static ContentEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1251 = Encoding.GetEncoding(1251);
    }

    private static bool HasBomPrefix(byte[] bytes, byte[] bom) => bytes.AsSpan(0, bom.Length).SequenceEqual(bom);

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        int offset = 0;
        if (bytes.Length >= Utf8Bom.Length && HasBomPrefix(bytes, Utf8Bom))
            return StrictUtf8.GetString(bytes, Utf8Bom.Length, bytes.Length - Utf8Bom.Length);
        if (bytes.Length >= Utf16LeBom.Length && HasBomPrefix(bytes, Utf16LeBom))
            return Encoding.Unicode.GetString(bytes, Utf16LeBom.Length, bytes.Length - Utf16LeBom.Length);
        if (bytes.Length >= Utf16BeBom.Length && HasBomPrefix(bytes, Utf16BeBom))
            return Encoding.BigEndianUnicode.GetString(bytes, Utf16BeBom.Length, bytes.Length - Utf16BeBom.Length);
        try
        {
            return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            return Cp1251.GetString(bytes, offset, bytes.Length - offset);
        }
    }
}
