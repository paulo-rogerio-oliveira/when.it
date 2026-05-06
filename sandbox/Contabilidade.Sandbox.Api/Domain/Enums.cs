namespace Contabilidade.Sandbox.Api.Domain;

public enum TipoConta
{
    Ativo = 1,
    Passivo = 2,
    Patrimonio = 3,
    Receita = 4,
    Despesa = 5
}

public enum NaturezaConta
{
    Devedora = 1,  // Ativo, Despesa
    Credora = 2    // Passivo, Patrimônio, Receita
}

public enum TipoLinha
{
    Debito = 1,
    Credito = 2
}

public enum StatusLancamento
{
    Rascunho = 1,
    Confirmado = 2,
    Cancelado = 3
}

public enum Porte
{
    Micro = 1,
    Pequeno = 2,
    Medio = 3,
    Grande = 4
}
