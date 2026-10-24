using System.Text.Json.Serialization;

namespace CodeRag.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskPriority
{
    Critical = 0,
    High = 1,
    Normal = 2,
    Low = 3,
    Backlog = 4
}
