# Reactions

Toda regra ativa tem **uma** reaction associada — o que o serviço executa quando o
trigger casa (e os companions required são satisfeitos). Este doc descreve os 3 tipos
suportados, os macros disponíveis nos placeholders, e armadilhas comuns.

Para visão geral, ver `../README.md`. Para arquitetura interna, ver `architecture.md`.

## Tipos

| Tipo | Quando usar | Saída |
|---|---|---|
| **`cmd`** | Disparar um processo (webhook curl, script, integração local) | Process exit code + stdout/stderr |
| **`sql`** | Escrever em outro schema/banco (auditoria, cache materializado, "outbox externo") | Linhas afetadas |
| **`rabbit`** | Publicar evento de domínio em uma exchange | Mensagem na fila |

A configuração vai em `rule.definition.reaction` — o `ReactionEditor` no front edita
esse bloco. Estrutura:

```json
{
  "reaction": {
    "type": "cmd" | "sql" | "rabbit",
    "config": { /* específico por tipo */ }
  }
}
```

## Como os placeholders são resolvidos

No momento do enqueue, o `OutboxEnqueuer.EnqueueAsync` chama
`PlaceholderExpander.Expand(reaction.config, rawPayload, rule.id, rule.version)`. O expander
varre o JSON da config e, **para cada string que é exatamente um placeholder**, substitui
pelo valor resolvido.

Importante: a substituição é **string inteira**, não interpolação. `"foo $.after.X bar"`
**não** vira `"foo Acme bar"` — o expander só age se a string for `"$.after.X"` direto.
Pra concatenação, use o tipo cmd com argumentos separados, ou parameters separados em SQL.

A config resolvida fica gravada em `outbox.ReactionConfig` (commit junto com `events_log`).
Retries reusam essa config — sem reexpansão, sem risco de divergência.

### Tabela de macros

| Token | Resolve pra |
|---|---|
| `$payload.json` | JSON inteiro do payload (raw) como string |
| `$rule.id` | UUID da rule |
| `$rule.version` | Inteiro |
| `$.after.<col>` | Valor da coluna no after-image (INSERT VALUES / UPDATE SET / WHERE eq do DELETE) |
| `$._meta.captured_at` | ISO timestamp do trigger event |
| `$._meta.table` | Nome da tabela do trigger |
| `$._meta.schema` | Schema do trigger |
| `$._meta.operation` | `insert` / `update` / `delete` |
| `$.<path>` | Qualquer caminho dentro do payload (ex: `$.after._meta.region` se o app gravou nested) |
| `$event.timestamp` | Alias pra `$._meta.captured_at` |
| `$trigger.table` | Alias pra `$._meta.table` |
| `$trigger.schema` | Alias pra `$._meta.schema` |
| `$trigger.operation` | Alias pra `$._meta.operation` |

Os aliases `$event.X` e `$trigger.X` foram adicionados pra compat com shapes gerados pelo
`InferenceService` (que usa esse formato no `_meta` do shape). Em rules novas, prefira
`$.X`/`$._meta.X`.

### Quando o valor não resolve

- **Path inexistente** → vira `null` no JSON resolvido (não erro).
- **Coluna sem valor extraível** (ex: `SET x = CASE WHEN ... THEN ... END`) → fica fora do
  `dml.Values`; o placeholder cai no fallback do `BuildAfterFields` que registra a chave
  com string vazia → `null` no resolved.

Quando aparecer `null` onde você esperava um valor:

1. Confira `dbsense.recording_events.parsed_payload` do evento — `values` deve listar a coluna.
2. Se `values` está vazio mas `columns` lista a coluna, o parser viu o nome mas não conseguiu
   extrair o valor (expressão complexa ou Worker rodando build sem o `TryUnwrapSpExecuteSql`).

---

## `cmd` reaction

Roda um processo no host do worker. **Sem shell** — não usa `cmd.exe` ou `bash`. Não interpreta
pipes, redirecionamentos, glob.

### Config

```json
{
  "type": "cmd",
  "config": {
    "executable": "C:\\Tools\\webhook.exe",
    "args": ["--cnpj", "$.after.Cnpj", "--evento", "$.empresa.criada"],
    "send_payload_to_stdin": true,
    "timeout_ms": 30000,
    "env": { "API_KEY": "..." }
  }
}
```

### Como o processo recebe os dados

- **`executable`** — caminho absoluto. Sem PATH lookup.
- **`args`** — array de strings, cada uma como `argv[i]`. Placeholders resolvem por **string inteira**:
  `"$.after.Cnpj"` vira `"12.345..."`. Strings literais (`"--cnpj"`) passam batido.
- **`send_payload_to_stdin`** (default true) — escreve o `Payload` (shaped) JSON no stdin
  do processo, fecha. Use no curl pra mandar o body do webhook:
  ```json
  "args": ["-X", "POST", "https://hooks.example.com/dbsense", "--data-binary", "@-"]
  ```
- **`env`** — variáveis de ambiente extras. Cada valor é uma string que passa pela mesma
  expansão de placeholders.
- Sempre injetadas: `DBSENSE_RULE_ID`, `DBSENSE_RULE_VERSION`, `DBSENSE_IDEMPOTENCY_KEY`.

### Resultado

`exit code 0` → success. Qualquer outro → failure, vai pra retry. `stdout`/`stderr` (até 4 KB
cada) ficam em `outbox.LastError` em caso de falha.

### Exemplo: webhook

```json
{
  "type": "cmd",
  "config": {
    "executable": "/usr/bin/curl",
    "args": [
      "-sSf", "-X", "POST",
      "-H", "Content-Type: application/json",
      "-H", "X-Idempotency-Key: $rule.id",
      "--data-binary", "@-",
      "https://meu-sistema/eventos/empresa-criada"
    ],
    "send_payload_to_stdin": true,
    "timeout_ms": 10000
  }
}
```

---

## `sql` reaction

Executa SQL parametrizado contra uma `connection` cadastrada (não precisa ser a mesma do
trigger).

### Config

```json
{
  "type": "sql",
  "config": {
    "connection_id": "uuid-da-connection",
    "sql": "INSERT INTO dbo.audit_empresa(cnpj, nome, criado_em) VALUES (@cnpj, @nome, @ts)",
    "parameters": {
      "@cnpj": "$.after.Cnpj",
      "@nome": "$.after.RazaoSocial",
      "@ts": "$event.timestamp"
    },
    "command_timeout_ms": 10000
  }
}
```

### Importante: NÃO interpolar valores no SQL

```sql
-- ❌ ERRADO — placeholder não substitui dentro de string
INSERT INTO Log(text) VALUES ('$.after.NomeFantasia')

-- ✅ CERTO — parâmetro nomeado, expander resolve em parameters
sql: "INSERT INTO Log(text) VALUES (@nome)"
parameters: { "@nome": "$.after.NomeFantasia" }
```

O expander só substitui quando o valor inteiro de uma string é um placeholder. `'$.after.X'`
dentro do SQL é uma string maior, então passa batido — o INSERT acaba escrevendo a string
literal `$.after.NomeFantasia` no banco. Pior: interpolação inline em SQL é injection
esperando acontecer.

A forma certa é separar **SQL fixo** (com `@param`) de **valores** (em `parameters`). O
handler usa `cmd.Parameters.AddWithValue("@param", value)`, que envia parametrizado pelo
ADO.NET — escape automático, tipo correto, sem injection.

### Stored procedure

`SqlReactionHandler` detecta `EXEC ` no início do SQL e troca pra `CommandType.StoredProcedure`:

```json
{
  "sql": "EXEC dbo.RegistrarMudancaEmpresa",
  "parameters": {
    "@id": "$.after.Id",
    "@nome": "$.after.NomeFantasia"
  }
}
```

Os parâmetros vão como input. Output parameters não são lidos no MVP.

### Resultado

Exception em `OpenAsync`/`ExecuteNonQueryAsync` → failure, retry. Sucesso → `outbox.AffectedRows`
guarda o retorno do `ExecuteNonQuery`.

---

## `rabbit` reaction

Publica em uma exchange via destination cadastrado.

### Config

```json
{
  "type": "rabbit",
  "config": {
    "destination_id": "uuid-do-rabbitmq-destination",
    "exchange": "events",
    "routing_key": "empresa.criada.$.after.Porte",
    "headers": {
      "tenant": "$.after.Cnpj",
      "rule_id": "$rule.id"
    },
    "body": "$payload.json"
  }
}
```

### Notas

- **`routing_key`** — placeholders são resolvidos. `"empresa.criada.$.after.Porte"` vira
  `"empresa.criada.Grande"`. Mantém um único placeholder por chave (não interpola).
- **`headers`** — objeto livre; cada valor passa pelo expander.
- **`body`** — geralmente `"$payload.json"` (envia o shaped inteiro). Pode ser uma string
  literal ou outro placeholder.

---

## Padrões úteis

### "Auditoria de tudo"

Rule que casa qualquer UPDATE em qualquer tabela do schema, reaction `sql` insere em uma
tabela `audit_log` com idempotency key como chave única. O "$payload.json" no body do header
preserva a operação inteira pra debug.

### "Webhook + retry"

`cmd` chamando curl com `--fail-with-body`, `timeout_ms` curto (2-3s). O `max_publish_attempts`
+ `backoff_strategy: "exponential"` da rule cuidam dos retries — o webhook do cliente só
precisa ser idempotente em relação ao header `X-Idempotency-Key: $rule.id`.

### "Cascata de regras"

Rule A (UPDATE empresas) tem reaction SQL que faz INSERT em `eventos_emitidos`. Rule B tem
trigger em `INSERT eventos_emitidos`. **Não vai disparar** — a connection da reaction usa
`ApplicationName = "DbSense.Worker"`, e o filtro da production XE session exclui esse nome.
Se você quer cascatear de propósito, faça via `cmd` ou `rabbit` (que rotam por outros canais).

---

## Troubleshooting

### `outbox.ReactionConfig` tem `parameters: { "@x": null }`

O placeholder não resolveu. Cheque `dbsense.recording_events.parsed_payload` do evento que
disparou — se `values` está vazio, o parser não conseguiu extrair o valor da coluna. Causas
comuns:

- Worker rodando build antigo (sem `TryUnwrapSpExecuteSql` ou sem `rpc_completed`).
- A coluna está num `SET col = CASE ... END` ou `func(...)` que o parser não decompõe.

### Reaction dispara mas o INSERT na tabela tem `null`

Verifica se você usou parameters separados (`@param` no SQL + `parameters: { "@param": "$.after.X" }`)
e não interpolação inline (que não é suportada).

### Reaction dispara duas vezes

Antes da fix de `rpc_completed`/`sp_statement_completed`, o EF gerava 2 eventos pro mesmo SQL
e a idempotency key (que inclui timestamp) não dedupava. Confirme que o Worker em execução é
build de hoje. Se persistir, cole o `outbox` (especialmente os campos `IdempotencyKey` e o
DDL atual da XE session via `SELECT * FROM sys.dm_xe_sessions`).

### Eventos das próprias reactions aparecem no recording

A connection da reaction não está marcada com `ApplicationName = "DbSense.Worker"`. Toda
conexão SqlClient aberta pelo Worker pra o banco alvo deve setar isso (ver `architecture.md`,
seção "Connection naming convention"). Se você adicionou um handler novo, lembre-se de marcar.

### "Pending match expirou sem completar" no log

A rule espera 1+ companions required que não chegaram dentro de `correlation.wait_ms`. Causas:

- A operação atual não inclui o companion (ex: rule inferida com 2 UPDATEs gravados, mas operação
  real tem só 1). Solução: editar a rule e remover o companion required, ou adicionar predicate
  no trigger pra restringir os casos onde a rule se aplica.
- Companion chegou após a janela. Aumentar `wait_ms`.
- Companion vem de outro `client_app_name` que está sendo filtrado. Conferir o filtro da XE.
