using Microsoft.CodeAnalysis;

namespace CodeRag.Analyzers.CSharp.Internal;

internal static class BclReferenceProvider
{
    private static readonly string[] BclAssemblyNames =
    {
        "System.Runtime",
        "System.Collections",
        "netstandard",
        "System.Private.CoreLib",
        "System.Linq",
        "System.Threading",
        "System.Collections.Immutable",
        "System.Memory",
        "System.Console",
    };

    private static readonly Lazy<IReadOnlyList<MetadataReference>> LazyRefs = new(GetBclReferencesInternal, true);

    public static IReadOnlyList<MetadataReference> GetReferences() => LazyRefs.Value;

    private static IReadOnlyList<MetadataReference> GetBclReferencesInternal()
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MetadataReference>();
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path)) return;
            if (!refs.Add(path)) return;
            try { result.Add(MetadataReference.CreateFromFile(path)); } catch { }
        }

        Add(typeof(object).Assembly.Location);
        Add(typeof(Console).Assembly.Location);
        Add(typeof(Enumerable).Assembly.Location);
        Add(typeof(List<>).Assembly.Location);
        Add(typeof(System.Runtime.GCSettings).Assembly.Location);

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null)
        {
            foreach (var name in BclAssemblyNames)
                Add(Path.Combine(runtimeDir, name + ".dll"));
        }

        var tpa = (string?)AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var p in tpa.Split(Path.PathSeparator))
            {
                foreach (var name in BclAssemblyNames)
                    if (p.EndsWith(name + ".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(p);
                        break;
                    }
            }
        }

        if (result.Count == 0) Add(typeof(object).Assembly.Location);
        return result;
    }
}
