import { api } from "./client";

export type RecordingStatus = "recording" | "completed" | "failed" | "discarded";

export type RecordingListItem = {
  id: string;
  connectionId: string;
  connectionName: string;
  name: string;
  description: string | null;
  status: RecordingStatus;
  startedAt: string;
  stoppedAt: string | null;
  eventCount: number;
};

export type RecordingDetail = RecordingListItem & {
  filterHostName: string | null;
  filterAppName: string | null;
  filterLoginName: string | null;
  filterSessionId: number | null;
};

export type CreateRecordingInput = {
  connectionId: string;
  name: string;
  description?: string;
  filterHostName?: string;
  filterAppName?: string;
  filterLoginName?: string;
};

export type ParsedDmlStatement = {
  operation: "insert" | "update" | "delete";
  schema: string | null;
  table: string;
  columns: string[];
  values: Record<string, string | null>;
  where: { column: string; op: string; value: string }[];
};

export type ParsedPayload = {
  statements: ParsedDmlStatement[];
};

export type RecordingEventItem = {
  id: number;
  eventTimestamp: string;
  eventType: string;
  sessionId: number;
  databaseName: string;
  objectName: string | null;
  sqlText: string;
  durationUs: number;
  rowCount: number | null;
  appName: string | null;
  hostName: string | null;
  loginName: string | null;
  transactionId: number | null;
  parsedPayload: string | null;
};

export function parseRecordingPayload(raw: string | null): ParsedPayload | null {
  if (!raw) return null;
  try { return JSON.parse(raw) as ParsedPayload; }
  catch { return null; }
}

export type RecordingEventsPage = {
  items: RecordingEventItem[];
  nextCursor: number | null;
  total: number;
};

export async function listRecordings() {
  const r = await api.get<RecordingListItem[]>("/recordings");
  return r.data;
}

export async function getRecording(id: string) {
  const r = await api.get<RecordingDetail>(`/recordings/${id}`);
  return r.data;
}

export async function startRecording(input: CreateRecordingInput) {
  const r = await api.post<RecordingDetail>("/recordings", input);
  return r.data;
}

export async function stopRecording(id: string) {
  const r = await api.post<RecordingDetail>(`/recordings/${id}/stop`);
  return r.data;
}

export async function discardRecording(id: string) {
  const r = await api.post<RecordingDetail>(`/recordings/${id}/discard`);
  return r.data;
}

export async function deleteRecording(id: string) {
  await api.delete(`/recordings/${id}`);
}

export async function listRecordingEvents(id: string, after?: number, limit = 100) {
  const params = new URLSearchParams();
  if (after !== undefined) params.set("after", String(after));
  params.set("limit", String(limit));
  const r = await api.get<RecordingEventsPage>(`/recordings/${id}/events?${params.toString()}`);
  return r.data;
}
