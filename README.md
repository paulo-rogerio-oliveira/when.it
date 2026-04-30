# DbSense

Implementação de referência do MVP descrito em `spec-dbsense-mvp.md`.

## Escopo desta fatia (semanas 1–2 da spec)

Scaffold + fluxo de setup:

- Solução .NET 8 com 4 projetos (`Api`, `Worker`, `Core`, `Contracts`).
- EF Core com todas as entidades do schema `dbsense` (10 tabelas + `setup_info`).
- Endpoints de `/api/setup/*` (status, test-connection, provision, create-admin) e `/api/auth/login`.
- Frontend React + Vite + Tailwind com wizard de 5 passos e página de login.
- `docker-compose.yml` com SQL Server de controle, API, Worker, RabbitMQ e SQL Server alvo.

Fora desta fatia (próximas): coletor XEvents, publisher RabbitMQ, motor de matching, inferência
de regras, gravação assistida, dashboard em tempo real.

## Estrutura

```
.
├── DbSense.sln
├── Directory.Build.props
├── docker-compose.yml
├── src/
│   ├── DbSense.Api/       # ASP.NET Core Web API
│   ├── DbSense.Worker/    # Worker (scaffold)
│   ├── DbSense.Core/      # Domínio, persistência, segurança, setup
│   └── DbSense.Contracts/ # DTOs compartilhados
└── frontend/              # Vite + React + Tailwind
```

## Requisitos

- .NET 8 SDK
- Node 20+
- Docker Desktop (para `docker-compose`)

## Rodando em dev (sem Docker)

1. Subir um SQL Server local (por exemplo via Docker: `docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=Dev@2026! -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`).

2. Configurar segredos (segredos de dev já estão em `appsettings.Development.json` mas
   **não use em produção**).

3. Backend:
   ```bash
   dotnet restore
   dotnet run --project src/DbSense.Api
   # em outro terminal:
   dotnet run --project src/DbSense.Worker
   ```
   A API sobe em `http://localhost:5000` (Swagger em `/swagger`).

4. Frontend:
   ```bash
   cd frontend
   npm install
   npm run dev
   ```
   Abre em `http://localhost:5173` com proxy para `/api` → `:5000`.

## Rodando tudo via Docker

```bash
docker compose up --build
```

Sobe: `dbsense-control-db` (1433), `dbsense-api` (5000), `dbsense-worker` (5001),
`rabbitmq` (5672 / 15672) e `sql-server-target` (1434).

## Fluxo de setup (primeira execução)

1. Acesse `http://localhost:5173` → redireciona para `/setup`.
2. **Passo 1** — revisão de pré-requisitos.
3. **Passo 2** — informe a conexão com o banco de controle e teste.
4. **Passo 3** — provisiona o schema `dbsense` (usa `EnsureCreated` do EF Core nesta fatia;
   migrações versionadas serão adicionadas nas próximas).
5. **Passo 4** — cria o usuário administrador (BCrypt).
6. **Passo 5** — redireciona para `/login`.

## Variáveis de ambiente

| Nome | Descrição |
|---|---|
| `ConnectionStrings__ControlDb` | Connection string do SQL Server de controle |
| `Security__EncryptionKey` | Chave AES-256 em base64 (32 bytes) |
| `Security__JwtSecret` | Segredo JWT HS256 em base64 (≥ 32 bytes) |
| `Security__JwtExpirationHours` | Default 8 |

Gerar uma chave AES-256 base64:
```bash
openssl rand -base64 32
```

## Reactions (§6.4 da spec)

Toda regra ativa tem **uma** reaction associada — o que o serviço executa quando o
trigger casa. O MVP suporta três tipos:

- **`cmd`** — roda um processo (sem shell) com payload via stdin/env. Ex.: `curl` chamando webhook do cliente.
- **`sql`** — executa SQL parametrizado contra uma `connection` cadastrada. Ex.: `UPDATE outbox_legado SET processado=1 WHERE id=@id`.
- **`rabbit`** — publica em exchange RabbitMQ via destination cadastrado.

A reaction é configurada no `rule.definition.reaction` e despachada pelo
`ReactionExecutorWorker` lendo do `dbsense.outbox`.

## Próximas fatias

Tracking das etapas seguintes do cronograma (seção 14 da spec):
- 3–4: XEventsCollector + Publisher RabbitMQ
- 5–6: Fluxo de gravação (UI + SSE + persistência de eventos brutos)
- 7–8: Inferência v1
- 9–10: Matching + outbox transacional
- 11: Dashboard em tempo real (SSE)
- 12+: Generalização, hardening, piloto
