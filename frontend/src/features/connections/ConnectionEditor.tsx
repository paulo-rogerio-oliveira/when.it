import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { Button } from "@/shared/components/ui/button";
import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Alert } from "@/shared/components/ui/alert";
import { Card, CardContent, CardHeader, CardTitle } from "@/shared/components/ui/card";
import {
  createConnection,
  getConnection,
  testConnectionAdHoc,
  updateConnection,
  type ConnectionAuthType,
  type SaveConnectionInput,
} from "@/shared/api/connections";

type TestState =
  | { kind: "idle" }
  | { kind: "testing" }
  | { kind: "ok"; elapsedMs: number }
  | { kind: "error"; message: string };

export function ConnectionEditor() {
  const { id } = useParams<{ id: string }>();
  const isEdit = Boolean(id);
  const navigate = useNavigate();

  const [name, setName] = useState("");
  const [server, setServer] = useState("");
  const [database, setDatabase] = useState("");
  const [authType, setAuthType] = useState<ConnectionAuthType>("sql");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [hasStoredPassword, setHasStoredPassword] = useState(false);
  const [clearPassword, setClearPassword] = useState(false);
  const [test, setTest] = useState<TestState>({ kind: "idle" });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!id) return;
    getConnection(id)
      .then((c) => {
        setName(c.name);
        setServer(c.server);
        setDatabase(c.database);
        setAuthType(c.authType);
        setUsername(c.username ?? "");
        setHasStoredPassword(c.hasPassword);
      })
      .catch((e) => setError(e instanceof Error ? e.message : "Falha ao carregar."));
  }, [id]);

  const buildPayload = (): SaveConnectionInput => ({
    name: name.trim(),
    server: server.trim(),
    database: database.trim(),
    authType,
    username: authType === "sql" ? username.trim() : undefined,
    password: password ? password : undefined,
  });

  const onTest = async () => {
    setTest({ kind: "testing" });
    try {
      const r = await testConnectionAdHoc(buildPayload());
      setTest(
        r.success
          ? { kind: "ok", elapsedMs: r.elapsedMs }
          : { kind: "error", message: r.error ?? "Falha desconhecida" },
      );
    } catch (e) {
      setTest({
        kind: "error",
        message: e instanceof Error ? e.message : "Falha ao contactar a API",
      });
    }
  };

  const onSave = async () => {
    setSaving(true);
    setError(null);
    try {
      const payload = buildPayload();
      if (isEdit && id) {
        await updateConnection(id, { ...payload, clearPassword });
      } else {
        await createConnection(payload);
      }
      navigate("/connections");
    } catch (e) {
      const msg = (e as { response?: { data?: { error?: string } }; message?: string })
        ?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao salvar.");
      setError(msg);
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="mx-auto max-w-2xl space-y-4">
      <div>
        <h2 className="text-2xl font-semibold">
          {isEdit ? "Editar conexão" : "Nova conexão"}
        </h2>
        <p className="text-sm text-muted-foreground">
          SQL Server alvo a ser observado.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Dados de conexão</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="name">Nome amigável</Label>
            <Input
              id="name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="ex.: ERP Produção"
            />
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div className="col-span-2 space-y-2">
              <Label htmlFor="server">Servidor</Label>
              <Input
                id="server"
                value={server}
                onChange={(e) => setServer(e.target.value)}
                placeholder="host,porta ou host\\INSTANCIA"
              />
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
                onChange={(e) => setAuthType(e.target.value as ConnectionAuthType)}
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
                  <Label htmlFor="password">
                    Senha
                    {isEdit && hasStoredPassword && !clearPassword && (
                      <span className="ml-2 text-xs text-muted-foreground">
                        (deixe em branco para manter a atual)
                      </span>
                    )}
                  </Label>
                  <Input
                    id="password"
                    type="password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                  />
                  {isEdit && hasStoredPassword && (
                    <label className="flex items-center gap-2 text-xs text-muted-foreground">
                      <input
                        type="checkbox"
                        checked={clearPassword}
                        onChange={(e) => setClearPassword(e.target.checked)}
                      />
                      Remover senha armazenada
                    </label>
                  )}
                </div>
              </>
            )}
          </div>

          {test.kind === "ok" && <Alert variant="success">Conexão ok ({test.elapsedMs} ms).</Alert>}
          {test.kind === "error" && <Alert variant="destructive">{test.message}</Alert>}
          {error && <Alert variant="destructive">{error}</Alert>}

          <div className="flex flex-wrap items-center justify-between gap-2 pt-2">
            <Button variant="outline" onClick={onTest} disabled={test.kind === "testing"}>
              {test.kind === "testing" ? "Testando…" : "Testar conexão"}
            </Button>
            <div className="flex gap-2">
              <Button variant="ghost" onClick={() => navigate("/connections")}>
                Cancelar
              </Button>
              <Button onClick={onSave} disabled={saving}>
                {saving ? "Salvando…" : isEdit ? "Salvar" : "Criar"}
              </Button>
            </div>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
