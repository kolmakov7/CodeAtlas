using System.Text.Json.Serialization;

namespace CodeRag.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsolidationJobType
{
    Duplicates = 0,
    Contradiction = 1,
    MissingSource = 2,
    Stale = 3,
    Consolidate = 4
}
