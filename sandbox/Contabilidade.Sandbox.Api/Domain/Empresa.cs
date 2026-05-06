namespace Contabilidade.Sandbox.Api.Domain;

public class Empresa
{
    public Guid Id { get; set; }
    public string Cnpj { get; set; } = string.Empty;
    public string RazaoSocial { get; set; } = string.Empty;
    public string? NomeFantasia { get; set; }
    public Porte Porte { get; set; } = Porte.Pequeno;
    public DateTime CriadoEm { get; set; }
    public bool Ativa { get; set; } = true;

    public DadosGrandePorte? DadosGrandePorte { get; set; }
    public DadosMicroEmpresa? DadosMicroEmpresa { get; set; }
}
