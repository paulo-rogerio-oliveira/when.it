import { createContext, useContext, useEffect, useState, type ReactNode } from "react";

type AuthSession = {
  token: string;
  username: string;
  role: string;
  expiresAt: string;
};

type AuthContextValue = {
  token: string | null;
  username: string | null;
  role: string | null;
  login: (session: AuthSession) => void;
  logout: () => void;
};

const STORAGE_KEY = "dbsense.session";
const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<AuthSession | null>(() => {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    try {
      const parsed = JSON.parse(raw) as AuthSession;
      if (new Date(parsed.expiresAt) <= new Date()) return null;
      return parsed;
    } catch {
      return null;
    }
  });

  useEffect(() => {
    if (session) localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
    else localStorage.removeItem(STORAGE_KEY);
  }, [session]);

  const value: AuthContextValue = {
    token: session?.token ?? null,
    username: session?.username ?? null,
    role: session?.role ?? null,
    login: (s) => setSession(s),
    logout: () => setSession(null),
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}
