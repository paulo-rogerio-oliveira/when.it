import { useMemo, useState } from 'react';
import { ChevronRight, ChevronDown } from 'lucide-react';
import { clsx } from 'clsx';
import { useEmpresaStore } from '../store/empresa';
import { usePlanoContas } from '../api/hooks';
import type { ContaComSaldo } from '../api/types';

interface TreeNode extends ContaComSaldo {
  filhos: TreeNode[];
}

function buildTree(contas: ContaComSaldo[]): TreeNode[] {
  const byId = new Map<string, TreeNode>();
  contas.forEach((c) => byId.set(c.id, { ...c, filhos: [] }));
  const roots: TreeNode[] = [];
  byId.forEach((node) => {
    if (node.contaPaiId && byId.has(node.contaPaiId)) {
      byId.get(node.contaPaiId)!.filhos.push(node);
    } else {
      roots.push(node);
    }
  });
  const sortRecursive = (ns: TreeNode[]) => {
    ns.sort((a, b) => a.codigo.localeCompare(b.codigo));
    ns.forEach((n) => sortRecursive(n.filhos));
  };
  sortRecursive(roots);
  return roots;
}

export default function PlanoContas() {
  const empresaId = useEmpresaStore((s) => s.empresaId);
  const { data: contas, isLoading } = usePlanoContas(empresaId);
  const [expandido, setExpandido] = useState<Set<string>>(new Set());

  const tree = useMemo(() => (contas ? buildTree(contas) : []), [contas]);

  if (!empresaId) return <div className="text-sm text-slate-500">Selecione uma empresa.</div>;
  if (isLoading) return <div className="text-sm text-slate-400">carregando…</div>;

  function toggle(id: string) {
    const next = new Set(expandido);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setExpandido(next);
  }

  function expandAll() {
    const all = new Set<string>();
    contas?.forEach((c) => { if (!c.aceitaLancamento) all.add(c.id); });
    setExpandido(all);
  }

  function collapseAll() { setExpandido(new Set()); }

  return (
    <div className="space-y-4">
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-xl font-semibold text-slate-900">Plano de Contas</h1>
          <p className="text-sm text-slate-500">Hierarquia com saldos consolidados.</p>
        </div>
        <div className="flex gap-2">
          <button
            onClick={expandAll}
            className="text-xs px-3 py-1.5 rounded-md border border-slate-300 hover:bg-slate-50"
          >Expandir tudo</button>
          <button
            onClick={collapseAll}
            className="text-xs px-3 py-1.5 rounded-md border border-slate-300 hover:bg-slate-50"
          >Recolher tudo</button>
        </div>
      </div>

      <div className="bg-white border border-slate-200 rounded-lg">
        <div className="px-4 py-2 border-b border-slate-200 grid grid-cols-12 gap-2 text-xs uppercase tracking-wide text-slate-500">
          <div className="col-span-2">Código</div>
          <div className="col-span-6">Nome</div>
          <div className="col-span-2">Tipo</div>
          <div className="col-span-2 text-right">Saldo</div>
        </div>
        <div>
          {tree.map((node) => (
            <ContaRow key={node.id} node={node} nivel={0} expandido={expandido} onToggle={toggle} />
          ))}
        </div>
      </div>
    </div>
  );
}

function ContaRow({
  node, nivel, expandido, onToggle
}: {
  node: TreeNode; nivel: number; expandido: Set<string>; onToggle: (id: string) => void;
}) {
  const aberto = expandido.has(node.id);
  const temFilhos = node.filhos.length > 0;

  return (
    <>
      <div
        className={clsx(
          'grid grid-cols-12 gap-2 px-4 py-2 border-b border-slate-100 text-sm hover:bg-slate-50',
          !node.aceitaLancamento && 'bg-slate-50/40 font-medium'
        )}
      >
        <div className="col-span-2 font-mono text-xs flex items-center" style={{ paddingLeft: nivel * 16 }}>
          {temFilhos ? (
            <button onClick={() => onToggle(node.id)} className="mr-1 text-slate-400 hover:text-slate-700">
              {aberto ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
            </button>
          ) : (
            <span className="inline-block w-4" />
          )}
          {node.codigo}
        </div>
        <div className="col-span-6 truncate">{node.nome}</div>
        <div className="col-span-2 text-xs text-slate-500">{node.tipo} · {node.natureza}</div>
        <div className={clsx(
          'col-span-2 text-right font-mono',
          node.saldo < 0 ? 'text-rose-600' : 'text-slate-900'
        )}>
          {node.saldo.toLocaleString('pt-BR', { minimumFractionDigits: 2 })}
        </div>
      </div>
      {aberto && node.filhos.map((f) => (
        <ContaRow key={f.id} node={f} nivel={nivel + 1} expandido={expandido} onToggle={onToggle} />
      ))}
    </>
  );
}
