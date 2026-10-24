using System.Text.Json.Serialization;

namespace CodeRag.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeType
{
    Fact = 0,
    Decision = 1,
    Constraint = 2,
    Research = 3,
    Hypothesis = 4,
    Issue = 5,
    Observation = 6
}
