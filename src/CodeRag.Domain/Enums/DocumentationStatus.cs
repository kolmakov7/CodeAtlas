namespace CodeRag.Domain.Enums;

/// <summary>
/// Lifecycle state of a generated documentation entry.
/// </summary>
public enum DocumentationStatus
{
    /// <summary>No documentation has been generated for this target yet.</summary>
    NotGenerated = 0,

    /// <summary>Documentation exists and matches the current source hash.</summary>
    Generated = 1,

    /// <summary>Documentation exists but the target (or a dependency) changed since generation.</summary>
    Stale = 2,

    /// <summary>Generation is in progress (a background job is running for this target).</summary>
    Generating = 3,

    /// <summary>The most recent generation attempt failed.</summary>
    Failed = 4
}
