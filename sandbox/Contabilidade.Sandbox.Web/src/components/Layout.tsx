import { NavLink, Outlet } from 'react-router-dom';
import { LayoutDashboard, Building2, BookOpen, ListTree } from 'lucide-react';
import { clsx } from 'clsx';
import EmpresaSelector from './EmpresaSelector';

const navItems = [
  { to: '/dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/empresas', label: 'Empresas', icon: Building2 },
  { to: '/plano-contas', label: 'Plano de Contas', icon: ListTree },
  { to: '/lancamentos', label: 'Lançamentos', icon: BookOpen }
];

export default function Layout() {
  return (
    <div className="flex h-full">
      <aside className="w-60 shrink-0 border-r border-slate-200 bg-white">
        <div className="px-5 py-5 border-b border-slate-200">
          <div className="font-semibold text-slate-900">Contabilidade</div>
          <div className="text-xs text-slate-500">Sandbox · DbSense</div>
        </div>
        <nav className="px-3 py-4 space-y-1">
          {navItems.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              className={({ isActive }) =>
                clsx(
                  'flex items-center gap-2 rounded-md px-3 py-2 text-sm transition',
                  isActive
                    ? 'bg-slate-900 text-white'
                    : 'text-slate-700 hover:bg-slate-100'
                )
              }
            >
              <item.icon className="h-4 w-4" />
              {item.label}
            </NavLink>
          ))}
        </nav>
      </aside>
      <main className="flex-1 overflow-auto">
        <header className="sticky top-0 z-10 flex items-center justify-between border-b border-slate-200 bg-white/90 backdrop-blur px-6 py-3">
          <div className="text-sm text-slate-500">
            Sandbox para validar recording / inferência do DbSense
          </div>
          <EmpresaSelector />
        </header>
        <div className="p-6">
          <Outlet />
        </div>
      </main>
    </div>
  );
}
