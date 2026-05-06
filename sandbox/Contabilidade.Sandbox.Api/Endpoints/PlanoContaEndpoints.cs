using Contabilidade.Sandbox.Api.Data;
using Contabilidade.Sandbox.Api.Domain;
using Contabilidade.Sandbox.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Contabilidade.Sandbox.Api.Endpoints;

public static class PlanoContaEndpoints
{
    public record ContaInput(
        string Codigo, string Nome, TipoConta Tipo, NaturezaConta Natureza,
        Guid? ContaPaiId, bool AceitaLancamento);

    public record ContaComSaldo(
        Guid Id, Guid EmpresaId, Guid? ContaPaiId, string Codigo, string Nome,
        TipoConta Tipo, NaturezaConta Natureza, bool AceitaLancamento, bool Ativa, decimal Saldo);

    public static void MapPlanoContaEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/empresas/{empresaId:guid}/plano-contas").WithTags("PlanoContas");

        g.MapGet("/", async (
            Guid empresaId, ContabilidadeContext ctx, SaldoService saldos,
            [FromQuery] DateTime? dataAte, CancellationToken ct) =>
        {
            var contas = await ctx.PlanoContas.AsNoTracking()
                .Where(c => c.EmpresaId == empresaId)
                .OrderBy(c => c.Codigo)
                .ToListAsync(ct);

            var saldosResolved = await saldos.CalcularSaldosEmpresaAsync(empresaId, dataAte, ct);
            var byId = saldosResolved.ToDictionary(s => s.ContaId);

            return contas.Select(c => new ContaComSaldo(
                c.Id, c.EmpresaId, c.ContaPaiId, c.Codigo, c.Nome,
                c.Tipo, c.Natureza, c.AceitaLancamento, c.Ativa,
                byId.TryGetValue(c.Id, out var s) ? s.Saldo : 0m));
        });

        g.MapPost("/", async (
            Guid empresaId, [FromBody] ContaInput input, ContabilidadeContext ctx, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Codigo) || string.IsNullOrWhiteSpace(input.Nome))
                return Results.BadRequest("Código e Nome são obrigatórios.");

            var existe = await ctx.PlanoContas.AnyAsync(
                c => c.EmpresaId == empresaId && c.Codigo == input.Codigo, ct);
            if (existe) return Results.Conflict("Já existe uma conta com este código nesta empresa.");

            var conta = new PlanoConta
            {
                Id = Guid.NewGuid(),
                EmpresaId = empresaId,
                ContaPaiId = input.ContaPaiId,
                Codigo = input.Codigo.Trim(),
                Nome = input.Nome.Trim(),
                Tipo = input.Tipo,
                Natureza = input.Natureza,
                AceitaLancamento = input.AceitaLancamento,
                Ativa = true,
                CriadoEm = DateTime.UtcNow
            };
            ctx.PlanoContas.Add(conta);
            await ctx.SaveChangesAsync(ct);
            return Results.Created($"/api/empresas/{empresaId}/plano-contas/{conta.Id}", conta);
        });
    }
}
