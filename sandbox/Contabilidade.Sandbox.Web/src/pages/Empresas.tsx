import { useEffect, useState } from 'react';
import { Pencil, Plus, X } from 'lucide-react';
import { clsx } from 'clsx';
import {
  useAtualizarEmpresa, useCriarEmpresa, useEmpresa, useEmpresas
} from '../api/hooks';
import type { EmpresaInput, Porte } from '../api/types';

const portes: Porte[] = ['Micro', 'Pequeno', 'Medio', 'Grande'];
const regimes = ['Simples Nacional', 'MEI', 'Lucro Presumido'];

interface FormState {
  cnpj: string;
  razaoSocial: string;
  nomeFantasia: string;
  porte: Porte;
  faturamentoAnualMilhoes: number;
  quantidadeFuncionarios: number;
  regimeTributario: string;
  cnaePrincipal: string;
}

const emptyForm: FormState = {
  cnpj: '', razaoSocial: '', nomeFantasia: '', porte: 'Pequeno',
  faturamentoAnualMilhoes: 0, quantidadeFuncionarios: 0,
  regimeTributario: 'Simples Nacional', cnaePrincipal: ''
};

export default function Empresas() {
  const { data, isLoading } = useEmpresas();
  const [editingId, setEditingId] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);

  function startCriar() {
    setEditingId(null);
    setShowForm(true);
  }

  function startEditar(id: string) {
    setEditingId(id);
    setShowForm(true);
  }

  function close() {
    setShowForm(false);
    setEditingId(null);
  }

  return (
    <div className="space-y-6">
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-xl font-semibold text-slate-900">Empresas</h1>
          <p className="text-sm text-slate-500">Cadastro multi-empresa com dados específicos por porte.</p>
        </div>
        {!showForm && (
          <button
            onClick={startCriar}
            className="bg-slate-900 text-white text-sm rounded-md px-4 py-2 hover:bg-slate-800 flex items-center gap-2"
          >
            <Plus className="h-4 w-4" /> Nova empresa
          </button>
        )}
      </div>

      {showForm && (
        <EmpresaForm
          empresaId={editingId}
          onCancel={close}
          onSaved={close}
        />
      )}

      <div className="bg-white border border-slate-200 rounded-lg overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-slate-50 text-slate-600">
            <tr>
              <th className="px-4 py-2 text-left font-medium">CNPJ</th>
              <th className="px-4 py-2 text-left font-medium">Razão Social</th>
              <th className="px-4 py-2 text-left font-medium">Fantasia</th>
              <th className="px-4 py-2 text-left font-medium">Porte</th>
              <th className="px-4 py-2 text-right font-medium">Ações</th>
            </tr>
          </thead>
          <tbody>
            {isLoading && (
              <tr><td className="px-4 py-4 text-slate-400" colSpan={5}>carregando…</td></tr>
            )}
            {data?.map((e) => (
              <tr key={e.id} className="border-t border-slate-100">
                <td className="px-4 py-2 font-mono text-xs">{e.cnpj}</td>
                <td className="px-4 py-2">{e.razaoSocial}</td>
                <td className="px-4 py-2 text-slate-600">{e.nomeFantasia ?? '—'}</td>
                <td className="px-4 py-2">
                  <PorteBadge porte={e.porte} />
                </td>
                <td className="px-4 py-2 text-right">
                  <button
                    onClick={() => startEditar(e.id)}
                    className="text-xs text-slate-700 hover:text-slate-900 inline-flex items-center gap-1"
                  >
                    <Pencil className="h-3.5 w-3.5" /> Editar
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function PorteBadge({ porte }: { porte: Porte }) {
  const map: Record<Porte, string> = {
    Micro: 'bg-amber-100 text-amber-700',
    Pequeno: 'bg-slate-100 text-slate-700',
    Medio: 'bg-sky-100 text-sky-700',
    Grande: 'bg-emerald-100 text-emerald-700'
  };
  return (
    <span className={clsx('px-2 py-0.5 rounded text-xs', map[porte])}>{porte}</span>
  );
}

function EmpresaForm({
  empresaId, onCancel, onSaved
}: { empresaId: string | null; onCancel: () => void; onSaved: () => void }) {
  const isEdit = !!empresaId;
  const { data: detalhe, isLoading: loadingDetalhe } = useEmpresa(empresaId);
  const criar = useCriarEmpresa();
  const atualizar = useAtualizarEmpresa();
  const [form, setForm] = useState<FormState>(emptyForm);
  const [erro, setErro] = useState<string | null>(null);

  useEffect(() => {
    if (!isEdit) { setForm(emptyForm); return; }
    if (!detalhe) return;
    setForm({
      cnpj: detalhe.cnpj,
      razaoSocial: detalhe.razaoSocial,
      nomeFantasia: detalhe.nomeFantasia ?? '',
      porte: detalhe.porte,
      faturamentoAnualMilhoes: detalhe.dadosGrandePorte?.faturamentoAnualMilhoes ?? 0,
      quantidadeFuncionarios: detalhe.dadosGrandePorte?.quantidadeFuncionarios ?? 0,
      regimeTributario: detalhe.dadosMicroEmpresa?.regimeTributario ?? 'Simples Nacional',
      cnaePrincipal: detalhe.dadosMicroEmpresa?.cnaePrincipal ?? ''
    });
  }, [isEdit, detalhe]);

  const pending = criar.isPending || atualizar.isPending;

  function buildInput(): EmpresaInput {
    return {
      cnpj: form.cnpj,
      razaoSocial: form.razaoSocial,
      nomeFantasia: form.nomeFantasia || null,
      porte: form.porte,
      dadosGrandePorte: form.porte === 'Grande'
        ? {
            faturamentoAnualMilhoes: Number(form.faturamentoAnualMilhoes),
            quantidadeFuncionarios: Number(form.quantidadeFuncionarios)
          }
        : null,
      dadosMicroEmpresa: form.porte === 'Micro'
        ? {
            regimeTributario: form.regimeTributario,
            cnaePrincipal: form.cnaePrincipal
          }
        : null
    };
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setErro(null);
    try {
      const input = buildInput();
      if (isEdit && empresaId) {
        await atualizar.mutateAsync({ id: empresaId, input });
      } else {
        await criar.mutateAsync(input);
      }
      onSaved();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: string } })?.response?.data
                ?? (e instanceof Error ? e.message : 'Falha ao salvar');
      setErro(typeof msg === 'string' ? msg : JSON.stringify(msg));
    }
  }

  if (isEdit && loadingDetalhe) {
    return (
      <div className="bg-white border border-slate-200 rounded-lg p-4 text-sm text-slate-400">
        carregando empresa…
      </div>
    );
  }

  return (
    <form onSubmit={handleSubmit} className="bg-white border border-slate-200 rounded-lg p-4 space-y-4 max-w-3xl">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold text-slate-900">
          {isEdit ? 'Editar empresa' : 'Nova empresa'}
        </h2>
        <button type="button" onClick={onCancel}
                className="text-slate-400 hover:text-slate-700">
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="grid grid-cols-2 gap-3">
        <Field label="CNPJ" value={form.cnpj}
               onChange={(v) => setForm({ ...form, cnpj: v })} required />
        <Field label="Razão Social" value={form.razaoSocial}
               onChange={(v) => setForm({ ...form, razaoSocial: v })} required />
      </div>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Nome Fantasia" value={form.nomeFantasia}
               onChange={(v) => setForm({ ...form, nomeFantasia: v })} />
        <label className="block text-sm">
          <span className="text-slate-700">Porte <span className="text-rose-500">*</span></span>
          <select
            value={form.porte}
            onChange={(e) => setForm({ ...form, porte: e.target.value as Porte })}
            className="mt-1 w-full rounded-md border border-slate-300 px-3 py-1.5"
          >
            {portes.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </label>
      </div>

      {form.porte === 'Grande' && (
        <fieldset className="border border-emerald-200 bg-emerald-50/40 rounded-md p-3 space-y-3">
          <legend className="text-xs font-semibold text-emerald-700 px-1">Dados de Grande Porte</legend>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Faturamento anual (R$ milhões)" type="number" step="0.01"
                   value={String(form.faturamentoAnualMilhoes)}
                   onChange={(v) => setForm({ ...form, faturamentoAnualMilhoes: Number(v) })} required />
            <Field label="Quantidade de funcionários" type="number" step="1"
                   value={String(form.quantidadeFuncionarios)}
                   onChange={(v) => setForm({ ...form, quantidadeFuncionarios: Number(v) })} required />
          </div>
        </fieldset>
      )}

      {form.porte === 'Micro' && (
        <fieldset className="border border-amber-200 bg-amber-50/40 rounded-md p-3 space-y-3">
          <legend className="text-xs font-semibold text-amber-700 px-1">Dados de Microempresa</legend>
          <div className="grid grid-cols-2 gap-3">
            <label className="block text-sm">
              <span className="text-slate-700">Regime Tributário <span className="text-rose-500">*</span></span>
              <select
                value={form.regimeTributario}
                onChange={(e) => setForm({ ...form, regimeTributario: e.target.value })}
                className="mt-1 w-full rounded-md border border-slate-300 px-3 py-1.5"
              >
                {regimes.map((r) => <option key={r} value={r}>{r}</option>)}
              </select>
            </label>
            <Field label="CNAE Principal" value={form.cnaePrincipal}
                   onChange={(v) => setForm({ ...form, cnaePrincipal: v })} required />
          </div>
        </fieldset>
      )}

      {erro && <div className="text-sm text-rose-600">{erro}</div>}

      <div className="flex gap-2">
        <button type="submit" disabled={pending}
                className="bg-slate-900 text-white text-sm rounded-md px-4 py-2 hover:bg-slate-800 disabled:opacity-50">
          {pending ? 'Salvando…' : isEdit ? 'Salvar alterações' : 'Cadastrar empresa'}
        </button>
        <button type="button" onClick={onCancel}
                className="text-sm rounded-md border border-slate-300 px-4 py-2 hover:bg-slate-50">
          Cancelar
        </button>
      </div>
    </form>
  );
}

function Field({
  label, value, onChange, type = 'text', step, required
}: {
  label: string; value: string; onChange: (v: string) => void;
  type?: string; step?: string; required?: boolean
}) {
  return (
    <label className="block text-sm">
      <span className="text-slate-700">{label}{required && <span className="text-rose-500"> *</span>}</span>
      <input
        type={type} step={step} required={required} value={value}
        onChange={(e) => onChange(e.target.value)}
        className="mt-1 w-full rounded-md border border-slate-300 px-3 py-1.5"
      />
    </label>
  );
}
