import { useState } from "react";
import { Button } from "@/shared/components/ui/button";
import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Alert } from "@/shared/components/ui/alert";
import { testConnection, type TestConnectionRequest } from "@/shared/api/setup";

type TestState =
  | { kind: "idle" }
  | { kind: "testing" }
  | { kind: "ok"; elapsedMs: number }
  | { kind: "error"; message: string };

export function DatabaseConnection({
  onContinue,
}: {
  onContinue: (connection: TestConnectionRequest) => void;
}) {
  const [server, setServer] = useState("localhost,1433");
  const [database, setDatabase] = useState("dbsense_control");
  const [authType, setAuthType] = useState<"sql" | "windows">("sql");
  const [username, setUsername] = useState("sa");
  const [password, setPassword] = useState("");
  const [state, setState] = useState<TestState>({ kind: "idle" });

  const payload: TestConnectionRequest = { server, database, authType, username, password };

  const onTest = async () => {
    setState({ kind: "testing" });
    try {
      const r = await testConnection(payload);
      setState(
        r.success
          ? { kind: "ok", elapsedMs: r.elapsedMs }
          : { kind: "error", message: r.error ?? "Falha desconhecida" },
      );
    } catch (e) {
      setState({
        kind: "error",
        message: e instanceof Error ? e.message : "Falha ao contactar a API",
      });
    }
  };

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-4">
        <div className="col-span-2 space-y-2">
          <Label htmlFor="server">Servidor</Label>
          <Input id="server" value={server} onChange={(e) => setServer(e.target.value)} placeholder="host,porta" />
        </div>
        <div className="space-y-2">
          <Label htmlFor="database">Database</Label>
          <Input id="database" value={database} onChange={(e) => setDatabase(e.target.value)} />
        </div>
        <div className="space-y-2">
          <Label htmlFor="authType">Autenticação</Label>
          <select
            id="authType"
            value={authType}
            onChange={(e) => setAuthType(e.target.value as "sql" | "windows")}
            className="flex h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
          >
            <option value="sql">SQL Server</option>
            <option value="windows">Windows integrada</option>
          </select>
        </div>
        {authType === "sql" && (
          <>
            <div className="space-y-2">
              <Label htmlFor="username">Usuário</Label>
              <Input id="username" value={username} onChange={(e) => setUsername(e.target.value)} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="password">Senha</Label>
              <Input
                id="password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </div>
          </>
        )}
      </div>

      {state.kind === "ok" && (
        <Alert variant="success">Conexão ok ({state.elapsedMs} ms).</Alert>
      )}
      {state.kind === "error" && <Alert variant="destructive">{state.message}</Alert>}

      <div className="flex justify-between">
        <Button variant="outline" onClick={onTest} disabled={state.kind === "testing"}>
          {state.kind === "testing" ? "Testando..." : "Testar conexão"}
        </Button>
        <Button onClick={() => onContinue(payload)} disabled={state.kind !== "ok"}>
          Continuar
        </Button>
      </div>
    </div>
  );
}
