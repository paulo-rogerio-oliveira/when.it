import { useEffect, useState } from "react";
import { NavLink, Outlet, useLocation } from "react-router-dom";
import { Menu, X, Moon, Sun, Monitor, LogOut } from "lucide-react";
import { Button } from "@/shared/components/ui/button";
import { useAuth } from "./auth-provider";
import { useTheme } from "./theme-provider";
import { cn } from "@/shared/utils/cn";

type NavItemSpec = { to: string; label: string; end?: boolean };

const NAV_ITEMS: NavItemSpec[] = [
  { to: "/", label: "Dashboard", end: true },
  { to: "/connections", label: "Conexões" },
  { to: "/rabbit-destinations", label: "Destinos Rabbit" },
  { to: "/recordings", label: "Gravações" },
  { to: "/rules", label: "Regras" },
];

export function AppShell() {
  const { username, role, logout } = useAuth();
  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();

  // Fecha o drawer ao navegar — caso contrário ele fica aberto após clicar
  // num item pelo mobile sheet.
  useEffect(() => {
    setMobileOpen(false);
  }, [location.pathname]);

  // Trava o scroll do body enquanto o drawer mobile está aberto.
  useEffect(() => {
    if (!mobileOpen) return;
    const original = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => { document.body.style.overflow = original; };
  }, [mobileOpen]);

  return (
    <div className="min-h-screen bg-muted/30 text-foreground">
      <header className="sticky top-0 z-30 border-b bg-background/80 backdrop-blur supports-[backdrop-filter]:bg-background/70">
        <div className="mx-auto flex h-14 max-w-7xl items-center justify-between gap-3 px-4 sm:px-6">
          <div className="flex items-center gap-3 lg:gap-8">
            <button
              type="button"
              onClick={() => setMobileOpen((v) => !v)}
              className="inline-flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-muted hover:text-foreground md:hidden"
              aria-label="Abrir menu"
              aria-expanded={mobileOpen}
            >
              {mobileOpen ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}
            </button>

            <h1 className="select-none text-base font-semibold tracking-tight sm:text-lg">
              <span className="text-primary">Db</span>Sense
            </h1>

            <nav className="hidden items-center gap-1 text-sm md:flex">
              {NAV_ITEMS.map((item) => (
                <NavItem key={item.to} to={item.to} end={item.end}>
                  {item.label}
                </NavItem>
              ))}
            </nav>
          </div>

          <div className="flex items-center gap-2">
            <ThemeToggle />
            <UserMenu username={username} role={role} onLogout={logout} />
          </div>
        </div>

        {/* Drawer mobile — só renderiza quando aberto pra economizar layout. */}
        {mobileOpen && (
          <>
            <div
              className="fixed inset-0 top-14 z-20 bg-background/60 backdrop-blur-sm md:hidden"
              onClick={() => setMobileOpen(false)}
              aria-hidden="true"
            />
            <nav className="fixed inset-x-0 top-14 z-20 animate-slide-down border-b bg-background px-4 py-3 shadow-lg md:hidden">
              <div className="flex flex-col gap-1">
                {NAV_ITEMS.map((item) => (
                  <NavItem key={item.to} to={item.to} end={item.end} block>
                    {item.label}
                  </NavItem>
                ))}
              </div>
            </nav>
          </>
        )}
      </header>

      <main className="mx-auto max-w-7xl px-4 py-6 sm:px-6 sm:py-8">
        <Outlet />
      </main>
    </div>
  );
}

function NavItem({
  to,
  end,
  children,
  block = false,
}: {
  to: string;
  end?: boolean;
  children: React.ReactNode;
  block?: boolean;
}) {
  return (
    <NavLink
      to={to}
      end={end}
      className={({ isActive }) =>
        cn(
          "rounded-md px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-muted hover:text-foreground",
          block && "block",
          isActive && "bg-muted font-medium text-foreground",
        )
      }
    >
      {children}
    </NavLink>
  );
}

function ThemeToggle() {
  const { theme, setTheme } = useTheme();

  // Cicla entre os 3 modos: light → dark → system → light. Ícone reflete o
  // estado atual escolhido, não o resolvido.
  const next = theme === "light" ? "dark" : theme === "dark" ? "system" : "light";
  const Icon = theme === "light" ? Sun : theme === "dark" ? Moon : Monitor;
  const label = theme === "system" ? "Tema do sistema" : theme === "dark" ? "Tema escuro" : "Tema claro";

  return (
    <button
      type="button"
      onClick={() => setTheme(next)}
      className="inline-flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
      title={label}
      aria-label={label}
    >
      <Icon className="h-4 w-4" />
    </button>
  );
}

function UserMenu({
  username,
  role,
  onLogout,
}: {
  username: string | null;
  role: string | null;
  onLogout: () => void;
}) {
  return (
    <div className="flex items-center gap-2">
      <div className="hidden text-right text-xs leading-tight sm:block">
        <div className="font-medium text-foreground">{username}</div>
        <div className="text-muted-foreground">{role}</div>
      </div>
      <Button size="sm" variant="outline" onClick={onLogout} aria-label="Sair">
        <LogOut className="h-4 w-4" />
        <span className="hidden sm:inline">Sair</span>
      </Button>
    </div>
  );
}
