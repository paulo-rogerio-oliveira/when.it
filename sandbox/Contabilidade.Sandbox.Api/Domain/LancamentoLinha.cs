namespace Contabilidade.Sandbox.Api.Domain;

public class LancamentoLinha
{
    public Guid Id { get; set; }
    public Guid LancamentoId { get; set; }
    public Guid ContaId { get; set; }
    public TipoLinha Tipo { get; set; }
    public decimal Valor { get; set; }
    public string? Historico { get; set; }
    public int Ordem { get; set; }
}
