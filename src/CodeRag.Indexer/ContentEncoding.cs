using System.Text;

namespace CodeRag.Indexer;

/// <summary>
/// Расшиковывает сырые байты содержимого файла в строку.
/// Порядок: BOM (UTF-8 / UTF-16 LE / BE) → строгий UTF-8 → cp1251 (легаси Windows-кириллица,
/// которой заполнены старые дампы БД). Без cp1251-фолбэка кириллица превращалась в U+FFFD.
/// </summary>
public static class ContentEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Cp1251;

    static ContentEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1251 = Encoding.GetEncoding(1251);
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        int offset = 0;

        // Явные BOM.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // Строгий UTF-8; при невалидной последовательности — cp1251.
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
