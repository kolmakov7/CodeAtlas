using CodeRag.Domain.Enums;

namespace CodeRag.Documentation.Models;

/// <summary>A single related symbol referenced in the context (dependency, caller, member, ...).</summary>
public sealed record ContextSymbol(
    string Name,
    string? Fqn,
    string Kind,
    string? Signature,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string? RepositoryName = null)
{
    public string Display => string.IsNullOrWhiteSpace(Fqn) ? Name : Fqn;

    public string ToLine(string? location = null)
    {
        var sig = string.IsNullOrWhiteSpace(Signature) ? string.Empty : $" — {Signature.Trim()}";
        var loc = string.IsNullOrWhiteSpace(location) ? string.Empty : $" ({location})";
        var repo = string.IsNullOrWhiteSpace(RepositoryName) ? string.Empty : $" [repo: {RepositoryName}]";
        return $"- {Display}{sig}{loc}{repo}";
    }
}

/// <summary>A labelled source-code block included in the context.</summary>
public sealed record ContextSource(
    string Label,
    string Language,
    string Path,
    int? StartLine,
    int? EndLine,
    string Code)
{
    public string Header =>
        $"{Label} — {Path}" +
        (StartLine is int s && EndLine is int e ? $" (lines {s}–{e})" : string.Empty);
}

/// <summary>
/// The complete, structured fact set used to document one target. Built strictly from the existing
/// symbol index / relations / chunks / on-disk source (no new parsing). Rendered to an English
/// prompt via <see cref="ToPromptText"/>. This is the anti-hallucination boundary: the LLM may only
/// re-express what is present here.
/// </summary>
public sealed class DocumentationContext
{
    public required DocumentationTarget Target { get; init; }

    public string? Summary { get; set; }

    /// <summary>Existing human-readable doc comments / signature notes (if any).</summary>
    public string? ExistingDocComment { get; set; }

    public string? Language { get; set; }
    public string? Namespace { get; set; }
    public string? FilePath { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public string? Signature { get; set; }
    public string? IndexedCommit { get; set; }
    public string? Branch { get; set; }
    public string? RepositoryName { get; set; }

    /// <summary>The target's own source (preferred, most important).</summary>
    public List<ContextSource> Sources { get; set; } = new();

    /// <summary>Parent type (for a member) — source + signature.</summary>
    public ContextSource? ParentSource { get; set; }
    public string? ParentFqn { get; set; }

    /// <summary>Members of the target (for a type): methods/properties/fields.</summary>
    public List<ContextSymbol> Members { get; set; } = new();

    /// <summary>Symbols the target depends on (references/calls/uses/implements).</summary>
    public List<ContextSymbol> Dependencies { get; set; } = new();

    /// <summary>Interfaces the target implements (if any).</summary>
    public List<ContextSymbol> Interfaces { get; set; } = new();

    /// <summary>Base types the target inherits from.</summary>
    public List<ContextSymbol> BaseTypes { get; set; } = new();

    /// <summary>Symbols that reference/call the target (one hop in).</summary>
    public List<ContextSymbol> Callers { get; set; } = new();

    /// <summary>Test symbols related to the target (if the test search succeeded).</summary>
    public List<ContextSymbol> Tests { get; set; } = new();

    /// <summary>Top related code chunks from semantic/full-text search (context snippets).</summary>
    public List<ContextSource> RelatedSnippets { get; set; } = new();

    public int? RelatedFileCount { get; set; }
    public string? Warnings { get; set; }

    /// <summary>True when the target source could not be resolved (only index metadata available).</summary>
    public bool SourceMissing => Sources.Count == 0;

    public int EstimateChars() => ToPromptText().Length;

    /// <summary>Render the context as a compact English prompt block (facts only, no invented content).</summary>
    public string ToPromptText()
    {
        var sb = new System.Text.StringBuilder();
        var t = Target;

        sb.AppendLine($"TARGET: {t.Name}");
        sb.AppendLine($"TYPE: {t.Type.ToText()}");
        if (!string.IsNullOrWhiteSpace(RepositoryName)) sb.AppendLine($"REPOSITORY: {RepositoryName}");
        if (!string.IsNullOrWhiteSpace(Namespace)) sb.AppendLine($"NAMESPACE: {Namespace}");
        if (!string.IsNullOrWhiteSpace(Language)) sb.AppendLine($"LANGUAGE: {Language}");
        if (!string.IsNullOrWhiteSpace(FilePath)) sb.AppendLine($"FILE: {FilePath}");
        if (StartLine is int s && EndLine is int e) sb.AppendLine($"LINES: {s}–{e}");
        if (!string.IsNullOrWhiteSpace(Signature)) sb.AppendLine($"SIGNATURE: {Signature}");
        if (!string.IsNullOrWhiteSpace(IndexedCommit)) sb.AppendLine($"INDEXED COMMIT: {IndexedCommit}");
        if (!string.IsNullOrWhiteSpace(Branch)) sb.AppendLine($"BRANCH: {Branch}");
        if (!string.IsNullOrWhiteSpace(ParentFqn)) sb.AppendLine($"PARENT TYPE: {ParentFqn}");
        if (!string.IsNullOrWhiteSpace(Summary)) sb.AppendLine($"KNOWN SUMMARY: {Summary}");
        if (!string.IsNullOrWhiteSpace(ExistingDocComment))
        {
            sb.AppendLine("EXISTING DOC COMMENT:");
            sb.AppendLine(ExistingDocComment.Trim());
        }
        if (RelatedFileCount is int n) sb.AppendLine($"RELATED FILES (1 hop): {n}");

        foreach (var src in Sources)
        {
            sb.AppendLine();
            sb.AppendLine($"SOURCE [{src.Header}]");
            sb.AppendLine("```" + src.Language);
            sb.AppendLine(src.Code);
            sb.AppendLine("```");
        }

        if (ParentSource is { } p)
        {
            sb.AppendLine();
            sb.AppendLine($"PARENT SOURCE [{p.Header}]");
            sb.AppendLine("```" + p.Language);
            sb.AppendLine(p.Code);
            sb.AppendLine("```");
        }

        if (Members.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("MEMBERS:");
            foreach (var m in Members) sb.AppendLine(m.ToLine());
        }

        if (Dependencies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("DEPENDS ON (references / calls / uses):");
            foreach (var d in Dependencies) sb.AppendLine(d.ToLine());
        }

        if (Interfaces.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("IMPLEMENTS:");
            foreach (var d in Interfaces) sb.AppendLine(d.ToLine());
        }

        if (BaseTypes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("EXTENDS / DERIVES FROM:");
            foreach (var d in BaseTypes) sb.AppendLine(d.ToLine());
        }

        if (Callers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("REFERENCED / CALLED BY:");
            foreach (var d in Callers) sb.AppendLine(d.ToLine());
        }

        if (Tests.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RELATED TESTS:");
            foreach (var d in Tests) sb.AppendLine(d.ToLine());
        }

        foreach (var sn in RelatedSnippets)
        {
            sb.AppendLine();
            sb.AppendLine($"RELATED SNIPPET [{sn.Header}]");
            sb.AppendLine("```" + sn.Language);
            sb.AppendLine(sn.Code);
            sb.AppendLine("```");
        }

        if (!string.IsNullOrWhiteSpace(Warnings))
        {
            sb.AppendLine();
            sb.AppendLine("NOTES / LIMITATIONS:");
            sb.AppendLine(Warnings);
        }

        return sb.ToString().TrimEnd();
    }
}
