import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";

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
};

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
  rabbit: { destinationId: "", exchange: "", routingKey: "", headersJson: "{}" },
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
          <code className="font-mono">$.after.&lt;coluna&gt;</code> — valor da coluna no INSERT/UPDATE
          (ou no WHERE do DELETE). Os valores ficam visíveis no review da gravação.
        </li>
        <li>
          <code className="font-mono">$event.timestamp</code> — timestamp do evento que disparou.
        </li>
        <li>
          <code className="font-mono">$rule.id</code> e <code className="font-mono">$rule.version</code> — identificam a regra.
        </li>
      </ul>
      <p className="mt-1 text-slate-500">
        Use os macros em qualquer campo (executável/args, SQL, routing key, headers, JSON de parâmetros).
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
  return (
    <div className="space-y-3 rounded-md border bg-muted/30 p-3">
      <div className="space-y-2">
        <Label htmlFor="sql-conn">Connection ID</Label>
        <Input
          id="sql-conn"
          value={sql.connectionId}
          onChange={(e) => onChange({ ...sql, connectionId: e.target.value })}
          placeholder="UUID da conexão SQL Server cadastrada"
        />
        <p className="text-xs text-muted-foreground">
          Pode ser a mesma do trigger ou outra conexão registrada em /connections.
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
  return (
    <div className="space-y-3 rounded-md border bg-muted/30 p-3">
      <div className="space-y-2">
        <Label htmlFor="rabbit-dest">Destination ID</Label>
        <Input
          id="rabbit-dest"
          value={rabbit.destinationId}
          onChange={(e) => onChange({ ...rabbit, destinationId: e.target.value })}
          placeholder="UUID do destino RabbitMQ cadastrado"
        />
      </div>

      <div className="grid grid-cols-2 gap-3">
        <div className="space-y-2">
          <Label htmlFor="rabbit-exchange">Exchange</Label>
          <Input
            id="rabbit-exchange"
            value={rabbit.exchange}
            onChange={(e) => onChange({ ...rabbit, exchange: e.target.value })}
            placeholder="seguradora.sinistros"
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="rabbit-rk">Routing key</Label>
          <Input
            id="rabbit-rk"
            value={rabbit.routingKey}
            onChange={(e) => onChange({ ...rabbit, routingKey: e.target.value })}
            placeholder="aprovado"
          />
        </div>
      </div>

      <div className="space-y-2">
        <Label htmlFor="rabbit-headers">Headers (JSON)</Label>
        <textarea
          id="rabbit-headers"
          value={rabbit.headersJson}
          onChange={(e) => onChange({ ...rabbit, headersJson: e.target.value })}
          rows={3}
          className="w-full rounded-md border border-input bg-background px-3 py-2 font-mono text-xs"
          placeholder={'{ "rule_id": "$rule.id" }'}
        />
        <p className="text-xs text-muted-foreground">
          Headers automáticos (<code>x-idempotency-key</code>, <code>x-rule-id</code>,
          <code> x-rule-version</code>, <code>content-type</code>) são adicionados pelo worker.
        </p>
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
      if (!state.rabbit.exchange.trim()) return { valid: false, reason: "Exchange é obrigatória." };
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
    case "rabbit":
      return {
        type: "rabbit",
        config: {
          destination_id: state.rabbit.destinationId.trim(),
          exchange: state.rabbit.exchange.trim(),
          routing_key: state.rabbit.routingKey.trim(),
          headers: safeJsonParse(state.rabbit.headersJson) ?? {},
        },
      };
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
