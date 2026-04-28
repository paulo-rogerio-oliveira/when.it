import axios from "axios";
import { api } from "./client";

export type SetupStatus = {
  status: "not_provisioned" | "pending_admin" | "ready";
  schemaVersion: string | null;
  provisionedAt: string | null;
};

export type TestConnectionRequest = {
  server: string;
  database: string;
  authType: "sql" | "windows";
  username?: string;
  password?: string;
};

export type TestConnectionResponse = {
  success: boolean;
  error: string | null;
  elapsedMs: number;
};

export type ProvisionRequest = TestConnectionRequest;

export type ProvisionResponse = {
  success: boolean;
  error: string | null;
  tablesCreated: number;
  schemaVersion: string;
  errorCode?: string | null;
  hint?: string | null;
};

export type CreateAdminRequest = {
  username: string;
  password: string;
};

export type CreateAdminResponse = {
  userId: string;
  username: string;
};

export async function getSetupStatus() {
  const r = await api.get<SetupStatus>("/setup/status");
  return r.data;
}

export async function testConnection(req: TestConnectionRequest) {
  const r = await api.post<TestConnectionResponse>("/setup/test-connection", req);
  return r.data;
}

export async function provision(req: ProvisionRequest): Promise<ProvisionResponse> {
  try {
    const r = await api.post<ProvisionResponse>("/setup/provision", req);
    return r.data;
  } catch (err) {
    if (axios.isAxiosError(err) && err.response?.data) {
      const data = err.response.data as Partial<ProvisionResponse>;
      if (typeof data.success === "boolean") {
        return {
          success: false,
          error: data.error ?? "Falha ao provisionar",
          tablesCreated: data.tablesCreated ?? 0,
          schemaVersion: data.schemaVersion ?? "",
          errorCode: data.errorCode ?? null,
          hint: data.hint ?? null,
        };
      }
    }
    throw err;
  }
}

export async function createAdmin(req: CreateAdminRequest) {
  const r = await api.post<CreateAdminResponse>("/setup/create-admin", req);
  return r.data;
}
