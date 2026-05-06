namespace Contabilidade.Sandbox.Api.Domain;

public class Lancamento
{
    public Guid Id { get; set; }
    public Guid EmpresaId { get; set; }
    public int Numero { get; set; }
    public DateTime DataLancamento { get; set; }
    public DateTime DataCompetencia { get; set; }
    public string Historico { get; set; } = string.Empty;
    public decimal ValorTotal { get; set; }
    public StatusLancamento Status { get; set; } = StatusLancamento.Rascunho;
    public DateTime CriadoEm { get; set; }

    public List<LancamentoLinha> Linhas { get; set; } = new();
}
