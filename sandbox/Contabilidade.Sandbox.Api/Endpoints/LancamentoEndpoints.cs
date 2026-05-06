using Contabilidade.Sandbox.Api.Data;
using Contabilidade.Sandbox.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Contabilidade.Sandbox.Api.Endpoints;

public static class LancamentoEndpoints
{
    public record LinhaInput(Guid ContaId, TipoLinha Tipo, decimal Valor, string? Historico);

    public record LancamentoInput(
        DateTime DataLancamento,
        DateTime DataCompetencia,
        string Historico,
        IReadOnlyList<LinhaInput> Linhas,
        bool Confirmar);

    public static void MapLancamentoEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/empresas/{empresaId:guid}/lancamentos").WithTags("Lancamentos");

        g.MapGet("/", async (
            Guid empresaId, ContabilidadeContext ctx,
            [FromQuery] int? skip, [FromQuery] int? take, CancellationToken ct) =>
        {
            var query = ctx.Lancamentos.AsNoTracking()
                .Where(l => l.EmpresaId == empresaId)
                .OrderByDescending(l => l.DataCompetencia)
                .ThenByDescending(l => l.Numero);

            var total = await query.CountAsync(ct);
            var items = await query
                .Skip(skip ?? 0)
                .Take(take ?? 50)
                .Select(l => new
                {
                    l.Id, l.Numero, l.DataLancamento, l.DataCompetencia,
                    l.Historico, l.ValorTotal, l.Status,
                    QtdLinhas = ctx.LancamentoLinhas.Count(ll => ll.LancamentoId == l.Id)
                })
                .ToListAsync(ct);

            return Results.Ok(new { Total = total, Items = items });
        });

        g.MapGet("/{id:guid}", async (Guid empresaId, Guid id, ContabilidadeContext ctx, CancellationToken ct) =>
        {
            var lanc = await ctx.Lancamentos.AsNoTracking()
                .Include(l => l.Linhas)
                .FirstOrDefaultAsync(l => l.Id == id && l.EmpresaId == empresaId, ct);
            return lanc is null ? Results.NotFound() : Results.Ok(lanc);
        });

        g.MapPost("/", async (
            Guid empresaId, [FromBody] LancamentoInput input, ContabilidadeContext ctx, CancellationToken ct) =>
        {
            if (input.Linhas is null || input.Linhas.Count < 2)
                return Results.BadRequest("Lançamento precisa ter pelo menos 2 linhas (débito e crédito).");
            if (string.IsNullOrWhiteSpace(input.Historico))
                return Results.BadRequest("Histórico é obrigatório.");

            var debitos = input.Linhas.Where(l => l.Tipo == TipoLinha.Debito).Sum(l => l.Valor);
            var creditos = input.Linhas.Where(l => l.Tipo == TipoLinha.Credito).Sum(l => l.Valor);
            if (debitos != creditos || debitos == 0)
                return Results.BadRequest($"Lançamento desbalanceado: débitos={debitos:F2} créditos={creditos:F2}.");

            var contasIds = input.Linhas.Select(l => l.ContaId).Distinct().ToList();
            var contasValidas = await ctx.PlanoContas
                .Where(c => c.EmpresaId == empresaId
                            && contasIds.Contains(c.Id)
                            && c.AceitaLancamento
                            && c.Ativa)
                .Select(c => c.Id)
                .ToListAsync(ct);
            if (contasValidas.Count != contasIds.Count)
                return Results.BadRequest("Uma ou mais contas são inválidas (inexistente, inativa ou não aceita lançamento).");

            var ultimoNumero = await ctx.Lancamentos
                .Where(l => l.EmpresaId == empresaId)
                .Select(l => (int?)l.Numero)
                .MaxAsync(ct) ?? 0;

            var lanc = new Lancamento
            {
                Id = Guid.NewGuid(),
                EmpresaId = empresaId,
                Numero = ultimoNumero + 1,
                DataLancamento = input.DataLancamento,
                DataCompetencia = input.DataCompetencia,
                Historico = input.Historico.Trim(),
                ValorTotal = debitos,
                Status = input.Confirmar ? StatusLancamento.Confirmado : StatusLancamento.Rascunho,
                CriadoEm = DateTime.UtcNow
            };
            int ordem = 1;
            foreach (var l in input.Linhas)
            {
                lanc.Linhas.Add(new LancamentoLinha
                {
                    Id = Guid.NewGuid(),
                    LancamentoId = lanc.Id,
                    ContaId = l.ContaId,
                    Tipo = l.Tipo,
                    Valor = l.Valor,
                    Historico = l.Historico,
                    Ordem = ordem++
                });
            }
            ctx.Lancamentos.Add(lanc);
            await ctx.SaveChangesAsync(ct);
            return Results.Created($"/api/empresas/{empresaId}/lancamentos/{lanc.Id}", new { lanc.Id, lanc.Numero });
        });

        g.MapPost("/{id:guid}/confirmar", async (
            Guid empresaId, Guid id, ContabilidadeContext ctx, CancellationToken ct) =>
        {
            var lanc = await ctx.Lancamentos.FirstOrDefaultAsync(
                l => l.Id == id && l.EmpresaId == empresaId, ct);
            if (lanc is null) return Results.NotFound();
            if (lanc.Status == StatusLancamento.Confirmado) return Results.NoContent();
            if (lanc.Status == StatusLancamento.Cancelado)
                return Results.BadRequest("Lançamento cancelado não pode ser confirmado.");

            lanc.Status = StatusLancamento.Confirmado;
            await ctx.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/cancelar", async (
            Guid empresaId, Guid id, ContabilidadeContext ctx, CancellationToken ct) =>
        {
            var lanc = await ctx.Lancamentos.FirstOrDefaultAsync(
                l => l.Id == id && l.EmpresaId == empresaId, ct);
            if (lanc is null) return Results.NotFound();
            lanc.Status = StatusLancamento.Cancelado;
            await ctx.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
