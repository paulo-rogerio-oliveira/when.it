# Contabilidade Sandbox

App de contabilidade simples para validar o fluxo de **screen save** (recording + inferência de regras) do DbSense.

## Stack

- API: ASP.NET Core 8 minimal API + EF Core + SQL Server
- Web: Vite + React + TypeScript + Tailwind + React Query

## Modelo

- **Empresas** — multi-tenant.
- **Plano de contas** — hierárquico (Ativo / Passivo / PL / Receita / Despesa).
- **Lançamentos** — partidas dobradas (header + linhas), validação débito = crédito.
- **Saldos** — calculados em C# descendo a árvore, ajustando sinal pela natureza (devedora/credora) da conta.

## Como rodar

### 1. API

```bash
cd sandbox/Contabilidade.Sandbox.Api
dotnet run
```

API em `http://localhost:5100`. Connection string default em `appsettings.json` aponta pra `localhost` SQL Server (Trusted Connection), banco `contabilidade_sandbox`.

Na primeira execução o banco é criado e populado com seed (2 empresas, plano de contas BR padrão e ~60 lançamentos balanceados nos últimos 90 dias).

### 2. Web

```bash
cd sandbox/Contabilidade.Sandbox.Web
npm install
npm run dev
```

Web em `http://localhost:5174`. Vite proxia `/api/*` pra API.

## Telas

- **Dashboard** — resumo de saldos e últimos lançamentos.
- **Empresas** — CRUD básico.
- **Plano de Contas** — árvore com saldos consolidados, expandir/recolher.
- **Lançamentos** — cadastro multi-linha com validação de balanceamento (D = C) em tempo real.

## Validar com DbSense

1. Aponte o `RecordingCollector` do DbSense pro banco `contabilidade_sandbox`.
2. Inicie uma recording.
3. No sandbox, crie um lançamento (gera INSERT em `lancamentos` + N INSERTs em `lancamento_linhas` na mesma transação) — caso ideal pra testar `correlation.companions` no `RuleEngine`.
4. Pare a recording, infera a regra, e ative.
