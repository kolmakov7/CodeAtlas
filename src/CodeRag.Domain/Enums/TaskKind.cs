using System.Text.Json.Serialization;

namespace CodeRag.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskKind
{
    Work = 0,
    Temporary = 1,
    Research = 2,
    Note = 3
}
