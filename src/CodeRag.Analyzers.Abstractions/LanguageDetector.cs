using System.Text;

namespace CodeRag.Analyzers.Abstractions;

/// <summary>
/// Детектор языка по расширению + shebang (first line).
/// Используется как fallback и для CanAnalyze; точный выбор — через ICodeAnalyzer.CanAnalyze в DI.
/// </summary>
public static class LanguageDetector
{
    private static readonly Dictionary<string, string> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // C#
        [".cs"] = SupportedLanguages.CSharp,
        [".csx"] = SupportedLanguages.CSharp,
        // Python
        [".py"] = SupportedLanguages.Python,
        [".pyw"] = SupportedLanguages.Python,
        [".pyi"] = SupportedLanguages.Python,
        // SQL
        [".sql"] = SupportedLanguages.Sql,
        [".tsql"] = SupportedLanguages.Sql,
        // VBS / VBScript
        [".vbs"] = SupportedLanguages.Vbs,
        [".vb"] = SupportedLanguages.Vbs,
        [".vba"] = SupportedLanguages.Vbs,
        [".bas"] = SupportedLanguages.Vbs,
        // C / C++
        [".c"] = SupportedLanguages.C,
        [".h"] = SupportedLanguages.C,
        [".cpp"] = SupportedLanguages.Cpp,
        [".cc"] = SupportedLanguages.Cpp,
        [".cxx"] = SupportedLanguages.Cpp,
        [".hpp"] = SupportedLanguages.Cpp,
        [".hh"] = SupportedLanguages.Cpp,
        [".hxx"] = SupportedLanguages.Cpp,
        // Java
        [".java"] = SupportedLanguages.Java,
        // Rust
        [".rs"] = SupportedLanguages.Rust,
        // Ruby
        [".rb"] = SupportedLanguages.Ruby,
        [".rake"] = SupportedLanguages.Ruby,
        [".gemspec"] = SupportedLanguages.Ruby,
        // PHP
        [".php"] = SupportedLanguages.Php,
        [".phtml"] = SupportedLanguages.Php,
        [".php3"] = SupportedLanguages.Php,
        [".php4"] = SupportedLanguages.Php,
        [".php5"] = SupportedLanguages.Php,
        // Bash
        [".sh"] = SupportedLanguages.Bash,
        [".bash"] = SupportedLanguages.Bash,
        [".zsh"] = SupportedLanguages.Bash,
        // JSON
        [".json"] = SupportedLanguages.Json,
        [".jsonc"] = SupportedLanguages.Json,
        // JavaScript / TypeScript (TS маппится в javascript для MVP, §13)
        [".js"] = SupportedLanguages.JavaScript,
        [".mjs"] = SupportedLanguages.JavaScript,
        [".cjs"] = SupportedLanguages.JavaScript,
        [".jsx"] = SupportedLanguages.JavaScript,
        [".ts"] = SupportedLanguages.JavaScript,
        [".tsx"] = SupportedLanguages.JavaScript,
        // Go
        [".go"] = SupportedLanguages.Go,
        // HTML
        [".html"] = SupportedLanguages.Html,
        [".htm"] = SupportedLanguages.Html,
        // CSS
        [".css"] = SupportedLanguages.Css,
        // XML / XSLT
        [".xml"] = SupportedLanguages.Xml,
        [".xsl"] = SupportedLanguages.Xml,
        [".xslt"] = SupportedLanguages.Xml,
    };

    /// <summary>Определить язык по пути; <c>null</c> если неизвестен.</summary>
    public static string? DetectByExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;
        return ExtensionMap.TryGetValue(ext, out var lang) ? lang : null;
    }

    /// <summary>Определить язык по shebang первой строки; <c>null</c> если нет/неизвестен.</summary>
    public static string? DetectByShebang(string content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var firstLine = ReadFirstLine(content);
        if (!firstLine.StartsWith("#!", StringComparison.Ordinal)) return null;

        var lower = firstLine.ToLowerInvariant();
        if (lower.Contains("python")) return SupportedLanguages.Python;
        if (lower.Contains("node") || lower.Contains("js")) return SupportedLanguages.JavaScript;
        if (lower.Contains("go")) return SupportedLanguages.Go;
        if (lower.Contains("bash") || lower.Contains("sh")) return SupportedLanguages.Bash;
        if (lower.Contains("ruby")) return SupportedLanguages.Ruby;
        if (lower.Contains("php")) return SupportedLanguages.Php;
        return null;
    }

    /// <summary>Комбинированный детектор: расширение приоритетно, затем shebang для файлов без расширения.</summary>
    public static string? Detect(string filePath, string? content = null)
    {
        var byExt = DetectByExtension(filePath);
        if (byExt != null) return byExt;
        if (content != null) return DetectByShebang(content);
        return null;
    }

    /// <summary>Проверить что файл может анализироваться (расширение известно или shebang известен).</summary>
    public static bool IsSupported(string filePath, string? content = null) => Detect(filePath, content) != null;

    private static string ReadFirstLine(string content)
    {
        var idx = content.IndexOf('\n');
        var line = idx < 0 ? content : content[..idx];
        return line.TrimEnd('\r').Trim();
    }
}
