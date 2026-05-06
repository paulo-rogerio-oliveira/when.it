import { api } from "./client";

export type RuleStatus = "draft" | "testing" | "active" | "paused" | "archived";

export type RuleListItem = {
  id: string;
  connectionId: string;
  connectionName: string;
  name: string;
  description: string | null;
  version: number;
  status: RuleStatus;
  createdAt: string;
  updatedAt: string;
  activatedAt: string | null;
};

export type RuleDetail = RuleListItem & {
  destinationId: string | null;
  sourceRecordingId: string | null;
  definition: string;
};

export type CreateRuleInput = {
  connectionId: string;
  sourceRecordingId?: string;
  name: string;
  description?: string;
  definition: string;
};

export type UpdateRuleInput = {
  name: string;
  description?: string;
  definition: string;
};

export type InferredPredicateClause = { field: string; op: string; value: string };

export type InferredCompanion = {
  eventId: number;
  operation: string;
  schema: string | null;
  table: string;
  required: boolean;
};

export type InferredRulePayload = {
  suggestedName: string;
  suggestedDescription: string;
  database: string;
  schema: string | null;
  table: string;
  operation: string;
  predicate: InferredPredicateClause[];
  afterFields: string[];
  partitionKey: string | null;
  companions: InferredCompanion[];
  correlationWaitMs: number;
  correlationScope: string;
  definitionJson: string;
};

export type ClassifiedEvent = {
  eventId: number;
  classification: "main" | "correlation" | "noise";
  reason: string | null;
};

export type HeuristicInference = {
  success: boolean;
  error: string | null;
  rule: InferredRulePayload | null;
  events: ClassifiedEvent[];
};

export type LlmInference = {
  enabled: boolean;
  success: boolean;
  error: string | null;
  rule: InferredRulePayload | null;
  reasoning: string | null;
  mainEventId: number | null;
  events: ClassifiedEvent[];
  inputTokens: number | null;
  outputTokens: number | null;
};

export type InferRuleResponse = {
  heuristic: HeuristicInference;
  llm: LlmInference;
};

export async function inferRule(recordingId: string) {
  const r = await api.post<InferRuleResponse>(`/recordings/${recordingId}/infer-rule`);
  return r.data;
}

export async function createRule(input: CreateRuleInput) {
  const r = await api.post<RuleDetail>("/rules", input);
  return r.data;
}

export async function listRules() {
  const r = await api.get<RuleListItem[]>("/rules");
  return r.data;
}

export async function getRule(id: string) {
  const r = await api.get<RuleDetail>(`/rules/${id}`);
  return r.data;
}

export async function updateRule(id: string, input: UpdateRuleInput) {
  const r = await api.put<RuleDetail>(`/rules/${id}`, input);
  return r.data;
}

export type TestReactionResult = {
  eventsLogId: number;
  outboxId: number;
  idempotencyKey: string;
  reactionType: string;
};

export async function activateRule(id: string) {
  const r = await api.post<RuleDetail>(`/rules/${id}/activate`);
  return r.data;
}

export async function pauseRule(id: string) {
  const r = await api.post<RuleDetail>(`/rules/${id}/pause`);
  return r.data;
}

export async function testReaction(id: string, payload?: unknown) {
  const r = await api.post<TestReactionResult>(`/rules/${id}/test-reaction`, { payload });
  return r.data;
}
