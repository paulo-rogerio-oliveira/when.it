using Contabilidade.Sandbox.Api.Data;
using Contabilidade.Sandbox.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Contabilidade.Sandbox.Api.Services;

public record SaldoConta(Guid ContaId, string Codigo, string Nome, decimal Saldo, NaturezaConta Natureza);

public class SaldoService
{
    private readonly ContabilidadeContext _ctx;

    public SaldoService(ContabilidadeContext ctx) { _ctx = ctx; }

    // Calcula saldo de UMA conta (somando descendentes). Considera só lançamentos
    // confirmados com data de competência ≤ dataAte (ou todos se null).
    public async Task<decimal> CalcularSaldoAsync(
        Guid contaId, DateTime? dataAte = null, CancellationToken ct = default)
    {
        var conta = await _ctx.PlanoContas.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == contaId, ct)
            ?? throw new InvalidOperationException("Conta não encontrada.");

        var ids = await GetSelfAndDescendantIdsAsync(conta.EmpresaId, contaId, ct);
        return await SomarMovimentoAsync(ids, conta.Natureza, dataAte, ct);
    }

    // Calcula saldo de TODAS as contas da empresa de uma vez (mais eficiente pra árvore).
    public async Task<IReadOnlyList<SaldoConta>> CalcularSaldosEmpresaAsync(
        Guid empresaId, DateTime? dataAte = null, CancellationToken ct = default)
    {
        var contas = await _ctx.PlanoContas.AsNoTracking()
            .Where(c => c.EmpresaId == empresaId)
            .ToListAsync(ct);

        var movimentos = await (
            from ll in _ctx.LancamentoLinhas.AsNoTracking()
            join l in _ctx.Lancamentos.AsNoTracking() on ll.LancamentoId equals l.Id
            where l.EmpresaId == empresaId
                  && l.Status == StatusLancamento.Confirmado
                  && (dataAte == null || l.DataCompetencia <= dataAte)
            select new { ll.ContaId, ll.Tipo, ll.Valor }
        ).ToListAsync(ct);

        // Saldo "próprio" (só desta conta, sem filhos).
        var saldoProprio = movimentos
            .GroupBy(m => m.ContaId)
            .ToDictionary(
                g => g.Key,
                g => g.Where(x => x.Tipo == TipoLinha.Debito).Sum(x => x.Valor)
                   - g.Where(x => x.Tipo == TipoLinha.Credito).Sum(x => x.Valor));

        // Saldo "consolidado" = saldo próprio + saldo dos filhos. Resolve via post-order.
        var byParent = contas.ToLookup(c => c.ContaPaiId);
        var consolidado = new Dictionary<Guid, decimal>();

        decimal Resolver(PlanoConta c)
        {
            if (consolidado.TryGetValue(c.Id, out var s)) return s;
            decimal total = saldoProprio.TryGetValue(c.Id, out var p) ? p : 0m;
            foreach (var filho in byParent[c.Id])
                total += Resolver(filho);
            consolidado[c.Id] = total;
            return total;
        }

        foreach (var c in contas) Resolver(c);

        return contas
            .Select(c =>
            {
                var bruto = consolidado[c.Id];
                // Ajusta sinal pela natureza: contas de natureza credora exibem
                // saldo positivo quando crédito > débito.
                var saldo = c.Natureza == NaturezaConta.Devedora ? bruto : -bruto;
                return new SaldoConta(c.Id, c.Codigo, c.Nome, Math.Round(saldo, 2), c.Natureza);
            })
            .OrderBy(s => s.Codigo, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<HashSet<Guid>> GetSelfAndDescendantIdsAsync(
        Guid empresaId, Guid rootId, CancellationToken ct)
    {
        var hierarquia = await _ctx.PlanoContas.AsNoTracking()
            .Where(c => c.EmpresaId == empresaId)
            .Select(c => new { c.Id, c.ContaPaiId })
            .ToListAsync(ct);

        var byParent = hierarquia.ToLookup(c => c.ContaPaiId);
        var result = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in byParent[current])
                if (result.Add(child.Id)) queue.Enqueue(child.Id);
        }
        return result;
    }

    private async Task<decimal> SomarMovimentoAsync(
        HashSet<Guid> contaIds, NaturezaConta natureza, DateTime? dataAte, CancellationToken ct)
    {
        var movs = await (
            from ll in _ctx.LancamentoLinhas.AsNoTracking()
            join l in _ctx.Lancamentos.AsNoTracking() on ll.LancamentoId equals l.Id
            where contaIds.Contains(ll.ContaId)
                  && l.Status == StatusLancamento.Confirmado
                  && (dataAte == null || l.DataCompetencia <= dataAte)
            select new { ll.Tipo, ll.Valor }
        ).ToListAsync(ct);

        var debitos = movs.Where(m => m.Tipo == TipoLinha.Debito).Sum(m => m.Valor);
        var creditos = movs.Where(m => m.Tipo == TipoLinha.Credito).Sum(m => m.Valor);
        var bruto = debitos - creditos;
        return Math.Round(natureza == NaturezaConta.Devedora ? bruto : -bruto, 2);
    }
}
