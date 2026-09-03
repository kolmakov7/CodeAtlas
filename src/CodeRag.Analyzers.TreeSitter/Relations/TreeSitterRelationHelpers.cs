using System.Collections.Concurrent;
using CodeRag.Analyzers.Abstractions;
using TreeSitter;

namespace CodeRag.Analyzers.TreeSitter.Relations;

/// <summary>Общие утилиты для TreeSitter whole-repo экстракторов (каталог, нормализация, разрешение).</summary>
internal static class TreeSitterRelationHelpers
{
    public static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", ".vs", "packages", ".nuget", "dist", "build", ".idea", ".gradle"
    };

    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var x = s;
        if (x.StartsWith("global::")) x = x["global::".Length..];
        x = x.Replace("<global namespace>", "").Replace(" ", "").Replace("\t", "").Replace("\r", "").Replace("\n", "");
        return x.ToLowerInvariant();
    }

    public static bool IsExcludedPath(string path)
    {
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (part.Length > 0 && ExcludedDirs.Contains(part)) return true;
        return false;
    }

    // Catalog indexes
    public sealed class CatalogIndex
    {
        public readonly Dictionary<string, List<CatalogSymbol>> ByFqn = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CatalogSymbol>> ByNormFqn = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CatalogSymbol>> ByName = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CatalogSymbol>> ByNameLower = new(StringComparer.Ordinal);

        public CatalogIndex(IReadOnlyList<CatalogSymbol> catalog)
        {
            foreach (var c in catalog)
            {
                if (!string.IsNullOrWhiteSpace(c.Fqn))
                {
                    Add(ByFqn, c.Fqn, c);
                    Add(ByNormFqn, Normalize(c.Fqn), c);
                    var last = LastSegment(c.Fqn);
                    if (!string.IsNullOrWhiteSpace(last)) Add(ByName, last, c);
                }
                if (!string.IsNullOrWhiteSpace(c.Name))
                {
                    Add(ByName, c.Name, c);
                    Add(ByNameLower, c.Name.ToLowerInvariant(), c);
                }
            }
        }

        private static void Add(Dictionary<string, List<CatalogSymbol>> dict, string key, CatalogSymbol val)
        {
            if (!dict.TryGetValue(key, out var list)) { list = new List<CatalogSymbol>(); dict[key] = list; }
            if (!list.Any(x => x.Id == val.Id)) list.Add(val);
        }

        private static string LastSegment(string fqn)
        {
            var idx = fqn.LastIndexOf('.');
            if (idx >= 0) return fqn[(idx + 1)..];
            idx = fqn.LastIndexOf("::");
            if (idx >= 0) return fqn[(idx + 2)..];
            return fqn;
        }

        public Guid? ResolveFqn(string? fqn)
        {
            if (string.IsNullOrWhiteSpace(fqn)) return null;
            if (ByFqn.TryGetValue(fqn, out var l1) && l1.Count == 1) return l1[0].Id;
            var nk = Normalize(fqn);
            if (ByNormFqn.TryGetValue(nk, out var l2) && l2.Count == 1) return l2[0].Id;
            return null;
        }

        public Guid? ResolveByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (ByName.TryGetValue(name, out var l1) && l1.Count == 1) return l1[0].Id;
            var lower = name.ToLowerInvariant();
            if (ByNameLower.TryGetValue(lower, out var l2) && l2.Count == 1) return l2[0].Id;
            return null;
        }

        public Guid? ResolveCall(string calleeName, string? qualifier)
        {
            if (!string.IsNullOrWhiteSpace(qualifier))
            {
                var qualified = $"{qualifier}.{calleeName}";
                var r = ResolveFqn(qualified);
                if (r != null) return r;
                // Try suffix match: any FQN ends with ".qualifier.callee" or ".callee" with qualifier segment
                foreach (var kv in ByFqn)
                {
                    if (kv.Value.Count != 1) continue;
                    var fqn = kv.Key;
                    if (fqn.EndsWith("." + calleeName, StringComparison.Ordinal) && fqn.Contains(qualifier, StringComparison.Ordinal))
                        return kv.Value[0].Id;
                }
            }
            return ResolveByName(calleeName);
        }
    }

    // Language load helpers (копия из TreeSitterAnalyzer)
    public static readonly Dictionary<string, string[]> LanguageLoadFallbacks = new(StringComparer.Ordinal)
    {
        ["C++"] = new[] { "Cpp", "cpp", "c++" },
        ["TypeScript"] = new[] { "Typescript", "typescript", "TSX", "tsx" },
        ["TSX"] = new[] { "TypeScript", "typescript" },
        ["JavaScript"] = new[] { "Javascript", "javascript", "JS" },
    };

    public static Language? TryLoadLanguage(string name)
    {
        var candidates = new List<string> { name };
        if (LanguageLoadFallbacks.TryGetValue(name, out var extra))
            candidates.AddRange(extra);
        foreach (var cand in candidates)
        {
            try { return new Language(cand); }
            catch { }
        }
        return null;
    }

    // Extract helpers — копия из TreeSitterAnalyzer для консистентности FQN
    public static string? ExtractName(Node node, string? treeLang = null)
    {
        if (node.Type == "function_definition" && treeLang == "C++")
        {
            var declarator = node.Children.FirstOrDefault(c => c.Type.Contains("declarator"));
            if (declarator != null)
            {
                var found = FindIdentifierDeep(declarator);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    if (found.Contains("::")) return found.Split("::").Last().Trim();
                    return found;
                }
            }
        }
        if (node.Type == "namespace_definition")
        {
            foreach (var child in node.Children)
                if (child.Type.Contains("identifier")) return child.Text.Trim();
        }
        if (node.Type == "class_specifier" && treeLang == "C++")
        {
            foreach (var child in node.Children)
                if (child.Type is "type_identifier" or "identifier") return child.Text.Trim();
            var deep = FindIdentifierDeep(node);
            if (!string.IsNullOrWhiteSpace(deep)) return deep;
        }
        foreach (var child in node.Children)
        {
            if (child.Type.Contains("identifier")) return child.Text.Trim();
        }
        foreach (var child in node.Children)
        {
            foreach (var grand in child.Children)
            {
                if (grand.Type.Contains("identifier")) return grand.Text.Trim();
                foreach (var gg in grand.Children)
                    if (gg.Type.Contains("identifier")) return gg.Text.Trim();
            }
        }
        var deep2 = FindIdentifierDeep(node);
        if (!string.IsNullOrWhiteSpace(deep2))
        {
            if (deep2.Contains("::")) return deep2.Split("::").Last().Trim();
            if (deep2.Contains(".")) return deep2.Split(".").Last().Trim();
            return deep2;
        }
        var txt = node.Text;
        if (!string.IsNullOrWhiteSpace(txt))
        {
            var idx = txt.IndexOf('(');
            if (idx > 0)
            {
                var before = txt[..idx].Trim();
                var parts = before.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var cand = parts[^1].Trim('*', '&');
                    if (cand.Contains("::")) cand = cand.Split("::").Last();
                    if (cand.Contains(".")) cand = cand.Split(".").Last();
                    return cand;
                }
            }
            var br = txt.IndexOf('{');
            if (br > 0)
            {
                var before = txt[..br].Trim();
                var parts = before.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    var cand = parts[^1].Trim();
                    if (cand.Contains("::")) cand = cand.Split("::").Last();
                    return cand;
                }
            }
        }
        return null;
    }

    public static string? FindIdentifierDeep(Node node)
    {
        if (node.Type.Contains("identifier")) return node.Text.Trim();
        foreach (var child in node.Children)
        {
            var res = FindIdentifierDeep(child);
            if (!string.IsNullOrWhiteSpace(res)) return res;
        }
        return null;
    }

    public static (string Name, string? Qualifier) ExtractCallInfo(Node node)
    {
        if (node.Children.Count > 0)
        {
            var func = node.Children[0];
            var txt = func.Text.Trim();
            // Handle member access: a.b.c() -> qualifier = a.b, name = c
            // TreeSitter func may be member_expression / field_expression / scoped_identifier
            var raw = txt.Split('(')[0].Trim();
            if (raw.Contains("::"))
            {
                var parts = raw.Split("::");
                var name = parts[^1].Trim();
                var qual = parts.Length > 1 ? string.Join("::", parts[..^1]).Trim() : null;
                return (name, qual);
            }
            if (raw.Contains('.'))
            {
                var segs = raw.Split('.');
                var name = segs[^1].Trim();
                var qual = segs.Length > 1 ? string.Join(".", segs[..^1]).Trim() : null;
                return (name, qual);
            }
            return (raw, null);
        }
        return (string.Empty, null);
    }
}
