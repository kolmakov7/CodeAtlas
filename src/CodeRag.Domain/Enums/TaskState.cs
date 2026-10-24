using System.Text.Json.Serialization;

namespace CodeRag.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskState
{
    Open = 0,
    InProgress = 1,
    Blocked = 2,
    Done = 3,
    Cancelled = 4,
    Archived = 5
}
