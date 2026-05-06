using Contabilidade.Sandbox.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Contabilidade.Sandbox.Api.Data;

// Seed idempotente: cria 2 empresas, plano de contas BR padrão e ~60 lançamentos
// balanceados nos últimos 90 dias. Roda só se o banco estiver vazio.
public static class DatabaseSeeder
{
    public static async Task SeedAsync(ContabilidadeContext ctx, CancellationToken ct = default)
    {
        // Sandbox sem migrations — cria o schema inteiro se o banco for novo.
        await ctx.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);

        // Se o banco já existia (com schema antigo), aplica as adições idempotentes.
        await AplicarMigracoesIdempotentesAsync(ctx, ct);

        if (await ctx.Empresas.AnyAsync(ct))
        {
            // Banco já populado — popula só os dados específicos de porte se ainda não existirem.
            await SeedDadosPorteParaEmpresasExistentesAsync(ctx, ct);
            return;
        }

        var empresas = new[]
        {
            new Empresa
            {
                Id = Guid.NewGuid(), Cnpj = "12.345.678/0001-90",
                RazaoSocial = "Acme Comércio e Serviços LTDA",
                NomeFantasia = "Acme",
                Porte = Porte.Micro,
                CriadoEm = DateTime.UtcNow, Ativa = true,
                DadosMicroEmpresa = new DadosMicroEmpresa
                {
                    RegimeTributario = "Simples Nacional",
                    CnaePrincipal = "4789-0/99"
                }
            },
            new Empresa
            {
                Id = Guid.NewGuid(), Cnpj = "98.765.432/0001-10",
                RazaoSocial = "Sigma Indústria S/A",
                NomeFantasia = "Sigma",
                Porte = Porte.Grande,
                CriadoEm = DateTime.UtcNow, Ativa = true,
                DadosGrandePorte = new DadosGrandePorte
                {
                    FaturamentoAnualMilhoes = 480.5m,
                    QuantidadeFuncionarios = 1240
                }
            }
        };
        ctx.Empresas.AddRange(empresas);
        await ctx.SaveChangesAsync(ct);

        foreach (var emp in empresas)
        {
            var contas = BuildPlanoContas(emp.Id);
            ctx.PlanoContas.AddRange(contas);
            await ctx.SaveChangesAsync(ct);

            var folhas = contas.Where(c => c.AceitaLancamento).ToDictionary(c => c.Codigo, c => c.Id);
            var lancamentos = BuildLancamentos(emp.Id, folhas);
            ctx.Lancamentos.AddRange(lancamentos);
            await ctx.SaveChangesAsync(ct);
        }
    }

    // Migrations manuais idempotentes — adicionam o que falta sem dropar dados existentes.
    private static async Task AplicarMigracoesIdempotentesAsync(ContabilidadeContext ctx, CancellationToken ct)
    {
        await ctx.Database.ExecuteSqlRawAsync(@"
IF COL_LENGTH('empresas', 'Porte') IS NULL
    ALTER TABLE empresas ADD Porte NVARCHAR(20) NOT NULL CONSTRAINT DF_empresas_Porte DEFAULT 'Pequeno';

IF OBJECT_ID('dados_grande_porte', 'U') IS NULL
    CREATE TABLE dados_grande_porte (
        EmpresaId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
            CONSTRAINT FK_dados_grande_porte_empresas REFERENCES empresas(Id) ON DELETE CASCADE,
        FaturamentoAnualMilhoes DECIMAL(18,2) NOT NULL,
        QuantidadeFuncionarios INT NOT NULL
    );

IF OBJECT_ID('dados_microempresa', 'U') IS NULL
    CREATE TABLE dados_microempresa (
        EmpresaId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
            CONSTRAINT FK_dados_microempresa_empresas REFERENCES empresas(Id) ON DELETE CASCADE,
        RegimeTributario NVARCHAR(40) NOT NULL,
        CnaePrincipal NVARCHAR(20) NOT NULL
    );
", ct);
    }

    // Caso o banco já exista (de antes do feature de porte), garante que Acme = Micro
    // e Sigma = Grande recebem dados de porte (idempotente — não duplica se já existir).
    private static async Task SeedDadosPorteParaEmpresasExistentesAsync(ContabilidadeContext ctx, CancellationToken ct)
    {
        var acme = await ctx.Empresas.FirstOrDefaultAsync(e => e.Cnpj == "12.345.678/0001-90", ct);
        if (acme is not null)
        {
            if (acme.Porte != Porte.Micro)
            {
                acme.Porte = Porte.Micro;
            }
            var jaTem = await ctx.DadosMicroEmpresa.AnyAsync(d => d.EmpresaId == acme.Id, ct);
            if (!jaTem)
            {
                ctx.DadosMicroEmpresa.Add(new DadosMicroEmpresa
                {
                    EmpresaId = acme.Id,
                    RegimeTributario = "Simples Nacional",
                    CnaePrincipal = "4789-0/99"
                });
            }
        }

        var sigma = await ctx.Empresas.FirstOrDefaultAsync(e => e.Cnpj == "98.765.432/0001-10", ct);
        if (sigma is not null)
        {
            if (sigma.Porte != Porte.Grande)
            {
                sigma.Porte = Porte.Grande;
            }
            var jaTem = await ctx.DadosGrandePorte.AnyAsync(d => d.EmpresaId == sigma.Id, ct);
            if (!jaTem)
            {
                ctx.DadosGrandePorte.Add(new DadosGrandePorte
                {
                    EmpresaId = sigma.Id,
                    FaturamentoAnualMilhoes = 480.5m,
                    QuantidadeFuncionarios = 1240
                });
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    private static List<PlanoConta> BuildPlanoContas(Guid empresaId)
    {
        var now = DateTime.UtcNow;
        var contas = new List<PlanoConta>();

        PlanoConta Add(string codigo, string nome, TipoConta tipo, NaturezaConta nat, string? paiCodigo, bool aceita)
        {
            var pai = paiCodigo is null ? (PlanoConta?)null : contas.First(c => c.Codigo == paiCodigo);
            var c = new PlanoConta
            {
                Id = Guid.NewGuid(),
                EmpresaId = empresaId,
                ContaPaiId = pai?.Id,
                Codigo = codigo,
                Nome = nome,
                Tipo = tipo,
                Natureza = nat,
                AceitaLancamento = aceita,
                Ativa = true,
                CriadoEm = now
            };
            contas.Add(c);
            return c;
        }

        // 1 - Ativo
        Add("1", "ATIVO", TipoConta.Ativo, NaturezaConta.Devedora, null, false);
        Add("1.1", "Ativo Circulante", TipoConta.Ativo, NaturezaConta.Devedora, "1", false);
        Add("1.1.01", "Caixa e Equivalentes", TipoConta.Ativo, NaturezaConta.Devedora, "1.1", false);
        Add("1.1.01.001", "Caixa Geral", TipoConta.Ativo, NaturezaConta.Devedora, "1.1.01", true);
        Add("1.1.01.002", "Banco Conta Corrente", TipoConta.Ativo, NaturezaConta.Devedora, "1.1.01", true);
        Add("1.1.02", "Contas a Receber", TipoConta.Ativo, NaturezaConta.Devedora, "1.1", false);
        Add("1.1.02.001", "Clientes", TipoConta.Ativo, NaturezaConta.Devedora, "1.1.02", true);
        Add("1.1.03", "Estoques", TipoConta.Ativo, NaturezaConta.Devedora, "1.1", false);
        Add("1.1.03.001", "Mercadorias", TipoConta.Ativo, NaturezaConta.Devedora, "1.1.03", true);

        // 2 - Passivo
        Add("2", "PASSIVO", TipoConta.Passivo, NaturezaConta.Credora, null, false);
        Add("2.1", "Passivo Circulante", TipoConta.Passivo, NaturezaConta.Credora, "2", false);
        Add("2.1.01", "Fornecedores", TipoConta.Passivo, NaturezaConta.Credora, "2.1", false);
        Add("2.1.01.001", "Contas a Pagar", TipoConta.Passivo, NaturezaConta.Credora, "2.1.01", true);
        Add("2.1.02", "Obrigações Trabalhistas", TipoConta.Passivo, NaturezaConta.Credora, "2.1", false);
        Add("2.1.02.001", "Salários a Pagar", TipoConta.Passivo, NaturezaConta.Credora, "2.1.02", true);

        // 3 - Patrimônio Líquido
        Add("3", "PATRIMÔNIO LÍQUIDO", TipoConta.Patrimonio, NaturezaConta.Credora, null, false);
        Add("3.1", "Capital", TipoConta.Patrimonio, NaturezaConta.Credora, "3", false);
        Add("3.1.01", "Capital Social", TipoConta.Patrimonio, NaturezaConta.Credora, "3.1", true);

        // 4 - Receitas
        Add("4", "RECEITAS", TipoConta.Receita, NaturezaConta.Credora, null, false);
        Add("4.1", "Receitas Operacionais", TipoConta.Receita, NaturezaConta.Credora, "4", false);
        Add("4.1.01", "Vendas", TipoConta.Receita, NaturezaConta.Credora, "4.1", false);
        Add("4.1.01.001", "Receita de Vendas", TipoConta.Receita, NaturezaConta.Credora, "4.1.01", true);

        // 5 - Despesas
        Add("5", "DESPESAS", TipoConta.Despesa, NaturezaConta.Devedora, null, false);
        Add("5.1", "Despesas Operacionais", TipoConta.Despesa, NaturezaConta.Devedora, "5", false);
        Add("5.1.01", "Administrativas", TipoConta.Despesa, NaturezaConta.Devedora, "5.1", false);
        Add("5.1.01.001", "Salários", TipoConta.Despesa, NaturezaConta.Devedora, "5.1.01", true);
        Add("5.1.01.002", "Aluguel", TipoConta.Despesa, NaturezaConta.Devedora, "5.1.01", true);
        Add("5.1.01.003", "Energia e Água", TipoConta.Despesa, NaturezaConta.Devedora, "5.1.01", true);
        Add("5.1.02", "Comerciais", TipoConta.Despesa, NaturezaConta.Devedora, "5.1", false);
        Add("5.1.02.001", "Comissões", TipoConta.Despesa, NaturezaConta.Devedora, "5.1.02", true);

        return contas;
    }

    private static List<Lancamento> BuildLancamentos(Guid empresaId, IReadOnlyDictionary<string, Guid> folhas)
    {
        // Seed determinístico (seed fixo) pra reprodutibilidade.
        var rng = new Random(42 + empresaId.GetHashCode());
        var inicio = DateTime.UtcNow.Date.AddDays(-90);
        var lancamentos = new List<Lancamento>();
        int numero = 1;

        // 1) Aporte de capital inicial.
        lancamentos.Add(MontarLancamento(numero++, inicio, "Integralização de capital social",
            (folhas["1.1.01.002"], TipoLinha.Debito, 50_000m),
            (folhas["3.1.01"], TipoLinha.Credito, 50_000m)));

        // 2) Compras, vendas, pagamentos, despesas — distribuídos nos últimos 90 dias.
        var templates = new (string Hist, string DebitoCodigo, string CreditoCodigo, decimal Min, decimal Max)[]
        {
            ("Venda à vista",                 "1.1.01.002", "4.1.01.001", 800m, 6_500m),
            ("Venda a prazo",                 "1.1.02.001", "4.1.01.001", 1_200m, 9_500m),
            ("Recebimento de cliente",        "1.1.01.002", "1.1.02.001", 500m, 4_000m),
            ("Compra de mercadorias a prazo", "1.1.03.001", "2.1.01.001", 600m, 5_500m),
            ("Pagamento a fornecedor",        "2.1.01.001", "1.1.01.002", 400m, 3_500m),
            ("Pagamento de aluguel",          "5.1.01.002", "1.1.01.002", 1_200m, 1_200m),
            ("Pagamento de energia/água",     "5.1.01.003", "1.1.01.002", 280m, 720m),
            ("Pagamento de salários",         "5.1.01.001", "1.1.01.002", 3_500m, 8_500m),
            ("Pagamento de comissões",        "5.1.02.001", "1.1.01.002", 200m, 1_400m),
            ("Saque para caixa",              "1.1.01.001", "1.1.01.002", 200m, 1_500m)
        };

        for (int i = 0; i < 60; i++)
        {
            var t = templates[rng.Next(templates.Length)];
            var dia = inicio.AddDays(rng.Next(1, 90));
            var valor = Math.Round((decimal)(rng.NextDouble() * (double)(t.Max - t.Min)) + t.Min, 2);
            lancamentos.Add(MontarLancamento(numero++, dia, t.Hist,
                (folhas[t.DebitoCodigo], TipoLinha.Debito, valor),
                (folhas[t.CreditoCodigo], TipoLinha.Credito, valor)));
        }

        return lancamentos;

        Lancamento MontarLancamento(int numero, DateTime data, string historico,
            params (Guid ContaId, TipoLinha Tipo, decimal Valor)[] linhas)
        {
            var lanc = new Lancamento
            {
                Id = Guid.NewGuid(),
                EmpresaId = empresaId,
                Numero = numero,
                DataLancamento = data,
                DataCompetencia = data,
                Historico = historico,
                ValorTotal = linhas.Where(l => l.Tipo == TipoLinha.Debito).Sum(l => l.Valor),
                Status = StatusLancamento.Confirmado,
                CriadoEm = DateTime.UtcNow
            };
            int ordem = 1;
            foreach (var l in linhas)
            {
                lanc.Linhas.Add(new LancamentoLinha
                {
                    Id = Guid.NewGuid(),
                    LancamentoId = lanc.Id,
                    ContaId = l.ContaId,
                    Tipo = l.Tipo,
                    Valor = l.Valor,
                    Ordem = ordem++
                });
            }
            return lanc;
        }
    }
}
