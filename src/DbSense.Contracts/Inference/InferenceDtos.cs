namespace DbSense.Contracts.Inference;

public record InferredPredicateClause(string Field, string Op, string Value);

public record InferredCompanionDto(
    long EventId,
    string Operation,
    string? Schema,
    string Table,
    bool Required);

public record InferredRulePayload(
    string SuggestedName,
    string SuggestedDescription,
    string Database,
    string? Schema,
    string Table,
    string Operation,
    IReadOnlyList<InferredPredicateClause> Predicate,
    IReadOnlyList<string> AfterFields,
    string? PartitionKey,
    IReadOnlyList<InferredCompanionDto> Companions,
    int CorrelationWaitMs,
    string CorrelationScope,
    string DefinitionJson);

public record ClassifiedEventDto(
    long EventId,
    string Classification,   // main | correlation | noise
    string? Reason);

public record HeuristicInferenceDto(
    bool Success,
    string? Error,
    InferredRulePayload? Rule,
    IReadOnlyList<ClassifiedEventDto> Events);

public record LlmInferenceDto(
    bool Enabled,
    bool Success,
    string? Error,
    InferredRulePayload? Rule,
    string? Reasoning,
    long? MainEventId,
    IReadOnlyList<ClassifiedEventDto> Events,
    int? InputTokens,
    int? OutputTokens);

public record InferRuleResponse(
    HeuristicInferenceDto Heuristic,
    LlmInferenceDto Llm);
