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
};

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

export async function listRecordingEvents(id: string, after?: number, limit = 100) {
  const params = new URLSearchParams();
  if (after !== undefined) params.set("after", String(after));
  params.set("limit", String(limit));
  const r = await api.get<RecordingEventsPage>(`/recordings/${id}/events?${params.toString()}`);
  return r.data;
}
