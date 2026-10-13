using System.Text;
using System.Text.RegularExpressions;

namespace CodeRag.Documentation;

/// <summary>
/// Post-processing for LLM output: normalizes the Markdown and guards against common
/// model misbehaviour (an accidental wrapping fence, a leading preamble, a trailing sign-off).
/// </summary>
public static partial class DocumentationValidator
{
    /// <summary>Clean an LLM response into a well-formed Markdown document.</summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim();

        // Strip a single wrapping ```markdown ... ``` fence if the model added one around the whole doc.
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline > 0)
            {
                var firstLine = s[..firstNewline].Trim();
                if (firstLine is "```" or "```markdown" or "```md" && s.EndsWith("```", StringComparison.Ordinal))
                {
                    var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
                    if (lastFence > firstNewline)
                        s = s[(firstNewline + 1)..lastFence].Trim();
                }
            }
        }

        // Drop a leading preamble that comes before the first heading (e.g. "Here is the documentation:").
        var headingIdx = IndexOfFirstHeading(s);
        if (headingIdx > 0)
            s = s[headingIdx..].Trim();

        // Drop a trailing sign-off after the last content line if it starts a new paragraph that is clearly meta.
        s = StripTrailingSignOff(s);

        s = s.Replace("\r\n", "\n").Trim();
        // Collapse 3+ consecutive blank lines.
        s = ThreeBlankLines().Replace(s, "\n\n");

        return s;
    }

    /// <summary>True when the (normalized) document looks like a usable Markdown doc (has a heading).</summary>
    public static bool LooksLikeMarkdown(string doc)
        => !string.IsNullOrWhiteSpace(doc) && IndexOfFirstHeading(doc) >= 0;

    private static int IndexOfFirstHeading(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is not '#') continue;
            if (i + 1 < s.Length && s[i + 1] == ' ')
                return i;
        }
        return -1;
    }

    private static string StripTrailingSignOff(string s)
    {
        // Trim the final paragraph when it is clearly a meta sign-off and the doc has real content before it.
        var markers = new[] { "Hope this helps", "Let me know", "If you have questions", "Let me know if" };
        var lines = s.Split('\n');

        // Index of the final non-empty line.
        var last = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i])) { last = i; break; }
        }
        if (last <= 0) return s; // nothing (or only this line) before it — leave the document intact.

        var line = lines[last].Trim();
        if (markers.Any(m => line.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
            return string.Join('\n', lines.Take(last)).TrimEnd();

        return s;
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ThreeBlankLines();
}
