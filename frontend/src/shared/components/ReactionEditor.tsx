import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { Alert } from "@/shared/components/ui/alert";
import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import {
  listRabbitDestinations,
  type RabbitDestinationListItem,
} from "@/shared/api/rabbit-destinations";
import { listConnections, type ConnectionListItem } from "@/shared/api/connections";

export type ReactionType = "none" | "cmd" | "sql" | "rabbit";

export type CmdReactionConfig = {
  executable: string;
  args: string[];
  sendPayloadToStdin: boolean;
  timeoutMs: number;
};

export type SqlReactionConfig = {
  connectionId: string;
  sql: string;
  parametersJson: string;     // JSON livre, validado no save
  commandTimeoutMs: number;
};

export type RabbitReactionConfig = {
  destinationId: string;
  exchange: string;
  routingKey: string;
  headersJson: string;
  // Corpo opcional. Vazio → o worker publica o PayloadJson do evento como está.
  // Aceita placeholders ($payload.json, $.after.X, etc).
  body: string;
};

// Headers escritos automaticamente pelo RabbitReactionHandler. Se o usuário definir
// um header com mesmo nome via config.headers, o último vence (Dictionary merge no
// handler) — o que quebra telemetria e idempotência. Avisamos no editor.
const RESERVED_HEADER_PREFIX = "x-dbsense-";
const AUTO_HEADERS = [
  "x-dbsense-rule-id",
  "x-dbsense-rule-version",
  "x-dbsense-idempotency-key",
  "x-dbsense-events-log-id",
];

export type ReactionState = {
  type: ReactionType;
  cmd: CmdReactionConfig;
  sql: SqlReactionConfig;
  rabbit: RabbitReactionConfig;
};

export const emptyReactionState = (): ReactionState => ({
  type: "none",
  cmd: { executable: "", args: [], sendPayloadToStdin: true, timeoutMs: 30000 },
  sql: { connectionId: "", sql: "", parametersJson: "{}", commandTimeoutMs: 10000 },
  rabbit: { destinationId: "", exchange: "", routingKey: "", headersJson: "{}", body: "" },
});

export function ReactionEditor({
  state,
  onChange,
}: {
  state: ReactionState;
  onChange: (next: ReactionState) => void;
}) {
  const setType = (t: ReactionType) => onChange({ ...state, type: t });
  const setCmd = (c: CmdReactionConfig) => onChange({ ...state, cmd: c });
  const setSql = (s: SqlReactionConfig) => onChange({ ...state, sql: s });
  const setRabbit = (r: RabbitReactionConfig) => onChange({ ...state, rabbit: r });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Reação ao disparar</CardTitle>
        <CardDescription>
          O que o serviço executa quando a regra é triggada. Tipos: <strong>cmd</strong> roda um
          processo no host do worker, <strong>sql</strong> executa SQL parametrizado, <strong>rabbit</strong> publica
          em uma exchange. Você pode salvar a regra sem reaction e configurar depois.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="flex flex-wrap gap-2">
          {(["none", "cmd", "sql", "rabbit"] as const).map((t) => (
            <button
              key={t}
              type="button"
              onClick={() => setType(t)}
              className={`rounded-md border px-3 py-1.5 text-sm transition-colors ${
                state.type === t
                  ? "border-primary bg-primary text-primary-foreground"
                  : "border-border bg-background hover:bg-muted"
              }`}
            >
              {t === "none" ? "sem reaction (rascunho)" : t}
            </button>
          ))}
        </div>

        {state.type === "cmd" && <CmdEditor cmd={state.cmd} onChange={setCmd} />}
        {state.type === "sql" && <SqlEditor sql={state.sql} onChange={setSql} />}
        {state.type === "rabbit" && <RabbitEditor rabbit={state.rabbit} onChange={setRabbit} />}

        {state.type !== "none" && <PlaceholdersHelp />}
      </CardContent>
    </Card>
  );
}

function PlaceholdersHelp() {
  return (
    <div className="rounded-md border border-dashed border-slate-300 bg-slate-50 p-3 text-xs">
      <p className="mb-1 font-semibold text-slate-700">Macros disponíveis</p>
      <ul className="space-y-0.5 text-slate-600">
        <li>
          <code className="font-mono">$.after.&lt;coluna&gt;</code> — valor da linha após INSERT/UPDATE
          (ou os filtros do WHERE no DELETE).
        </li>
        <li>
          <code className="font-mono">$.before.&lt;coluna&gt;</code> — valor anterior; só populado
          pras colunas que apareceram em predicados <code>=</code> do WHERE (ex.:{" "}
          <code>WHERE status='A'</code> → <code>before.status='A'</code>). Coluna em{" "}
          <code>SET</code> sem filtro correspondente fica <code>null</code> (XEvent não captura row
          state).
        </li>
        <li>
          <code className="font-mono">$payload.json</code> — payload completo do evento como JSON string.
        </li>
        <li>
          <code className="font-mono">$rule.id</code>, <code className="font-mono">$rule.version</code> — identificam a regra.
        </li>
        <li>
          <code className="font-mono">$event.timestamp</code> — timestamp capturado (alias de <code>$._meta.captured_at</code>).
        </li>
        <li>
          <code className="font-mono">$trigger.table</code>, <code className="font-mono">$trigger.schema</code>,{" "}
          <code className="font-mono">$trigger.operation</code> — metadados do trigger.
        </li>
      </ul>
      <p className="mt-1 text-slate-500">
        Use os macros em qualquer string da config (args do cmd, SQL, routing key, body, headers, JSON de parâmetros).
        Resolução acontece quando o evento é enfileirado no outbox.
      </p>
    </div>
  );
}

function CmdEditor({
  cmd,
  onChange,
}: {
  cmd: CmdReactionConfig;
  onChange: (c: CmdReactionConfig) => void;
}) {
  return (
    <div className="space-y-3 rounded-md border bg-muted/30 p-3">
      <div className="space-y-2">
        <Label htmlFor="cmd-exec">Executável</Label>
        <Input
          id="cmd-exec"
          value={cmd.executable}
          onChange={(e) => onChange({ ...cmd, executable: e.target.value })}
          placeholder="/usr/bin/curl ou C:\\Tools\\webhook.exe"
        />
        <p className="text-xs text-muted-foreground">
          Caminho absoluto. Sem shell expansion — não use <code>|</code>, <code>&amp;&amp;</code>,
          redirects.
        </p>
      </div>

      <div className="space-y-2">
        <Label htmlFor="cmd-args">Argumentos (um por linha)</Label>
        <textarea
          id="cmd-args"
          value={cmd.args.join("\n")}
          onChange={(e) =>
            onChange({
              ...cmd,
              args: e.target.value.split("\n").map((s) => s.trim()).filter(Boolean),
            })
          }
          rows={4}
          className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
          placeholder={"-X\nPOST\nhttps://meu-sistema.com/webhook"}
        />
      </div>

      <div className="grid grid-cols-2 gap-4">
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={cmd.sendPayloadToStdin}
            onChange={(e) => onChange({ ...cmd, sendPayloadToStdin: e.target.checked })}
          />
          Enviar payload JSON via stdin
        </label>
        <div className="space-y-1">
          <Label htmlFor="cmd-timeout">Timeout (ms)</Label>
          <Input
            id="cmd-timeout"
            type="number"
            min={1000}
            step={1000}
            value={cmd.timeoutMs}
            onChange={(e) =>
              onChange({ ...cmd, timeoutMs: Number(e.target.value) || 30000 })
            }
          />
        </div>
      </div>
    </div>
  );
}

function SqlEditor({
  sql,
  onChange,
}: {
  sql: SqlReactionConfig;
  onChange: (s: SqlReactionConfig) => void;
}) {
  // Lista as conexões cadastradas pra dropdown. Mesmo padrão do RabbitEditor:
  // se a API falhar, cai pra input livre — assim usuários conseguem colar UUID
  // manualmente em ambientes sem listagem.
  const [connections, setConnections] = useState<ConnectionListItem[] | null>(null);
  const [connError, setConnError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    listConnections()
      .then((c) => { if (!cancelled) setConnections(c); })
      .catch((e) => { if (!cancelled) setConnError(e instanceof Error ? e.message : "Falha ao listar conexões."); });
    return () => { cancelled = true; };
  }, []);

  // Cobre conexão herdada de uma versão anterior da rule que sumiu da listagem.
  const selectedMissing =
    sql.connectionId !== "" &&
    connections !== null &&
    !connections.some((c) => c.id === sql.connectionId);

  return (
    <div className="space-y-3 rounded-md border bg-muted/30 p-3">
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <Label htmlFor="sql-conn">Conexão SQL Server</Label>
          <Link
            to="/connections/new"
            target="_blank"
            rel="noreferrer"
            className="text-xs text-primary hover:underline"
          >
            + cadastrar nova
          </Link>
        </div>
        {connError ? (
          <>
            <Input
              id="sql-conn"
              value={sql.connectionId}
              onChange={(e) => onChange({ ...sql, connectionId: e.target.value })}
              placeholder="UUID da conexão"
            />
            <p className="text-xs text-destructive">
              Falha ao carregar conexões ({connError}). Cole o UUID manualmente.
            </p>
          </>
        ) : (
          <select
            id="sql-conn"
            value={sql.connectionId}
            disabled={connections === null}
            onChange={(e) => onChange({ ...sql, connectionId: e.target.value })}
            className="flex h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
          >
            <option value="">
              {connections === null ? "Carregando…" : "— selecione uma conexão —"}
            </option>
            {connections?.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name} ({c.server}/{c.database}
                {c.status === "error" ? " ⚠ erro no último teste" : ""}
                {c.status === "inactive" ? " • nunca testada" : ""})
              </option>
            ))}
            {selectedMissing && (
              <option value={sql.connectionId}>
                {sql.connectionId} (não encontrada)
              </option>
            )}
          </select>
        )}
        {selectedMissing && (
          <Alert variant="destructive" className="text-xs">
            A conexão <code>{sql.connectionId}</code> não está mais cadastrada. Selecione
            outra antes de salvar.
          </Alert>
        )}
        {connections !== null && connections.length === 0 && !connError && (
          <p className="text-xs text-muted-foreground">
            Nenhuma conexão cadastrada.{" "}
            <Link to="/connections/new" className="text-primary hover:underline">
              Cadastre a primeira
            </Link>
            .
          </p>
        )}
        <p className="text-xs text-muted-foreground">
          Pode ser a mesma conexão do trigger ou outra registrada em /connections.
        </p>
      </div>

      <div className="space-y-2">
        <Label htmlFor="sql-text">SQL</Label>
        <textarea
          id="sql-text"
          value={sql.sql}
          onChange={(e) => onChange({ ...sql, sql: e.target.value })}
          rows={4}
          className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
          placeholder={"UPDATE dbo.Outbox SET processado = 1 WHERE id = @id"}
        />
        <p className="text-xs text-muted-foreground">
          Use parâmetros <code>@nome</code>. <code>EXEC sp_xyz @p1=...</code> é tratado como stored procedure.
        </p>
      </div>

      <div className="space-y-2">
        <Label htmlFor="sql-params">Parâmetros (JSON com paths $.after.X)</Label>
        <textarea
          id="sql-params"
          value={sql.parametersJson}
          onChange={(e) => onChange({ ...sql, parametersJson: e.target.value })}
          rows={3}
          className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
          placeholder={'{ "@id": "$.after.id" }'}
        />
      </div>

      <div className="space-y-1">
        <Label htmlFor="sql-timeout">Timeout do comando (ms)</Label>
        <Input
          id="sql-timeout"
          type="number"
          min={1000}
          step={1000}
          value={sql.commandTimeoutMs}
          onChange={(e) =>
            onChange({ ...sql, commandTimeoutMs: Number(e.target.value) || 10000 })
          }
        />
      </div>
    </div>
  );
}

function RabbitEditor({
  rabbit,
  onChange,
}: {
  rabbit: RabbitReactionConfig;
  onChange: (r: RabbitReactionConfig) => void;
}) {
  // Carrega lista de destinos pra dropdown. Falha silenciosamente — no pior caso
  // o usuário ainda pode colar um UUID via fallback (mostrado quando há erro).
  const [destinations, setDestinations] = useState<RabbitDestinationListItem[] | null>(null);
  const [destError, setDestError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    listRabbitDestinations()
      .then((d) => { if (!cancelled) setDestinations(d); })
      .catch((e) => { if (!cancelled) setDestError(e instanceof Error ? e.message : "Falha ao listar destinos."); });
    return () => { cancelled = true; };
  }, []);

  // Resolve o destino selecionado pra mostrar default_exchange como hint do campo exchange.
  const selectedDestination = destinations?.find((d) => d.id === rabbit.destinationId);

  // Detecta se o JSON dos headers tenta sobrescrever um header reservado.
  // O handler faz merge "user vence", então isso quebraria telemetria/idempotência
  // sem dar erro — só conseguimos avisar daqui.
  const headerWarnings = (() => {
    const parsed = safeJsonParse(rabbit.headersJson);
    if (!parsed || typeof parsed !== "object") return [];
    return Object.keys(parsed as object).filter((k) =>
      k.toLowerCase().startsWith(RESERVED_HEADER_PREFIX),
    );
  })();

  // Usuário pode ter selecionado um destino (anterior) que foi apagado, ou pode existir
  // um valor herdado de uma versão antiga da rule sem destino correspondente.
  const selectedMissing =
    rabbit.destinationId !== "" &&
    destinations !== null &&
    !destinations.some((d) => d.id === rabbit.destinationId);

  return (
    <div className="space-y-3 rounded-md border bg-muted/30 p-3">
      <div className="space-y-2">
        <div className="flex items-center justify-between">
          <Label htmlFor="rabbit-dest">Destino RabbitMQ</Label>
          <Link
            to="/rabbit-destinations/new"
            target="_blank"
            rel="noreferrer"
            className="text-xs text-primary hover:underline"
          >
            + cadastrar novo
          </Link>
        </div>
        {destError ? (
          <>
            <Input
              id="rabbit-dest"
              value={rabbit.destinationId}
              onChange={(e) => onChange({ ...rabbit, destinationId: e.target.value })}
              placeholder="UUID do destino"
            />
            <p className="text-xs text-destructive">
              Falha ao carregar destinos ({destError}). Cole o UUID manualmente.
            </p>
          </>
        ) : (
          <select
            id="rabbit-dest"
            value={rabbit.destinationId}
            disabled={destinations === null}
            onChange={(e) => onChange({ ...rabbit, destinationId: e.target.value })}
            className="flex h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
          >
            <option value="">
              {destinations === null ? "Carregando…" : "— selecione um destino —"}
            </option>
            {destinations?.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name} ({d.host}
                {d.virtualHost && d.virtualHost !== "/" ? `${d.virtualHost}` : ""}
                {d.status === "error" ? " ⚠ erro no último teste" : ""}
                {d.status === "inactive" ? " • nunca testado" : ""})
              </option>
            ))}
            {selectedMissing && (
              <option value={rabbit.destinationId}>
                {rabbit.destinationId} (não encontrado)
              </option>
            )}
          </select>
        )}
        {selectedMissing && (
          <Alert variant="destructive" className="text-xs">
            O destino <code>{rabbit.destinationId}</code> não está mais cadastrado. Cadastre
            um novo ou selecione outro antes de salvar.
          </Alert>
        )}
        {destinations !== null && destinations.length === 0 && !destError && (
          <p className="text-xs text-muted-foreground">
            Nenhum destino cadastrado.{" "}
            <Link to="/rabbit-destinations/new" className="text-primary hover:underline">
              Cadastre o primeiro
            </Link>
            .
          </p>
        )}
      </div>

      <div className="grid grid-cols-2 gap-3">
        <div className="space-y-2">
          <Label htmlFor="rabbit-exchange">Exchange</Label>
          <Input
            id="rabbit-exchange"
            value={rabbit.exchange}
            onChange={(e) => onChange({ ...rabbit, exchange: e.target.value })}
            placeholder={
              selectedDestination?.defaultExchange
                ? `default: ${selectedDestination.defaultExchange}`
                : "(default exchange do broker)"
            }
          />
          <p className="text-xs text-muted-foreground">
            Vazio cai pra <code>default_exchange</code> do destino
            {selectedDestination?.defaultExchange && (
              <> (atualmente <code>{selectedDestination.defaultExchange}</code>)</>
            )}
            . Se ambos forem vazios, publica na default exchange do broker — nesse caso a
            routing key precisa bater com o nome de uma fila.
          </p>
        </div>
        <div className="space-y-2">
          <Label htmlFor="rabbit-rk">Routing key</Label>
          <Input
            id="rabbit-rk"
            value={rabbit.routingKey}
            onChange={(e) => onChange({ ...rabbit, routingKey: e.target.value })}
            placeholder="orders.changed ou $trigger.table"
          />
          <p className="text-xs text-muted-foreground">
            Mensagem é publicada com <code>mandatory=true</code>: sem binding pra essa
            routing key, o broker devolve e a reaction falha.
          </p>
        </div>
      </div>

      <div className="space-y-2">
        <Label htmlFor="rabbit-body">Body (opcional)</Label>
        <textarea
          id="rabbit-body"
          value={rabbit.body}
          onChange={(e) => onChange({ ...rabbit, body: e.target.value })}
          rows={3}
          className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
          placeholder={'{ "type": "$trigger.operation", "data": $payload.json }'}
        />
        <p className="text-xs text-muted-foreground">
          Vazio publica o payload do evento como está. Use placeholders pra moldar um envelope.
          Content-type é sempre <code>application/json</code>; <code>delivery_mode=2</code> (persistente);
          o publish espera ack do broker (<code>WaitForConfirmsOrDie</code>, timeout 10 s).
        </p>
      </div>

      <div className="space-y-2">
        <Label htmlFor="rabbit-headers">Headers extras (JSON)</Label>
        <textarea
          id="rabbit-headers"
          value={rabbit.headersJson}
          onChange={(e) => onChange({ ...rabbit, headersJson: e.target.value })}
          rows={3}
          className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
          placeholder={'{ "tenant": "$.after.tenant_id" }'}
        />
        <p className="text-xs text-muted-foreground">
          O worker já injeta automaticamente:{" "}
          {AUTO_HEADERS.map((h, i) => (
            <span key={h}>
              {i > 0 ? ", " : ""}
              <code>{h}</code>
            </span>
          ))}
          {", "}além de <code>message_id</code> = idempotency key.
        </p>
        {headerWarnings.length > 0 && (
          <Alert variant="destructive" className="text-xs">
            Headers começando com <code>{RESERVED_HEADER_PREFIX}</code> são reservados e serão
            sobrescritos pelos do worker, quebrando telemetria/idempotência. Renomeie:{" "}
            {headerWarnings.map((h, i) => (
              <span key={h}>
                {i > 0 ? ", " : ""}
                <code>{h}</code>
              </span>
            ))}
          </Alert>
        )}
      </div>
    </div>
  );
}

export function reactionStateValid(state: ReactionState): { valid: boolean; reason?: string } {
  switch (state.type) {
    case "none":
      return { valid: true };
    case "cmd":
      if (!state.cmd.executable.trim()) return { valid: false, reason: "Executável é obrigatório." };
      return { valid: true };
    case "sql":
      if (!state.sql.connectionId.trim()) return { valid: false, reason: "Connection ID é obrigatório." };
      if (!state.sql.sql.trim()) return { valid: false, reason: "SQL é obrigatório." };
      if (!safeJsonParse(state.sql.parametersJson)) return { valid: false, reason: "Parâmetros: JSON inválido." };
      return { valid: true };
    case "rabbit":
      if (!state.rabbit.destinationId.trim()) return { valid: false, reason: "Destination ID é obrigatório." };
      // Exchange e routing key podem ser vazias — o handler cai pra default_exchange
      // do destination ou pra default exchange do broker (routing key vira nome de fila).
      if (!safeJsonParse(state.rabbit.headersJson)) return { valid: false, reason: "Headers: JSON inválido." };
      return { valid: true };
  }
}

export function mergeReactionIntoDefinition(
  definitionJson: string,
  state: ReactionState,
): string {
  const obj = JSON.parse(definitionJson);
  obj.reaction = buildReactionObject(state);
  return JSON.stringify(obj, null, 2);
}

function buildReactionObject(state: ReactionState) {
  switch (state.type) {
    case "none":
      return null;
    case "cmd":
      return {
        type: "cmd",
        config: {
          executable: state.cmd.executable.trim(),
          args: state.cmd.args,
          send_payload_to_stdin: state.cmd.sendPayloadToStdin,
          timeout_ms: state.cmd.timeoutMs,
        },
      };
    case "sql":
      return {
        type: "sql",
        config: {
          connection_id: state.sql.connectionId.trim(),
          sql: state.sql.sql,
          parameters: safeJsonParse(state.sql.parametersJson) ?? {},
          command_timeout_ms: state.sql.commandTimeoutMs,
        },
      };
    case "rabbit": {
      const config: Record<string, unknown> = {
        destination_id: state.rabbit.destinationId.trim(),
        exchange: state.rabbit.exchange.trim(),
        routing_key: state.rabbit.routingKey.trim(),
        headers: safeJsonParse(state.rabbit.headersJson) ?? {},
      };
      // Só inclui body se preenchido — assim o handler usa o default (PayloadJson).
      const body = state.rabbit.body;
      if (body !== "") config.body = body;
      return { type: "rabbit", config };
    }
  }
}

export function extractReactionFromDefinition(definitionJson: string): ReactionState {
  const base = emptyReactionState();
  try {
    const obj = JSON.parse(definitionJson);
    const r = obj?.reaction;
    if (!r || typeof r !== "object") return base;
    if (r.type === "cmd") {
      return {
        ...base,
        type: "cmd",
        cmd: {
          executable: String(r.config?.executable ?? ""),
          args: Array.isArray(r.config?.args) ? r.config.args.map(String) : [],
          sendPayloadToStdin: r.config?.send_payload_to_stdin !== false,
          timeoutMs: Number(r.config?.timeout_ms ?? 30000) || 30000,
        },
      };
    }
    if (r.type === "sql") {
      return {
        ...base,
        type: "sql",
        sql: {
          connectionId: String(r.config?.connection_id ?? ""),
          sql: String(r.config?.sql ?? ""),
          parametersJson: JSON.stringify(r.config?.parameters ?? {}, null, 2),
          commandTimeoutMs: Number(r.config?.command_timeout_ms ?? 10000) || 10000,
        },
      };
    }
    if (r.type === "rabbit") {
      return {
        ...base,
        type: "rabbit",
        rabbit: {
          destinationId: String(r.config?.destination_id ?? ""),
          exchange: String(r.config?.exchange ?? ""),
          routingKey: String(r.config?.routing_key ?? ""),
          headersJson: JSON.stringify(r.config?.headers ?? {}, null, 2),
          body: typeof r.config?.body === "string" ? r.config.body : "",
        },
      };
    }
  } catch {
    // ignora — começa em "none"
  }
  return base;
}

function safeJsonParse(s: string): unknown | null {
  try {
    const v = JSON.parse(s);
    return typeof v === "object" && v !== null ? v : null;
  } catch {
    return null;
  }
}
