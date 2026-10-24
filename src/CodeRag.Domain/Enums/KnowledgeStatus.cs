using System.Text.Json.Serialization;

namespace CodeRag.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeStatus
{
    Active = 0,
    Stale = 1,
    Superseded = 2,
    Archived = 3
}
