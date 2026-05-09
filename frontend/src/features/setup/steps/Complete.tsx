import { useEffect, useState } from "react";
import { Button } from "@/shared/components/ui/button";
import { Alert } from "@/shared/components/ui/alert";
import { finalizeSetup, type FinalizeSetupRequest, type FinalizeSetupResponse } from "@/shared/api/setup";

type FinalizeState =
  | { kind: "idle" }
  | { kind: "running" }
  | { kind: "done"; response: FinalizeSetupResponse }
  | { kind: "error"; message: string };

export function Complete({
  connection,
  onFinish,
}: {
  // Pode ser null se o setup começou em pending_admin (passo 4) — nesse caso
  // pulamos o finalize porque não temos a connection que o usuário aprovou.
  connection: FinalizeSetupRequest | null;
  onFinish: () => void;
}) {
  const [state, setState] = useState<FinalizeState>(
    connection ? { kind: "running" } : { kind: "idle" },
  );

  useEffect(() => {
    if (!connection) return;
    let cancelled = false;
    finalizeSetup(connection)
      .then((response) => {
        if (!cancelled) setState({ kind: "done", response });
      })
      .catch((e) => {
        if (!cancelled) {
          setState({
            kind: "error",
            message: e instanceof Error ? e.message : "Falha ao finalizar setup",
          });
        }
      });
    return () => {
      cancelled = true;
    };
  }, [connection]);

  return (
    <div className="space-y-4">
      <div className="rounded-md border border-emerald-300 bg-emerald-50 p-4 text-sm text-emerald-900">
        Configuração concluída. Faça login para começar a usar o DbSense.
      </div>

      {state.kind === "running" && (
        <Alert>Persistindo configurações no sistema operacional...</Alert>
      )}

      {state.kind === "done" && state.response.envVarsPersisted && (
        <Alert variant="success">
          Variáveis de ambiente persistidas no escopo do usuário ({state.response.persistedKeys.length}):{" "}
          <code className="text-xs">{state.response.persistedKeys.join(", ")}</code>. Próximas
          execuções vão usar essas configs direto do SO.
        </Alert>
      )}

      {state.kind === "done" && !state.response.envVarsPersisted && state.response.error && (
        <Alert>{state.response.error}</Alert>
      )}

      {state.kind === "error" && (
        <Alert variant="destructive">
          Não foi possível persistir as variáveis: {state.message}. As configs continuam vindo
          do <code>dbsense.config.json</code>.
        </Alert>
      )}

      <div className="flex justify-end">
        <Button onClick={onFinish} disabled={state.kind === "running"}>
          Concluir
        </Button>
      </div>
    </div>
  );
}
