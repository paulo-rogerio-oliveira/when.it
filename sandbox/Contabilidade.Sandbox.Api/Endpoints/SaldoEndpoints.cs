using Contabilidade.Sandbox.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Contabilidade.Sandbox.Api.Endpoints;

public static class SaldoEndpoints
{
    public static void MapSaldoEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/empresas/{empresaId:guid}/saldos").WithTags("Saldos");

        g.MapGet("/", async (
            Guid empresaId, SaldoService svc,
            [FromQuery] DateTime? dataAte, CancellationToken ct) =>
        {
            var result = await svc.CalcularSaldosEmpresaAsync(empresaId, dataAte, ct);
            return Results.Ok(result);
        });

        g.MapGet("/{contaId:guid}", async (
            Guid empresaId, Guid contaId, SaldoService svc,
            [FromQuery] DateTime? dataAte, CancellationToken ct) =>
        {
            try
            {
                var saldo = await svc.CalcularSaldoAsync(contaId, dataAte, ct);
                return Results.Ok(new { ContaId = contaId, Saldo = saldo, DataAte = dataAte });
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(ex.Message);
            }
        });
    }
}
