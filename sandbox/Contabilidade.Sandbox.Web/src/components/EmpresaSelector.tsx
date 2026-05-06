import { useEffect } from 'react';
import { useEmpresas } from '../api/hooks';
import { useEmpresaStore } from '../store/empresa';

export default function EmpresaSelector() {
  const { data: empresas, isLoading } = useEmpresas();
  const empresaId = useEmpresaStore((s) => s.empresaId);
  const setEmpresaId = useEmpresaStore((s) => s.setEmpresaId);

  // Auto-seleciona a primeira se nada estiver salvo.
  useEffect(() => {
    if (!empresaId && empresas && empresas.length > 0) {
      setEmpresaId(empresas[0].id);
    }
  }, [empresas, empresaId, setEmpresaId]);

  if (isLoading) return <div className="text-xs text-slate-400">carregando empresas…</div>;
  if (!empresas || empresas.length === 0)
    return <div className="text-xs text-slate-400">nenhuma empresa cadastrada</div>;

  return (
    <div className="flex items-center gap-2">
      <label className="text-xs text-slate-500">Empresa</label>
      <select
        value={empresaId ?? ''}
        onChange={(e) => setEmpresaId(e.target.value || null)}
        className="rounded-md border border-slate-300 bg-white px-2 py-1 text-sm"
      >
        {empresas.map((e) => (
          <option key={e.id} value={e.id}>
            {e.nomeFantasia ?? e.razaoSocial}
          </option>
        ))}
      </select>
    </div>
  );
}
