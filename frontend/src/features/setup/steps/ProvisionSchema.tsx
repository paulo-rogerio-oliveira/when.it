import { useState } from "react";
import { Button } from "@/shared/components/ui/button";
import { Alert } from "@/shared/components/ui/alert";
import { provision, type TestConnectionRequest } from "@/shared/api/setup";

type State =
  | { kind: "idle" }
  | { kind: "provisioning" }
  | { kind: "done"; schemaVersion: string; tablesCreated: number }
  | { kind: "error"; message: string; hint?: string | null; code?: string | null };

export function ProvisionSchema({
  connection,
  onBack,
  onContinue,
}: {
  connection: TestConnectionRequest;
  onBack: () => void;
  onContinue: () => void;
}) {
  const [state, setState] = useState<State>({ kind: "idle" });

  const run = async () => {
    setState({ kind: "provisioning" });
    try {
      const r = await provision(connection);
      if (r.success) {
        setState({ kind: "done", schemaVersion: r.schemaVersion, tablesCreated: r.tablesCreated });
      } else {
        setState({
          kind: "error",
          message: r.error ?? "Falha ao provisionar",
          hint: r.hint,
          code: r.errorCode,
        });
      }
    } catch (e) {
      setState({
        kind: "error",
        message: e instanceof Error ? e.message : "Falha ao contactar a API",
      });
    }
  };

  return (
    <div className="space-y-4">
      <p className="text-sm">
        Vamos criar o schema <code className="rounded bg-muted px-1">dbsense</code> e todas as
        tabelas de controle no banco{" "}
        <strong className="font-semibold">{connection.database}</strong> em{" "}
        <strong className="font-semibold">{connection.server}</strong>.
      </p>

      <details className="rounded-md border bg-muted/30 p-3 text-sm">
        <summary className="cursor-pointer font-medium">Tabelas que serão criadas</summary>
        <ul className="mt-2 list-disc space-y-1 pl-6 text-muted-foreground">
          <li>connections, rabbitmq_destinations</li>
          <li>recordings, recording_events</li>
          <li>rules, events_log, outbox</li>
          <li>users, audit_log, worker_commands</li>
          <li>setup_info</li>
        </ul>
      </details>

      {state.kind === "done" && (
        <Alert variant="success">
          Schema criado. Versão <strong>{state.schemaVersion}</strong>, {state.tablesCreated} tabelas.
        </Alert>
      )}
      {state.kind === "error" && (
        <Alert variant="destructive">
          <div className="space-y-2">
            {state.hint && <p className="font-medium">{state.hint}</p>}
            <details className={state.hint ? "text-xs opacity-80" : undefined}>
              <summary className="cursor-pointer">
                {state.hint ? "Mensagem do servidor" : "Detalhes"}
                {state.code ? ` (${state.code})` : null}
              </summary>
              <pre className="mt-1 whitespace-pre-wrap break-words font-mono text-xs">
                {state.message}
              </pre>
            </details>
          </div>
        </Alert>
      )}

      <div className="flex justify-between">
        <Button variant="outline" onClick={onBack} disabled={state.kind === "provisioning"}>
          Voltar
        </Button>
        {state.kind === "done" ? (
          <Button onClick={onContinue}>Continuar</Button>
        ) : (
          <Button onClick={run} disabled={state.kind === "provisioning"}>
            {state.kind === "provisioning" ? "Provisionando..." : "Provisionar"}
          </Button>
        )}
      </div>
    </div>
  );
}
