export type TipoConta = 'Ativo' | 'Passivo' | 'Patrimonio' | 'Receita' | 'Despesa';
export type NaturezaConta = 'Devedora' | 'Credora';
export type TipoLinha = 'Debito' | 'Credito';
export type StatusLancamento = 'Rascunho' | 'Confirmado' | 'Cancelado';
export type Porte = 'Micro' | 'Pequeno' | 'Medio' | 'Grande';

export interface Empresa {
  id: string;
  cnpj: string;
  razaoSocial: string;
  nomeFantasia: string | null;
  porte: Porte;
  ativa: boolean;
  criadoEm: string;
}

export interface DadosGrandePorte {
  empresaId: string;
  faturamentoAnualMilhoes: number;
  quantidadeFuncionarios: number;
}

export interface DadosMicroEmpresa {
  empresaId: string;
  regimeTributario: string;
  cnaePrincipal: string;
}

export interface EmpresaDetalhe extends Empresa {
  dadosGrandePorte: DadosGrandePorte | null;
  dadosMicroEmpresa: DadosMicroEmpresa | null;
}

export interface EmpresaInput {
  cnpj: string;
  razaoSocial: string;
  nomeFantasia: string | null;
  porte: Porte;
  dadosGrandePorte: { faturamentoAnualMilhoes: number; quantidadeFuncionarios: number } | null;
  dadosMicroEmpresa: { regimeTributario: string; cnaePrincipal: string } | null;
}

export interface ContaComSaldo {
  id: string;
  empresaId: string;
  contaPaiId: string | null;
  codigo: string;
  nome: string;
  tipo: TipoConta;
  natureza: NaturezaConta;
  aceitaLancamento: boolean;
  ativa: boolean;
  saldo: number;
}

export interface LancamentoListItem {
  id: string;
  numero: number;
  dataLancamento: string;
  dataCompetencia: string;
  historico: string;
  valorTotal: number;
  status: StatusLancamento;
  qtdLinhas: number;
}

export interface LancamentoDetalhe {
  id: string;
  empresaId: string;
  numero: number;
  dataLancamento: string;
  dataCompetencia: string;
  historico: string;
  valorTotal: number;
  status: StatusLancamento;
  linhas: {
    id: string;
    contaId: string;
    tipo: TipoLinha;
    valor: number;
    historico: string | null;
    ordem: number;
  }[];
}

export interface LinhaInput {
  contaId: string;
  tipo: TipoLinha;
  valor: number;
  historico?: string | null;
}

export interface LancamentoInput {
  dataLancamento: string;
  dataCompetencia: string;
  historico: string;
  linhas: LinhaInput[];
  confirmar: boolean;
}
