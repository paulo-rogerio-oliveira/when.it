import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from './client';
import type {
  Empresa, EmpresaDetalhe, EmpresaInput,
  ContaComSaldo, LancamentoListItem, LancamentoDetalhe,
  LancamentoInput
} from './types';

// ----- Empresas -----
export const useEmpresas = () =>
  useQuery({
    queryKey: ['empresas'],
    queryFn: async () => (await api.get<Empresa[]>('/empresas')).data
  });

export const useEmpresa = (id: string | null) =>
  useQuery({
    queryKey: ['empresa', id],
    enabled: !!id,
    queryFn: async () => (await api.get<EmpresaDetalhe>(`/empresas/${id}`)).data
  });

export const useCriarEmpresa = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (input: EmpresaInput) =>
      (await api.post<{ id: string }>('/empresas', input)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['empresas'] })
  });
};

export const useAtualizarEmpresa = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, input }: { id: string; input: EmpresaInput }) =>
      (await api.put<{ id: string }>(`/empresas/${id}`, input)).data,
    onSuccess: (_, vars) => {
      qc.invalidateQueries({ queryKey: ['empresas'] });
      qc.invalidateQueries({ queryKey: ['empresa', vars.id] });
    }
  });
};

// ----- Plano de contas -----
export const usePlanoContas = (empresaId: string | null) =>
  useQuery({
    queryKey: ['plano-contas', empresaId],
    enabled: !!empresaId,
    queryFn: async () =>
      (await api.get<ContaComSaldo[]>(`/empresas/${empresaId}/plano-contas`)).data
  });

export const useCriarConta = (empresaId: string | null) => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (input: {
      codigo: string; nome: string; tipo: string; natureza: string;
      contaPaiId: string | null; aceitaLancamento: boolean;
    }) =>
      (await api.post(`/empresas/${empresaId}/plano-contas`, input)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['plano-contas', empresaId] })
  });
};

// ----- Lançamentos -----
export const useLancamentos = (empresaId: string | null) =>
  useQuery({
    queryKey: ['lancamentos', empresaId],
    enabled: !!empresaId,
    queryFn: async () =>
      (await api.get<{ total: number; items: LancamentoListItem[] }>(
        `/empresas/${empresaId}/lancamentos?take=200`
      )).data
  });

export const useLancamentoDetalhe = (empresaId: string | null, id: string | null) =>
  useQuery({
    queryKey: ['lancamento', empresaId, id],
    enabled: !!empresaId && !!id,
    queryFn: async () =>
      (await api.get<LancamentoDetalhe>(`/empresas/${empresaId}/lancamentos/${id}`)).data
  });

export const useCriarLancamento = (empresaId: string | null) => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (input: LancamentoInput) =>
      (await api.post(`/empresas/${empresaId}/lancamentos`, input)).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['lancamentos', empresaId] });
      qc.invalidateQueries({ queryKey: ['plano-contas', empresaId] });
    }
  });
};

export const useConfirmarLancamento = (empresaId: string | null) => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) =>
      (await api.post(`/empresas/${empresaId}/lancamentos/${id}/confirmar`)).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['lancamentos', empresaId] });
      qc.invalidateQueries({ queryKey: ['plano-contas', empresaId] });
    }
  });
};
