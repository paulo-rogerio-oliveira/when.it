import { NavLink, Outlet } from "react-router-dom";
import { Button } from "@/shared/components/ui/button";
import { useAuth } from "./auth-provider";
import { cn } from "@/shared/utils/cn";

export function AppShell() {
  const { username, role, logout } = useAuth();

  return (
    <div className="min-h-screen bg-muted/30">
      <header className="flex items-center justify-between border-b bg-background px-6 py-3">
        <div className="flex items-center gap-8">
          <h1 className="text-lg font-semibold">DbSense</h1>
          <nav className="flex items-center gap-1 text-sm">
            <NavItem to="/">Dashboard</NavItem>
            <NavItem to="/connections">Conexões</NavItem>
            <NavItem to="/rabbit-destinations">Destinos Rabbit</NavItem>
            <NavItem to="/recordings">Gravações</NavItem>
            <NavItem to="/rules">Regras</NavItem>
          </nav>
        </div>
        <div className="flex items-center gap-4 text-sm">
          <span className="text-muted-foreground">
            {username} <span className="text-xs">({role})</span>
          </span>
          <Button size="sm" variant="outline" onClick={logout}>
            Sair
          </Button>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-8">
        <Outlet />
      </main>
    </div>
  );
}

function NavItem({ to, children }: { to: string; children: React.ReactNode }) {
  return (
    <NavLink
      to={to}
      end={to === "/"}
      className={({ isActive }) =>
        cn(
          "rounded-md px-3 py-1.5 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground",
          isActive && "bg-muted text-foreground",
        )
      }
    >
      {children}
    </NavLink>
  );
}
