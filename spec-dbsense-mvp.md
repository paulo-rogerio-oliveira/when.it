# DbSense — Especificação Técnica do MVP

**Versão:** 0.1 (draft)
**Data:** 2026-04-23
**Status:** Proposta para validação

---

## 1. Visão Geral

DbSense é uma plataforma que transforma eventos de banco de dados SQL Server em eventos de negócio publicados em filas de mensageria, configurada por **demonstração** (o analista executa a ação no sistema legado e a regra é inferida automaticamente) em vez de por especificação técnica.

### 1.1 Escopo do MVP

O MVP implementa o fluxo mínimo viável para validar a proposta de valor:

1. Setup inicial automatizado (criação das tabelas de controle)
2. Cadastro de conexão com SQL Server alvo (o banco observado)
3. Gravação assistida de uma operação no sistema legado
4. Geração de regra candidata a partir da gravação
5. Ativação da regra em modo produção
6. Execução contínua: matching de eventos SQL contra regras ativas e publicação em RabbitMQ

### 1.2 Fora do escopo do MVP

- Suporte a Debezium (post-MVP, ver seção 12)
- Suporte a outros bancos (Postgres, MySQL, Oracle)
- Orquestrador de workers gerenciados
- Self-healing assistido por LLM
- Multi-tenancy verdadeiro (MVP assume single-tenant por instância)
- Integração com sistemas destino além de RabbitMQ
- Shadow mode / replay histórico
- RBAC granular (MVP tem autenticação simples)

### 1.3 Stack técnica

- **Frontend:** React 18 + TypeScript + Vite. Tailwind CSS. TanStack Query para estado servidor. Zustand para estado local. React Router.
- **Backend:** .NET 8 (ASP.NET Core Web API + Worker Services). C# 12.
- **Banco de controle:** SQL Server 2019+ (o banco do produto, separado do banco observado).
- **Motor de captura:** Extended Events (XEvents) via `Microsoft.SqlServer.XEvent.XELite`.
- **Mensageria destino:** RabbitMQ 3.12+ via `RabbitMQ.Client`.
- **Infra:** Docker Compose para dev, containers para deploy on-prem.

---

## 2. Arquitetura de Alto Nível

```
┌──────────────────────────────────────────────────────────────────────┐
│                        Cliente / Usuário final                        │
└──────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│  Frontend React (SPA)                                                 │
│  - Setup wizard (primeira execução)                                   │
│  - Cadastro de conexões                                               │
│  - Gravador de ações                                                  │
│  - Editor/revisor de regras                                           │
│  - Dashboard de eventos em tempo real (SSE)                           │
└──────────────────────────────────────────────────────────────────────┘
                                    │  HTTPS / JSON / SSE
                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│  DbSense.Api (.NET 8 Web API)                                         │
│  - Endpoints REST para CRUD de entidades                              │
│  - Endpoint de setup (provisiona schema)                              │
│  - Endpoint de gravação (start/stop/finalize)                         │
│  - SSE stream para eventos em tempo real                              │
│  - Autenticação JWT simples                                           │
└──────────────────────────────────────────────────────────────────────┘
           │                                      │
           │ DB write/read                        │ Inter-process
           ▼                                      ▼
┌─────────────────────────┐         ┌────────────────────────────────┐
│ SQL Server de controle  │         │ DbSense.Worker (.NET Worker)   │
│ (schema dbsense)        │◄────────┤ - Coletor XEvents              │
│                         │         │ - Normalizador de eventos      │
│ - connections           │         │ - Motor de matching            │
│ - recordings            │         │ - Inferência de regras         │
│ - rules                 │         │ - Publisher RabbitMQ           │
│ - events_log            │         │ - Gerenciador de sessões       │
│ - outbox                │         │   XEvents por conexão          │
└─────────────────────────┘         └────────────────────────────────┘
                                                    │
                                                    │ TCP 1433
                                                    ▼
                                    ┌────────────────────────────────┐
                                    │ SQL Server ALVO (do cliente)   │
                                    │ - banco do sistema legado      │
                                    │ - acesso somente leitura +     │
                                    │   permissão XEvents            │
                                    └────────────────────────────────┘
                                                    │
                                                    │ AMQP
                                                    ▼
                                    ┌────────────────────────────────┐
                                    │ RabbitMQ (do cliente)          │
                                    └────────────────────────────────┘
```

### 2.1 Processos e responsabilidades

**DbSense.Api** (processo 1)
- Serve o frontend (arquivos estáticos) e endpoints REST.
- Escreve no banco de controle.
- Orquestra o Worker via comandos persistidos no banco (`worker_commands`) e eventos de domínio. Não há comunicação direta por IPC; toda coordenação é via banco (simplicidade no MVP).

**DbSense.Worker** (processo 2)
- Background service que lê configuração do banco de controle.
- Mantém uma sessão XEvents por conexão ativa.
- Normaliza, aplica regras, publica eventos em RabbitMQ.
- Expõe healthcheck HTTP na porta 5001 (para o Api consultar status).
- Pode rodar no mesmo container do Api ou separado (no MVP, separado para isolar responsabilidades).

---

## 3. Banco de Dados de Controle

Schema `dbsense` no SQL Server de controle. Criado pelo endpoint de setup na primeira execução.

### 3.1 Tabelas

#### 3.1.1 `dbsense.connections`
Conexões com bancos observados.

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `uniqueidentifier` PK | Identificador |
| `name` | `nvarchar(200)` | Nome amigável |
| `server` | `nvarchar(500)` | Host do SQL Server alvo |
| `database` | `nvarchar(200)` | Banco observado |
| `auth_type` | `nvarchar(20)` | `sql` \| `windows` |
| `username` | `nvarchar(200) NULL` | |
| `password_encrypted` | `varbinary(max) NULL` | Criptografado com chave do servidor (DPAPI ou AES-256 + chave de ambiente) |
| `status` | `nvarchar(20)` | `inactive` \| `testing` \| `active` \| `error` |
| `last_tested_at` | `datetime2 NULL` | |
| `last_error` | `nvarchar(max) NULL` | |
| `created_at` | `datetime2` | |
| `updated_at` | `datetime2` | |

#### 3.1.2 `dbsense.rabbitmq_destinations`
Destinos de publicação.

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `uniqueidentifier` PK | |
| `name` | `nvarchar(200)` | |
| `host` | `nvarchar(500)` | |
| `port` | `int` | |
| `virtual_host` | `nvarchar(200)` | |
| `username` | `nvarchar(200)` | |
| `password_encrypted` | `varbinary(max)` | |
| `use_tls` | `bit` | |
| `default_exchange` | `nvarchar(200)` | Pode ser override em cada regra |
| `status` | `nvarchar(20)` | |
| `created_at` | `datetime2` | |

#### 3.1.3 `dbsense.recordings`
Gravações feitas pelo usuário.

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `uniqueidentifier` PK | |
| `connection_id` | `uniqueidentifier` FK | |
| `name` | `nvarchar(200)` | Nome dado pelo usuário ("aprovar sinistro") |
| `description` | `nvarchar(max) NULL` | |
| `started_at` | `datetime2` | |
| `stopped_at` | `datetime2 NULL` | |
| `status` | `nvarchar(20)` | `recording` \| `completed` \| `failed` \| `discarded` |
| `filter_session_id` | `int NULL` | session_id do SQL Server a filtrar (se identificado) |
| `filter_host_name` | `nvarchar(200) NULL` | |
| `filter_app_name` | `nvarchar(200) NULL` | |
| `filter_login_name` | `nvarchar(200) NULL` | |
| `event_count` | `int` | Quantos eventos brutos foram capturados |
| `created_at` | `datetime2` | |

#### 3.1.4 `dbsense.recording_events`
Eventos brutos capturados durante gravações. **É uma tabela grande** — particionada por `recording_id` idealmente.

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `bigint` PK identity | |
| `recording_id` | `uniqueidentifier` FK | |
| `event_timestamp` | `datetime2(7)` | Timestamp do SQL Server |
| `event_type` | `nvarchar(50)` | `sql_batch_completed` \| `rpc_completed` \| `sp_statement_completed` |
| `session_id` | `int` | |
| `database_name` | `nvarchar(200)` | |
| `object_name` | `nvarchar(500) NULL` | Quando aplicável (nome de SP, tabela) |
| `sql_text` | `nvarchar(max)` | Texto completo do SQL |
| `statement` | `nvarchar(max) NULL` | Statement individual (para sp_statement_completed) |
| `duration_us` | `bigint` | Microssegundos |
| `cpu_time_us` | `bigint NULL` | |
| `reads` | `bigint NULL` | |
| `writes` | `bigint NULL` | |
| `row_count` | `bigint NULL` | |
| `app_name` | `nvarchar(200) NULL` | |
| `host_name` | `nvarchar(200) NULL` | |
| `login_name` | `nvarchar(200) NULL` | |
| `transaction_id` | `bigint NULL` | Agrupar eventos da mesma transação |
| `raw_payload` | `nvarchar(max) NULL` | JSON bruto do XEvent (debugging) |

Índices: `(recording_id, event_timestamp)`, `(recording_id, transaction_id)`.

#### 3.1.5 `dbsense.rules`
Regras geradas e mantidas pelo usuário.

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `uniqueidentifier` PK | |
| `connection_id` | `uniqueidentifier` FK | |
| `destination_id` | `uniqueidentifier` FK | |
| `source_recording_id` | `uniqueidentifier FK NULL` | Recording que originou a regra |
| `name` | `nvarchar(200)` | |
| `description` | `nvarchar(max) NULL` | |
| `version` | `int` | Incrementa a cada edição |
| `definition` | `nvarchar(max)` | JSON completo da regra (ver seção 6) |
| `status` | `nvarchar(20)` | `draft` \| `testing` \| `active` \| `paused` \| `archived` |
| `created_at` | `datetime2` | |
| `updated_at` | `datetime2` | |
| `activated_at` | `datetime2 NULL` | |

#### 3.1.6 `dbsense.events_log`
Log de eventos que disparam regras em produção. **Tabela quente, com retenção curta** (default 30 dias, configurável).

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `bigint` PK identity | |
| `rule_id` | `uniqueidentifier` FK | |
| `connection_id` | `uniqueidentifier` FK | |
| `matched_at` | `datetime2(7)` | |
| `sql_timestamp` | `datetime2(7)` | |
| `event_payload` | `nvarchar(max)` | JSON do evento publicado |
| `idempotency_key` | `nvarchar(200)` | Hash determinístico pro destino |
| `publish_status` | `nvarchar(20)` | `pending` \| `published` \| `failed` \| `dead_lettered` |
| `publish_attempts` | `int` | |
| `last_error` | `nvarchar(max) NULL` | |
| `published_at` | `datetime2 NULL` | |

Índices: `(rule_id, matched_at)`, `(publish_status, matched_at)` filtrado, `(idempotency_key)` UNIQUE.

#### 3.1.7 `dbsense.outbox`
Padrão transactional outbox. Eventos matched são gravados aqui na mesma transação que escreve em `events_log`. Um processo separado (`ReactionExecutorWorker`) lê daqui e despacha pra reaction correspondente (cmd / sql / rabbit).

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `bigint` PK identity | |
| `events_log_id` | `bigint` FK | |
| `payload` | `nvarchar(max)` | JSON do evento (após `shape`) |
| `reaction_type` | `nvarchar(20)` | `cmd` \| `sql` \| `rabbit` |
| `reaction_config` | `nvarchar(max)` | Config da reaction com placeholders já expandidos (JSON) |
| `status` | `nvarchar(20)` | `pending` \| `processing` \| `processed` \| `failed` |
| `attempts` | `int` | |
| `next_attempt_at` | `datetime2` | |
| `locked_by` | `nvarchar(100) NULL` | Instance ID do worker |
| `locked_until` | `datetime2 NULL` | |
| `last_error` | `nvarchar(max) NULL` | Mensagem de erro da última tentativa (stdout/stderr para cmd, message do SqlException, etc.) |

Índices: `(status, next_attempt_at)` para worker picking, `(locked_by, locked_until)`.

#### 3.1.8 `dbsense.users`
Autenticação (MVP simples).

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `uniqueidentifier` PK | |
| `username` | `nvarchar(100)` UNIQUE | |
| `password_hash` | `nvarchar(500)` | BCrypt |
| `role` | `nvarchar(20)` | `admin` \| `operator` |
| `created_at` | `datetime2` | |

#### 3.1.9 `dbsense.audit_log`
Auditoria de ações administrativas.

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `bigint` PK identity | |
| `user_id` | `uniqueidentifier` FK | |
| `action` | `nvarchar(100)` | |
| `entity_type` | `nvarchar(50)` | |
| `entity_id` | `nvarchar(100)` | |
| `payload` | `nvarchar(max) NULL` | |
| `timestamp` | `datetime2(7)` | |

#### 3.1.10 `dbsense.worker_commands`
Fila de comandos do Api para o Worker (coordenação via banco).

| Coluna | Tipo | Descrição |
|---|---|---|
| `id` | `bigint` PK identity | |
| `command` | `nvarchar(50)` | `start_recording` \| `stop_recording` \| `reload_rules` \| `test_connection` |
| `target_id` | `uniqueidentifier NULL` | |
| `payload` | `nvarchar(max) NULL` | |
| `issued_at` | `datetime2` | |
| `processed_at` | `datetime2 NULL` | |
| `status` | `nvarchar(20)` | `pending` \| `processed` \| `failed` |
| `result` | `nvarchar(max) NULL` | |

### 3.2 Migrations

Usar **FluentMigrator** ou **EF Core Migrations**. MVP recomenda EF Core para velocidade.

Todas as tabelas criadas em uma migration inicial (`0001_InitialSchema`). Migrations subsequentes versionadas por data (`0002_20260501_AddColumnX`).

### 3.3 Script de setup (seed)

O endpoint de setup executa:
1. Cria o schema `dbsense` (se não existir).
2. Aplica todas as migrations pendentes.
3. Cria usuário admin inicial (senha gerada e retornada ao usuário uma única vez).
4. Opcionalmente, cria dados de exemplo (uma conexão fictícia, um destino fictício) — controlado por flag.
5. Grava um registro em `dbsense.setup_info` indicando versão instalada.

---

## 4. Permissões Necessárias

### 4.1 No SQL Server de controle
O usuário do DbSense precisa de `db_owner` no banco de controle (ele mesmo cria as tabelas).

### 4.2 No SQL Server alvo (banco observado)
Usuário com privilégios mínimos:

```sql
-- Criar login e usuário
CREATE LOGIN dbsense_reader WITH PASSWORD = '...';
USE [banco_observado];
CREATE USER dbsense_reader FOR LOGIN dbsense_reader;

-- Permissões para XEvents
GRANT ALTER ANY EVENT SESSION TO dbsense_reader;
GRANT VIEW SERVER STATE TO dbsense_reader;

-- Permissões para leitura de metadata (inferência de regras)
GRANT VIEW DEFINITION TO dbsense_reader;
GRANT SELECT ON SCHEMA::sys TO dbsense_reader;

-- NÃO é necessário db_datareader em tabelas de negócio
-- XEvents captura tudo sem precisar ler tabelas diretamente
```

No setup, o frontend mostra o script acima para o DBA do cliente executar.

---

## 5. Frontend React

### 5.1 Estrutura de diretórios

```
src/
  app/
    router.tsx               # Config do React Router
    auth-provider.tsx        # Context de auth
    query-provider.tsx       # TanStack Query
  features/
    setup/                   # Wizard inicial
      SetupWizard.tsx
      steps/
        DatabaseConnection.tsx
        ProvisionSchema.tsx
        CreateAdminUser.tsx
        Complete.tsx
    auth/
      LoginPage.tsx
    connections/
      ConnectionList.tsx
      ConnectionEditor.tsx
      ConnectionTest.tsx
    destinations/
      DestinationList.tsx
      DestinationEditor.tsx
    recording/
      RecordingWizard.tsx    # Fluxo completo
      RecordingSession.tsx   # Tela ativa durante gravação
      SessionIdentifier.tsx  # Identifica session_id do SQL
      EventStream.tsx        # Stream ao vivo via SSE
      RecordingReview.tsx    # Revisão pós-gravação
    rules/
      RuleList.tsx
      RuleEditor.tsx         # Edição manual de regra
      RuleGenerated.tsx      # Tela pós-inferência
      RuleSimulator.tsx      # Testa regra contra eventos históricos
    dashboard/
      Dashboard.tsx
      LiveEventsPanel.tsx
      RuleStatusGrid.tsx
  shared/
    api/
      client.ts              # Axios instance
      connections.ts         # Endpoints tipados
      recordings.ts
      rules.ts
      setup.ts
    components/
      ui/                    # shadcn/ui
      EventCard.tsx
      SqlViewer.tsx          # Viewer de SQL com highlight
      JsonViewer.tsx
    hooks/
      useSSE.ts
      useConnectionTest.ts
    types/
      api.ts                 # Tipos compartilhados com o backend
  main.tsx
```

### 5.2 Rotas

```
/setup                    → SetupWizard (acessível só se schema não existe)
/login                    → LoginPage
/                         → Dashboard (autenticado)
/connections              → ConnectionList
/connections/new          → ConnectionEditor
/connections/:id          → ConnectionEditor
/destinations             → DestinationList
/destinations/new         → DestinationEditor
/destinations/:id         → DestinationEditor
/recordings/new           → RecordingWizard
/recordings/:id/session   → RecordingSession
/recordings/:id/review    → RecordingReview
/rules                    → RuleList
/rules/:id                → RuleEditor
/rules/:id/simulate       → RuleSimulator
```

### 5.3 Fluxo do Setup Wizard

Primeira execução do sistema:

1. Frontend faz `GET /api/setup/status`. Se retornar `not_provisioned`, redireciona para `/setup`.

2. **Step 1 — Boas-vindas e pré-requisitos.** Explica o que será feito. Checklist do que o usuário precisa ter em mãos:
   - Credencial de conexão com SQL Server de controle (com permissão de criar schema/tabelas)
   - Credencial do SQL Server alvo (com permissões listadas em 4.2)
   - Host/credencial do RabbitMQ

3. **Step 2 — Conexão com banco de controle.**
   - Formulário: server, porta, autenticação (SQL/Windows), user, senha, database.
   - Botão "Testar conexão" → `POST /api/setup/test-connection`. Backend tenta conectar. Retorna sucesso/falha com mensagem clara.
   - Botão "Continuar" (habilitado só após teste ok).

4. **Step 3 — Provisionar schema.**
   - Tela explica: "Vamos criar o schema `dbsense` e suas tabelas no banco `<X>`."
   - Mostra preview do SQL que será executado (colapsável).
   - Botão "Provisionar" → `POST /api/setup/provision`. Backend executa migrations.
   - Tela mostra progresso (um log em tempo real via SSE seria ideal; no MVP pode ser spinner com steps hardcoded).
   - Após sucesso, mostra resumo: "Criadas N tabelas, versão X.Y.Z."

5. **Step 4 — Criar usuário administrador.**
   - Formulário: username, senha (com regra de força), confirmação.
   - `POST /api/setup/create-admin`.
   - Usuário é criado com role `admin`.

6. **Step 5 — Concluído.**
   - Mensagem de sucesso.
   - Botão "Ir para o sistema" → redireciona para `/login`.

### 5.4 Fluxo de Gravação

Fluxo principal do produto. Precisa ser **excelente** — é onde o cliente sente o diferencial.

#### 5.4.1 Iniciar gravação

Usuário acessa `/recordings/new`.

**Tela 1 — Seleção de contexto.**
- Dropdown: conexão alvo (se só tem uma, pré-selecionada).
- Campo: nome da gravação (ex: "aprovar sinistro").
- Campo: descrição opcional.
- Seção colapsável: **Filtro de sessão** (avançado).
  - Modo automático (default): "Vamos tentar identificar automaticamente sua sessão ao começar a usar o sistema. Pode levar alguns segundos."
  - Modo manual: campos para `host_name`, `app_name`, `login_name` preenchidos pelo usuário.
- Botão "Iniciar gravação" → `POST /api/recordings`, cria registro com status `recording`, emite `start_recording` para o Worker.

**Tela 2 — Sessão ativa.**
Essa é a tela crítica. O usuário precisa sair do DbSense, ir pro sistema legado, fazer a ação, e voltar. A UI precisa orientar isso bem.

Layout sugerido:
- Top bar fixa em vermelho claro: "GRAVANDO — [nome]" + contador de tempo + botão grande vermelho "PARAR GRAVAÇÃO".
- Área principal dividida em 2 colunas:
  - Esquerda: instruções passo-a-passo ("Vá ao seu sistema e execute a operação agora. Quando terminar, volte aqui e clique em parar.").
  - Direita: stream ao vivo de eventos capturados (via SSE em `/api/recordings/:id/events/stream`). Cada evento mostra: timestamp, tipo, tabela/SP, duração, preview do SQL. Scroll automático.
- Se modo automático de identificação de sessão estiver ativo: banner indicando "Identificando sua sessão... aguardando atividade no sistema."
- Indicador de "saúde" da gravação: número de eventos capturados, alerta se zero após 30s.

Quando usuário clica "Parar":
- Confirma: "Parar a gravação? Você capturou N eventos. Se capturou pouca atividade, pode voltar e continuar."
- `POST /api/recordings/:id/stop`.
- Redireciona para `/recordings/:id/review`.

#### 5.4.2 Revisão

Tela `/recordings/:id/review`.

Layout:
- Header: metadata (nome, duração, total de eventos).
- **Seção "Evento de negócio detectado":** card destacado com a regra inferida. Mostra:
  - Nome sugerido (editável)
  - Tabela principal
  - Tipo de operação (INSERT/UPDATE/DELETE em SQL Server)
  - Condições detectadas (ex: "UPDATE em Sinistro WHERE status passa a ser 'APROVADO'")
  - Schema do payload previsto (com exemplo)
  - Chave de partição sugerida
- **Seção "Eventos capturados":** timeline com todos os eventos. Cada evento pode ser:
  - ✓ Incluído na regra (verde)
  - ✗ Ignorado (cinza) — com motivo ("ruído: SET NOCOUNT", "não persiste dados", etc.)
  - ⚠ Ambíguo (amarelo) — o sistema pede decisão do usuário
- **Botões de ação:**
  - "Gravar novamente" — descarta essa gravação e faz nova.
  - "Gravar mais exemplos" (recomendado) — inicia nova gravação vinculada, para generalizar. Usuário executa a mesma ação com dados diferentes. Sistema faz diff e refina.
  - "Ajustar manualmente" — abre editor YAML/formulário para override.
  - "Salvar e testar" — cria regra em status `testing`.

#### 5.4.3 Múltiplas gravações (generalização)

Se usuário fez 2+ gravações vinculadas, a tela de revisão mostra comparação:
- Colunas: valores constantes entre gravações, valores que variaram (viram parâmetros).
- Algoritmo descrito na seção 7.

### 5.5 Identificação de sessão SQL Server

Problema: como saber qual `session_id` do SQL Server corresponde ao usuário gravando?

Estratégia MVP (cascata):

1. **Filtro por `app_name` + `host_name` + `login_name`**: se o usuário informa isso manualmente, filtramos a sessão XEvents por esses valores. Mais confiável, mas exige que o usuário saiba.

2. **Detecção por primeira atividade após start**: durante os primeiros 30s, monitoramos qualquer sessão que mostre atividade. Se apenas uma sessão nova aparece (login_name combina com algum palpite), usa ela. Falha se o ambiente tem muitos usuários ativos.

3. **Fallback: captura tudo, filtra depois**: o usuário executa a ação, sistema mostra todas as sessões que tiveram atividade no período, usuário escolhe qual é a dele. Menos automático mas sempre funciona.

No MVP, implementar opções 1 e 3. Opção 2 em versão futura.

### 5.6 Componentes de UI

Usar **shadcn/ui** como base. Tailwind para estilos. Componentes próprios:

- `SqlViewer`: viewer com syntax highlight (Monaco Editor em modo readonly ou highlight.js + tema T-SQL).
- `EventCard`: card compacto para um evento capturado.
- `EventTimeline`: lista virtualizada (react-virtual) para performance com milhares de eventos.
- `JsonSchema`: preview de schema de evento com exemplo.
- `ConnectionStatus`: badge colorido mostrando status de conexão com animação de teste.

### 5.7 Estado e dados

- **Server state** (dados vindos do backend): TanStack Query. Invalidação explícita após mutações.
- **Client state** (UI, filtros, drafts locais): Zustand para estado global, `useState` para local.
- **Streaming** (eventos em tempo real): EventSource (SSE). Hook `useSSE(url)` que retorna array de mensagens + status.

### 5.8 Autenticação

- Login em `/login` com username/senha.
- Backend retorna JWT com expiração de 8h.
- Token armazenado em `localStorage` (MVP; idealmente `httpOnly cookie` em v2).
- Axios interceptor adiciona `Authorization: Bearer <token>`.
- Interceptor de resposta: se 401, redireciona para `/login`.

---

## 6. Linguagem de Regras

Regras são armazenadas como JSON na coluna `dbsense.rules.definition`. Versionamento semântico por inteiro (`version`).

### 6.1 Schema da regra (MVP)

```jsonc
{
  "id": "rule_uuid",
  "name": "aprovar_sinistro",
  "version": 1,
  "connection_id": "conn_uuid",

  "trigger": {
    // Padrão contra qual evento SQL dispara
    "event_kind": "dml",           // "dml" | "sp_call" | "batch"
    "operation": "update",         // "insert" | "update" | "delete" | "any"
    "database": "CotaSeg",
    "schema": "dbo",
    "table": "Sinistro",
    "predicate": {
      // Expressão que os dados devem satisfazer
      // Linguagem simples: comparações com before/after
      "all": [
        { "field": "after.status", "op": "eq", "value": "APROVADO" },
        { "field": "before.status", "op": "ne", "value": "APROVADO" }
      ]
    }
  },

  "correlation": {
    // Aguardar eventos correlacionados antes de emitir
    "scope": "transaction",        // "transaction" | "time_window" | "none"
    "wait_ms": 2000,               // timeout máximo
    "companions": [
      {
        "event_kind": "dml",
        "operation": "insert",
        "table": "dbo.LogAprovacao",
        "join": { "field": "after.sinistro_id", "equals": "$.after.id" },
        "required": false
      }
    ]
  },

  "shape": {
    // Transformação para o payload publicado
    "sinistro_id": "$.after.id",
    "aprovado_por": "$.after.aprovado_por",
    "data_aprovacao": "$.after.data_aprov",
    "valor_pago": "$.after.valor_pago",
    "status_anterior": "$.before.status",
    "_meta": {
      "source_table": "$trigger.table",
      "captured_at": "$event.timestamp"
    }
  },

  "partition_key": "$.after.id",

  "reaction": {
    // O que o serviço executa quando a regra é triggada.
    // Tipo único, configuração depende do tipo (ver §6.4).
    "type": "rabbit",                // "cmd" | "sql" | "rabbit"
    "config": { /* depende do type */ }
  },

  "reliability": {
    "dedupe_window_s": 60,
    "max_publish_attempts": 5,
    "backoff_strategy": "exponential"
  },

  "metadata": {
    "description": "Emitido quando um sinistro é aprovado",
    "source_recording_ids": ["rec_uuid"],
    "inferred_from_examples": 3,
    "reviewed_by": "user_uuid",
    "reviewed_at": "2026-05-10T14:00:00Z"
  }
}
```

### 6.2 Operadores de predicado suportados (MVP)

- `eq`, `ne`, `gt`, `gte`, `lt`, `lte`
- `in`, `not_in`
- `contains`, `starts_with`, `ends_with`
- `is_null`, `is_not_null`
- Agregados: `all` (AND), `any` (OR), `not`

### 6.3 Linguagem de expressões (`$` paths)

- `$.after.X` — valor novo (em UPDATE/INSERT)
- `$.before.X` — valor anterior (em UPDATE/DELETE)
- `$.changed` — lista de colunas alteradas
- `$event.X` — metadados (`timestamp`, `transaction_id`, `session_id`, `app_name`)
- `$trigger.X` — referência à configuração do trigger
- `$rule.X` — metadata da regra

### 6.4 Tipos de reaction

Toda regra tem **uma** reaction associada. Quando o trigger casa (e os companions
required também), o `ReactionExecutorWorker` despacha de acordo com o `reaction.type`.

#### 6.4.1 `cmd` — executar um comando no servidor do worker

Roda um processo (sem shell, via `Process.Start`) com argumentos parametrizados e
recebe o payload via stdin (JSON) e/ou variáveis de ambiente.

```jsonc
"reaction": {
  "type": "cmd",
  "config": {
    "executable": "/usr/bin/curl",
    "args": ["-X", "POST", "https://meusistema.example/webhook"],
    "send_payload_to_stdin": true,         // payload JSON via STDIN
    "env": {
      "DBSENSE_RULE_ID": "$rule.id",
      "DBSENSE_PAYLOAD": "$payload.json"   // payload completo serializado
    },
    "working_directory": null,
    "timeout_ms": 30000
  }
}
```

Implementação:
- **Sem shell expansion**. `executable` é o caminho do binário; `args` é uma lista, cada item vira um argv separado. Sem ` | `, ` && `, ` > arquivo` etc.
- Templates `$rule.X`, `$payload.json`, `$.after.X` resolvidos antes de executar.
- Stdout/stderr capturados, primeiros 4 KB gravados em `events_log.last_error` se exit code != 0.
- Reação considerada bem-sucedida se exit code == 0 dentro do `timeout_ms`. Caso contrário entra no fluxo de retry/DLQ via outbox.
- Permissões: o usuário do processo do worker precisa ter execute permission no `executable` e acesso ao caminho.

#### 6.4.2 `sql` — executar SQL na conexão alvo (ou outra)

Executa um statement SQL com parâmetros derivados do payload contra uma conexão SQL Server cadastrada.

```jsonc
"reaction": {
  "type": "sql",
  "config": {
    "connection_id": "conn_uuid",          // mesma do trigger ou outra cadastrada
    "sql": "UPDATE dbo.Outbox SET processado=1 WHERE id = @id",
    "parameters": {
      "@id": "$.after.id"
    },
    "command_timeout_ms": 10000
  }
}
```

Implementação:
- `Microsoft.Data.SqlClient` com `SqlCommand` parametrizado (sem string concat).
- A conexão é a mesma cadastrada em `dbsense.connections`; user precisa ter permissão de escrita na tabela alvo (ou direito de execução em SP).
- Suporte a `EXEC sp_xyz @p1=...` direto via `CommandType.StoredProcedure` se `sql` começa com `EXEC `.

#### 6.4.3 `rabbit` — publicar em exchange RabbitMQ

Publica em uma exchange via destino RabbitMQ cadastrado.

```jsonc
"reaction": {
  "type": "rabbit",
  "config": {
    "destination_id": "dest_uuid",         // FK para dbsense.rabbitmq_destinations
    "exchange": "seguradora.sinistros",
    "routing_key": "aprovado",
    "headers": {
      "rule_id": "$rule.id",
      "rule_version": "$rule.version"
    }
  }
}
```

Implementação:
- Usa `RabbitMQ.Client` com publisher confirms (`confirmSelect`).
- Pool de connection por `destination_id`.
- Headers incluem automaticamente `x-idempotency-key`, `x-rule-id`, `x-rule-version`, `content-type: application/json`.

### 6.5 Roteamento da reaction

Independente do tipo, o fluxo é:

1. Matcher detecta o trigger casado, gera o payload via `shape`.
2. Escreve `events_log` + `outbox` na mesma transação.
3. `outbox` carrega `reaction_type` e `reaction_config` resolvidos (placeholders já expandidos).
4. `ReactionExecutorWorker` lê do `outbox` e despacha pra `ICmdReaction`, `ISqlReaction` ou `IRabbitReaction` conforme o tipo.
5. Sucesso → marca `outbox.status='processed'`. Falha → backoff exponencial até `max_publish_attempts`, depois DLQ.

---

## 7. Algoritmo de Inferência de Regras

### 7.1 Entrada

Lista de eventos brutos capturados durante uma ou mais gravações vinculadas, filtrados pela sessão correta.

### 7.2 Pipeline

**Passo 1 — Limpeza/filtragem.**
Remove ruído conhecido:
- `SET` statements (`SET NOCOUNT ON`, `SET ANSI_NULLS`)
- `DECLARE`/comentários
- Queries internas do framework (`sp_reset_connection`, `exec sp_describe_first_result_set`)
- Queries em tabelas do sistema (`INFORMATION_SCHEMA`, `sys.*`)
- SELECTs que não são precondição relevante (heurística: SELECT sem UPDATE/INSERT subsequente)

**Passo 2 — Detecção de transações.**
Agrupa eventos por `transaction_id`. Cada grupo transacional é um evento de negócio candidato.

**Passo 3 — Identificação da tabela principal.**
Dentro de uma transação, a tabela principal é:
- Aquela com UPDATE/INSERT/DELETE
- Preferência a UPDATE (mais comum em operações transacionais)
- Em caso de empate, a mais referenciada em SELECTs anteriores

**Passo 4 — Extração de colunas afetadas.**
Parse do SQL usando o ScriptDom (da Microsoft) ou TSqlParser:
- `UPDATE Sinistro SET status = 'APROVADO', ... WHERE id = 47821` →
  - Tabela: `Sinistro`
  - Colunas alteradas: `status`, outras
  - Predicado do WHERE: `id = 47821` → parâmetro candidato

**Passo 5 — Geração do trigger predicate.**
- Valores em WHERE viram parâmetros (variáveis).
- Valores em SET viram `after.X = <valor>`.
- Se for possível recuperar `before.X` (via SELECT anterior, ou inferindo), adiciona condição `before.X != valor_novo`.

**Passo 6 — Identificação de correlações.**
Outros eventos na mesma transação:
- INSERT em tabela de log/auditoria → correlação opcional
- UPDATE em tabela secundária → correlação obrigatória se compartilha chave
- Chamada de SP → registra como `companions[]`

**Passo 7 — Generalização por múltiplas gravações.**

Se há 2+ gravações, faz diff:
- Valores idênticos entre gravações → constantes na regra
- Valores diferentes → parâmetros
- Eventos presentes em uma gravação mas não em outra → ambiguidade, pede confirmação

Algoritmo:
```
for each event_position in recording_1:
  event_1 = recording_1[event_position]
  event_n = recording_n[event_position]  # mesma posição relativa
  
  if same_structure(event_1, event_n):
    for each field in event_1:
      if value(event_1, field) == value(event_n, field) for all n:
        mark field as CONSTANT
      else:
        mark field as PARAMETER
        record typical type from observed values
  else:
    mark event as OPTIONAL / AMBIGUOUS
```

**Passo 8 — Geração do schema de payload (shape).**
- Cada coluna de `after` das tabelas principais vira campo do payload.
- Nomes podem ser humanizados (snake_case → snake_case, CamelCase → snake_case).
- Tipo inferido do catálogo do SQL Server (consulta a `sys.columns`).

**Passo 9 — Sugestão de partition key.**
- Primary key da tabela principal (consulta a `sys.indexes` + `sys.index_columns`).
- Se não houver PK, sugere primeira coluna que parece ID (`*_id`, `id`, `codigo`).

**Passo 10 — Sugestão de nome e descrição.**
- Nome: baseado em tabela + operação + coluna chave ("aprovar_sinistro" se tabela=Sinistro + SET status=APROVADO).
- Descrição: gerada template ("Emitido quando ocorre UPDATE em Sinistro com status passando para 'APROVADO'").

**Passo 11 — Validação.**
- Tenta executar a regra contra os eventos da gravação original. Deve dar match em 100%.
- Se não der, algo está mal inferido. Reporta erro ao usuário.

**Passo 12 — Retorna candidata.**

### 7.3 Pontos de ambiguidade explícitos

Quando o algoritmo não consegue decidir, emite **perguntas estruturadas** que a UI apresenta ao usuário:

- "O valor 'APROVADO' da coluna `status` é sempre este, ou varia?"
- "Você tocou a tabela X nas 2 gravações, mas com estruturas diferentes. Ela deve ser incluída?"
- "Detectamos 3 possíveis chaves primárias. Qual usar como `partition_key`?"

Essas perguntas são renderizadas na tela de revisão como cards de decisão.

---

## 8. Backend .NET — Arquitetura dos Serviços

### 8.1 Solução

```
DbSense.sln
├── src/
│   ├── DbSense.Api/                    # ASP.NET Core Web API
│   │   ├── Controllers/
│   │   ├── Program.cs
│   │   └── appsettings.json
│   ├── DbSense.Worker/                 # .NET Worker Service
│   │   ├── Workers/
│   │   │   ├── XEventsCollectorWorker.cs
│   │   │   ├── RuleMatcherWorker.cs
│   │   │   ├── ReactionExecutorWorker.cs
│   │   │   └── CommandProcessorWorker.cs
│   │   └── Program.cs
│   ├── DbSense.Core/                   # Biblioteca compartilhada
│   │   ├── Domain/                     # Entidades
│   │   ├── Rules/                      # Engine de matching
│   │   ├── Inference/                  # Algoritmo de inferência
│   │   ├── XEvents/                    # Wrapper XELite
│   │   ├── Reactions/                  # Executors (cmd, sql, rabbit)
│   │   ├── Persistence/                # DbContext
│   │   └── Security/                   # Crypto, hashing
│   └── DbSense.Contracts/              # DTOs, tipos compartilhados
└── tests/
    ├── DbSense.Core.Tests/
    ├── DbSense.Api.Tests/
    └── DbSense.Integration.Tests/
```

### 8.2 DbSense.Api — Controllers

Endpoints principais (REST, JSON):

```
POST   /api/setup/status
POST   /api/setup/test-connection
POST   /api/setup/provision
POST   /api/setup/create-admin

POST   /api/auth/login
POST   /api/auth/refresh

GET    /api/connections
POST   /api/connections
GET    /api/connections/:id
PUT    /api/connections/:id
DELETE /api/connections/:id
POST   /api/connections/:id/test

GET    /api/destinations
POST   /api/destinations
GET    /api/destinations/:id
PUT    /api/destinations/:id
DELETE /api/destinations/:id
POST   /api/destinations/:id/test

POST   /api/recordings
GET    /api/recordings
GET    /api/recordings/:id
POST   /api/recordings/:id/stop
POST   /api/recordings/:id/discard
GET    /api/recordings/:id/events         # paginado
GET    /api/recordings/:id/events/stream  # SSE
POST   /api/recordings/:id/infer-rule     # dispara inferência

GET    /api/rules
POST   /api/rules
GET    /api/rules/:id
PUT    /api/rules/:id
POST   /api/rules/:id/activate
POST   /api/rules/:id/pause
POST   /api/rules/:id/archive
POST   /api/rules/:id/simulate            # testa contra eventos históricos

GET    /api/events-log                    # paginado, com filtros
GET    /api/events-log/stream             # SSE, tempo real
GET    /api/events-log/:id                # detalhe do evento

GET    /api/health
GET    /api/health/worker                 # consulta o Worker
```

### 8.3 DbSense.Worker — Workers

Workers rodam como `IHostedService`. Cada um com responsabilidade única.

#### 8.3.1 CommandProcessorWorker
- Polla `dbsense.worker_commands` a cada 1s (ou LISTEN/NOTIFY equivalente no futuro).
- Processa comandos (start_recording, stop_recording, reload_rules, test_connection).
- Atualiza status.

#### 8.3.2 XEventsCollectorWorker
- Mantém um `XEventStreamer` por conexão ativa.
- Usa `Microsoft.SqlServer.XEvent.XELite` para conectar e streamar eventos.
- Filtra por sessão (quando em gravação) ou captura globalmente (quando em produção).
- Normaliza eventos em `NormalizedSqlEvent` (tipo do Contracts).
- Enfileira para consumo do RuleMatcher.

Configuração da sessão XEvents criada no SQL Server alvo:

```sql
CREATE EVENT SESSION [dbsense_session_<id>] ON SERVER
ADD EVENT sqlserver.sql_batch_completed(
  WHERE (sqlserver.database_name = N'CotaSeg')
),
ADD EVENT sqlserver.rpc_completed(
  WHERE (sqlserver.database_name = N'CotaSeg')
),
ADD EVENT sqlserver.sp_statement_completed(
  WHERE (sqlserver.database_name = N'CotaSeg')
)
ADD TARGET package0.ring_buffer(SET max_memory=(4096))
WITH (EVENT_RETENTION_MODE=ALLOW_SINGLE_EVENT_LOSS, MAX_DISPATCH_LATENCY=1 SECONDS);
```

O Worker consome via **streaming** usando `XELiveEventStreamer` (lê do SQL Server em tempo real, sem arquivos intermediários).

Tratamento de erros:
- Queda de conexão: retry com backoff exponencial (5s, 10s, 20s, max 5min).
- SQL Server reiniciado: reCRIA a sessão se necessário.
- Buffer cheio: loga warning, considera aumentar `max_memory`.

#### 8.3.3 RuleMatcherWorker
- Lê eventos normalizados da fila interna.
- Para cada evento, consulta regras ativas da conexão (cache em memória, invalidado por `reload_rules`).
- Aplica matcher:
  - Filtro rápido por tabela/operação.
  - Avaliação de predicado.
  - Tratamento de correlação (mantém "buffer de transação" em memória, emite evento final quando transação commita ou timeout).
- Se match, gera evento de negócio (aplica `shape`), escreve em `events_log` + `outbox` (mesma transação).

Parser de SQL: usa **Microsoft.SqlServer.TransactSql.ScriptDom** (NuGet `Microsoft.SqlServer.DacFx`). Permite parsear `UPDATE`/`INSERT`/`DELETE` e extrair tabelas, colunas, valores, WHERE predicates.

#### 8.3.4 ReactionExecutorWorker
- Polla `dbsense.outbox` com status `pending`, ordenado por `next_attempt_at`.
- Faz lock pessimista simples: `UPDATE TOP (N) ... SET status='processing', locked_by=@me WHERE status='pending' AND next_attempt_at <= GETUTCDATE() OUTPUT inserted.*`.
- Despacha por `reaction_type` para o handler apropriado:
  - `cmd` → `ICmdReaction` (`Process.Start` sem shell, payload via stdin/env, timeout configurado)
  - `sql` → `ISqlReaction` (`SqlCommand` parametrizado contra `connection_id` cadastrado)
  - `rabbit` → `IRabbitReaction` (publish com `confirmSelect`, pool de conexões por destination)
- Se sucesso: marca `processed`.
- Se falha: grava `last_error`, incrementa `attempts`, calcula próximo `next_attempt_at` com backoff exponencial, ou marca `failed` (DLQ) se excedeu `max_publish_attempts`.
- Configuração: batch de 50, paralelismo de 4 tasks.

#### 8.3.5 HealthCheck endpoint
- HTTP server interno na porta 5001.
- `GET /health` retorna status de cada worker, contagem de eventos processados, latência.

### 8.4 Reactions

Cada `reaction.type` é resolvido por um handler dedicado registrado no DI. O
`ReactionExecutorWorker` faz só o lock + despacho; toda lógica específica vive nos
handlers, em `DbSense.Core/Reactions/`.

```csharp
public interface IReactionHandler
{
    string Type { get; }   // "cmd" | "sql" | "rabbit"
    Task<ReactionResult> ExecuteAsync(ReactionContext ctx, CancellationToken ct = default);
}

public record ReactionContext(
    long EventsLogId,
    string PayloadJson,
    JsonElement Config,         // o reaction_config já com placeholders expandidos
    string IdempotencyKey,
    Guid RuleId,
    int RuleVersion);

public record ReactionResult(
    bool Success,
    string? Error,              // primeiros 4 KB de stdout/stderr (cmd), Message (sql), AMQP error
    int? ExitCode,              // exclusivo do cmd
    long? AffectedRows);        // exclusivo do sql
```

Handlers do MVP:

- **`CmdReactionHandler`** (`type: cmd`)
  - `Process.Start` com `UseShellExecute = false`, `RedirectStandardInput/Output/Error = true`.
  - Sem expansão de shell — `executable` + `args[]` são passados como argv.
  - Se `send_payload_to_stdin = true`, escreve o JSON no stdin e fecha.
  - Aguarda exit com `WaitForExitAsync(timeout_ms)`. Timeout → mata o processo + falha.
  - Sucesso = exit code 0.

- **`SqlReactionHandler`** (`type: sql`)
  - `Microsoft.Data.SqlClient.SqlCommand` com parâmetros explícitos.
  - Connection string montada do `connection_id` (com password decifrada).
  - Idempotência: hash de `(rule_id, payload_idempotency_key)` em coluna user-defined ou skip se cliente preferir.

- **`RabbitReactionHandler`** (`type: rabbit`)
  - Pool de conexões por destination (uma conexão, N channels).
  - `confirmSelect`, `mandatory=true`, retorna após confirm ou timeout de 10s.
  - Headers automáticos: `x-idempotency-key`, `x-rule-id`, `x-rule-version`, `content-type: application/json`.

### 8.5 Segurança

- **Senhas de conexão** (SQL Server, RabbitMQ): criptografadas com AES-256. Chave vem de variável de ambiente `DBSENSE_ENCRYPTION_KEY` (32 bytes base64). Rotação de chave suportada via utilitário CLI.
- **Senhas de usuário**: BCrypt com cost 12.
- **JWT**: HS256 com secret de 512 bits em env `DBSENSE_JWT_SECRET`. Expiração 8h. Refresh token de 30 dias.
- **Logs**: nunca logar payloads SQL completos em produção (podem conter PII). Log redact configurável por regra.
- **TLS**: obrigatório em todas as conexões externas (exceto em localhost dev). Self-signed aceito apenas em modo dev.

---

## 9. Fluxos de Dados Críticos

### 9.1 Fluxo de gravação

```
[Usuário clica "Iniciar"]
  └→ Frontend POST /api/recordings
     └→ Api: cria registro em dbsense.recordings (status=recording)
     └→ Api: cria comando em dbsense.worker_commands (start_recording)
     └→ Api: retorna recording_id

[Worker pica o comando]
  └→ CommandProcessorWorker: lê comando
     └→ XEventsCollectorWorker: inicia sessão XEvents filtrada
     └→ Eventos chegam
     └→ Cada evento: INSERT em dbsense.recording_events + pub SSE

[Frontend subscreve SSE]
  └→ GET /api/recordings/:id/events/stream
     └→ Renderiza eventos em tempo real

[Usuário clica "Parar"]
  └→ Frontend POST /api/recordings/:id/stop
     └→ Api: comando stop_recording
     └→ Worker: encerra sessão XEvents, atualiza recording.status=completed
     └→ Api: dispara inferência (POST interno ou background job)

[Inferência]
  └→ InferenceService lê dbsense.recording_events do recording
     └→ Aplica pipeline (seção 7)
     └→ Gera rule candidate (status=draft)
     └→ Retorna JSON para o frontend
```

### 9.2 Fluxo de produção (regra ativa)

```
[Evento ocorre no SQL Server alvo]
  └→ XEvents captura e envia para XEventsCollectorWorker
     └→ Normaliza evento

[Matcher avalia]
  └→ RuleMatcherWorker recebe evento
     └→ Consulta cache de regras ativas
     └→ Para cada regra que faz trigger match:
        └→ Aplica predicate
        └→ Se correlation.scope=transaction: aguarda commit ou timeout
        └→ Se match final:
           └→ Gera payload (aplica shape)
           └→ BEGIN TRANSACTION
              └→ INSERT em events_log
              └→ INSERT em outbox
           └→ COMMIT

[Reaction executa]
  └→ ReactionExecutorWorker acorda (ou é notificado)
     └→ Lock pessimista em outbox rows pending
     └→ Despacha por reaction_type:
        ├→ cmd:    Process.Start com payload via stdin/env (timeout)
        ├→ sql:    SqlCommand parametrizado contra connection_id
        └→ rabbit: publish com confirmSelect na exchange/routing_key
     └→ Atualiza outbox.status=processed + events_log.publish_status=published
```

### 9.3 Garantias

- **At-least-once delivery** para RabbitMQ (pode duplicar em falha de confirm; idempotency key mitiga do lado do consumer).
- **Ordering** dentro da mesma `partition_key` (garantido se o publisher usa routing por hash consistente; opcional no MVP).
- **Durabilidade**: exchange durable + queue durable + persistent messages configurados no destino.
- **Transactional integrity** entre events_log e outbox (mesma transação SQL).

---

## 10. Configuração e Deploy

### 10.1 Variáveis de ambiente

```
# Banco de controle
DBSENSE_CONTROL_DB_CONNECTION="Server=...;Database=dbsense_control;User Id=...;Password=...;TrustServerCertificate=true"

# Segurança
DBSENSE_ENCRYPTION_KEY="<base64 32 bytes>"
DBSENSE_JWT_SECRET="<base64 64 bytes>"
DBSENSE_JWT_EXPIRATION_HOURS=8

# Worker
DBSENSE_WORKER_INSTANCE_ID="worker-01"
DBSENSE_WORKER_HTTP_PORT=5001
DBSENSE_OUTBOX_BATCH_SIZE=50
DBSENSE_OUTBOX_POLL_INTERVAL_MS=500

# Api
DBSENSE_API_PORT=5000
DBSENSE_CORS_ORIGINS="https://dbsense.cliente.com"

# Logs
DBSENSE_LOG_LEVEL=Information
DBSENSE_LOG_REDACT_SQL=true
```

### 10.2 docker-compose (dev)

```yaml
version: "3.9"
services:
  dbsense-control-db:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: "Y"
      SA_PASSWORD: "Dev@2026!"
    ports: ["1433:1433"]

  dbsense-api:
    build:
      context: .
      dockerfile: src/DbSense.Api/Dockerfile
    environment:
      DBSENSE_CONTROL_DB_CONNECTION: "Server=dbsense-control-db;..."
      # ...
    ports: ["5000:5000"]
    depends_on: [dbsense-control-db]

  dbsense-worker:
    build:
      context: .
      dockerfile: src/DbSense.Worker/Dockerfile
    environment:
      DBSENSE_CONTROL_DB_CONNECTION: "Server=dbsense-control-db;..."
      # ...
    ports: ["5001:5001"]
    depends_on: [dbsense-control-db]

  rabbitmq:
    image: rabbitmq:3.12-management
    ports: ["5672:5672", "15672:15672"]

  sql-server-target:
    # SQL Server simulando o do cliente (para testes)
    image: mcr.microsoft.com/mssql/server:2019-latest
    environment:
      ACCEPT_EULA: "Y"
      SA_PASSWORD: "Dev@2026!"
    ports: ["1434:1433"]
```

### 10.3 Empacotamento para produção

- Imagens Docker versionadas e publicadas em registry (cliente ou Anthropic/seu).
- Deploy on-premises via docker-compose.yml fornecido ao cliente.
- Cliente provisiona os 3 contêineres: api, worker, (opcional) rabbitmq/sql control.
- Healthchecks configurados em cada container.

---

## 11. Testes

### 11.1 Unitários (`DbSense.Core.Tests`)
- Algoritmo de inferência: snapshots de traces reais → asserção sobre regra gerada.
- Motor de matching: cenários de predicate, correlação, transação.
- Parser SQL: casos de UPDATE/INSERT/DELETE com variações.

### 11.2 Integração (`DbSense.Integration.Tests`)
- Sobe SQL Server em container (Testcontainers for .NET).
- Executa operações SQL reais contra o banco alvo.
- Verifica captura XEvents, matching, publicação em RabbitMQ (também em container).

### 11.3 E2E
- Playwright contra o frontend + backend real.
- Cenário: setup → cria conexão → grava → inferência → ativa regra → executa SQL → verifica mensagem no RabbitMQ.

---

## 12. Roadmap Post-MVP

Features deliberadamente fora do MVP, mas que a arquitetura deve permitir sem refactor radical:

- **Debezium como motor alternativo**: abstrair `IEventCollector`. Implementação XEvents no MVP; adicionar DebeziumEngineCollector depois.
- **Suporte multi-banco**: PostgreSQL, MySQL, Oracle via Debezium.
- **Shadow mode**: regra executa em paralelo sem publicar, compara com regra "oficial".
- **Replay histórico**: reprocessa `recording_events` antigos contra nova regra.
- **Orquestrador de workers gerenciados**: consumers no próprio DbSense chamam APIs externas.
- **Multi-tenancy verdadeiro**: isolamento por tenant, billing.
- **SSO**: SAML/OAuth.
- **RBAC granular**: permissões por conexão/regra.
- **LLM-assisted inference**: quando heurística falha, fallback para LLM.
- **Catálogo de templates**: regras prontas para sistemas conhecidos (TOTVS, SAP B1).
- **App companion**: gravador desktop que captura UI + SQL correlacionados.

---

## 13. Riscos Técnicos Conhecidos

| Risco | Mitigação |
|---|---|
| Identificação de sessão XEvents falha em ambiente com muitos usuários | Fallback manual sempre disponível (usuário escolhe sessão pós-gravação) |
| Parser SQL falha em queries com SQL dinâmico ou sintaxe complexa | ScriptDom cobre ~95% de T-SQL válido; para casos restantes, regra fica em modo "raw trigger" (dispara em qualquer match de tabela+op, usuário revisa payload) |
| Overhead de XEvents em SQL Server carregado | Benchmark documentado, recomendação de filtros por database/table, alerta de drop de eventos |
| Outbox cresce indefinidamente se RabbitMQ fica indisponível | Alerta em dashboard, retenção configurável, DLQ após N tentativas |
| Transações longas (>2s) causam eventos fora de ordem | Correlation com timeout configurável + warning no UI |
| Crash do Worker em meio a processamento | Idempotency key no outbox evita duplicação; events_log mantém rastro |
| Eventos duplicados após reconexão XEvents | Dedupe baseado em `(timestamp, transaction_id, statement_hash)` em janela configurável |

---

## 14. Cronograma Sugerido (MVP — 16 semanas)

| Semana | Marco |
|---|---|
| 1-2 | Scaffold solução .NET, projeto React, docker-compose, migrations iniciais, auth básica |
| 3-4 | XEventsCollector funcional (stream de eventos para console), Publisher RabbitMQ simples |
| 5-6 | Fluxo de gravação completo (UI + backend + persistência de eventos brutos) |
| 7-8 | Algoritmo de inferência v1 (heurístico, sem generalização multi-gravação) |
| 9-10 | Motor de matching + transactional outbox + publisher com confirmações |
| 11 | Setup wizard e fluxo de onboarding completo |
| 12 | Dashboard de eventos em tempo real (SSE) |
| 13 | Generalização por múltiplas gravações + UI de revisão refinada |
| 14 | Hardening: erros, retries, observabilidade, testes de integração |
| 15 | Teste em cliente piloto (instalação real, feedback) |
| 16 | Correções, documentação, release MVP |

---

## 15. Definição de Pronto (MVP)

O MVP está pronto quando:

1. Um usuário técnico consegue, sem intervenção externa:
   - Executar o setup wizard completo em menos de 15 minutos
   - Cadastrar uma conexão SQL Server e validar
   - Cadastrar um destino RabbitMQ e validar
   - Gravar uma operação simples (UPDATE numa tabela) e ver a regra candidata gerada
   - Ajustar a regra, ativar
   - Ver o evento sendo publicado no RabbitMQ ao executar a operação novamente no SQL Server
2. Todos os fluxos principais têm cobertura de teste de integração.
3. A arquitetura suporta trocar XEvents por Debezium sem refactor do frontend nem da camada de regras.
4. Documentação de instalação, operação e troubleshooting está completa.
5. Demo gravada (vídeo de 10min) mostrando o fluxo end-to-end.

---

**Fim do documento.**
