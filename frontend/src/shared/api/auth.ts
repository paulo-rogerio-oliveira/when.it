import { api } from "./client";

export type LoginRequest = { username: string; password: string };
export type LoginResponse = {
  token: string;
  expiresAt: string;
  username: string;
  role: string;
};

export async function login(req: LoginRequest) {
  const r = await api.post<LoginResponse>("/auth/login", req);
  return r.data;
}
