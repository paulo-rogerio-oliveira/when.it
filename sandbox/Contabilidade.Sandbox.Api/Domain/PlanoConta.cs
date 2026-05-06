namespace Contabilidade.Sandbox.Api.Domain;

public class PlanoConta
{
    public Guid Id { get; set; }
    public Guid EmpresaId { get; set; }
    public Guid? ContaPaiId { get; set; }
    public string Codigo { get; set; } = string.Empty;
    public string Nome { get; set; } = string.Empty;
    public TipoConta Tipo { get; set; }
    public NaturezaConta Natureza { get; set; }
    public bool AceitaLancamento { get; set; }
    public bool Ativa { get; set; } = true;
    public DateTime CriadoEm { get; set; }
}
