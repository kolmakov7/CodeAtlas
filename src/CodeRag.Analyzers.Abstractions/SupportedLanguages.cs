namespace CodeRag.Analyzers.Abstractions;

/// <summary>Константы языков §13.</summary>
public static class SupportedLanguages
{
    public const string CSharp = "csharp";
    public const string Python = "python";
    public const string Sql = "sql";
    public const string Vbs = "vbs";
    public const string Cpp = "cpp";
    public const string JavaScript = "javascript";
    public const string Go = "go";
    public const string Html = "html";
    public const string Css = "css";
    public const string Xml = "xml";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CSharp, Python, Sql, Vbs, Cpp, JavaScript, Go, Html, Css, Xml
    };
}
