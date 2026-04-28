import { api } from "./client";

export type ConnectionAuthType = "sql" | "windows";
export type ConnectionStatus = "inactive" | "testing" | "active" | "error";

export type ConnectionListItem = {
  id: string;
  name: string;
  server: string;
  database: string;
  authType: ConnectionAuthType;
  username: string | null;
  status: ConnectionStatus;
  lastTestedAt: string | null;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
};

export type ConnectionDetail = ConnectionListItem & {
  hasPassword: boolean;
};

export type SaveConnectionInput = {
  name: string;
  server: string;
  database: string;
  authType: ConnectionAuthType;
  username?: string;
  password?: string;
};

export type ConnectionTestOutcome = {
  success: boolean;
  error: string | null;
  elapsedMs: number;
};

export async function listConnections() {
  const r = await api.get<ConnectionListItem[]>("/connections");
  return r.data;
}

export async function getConnection(id: string) {
  const r = await api.get<ConnectionDetail>(`/connections/${id}`);
  return r.data;
}

export async function createConnection(input: SaveConnectionInput) {
  const r = await api.post<ConnectionDetail>("/connections", input);
  return r.data;
}

export async function updateConnection(
  id: string,
  input: SaveConnectionInput & { clearPassword?: boolean },
) {
  const r = await api.put<ConnectionDetail>(`/connections/${id}`, input);
  return r.data;
}

export async function deleteConnection(id: string) {
  await api.delete(`/connections/${id}`);
}

export async function testConnectionById(id: string) {
  const r = await api.post<ConnectionTestOutcome>(`/connections/${id}/test`);
  return r.data;
}

export async function testConnectionAdHoc(input: SaveConnectionInput) {
  const r = await api.post<ConnectionTestOutcome>("/connections/test", input);
  return r.data;
}
