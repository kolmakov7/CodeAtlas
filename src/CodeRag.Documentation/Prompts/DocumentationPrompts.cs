using CodeRag.Documentation.Models;
using CodeRag.Domain.Enums;

namespace CodeRag.Documentation.Prompts;

/// <summary>
/// Versioned, English documentation-generation prompts. The prompt version is stored on the
/// generated entry so that prompt changes can later drive targeted regeneration.
/// The user message is the structured context (see <see cref="DocumentationContext.ToPromptText"/>);
/// the system message fixes the output contract and the anti-hallucination rules.
/// </summary>
public static class DocumentationPrompts
{
    /// <summary>Current prompt version (stored on each generated entry).</summary>
    public const string CurrentVersion = "v1";

    /// <summary>
    /// Build the system prompt for a target. The contract is the same across target types, but the
    /// required sections and the framing differ slightly (repo vs module vs symbol).
    /// </summary>
    public static string BuildSystemPrompt(DocumentationTarget target)
    {
        var sections = target.Type switch
        {
            DocumentationTargetType.Repository => RepoSections,
            DocumentationTargetType.Module => ModuleSections,
            _ => SymbolSections
        };

        var placeholder = target.Type switch
        {
            DocumentationTargetType.Repository => "{repo}",
            DocumentationTargetType.Module => "{module}",
            _ => "{name}"
        };
        sections = sections.Replace(placeholder, target.Name);

        return $"""
            You are a precise technical documentation writer for a codebase.

            You are given a structured context extracted from an existing, already-built code index
            (symbols, relations, and source code). Your ONLY job is to turn those facts into clear,
            accurate, human-readable technical documentation.

            HARD RULES:
            1. Write ONLY in English.
            2. Base every claim strictly on the provided context. Do NOT invent APIs, behaviour,
               parameters, return values, dependencies, or examples that are not present in the context.
            3. If the context does not contain enough information to answer a section, write
               "Not enough information in the index to determine this." for that section — do not guess.
            4. Output MUST be a single Markdown document. Use the section structure below, in order,
               using Markdown headings. Do not add any text before the first heading or after the last section.
            5. Be concise but complete. Prefer precise technical language over marketing language.
            6. When quoting code, keep it minimal and only where it clarifies the prose.

            REQUIRED SECTIONS:
            {sections}
            """;
    }

    private const string SymbolSections = """
            - `# {name}` — one or two sentence overview of what this element does.
            - `## Purpose` — its responsibility in the system and why it exists.
            - `## How It Works` — key logic, flow, and important implementation details (for a method: inputs, outputs, side effects, notable branches).
            - `## Dependencies` — the important symbols/types it relies on and the role of each. Omit if none.
            - `## Usage / Callers` — how it is used by other parts of the codebase. Omit if none.
            - `## Tests` — related test coverage that is known from the index. Omit if none.
            - `## Notes` — edge cases, constraints, or caveats evident from the code. Omit if none.
            """;

    private const string ModuleSections = """
            - `# {module}` — one or two sentence overview of this module/directory.
            - `## Responsibility` — what this module owns and its role in the system.
            - `## Key Components` — the most important symbols/files in the module and their purpose.
            - `## Interactions` — how the module interacts with other parts of the system.
            - `## Notes` — caveats, constraints, or notable patterns. Omit if none.
            """;

    private const string RepoSections = """
            - `# {repo}` — one or two sentence overview of this repository/project.
            - `## Purpose` — what the project does and the problem it solves.
            - `## Architecture` — the main modules/components and how they fit together.
            - `## Key Components` — the most important entry points and abstractions.
            - `## Conventions` — notable patterns, testing approach, or structure evident from the index. Omit if none.
            - `## Notes` — caveats or limitations of the available information. Omit if none.
            """;

    /// <summary>
    /// Build the user message wrapping the (possibly truncated) context block. The context is passed
    /// in already-rendered so the caller can enforce the model-window cap before it reaches the prompt.
    /// </summary>
    public static string BuildUserPrompt(DocumentationTarget target, string contextText)
    {
        var header = target.Type switch
        {
            DocumentationTargetType.Repository => $"Below is the indexed context for the repository \"{target.Name}\".",
            DocumentationTargetType.Module => $"Below is the indexed context for the module \"{target.Name}\".",
            _ => $"Below is the indexed context for the symbol \"{target.Name}\"."
        };

        return $"""
            {header}

            Produce the Markdown documentation now, following the required section structure exactly.

            <context>
            {contextText}
            </context>
            """;
    }
}
