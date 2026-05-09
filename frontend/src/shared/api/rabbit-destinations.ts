import { api } from "./client";

export type RabbitDestinationStatus = "inactive" | "active" | "error";

export type RabbitDestinationListItem = {
  id: string;
  name: string;
  host: string;
  port: number;
  virtualHost: string;
  username: string;
  useTls: boolean;
  defaultExchange: string;
  status: RabbitDestinationStatus;
  lastTestedAt: string | null;
  lastError: string | null;
  createdAt: string;
};

export type RabbitDestinationDetail = RabbitDestinationListItem & {
  hasPassword: boolean;
};

export type SaveRabbitDestinationInput = {
  name: string;
  host: string;
  port: number;
  virtualHost: string;
  username: string;
  password?: string;
  useTls: boolean;
  defaultExchange: string;
};

export type RabbitDestinationTestOutcome = {
  success: boolean;
  error: string | null;
  elapsedMs: number;
};

export async function listRabbitDestinations() {
  const r = await api.get<RabbitDestinationListItem[]>("/rabbit-destinations");
  return r.data;
}

export async function getRabbitDestination(id: string) {
  const r = await api.get<RabbitDestinationDetail>(`/rabbit-destinations/${id}`);
  return r.data;
}

export async function createRabbitDestination(input: SaveRabbitDestinationInput) {
  const r = await api.post<RabbitDestinationDetail>("/rabbit-destinations", input);
  return r.data;
}

export async function updateRabbitDestination(
  id: string,
  input: SaveRabbitDestinationInput & { clearPassword?: boolean },
) {
  const r = await api.put<RabbitDestinationDetail>(`/rabbit-destinations/${id}`, input);
  return r.data;
}

export async function deleteRabbitDestination(id: string) {
  await api.delete(`/rabbit-destinations/${id}`);
}

export async function testRabbitDestinationById(id: string) {
  const r = await api.post<RabbitDestinationTestOutcome>(`/rabbit-destinations/${id}/test`);
  return r.data;
}

export async function testRabbitDestinationAdHoc(input: SaveRabbitDestinationInput) {
  const r = await api.post<RabbitDestinationTestOutcome>("/rabbit-destinations/test", input);
  return r.data;
}
