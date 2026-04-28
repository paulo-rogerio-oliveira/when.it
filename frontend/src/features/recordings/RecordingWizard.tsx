import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button } from "@/shared/components/ui/button";
import { Input } from "@/shared/components/ui/input";
import { Label } from "@/shared/components/ui/label";
import { Alert } from "@/shared/components/ui/alert";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/shared/components/ui/card";
import { listConnections, type ConnectionListItem } from "@/shared/api/connections";
import { startRecording } from "@/shared/api/recordings";

export function RecordingWizard() {
  const navigate = useNavigate();
  const [connections, setConnections] = useState<ConnectionListItem[] | null>(null);
  const [connectionId, setConnectionId] = useState<string>("");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [filterHostName, setFilterHostName] = useState("");
  const [filterAppName, setFilterAppName] = useState("");
  const [filterLoginName, setFilterLoginName] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listConnections()
      .then((items) => {
        setConnections(items);
        if (items.length > 0) setConnectionId(items[0].id);
      })
      .catch((e) => setError(e instanceof Error ? e.message : "Falha ao carregar conexões."));
  }, []);

  const onStart = async () => {
    if (!connectionId || !name.trim()) return;
    setSubmitting(true);
    setError(null);
    try {
      const rec = await startRecording({
        connectionId,
        name: name.trim(),
        description: description.trim() || undefined,
        filterHostName: filterHostName.trim() || undefined,
        filterAppName: filterAppName.trim() || undefined,
        filterLoginName: filterLoginName.trim() || undefined,
      });
      navigate(`/recordings/${rec.id}/session`);
    } catch (e) {
      const msg = (e as { response?: { data?: { error?: string } }; message?: string })
        ?.response?.data?.error
        ?? (e instanceof Error ? e.message : "Falha ao iniciar gravação.");
      setError(msg);
    } finally {
      setSubmitting(false);
    }
  };

  if (connections === null) {
    return <p className="text-sm text-muted-foreground">Carregando conexões…</p>;
  }

  if (connections.length === 0) {
    return (
      <Card className="mx-auto max-w-xl">
        <CardHeader>
          <CardTitle>Cadastre uma conexão primeiro</CardTitle>
          <CardDescription>
            A gravação observa um SQL Server alvo. Cadastre a conexão antes de iniciar.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <Button onClick={() => navigate("/connections/new")}>Ir para conexões</Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="mx-auto max-w-2xl space-y-4">
      <div>
        <h2 className="text-2xl font-semibold">Nova gravação</h2>
        <p className="text-sm text-muted-foreground">
          Defina o que você vai capturar. Em seguida você executa a operação no sistema legado.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Contexto</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="connection">Conexão</Label>
            <select
              id="connection"
              value={connectionId}
              onChange={(e) => setConnectionId(e.target.value)}
              className="flex h-10 w-full rounded-md border border-input bg-background px-3 text-sm"
            >
              {connections.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name} — {c.server} / {c.database}
                </option>
              ))}
            </select>
          </div>

          <div className="space-y-2">
            <Label htmlFor="name">Nome da gravação</Label>
            <Input
              id="name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="ex.: aprovar sinistro"
            />
          </div>

          <div className="space-y-2">
            <Label htmlFor="description">Descrição (opcional)</Label>
            <Input
              id="description"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="O que esta operação representa no negócio"
            />
          </div>

          <div className="rounded-md border bg-muted/30 p-3">
            <button
              type="button"
              onClick={() => setShowAdvanced((v) => !v)}
              className="flex w-full items-center justify-between text-left text-sm font-medium"
            >
              <span>Filtro de sessão (avançado)</span>
              <span className="text-muted-foreground">{showAdvanced ? "−" : "+"}</span>
            </button>
            {showAdvanced && (
              <div className="mt-3 space-y-3">
                <p className="text-xs text-muted-foreground">
                  Em ambientes com vários usuários, informe identificadores da sua sessão para que o
                  coletor filtre os eventos. Se deixar em branco, o sistema captura tudo e você
                  escolhe a sessão depois.
                </p>
                <div className="grid grid-cols-3 gap-3">
                  <div className="space-y-2">
                    <Label htmlFor="hostName">host_name</Label>
                    <Input id="hostName" value={filterHostName} onChange={(e) => setFilterHostName(e.target.value)} />
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="appName">app_name</Label>
                    <Input id="appName" value={filterAppName} onChange={(e) => setFilterAppName(e.target.value)} />
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="loginName">login_name</Label>
                    <Input id="loginName" value={filterLoginName} onChange={(e) => setFilterLoginName(e.target.value)} />
                  </div>
                </div>
              </div>
            )}
          </div>

          {error && <Alert variant="destructive">{error}</Alert>}

          <div className="flex justify-between pt-2">
            <Button variant="ghost" onClick={() => navigate("/recordings")}>
              Cancelar
            </Button>
            <Button onClick={onStart} disabled={submitting || !name.trim() || !connectionId}>
              {submitting ? "Iniciando…" : "Iniciar gravação"}
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
