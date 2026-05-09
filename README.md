# DbSense

Plataforma para transformar operações DML em eventos de domínio sem mexer na aplicação alvo:
captura SQL via Extended Events do SQL Server, extrai a estrutura da operação, infere uma
**regra** (descrição declarativa do trigger + companions), e dispara uma **reação**
(processo, SQL ou mensagem RabbitMQ) com idempotência.

## Estado atual

Pipeline ponta-a-ponta funcionando:

- **Recording** assistido (UI → XE session por gravação → ring buffer → eventos persistidos com SQL parseado).
- **Inferência** heurística (ScriptDom) e por LLM (Anthropic, opcional).
- **Matching engine** stateful com correlação por janela temporal ou transação, idempotência por trigger.
- **Outbox transacional** com retry, lock e expansão de placeholders.
- **Reactions** dos tipos `cmd`, `sql` e `rabbit`.
- **Sandbox de contabilidade** (`sandbox/`) — app real (.NET + React) que serve de alvo pra validar o fluxo.

A spec de referência continua em `spec-dbsense-mvp.md`. Este README descreve o que **está** no código.

## Estrutura

```
.
├── DbSense.sln
├── docker-compose.yml
├── src/
│   ├── DbSense.Api/         # ASP.NET Core API (auth, setup, conexões, recordings, rules, dashboard)
│   ├── DbSense.Worker/      # Hosted services: Recording, Matcher, ReactionExecutor, Commands
│   ├── DbSense.Core/        # Domain, EF, parser, inferência, engine, reactions, security
│   └── DbSense.Contracts/   # DTOs compartilhados
├── frontend/                # Vite + React + TS + Tailwind (UI principal do DbSense)
├── electron/                # Shell desktop: sobe API + Worker + UI numa BrowserWindow
├── sandbox/                 # App alvo de teste (contabilidade) — ver sandbox/README.md
└── docs/                    # Documentação técnica (arquitetura, reações)
```

## Requisitos

- **.NET 10 SDK** (target dos projetos)
- Node 20+
- SQL Server 2022 acessível (Docker, Express ou instância existente)
- Docker Desktop é opcional (só pra `docker compose`)

## Rodando em dev

### 1. SQL Server

Qualquer instância serve. Exemplos:

- **SQL Server Express local**: `Server=.\SQLEXPRESS;Trusted_Connection=true;TrustServerCertificate=true;Encrypt=false`
- **Docker**: `docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=Dev@2026! -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`
   → `Server=localhost,1433;User Id=sa;Password=Dev@2026!;TrustServerCertificate=true`

A connection string vai em `src/DbSense.Api/appsettings.Development.json` (ou via variável `ConnectionStrings__ControlDb`). O **schema é criado pelo wizard de setup** na primeira execução.

### 2. Backend

```bash
dotnet restore
dotnet run --project src/DbSense.Api      # http://localhost:5000
dotnet run --project src/DbSense.Worker   # workers em background
```

### 3. Frontend

```bash
cd frontend
npm install
npm run dev                                # http://localhost:5173
```

### 4. Electron (opcional — single-window desktop)

Sobe API + Worker + Vite numa janela só, com cleanup automático no fechamento:

```bash
cd electron
npm install
npm run dev    # API:5000 + Worker + Vite:5173 → BrowserWindow
```

**Empacotando o `.exe`** (NSIS installer + portable, x64):

```bash
cd electron
npm run dist        # build:frontend + publish:api + publish:worker + electron-builder
# saída em electron/dist/DbSense-<versão>-x64.exe (installer) e DbSense-<versão>-x64-portable.exe
```

`npm run dist:dir` produz só a pasta unpacked (útil pra debug, sem instalador).

No primeiro launch do `.exe`, é gerado `%APPDATA%/DbSense/dbsense.config.json` com
secrets aleatórios (encryption key + JWT) e a connection string de control DB
default — edite esse arquivo se o seu SQL Server estiver em outro host. O
`runtime-config.json` do setup wizard também vai pra `%APPDATA%/DbSense/`.

Pré-requisitos: SQL Server acessível e `.NET 10 SDK` no PATH (necessário pro
`dotnet publish --self-contained` que empacota os runtimes da API/Worker).

### 5. Sandbox (opcional, mas recomendado pra validar)

```bash
dotnet run --project sandbox/Contabilidade.Sandbox.Api    # http://localhost:5100
cd sandbox/Contabilidade.Sandbox.Web && npm install && npm run dev   # http://localhost:5174
```

Detalhes em [`sandbox/README.md`](sandbox/README.md).

## Setup inicial (primeira execução)

`http://localhost:5173` redireciona pra `/setup`:

1. **Pré-requisitos**.
2. **Conexão de controle** — testar conexão com o SQL Server e provisionar o database `dbsense_control` se necessário.
3. **Schema** — `EnsureCreated` do EF Core monta as tabelas em `dbsense.*`. Migrations idempotentes adicionais (`OutboxSchemaMigrator`, `RecordingSchemaMigrator`) rodam em todo startup do Worker.
4. **Admin** — cria o usuário inicial (BCrypt).
5. **Login** com o usuário criado.

## Fluxo principal

```
   ┌────────────────┐                        ┌──────────────────┐
   │ App alvo (EF)  │                        │  XE / ring_buffer│
   └────────┬───────┘                        └────────▲─────────┘
            │ INSERT/UPDATE/DELETE                    │
            ▼                                         │
   ┌────────────────────────────────────────────┐     │
   │  SQL Server (eventos sql_batch_completed   │─────┘
   │  + rpc_completed publicados)               │
   └────────────────────────────────────────────┘
            │
            ├──► RecordingCollector (1 session por recording)
            │       parseia → grava recording_events com parsed_payload
            │
            └──► ProductionXeStream (1 session por connection ativa)
                    │
                    ▼
              RuleMatcherWorker.OnEvent
                    │
                    ├──► absorve em pending matches existentes
                    └──► avalia trigger; cria pending ou match imediato
                              │
                              ▼ (quando completa)
                        OutboxEnqueuer
                              │ expande placeholders contra raw payload
                              ▼
                        events_log + outbox  (mesma TX)
                              │
                              ▼
                        ReactionExecutorWorker
                              │
                              ▼
                        Cmd / Sql / Rabbit handler
```

## Configuração relevante

| Nome | Descrição |
|---|---|
| `ConnectionStrings__ControlDb` | Connection string do SQL Server de controle |
| `Security__EncryptionKey` | Chave AES-256 base64 (32 bytes) — criptografa senhas de connection |
| `Security__JwtSecret` | Segredo HS256 base64 (≥ 32 bytes) |
| `Security__JwtExpirationHours` | Default 8 |
| `Llm__Provider` | `anthropic` ou vazio (LLM é opt-in) |
| `Llm__ApiKey` | API key do provider escolhido |
| `Worker__InstanceId` | Identificador do executor (default: hostname) |

Gerar uma chave AES-256 base64:

```bash
openssl rand -base64 32
```

## Documentação técnica

- **[docs/architecture.md](docs/architecture.md)** — pipeline interno (XE config, parser, engine stateful, idempotência, scopes, schema migrations).
- **[docs/reactions.md](docs/reactions.md)** — tipos de reaction, tabela de placeholders/macros, exemplos práticos, troubleshooting.
- **[docs/production.md](docs/production.md)** — checklist de permissões, retenção, segurança, backup e operação.
- **[sandbox/README.md](sandbox/README.md)** — app de contabilidade pra exercitar o pipeline.
- **`spec-dbsense-mvp.md`** — spec original.

## Rodando via Docker

```bash
docker compose up --build
```

Sobe `dbsense-control-db` (1433), `dbsense-api` (5000), `dbsense-worker`, `rabbitmq` (5672 / 15672) e `sql-server-target` (1434). Útil pra reproduzir o ambiente sem instalação local — a UI da spec ainda assume o setup wizard pra inicialização.

## Limitações conhecidas (MVP)

- **Stored procedures**: o parser não enxerga o corpo de procs (só vê o `EXEC dbo.proc @p=...`). Operações encapsuladas em procs não geram DMLs no parser.
- **`sql_statement_completed`** (ad-hoc granular) não é capturado — só `sql_batch_completed`. Pra batches ad-hoc multi-statement, o parser ainda extrai cada DML, mas você só pega o evento "fim do batch".
- **Migrations**: `EnsureCreated` + migrators idempotentes específicos. Migrations versionadas (FluentMigrator/EFCore) ainda não foram introduzidas.
- **Optional companions**: a engine ignora companions com `required: false` no MVP.
- **Predicates**: `eq` e `ne` só. `>`, `<`, `LIKE`, `IN` ainda não.
