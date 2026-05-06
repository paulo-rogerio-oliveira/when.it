namespace Contabilidade.Sandbox.Api.Domain;

// Dados específicos gravados quando empresa.Porte = Micro.
// 1:1 com Empresa (EmpresaId é PK e FK).
public class DadosMicroEmpresa
{
    public Guid EmpresaId { get; set; }
    public string RegimeTributario { get; set; } = "Simples Nacional";
    public string CnaePrincipal { get; set; } = string.Empty;
}
