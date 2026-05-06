import { useEmpresaStore } from '../store/empresa';
import { useLancamentos, usePlanoContas } from '../api/hooks';
import { TrendingDown, TrendingUp, Wallet, Receipt } from 'lucide-react';

export default function Dashboard() {
  const empresaId = useEmpresaStore((s) => s.empresaId);
  const { data: contas } = usePlanoContas(empresaId);
  const { data: lancamentos } = useLancamentos(empresaId);

  if (!empresaId) {
    return <Empty message="Selecione uma empresa no topo." />;
  }

  const ativos = contas?.find((c) => c.codigo === '1');
  const passivos = contas?.find((c) => c.codigo === '2');
  const receitas = contas?.find((c) => c.codigo === '4');
  const despesas = contas?.find((c) => c.codigo === '5');

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-slate-900">Visão geral</h1>
        <p className="text-sm text-slate-500">
          Resumo dos saldos consolidados e últimos lançamentos.
        </p>
      </div>

      <div className="grid grid-cols-4 gap-4">
        <Card label="Ativos" value={ativos?.saldo ?? 0} icon={<Wallet />} variant="positive" />
        <Card label="Passivos" value={passivos?.saldo ?? 0} icon={<Receipt />} variant="neutral" />
        <Card label="Receitas" value={receitas?.saldo ?? 0} icon={<TrendingUp />} variant="positive" />
        <Card label="Despesas" value={despesas?.saldo ?? 0} icon={<TrendingDown />} variant="negative" />
      </div>

      <div className="bg-white rounded-lg border border-slate-200">
        <div className="px-4 py-3 border-b border-slate-200">
          <h2 className="text-sm font-semibold text-slate-900">Últimos lançamentos</h2>
        </div>
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-600">
            <tr>
              <th className="px-4 py-2 text-left font-medium">#</th>
              <th className="px-4 py-2 text-left font-medium">Data</th>
              <th className="px-4 py-2 text-left font-medium">Histórico</th>
              <th className="px-4 py-2 text-right font-medium">Valor</th>
              <th className="px-4 py-2 text-left font-medium">Status</th>
            </tr>
          </thead>
          <tbody>
            {lancamentos?.items.slice(0, 10).map((l) => (
              <tr key={l.id} className="border-t border-slate-100">
                <td className="px-4 py-2 font-mono text-xs">{l.numero}</td>
                <td className="px-4 py-2">{new Date(l.dataCompetencia).toLocaleDateString('pt-BR')}</td>
                <td className="px-4 py-2">{l.historico}</td>
                <td className="px-4 py-2 text-right font-mono">
                  {l.valorTotal.toLocaleString('pt-BR', { minimumFractionDigits: 2 })}
                </td>
                <td className="px-4 py-2">
                  <StatusBadge status={l.status} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Card({
  label, value, icon, variant
}: { label: string; value: number; icon: React.ReactNode; variant: 'positive' | 'negative' | 'neutral' }) {
  const color =
    variant === 'positive' ? 'text-emerald-600' : variant === 'negative' ? 'text-rose-600' : 'text-slate-700';
  return (
    <div className="bg-white rounded-lg border border-slate-200 p-4">
      <div className="flex items-center justify-between">
        <span className="text-xs uppercase tracking-wide text-slate-500">{label}</span>
        <span className={color}>{icon}</span>
      </div>
      <div className={`mt-2 text-2xl font-semibold ${color}`}>
        R$ {value.toLocaleString('pt-BR', { minimumFractionDigits: 2 })}
      </div>
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const map: Record<string, string> = {
    Confirmado: 'bg-emerald-100 text-emerald-700',
    Rascunho: 'bg-amber-100 text-amber-700',
    Cancelado: 'bg-rose-100 text-rose-700'
  };
  return (
    <span className={`px-2 py-0.5 rounded text-xs ${map[status] ?? 'bg-slate-100 text-slate-700'}`}>
      {status}
    </span>
  );
}

function Empty({ message }: { message: string }) {
  return <div className="text-sm text-slate-500">{message}</div>;
}
