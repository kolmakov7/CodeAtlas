using System.Text.Json.Serialization;

namespace CodeRag.Domain.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsolidationJobStatus
{
    Pending = 0,
    ReadyForAnalysis = 1,
    AwaitingLlm = 2,
    ReadyForReview = 3,
    Accepted = 4,
    Rejected = 5,
    Cancelled = 6,
    Error = 7,
    Postponed = 8,
    Undone = 9
}
