using Contabilidade.Sandbox.Api.Data;
using Contabilidade.Sandbox.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Contabilidade.Sandbox.Api.Endpoints;

public static class EmpresaEndpoints
{
    public record DadosGrandePorteInput(decimal FaturamentoAnualMilhoes, int QuantidadeFuncionarios);
    public record DadosMicroEmpresaInput(string RegimeTributario, string CnaePrincipal);

    public record EmpresaInput(
        string Cnpj,
        string RazaoSocial,
        string? NomeFantasia,
        Porte Porte,
        DadosGrandePorteInput? DadosGrandePorte,
        DadosMicroEmpresaInput? DadosMicroEmpresa);

    public record EmpresaDetalhe(
        Guid Id,
        string Cnpj,
        string RazaoSocial,
        string? NomeFantasia,
        Porte Porte,
        bool Ativa,
        DateTime CriadoEm,
        DadosGrandePorte? DadosGrandePorte,
        DadosMicroEmpresa? DadosMicroEmpresa);

    public static void MapEmpresaEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/empresas").WithTags("Empresas");

        g.MapGet("/", async (ContabilidadeContext ctx, CancellationToken ct) =>
            await ctx.Empresas.AsNoTracking().OrderBy(e => e.RazaoSocial).ToListAsync(ct));

        g.MapGet("/{id:guid}", async (Guid id, ContabilidadeContext ctx, CancellationToken ct) =>
        {
            var e = await ctx.Empresas.AsNoTracking()
                .Include(x => x.DadosGrandePorte)
                .Include(x => x.DadosMicroEmpresa)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound();
            return Results.Ok(new EmpresaDetalhe(
                e.Id, e.Cnpj, e.RazaoSocial, e.NomeFantasia, e.Porte, e.Ativa, e.CriadoEm,
                e.DadosGrandePorte, e.DadosMicroEmpresa));
        });

        g.MapPost("/", async ([FromBody] EmpresaInput input, ContabilidadeContext ctx, CancellationToken ct) =>
        {
            var erro = ValidarInput(input);
            if (erro is not null) return Results.BadRequest(erro);

            var emp = new Empresa
            {
                Id = Guid.NewGuid(),
                Cnpj = input.Cnpj.Trim(),
                RazaoSocial = input.RazaoSocial.Trim(),
                NomeFantasia = input.NomeFantasia?.Trim(),
                Porte = input.Porte,
                CriadoEm = DateTime.UtcNow,
                Ativa = true
            };
            // Dados condicionais ao porte gravados na MESMA transação que o INSERT empresas
            // (companion natural pra correlation no DbSense).
            if (input.Porte == Porte.Grande && input.DadosGrandePorte is not null)
            {
                emp.DadosGrandePorte = new DadosGrandePorte
                {
                    EmpresaId = emp.Id,
                    FaturamentoAnualMilhoes = input.DadosGrandePorte.FaturamentoAnualMilhoes,
                    QuantidadeFuncionarios = input.DadosGrandePorte.QuantidadeFuncionarios
                };
            }
            else if (input.Porte == Porte.Micro && input.DadosMicroEmpresa is not null)
            {
                emp.DadosMicroEmpresa = new DadosMicroEmpresa
                {
                    EmpresaId = emp.Id,
                    RegimeTributario = input.DadosMicroEmpresa.RegimeTributario,
                    CnaePrincipal = input.DadosMicroEmpresa.CnaePrincipal
                };
            }
            ctx.Empresas.Add(emp);
            await ctx.SaveChangesAsync(ct);
            return Results.Created($"/api/empresas/{emp.Id}", new { emp.Id });
        });

        g.MapPut("/{id:guid}", async (
            Guid id, [FromBody] EmpresaInput input, ContabilidadeContext ctx, CancellationToken ct) =>
        {
            var erro = ValidarInput(input);
            if (erro is not null) return Results.BadRequest(erro);

            var emp = await ctx.Empresas
                .Include(x => x.DadosGrandePorte)
                .Include(x => x.DadosMicroEmpresa)
                .FirstOrDefaultAsync(x => x.Id == id, ct);
            if (emp is null) return Results.NotFound();

            emp.Cnpj = input.Cnpj.Trim();
            emp.RazaoSocial = input.RazaoSocial.Trim();
            emp.NomeFantasia = input.NomeFantasia?.Trim();
            emp.Porte = input.Porte;

            // Reconcilia dados de porte: cria, atualiza ou remove conforme o porte selecionado.
            if (input.Porte == Porte.Grande)
            {
                emp.DadosMicroEmpresa = null;
                if (input.DadosGrandePorte is not null)
                {
                    emp.DadosGrandePorte ??= new DadosGrandePorte { EmpresaId = emp.Id };
                    emp.DadosGrandePorte.FaturamentoAnualMilhoes = input.DadosGrandePorte.FaturamentoAnualMilhoes;
                    emp.DadosGrandePorte.QuantidadeFuncionarios = input.DadosGrandePorte.QuantidadeFuncionarios;
                }
            }
            else if (input.Porte == Porte.Micro)
            {
                emp.DadosGrandePorte = null;
                if (input.DadosMicroEmpresa is not null)
                {
                    emp.DadosMicroEmpresa ??= new DadosMicroEmpresa { EmpresaId = emp.Id };
                    emp.DadosMicroEmpresa.RegimeTributario = input.DadosMicroEmpresa.RegimeTributario;
                    emp.DadosMicroEmpresa.CnaePrincipal = input.DadosMicroEmpresa.CnaePrincipal;
                }
            }
            else
            {
                emp.DadosGrandePorte = null;
                emp.DadosMicroEmpresa = null;
            }

            await ctx.SaveChangesAsync(ct);
            return Results.Ok(new { emp.Id });
        });
    }

    private static string? ValidarInput(EmpresaInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Cnpj) || string.IsNullOrWhiteSpace(input.RazaoSocial))
            return "CNPJ e Razão Social são obrigatórios.";

        if (input.Porte == Porte.Grande)
        {
            if (input.DadosGrandePorte is null)
                return "Empresa de Grande Porte exige dados específicos.";
            if (input.DadosGrandePorte.FaturamentoAnualMilhoes <= 0)
                return "FaturamentoAnualMilhoes deve ser > 0.";
            if (input.DadosGrandePorte.QuantidadeFuncionarios <= 0)
                return "QuantidadeFuncionarios deve ser > 0.";
        }
        else if (input.Porte == Porte.Micro)
        {
            if (input.DadosMicroEmpresa is null)
                return "Microempresa exige dados específicos.";
            if (string.IsNullOrWhiteSpace(input.DadosMicroEmpresa.RegimeTributario))
                return "RegimeTributario é obrigatório.";
            if (string.IsNullOrWhiteSpace(input.DadosMicroEmpresa.CnaePrincipal))
                return "CnaePrincipal é obrigatório.";
        }
        return null;
    }
}
