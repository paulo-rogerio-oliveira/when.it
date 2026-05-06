namespace Contabilidade.Sandbox.Api.Domain;

// Dados específicos gravados quando empresa.Porte = Grande.
// 1:1 com Empresa (EmpresaId é PK e FK).
public class DadosGrandePorte
{
    public Guid EmpresaId { get; set; }
    public decimal FaturamentoAnualMilhoes { get; set; }
    public int QuantidadeFuncionarios { get; set; }
}
