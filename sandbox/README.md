# Contabilidade Sandbox

App de contabilidade pra exercitar o fluxo do DbSense (recording → inferência → matching → reaction).
Não é um sistema real — é um alvo realista pra produzir DMLs com cenários úteis: INSERT solo,
INSERT com companions na mesma transação, UPDATEs por porte de empresa.

## Stack

- **API**: ASP.NET Core 10 minimal API + EF Core 8 + SQL Server
- **Web**: Vite + React 18 + TypeScript + TailwindCSS + React Query + Zustand

## Modelo

- **Empresas** — multi-tenant; ganha um `Porte` (Micro / Pequeno / Médio / Grande).
- **Dados específicos por porte** (1:1 com empresa, em tabelas separadas):
  - `dados_grande_porte` — `FaturamentoAnualMilhoes`, `QuantidadeFuncionarios`.
  - `dados_microempresa` — `RegimeTributario`, `CnaePrincipal`.
  - Pequeno e Médio não têm tabela específica.
- **Plano de contas** — hierárquico via `ContaPaiId`, com `Tipo` (Ativo/Passivo/PL/Receita/Despesa) e `Natureza` (Devedora/Credora).
- **Lançamentos** — partidas dobradas (cabeçalho + linhas), validação `débito = crédito`, status `Rascunho`/`Confirmado`/`Cancelado`.
- **Saldos** — calculados em C# descendo a árvore (`SaldoService`), ajustando sinal pela natureza da conta.

## Como rodar

### 1. SQL Server

Qualquer instância. Default em `appsettings.json`:

```
Server=.\SQLEXPRESS;Database=contabilidade_sandbox;Trusted_Connection=true;TrustServerCertificate=true;Encrypt=false
```

Outras opções (override via `ConnectionStrings__Default`):

```bash
# Docker SA
ConnectionStrings__Default="Server=localhost,1433;Database=contabilidade_sandbox;User Id=sa;Password=Dev@2026!;TrustServerCertificate=true"

# Trusted Connection em named instance
ConnectionStrings__Default="Server=.\\SQLEXPRESS;Database=contabilidade_sandbox;Trusted_Connection=true;TrustServerCertificate=true;Encrypt=false"
```

### 2. API

```bash
dotnet run --project sandbox/Contabilidade.Sandbox.Api
```

Sobe em `http://localhost:5100`. Na primeira execução o banco `contabilidade_sandbox` é criado e populado:

- 2 empresas: **Acme** (Micro / Simples Nacional) e **Sigma** (Grande / faturamento ~R$ 480M, 1240 func).
- Plano de contas BR padrão (~30 contas).
- ~60 lançamentos balanceados nos últimos 90 dias + 1 aporte de capital de R$ 50k.

Re-execuções são idempotentes:

- `EnsureCreatedAsync` cria schema se não existir.
- `AplicarMigracoesIdempotentesAsync` adiciona colunas/tabelas novas (`Porte`, `dados_grande_porte`, `dados_microempresa`) preservando dados.
- `SeedDadosPorteParaEmpresasExistentesAsync` popula dados de porte pras empresas seed se ainda não tiverem.

### 3. Web

```bash
cd sandbox/Contabilidade.Sandbox.Web
npm install
npm run dev
```

Abre em `http://localhost:5174`. Vite proxia `/api/*` → `:5100`.

## Telas

- **Dashboard** — saldos consolidados (Ativos/Passivos/Receitas/Despesas) e últimos lançamentos.
- **Empresas** — CRUD com formulário unificado (criar/editar). Campos extras aparecem condicionalmente:
  - Porte = **Grande** → fieldset verde com Faturamento + QtdFuncionários.
  - Porte = **Micro** → fieldset amarelo com Regime Tributário + CNAE.
  - Pequeno/Médio → só campos básicos.
- **Plano de Contas** — árvore hierárquica com saldos consolidados, expandir/recolher.
- **Lançamentos** — cadastro multi-linha com validação `D = C` em tempo real (badge OK/OFF), confirmar/rascunho, lista com ação de confirmar.

A seleção de empresa fica no header (persistida em `localStorage` via Zustand).

## Por que este modelo é útil pra testar o DbSense

| Cenário | Operação no sandbox | DMLs gerados | Pra que serve |
|---|---|---|---|
| INSERT solo | Edit nome fantasia | 1 UPDATE em `empresas` | Trigger sem companions |
| INSERT + companion | Cadastra empresa Grande | 1 INSERT `empresas` + 1 INSERT `dados_grande_porte` (mesma TX) | Correlação por `transaction` ou `time_window` |
| Multi-linha | Cria lançamento | 1 INSERT `lancamentos` + N INSERTs `lancamento_linhas` | Companions múltiplos required |
| UPDATE em cascata | Confirma lançamento | UPDATE `lancamentos` mudando status | Trigger com predicate `status = 'Confirmado'` |
| Mudança de porte | Edita empresa Grande → Micro | UPDATE `empresas` + DELETE `dados_grande_porte` + INSERT `dados_microempresa` | Predicate em `$.after.Porte`, companions condicionais |

## Validar com DbSense

1. Cadastre uma `connection` no DbSense apontando pro `contabilidade_sandbox`.
2. Inicie uma gravação a partir da UI do DbSense.
3. No sandbox, faça uma operação (ex: cadastre uma empresa Grande).
4. Pare a gravação. Veja em **Revisão**:
   - O `parsed_payload` de cada evento mostra `values` resolvidos (ex: `{"Cnpj": "12.345...", "RazaoSocial": "Acme", ...}`).
   - A inferência heurística sugere a regra com trigger + companions detectados.
5. Edite o nome da regra, configure a **reaction** (ver [docs/reactions.md](../docs/reactions.md)) e ative.
6. Volte ao sandbox e execute a operação novamente — a reaction deve disparar com os macros (`$.after.Cnpj`, etc.) já resolvidos com os valores reais.

## Endpoints da API

| Método | Rota | Descrição |
|---|---|---|
| GET | `/api/empresas` | Lista resumida |
| GET | `/api/empresas/{id}` | Detalhe (inclui dados de porte) |
| POST | `/api/empresas` | Cria (com dados condicionais ao porte) |
| PUT | `/api/empresas/{id}` | Edita; reconcilia dados de porte (cria/atualiza/deleta cascata) |
| GET | `/api/empresas/{id}/plano-contas` | Lista plano + saldos |
| POST | `/api/empresas/{id}/plano-contas` | Cria conta |
| GET | `/api/empresas/{id}/lancamentos` | Lista paginada |
| GET | `/api/empresas/{id}/lancamentos/{id}` | Detalhe com linhas |
| POST | `/api/empresas/{id}/lancamentos` | Cria lançamento (valida balanceamento) |
| POST | `/api/empresas/{id}/lancamentos/{id}/confirmar` | Confirma rascunho |
| POST | `/api/empresas/{id}/lancamentos/{id}/cancelar` | Cancela |
| GET | `/api/empresas/{id}/saldos` | Saldos consolidados (todos) |
| GET | `/api/empresas/{id}/saldos/{contaId}` | Saldo de uma conta específica |
