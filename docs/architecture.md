# Arquitetura

Documentação técnica do pipeline interno do DbSense — decisões de design, contratos
entre os módulos, e armadilhas conhecidas. Para visão geral, ver `../README.md`. Para
guia de reactions, ver `reactions.md`.

## Pipeline

```
   App alvo                 SQL Server (XEvents)            DbSense.Worker
   ────────                 ────────────────────            ──────────────
   INSERT/                 ┌─ sql_batch_completed ──┐       ┌──────────────────┐
   UPDATE/      ────────►  │                        │ ────► │ RecordingCollector│
   DELETE                  └─ rpc_completed ────────┘       │ (1 por gravação) │
                                       │                    └─────┬────────────┘
                                       │                          │ parseia
                                       │                          ▼
                                       │                    recording_events
                                       │                    (com parsed_payload)
                                       │
                                       └────────► ProductionXeStream
                                                  (1 por connection ativa)
                                                          │
                                                          ▼
                                                   RuleMatcherWorker
                                                          │
                                            ┌─────────────┴─────────────┐
                                            ▼                           ▼
                                       OnEvent (engine)            SweepExpired
                                       absorve em pendings         drena pendings
                                       avalia trigger              que excederam
                                       cria pending OU             wait_ms (warning)
                                       match imediato
                                            │
                                            ▼ (quando completo)
                                       OutboxEnqueuer
                                       expande placeholders contra raw payload
                                            │
                                            ▼ (mesma transação)
                                       events_log + outbox
                                            │
                                            ▼
                                       ReactionExecutorWorker
                                       lock-and-execute com retry exponencial
                                            │
                                            ▼
                                       Cmd / Sql / Rabbit handler
```

## Componentes principais

### XEvents — `RecordingCollector` e `ProductionXeStream`

Duas XE sessions por escopo distinto:

- **Recording** — uma session por gravação ativa. Filtros vêm do `recordings.filter_*` (host/app/login).
  Persiste eventos brutos em `recording_events` pra revisão humana e inferência.
- **Production** — uma session por `connection` que tem alguma rule ativa. Sem filtro de host/app
  (escuta tudo no database). Eventos vão direto pro `RuleMatcherWorker` em memória.

Ambos os DDLs adicionam **`sql_batch_completed`** + **`rpc_completed`**, com filtro
`client_app_name <> N'DbSense.Worker'` pra excluir o tráfego do próprio worker
(polling do ring buffer + reactions SQL).

#### Decisão: por que NÃO `sp_statement_completed`

A escolha óbvia parecia capturar `sp_statement_completed` pra ter granularidade
statement-by-statement. Foi tentado e abandonado por dois problemas:

1. **Perde valores dos parâmetros**. Pra um `EXEC sp_executesql N'UPDATE ...', @p0=N'Sigma'`,
   o `sp_statement_completed.statement` traz só `UPDATE ... SET col = @p0` — sem o valor.
   Os valores estão *no batch_text do `rpc_completed`*. Sem o `rpc_completed`, o parser
   não tem mapa pra resolver `@pN` e `dml.Values` fica vazio.

2. **Duplica eventos** se ativado em conjunto com `rpc_completed`: o EF gera 1 RPC + N
   statements internos, todos com timestamps diferentes. A idempotency key (que inclui
   timestamp) não dedupa — reaction dispara N+1 vezes.

A combinação `sql_batch_completed + rpc_completed` funciona porque os canais **não se
sobrepõem**:

- Batches ad-hoc (SSMS, scripts) → só `sql_batch_completed`.
- Chamadas RPC (sp_executesql do EF, EXEC dbo.proc) → só `rpc_completed`.

Trade-off aceito: stored procs `EXEC dbo.MinhaProc @p=...` não geram DMLs no parser
(não temos acesso ao corpo da proc). Pra esse caso, o app precisaria emitir DMLs ad-hoc
ou via sp_executesql.

#### Identificação do tráfego do próprio Worker

Toda conexão SqlClient aberta pelo Worker seta `ApplicationName = "DbSense.Worker"`
(constante `ProductionXeStream.SelfAppName`). Isso inclui:

- Polling do ring buffer (`RecordingCollector` / `RuleMatcherWorker`).
- DDL de criação/drop das XE sessions.
- Reactions SQL (`SqlReactionHandler`).

Sem isso, o driver `Microsoft.Data.SqlClient` envia `"Core Microsoft SqlClient Data Provider"`
como `client_app_name` (default), o filtro XE não exclui, e os INSERTs feitos pelas próprias
reactions vão pro recording — poluindo a inferência e potencialmente disparando rules
recursivamente.

---

### `SqlParser` (ScriptDom)

Recebe o `batch_text` cru e retorna `IReadOnlyList<ParsedDml>` com:

```csharp
public record ParsedDml(
    DmlOperation Operation,           // Insert | Update | Delete
    string? Schema,
    string Table,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ParsedPredicate> Where,
    IReadOnlyDictionary<string, string?> Values   // col → valor literal resolvido
);
```

Fluxo:

1. **`TryUnwrapSpExecuteSql`** detecta o padrão `EXEC sp_executesql N'...', N'@p0...', @p0=N'val'`,
   lê o SQL embutido (parameters[0]), pula a string de declarações de tipo (parameters[1]),
   e popula um mapa `paramMap[@pN] → valor` a partir de parameters[2..]. Retorna `(innerSql, paramMap)`.

2. **`ParseAllFromScript(innerSql, paramMap)`** parseia o SQL embutido com `TSql160Parser`,
   itera os statements, e produz um `ParsedDml` por DML. Pra cada coluna em
   `INSERT VALUES (...)` ou `UPDATE SET col = expr`, chama `ExtractScalarValue(expr, paramMap)`:

   - `Literal` → o valor literal direto.
   - `VariableReference` (`@pN`) → resolve via `paramMap`.
   - `NullLiteral` → null.
   - Outros (function calls, expressões binárias) → null (não suportado).

3. Pra batches ad-hoc (sem sp_executesql), `paramMap` fica vazio e os literais entram direto.

`Where` extrai igualdades simples (`col = @p` ou `col = 'literal'`) com `eq` ou `ne`. Outras
formas (`>`, `LIKE`, `IN`) ainda não são suportadas.

---

### `RuleEngine` (stateful)

Singleton no DI. Mantém `ConcurrentDictionary<connectionId, ConnectionState>` onde cada
`ConnectionState` tem uma `List<PendingMatch>` protegida por `lock`.

**Contrato:**

```csharp
public interface IRuleEngine
{
    IReadOnlyList<RuleMatch> OnEvent(
        Guid connectionId, string databaseName, ParsedDml dml, EventContext ctx);

    IReadOnlyList<ExpiredMatch> SweepExpired(DateTime now);
}

public record RuleMatch(Rule Rule, JsonElement Payload, JsonElement RawPayload, string IdempotencyKeySuffix);
```

**`OnEvent` em duas fases:**

1. **Absorver em pendings existentes**: para cada pending compatível com o evento (scope OK
   e ainda dentro da janela), tenta consumir 1 companion required. Se a lista zera, o pending
   completa e vira `RuleMatch`.
2. **Avaliar trigger**: para cada rule ativa na conexão, se o trigger casa, monta `rawPayload`
   e `shaped`. Se a rule tem `requiredCompanions.Count == 0`, match imediato. Senão, cria
   um novo `PendingMatch` com `Deadline = ts + wait_ms`.

`SweepExpired` é chamado a cada tick pelo `RuleMatcherWorker` — drena pendings cujo deadline
passou e os retorna como `ExpiredMatch` (logados como Warning, não viram reaction).

#### Scopes de correlação

| Scope | Filtro pra absorção | Quando expira |
|---|---|---|
| `time_window` | `eventTs <= pending.Deadline` | `now > Deadline` |
| `transaction` | `eventTxId == pending.TriggerTransactionId` | `now > Deadline` (deadline ainda existe pra não vazar memória se a TX morrer sem commit) |
| `none` (com required > 0) | fallback pra `time_window` | igual |

#### Idempotência

A idempotency key é **ancorada no trigger**:

```
{connectionId:N}:{trigger_ts:O}:{trigger_session}:{dml_index_no_batch}:{op}:{table}
```

Calculada no momento em que o trigger é avaliado, congelada no `PendingMatch`. Quando o
match completa (companions chegam), a mesma key vai pra `events_log.IdempotencyKey` (UNIQUE
index). Reentregas do mesmo trigger event tentam re-inserir e batem na unique violation —
tratada como "já enqueuado", silenciosa.

#### Raw vs Shaped payload

`OnEvent` constrói **dois** payloads:

- **`rawPayload`** — `{after: {col→val}, _meta: {captured_at, table, schema, operation}}`.
- **`shaped`** — resultado de `TryApplyShape(rule, rawPayload)`. Se a rule tem `shape` na
  definition, expande os placeholders do shape contra o rawPayload e retorna o resultado.
  Senão, é igual ao rawPayload.

Ambos vão pro `RuleMatch`. O `OutboxEnqueuer` usa:

- **Shaped** → grava em `events_log.EventPayload` e `outbox.Payload` (visível pro consumer).
- **Raw** → passa pro `PlaceholderExpander` ao expandir a `reaction.config`.

A separação é importante porque o shape pode renomear/remover campos (ex: `after.NomeFantasia`
vira `nome_fantasia` no root). Se o expander resolvesse contra o shaped, `$.after.X` deixaria
de funcionar quando há shape ativo. Resolvendo contra o raw, `$.after.X` sempre funciona.

---

### `OutboxEnqueuer`

Recebe `EnqueueRequest(rule, payload, rawPayload, sqlTimestamp, idempotencyKeySuffix)`. Faz tudo
em uma transação SQL (via `ExecutionStrategy` pra ser replay-safe):

1. **Extrai reaction** da `rule.definition` (`reaction.type` + `reaction.config`).
2. **Expande placeholders** na config usando `rawPayload` (ver `reactions.md` pra tokens suportados).
3. **Calcula idempotency key**: `SHA256(ruleId:version:suffix)` truncado a 32 chars (UNIQUE em events_log).
4. **Insere `events_log`** com payload shaped + idempotency key + `publish_status='pending'`.
5. **Insere `outbox`** com `Payload`, `ReactionType`, `ReactionConfig` (já resolvido), status pending.
6. Commit.

Se a key colide (UNIQUE violation no events_log), trata como reentrega — não reenvia.

---

### `ReactionExecutorWorker`

Polling no `outbox` com lock por instância:

```sql
UPDATE TOP (N) outbox
SET LockedBy = @instance, LockedUntil = DATEADD(...)
OUTPUT inserted.*
WHERE Status = 'pending' AND NextAttemptAt <= GETUTCDATE()
  AND (LockedUntil IS NULL OR LockedUntil < GETUTCDATE())
```

Pra cada item travado, dispatcha pelo `Type` em `IReactionHandler` (`cmd`, `sql`, `rabbit`).
Sucesso → `Status = 'sent'`. Falha → incrementa `Attempts`, agenda próxima tentativa com
backoff exponencial até `max_publish_attempts` (default 5), então marca `failed`.

`OutboxSchemaMigrator.EnsureUpToDateAsync` roda no startup do Worker pra garantir que o schema
da outbox tá no formato com `ReactionType`/`ReactionConfig`/`LastError` (migra dados antigos
que tinham `Exchange`/`RoutingKey`/`Headers` direto).

`RecordingSchemaMigrator.EnsureUpToDateAsync` roda em paralelo pra garantir que
`recording_events.ParsedPayload` (nvarchar(max)) existe — adicionado depois da v1 do schema.

---

### `RecordingsService` e ciclo de vida de gravação

```
StartAsync ──► insere row em recordings (status=recording)
              insere worker_command 'start_recording'
              CommandProcessorWorker → IRecordingCollector.StartAsync
                                         │
                                         ▼
                                   cria XE session "dbsense_rec_{id:N}"
                                   inicia poll loop async

StopAsync  ──► seta status='completed', stoppedAt
              insere worker_command 'stop_recording'
              CommandProcessorWorker → IRecordingCollector.StopAsync
                                         │
                                         ▼
                                   cancela poll loop
                                   DROP XE session

DiscardAsync ──► igual a stop, mas seta status='discarded'

DeleteAsync ──► bloqueia se status='recording' (race com persist do collector)
                Rules.SourceRecordingId = NULL (preserva regras inferidas)
                ExecuteDeleteAsync em recording_events
                Remove row de recordings
```

---

### Inferência (`InferenceService` heurístico, `LlmInferenceService` opcional)

Recebe `recording_id`, lê os events parseados, e tenta:

1. **Heurística**: classifica os DMLs como main/correlation/noise por proximidade temporal
   (mesmo evento, mesma transação, ou ±wait_ms do main escolhido). Gera `InferredRulePreview`
   com trigger + companions + shape default.

2. **LLM** (Anthropic, opcional via `Llm:Provider`): mostra a descrição humana + os events
   classificados pra um modelo Claude, que escolhe o main e marca companions/noise. Útil
   quando há vários DMLs próximos no tempo e a heurística não distingue.

Os dois rodam em paralelo (`Task.WhenAll`) e o usuário vê os dois cards no review.
Se o LLM não tem chave configurada, só o heurístico aparece.

---

## Migrations

O projeto não usa migrations versionadas (FluentMigrator/EF Migrations) — são duas estratégias:

1. **`EnsureCreatedAsync`** no setup wizard cria o schema completo na primeira execução.
2. **Migrators idempotentes** rodam no startup do Worker pra adicionar coisas após v1:
   - `OutboxSchemaMigrator` — DROP+CREATE da `outbox` se detectar formato antigo (Exchange/RoutingKey/Headers).
     Aceitável só em dev — perderia mensagens em produção.
   - `RecordingSchemaMigrator` — `IF COL_LENGTH IS NULL ALTER TABLE ADD ParsedPayload nvarchar(max) NULL`. Não-destrutivo.

Quando migrations versionadas forem adicionadas, esses migrators podem virar primeira migration.

---

## Connection naming convention

Toda `SqlConnectionStringBuilder` aberta pelo Worker pra o database alvo (não pro controle)
deve setar `ApplicationName = ProductionXeStream.SelfAppName` (`"DbSense.Worker"`). Isso garante
que o filtro `client_app_name <> N'DbSense.Worker'` das XE sessions exclua o tráfego.

Pontos atuais:

- ✅ `RecordingCollector.BuildConnectionString`
- ✅ `RuleMatcherWorker.BuildConnectionString`
- ✅ `ProductionXeStream` (DDL via mesma cs)
- ✅ `SqlReactionHandler.ExecuteAsync`

Se adicionar um novo handler/serviço que abre conn no banco alvo, **lembre-se de marcar**.
Sem isso, eventos da operação aparecem no recording com `client_app_name = "Core Microsoft SqlClient Data Provider"`
(default do driver) e poluem a inferência ou disparam rules recursivamente.
