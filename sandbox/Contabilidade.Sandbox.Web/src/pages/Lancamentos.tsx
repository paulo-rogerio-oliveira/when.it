import { useMemo, useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { useEmpresaStore } from '../store/empresa';
import {
  useConfirmarLancamento, useCriarLancamento, useLancamentos, usePlanoContas
} from '../api/hooks';
import type { LinhaInput, TipoLinha } from '../api/types';

export default function Lancamentos() {
  const empresaId = useEmpresaStore((s) => s.empresaId);
  const { data: contas } = usePlanoContas(empresaId);
  const { data: lancamentos } = useLancamentos(empresaId);
  const criar = useCriarLancamento(empresaId);
  const confirmar = useConfirmarLancamento(empresaId);

  const folhas = useMemo(
    () => (contas ?? []).filter((c) => c.aceitaLancamento && c.ativa),
    [contas]
  );

  const hoje = new Date().toISOString().slice(0, 10);
  const [form, setForm] = useState({
    dataLancamento: hoje,
    dataCompetencia: hoje,
    historico: '',
    confirmar: true,
    linhas: [
      { contaId: '', tipo: 'Debito' as TipoLinha, valor: 0, historico: '' },
      { contaId: '', tipo: 'Credito' as TipoLinha, valor: 0, historico: '' }
    ] as (LinhaInput & { historico: string })[]
  });
  const [erro, setErro] = useState<string | null>(null);

  const totDebito = form.linhas.filter((l) => l.tipo === 'Debito').reduce((s, l) => s + Number(l.valor || 0), 0);
  const totCredito = form.linhas.filter((l) => l.tipo === 'Credito').reduce((s, l) => s + Number(l.valor || 0), 0);
  const balanceado = totDebito === totCredito && totDebito > 0;

  function setLinha(i: number, patch: Partial<(typeof form.linhas)[number]>) {
    const next = [...form.linhas];
    next[i] = { ...next[i], ...patch };
    setForm({ ...form, linhas: next });
  }

  function addLinha() {
    setForm({
      ...form,
      linhas: [...form.linhas, { contaId: '', tipo: 'Debito', valor: 0, historico: '' }]
    });
  }

  function removeLinha(i: number) {
    if (form.linhas.length <= 2) return;
    setForm({ ...form, linhas: form.linhas.filter((_, idx) => idx !== i) });
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setErro(null);
    if (!balanceado) { setErro('Lançamento desbalanceado: débito ≠ crédito.'); return; }
    if (form.linhas.some((l) => !l.contaId)) { setErro('Selecione a conta em todas as linhas.'); return; }

    try {
      await criar.mutateAsync({
        dataLancamento: new Date(form.dataLancamento).toISOString(),
        dataCompetencia: new Date(form.dataCompetencia).toISOString(),
        historico: form.historico,
        confirmar: form.confirmar,
        linhas: form.linhas.map((l) => ({
          contaId: l.contaId,
          tipo: l.tipo,
          valor: Number(l.valor),
          historico: l.historico || null
        }))
      });
      setForm({
        dataLancamento: hoje,
        dataCompetencia: hoje,
        historico: '',
        confirmar: true,
        linhas: [
          { contaId: '', tipo: 'Debito', valor: 0, historico: '' },
          { contaId: '', tipo: 'Credito', valor: 0, historico: '' }
        ]
      });
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: string } })?.response?.data
                ?? (e instanceof Error ? e.message : 'Falha ao criar lançamento');
      setErro(typeof msg === 'string' ? msg : JSON.stringify(msg));
    }
  }

  if (!empresaId) return <div className="text-sm text-slate-500">Selecione uma empresa.</div>;

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-slate-900">Lançamentos contábeis</h1>
        <p className="text-sm text-slate-500">
          Crie um lançamento de partidas dobradas. O sistema valida que o total de débitos = total de créditos.
        </p>
      </div>

      <form onSubmit={handleSubmit} className="bg-white border border-slate-200 rounded-lg p-4 space-y-4">
        <div className="grid grid-cols-3 gap-3">
          <Field label="Data do lançamento" type="date" value={form.dataLancamento}
                 onChange={(v) => setForm({ ...form, dataLancamento: v })} />
          <Field label="Data de competência" type="date" value={form.dataCompetencia}
                 onChange={(v) => setForm({ ...form, dataCompetencia: v })} />
          <label className="block text-sm">
            <span className="text-slate-700">Confirmar ao salvar</span>
            <select
              value={form.confirmar ? '1' : '0'}
              onChange={(e) => setForm({ ...form, confirmar: e.target.value === '1' })}
              className="mt-1 w-full rounded-md border border-slate-300 px-3 py-1.5"
            >
              <option value="1">Sim (Confirmado)</option>
              <option value="0">Não (Rascunho)</option>
            </select>
          </label>
        </div>

        <Field label="Histórico" value={form.historico}
               onChange={(v) => setForm({ ...form, historico: v })} required />

        <div className="border-t border-slate-200 pt-3">
          <div className="flex items-center justify-between mb-2">
            <h3 className="text-sm font-semibold text-slate-700">Linhas</h3>
            <button type="button" onClick={addLinha}
                    className="text-xs flex items-center gap-1 px-2 py-1 rounded-md border border-slate-300 hover:bg-slate-50">
              <Plus className="h-3.5 w-3.5" /> Adicionar linha
            </button>
          </div>

          <div className="space-y-2">
            {form.linhas.map((l, i) => (
              <div key={i} className="grid grid-cols-12 gap-2">
                <select
                  value={l.contaId}
                  onChange={(e) => setLinha(i, { contaId: e.target.value })}
                  className="col-span-5 rounded-md border border-slate-300 px-2 py-1.5 text-sm"
                  required
                >
                  <option value="">— escolha a conta —</option>
                  {folhas.map((c) => (
                    <option key={c.id} value={c.id}>{c.codigo} — {c.nome}</option>
                  ))}
                </select>
                <select
                  value={l.tipo}
                  onChange={(e) => setLinha(i, { tipo: e.target.value as TipoLinha })}
                  className="col-span-2 rounded-md border border-slate-300 px-2 py-1.5 text-sm"
                >
                  <option value="Debito">Débito</option>
                  <option value="Credito">Crédito</option>
                </select>
                <input
                  type="number" step="0.01" min="0" value={l.valor}
                  onChange={(e) => setLinha(i, { valor: Number(e.target.value) })}
                  className="col-span-2 rounded-md border border-slate-300 px-2 py-1.5 text-sm font-mono text-right"
                />
                <input
                  type="text" placeholder="histórico (opcional)" value={l.historico}
                  onChange={(e) => setLinha(i, { historico: e.target.value })}
                  className="col-span-2 rounded-md border border-slate-300 px-2 py-1.5 text-sm"
                />
                <button
                  type="button" onClick={() => removeLinha(i)}
                  disabled={form.linhas.length <= 2}
                  className="col-span-1 rounded-md text-rose-600 hover:bg-rose-50 disabled:opacity-30 flex items-center justify-center"
                >
                  <Trash2 className="h-4 w-4" />
                </button>
              </div>
            ))}
          </div>

          <div className="mt-3 grid grid-cols-12 gap-2 text-xs text-slate-600">
            <div className="col-span-7 text-right">Totais:</div>
            <div className="col-span-2 font-mono">D {totDebito.toFixed(2)}</div>
            <div className="col-span-2 font-mono">C {totCredito.toFixed(2)}</div>
            <div className={`col-span-1 font-medium ${balanceado ? 'text-emerald-600' : 'text-rose-600'}`}>
              {balanceado ? 'OK' : 'OFF'}
            </div>
          </div>
        </div>

        {erro && <div className="text-sm text-rose-600">{erro}</div>}
        <button
          type="submit"
          disabled={criar.isPending}
          className="bg-slate-900 text-white text-sm rounded-md px-4 py-2 hover:bg-slate-800 disabled:opacity-50"
        >
          {criar.isPending ? 'Salvando…' : 'Salvar lançamento'}
        </button>
      </form>

      <div className="bg-white border border-slate-200 rounded-lg overflow-hidden">
        <div className="px-4 py-3 border-b border-slate-200">
          <h2 className="text-sm font-semibold text-slate-900">Histórico</h2>
        </div>
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-600">
            <tr>
              <th className="px-4 py-2 text-left font-medium">#</th>
              <th className="px-4 py-2 text-left font-medium">Competência</th>
              <th className="px-4 py-2 text-left font-medium">Histórico</th>
              <th className="px-4 py-2 text-right font-medium">Valor</th>
              <th className="px-4 py-2 text-left font-medium">Status</th>
              <th className="px-4 py-2 text-right font-medium">Ações</th>
            </tr>
          </thead>
          <tbody>
            {lancamentos?.items.map((l) => (
              <tr key={l.id} className="border-t border-slate-100">
                <td className="px-4 py-2 font-mono text-xs">{l.numero}</td>
                <td className="px-4 py-2">{new Date(l.dataCompetencia).toLocaleDateString('pt-BR')}</td>
                <td className="px-4 py-2">{l.historico}</td>
                <td className="px-4 py-2 text-right font-mono">
                  {l.valorTotal.toLocaleString('pt-BR', { minimumFractionDigits: 2 })}
                </td>
                <td className="px-4 py-2">
                  <span className={`px-2 py-0.5 rounded text-xs ${
                    l.status === 'Confirmado' ? 'bg-emerald-100 text-emerald-700'
                    : l.status === 'Rascunho' ? 'bg-amber-100 text-amber-700'
                    : 'bg-rose-100 text-rose-700'
                  }`}>{l.status}</span>
                </td>
                <td className="px-4 py-2 text-right">
                  {l.status === 'Rascunho' && (
                    <button
                      onClick={() => confirmar.mutate(l.id)}
                      className="text-xs text-slate-700 hover:text-slate-900"
                    >Confirmar</button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Field({
  label, value, onChange, type = 'text', required
}: { label: string; value: string; onChange: (v: string) => void; type?: string; required?: boolean }) {
  return (
    <label className="block text-sm">
      <span className="text-slate-700">{label}{required && <span className="text-rose-500"> *</span>}</span>
      <input
        type={type} required={required} value={value}
        onChange={(e) => onChange(e.target.value)}
        className="mt-1 w-full rounded-md border border-slate-300 px-3 py-1.5"
      />
    </label>
  );
}
