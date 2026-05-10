import { createContext, useContext, useEffect, useState, type ReactNode } from "react";

// Tema com 3 estados: light fixo, dark fixo, ou "system" (segue o OS via
// prefers-color-scheme). Persistido em localStorage. Aplica/remove a classe
// `.dark` no <html> que o Tailwind escuta (darkMode: ["class"]).
type Theme = "light" | "dark" | "system";

type ThemeContextValue = {
  theme: Theme;
  resolvedTheme: "light" | "dark";
  setTheme: (theme: Theme) => void;
};

const STORAGE_KEY = "dbsense-theme";
const ThemeContext = createContext<ThemeContextValue | undefined>(undefined);

function readStored(): Theme {
  if (typeof localStorage === "undefined") return "system";
  const v = localStorage.getItem(STORAGE_KEY);
  return v === "light" || v === "dark" || v === "system" ? v : "system";
}

function systemPrefersDark(): boolean {
  if (typeof window === "undefined") return false;
  return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function applyToHtml(resolved: "light" | "dark") {
  const root = document.documentElement;
  if (resolved === "dark") root.classList.add("dark");
  else root.classList.remove("dark");
  // color-scheme ajuda inputs/scrollbars nativos a herdarem o tom.
  root.style.colorScheme = resolved;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(() => readStored());
  const [resolvedTheme, setResolvedTheme] = useState<"light" | "dark">(() =>
    readStored() === "system" ? (systemPrefersDark() ? "dark" : "light") : (readStored() as "light" | "dark"),
  );

  useEffect(() => {
    const resolved = theme === "system" ? (systemPrefersDark() ? "dark" : "light") : theme;
    setResolvedTheme(resolved);
    applyToHtml(resolved);
    localStorage.setItem(STORAGE_KEY, theme);
  }, [theme]);

  // Quando o tema é "system", segue mudanças do OS em runtime — o usuário
  // troca o tema do macOS/Windows e a UI acompanha sem reload.
  useEffect(() => {
    if (theme !== "system") return;
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => {
      const next = mq.matches ? "dark" : "light";
      setResolvedTheme(next);
      applyToHtml(next);
    };
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, [theme]);

  return (
    <ThemeContext.Provider value={{ theme, resolvedTheme, setTheme: setThemeState }}>
      {children}
    </ThemeContext.Provider>
  );
}

export function useTheme() {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error("useTheme deve ser usado dentro de <ThemeProvider>");
  return ctx;
}
